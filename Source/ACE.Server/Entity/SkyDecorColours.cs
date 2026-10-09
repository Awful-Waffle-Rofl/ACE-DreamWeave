using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Database.Models.World;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE fork: the ONE place that maps a Sky Rift colour NAME to its wcid.
    ///
    /// A sky_decor_region palette can be written either way - `1002642,1002647` or `purple,red` - and
    /// the names exist because that column is the one thing in the table a person actually composes by
    /// eye. Nobody remembers which of eight consecutive wcids is orange.
    ///
    /// Every entry below was read off the weenie FILE NAMES in Content/sql/weenies
    /// (`10026NN Sky Rift (Colour).sql`) rather than from memory, because a wrong id here would not fail
    /// anywhere - it would quietly put the wrong colour in the sky and look like a rendering problem.
    ///
    /// The map lives here and nowhere else. Parsing (SkyDecorLayout.ParsePalette), rendering
    /// (<see cref="Format"/>, used by /sky-decor show and /sky-decor sql) and the /sky-decor colours
    /// listing all read this table, so a colour added to it is immediately usable in all three.
    /// </summary>
    public static class SkyDecorColours
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public sealed class Colour
        {
            public readonly string Name;
            public readonly uint WeenieClassId;

            /// <summary>Alternative spelling accepted on input; never used on output. Null when there is none.</summary>
            public readonly string Alias;

            /// <summary>Still parses and still spawns, but should not be chosen for new work.</summary>
            public readonly bool Retired;

            public readonly string Note;

            public Colour(string name, uint weenieClassId, string alias = null, bool retired = false, string note = null)
            {
                Name = name;
                WeenieClassId = weenieClassId;
                Alias = alias;
                Retired = retired;
                Note = note;
            }
        }

        /// <summary>
        /// The eight Sky Rift colours, in wcid order. Verified 2026-08-20 against the file names in
        /// Content/sql/weenies: 1002642 Purple, 1002643 Blue, 1002644 Yellow, 1002645 Green,
        /// 1002646 Orange, 1002647 Red, 1002648 White, 1002649 Grey. Plus the two Sky Veil colours,
        /// verified 2026-09-17 the same way: 1005750 Violet, 1005751 Shadow. Sky Veil is the
        /// experimental alpha-blended (non-additive) alternative to the additive Sky Rift swirl - same
        /// weenie shape, different mesh - so it TINTS a bright sky instead of washing it toward white.
        /// </summary>
        public static readonly IReadOnlyList<Colour> All = new List<Colour>
        {
            new Colour("purple", 1002642),
            new Colour("blue",   1002643),
            new Colour("yellow", 1002644),
            new Colour("green",  1002645),
            new Colour("orange", 1002646),
            new Colour("red",    1002647),
            new Colour("white",  1002648, retired: true,
                note: "retired from lineups: its sparse quad renders pixelated at sky scale"),
            new Colour("grey",   1002649, alias: "gray"),
            new Colour("violet", 1005750,
                note: "experimental Sky Veil: alpha-blended (Translucent) quad, not additive - tints instead of washing to white"),
            new Colour("shadow", 1005751,
                note: "experimental Sky Veil: alpha-blended (Alpha) quad, not additive - tints instead of washing to white"),
        };

        /// <summary>Every accepted spelling, aliases included, for an error message that lists the options.</summary>
        public static string NameList
        {
            get
            {
                return string.Join(", ", All.Select(c => c.Alias == null ? c.Name : $"{c.Name} (or {c.Alias})"));
            }
        }

        public static Colour ByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var token = name.Trim();

            return All.FirstOrDefault(c =>
                string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase)
                || (c.Alias != null && string.Equals(c.Alias, token, StringComparison.OrdinalIgnoreCase)));
        }

        public static Colour ByWcid(uint wcid)
        {
            return All.FirstOrDefault(c => c.WeenieClassId == wcid);
        }

        /// <summary>
        /// Renders a palette back out in its canonical form: colour names where the wcid is a known
        /// colour, the raw wcid otherwise, and `:weight` only where the weight is not the default 1.
        ///
        /// Deliberately rebuilt from the PARSED entries rather than tidied up as text, which is what makes
        /// the /sky-decor sql output round-trip: whatever comes back out of here parses to exactly the
        /// entry list it was rendered from. The cost is that an unparseable token is dropped rather than
        /// echoed, which is correct for a statement meant to be pasted into a content file.
        /// </summary>
        public static string Format(string csv)
        {
            var entries = SkyDecorLayout.ParsePalette(csv);

            if (entries.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            foreach (var entry in entries)
            {
                if (sb.Length > 0)
                    sb.Append(',');

                var colour = ByWcid(entry.WeenieClassId);

                sb.Append(colour != null ? colour.Name : entry.WeenieClassId.ToString(CultureInfo.InvariantCulture));

                if (entry.Weight != 1)
                    sb.Append(':').Append(entry.Weight.ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------
        // Warn-once logging for palettes loaded from the database
        // -------------------------------------------------------------------------------------------

        private static readonly object warnedGate = new object();

        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Logs anything wrong with a region's palette exactly ONCE per distinct palette string.
        ///
        /// The dedup is the whole reason this exists. Palettes are parsed at every landblock activation,
        /// so a typo in one row would otherwise print a warning per block per region for the life of the
        /// server - which buries the message it is trying to deliver. Keying on the region name plus the
        /// palette text means an edited palette warns again, and a fixed one goes quiet.
        ///
        /// This is the database load path, so a bad token is SKIPPED and logged rather than refused;
        /// refusing would leave the region with no palette and therefore no clouds at all. The /sky-decor
        /// set path is the strict one - see SkyDecorOverrides.
        /// </summary>
        public static void WarnAboutPalette(SkyDecorRegion region)
        {
            if (region == null)
                return;

            var key = region.Name + "\0" + (region.Palette ?? string.Empty);

            lock (warnedGate)
            {
                if (!warned.Add(key))
                    return;
            }

            var problems = new List<string>();

            var entries = SkyDecorLayout.ParsePalette(region.Palette, problems);

            foreach (var problem in problems)
                log.Warn($"[SKYDECOR] region '{region.Name}': {problem}");

            if (entries.Count == 0)
                log.Warn($"[SKYDECOR] region '{region.Name}': palette '{region.Palette}' has no usable entries, so this region will spawn nothing. Accepted names: {NameList}");
        }

        /// <summary>Test seam: forget which palettes have already been warned about.</summary>
        public static void ResetWarnings()
        {
            lock (warnedGate)
                warned.Clear();
        }
    }
}
