using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common.Extensions;

using CharacterSheetLink = ACE.Database.Models.Shard.CharacterSheetLink;

namespace ACE.Server.Managers.CharacterSheets
{
    /// <summary>
    /// Answers a public character sheet request by slug, and manages each character's opt-in link
    /// (Docs/CharacterSheet/DESIGN.md sections 4.1 and 5).
    ///
    /// SERVED THROUGH the market-api-v1 host (MarketApiHost, wired via Program.BuildMarketApiOptions'
    /// CharacterSheets), which only starts when Market.Enabled is set in Config.js - so the sheet
    /// routes are inert on a server with the market module turned off, independent of charsheet_enabled.
    ///
    /// Every dependency on world state, the shard database and PropertyManager sits behind the constructor
    /// seams (ICharacterSheetWorld, ICharacterSheetLinkRepository, the Func settings), so the rules here are
    /// unit tested with fakes; <see cref="Instance"/> is the only place the live pieces are wired.
    ///
    /// THREADING. Every public method runs on the CALLER's thread (the market API request thread) and may
    /// block. Never call one from a world or landblock thread: an online build waits for the player's own
    /// action queue, and the rank snapshot waits for the WorldManager queue, so a world-thread caller would
    /// wait on itself until the budget ran out. The service never touches a Player; the projection runs where
    /// ICharacterSheetWorld puts it (see LiveCharacterSheetWorld).
    ///
    /// ONE REQUEST BUDGET. GetSheet takes a single deadline at entry, buildTimeoutMs (2500) ahead on a
    /// MONOTONIC clock (the monotonicMs seam, Environment.TickCount64 by default; never utcNow, which a test
    /// freezes and a wall-clock step can move). The online/offline build, the rank owner's build and a rank
    /// waiter's wait are each given only what is left of that budget. The link read and the presence read are
    /// synchronous database calls OUTSIDE its control: they spend the budget, but nothing bounds them, so a
    /// stalled MySQL read can overrun 2500ms on its own. The API route's request timeout, not this budget, is
    /// the outer bound on a request. With no budget left for ranks the sheet carries the last snapshot's
    /// ranks, else none; the sheet itself never fails for want of ranks.
    ///
    /// OUTCOMES. Disabled: charsheet_enabled is off. NotFound: malformed slug, no link row, missing or
    /// deleted character (one indistinguishable answer, so a slug cannot be probed for WHY it is absent).
    /// Busy: the build returned null (online player not ticking, offline load refused or timed out) or the
    /// offline build cap is full. Failed: a repository read or write failed, or the world threw.
    ///
    /// CACHES. Response cache: per slug, ordinal (slugs are case-sensitive), for cacheSeconds; only Ok
    /// sheets are cached, with ranks attached, and NOT a sheet whose ranks fell back to empty for want of any
    /// snapshot (a stale snapshot's ranks are cached normally). Every SweepEveryWrites (64) writes, expired
    /// entries are swept, so slugs nobody requests again do not stay in memory. Rotate and Disable evict the
    /// old slug and bump an eviction epoch; a build that was already in flight when an eviction happened
    /// does not leave its sheet in the cache, so a rotated-away slug stops resolving at once rather than up
    /// to cacheSeconds later. The epoch is GLOBAL, not per slug, on purpose: an eviction anywhere also stops
    /// every other build in flight at that moment from caching its (still correct) sheet, which costs one
    /// extra rebuild for those slugs. Rotate and Disable are rare owner actions, so that cost was accepted
    /// over per-slug bookkeeping. Rank snapshot: one per service, reused while younger than 60s; concurrent
    /// requests needing a new one share a single in-flight build, and a failed build falls back to the
    /// previous snapshot, else no ranks.
    ///
    /// LINKS. A slug collision on Upsert is re-checked against the character's row before retrying: the DAO
    /// reports the character_Id primary key's 1062 as a collision too, so a racing first enable (or rotate)
    /// that already wrote the row is answered with that row instead of being rotated away.
    ///
    /// OFFLINE CAP, in two layers. Here, a SemaphoreSlim(maxConcurrentOfflineBuilds) taken with Wait(0)
    /// bounds how many REQUESTS wait on offline builds at once. Its slot is released when BuildOffline
    /// returns, which on a timeout is before the underlying load has finished; holding it until completion
    /// would leak it forever on ProfileBuilder's no-callback failures. The LOADS are bounded in
    /// LiveCharacterSheetWorld: per-character single-flight (a repeat request joins the in-flight load
    /// rather than queueing another), a hard ceiling of 4 outstanding loads with timed-out ones still counted
    /// (beyond it BuildOffline answers null, so Busy, and queues nothing), and a 30s expiry that frees an
    /// entry whose load never called back. However many requests arrive, a backed-up shard worker holds at
    /// most 4 sheet loads.
    /// </summary>
    public sealed class CharacterSheetService : ICharacterSheetService
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Upsert attempts after the first when the slug collides; 1 + this many attempts in all.</summary>
        public const int MaxSlugRetries = 5;

