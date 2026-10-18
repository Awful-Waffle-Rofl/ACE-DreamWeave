using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Broad themes for the World Events families (D6 - pure), so a run whose family has no ranged member can borrow
    /// archers and casters from one that fits beside it (WorldEventRangedMembers.CompatiblePool): rats and grievvers
    /// borrow from wasps and knathteads, never from Virindi. A family may sit in more than one theme. A family not
    /// listed here has no theme and never borrows or lends.
    /// </summary>
    public static class WorldEventFamilyThemes
    {
        public const string Wild = "wild";
        public const string Deep = "deep";
        public const string Undead = "undead";
        public const string Arcane = "arcane";
        public const string Humanoid = "humanoid";

        private static readonly Dictionary<string, string[]> themes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Beasts, vermin, insects and reptiles.
            ["rat"] = new[] { Wild },
            ["grievver"] = new[] { Wild },
            ["armoredillo"] = new[] { Wild },
            ["mite"] = new[] { Wild },
            ["phyntos_wasp"] = new[] { Wild },
            ["rabbit"] = new[] { Wild },
            ["chicken"] = new[] { Wild },
            ["mattekar"] = new[] { Wild },
            ["monouga"] = new[] { Wild },
            ["ursuin"] = new[] { Wild },
            ["carenzi"] = new[] { Wild },
            ["gromnie"] = new[] { Wild },
            ["knathtead"] = new[] { Wild },
            ["chittick"] = new[] { Wild },
            ["shreth"] = new[] { Wild },
            ["thrungus"] = new[] { Wild },
            ["margul"] = new[] { Wild },
            ["olthoi"] = new[] { Wild },
            ["olthoi_larvae"] = new[] { Wild },
            ["slithis"] = new[] { Wild, Deep },
            ["reedshark"] = new[] { Wild, Deep },

            // Sea and shore.
            ["sleech"] = new[] { Deep },
            ["remoran"] = new[] { Deep },
            ["siraluun"] = new[] { Deep },
            ["moarsman"] = new[] { Deep },
            ["blighted_moarsman"] = new[] { Deep },
            ["shallows_shark"] = new[] { Deep },

            // The dead and the haunting.
            ["skeleton"] = new[] { Undead },
            ["undead"] = new[] { Undead },
            ["ghost"] = new[] { Undead },
            ["shadow"] = new[] { Undead },
            ["hollow_minion"] = new[] { Undead },
            ["doll"] = new[] { Undead },
            ["scarecrow"] = new[] { Undead },

            // Elementals, spirits, constructs and the Virindi.
            ["acid_elemental"] = new[] { Arcane },
            ["elemental"] = new[] { Arcane },
            ["fire_elemental"] = new[] { Arcane },
            ["frost_elemental"] = new[] { Arcane },
            ["lightning_elemental"] = new[] { Arcane },
            ["golem"] = new[] { Arcane },
            ["wisp"] = new[] { Arcane },
            ["zefir"] = new[] { Arcane },
            ["virindi"] = new[] { Arcane },
            ["snowman"] = new[] { Arcane },
            ["anekshay"] = new[] { Arcane },
            ["fiun"] = new[] { Arcane },
            ["simulacrum"] = new[] { Arcane, Humanoid },
            ["gear_knight"] = new[] { Arcane, Humanoid },

            // Tribes, warbands and soldiers.
            ["drudge"] = new[] { Humanoid },
            ["mosswart"] = new[] { Humanoid },
            ["tumerok"] = new[] { Humanoid },
            ["aun_tumerok"] = new[] { Humanoid },
            ["hea_tumerok"] = new[] { Humanoid },
            ["banderling"] = new[] { Humanoid },
            ["burun"] = new[] { Humanoid },
            ["lugian"] = new[] { Humanoid },
            ["gotrok_lugian"] = new[] { Humanoid },
            ["tusker"] = new[] { Humanoid },
            ["ruschk"] = new[] { Humanoid },
            ["mukkir"] = new[] { Humanoid },
            ["sclavus"] = new[] { Humanoid },
            ["human"] = new[] { Humanoid },
            ["viamontian_knight"] = new[] { Humanoid },
        };

        /// <summary>The themes <paramref name="familyId"/> belongs to (empty when it has none).</summary>
        public static IReadOnlyList<string> ThemesOf(string familyId)
        {
            if (string.IsNullOrWhiteSpace(familyId) || !themes.TryGetValue(familyId.Trim(), out var list))
                return Array.Empty<string>();

            return list;
        }

        /// <summary>True when two different families share a theme.</summary>
        public static bool Compatible(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            var other = ThemesOf(b);

            return ThemesOf(a).Any(t => other.Contains(t));
        }

        /// <summary>Every family the table knows (tests check each one exists in the content).</summary>
        public static IReadOnlyCollection<string> KnownFamilies => themes.Keys;
    }
}
