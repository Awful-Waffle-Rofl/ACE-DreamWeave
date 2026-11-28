using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Spawns and destroys the Weave Caches that carry a run's reward (TECH-DESIGN 2.7).
    ///
    /// WorldEvent.Finish calls SpawnCaches once, at the moment it enters Rewarding; WorldEvent.Tick calls
    /// DestroyCaches once, when the claim window ends (and Finish calls it again when an abort cuts the
    /// window short). Both calls are already wrapped in try/catch by the caller, so an exception here can
    /// never strand a run - but nothing in here is expected to throw either.
    ///
    /// THREADING. Both entry points are called from the world thread. Every AddWorldObject / Destroy /
    /// position write goes through the owning landblock's action queue, because a cross-thread
    /// AddWorldObjectInternal is logged as an error by the landblock itself.
    /// </summary>
    public static class WorldEventRewardDelivery
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Radius in metres of the ring the caches are placed on around the anchor.</summary>
        private const float CacheRingRadius = 3.0f;

        /// <summary>
        /// Fallback when a reward axis declares a non-positive participantsPerCache, which would otherwise
        /// divide by zero (rewards.json ships 8).
        /// </summary>
        private const int DefaultParticipantsPerCache = 8;

        /// <summary>
        /// Defensive ceiling on how many caches one run may place. The formula is audience-driven, so a
        /// pathological reward axis (participantsPerCache 1) plus a large audience would otherwise queue
        /// hundreds of AddWorldObject calls onto a single landblock in one action. Claim state is shared
        /// across every cache, so extra caches only spread the crowd out - clamping costs nobody a reward.
        /// </summary>
        public const int MaxCachesPerRun = 24;

        /// <summary>
        /// The shipped default of world_events_cache_effect_script: 0x8D WeddingBliss, the same multicolored
        /// sparkle burst a player gets on maxing a skill (Player_Skills.cs:52). This is the sentinel a bad
        /// or undefined configured value falls back to - see <see cref="ResolveCacheEffectScript"/>.
        /// </summary>
        public const long DefaultCacheEffectScript = (long)PlayScript.WeddingBliss;

        /// <summary>
        /// The player/human PhysicsEffectTable (PropertyDataId 22 on retail weenies 1 and 4), whose entries are
        /// what a PlayScript TYPE means when a player plays it - e.g. 0x8D WeddingBliss -> 0x330006DE, the
        /// skill-max sparkle. The cache effect is resolved through THIS table, not the cache's own, because
        /// every Weave Cache carries the chest table 0x3400002B, which has no WeddingBliss entry at all (it
        /// holds only the Enchant colors, Create, Destroy and DisappearDestroy). A 0xF755 PlayEffect names a
        /// type the client looks up in the TARGET's table, so on a cache that lookup missed and nothing drew.
        /// </summary>
        public const uint PlayerPhysicsScriptTableId = 0x34000004;

        /// <summary>The shipped default of world_events_cache_effect_repeat_seconds.</summary>
        public const long DefaultCacheEffectRepeatSeconds = 30;

        /// <summary>How long after EnterWorld the cache effect first plays, so freshly-joined clients have
        /// already received the create-object message for the cache.</summary>
        private const double CacheEffectInitialDelaySeconds = 0.5;

        /// <summary>Set once world_events_cache_effect_script has been reported bad, so a wrong value warns once rather than per cache.</summary>
        private static bool badCacheEffectScriptLogged;

        public static void SpawnCaches(WorldEvent evt)
        {
            if (evt == null)
                return;

            // Finish already skips Rewarding for every Aborted* outcome; this guard is kept anyway so the
            // "aborted runs pay nothing" rule survives a future caller.
            if (IsAborted(evt.Outcome))
            {
                log.Info($"[WORLDEVENT] run={evt.RunId} rewarding skipped: outcome {evt.Outcome} pays nothing");
                return;
            }

            var composition = evt.Composition;
            var reward = composition?.Reward;
            var anchor = composition?.AnchorPosition;

            if (anchor == null)
            {
                log.Info($"[WORLDEVENT] run={evt.RunId} rewarding skipped: no anchor position");
                return;
            }

            if (reward == null || reward.CacheWcid == 0)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} rewarding skipped: reward axis {reward?.Id ?? "(none)"} declares no cacheWcid");
                return;
            }

            // The objective's participant radius and staff rule (World Events Hold H6), and its claim policy's cache rule; a
            // run whose objective sets none is the source's rewardRadius, the sampler's staff rule and the axis's value exactly.
            var rewardRadius = evt.EffectiveParticipantRadius;
            var audience = WorldEventAudienceSampler.Sample(anchor, rewardRadius, evt.ObjectiveCountsStaff());
            var perCache = ParticipantsPerCacheFor(evt, reward);

            var count = ComputeCacheCount(audience.Count, perCache, reward.CacheCount, out var unclamped);

            if (unclamped > count)
                log.Warn($"[WORLDEVENT] run={evt.RunId} caches clamped from {unclamped} to {count}");

            // WP-18 item 3: with a single cache there is no ring to build - a lone chest offset 3 m from the
            // centre reads as misplaced, and the centre is where the fight (and the decor above it) already
            // points. Multi-cache runs keep the ring exactly as before, which is what spreads a crowd out.
            var positions = count == 1
                ? new List<Position> { new Position(anchor) }
                : BuildRing(anchor, count);

            var landblock = LandblockManager.GetLandblock(new LandblockId(anchor.LandblockId.Raw), anchor.Instance, false);

            if (landblock == null)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} rewarding skipped: landblock 0x{anchor.LandblockId.Raw:X8} (instance {anchor.Instance}) is not loaded");
                return;
            }

            var runId = evt.RunId;

            // WP-19 item 2: the live override, so the repo owner can try a different chest weenie with
            // "/modifylong world_events_cache_wcid <wcid>" without editing rewards.json or rebuilding. Any
            // wcid is acceptable here - this code knows nothing about which chests exist.
            var overrideWcid = ReadCacheWcidOverride();
            var overrideResolves = overrideWcid != 0 && WeenieExists(overrideWcid);

            if (overrideWcid != 0 && !overrideResolves)
                // Once per run, not once per cache: a bad override is one mistake, not N.
                log.Warn($"[WORLDEVENT] run={runId} world_events_cache_wcid {overrideWcid} does not resolve to a weenie; falling back to the reward profile's cacheWcid {reward.CacheWcid}");

            // The pool is a SUCCESS reward: cacheWcid is the failure/consolation look, so a run that did not
            // succeed must never draw from the fancy pool, whatever rewards.json lists. The override still
            // wins on either outcome - it is a "force this look" test knob, not part of the reward design.
            // Skip the WeenieExists filtering (and its WARN) entirely when the pool is not going to be used
            // at all, so a failed run does not spend a DB lookup - or log noise - on wcids it will never
            // consider.
            var usePool = evt.Outcome == WorldEventOutcome.Success;

            var resolvedPool = new List<uint>();

            if (usePool)
            {
                // A pool entry that no longer exists (a removed weenie, a typo) is one bad wcid, not a
                // reason to fail the whole run - it is simply excluded, once, with a single WARN for the run.
                var skippedPool = new List<uint>();

                foreach (var wcid in reward.CacheWcids ?? new List<uint>())
                {
                    if (WeenieExists(wcid))
                        resolvedPool.Add(wcid);
                    else
                        skippedPool.Add(wcid);
                }

                if (skippedPool.Count > 0)
                    log.Warn($"[WORLDEVENT] run={runId} cacheWcids pool entries do not resolve to a weenie, skipped: {string.Join(",", skippedPool)}");
            }

            var cacheWcid = ResolveCacheWcid(reward.CacheWcid, resolvedPool, overrideWcid, overrideResolves,
                n => ThreadSafeRandom.Next(0, n - 1));

            string wcidSource;

            if (overrideWcid != 0 && overrideResolves)
                wcidSource = "override";
            else if (resolvedPool.Count > 0)
                wcidSource = "pool";
            else if (!usePool && reward.CacheWcids != null && reward.CacheWcids.Count > 0)
                // Distinguishes "no pool configured" from "pool configured, but this run did not succeed so
                // it was bypassed" - the reader otherwise cannot tell those apart from "profile" alone.
                wcidSource = "profile-failed";
            else
                wcidSource = "profile";

            log.Info($"[WORLDEVENT] run={runId} rewarding audience={audience.Count} perCache={Math.Max(1, perCache)} cacheCount={reward.CacheCount} caches={count} wcid={cacheWcid} source={wcidSource}");

            var cacheEffectScript = CacheEffectScript();
            var cacheEffectRepeatSeconds = ReadCacheEffectRepeatSeconds();

            if (cacheEffectScript == PlayScript.Invalid)
                log.Info($"[WORLDEVENT] run={runId} cache effect disabled");
            else
                log.Info($"[WORLDEVENT] run={runId} cache effect script={(uint)cacheEffectScript} repeat={cacheEffectRepeatSeconds}s caches={count}");

            // The PlayScript type above is resolved here, once per run, to the raw 0x33 script DataID a
            // player would play for it, and that DataID is what goes on the wire - see
            // PlayerPhysicsScriptTableId for why the type alone never drew on a cache.
            var cacheEffectScriptDid = ResolveCacheEffectScriptDid(runId, cacheEffectScript);

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                // The run token: by the time this action runs the event may already have been aborted, in
                // which case there is nothing left to reward and the caches would be orphaned.
                if (!evt.RunValid(runId))
                {
                    log.Info($"[WORLDEVENT] run={runId} cache spawn abandoned: run no longer valid");
                    return;
                }

                for (var cacheIndex = 0; cacheIndex < positions.Count; cacheIndex++)
                {
                    if (!SpawnOne(evt, runId, cacheWcid, positions[cacheIndex], anchor, cacheIndex, reward.CacheDecor, cacheEffectScriptDid, cacheEffectRepeatSeconds))
                        break;   // permanent: the weenie does not build, so the remaining caches would fail identically
                }
            }));
        }

        /// <summary>
        /// Destroys every Weave Cache this run placed. Safe to call twice: WorldObject.Destroy is itself
        /// idempotent, and an object whose destroy is already queued is simply queued again.
        /// </summary>
        public static void DestroyCaches(WorldEvent evt)
        {
            if (evt == null)
                return;

            var caches = new List<WorldObject>();

            lock (evt.HeldObjects)
            {
                foreach (var wo in evt.HeldObjects)
                {
                    if (wo == null || wo.IsDestroyed)
                        continue;

                    // WP-19: the dressing goes with the chest it belongs to, at the same moment. It is
                    // matched by guid rather than by a property, for the reason WorldEvent.IsCacheDecor
                    // gives - the only existing marker is the one the claim handler dispatches on.
                    if (wo.GetProperty(PropertyBool.WorldEventCache) != true && !evt.IsCacheDecor(wo.Guid.Full))
                        continue;

                    caches.Add(wo);
                }
            }

            foreach (var cache in caches)
            {
                var landblock = cache.CurrentLandblock;

                if (landblock != null)
                {
                    var target = cache;
                    landblock.EnqueueAction(new ActionEventDelegate(() => target.Destroy()));
                }
                else
                    cache.Destroy();
            }

            log.Info($"[WORLDEVENT] run={evt.RunId} cache destroy scheduled={caches.Count}");
        }

        /// <summary>
        /// The participants-per-cache rule SpawnCaches uses: the run's claim policy's ParticipantsPerCache when positive
        /// (World Events Hold's participants_per_cache), else the reward axis's own value, exactly as before.
        /// </summary>
        internal static int ParticipantsPerCacheFor(WorldEvent evt, Defs.RewardDef reward) =>
            WorldEventCacheHandler.ClaimPolicyFor(evt).EffectiveParticipantsPerCache(reward?.ParticipantsPerCache ?? 0);

        /// <summary>
        /// ceil(audience / participantsPerCache), at least one cache and at most <see cref="MaxCachesPerRun"/>.
        /// Pure - the only reason it is not private is that it is the one piece of SpawnCaches worth testing
        /// without a landblock.
        /// </summary>
        public static int ComputeCacheCount(int audienceCount, int participantsPerCache)
        {
            return ComputeCacheCount(audienceCount, participantsPerCache, out _);
        }

        /// <summary>
        /// As <see cref="ComputeCacheCount(int, int)"/>, but also reports the count BEFORE the
        /// <see cref="MaxCachesPerRun"/> ceiling was applied, so the caller can log the clamp rather than
        /// letting it happen silently. <paramref name="unclamped"/> is always at least 1.
        /// </summary>
        public static int ComputeCacheCount(int audienceCount, int participantsPerCache, out int unclamped)
        {
            return ComputeCacheCount(audienceCount, participantsPerCache, 0, out unclamped);
        }

        /// <summary>
        /// WP-18 item 3. <paramref name="cacheCount"/> greater than 0 is a FIXED count and wins outright -
        /// the audience is not consulted at all, which is what "one large chest, however many players show
        /// up" means. 0 (the default, and any axis file that omits the key) falls through to the
        /// audience-driven formula exactly as before. The MaxCachesPerRun ceiling still applies to both, so
        /// a fixed count cannot queue an unbounded number of AddWorldObject calls either.
        /// </summary>
        public static int ComputeCacheCount(int audienceCount, int participantsPerCache, int cacheCount, out int unclamped)
        {
            if (cacheCount > 0)
            {
                unclamped = cacheCount;

                return cacheCount > MaxCachesPerRun ? MaxCachesPerRun : cacheCount;
            }

            var per = participantsPerCache > 0 ? participantsPerCache : DefaultParticipantsPerCache;

            var count = (int)Math.Ceiling(Math.Max(0, audienceCount) / (double)per);

            if (count < 1)
                count = 1;

            unclamped = count;

            if (count > MaxCachesPerRun)
                count = MaxCachesPerRun;

            return count;
        }

        /// <summary>
        /// A ring of <paramref name="count"/> points of radius <see cref="CacheRingRadius"/> around the
        /// anchor, at angle 2*pi*i/count. Any point that lands in a different landblock than the anchor (a
        /// 3 m ring can cross a block edge) falls back to the anchor itself rather than being placed on a
        /// landblock this code is not holding the action queue of.
        ///
        /// WP-03 owns WorldEventGeometry, which does not exist yet; this is deliberately its own tiny
        /// placement rather than a second implementation of that helper's contract.
        /// </summary>
        private static List<Position> BuildRing(Position anchor, int count)
        {
            var positions = new List<Position>(count);

            for (var i = 0; i < count; i++)
            {
                var angle = 2.0 * Math.PI * i / count;

                var offset = new Vector3(
                    (float)(CacheRingRadius * Math.Cos(angle)),
                    (float)(CacheRingRadius * Math.Sin(angle)),
                    0f);

                var position = new Position(anchor);
                position.SetPosition(anchor.Pos + offset);

                if (position.InstancedLandblock != anchor.InstancedLandblock)
                {
                    positions.Add(new Position(anchor));
                    continue;
                }

                // Outdoors the ring point can sit above or below the terrain it was projected onto; indoors
                // SetLandblock/SetLandCell are no-ops and the terrain has no meaning, so the Z is left alone
                // and a bad point is caught by the EnterWorld failure below.
                if (!position.Indoors)
                {
                    try
                    {
                        position.AdjustMapCoords();
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"[WORLDEVENT] cache ring point {i} could not be snapped to terrain; using the raw Z", ex);
                    }
                }

                positions.Add(position);
            }

            return positions;
        }

        /// <summary>
        /// The 2.4 spawn recipe for one cache. Runs on the anchor landblock's action queue. Returns false only
        /// on a PERMANENT failure (the cache weenie does not build), which the caller treats as "stop placing";
        /// a cache that every candidate refused, or one placed after cleanup, returns true.
        /// </summary>
        private static bool SpawnOne(WorldEvent evt, uint runId, uint cacheWcid, Position position, Position anchor, int cacheIndex,
            IReadOnlyList<DecorDef> cacheDecor, uint cacheEffectScriptDid, long cacheEffectRepeatSeconds)
        {
            // 2026-11-23 prod run 4: a single-cache run asks for the anchor itself, and the old "one retry at
            // the anchor" was then the SAME point a second time, so one crowded centre (14 players plus the
            // wave creatures whose destroys are queued BEHIND this action) refused the cache twice and the run
            // paid nobody. The plan below tries the requested point, the anchor, solid points on growing rings
            // around the anchor, and finally an ethereal cache, which only static geometry can refuse. It is
            // lazy: a cache accepted at its requested point snaps no ring point at all.
            var plan = EnumeratePlacementPlan(position, anchor, cacheIndex, SnapToTerrain);

            WorldObject placed = null;

            var outcome = PlaceFirstAccepted(plan,
                attempt => TryPlace(evt, runId, cacheWcid, attempt.Position, attempt.Ethereal, out placed),
                // A closed held list is not a placement failure: retrying would only build and destroy
                // more objects and log an error for a run that is already cleaning up.
                () => evt.HeldClosed);

            if (outcome.Permanent)
                return false;

            if (outcome.Index < 0 || placed == null)
            {
                if (evt.HeldClosed)
                    return true;

                log.Warn($"[WORLDEVENT] run={runId} cache wcid={cacheWcid} refused at every one of {outcome.Tried} placement candidates around the anchor {anchor.ToLOCString()}");
                log.Error($"[WORLDEVENT] run={runId} cache wcid={cacheWcid} failed to enter the world at {position.ToLOCString()} and at the anchor; skipped");
                return true;
            }

            if (outcome.Index > 0)
            {
                var used = outcome.Used;

                log.Warn($"[WORLDEVENT] run={runId} cache wcid={cacheWcid} placed by fallback candidate={outcome.Index + 1} kind={used.Kind} ethereal={used.Ethereal} requested={position.ToLOCString()} loc={placed.Location?.ToLOCString()}");
            }

            ScheduleCacheEffect(evt, runId, placed, cacheEffectScriptDid, cacheEffectRepeatSeconds);

            // Dressed around where the cache ACTUALLY landed (AddPhysicsObj synced Location from physics, so a
            // slide or a fallback candidate is already reflected), not where it was first asked to go.
            SpawnCacheDecor(evt, runId, cacheDecor, new Position(placed.Location ?? position));

            return true;
        }

        /// <summary>Radii in metres of the solid fallback rings around the anchor, tried in this order.</summary>
        public static readonly IReadOnlyList<float> FallbackRingRadii = new[] { 5f, 8f, 12f };

        /// <summary>Points on each fallback ring. Each ring is turned half a step from the previous one.</summary>
        public const int FallbackPointsPerRing = 8;

        /// <summary>
        /// Spacing in metres of the per-cache ethereal offsets: cache k (k &gt; 0) puts its ethereal tail on
        /// circle ceil(k / <see cref="EtherealSpreadPerCircle"/>), of radius that many times this, so two caches
        /// that both fall through to the last resort never stack on one point. Cache 0 keeps the exact points.
        /// </summary>
        public const float EtherealSpreadSpacing = 1.0f;

        /// <summary>Offsets per ethereal spread circle.</summary>
        public const int EtherealSpreadPerCircle = 8;

        /// <summary>Two plan entries closer than this, in the same cell, are the same point.</summary>
        private const float SamePointTolerance = 0.01f;

        /// <summary>What one placement attempt came to.</summary>
        public enum CachePlaceResult
        {
            /// <summary>In the world and held: stop, this is the cache.</summary>
            Placed,

            /// <summary>This candidate was refused; the next one may not be.</summary>
            Refused,

            /// <summary>No candidate can ever succeed (the cache weenie does not build): stop the whole plan.</summary>
            Permanent,
        }

        /// <summary>The result of <see cref="PlaceFirstAccepted"/>.</summary>
        public readonly struct CachePlacementOutcome
        {
            public CachePlacementOutcome(int index, int tried, bool permanent, CachePlacementAttempt used)
            {
                Index = index;
                Tried = tried;
                Permanent = permanent;
                Used = used;
            }

            /// <summary>Index of the accepted attempt, or -1.</summary>
            public int Index { get; }

            /// <summary>How many attempts were actually made.</summary>
            public int Tried { get; }

            /// <summary>True when an attempt reported <see cref="CachePlaceResult.Permanent"/>.</summary>
            public bool Permanent { get; }

            /// <summary>The accepted attempt (default when none was).</summary>
            public CachePlacementAttempt Used { get; }
        }

        /// <summary>One placement attempt for a cache: where, and whether the cache is built ethereal.</summary>
        public readonly struct CachePlacementAttempt
        {
            public CachePlacementAttempt(Position position, bool ethereal, string kind)
            {
                Position = position;
                Ethereal = ethereal;
                Kind = kind;
            }

            public Position Position { get; }

            /// <summary>
            /// An ethereal cache is walk-through: its placement ignores every non-static object (players,
            /// creatures, corpses), so only baked geometry can refuse it (PhysicsObj.FindObjCollisions_Inner;
            /// measured in WorldEventCachePlacementDatTests). Nothing found on the server side refuses a Use on
            /// an ethereal object, but whether the CLIENT lets a player select and use an ethereal chest is NOT
            /// verified - it is owed an in-game check. That is why ethereal is the last resort, not the default.
            /// </summary>
            public bool Ethereal { get; }

            /// <summary>"requested", "anchor", "ring" or "anchor-ground": what the log line reports.</summary>
            public string Kind { get; }
        }

        /// <summary>
        /// As <see cref="EnumeratePlacementPlan"/>, materialised (tests and diagnostics; production stays lazy).
        /// </summary>
        public static List<CachePlacementAttempt> BuildPlacementPlan(Position requested, Position anchor, Action<Position> snapOutdoor, int cacheIndex = 0)
        {
            return EnumeratePlacementPlan(requested, anchor, cacheIndex, snapOutdoor).ToList();
        }

        /// <summary>
        /// Every place one cache may go, in order, produced lazily so a cache accepted early never pays for the
        /// later candidates' terrain snaps (pure apart from <paramref name="snapOutdoor"/>, which the caller
        /// supplies so a test can run without a landscape):
        /// 1. the requested point, solid (a ring point, or the anchor for a single-cache run);
        /// 2. the anchor exactly as composed, solid, unless that is the requested point;
        /// 3. outdoors only: <see cref="FallbackPointsPerRing"/> solid points on each of
        ///    <see cref="FallbackRingRadii"/> around the anchor, terrain-snapped, dropping any that leave the
        ///    anchor's landblock (this code holds only that landblock's action queue). Indoors a 5-12 m offset
        ///    is a wall or another room, so the rings are skipped;
        /// 4. ethereal, the last resort: the requested point, the anchor, and (outdoors) the anchor snapped to
        ///    the terrain under it, duplicates dropped. For <paramref name="cacheIndex"/> above 0 all three are
        ///    moved by that cache's spread offset (see <see cref="EtherealSpreadSpacing"/>) and snapped outdoors,
        ///    so the caches of one run never stack on the same point.
        /// A crowd moves on; static geometry does not, which is why the ethereal tail is the last word rather
        /// than a deferred retry: by the time an ethereal cache is refused, waiting would not change the answer.
        /// </summary>
        public static IEnumerable<CachePlacementAttempt> EnumeratePlacementPlan(Position requested, Position anchor, int cacheIndex, Action<Position> snapOutdoor)
        {
            if (requested == null && anchor == null)
                yield break;

            requested ??= anchor;
            anchor ??= requested;

            var seen = new List<Position>();

            var first = new Position(requested);
            seen.Add(first);
            yield return new CachePlacementAttempt(first, false, "requested");

            var composed = new Position(anchor);

            if (AddDistinct(seen, composed))
                yield return new CachePlacementAttempt(composed, false, "anchor");

            if (!anchor.Indoors)
            {
                for (var ring = 0; ring < FallbackRingRadii.Count; ring++)
                {
                    var radius = FallbackRingRadii[ring];

                    for (var i = 0; i < FallbackPointsPerRing; i++)
                    {
                        var angle = 2.0 * Math.PI * (i + 0.5 * ring) / FallbackPointsPerRing;

                        var candidate = Offset(anchor, radius, angle);

                        if (candidate == null)
                            continue;

                        Snap(candidate, snapOutdoor);

                        if (AddDistinct(seen, candidate))
                            yield return new CachePlacementAttempt(candidate, false, "ring");
                    }
                }
            }

            var spreadRadius = 0f;
            var spreadAngle = 0.0;

            if (cacheIndex > 0)
            {
                var circle = (cacheIndex - 1) / EtherealSpreadPerCircle + 1;
                spreadRadius = circle * EtherealSpreadSpacing;
                spreadAngle = 2.0 * Math.PI * ((cacheIndex - 1) % EtherealSpreadPerCircle) / EtherealSpreadPerCircle;
            }

            var ethereal = new List<Position>();

            foreach (var (basePoint, kind, ground) in new[] { (requested, "requested", false), (anchor, "anchor", false), (anchor, "anchor-ground", true) })
            {
                if (ground && anchor.Indoors)
                    continue;

                Position point;

                if (spreadRadius > 0f)
                {
                    point = Offset(basePoint, spreadRadius, spreadAngle);

                    if (point == null)
                        continue;

                    Snap(point, snapOutdoor);
                }
                else
                {
                    point = new Position(basePoint);

                    if (ground)
                    {
                        Snap(point, snapOutdoor);

                        if (point.InstancedLandblock != anchor.InstancedLandblock)
                            continue;
                    }
                }

                if (AddDistinct(ethereal, point))
                    yield return new CachePlacementAttempt(point, true, kind);
            }
        }

        /// <summary>
        /// <paramref name="from"/> moved <paramref name="radius"/> metres at <paramref name="angle"/>, or null when
        /// that leaves the landblock (or, from outdoors, lands on an indoor cell id).
        /// </summary>
        private static Position Offset(Position from, float radius, double angle)
        {
            var point = new Position(from);

            point.SetPosition(from.Pos + new Vector3((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)), 0f));

            if (point.InstancedLandblock != from.InstancedLandblock || (!from.Indoors && point.Indoors))
                return null;

            return point;
        }

        /// <summary>
        /// Tries <paramref name="plan"/> in order and stops at the first <see cref="CachePlaceResult.Placed"/>, at
        /// the first <see cref="CachePlaceResult.Permanent"/>, or when <paramref name="stop"/> (consulted after every
        /// refusal) says the run is cleaning up. The plan is enumerated only as far as it is tried.
        /// </summary>
        public static CachePlacementOutcome PlaceFirstAccepted(IEnumerable<CachePlacementAttempt> plan, Func<CachePlacementAttempt, CachePlaceResult> tryPlace, Func<bool> stop)
        {
            if (plan == null || tryPlace == null)
                return new CachePlacementOutcome(-1, 0, false, default);

            var i = 0;

            foreach (var attempt in plan)
            {
                var result = tryPlace(attempt);

                if (result == CachePlaceResult.Placed)
                    return new CachePlacementOutcome(i, i + 1, false, attempt);

                if (result == CachePlaceResult.Permanent)
                    return new CachePlacementOutcome(-1, i + 1, true, default);

                i++;

                if (stop != null && stop())
                    return new CachePlacementOutcome(-1, i, false, default);
            }

            return new CachePlacementOutcome(-1, i, false, default);
        }

        private static void Snap(Position position, Action<Position> snapOutdoor)
        {
            if (snapOutdoor == null || position.Indoors)
                return;

            try
            {
                snapOutdoor(position);
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] cache placement candidate {position.ToLOCString()} could not be snapped to terrain; using the raw Z", ex);
            }
        }

        private static void SnapToTerrain(Position position) => position.AdjustMapCoords();

        /// <summary>Adds <paramref name="candidate"/> unless an equal point is already listed; true when added.</summary>
        private static bool AddDistinct(List<Position> list, Position candidate)
        {
            foreach (var p in list)
            {
                if (SamePoint(p, candidate))
                    return false;
            }

            list.Add(candidate);

            return true;
        }

        private static bool SamePoint(Position a, Position b)
        {
            if (a == null || b == null)
                return false;

            return a.Cell == b.Cell && a.Instance == b.Instance && Vector3.Distance(a.Pos, b.Pos) < SamePointTolerance;
        }

        /// <summary>
        /// WP-19 item 3: the environmental treasure standing around one cache. Runs on the same landblock
        /// action queue, immediately after the cache it belongs to entered the world.
        ///
        /// Deliberately NOT routed through WorldEventSpawner.SpawnDecor, even though the shape is the same.
        /// That path ends in Spawner.Adopt, which refuses outright once the creature cleanup pass has run,
        /// and caches are placed at exactly that moment: WorldEvent.Finish calls SpawnCaches and then
        /// DestroyEventCreatures on the very next statement, while SpawnCaches has only ENQUEUED the
        /// placement onto the landblock queue. By the time this actually runs, creaturePassRan is true.
        /// That is precisely why the cache itself uses evt.AddHeld directly rather than Adopt, and the
        /// dressing has to do the same.
        ///
        /// It is also why the dressing carries NO PropertyBool.WorldEventCache: GenericObject.ActOnUse
        /// dispatches the reward claim on that flag (GenericObject.cs:53-54), so flagging a prop would let
        /// a player claim their reward by clicking a pile of treasure. Its cache-matched lifetime comes
        /// from WorldEvent.AddCacheDecor instead.
        /// </summary>
        private static void SpawnCacheDecor(WorldEvent evt, uint runId, IReadOnlyList<DecorDef> cacheDecor, Position cachePosition)
        {
            if (cacheDecor == null || cacheDecor.Count == 0 || cachePosition == null)
                return;

            var placed = 0;

            foreach (var def in cacheDecor)
            {
                if (def == null || def.Wcid == 0)
                    continue;

                WorldObject wo;

                try
                {
                    wo = WorldObjectFactory.CreateNewWorldObject(def.Wcid);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} could not create cache decor wcid {def.Wcid}", ex);
                    continue;
                }

                if (wo == null)
                {
                    log.Error($"[WORLDEVENT] run={runId} cache decor wcid {def.Wcid} does not exist; skipped");
                    continue;
                }

                var position = CacheDecorPosition(cachePosition, def.Dx, def.Dy, def.Inverted, def.Yaw, def.Pitch);

                try
                {
                    // Unlike sky decor, these stand on the ground beside a chest, so they ARE snapped and
                    // Dz is re-applied on top of the snapped terrain - the WP-15 npc rule, which is what
                    // makes dz mean "metres above the ground under this prop".
                    if (!position.Indoors)
                    {
                        position.AdjustMapCoords();
                        position.PositionZ += def.Dz;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} cache decor wcid={def.Wcid} terrain snap failed at {position.ToLOCString()}", ex);

                    wo.Destroy();
                    continue;
                }

                wo.Location = position;

                wo.SetProperty(PropertyInt.WorldEventId, (int)runId);
                wo.SetProperty(PropertyFloat.DefaultScale, def.Scale);
                wo.SetProperty(PropertyFloat.MotionSpeed, def.Speed);

                // Same AND-not-instead reasoning as spawn-time decor: the create packet ForwardSpeed was
                // seeded from the weenie while the object was being built, so both are written. A prop with
                // no MotionTable has a null CurrentMotionState and simply ignores it.
                if (wo.CurrentMotionState?.MotionState != null)
                    wo.CurrentMotionState.MotionState.ForwardSpeed = def.Speed;

                // Mandatory, exactly as for the cache: no Generator back-reference means the landblock
                // heartbeat would otherwise decay it mid claim window.
                wo.TimeToRot = -1;

                var entered = false;

                try
                {
                    entered = wo.EnterWorld();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} cache decor wcid={def.Wcid} failed to enter the world at {position.ToLOCString()}", ex);
                }

                if (!entered)
                {
                    log.Error($"[WORLDEVENT] run={runId} cache decor wcid={def.Wcid} failed to enter the world at {position.ToLOCString()}");

                    if (!wo.IsDestroyed)
                        wo.Destroy();

                    continue;
                }

                if (!evt.AddHeld(wo))
                {
                    log.Info($"[WORLDEVENT] run={runId} cache decor wcid={def.Wcid} placed after cleanup closed the held list; destroyed");
                    wo.Destroy();
                    continue;
                }

                // What ties it to the CACHE lifetime rather than the run lifetime: DestroyCaches destroys
                // these alongside the caches at the end of the claim window, and the cache-exempt creature
                // pass skips them.
                evt.AddCacheDecor(wo.Guid.Full);

                placed++;

                log.Info($"[WORLDEVENT] run={runId} spawn wcid={wo.WeenieClassId} guid=0x{wo.Guid.Full:X8} loc={wo.Location?.ToLOCString()}");
            }

            if (placed > 0)
                log.Info($"[WORLDEVENT] run={runId} cache decor placed={placed} of {cacheDecor.Count}");
        }

        private static CachePlaceResult TryPlace(WorldEvent evt, uint runId, uint cacheWcid, Position position, bool ethereal, out WorldObject placed)
        {
            placed = null;

            var wo = WorldObjectFactory.CreateNewWorldObject(cacheWcid);

            if (wo == null)
            {
                // Permanent: the weenie does not build, so every later candidate would fail the same way.
                log.Error($"[WORLDEVENT] run={runId} failed to create cache wcid={cacheWcid}");
                return CachePlaceResult.Permanent;
            }

            if (!TryEnterCache(wo, position, ethereal, w => w.EnterWorld()))
            {
                wo.Destroy();
                return CachePlaceResult.Refused;
            }

            StampPlacedCache(wo, runId);

            // AddHeld is the single held-list entry point (it takes the HeldObjects monitor itself) and
            // refuses once the run has closed the list. A refusal means this cache was placed after the
            // final cleanup sweep took its snapshot - it carries TimeToRot = -1 and nothing would ever
            // remove it - so it is destroyed here rather than orphaned.
            if (!evt.AddHeld(wo))
            {
                log.Info($"[WORLDEVENT] run={runId} cache wcid={cacheWcid} placed after cleanup closed the held list; destroyed");
                wo.Destroy();
                return CachePlaceResult.Refused;
            }

            log.Info($"[WORLDEVENT] run={runId} spawn wcid={cacheWcid} guid=0x{wo.Guid.Full:X8} loc={wo.Location.ToLOCString()}");

            placed = wo;

            return CachePlaceResult.Placed;
        }

        /// <summary>
        /// Everything a cache needs BEFORE it enters the world. The ethereal flag must be set here and not
        /// after: InitPhysicsObj builds the physics state from the property (CalculatedPhysicsState), so a flag
        /// set once the physics object exists is too late for the placement it was meant to change. The
        /// location is a copy, because a physics placement mutates Location and the plan's position is shared
        /// with the log lines.
        /// </summary>
        internal static void PrepareCache(WorldObject wo, Position at, bool ethereal)
        {
            if (wo == null)
                return;

            if (ethereal)
                wo.Ethereal = true;

            wo.Location = at != null ? new Position(at) : null;
        }

        /// <summary>
        /// PrepareCache, then <paramref name="enter"/> (EnterWorld in production; the dat test passes the
        /// landblock-free InitPhysicsObj + AddPhysicsObj pair). One sequence for both, so the test exercises the
        /// ordering production uses rather than a copy of it.
        /// </summary>
        internal static bool TryEnterCache(WorldObject wo, Position at, bool ethereal, Func<WorldObject, bool> enter)
        {
            if (wo == null || enter == null)
                return false;

            PrepareCache(wo, at, ethereal);

            return enter(wo);
        }

        /// <summary>
        /// The stamps a cache gets once it is in the world (testable without a landblock).
        /// </summary>
        internal static void StampPlacedCache(WorldObject wo, uint runId)
        {
            if (wo == null)
                return;

            // Mandatory. A hand-spawned object has a dynamic guid and no Generator back-reference, so
            // IsDecayable() is true for it and the landblock heartbeat would Destroy it at DefaultTimeToRot -
            // in the middle of the claim window, with no message to anyone.
            wo.TimeToRot = -1;

            // The run stamp: it is what makes the claim handler refuse a stale cache, what excludes the
            // object from the shard save, and what the orphan sweep looks for.
            wo.SetProperty(PropertyInt.WorldEventId, (int)runId);
        }

        /// <summary>
        /// WP-19 item 2 (D6 - pure): which cache weenie a run actually uses. A non-zero override wins, but
        /// ONLY when it resolved to a real weenie - an override naming a wcid that does not exist must fall
        /// back to the reward profile rather than spawning nothing, because a run that places no cache pays
        /// nobody. The caller does the resolving (it needs the world DB) and passes the answer in.
        ///
        /// Kept for callers written before the pool existed; delegates to the pool-aware overload below with
        /// an empty pool, so this always resolves to the profile when the override does not win.
        /// </summary>
        public static uint ResolveCacheWcid(uint profileWcid, uint overrideWcid, bool overrideResolves)
        {
            return ResolveCacheWcid(profileWcid, Array.Empty<uint>(), overrideWcid, overrideResolves, null);
        }

        /// <summary>
        /// The pool-aware form. Precedence is: a resolving override wins outright; otherwise, if
        /// <paramref name="pool"/> is non-empty, one entry is chosen at random via <paramref name="pickIndex"/>
        /// (called with the pool's count, expected to return an index in [0, count)); otherwise
        /// <paramref name="profileWcid"/>. Pure and testable - the caller does the weenie-existence filtering
        /// on <paramref name="pool"/> and the random draw, and passes both in. This function has no notion of
        /// run outcome: the pool is a Success-only reward, so a caller rewarding a failed run passes an EMPTY
        /// <paramref name="pool"/> rather than teaching this helper about <see cref="WorldEventOutcome"/>.
        /// </summary>
        public static uint ResolveCacheWcid(uint profileWcid, IReadOnlyList<uint> pool, uint overrideWcid,
            bool overrideResolves, Func<int, int> pickIndex)
        {
            if (overrideWcid != 0 && overrideResolves)
                return overrideWcid;

            if (pool != null && pool.Count > 0)
                return pool[pickIndex(pool.Count)];

            return profileWcid;
        }

        /// <summary>
        /// Where one piece of cache dressing goes (WP-19 item 3, D6 - pure, no engine object): the cache
        /// position offset by (<paramref name="dx"/> east, <paramref name="dy"/> north), turned by
        /// <paramref name="yawDegrees"/> and optionally inverted.
        ///
        /// SetPosition rather than a raw coordinate write, for the reason WorldEventSpawner.NpcPosition
        /// gives: X and Y move here, a land cell is 24 m, and SetPosition is what re-addresses the position
        /// to the cell it actually landed in. Z is left alone - the caller snaps it to terrain and applies
        /// Dz, which needs a live landcell and so cannot happen in a pure helper.
        ///
        /// The five-argument overload is kept so WP-19's callers and tests read unchanged; it is exactly
        /// the six-argument overload below with pitch 0 (WP-21).
        /// </summary>
        public static Position CacheDecorPosition(Position cache, float dx, float dy, bool inverted, float yawDegrees)
        {
            return CacheDecorPosition(cache, dx, dy, inverted, yawDegrees, 0f);
        }

        /// <summary>
        /// The WP-21 form: as the five-argument overload, but also applies <paramref name="pitchDegrees"/>
        /// about the object's local X axis before the yaw (see WorldEventSpawner.DecorRotation's remarks).
        /// </summary>
        public static Position CacheDecorPosition(Position cache, float dx, float dy, bool inverted,
            float yawDegrees, float pitchDegrees)
        {
            if (cache == null)
                return null;

            var position = new Position(cache.LandblockId.Raw,
                cache.PositionX, cache.PositionY, cache.PositionZ,
                cache.RotationX, cache.RotationY, cache.RotationZ, cache.RotationW,
                cache.Instance);

            position.SetPosition(new Vector3(cache.PositionX + dx, cache.PositionY + dy, cache.PositionZ));

            position.Rotation = WorldEventSpawner.DecorRotation(inverted, yawDegrees, pitchDegrees);

            return position;
        }

        /// <summary>
        /// The configured cache-weenie override, read fresh per run so the repo owner can swap chests live
        /// with "/modifylong world_events_cache_wcid &lt;wcid&gt;". 0 means "use the reward profile".
        /// Negative or out-of-range values are treated as 0 rather than wrapped into a nonsense wcid.
        /// </summary>
        private static uint ReadCacheWcidOverride()
        {
            var configured = PropertyManager.GetLong("world_events_cache_wcid").Item;

            return configured > 0 && configured <= uint.MaxValue ? (uint)configured : 0u;
        }

        // ---- cache effect (sparkle burst on appearance, WP-22) -----------------------------------------

        /// <summary>
        /// world_events_cache_effect_script -> the validated <see cref="PlayScript"/> to play (D6 - pure).
        /// 0 is a distinct "disabled" value, reported through <paramref name="valid"/> as true (it is not a
        /// mistake, it is the off switch) and resolved to <see cref="PlayScript.Invalid"/>, which
        /// <see cref="ResolveCacheEffectScriptDid"/> turns into DataID 0, so nothing is armed. Any other value must name a defined
        /// <see cref="PlayScript"/> member; one that does not is refused rather than cast blindly (the same
        /// reasoning <see cref="WorldEventAnnouncer.ResolveAnnounceChatType"/> uses for chat types) and
        /// falls back to <see cref="DefaultCacheEffectScript"/> (WeddingBliss).
        /// </summary>
        public static PlayScript ResolveCacheEffectScript(long configured, out bool valid)
        {
            if (configured == 0)
            {
                valid = true;
                return PlayScript.Invalid;
            }

            valid = configured > 0 && configured <= uint.MaxValue
                    && Enum.IsDefined(typeof(PlayScript), (PlayScript)(uint)configured);

            return valid ? (PlayScript)(uint)configured : (PlayScript)DefaultCacheEffectScript;
        }

        /// <summary>
        /// Whether a repeat fire scheduled <paramref name="repeatSeconds"/> after <paramref name="lastPlayedAt"/>
        /// should actually play (D6 - pure): repeat 0 (or negative) means "once only" and never replays;
        /// otherwise a replay is due once <paramref name="now"/> reaches the next cadence tick, but never
        /// once <paramref name="now"/> has reached <paramref name="claimWindowEndsAt"/> (0 means the window
        /// has no end yet and never gates). All four are the same unix-seconds convention WorldEvent.Now()
        /// and ClaimWindowEndsAt use.
        /// </summary>
        public static bool ShouldReplay(double now, double claimWindowEndsAt, double lastPlayedAt, long repeatSeconds)
        {
            if (repeatSeconds <= 0)
                return false;

            if (claimWindowEndsAt > 0 && now >= claimWindowEndsAt)
                return false;

            return now >= lastPlayedAt + repeatSeconds;
        }

        /// <summary>
        /// The configured cache-effect script, read fresh per run. A read that throws - a unit test has no
        /// shard config at all - reads as the shipped default, mirroring WorldEvent.ReadBossDamageDials. A
        /// bad (non-zero, undefined) value warns ONCE for the process, not once per cache.
        /// </summary>
        private static PlayScript CacheEffectScript()
        {
            long configured;

            try
            {
                configured = PropertyManager.GetLong("world_events_cache_effect_script", DefaultCacheEffectScript).Item;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read world_events_cache_effect_script; using the shipped default", ex);

                configured = DefaultCacheEffectScript;
            }

            var script = ResolveCacheEffectScript(configured, out var valid);

            if (!valid && !badCacheEffectScriptLogged)
            {
                badCacheEffectScriptLogged = true;

                log.Warn($"[WORLDEVENT] world_events_cache_effect_script {configured} is not a defined PlayScript; using {(uint)script} ({script})");
            }

            return script;
        }

        /// <summary>
        /// world_events_cache_effect_repeat_seconds, read fresh per run. A read that throws reads as the
        /// shipped default; a negative configured value is treated as 0 ("play once only") rather than
        /// handed to the delay math.
        /// </summary>
        private static long ReadCacheEffectRepeatSeconds()
        {
            try
            {
                var configured = PropertyManager.GetLong("world_events_cache_effect_repeat_seconds", DefaultCacheEffectRepeatSeconds).Item;

                return configured > 0 ? configured : 0;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read world_events_cache_effect_repeat_seconds; using the shipped default", ex);

                return DefaultCacheEffectRepeatSeconds;
            }
        }

        /// <summary>
        /// Picks the full-intensity script out of one PhysicsScriptTable entry (pure): the (mod, DataID) pair
        /// with the highest mod. Entries are graded by mod - the Enchant colors carry 0 / 0.5 / 1, WeddingBliss
        /// a single 1 - and a PlayEffect sent at the default speed 1.0 is the top grade. Returns 0 for a
        /// missing or empty entry, which the caller treats as "nothing to play".
        /// </summary>
        public static uint SelectFullIntensityScript(IEnumerable<(float Mod, uint ScriptId)> entries)
        {
            if (entries == null)
                return 0;

            var found = false;
            var bestMod = 0f;
            var best = 0u;

            foreach (var (mod, scriptId) in entries)
            {
                if (scriptId == 0)
                    continue;

                if (!found || mod > bestMod)
                {
                    found = true;
                    bestMod = mod;
                    best = scriptId;
                }
            }

            return best;
        }

        /// <summary>
        /// The configured PlayScript type -> the raw 0x33 DataID the player table maps it to
        /// (<see cref="PlayerPhysicsScriptTableId"/>), read from the portal dat once per run. 0 means "send
        /// nothing": the effect is disabled, the type has no player-table entry, or the dat read failed - the
        /// latter two log a warning, so a silent no-show can never recur unexplained.
        /// </summary>
        private static uint ResolveCacheEffectScriptDid(uint runId, PlayScript script)
        {
            if (script == PlayScript.Invalid)
                return 0;

            uint did = 0;

            try
            {
                var table = DatManager.PortalDat?.ReadFromDat<ACE.DatLoader.FileTypes.PhysicsScriptTable>(PlayerPhysicsScriptTableId);

                if (table?.ScriptTable != null && table.ScriptTable.TryGetValue((uint)script, out var data) && data != null)
                    did = SelectFullIntensityScript(data.Scripts.Select(s => (s.Mod, s.ScriptId)));
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={runId} could not read PhysicsScriptTable 0x{PlayerPhysicsScriptTableId:X8} for the cache effect", ex);
                return 0;
            }

            if (did == 0)
                log.Warn($"[WORLDEVENT] run={runId} cache effect script {(uint)script} ({script}) has no entry in PhysicsScriptTable 0x{PlayerPhysicsScriptTableId:X8}; no effect will play");
            else
                log.Info($"[WORLDEVENT] run={runId} cache effect script {(uint)script} ({script}) resolves to 0x{did:X8}");

            return did;
        }

        /// <summary>
        /// Arms the sparkle burst on one just-placed cache (TECH-DESIGN 2.7). Runs after EnterWorld/AddHeld
        /// both succeeded. A disabled or unresolvable script (DataID 0) or a cache with no landblock (should
        /// not happen - EnterWorld just succeeded on one) is a no-op.
        /// </summary>
        private static void ScheduleCacheEffect(WorldEvent evt, uint runId, WorldObject cache, uint scriptDid, long repeatSeconds)
        {
            if (cache == null || scriptDid == 0)
                return;

            var landblock = cache.CurrentLandblock;

            if (landblock == null)
                return;

            ArmCacheEffect(evt, runId, landblock, cache.Guid, scriptDid, repeatSeconds, CacheEffectInitialDelaySeconds, 0);
        }

        /// <summary>
        /// Queues one delayed fire of the cache effect on the landblock's action chain. Captures only the
        /// runId/guid, never the WorldObject itself, so a chain armed now and fired much later (a long
        /// repeat interval) always re-resolves the cache against the landblock rather than holding a
        /// reference that could outlive it.
        /// </summary>
        private static void ArmCacheEffect(WorldEvent evt, uint runId, Landblock landblock, ObjectGuid guid,
            uint scriptDid, long repeatSeconds, double delaySeconds, double lastPlayedAt)
        {
            try
            {
                var chain = new ActionChain();
                chain.AddDelaySeconds(delaySeconds);
                chain.AddAction(landblock, () => FireCacheEffect(evt, runId, landblock, guid, scriptDid, repeatSeconds, lastPlayedAt));
                chain.EnqueueChain();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={runId} could not arm the cache effect", ex);
            }
        }

        /// <summary>
        /// One fire of the cache effect: the initial play when <paramref name="lastPlayedAt"/> is 0 (which
        /// always plays, whatever repeatSeconds is - "repeat 0" still gets the one appearance burst), a
        /// scheduled repeat otherwise (gated by <see cref="ShouldReplay"/>, which is what stops the chain
        /// once the claim window has closed). Re-resolves the cache by guid rather than trusting a captured
        /// reference, and stops outright once the run is no longer valid or the cache is gone.
        /// </summary>
        private static void FireCacheEffect(WorldEvent evt, uint runId, Landblock landblock, ObjectGuid guid,
            uint scriptDid, long repeatSeconds, double lastPlayedAt)
        {
            if (!evt.RunValid(runId))
                return;

            var now = Time.GetUnixTime();

            if (lastPlayedAt > 0 && !ShouldReplay(now, evt.ClaimWindowEndsAt, lastPlayedAt, repeatSeconds))
                return;

            var cache = landblock.GetObject(guid);

            if (cache == null || cache.IsDestroyed)
                return;

            // 0xF754 PlayScriptId with the raw DataID, NOT PlayParticleEffect's 0xF755 PlayEffect: the latter
            // names a type the client resolves through the cache's own table, which lacks it (see
            // PlayerPhysicsScriptTableId), so it never drew. The DataID form bypasses that table.
            cache.EnqueueBroadcast(new GameMessagePlayScriptId(guid, scriptDid));

            if (repeatSeconds > 0)
                ArmCacheEffect(evt, runId, landblock, guid, scriptDid, repeatSeconds, repeatSeconds, now);
        }

        /// <summary>
        /// Whether a wcid names a weenie this server can actually build. Uses the same cached world-DB
        /// lookup WorldObject.InitPhysicsObj does, so an override is validated without building an object.
        /// </summary>
        private static bool WeenieExists(uint wcid)
        {
            try
            {
                return DatabaseManager.World.GetCachedWeenie(wcid) != null;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] could not check cache override wcid {wcid}", ex);
                return false;
            }
        }

        private static bool IsAborted(WorldEventOutcome outcome)
        {
            return outcome == WorldEventOutcome.AbortedAdmin
                || outcome == WorldEventOutcome.AbortedShutdown
                || outcome == WorldEventOutcome.AbortedError;
        }
    }
}