        public static readonly TimeSpan RankSnapshotMaxAge = TimeSpan.FromSeconds(60);

        private static readonly Lazy<CharacterSheetService> instance =
            new Lazy<CharacterSheetService>(CreateLive, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Live wiring, created on first use.</summary>
        public static CharacterSheetService Instance => instance.Value;

        private readonly ICharacterSheetWorld world;
        private readonly ICharacterSheetLinkRepository links;
        private readonly Func<bool> enabled;
        private readonly Func<bool> classAbilitiesEnabled;
        private readonly Func<string> siteHost;
        private readonly Func<int> cacheSeconds;
        private readonly Func<DateTime> utcNow;
        private readonly SemaphoreSlim offlineSlots;
        private readonly TimeSpan buildTimeout;
        private readonly Func<long> monotonicMs;

        /// <summary>Test seam: entries currently held by the response cache.</summary>
        internal int CachedCount => cache.Count;

        /// <summary>Test seam: sweep expired response-cache entries after every this-many cache writes.</summary>
        internal int SweepEveryWrites { get; set; } = 64;

        private readonly ConcurrentDictionary<string, (DateTime at, CharacterSheet sheet)> cache =
            new ConcurrentDictionary<string, (DateTime at, CharacterSheet sheet)>(StringComparer.Ordinal);

        // bumped by every eviction; a GetSheet that saw it change while building does not keep its cache entry
        private long cacheEpoch;

        private readonly object rankLock = new object();
        private IReadOnlyDictionary<uint, List<SheetRank>> rankSnapshot;     // guarded by rankLock
        private DateTime rankSnapshotAt;                                     // guarded by rankLock
        private Task<IReadOnlyDictionary<uint, List<SheetRank>>> rankBuild;  // guarded by rankLock; null = none in flight

        public CharacterSheetService(ICharacterSheetWorld world, ICharacterSheetLinkRepository links,
            Func<bool> enabled, Func<bool> classAbilitiesEnabled, Func<string> siteHost, Func<int> cacheSeconds,
            Func<DateTime> utcNow, int maxConcurrentOfflineBuilds = 2, int buildTimeoutMs = 2500, Func<long> monotonicMs = null)
        {
            this.monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.links = links ?? throw new ArgumentNullException(nameof(links));
            this.enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
            this.classAbilitiesEnabled = classAbilitiesEnabled ?? throw new ArgumentNullException(nameof(classAbilitiesEnabled));
            this.siteHost = siteHost ?? throw new ArgumentNullException(nameof(siteHost));
            this.cacheSeconds = cacheSeconds ?? throw new ArgumentNullException(nameof(cacheSeconds));
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

            if (maxConcurrentOfflineBuilds < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentOfflineBuilds), "must be at least 1");
            if (buildTimeoutMs < 1)
                throw new ArgumentOutOfRangeException(nameof(buildTimeoutMs), "must be at least 1");

            offlineSlots = new SemaphoreSlim(maxConcurrentOfflineBuilds, maxConcurrentOfflineBuilds);
            buildTimeout = TimeSpan.FromMilliseconds(buildTimeoutMs);
        }

        private static CharacterSheetService CreateLive()
        {
            return new CharacterSheetService(
                new LiveCharacterSheetWorld(),
                new ShardCharacterSheetLinkRepository(),
                () => PropertyManager.GetBool("charsheet_enabled").Item,
                () => PropertyManager.GetBool("class_abilities_enabled").Item,
                () => PropertyManager.GetString("charsheet_site_url").Item,
                () => (int)Math.Clamp(PropertyManager.GetLong("charsheet_cache_seconds").Item, 0L, int.MaxValue),
                () => DateTime.UtcNow);
        }

