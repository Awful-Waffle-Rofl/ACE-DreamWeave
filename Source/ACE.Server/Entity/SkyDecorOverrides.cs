using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Database.Models.World;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE fork: RUNTIME-ONLY overrides of sky_decor_region values, so the sky can be tuned from
    /// inside the client without a database round trip.
    ///
    /// Nothing here ever writes the database. An override lives in this process only, is dropped by
    /// `/sky-decor reload` and by a restart, and exists so that the owner can fly around adjusting scale
    /// and separation by eye and then paste ONE finished UPDATE (see <see cref="BuildUpdateSql"/>) into
    /// the content unit. That one-way-ness is the whole safety story: a tuning session can never leave a
    /// half-tuned value in the world database, because it cannot reach it.
    ///
    /// THREADING. The cached region list is read from landblock threads during a rebuild, so an override
    /// is never applied by mutating a cached row in place - a half-applied edit would be visible to a
    /// rebuild already in flight. Instead a modified COPY is published under a lock and readers get an
    /// internally consistent object either way. <see cref="Effective(List{SkyDecorRegion})"/> builds a
    /// fresh list per call rather than caching one, because the underlying database cache can be swapped
    /// out from under it at any moment by a reload.
    ///
    /// One asymmetry worth knowing: the database cache only ever loads ENABLED rows, so `enabled` can be
    /// overridden to 0 (which removes the region from the effective list) but a row disabled in the
    /// database cannot be switched on from here - it is not in the cache to name.
    /// </summary>
    public static class SkyDecorOverrides
    {
        /// <summary>
        /// One tunable column: how to read it, how to write it, and how to render it as SQL.
        ///
        /// A single table drives `/sky-decor set`, `/sky-decor show` and `/sky-decor sql` together, which
        /// is the point of it - a column added here is immediately settable, visible and round-trippable,
        /// with no chance of the three lists drifting apart.
        /// </summary>
        private sealed class Tunable
        {
            public string Column;

            /// <summary>the value as `show` prints it and as `set` reports it</summary>
            public Func<SkyDecorRegion, string> Read;

            /// <summary>the value as it appears on the right of an `=` in an UPDATE statement</summary>
            public Func<SkyDecorRegion, string> Sql;

            /// <summary>
            /// Parse and assign; returns an error message, or null on success. The third argument is a
            /// sink for ADVISORY lines - things accepted but worth saying out loud, like a retired colour.
            /// Kept separate from the return value so "this is wrong" and "this is allowed but odd" can
            /// never be confused for one another by a caller.
            /// </summary>
            public Func<SkyDecorRegion, string, List<string>, string> Apply;
        }

        private static string Num(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private static string Bit(bool v) => v ? "b'1'" : "b'0'";

        private static string Quote(string v) => "'" + (v ?? string.Empty).Replace("'", "''") + "'";

        private static string ApplyFloat(string raw, Action<float> assign, float min, float max)
        {
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return $"'{raw}' is not a number";

            if (float.IsNaN(value) || float.IsInfinity(value))
                return $"'{raw}' is not a finite number";

            if (value < min || value > max)
                return $"{Num(value)} is outside the accepted range {Num(min)} to {Num(max)}";

            assign(value);

            return null;
        }

        private static string ApplyInt(string raw, Action<int> assign)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return $"'{raw}' is not a whole number";

            assign(value);

            return null;
        }

        private static string ApplyBool(string raw, Action<bool> assign)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    assign(true);
                    return null;

                case "0":
                case "false":
                case "no":
                case "off":
                    assign(false);
                    return null;

                default:
                    return $"'{raw}' is not a boolean (use 0, 1, true or false)";
            }
        }

        // Invariant culture everywhere, deliberately: a value typed in the client has to parse the same
        // way on every machine, and the SQL this class emits has to be pasteable into a .sql file. A
        // locale that renders 2.5 as "2,5" would break both, and would break them silently.
        private static readonly List<Tunable> tunables = new List<Tunable>
        {
            new Tunable { Column = "enabled",          Read = r => r.Enabled ? "1" : "0",   Sql = r => Bit(r.Enabled),          Apply = (r, v, w) => ApplyBool(v, x => r.Enabled = x) },
            new Tunable { Column = "seed",             Read = r => r.Seed.ToString(CultureInfo.InvariantCulture),    Sql = r => r.Seed.ToString(CultureInfo.InvariantCulture),    Apply = (r, v, w) => ApplyInt(v, x => r.Seed = x) },
            new Tunable { Column = "version",          Read = r => r.Version.ToString(CultureInfo.InvariantCulture), Sql = r => r.Version.ToString(CultureInfo.InvariantCulture), Apply = (r, v, w) => ApplyInt(v, x => r.Version = x) },
            new Tunable { Column = "density",          Read = r => Num(r.Density),          Sql = r => Num(r.Density),          Apply = (r, v, w) => ApplyFloat(v, x => r.Density = x, 0.0f, 64.0f) },
            new Tunable { Column = "palette",          Read = r => SkyDecorColours.Format(r.Palette), Sql = r => Quote(SkyDecorColours.Format(r.Palette)), Apply = ApplyPalette },
            new Tunable { Column = "scale_min",        Read = r => Num(r.ScaleMin),         Sql = r => Num(r.ScaleMin),         Apply = (r, v, w) => ApplyFloat(v, x => r.ScaleMin = x, 0.001f, 100000.0f) },
            new Tunable { Column = "scale_max",        Read = r => Num(r.ScaleMax),         Sql = r => Num(r.ScaleMax),         Apply = (r, v, w) => ApplyFloat(v, x => r.ScaleMax = x, 0.001f, 100000.0f) },
            new Tunable { Column = "height_min",       Read = r => Num(r.HeightMin),        Sql = r => Num(r.HeightMin),        Apply = (r, v, w) => ApplyFloat(v, x => r.HeightMin = x, -1000.0f, 10000.0f) },
            new Tunable { Column = "height_max",       Read = r => Num(r.HeightMax),        Sql = r => Num(r.HeightMax),        Apply = (r, v, w) => ApplyFloat(v, x => r.HeightMax = x, -1000.0f, 10000.0f) },
            new Tunable { Column = "tilt_max_deg",     Read = r => Num(r.TiltMaxDeg),       Sql = r => Num(r.TiltMaxDeg),       Apply = (r, v, w) => ApplyFloat(v, x => r.TiltMaxDeg = x, 0.0f, 180.0f) },
            new Tunable { Column = "spin_min",         Read = r => Num(r.SpinMin),          Sql = r => Num(r.SpinMin),          Apply = (r, v, w) => ApplyFloat(v, x => r.SpinMin = x, 0.0f, 100.0f) },
            new Tunable { Column = "spin_max",         Read = r => Num(r.SpinMax),          Sql = r => Num(r.SpinMax),          Apply = (r, v, w) => ApplyFloat(v, x => r.SpinMax = x, 0.0f, 100.0f) },
            new Tunable { Column = "part_offset",      Read = r => Num(r.PartOffset),       Sql = r => Num(r.PartOffset),       Apply = (r, v, w) => ApplyFloat(v, x => r.PartOffset = x, 0.0f, 100.0f) },
            new Tunable { Column = "over_water",       Read = r => r.OverWater ? "1" : "0", Sql = r => Bit(r.OverWater),        Apply = (r, v, w) => ApplyBool(v, x => r.OverWater = x) },
            new Tunable { Column = "pair_chance",      Read = r => Num(r.PairChance),       Sql = r => Num(r.PairChance),       Apply = (r, v, w) => ApplyFloat(v, x => r.PairChance = x, 0.0f, 1.0f) },
            new Tunable { Column = "pair_scale_ratio", Read = r => Num(r.PairScaleRatio),   Sql = r => Num(r.PairScaleRatio),   Apply = (r, v, w) => ApplyFloat(v, x => r.PairScaleRatio = x, 0.0f, 100.0f) },
            new Tunable { Column = "pair_gap",         Read = r => Num(r.PairGap),          Sql = r => Num(r.PairGap),          Apply = (r, v, w) => ApplyFloat(v, x => r.PairGap = x, -1000.0f, 1000.0f) },
            new Tunable { Column = "pair_speed_ratio", Read = r => Num(r.PairSpeedRatio),   Sql = r => Num(r.PairSpeedRatio),   Apply = (r, v, w) => ApplyFloat(v, x => r.PairSpeedRatio = x, 0.0f, 100.0f) },
            new Tunable { Column = "min_separation",   Read = r => Num(r.MinSeparation),    Sql = r => Num(r.MinSeparation),    Apply = (r, v, w) => ApplyFloat(v, x => r.MinSeparation = x, 0.0f, 100.0f) },
            new Tunable { Column = "translucency_min", Read = r => Num(r.TranslucencyMin),  Sql = r => Num(r.TranslucencyMin),  Apply = (r, v, w) => ApplyFloat(v, x => r.TranslucencyMin = x, 0.0f, 1.0f) },
            new Tunable { Column = "translucency_max", Read = r => Num(r.TranslucencyMax),  Sql = r => Num(r.TranslucencyMax),  Apply = (r, v, w) => ApplyFloat(v, x => r.TranslucencyMax = x, 0.0f, 1.0f) },
            new Tunable { Column = "rig",              Read = r => r.Rig ?? SkyDecorLayout.RigInverted, Sql = r => Quote(r.Rig ?? SkyDecorLayout.RigInverted), Apply = ApplyRig },
        };

        private static string ApplyPalette(SkyDecorRegion region, string raw, List<string> warnings)
        {
            // Parsed rather than pattern-matched, and by the SAME parser the spawner uses. A palette that
            // ParsePalette rejects would leave the region silently cloudless (Plan returns nothing on an
            // empty palette), which reads in game as "the command did nothing".
            var problems = new List<string>();

            var parsed = SkyDecorLayout.ParsePalette(raw, problems);

            // A DROPPED token is the failure, and it is detected by COUNTING rather than by reading the
            // problem text: the parser drops a token for several different reasons and matching on the
            // wording of any of them would silently stop working the day one is reworded. Every non-empty
            // token has to have produced an entry.
            var tokens = (raw ?? string.Empty).Split(',').Count(t => t.Trim().Length > 0);

            if (parsed.Count != tokens)
                return string.Join("; ", problems);

            if (parsed.Count == 0)
                return $"palette must name at least one colour or wcid. Names: {SkyDecorColours.NameList}";

            if (parsed.Sum(e => e.Weight) <= 0)
                return "palette has no entry with a positive weight, so nothing could ever be picked";

            // Stored in the canonical NAME form, so the column, /sky-decor show and /sky-decor sql all
            // read the same way whether the owner typed names, wcids or a mix of the two.
            var canonical = SkyDecorColours.Format(raw);

            if (canonical.Length > 255)
                return "palette is longer than the column's 255 characters";

            // Nothing was rejected, so anything left in `problems` is advisory - a retired colour.
            if (warnings != null)
                warnings.AddRange(problems);

            region.Palette = canonical;

            return null;
        }

        private static string ApplyRig(SkyDecorRegion region, string raw, List<string> warnings)
        {
            var value = (raw ?? string.Empty).Trim().ToLowerInvariant();

            if (value != SkyDecorLayout.RigInverted && value != SkyDecorLayout.RigMast)
                return $"rig must be '{SkyDecorLayout.RigInverted}' or '{SkyDecorLayout.RigMast}'";

            region.Rig = value;

            return null;
        }

        /// <summary>Every settable column, in the order `show` and `sql` print them.</summary>
        public static IReadOnlyList<string> Columns => tunables.Select(t => t.Column).ToList();

        public static bool IsColumn(string column)
        {
            return tunables.Any(t => string.Equals(t.Column, column, StringComparison.OrdinalIgnoreCase));
        }

        private static Tunable Find(string column)
        {
            return tunables.FirstOrDefault(t => string.Equals(t.Column, column, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The value of one column as `show` prints it. Null for an unknown column.</summary>
        public static string Read(SkyDecorRegion region, string column)
        {
            return Find(column)?.Read(region);
        }

        // -------------------------------------------------------------------------------------------
        // The override store
        // -------------------------------------------------------------------------------------------

        private static readonly object gate = new object();

        /// <summary>region name -> the modified COPY that replaces the cached row</summary>
        private static readonly Dictionary<string, SkyDecorRegion> overridden = new Dictionary<string, SkyDecorRegion>(StringComparer.OrdinalIgnoreCase);

        /// <summary>region name -> which columns were set, so `show` can mark them</summary>
        private static readonly Dictionary<string, HashSet<string>> marks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Read WITHOUT the lock on the hot path. Landblock activation asks this on every outdoor block,
        /// and the overwhelmingly common answer is "no overrides", which then costs one volatile read
        /// instead of a lock acquisition.
        /// </summary>
        private static volatile bool anyActive;

        public static bool AnyActive => anyActive;

        /// <summary>
        /// Applies one column to a COPY of <paramref name="baseline"/> and validates the result. Pure -
        /// it touches no stored state - so the whole validation matrix is testable without a world.
        /// </summary>
        public static bool TryApply(SkyDecorRegion baseline, string column, string value, out SkyDecorRegion updated, out string error)
        {
            return TryApply(baseline, column, value, out updated, out error, out _);
        }

        /// <summary>
        /// <see cref="TryApply(SkyDecorRegion, string, string, out SkyDecorRegion, out string)"/>, also
        /// returning any ADVISORY lines the edit produced. An advisory is not a failure: a palette naming
        /// the retired white disc is accepted and applied, and the warning exists so the owner is told
        /// why it will look wrong rather than discovering it in the sky.
        /// </summary>
        public static bool TryApply(SkyDecorRegion baseline, string column, string value, out SkyDecorRegion updated, out string error, out List<string> warnings)
        {
            updated = null;
            error = null;
            warnings = new List<string>();

            var tunable = Find(column);

            if (tunable == null)
            {
                error = $"unknown column '{column}'. Settable: {string.Join(", ", Columns)}";
                return false;
            }

            var copy = baseline.Clone();

            error = tunable.Apply(copy, value, warnings);

            if (error != null)
            {
                warnings.Clear();
                return false;
            }

            // Cross-column validation runs AFTER the assignment and on the copy, so a rejected edit leaves
            // nothing behind. Checked here rather than in the layout math because the layout deliberately
            // tolerates a swapped pair (it sorts them), and silently sorting an obvious typo is worse
            // feedback than refusing it.
            error = Validate(copy);

            if (error != null)
            {
                warnings.Clear();
                return false;
            }

            WarnIfMastCouplesHeight(copy, tunable.Column, warnings);

            updated = copy;

            return true;
        }

        /// <summary>
        /// Under 'mast' the disc stands partOffset * scale above an origin held
        /// <see cref="SkyDecorLayout.MastOriginClearance"/> above the terrain, so its height is
        /// max(rolled height, partOffset * scale + clearance) and SCALE - not the height columns -
        /// decides where the sky sits the moment that size term wins. Nothing about that is visible from
        /// the command's own reply, and a live tuning round was spent discovering it by eye ("setting the
        /// scale also increases the elevation", 2026-08-20), so any edit that moves either dial while the
        /// coupling is live says so out loud.
        ///
        /// Advisory, never a refusal: 'mast' is still the only pose whose spin reads from underneath, so
        /// a region may want the coupling. The warning states the band the current numbers produce rather
        /// than the rule in the abstract, because the rule is only interesting when it binds.
        /// </summary>
        private static void WarnIfMastCouplesHeight(SkyDecorRegion region, string column, List<string> warnings)
        {
            if (warnings == null || !SkyDecorLayout.IsMast(region.Rig))
                return;

            switch (column)
            {
                case "scale_min":
                case "scale_max":
                case "height_min":
                case "height_max":
                case "part_offset":
                case "rig":
                    break;

                default:
                    return;
            }

            // Widest lift, i.e. at tilt 0, where cos(tilt) is 1. A tilted disc lifts slightly less.
            var low = region.PartOffset * Math.Min(region.ScaleMin, region.ScaleMax) + SkyDecorLayout.MastOriginClearance;
            var high = region.PartOffset * Math.Max(region.ScaleMin, region.ScaleMax) + SkyDecorLayout.MastOriginClearance;

            // Every rolled height already clears the size term, so the height columns genuinely decide
            // and there is nothing surprising to report.
            if (high <= Math.Min(region.HeightMin, region.HeightMax))
                return;

            // One decimal on the derived band, not the full round-trip Num: these are read off the screen
            // while flying, and "44.680002-137.500004" reads as noise where "44.7-137.5" reads as a height.
            string Band(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

            warnings.Add($"rig 'mast' ties HEIGHT to SCALE: a disc sits at max(rolled height, {Num(region.PartOffset)} * scale + {Num(SkyDecorLayout.MastOriginClearance)}), so scale {Num(region.ScaleMin)}-{Num(region.ScaleMax)} puts the sky at {Band(low)}-{Band(high)} m and height_min/height_max are inert below that. `/sky-decor set rig inverted` makes size and height independent dials.");
        }

        private static string Validate(SkyDecorRegion region)
        {
            if (region.ScaleMin > region.ScaleMax)
                return $"scale_min ({Num(region.ScaleMin)}) is above scale_max ({Num(region.ScaleMax)})";

            if (region.HeightMin > region.HeightMax)
                return $"height_min ({Num(region.HeightMin)}) is above height_max ({Num(region.HeightMax)})";

            if (region.SpinMin > region.SpinMax)
                return $"spin_min ({Num(region.SpinMin)}) is above spin_max ({Num(region.SpinMax)})";

            if (region.TranslucencyMin > region.TranslucencyMax)
                return $"translucency_min ({Num(region.TranslucencyMin)}) is above translucency_max ({Num(region.TranslucencyMax)})";

            return null;
        }

        /// <summary>
        /// Publishes an already-validated copy as the effective region and marks the column as overridden.
        /// </summary>
        public static void Store(SkyDecorRegion updated, string column)
        {
            var tunable = Find(column);

            lock (gate)
            {
                overridden[updated.Name] = updated;

                if (!marks.TryGetValue(updated.Name, out var set))
                    marks[updated.Name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                set.Add(tunable != null ? tunable.Column : column);

                anyActive = true;
            }
        }

        /// <summary>Drops every override. Returns how many regions had one.</summary>
        public static int ClearAll()
        {
            lock (gate)
            {
                var count = overridden.Count;

                overridden.Clear();
                marks.Clear();

                anyActive = false;

                return count;
            }
        }

        public static bool IsOverridden(string regionName, string column)
        {
            if (!anyActive)
                return false;

            lock (gate)
                return marks.TryGetValue(regionName, out var set) && set.Contains(column);
        }

        /// <summary>Which columns are overridden on one region, in `show` order. Empty when none are.</summary>
        public static List<string> OverriddenColumns(string regionName)
        {
            if (!anyActive)
                return new List<string>();

            lock (gate)
            {
                if (!marks.TryGetValue(regionName, out var set))
                    return new List<string>();

                return tunables.Where(t => set.Contains(t.Column)).Select(t => t.Column).ToList();
            }
        }

        /// <summary>
        /// The region list the spawner should actually use: the given database rows, with any overridden
        /// region replaced by its override copy, and any region overridden to enabled = 0 removed.
        /// </summary>
        public static List<SkyDecorRegion> Effective(List<SkyDecorRegion> fromDatabase)
        {
            if (fromDatabase == null)
                return new List<SkyDecorRegion>();

            if (!anyActive)
                return fromDatabase;

            lock (gate)
            {
                var result = new List<SkyDecorRegion>(fromDatabase.Count);

                foreach (var region in fromDatabase)
                {
                    var effective = overridden.TryGetValue(region.Name, out var over) ? over : region;

                    // The database cache loads enabled rows only, so this is the one direction the
                    // override can travel: a region can be switched off at runtime, never on.
                    if (effective.Enabled)
                        result.Add(effective);
                }

                return result;
            }
        }

        /// <summary>
        /// The effective region list for the running server. This, and never
        /// DatabaseManager.World.GetCachedSkyDecorRegions() directly, is what the spawner reads - going
        /// straight to the cache would quietly ignore every override.
        /// </summary>
        public static List<SkyDecorRegion> EffectiveRegions()
        {
            return Effective(DatabaseManager.World.GetCachedSkyDecorRegions());
        }

        /// <summary>The database row behind a name, ignoring any override. Null when there is none.</summary>
        public static SkyDecorRegion DatabaseRegion(string regionName)
        {
            return DatabaseManager.World.GetCachedSkyDecorRegions()
                .FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// A ready-to-paste UPDATE carrying every tunable at its CURRENT effective value. The point is
        /// that a tuning session ends by copying one statement into the content unit, so this deliberately
        /// emits all of them rather than only the overridden ones - the unit's row is then completely
        /// described by what is on screen, with nothing left to remember.
        /// </summary>
        public static string BuildUpdateSql(SkyDecorRegion region)
        {
            var sb = new StringBuilder();

            sb.Append("UPDATE sky_decor_region SET ");
            sb.Append(string.Join(", ", tunables.Select(t => $"{t.Column}={t.Sql(region)}")));
            sb.Append($" WHERE name={Quote(region.Name)};");

            return sb.ToString();
        }
    }
}
