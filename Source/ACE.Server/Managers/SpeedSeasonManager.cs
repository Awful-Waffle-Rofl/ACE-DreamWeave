using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Database.Models.World;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Proving Grounds: Speed - the season registry, loaded once at boot from the world database's
    /// speed_season table and reloadable at runtime. Mirrors RealmManager's shape: a volatile
    /// dictionary swapped wholesale by reference so readers on world threads always see a complete
    /// registry, and an additive Reload() that never removes a season a live run might still
    /// reference.
    /// </summary>
    public static class SpeedSeasonManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // replaced wholesale by Initialize/Reload (reference swap); readers on world
        // threads always see a complete registry
        private static volatile Dictionary<int, SpeedSeason> seasons = new Dictionary<int, SpeedSeason>();

        /// <summary>
        /// Builds the registry from scratch from the world database.
        ///
        /// Unlike RealmManager, a database read failure here is swallowed rather than left to
        /// propagate: speed_season is a brand-new, optional table (not part of every deployment's
        /// world database), and AutoApplyDatabaseUpdates is config-gated (Program.cs), so a server
        /// whose operator has that flag off would otherwise fail to boot on a table it does not have.
        /// An empty registry degrades correctly - GetActiveSeason() returns null and the season
        /// portal refuses use - so failing open here is strictly safer than RealmManager's table,
        /// which is core and whose absence really does mean a broken deployment.
        /// </summary>
        public static void Initialize()
        {
            List<SpeedSeason> rows;

            try
            {
                rows = DatabaseManager.World.GetAllSpeedSeasons();
            }
            catch (Exception ex)
            {
                log.Error("SpeedSeasonManager: failed to read the speed_season table - the season registry will be empty. This is expected if the table does not exist yet on this deployment.", ex);
                seasons = new Dictionary<int, SpeedSeason>();
                return;
            }

            var loaded = new Dictionary<int, SpeedSeason>();

            MergeSeasons(loaded, rows);
            LogContentErrors(rows);

            seasons = loaded;

            log.Info($"SpeedSeasonManager: {seasons.Count} season(s) loaded from the world database.");
        }

        /// <summary>
        /// Re-reads the season registry from the world database and merges it additively into the
        /// live registry: new seasons are added, existing seasons pick up field changes. Seasons no
        /// longer in the database are kept and reported as missing - a live run may still reference
        /// the season it started under, same reasoning as RealmManager.Reload().
        ///
        /// Same read-failure handling as Initialize(): on a database error, the live registry is left
        /// untouched and (0, 0, empty) is returned.
        /// </summary>
        public static (int added, int updated, List<int> missing) Reload()
        {
            List<SpeedSeason> rows;

            try
            {
                rows = DatabaseManager.World.GetAllSpeedSeasons();
            }
            catch (Exception ex)
            {
                log.Error("SpeedSeasonManager: failed to read the speed_season table during reload - the registry is unchanged.", ex);
                return (0, 0, new List<int>());
            }

            var merged = new Dictionary<int, SpeedSeason>(seasons);

            var (added, updated, seen) = MergeSeasons(merged, rows);

            var missing = merged.Keys.Where(id => !seen.Contains(id)).OrderBy(id => id).ToList();

            LogContentErrors(rows);

            seasons = merged;

            log.Info($"SpeedSeasonManager: registry reloaded - {added} added, {updated} updated, {missing.Count} missing from database (kept), {merged.Count} total.");

            return (added, updated, missing);
        }

        /// <summary>
        /// Logs a warning for every malformed row skipped by the load, and a warning for every
        /// overlapping pair, naming both rows and stating that the latest StartsAt wins. Shared by
        /// Initialize() and Reload() so an admin running the reload command sees the same content
        /// errors a boot would have shown.
        /// </summary>
        private static void LogContentErrors(List<SpeedSeason> rows)
        {
            foreach (var row in rows)
            {
                if (!IsWellFormed(row))
                    log.Warn($"SpeedSeasonManager: season {row?.Id} ({row?.Name}) is malformed (EndsAt <= StartsAt, or a blank Name) - skipped.");
            }

            foreach (var (first, second) in FindOverlaps(rows))
            {
                log.Warn($"SpeedSeasonManager: season {first.Id} ({first.Name}, {first.StartsAt:u} - {first.EndsAt:u}) overlaps season {second.Id} ({second.Name}, {second.StartsAt:u} - {second.EndsAt:u}) - the row with the latest StartsAt wins.");
            }
        }

        /// <summary>
        /// The additive merge, factored out as a pure function over an in-memory dictionary
        /// precisely so Reload()'s semantics are testable without a database. Adds rows whose Id is
        /// absent from <paramref name="target"/>, replaces rows whose Id is present, and returns the
        /// ids it saw (well-formed rows only). Rows failing <see cref="IsWellFormed"/> are skipped
        /// and count toward neither added nor updated.
        /// </summary>
        public static (int added, int updated, HashSet<int> seen) MergeSeasons(Dictionary<int, SpeedSeason> target, IEnumerable<SpeedSeason> rows)
        {
            var added = 0;
            var updated = 0;
            var seen = new HashSet<int>();

            if (rows == null)
                return (added, updated, seen);

            foreach (var row in rows)
            {
                if (!IsWellFormed(row))
                    continue;

                seen.Add(row.Id);

                if (target.ContainsKey(row.Id))
                    updated++;
                else
                    added++;

                target[row.Id] = row;
            }

            return (added, updated, seen);
        }

        /// <summary>A season row is well-formed when it is non-null, its window is non-empty (EndsAt &gt; StartsAt), and it carries a name.</summary>
        public static bool IsWellFormed(SpeedSeason season)
        {
            if (season == null)
                return false;

            if (season.EndsAt <= season.StartsAt)
                return false;

            if (string.IsNullOrWhiteSpace(season.Name))
                return false;

            return true;
        }

        /// <summary>
        /// Returns the active season at <paramref name="utcNow"/>, the one whose half-open window
        /// [StartsAt, EndsAt) contains it - StartsAt is inclusive, EndsAt is exclusive, so two
        /// adjacent seasons hand over cleanly with no overlap and no gap at the boundary instant.
        /// Returns null when no row covers the instant (a gap) or <paramref name="candidates"/> is
        /// null or empty. Malformed rows (EndsAt &lt;= StartsAt) are never selectable.
        ///
        /// Overlapping rows are a content error: the manager picks the one with the latest StartsAt,
        /// and on an exact tie the higher Id, so selection is deterministic rather than dependent on
        /// input order.
        /// </summary>
        public static SpeedSeason SelectSeasonAt(IEnumerable<SpeedSeason> candidates, DateTime utcNow)
        {
            if (candidates == null)
                return null;

            SpeedSeason best = null;

            foreach (var candidate in candidates)
            {
                if (!IsWellFormed(candidate))
                    continue;

                if (candidate.StartsAt > utcNow || utcNow >= candidate.EndsAt)
                    continue;

                if (best == null
                    || candidate.StartsAt > best.StartsAt
                    || (candidate.StartsAt == best.StartsAt && candidate.Id > best.Id))
                {
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// The NEXT scheduled season at <paramref name="utcNow"/>: the well-formed row with the
        /// earliest StartsAt strictly greater than that instant, tie-broken by the LOWER Id so
        /// selection is deterministic rather than dependent on input order. Returns null when nothing
        /// is scheduled ahead of the instant, or <paramref name="candidates"/> is null or empty.
        ///
        /// Strictly greater, so the currently-active season is never "next" - StartsAt is inclusive in
        /// SelectSeasonAt's half-open window, and a row that has already started is either the active
        /// one or already over. Malformed rows are never selectable, same filter as SelectSeasonAt.
        ///
        /// Pure and static, mirroring SelectSeasonAt, so it is unit-testable with no database.
        /// </summary>
        public static SpeedSeason SelectNextSeasonAt(IEnumerable<SpeedSeason> candidates, DateTime utcNow)
        {
            if (candidates == null)
                return null;

            SpeedSeason best = null;

            foreach (var candidate in candidates)
            {
                if (!IsWellFormed(candidate))
                    continue;

                if (candidate.StartsAt <= utcNow)
                    continue;

                if (best == null
                    || candidate.StartsAt < best.StartsAt
                    || (candidate.StartsAt == best.StartsAt && candidate.Id < best.Id))
                {
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Every unordered pair of well-formed rows whose half-open [StartsAt, EndsAt) windows
        /// intersect. Used at load time to log a warning naming both rows. Deterministic order: by
        /// First.Id then Second.Id.
        /// </summary>
        public static List<(SpeedSeason First, SpeedSeason Second)> FindOverlaps(IReadOnlyList<SpeedSeason> candidates)
        {
            var overlaps = new List<(SpeedSeason First, SpeedSeason Second)>();

            if (candidates == null)
                return overlaps;

            var wellFormed = candidates.Where(IsWellFormed).OrderBy(c => c.Id).ToList();

            for (var i = 0; i < wellFormed.Count; i++)
            {
                for (var j = i + 1; j < wellFormed.Count; j++)
                {
                    var a = wellFormed[i];
                    var b = wellFormed[j];

                    // half-open windows intersect iff a starts before b ends AND b starts before a ends
                    if (a.StartsAt < b.EndsAt && b.StartsAt < a.EndsAt)
                        overlaps.Add((a, b));
                }
            }

            return overlaps;
        }

        /// <summary>
        /// The count most recent seasons by StartsAt descending, tie-broken by Id descending.
        /// Includes the currently-active season. Returns fewer than count when fewer exist, and an
        /// empty list for count &lt;= 0.
        ///
        /// Factored as a pure static helper over an explicit candidate list (mirroring
        /// SelectSeasonAt) so GetRecentSeasons' ordering is testable without a database - the public
        /// SelectRecentSeasons is the internal seam GetRecentSeasons(int) delegates to.
        /// </summary>
        public static List<SpeedSeason> SelectRecentSeasons(IEnumerable<SpeedSeason> candidates, int count)
        {
            if (candidates == null || count <= 0)
                return new List<SpeedSeason>();

            return candidates
                .OrderByDescending(s => s.StartsAt)
                .ThenByDescending(s => s.Id)
                .Take(count)
                .ToList();
        }

        /// <summary>The active season - StartsAt &lt;= now &lt; EndsAt - or null when there is a gap.</summary>
        /// <remarks>
        /// UTC throughout. starts_at/ends_at are MySQL datetime, which carries no timezone; the
        /// fork's convention is UTC everywhere (DateTime.UtcNow is used across
        /// ACE.Server/Managers/, and ACE.Common.Time.GetUnixTime() is defined as
        /// GetUnixTime(DateTime.UtcNow)). Never DateTime.Now here.
        /// </remarks>
        public static SpeedSeason GetActiveSeason()
        {
            return SelectSeasonAt(seasons.Values, DateTime.UtcNow);
        }

        /// <summary>The next scheduled season - the earliest StartsAt still in the future - or null when none is scheduled.</summary>
        /// <remarks>
        /// UTC throughout, for exactly the reason GetActiveSeason's remarks give: starts_at/ends_at are
        /// MySQL datetime and carry no timezone, and the fork compares against DateTime.UtcNow
        /// everywhere. Never DateTime.Now here.
        /// </remarks>
        public static SpeedSeason GetNextSeason()
        {
            return SelectNextSeasonAt(seasons.Values, DateTime.UtcNow);
        }

        /// <summary>The season with the given id, or null when absent.</summary>
        public static SpeedSeason GetSeason(int id)
        {
            seasons.TryGetValue(id, out var season);
            return season;
        }

        /// <summary>
        /// The <paramref name="count"/> most recent seasons by StartsAt descending, tie-broken by Id
        /// descending, including the currently-active season. Feeds /top speed winners.
        /// </summary>
        public static List<SpeedSeason> GetRecentSeasons(int count)
        {
            return SelectRecentSeasons(seasons.Values, count);
        }
    }
}
