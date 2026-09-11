using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Proving Grounds: Speed - one ranked board line. Immutable: every public read on
    /// SpeedBoardManager hands back a fresh list of these, so a caller can never see the cache
    /// mutating underneath it.
    /// </summary>
    public sealed class SpeedBoardEntry
    {
        public SpeedBoardEntry(uint characterId, string characterName, int seasonId, long centiseconds, int characterLevel, DateTime completedAt)
        {
            CharacterId = characterId;
            CharacterName = characterName;
            SeasonId = seasonId;
            Centiseconds = centiseconds;
            CharacterLevel = characterLevel;
            CompletedAt = completedAt;
        }

        public uint CharacterId { get; }

        /// <summary>Snapshot taken at completion time - survives a later rename or character delete.</summary>
        public string CharacterName { get; }

        public int SeasonId { get; }

        /// <summary>Elapsed run time in hundredths of a second. LOWER IS BETTER.</summary>
        public long Centiseconds { get; }

        public int CharacterLevel { get; }

        /// <summary>UTC.</summary>
        public DateTime CompletedAt { get; }
    }

    /// <summary>
    /// Proving Grounds: Speed - the in-memory board cache that answers /top speed.
    ///
    /// THIS BOARD INVERTS THE OTHER THREE. The DPS, Survival and Wave boards keep their scores on the
    /// character biota and are served from PlayerManager's in-memory collections, so a raw SQL edit to
    /// a score there is invisible until the next restart. Speed is the opposite: the shard table
    /// `character_speed_run` is the RECORD OF TRUTH and this cache is DERIVED from it. Admin tooling
    /// must therefore write the TABLE and then refresh this cache (RefreshSeason), never the biota.
    /// See Docs/ProvingGroundsSpeed/DESIGN.md section 3.5.
    ///
    /// Board semantics: ONE entry per character per season - that character's best (lowest) time in
    /// that season - ranked ASCENDING by centiseconds, because lower is better.
    ///
    /// Threading: completions arrive on world threads, which tick landblocks in parallel, so the cache
    /// is guarded by a lock rather than by SpeedSeasonManager's volatile reference swap. That pattern
    /// works there because the season registry is only ever replaced wholesale; this one mutates on
    /// every completion.
    /// </summary>
    public static class SpeedBoardManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object boardLock = new object();

        // seasonId -> characterId -> that character's best entry in that season.
        // Only ever touched under boardLock; every public read copies out.
        private static readonly Dictionary<int, Dictionary<uint, SpeedBoardEntry>> boards = new Dictionary<int, Dictionary<uint, SpeedBoardEntry>>();

        /// <summary>
        /// Builds the whole cache from scratch from `character_speed_run`, at boot.
        ///
        /// A read failure is swallowed into an empty cache rather than left to propagate, for the same
        /// reason SpeedSeasonManager.Initialize() swallows its own: `character_speed_run` is a
        /// brand-new table created by a versioned migration, and AutoApplyDatabaseUpdates is
        /// config-gated (Program.cs), so a deployment with that flag off has no such table and must
        /// still boot. An empty board degrades correctly - /top speed simply shows nothing.
        /// </summary>
        public static void Initialize()
        {
            List<CharacterSpeedRun> rows;

            try
            {
                rows = DatabaseManager.Shard.BaseDatabase.GetAllSpeedRuns();
            }
            catch (Exception ex)
            {
                log.Error("SpeedBoardManager: failed to read the character_speed_run table - the speed board will be empty. This is expected if the table does not exist yet on this deployment.", ex);

                lock (boardLock)
                    boards.Clear();

                return;
            }

            var loaded = new Dictionary<int, Dictionary<uint, SpeedBoardEntry>>();
            var entryCount = 0;

            if (rows != null)
            {
                foreach (var seasonRows in rows.Where(r => r != null).GroupBy(r => r.SeasonId))
                {
                    var season = BuildSeasonMap(seasonRows.Select(FromRow));
                    loaded[seasonRows.Key] = season;
                    entryCount += season.Count;
                }
            }

            lock (boardLock)
            {
                boards.Clear();

                foreach (var kvp in loaded)
                    boards[kvp.Key] = kvp.Value;
            }

            log.Info($"SpeedBoardManager: {entryCount} board entr(ies) across {loaded.Count} season(s) loaded from the shard database.");
        }

        /// <summary>
        /// Folds one completion into the cache: insert, or improve the character's existing line when
        /// this run ranks better. CACHE ONLY - no database work at all. The caller is responsible for
        /// writing the row to `character_speed_run`, which is the record of truth.
        /// </summary>
        public static void RecordCompletion(SpeedBoardEntry entry)
        {
            if (entry == null)
                return;

            lock (boardLock)
            {
                if (!boards.TryGetValue(entry.SeasonId, out var season))
                {
                    season = new Dictionary<uint, SpeedBoardEntry>();
                    boards[entry.SeasonId] = season;
                }

                if (!season.TryGetValue(entry.CharacterId, out var existing) || CompareEntries(entry, existing) < 0)
                    season[entry.CharacterId] = entry;
            }
        }

        /// <summary>
        /// A fresh, rank-ordered snapshot of one season's board - one line per character, best time
        /// first. An unknown season returns an empty list.
        /// </summary>
        public static List<SpeedBoardEntry> GetSeasonBoard(int seasonId)
        {
            List<SpeedBoardEntry> snapshot;

            lock (boardLock)
            {
                if (!boards.TryGetValue(seasonId, out var season))
                    return new List<SpeedBoardEntry>();

                snapshot = season.Values.ToList();
            }

            return BuildBoard(snapshot);
        }

        /// <summary>
        /// Re-reads ONE season from `character_speed_run` and replaces that season in the cache
        /// wholesale - including replacing it with nothing when the table now holds no rows for it.
        /// This is the "the table is authoritative" half of the design: admin tooling writes the table
        /// and then calls this.
        ///
        /// On a read failure the cached season is left UNTOUCHED and the error is logged. Blanking a
        /// live board because one query failed would be strictly worse than serving a slightly stale
        /// one, and the next boot rebuilds from the table regardless.
        /// </summary>
        public static void RefreshSeason(int seasonId)
        {
            List<CharacterSpeedRun> rows;

            try
            {
                rows = DatabaseManager.Shard.BaseDatabase.GetSpeedRunsBySeason(seasonId);
            }
            catch (Exception ex)
            {
                log.Error($"SpeedBoardManager: failed to re-read season {seasonId} from the character_speed_run table; the cached board for that season is left as it was and may now be stale.", ex);
                return;
            }

            var season = BuildSeasonMap((rows ?? new List<CharacterSpeedRun>()).Where(r => r != null).Select(FromRow));

            lock (boardLock)
            {
                if (season.Count == 0)
                    boards.Remove(seasonId);
                else
                    boards[seasonId] = season;
            }

            log.Info($"SpeedBoardManager: season {seasonId} refreshed from the shard database - {season.Count} board entr(ies).");
        }

        /// <summary>
        /// The season's leader: the fastest entry that actually counts. Walks the board in rank order
        /// and returns the first entry that has a POSITIVE time and whose character is not
        /// leaderboard-exempt; null when there is no such entry.
        ///
        /// Because the board is already rank-ordered this short-circuits on the first eligible entry
        /// rather than scanning the whole board.
        ///
        /// A non-positive time is a backwards-clock artefact (the funnel logs it loudly), not a real
        /// record, so it is cached - the cache must agree with the table - but never sets the bar.
        /// A character the lookup cannot resolve (deleted since the run) is NOT exempt and its time
        /// counts: the table deliberately outlives the character, which is why it snapshots the name.
        ///
        /// This is the single implementation of "who counts" on this board: the record bar
        /// (GetSeasonRecord) and the /top speed winners line both read it, so the name shown as the
        /// winner and the time treated as the record can never disagree.
        /// </summary>
        public static SpeedBoardEntry GetSeasonLeader(int seasonId, IReadOnlySet<string> exemptNames)
        {
            foreach (var entry in GetSeasonBoard(seasonId))
            {
                if (entry.Centiseconds <= 0)
                    continue;

                var player = PlayerManager.FindByGuid(entry.CharacterId);

                if (player != null && LeaderboardExemptionManager.IsExempt(player, exemptNames))
                    continue;

                return entry;
            }

            return null;
        }

        /// <summary>
        /// The record bar for a season - the leader's time, or null when nobody eligible has one.
        /// A thin forwarder onto <see cref="GetSeasonLeader"/>; see that method for the eligibility rules.
        /// </summary>
        public static long? GetSeasonRecord(int seasonId, IReadOnlySet<string> exemptNames)
        {
            return GetSeasonLeader(seasonId, exemptNames)?.Centiseconds;
        }

        /// <summary>
        /// THE rank comparison, and a total order so board ordering never depends on input order:
        /// a NON-POSITIVE time ranks below every positive one, then ascending centiseconds (lower is
        /// better), then earlier CompletedAt (first to achieve a time holds the higher rank), then
        /// ascending CharacterId. Nulls sort last.
        ///
        /// The non-positive rule is load-bearing, not cosmetic, because this comparison is ALSO the
        /// best-per-character collapse in BuildSeasonMap. A backwards-clock artefact is recorded as 0
        /// (ToSpeedRunCentiseconds), which is the lowest value a time can take, so a raw ascending
        /// compare would make that 0 the character's permanent "best" and every later genuine run would
        /// lose to it. GetSeasonLeader and the /top speed renderer both skip non-positive entries, so
        /// that character would then vanish from the board for the whole season - and because
        /// character_speed_run is append-only and is the record of truth, a restart rebuilds the same
        /// collapse rather than healing it. GetCachedSpeedBest already treats a non-positive cached best
        /// as unset; this is the same rule applied to the store that outlives the process.
        ///
        /// Pure and static, mirroring SpeedSeasonManager.SelectSeasonAt, so the ranking rule is
        /// unit-testable with no database and no live world.
        /// </summary>
        public static int CompareEntries(SpeedBoardEntry a, SpeedBoardEntry b)
        {
            if (ReferenceEquals(a, b))
                return 0;

            if (a == null)
                return 1;

            if (b == null)
                return -1;

            // a real time always outranks a backwards-clock artefact, whatever the artefact's value
            var aCounts = a.Centiseconds > 0;
            var bCounts = b.Centiseconds > 0;

            if (aCounts != bCounts)
                return aCounts ? -1 : 1;

            var byTime = a.Centiseconds.CompareTo(b.Centiseconds);
            if (byTime != 0)
                return byTime;

            var byWhen = a.CompletedAt.CompareTo(b.CompletedAt);
            if (byWhen != 0)
                return byWhen;

            return a.CharacterId.CompareTo(b.CharacterId);
        }

        /// <summary>
        /// Collapses raw rows to one line per character - that character's best-ranking run - and
        /// returns them in rank order. Tolerates a null sequence and null elements.
        ///
        /// Pure and static for the same reason CompareEntries is.
        /// </summary>
        public static List<SpeedBoardEntry> BuildBoard(IEnumerable<SpeedBoardEntry> rows)
        {
            if (rows == null)
                return new List<SpeedBoardEntry>();

            var best = BuildSeasonMap(rows);

            var board = best.Values.ToList();
            board.Sort(CompareEntries);

            return board;
        }

        /// <summary>
        /// The best-per-character collapse, shared by BuildBoard and the cache builders so the cache
        /// and a freshly built board can never disagree about which of a character's runs is "best".
        /// </summary>
        private static Dictionary<uint, SpeedBoardEntry> BuildSeasonMap(IEnumerable<SpeedBoardEntry> rows)
        {
            var best = new Dictionary<uint, SpeedBoardEntry>();

            if (rows == null)
                return best;

            foreach (var row in rows)
            {
                if (row == null)
                    continue;

                if (!best.TryGetValue(row.CharacterId, out var existing) || CompareEntries(row, existing) < 0)
                    best[row.CharacterId] = row;
            }

            return best;
        }

        private static SpeedBoardEntry FromRow(CharacterSpeedRun row)
        {
            return new SpeedBoardEntry(row.CharacterId, row.CharacterName, row.SeasonId, row.Centiseconds, row.CharacterLevel, row.CompletedAt);
        }
    }
}