        public SheetResult GetSheet(string slug)
        {
            if (!enabled())
                return new SheetResult(SheetOutcome.Disabled, null);

            // no database read for a slug that cannot exist
            if (!SheetSlug.IsWellFormed(slug))
                return new SheetResult(SheetOutcome.NotFound, null);

            // ONE budget for the whole request: the link read, the build and the rank wait all spend it
            var deadlineMs = monotonicMs() + (long)buildTimeout.TotalMilliseconds;

            try
            {
                return GetSheetCore(slug, deadlineMs);
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] GetSheet failed for slug {slug}: {ex.GetFullMessage()}");
                return new SheetResult(SheetOutcome.Failed, null);
            }
        }

        /// <summary>What is left of the request budget; never negative.</summary>
        private TimeSpan Remaining(long deadlineMs) => TimeSpan.FromMilliseconds(Math.Max(0L, deadlineMs - monotonicMs()));

        private SheetResult GetSheetCore(string slug, long deadlineMs)
        {
            if (TryGetCached(slug, out var cached))
                return new SheetResult(SheetOutcome.Ok, cached);

            // read BEFORE the link row, so an eviction that commits after this point is always seen below
            var epoch = Interlocked.Read(ref cacheEpoch);

            if (!links.TryGetBySlug(slug, out var row))
                return new SheetResult(SheetOutcome.Failed, null);

            if (row == null)
                return new SheetResult(SheetOutcome.NotFound, null);

            var characterGuid = row.CharacterId;
            var includeClassAbilities = classAbilitiesEnabled();

            CharacterSheet sheet;

            switch (world.Presence(characterGuid))
            {
                case CharacterPresence.Online:
                    sheet = world.BuildOnline(characterGuid, includeClassAbilities, Remaining(deadlineMs));
                    break;

                case CharacterPresence.Offline:
                    if (!offlineSlots.Wait(0))
                        return new SheetResult(SheetOutcome.Busy, null);

                    try
                    {
                        sheet = world.BuildOffline(characterGuid, includeClassAbilities, Remaining(deadlineMs));
                    }
                    finally
                    {
                        offlineSlots.Release();
                    }
                    break;

                default:
                    return new SheetResult(SheetOutcome.NotFound, null);
            }

            if (sheet == null)
                return new SheetResult(SheetOutcome.Busy, null);

            var snapshot = GetRankSnapshot(deadlineMs);
            sheet.Ranks = snapshot != null && snapshot.TryGetValue(characterGuid, out var ranks) && ranks != null
                ? new List<SheetRank>(ranks)
                : new List<SheetRank>();

            // No snapshot at all means the ranks fell back to EMPTY: serve the sheet but do not cache it, so the
            // next request tries for ranks again instead of reading a rankless sheet for cacheSeconds. A sheet
            // carrying a stale snapshot's ranks is cached normally.
            if (snapshot == null)
                return new SheetResult(SheetOutcome.Ok, sheet);

            var ttlSeconds = cacheSeconds();

            if (ttlSeconds <= 0)
                return new SheetResult(SheetOutcome.Ok, sheet);

            // Write, THEN check the epoch. Evict bumps the epoch before removing, so either this sees the
            // bump and removes its own entry, or the bump came later and Evict's removal lands after the write.
            var entry = (at: utcNow(), sheet);
            cache[slug] = entry;

            if (Interlocked.Read(ref cacheEpoch) != epoch)
                cache.TryRemove(new KeyValuePair<string, (DateTime at, CharacterSheet sheet)>(slug, entry));

            if (Interlocked.Increment(ref writesSinceSweep) >= Math.Max(1, SweepEveryWrites))
            {
                Interlocked.Exchange(ref writesSinceSweep, 0);
                SweepExpired(ttlSeconds);
            }

            return new SheetResult(SheetOutcome.Ok, sheet);
        }

        // cache writes since the last sweep
        private int writesSinceSweep;

        /// <summary>
        /// Drops every response-cache entry at least <paramref name="ttlSeconds"/> old, so slugs nobody asks for
        /// again do not sit in memory forever. Conditional removal: an entry rewritten meanwhile survives.
        /// </summary>
        private void SweepExpired(int ttlSeconds)
        {
            var cutoff = utcNow() - TimeSpan.FromSeconds(ttlSeconds);

            foreach (var kv in cache)
            {
                if (kv.Value.at <= cutoff)
                    cache.TryRemove(kv);
            }
        }

        private bool TryGetCached(string slug, out CharacterSheet sheet)
        {
            sheet = null;

            if (!cache.TryGetValue(slug, out var entry))
                return false;

            var ttlSeconds = cacheSeconds();

            if (ttlSeconds > 0 && utcNow() - entry.at < TimeSpan.FromSeconds(ttlSeconds))
            {
                sheet = entry.sheet;
                return true;
            }

            // stale: drop it (only if nobody replaced it meanwhile) so dead slugs do not accumulate
            cache.TryRemove(new KeyValuePair<string, (DateTime at, CharacterSheet sheet)>(slug, entry));
            return false;
        }

        /// <summary>
        /// The current rank snapshot, rebuilding it when older than <see cref="RankSnapshotMaxAge"/>. One build
        /// at a time: a caller arriving while one is in flight waits on that build instead of queueing another.
        /// Every wait, owner's or waiter's, spends only what is left of the request budget, and with no budget
        /// left no build is attempted. A null, thrown or unfinished build answers with the previous snapshot
        /// (possibly stale), else null.
        /// </summary>
        private IReadOnlyDictionary<uint, List<SheetRank>> GetRankSnapshot(long deadlineMs)
        {
            TaskCompletionSource<IReadOnlyDictionary<uint, List<SheetRank>>> owned = null;
            Task<IReadOnlyDictionary<uint, List<SheetRank>>> pending;

            lock (rankLock)
            {
                if (rankSnapshot != null && utcNow() - rankSnapshotAt < RankSnapshotMaxAge)
                    return rankSnapshot;

                // budget already spent: whatever snapshot there is, without building
                if (Remaining(deadlineMs) <= TimeSpan.Zero)
                    return rankSnapshot;

                if (rankBuild == null)
                {
                    owned = new TaskCompletionSource<IReadOnlyDictionary<uint, List<SheetRank>>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rankBuild = owned.Task;
                }

                pending = rankBuild;
            }

            IReadOnlyDictionary<uint, List<SheetRank>> built = null;

            if (owned != null)
            {
                try
                {
                    built = world.BuildRankSnapshot(Remaining(deadlineMs));
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] rank snapshot build failed: {ex.GetFullMessage()}");
                    built = null;
                }
                finally
                {
                    lock (rankLock)
                    {
                        if (built != null)
                        {
                            rankSnapshot = built;
                            rankSnapshotAt = utcNow();
                        }

                        rankBuild = null;
                    }

                    owned.TrySetResult(built);
                }
            }
            else
            {
                var left = Remaining(deadlineMs);

                if (left > TimeSpan.Zero && pending.Wait(left))
                    built = pending.Result;
            }

            if (built != null)
                return built;

            lock (rankLock)
                return rankSnapshot;
        }

        /// <summary>
        /// The owner's view of one character (suit builder): the same online/offline build path and the same
        /// request budget and offline-build cap as <see cref="GetSheet"/>, but with no slug, no link row, no
        /// response cache, no rank snapshot and no charsheet_enabled check. The caller has already proved
        /// ownership. Busy: the build returned null (online player not ticking, offline load refused, timed
        /// out or capped). NotFound: the character is missing or deleted.
        ///
        /// SHARED CAPACITY. Owner-profile builds draw on the SAME offline-build limits as public sheets: this
        /// service's maxConcurrentOfflineBuilds request slots, and LiveCharacterSheetWorld's ceiling of 4
        /// outstanding offline loads (owner-view and public loads are separate in-flight entries, but both
        /// count against that one ceiling). A burst of public sheet requests for offline characters can
        /// therefore answer an owner Busy, and the reverse. There is deliberately no separate budget.
        /// </summary>
        public OwnerProfileResult GetOwnerProfile(uint characterGuid)
        {
            var deadlineMs = monotonicMs() + (long)buildTimeout.TotalMilliseconds;

            try
            {
                var online = false;
                CharacterSheet sheet;

                switch (world.Presence(characterGuid))
                {
                    case CharacterPresence.Online:
                        online = true;
                        sheet = world.BuildOnline(characterGuid, false, Remaining(deadlineMs), ownerView: true);
                        break;

                    case CharacterPresence.Offline:
                        if (!offlineSlots.Wait(0))
                            return new OwnerProfileResult(SheetOutcome.Busy, null);

                        try
                        {
                            sheet = world.BuildOffline(characterGuid, false, Remaining(deadlineMs), ownerView: true);
                        }
                        finally
                        {
                            offlineSlots.Release();
                        }
                        break;

                    default:
                        return new OwnerProfileResult(SheetOutcome.NotFound, null);
                }

                if (sheet?.Owner == null)
                    return new OwnerProfileResult(SheetOutcome.Busy, null);

                sheet.Owner.CharacterGuid = characterGuid;
                sheet.Owner.Online = online;

                return new OwnerProfileResult(SheetOutcome.Ok, sheet.Owner);
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] GetOwnerProfile failed for 0x{characterGuid:X8}: {ex.GetFullMessage()}");
                return new OwnerProfileResult(SheetOutcome.Failed, null);
            }
        }

        public LinkResult GetLink(uint characterGuid)
        {
            if (!enabled())
                return new LinkResult(SheetOutcome.Disabled, null);

            if (!links.TryGetByCharacter(characterGuid, out var row))
                return new LinkResult(SheetOutcome.Failed, null);

            return new LinkResult(SheetOutcome.Ok, ToLink(row));
        }

        public LinkResult EnableOrRotate(uint characterGuid, bool rotate)
        {
            if (!enabled())
                return new LinkResult(SheetOutcome.Disabled, null);

            if (!links.TryGetByCharacter(characterGuid, out var existing))
                return new LinkResult(SheetOutcome.Failed, null);

            if (existing != null && !rotate)
                return new LinkResult(SheetOutcome.Ok, ToLink(existing));

            for (var attempt = 0; attempt <= MaxSlugRetries; attempt++)
            {
                string slug;
                do
                {
                    slug = SheetSlug.New();
                }
                while (existing != null && string.Equals(slug, existing.Slug, StringComparison.Ordinal));

                var row = new CharacterSheetLink { CharacterId = characterGuid, Slug = slug };

                if (links.Upsert(row, out var slugCollision))
                {
                    if (existing != null)
                        Evict(existing.Slug);

                    return new LinkResult(SheetOutcome.Ok, ToLink(row));
                }

                if (!slugCollision)
                    return new LinkResult(SheetOutcome.Failed, null);

                // The DAO reports ANY 1062 as a collision, including the character_Id PRIMARY key: a racing first
                // enable (or rotate) may have written this character's row. If the row now differs from the one
                // read above, that writer won - answer with its row instead of rotating it away with another upsert.
                if (!links.TryGetByCharacter(characterGuid, out var current))
                    return new LinkResult(SheetOutcome.Failed, null);

                if (current != null && (existing == null || !string.Equals(current.Slug, existing.Slug, StringComparison.Ordinal)))
                {
                    if (existing != null)
                        Evict(existing.Slug);

                    return new LinkResult(SheetOutcome.Ok, ToLink(current));
                }
            }

            log.Error($"[CHARSHEET] EnableOrRotate gave up for character 0x{characterGuid:X8}: {1 + MaxSlugRetries} slug collisions in a row.");
            return new LinkResult(SheetOutcome.Failed, null);
        }

        public LinkResult Disable(uint characterGuid)
        {
            if (!enabled())
                return new LinkResult(SheetOutcome.Disabled, null);

            // the old slug is needed to evict its cached sheet
            if (!links.TryGetByCharacter(characterGuid, out var existing))
                return new LinkResult(SheetOutcome.Failed, null);

            if (!links.Delete(characterGuid))
                return new LinkResult(SheetOutcome.Failed, null);

            if (existing != null)
                Evict(existing.Slug);

            return new LinkResult(SheetOutcome.Ok, ToLink(null));
        }

        private void Evict(string slug)
        {
            // bump first, remove second - GetSheetCore's write-then-check relies on this order
            Interlocked.Increment(ref cacheEpoch);
            cache.TryRemove(slug, out _);
        }

        private SheetLink ToLink(CharacterSheetLink row)
        {
            if (row == null)
                return new SheetLink { Enabled = false, Slug = null, Url = null };

            return new SheetLink
            {
                Enabled = true,
                Slug = row.Slug,
                Url = "https://" + (siteHost() ?? string.Empty).Trim().TrimEnd('/') + "/" + row.Slug,
            };
        }
    }
}
