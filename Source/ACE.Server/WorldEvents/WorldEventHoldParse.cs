using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

// The pure goals.json checks the Hold definitions need at load (D6 - no clock, no world, no settings). These are the
// parse-time members only, split out as partial classes so the runtime halves of the same classes can be ported in later
// slices without touching this file.
namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        /// <summary>
        /// The DreamWeave player-text rule: replaces every em dash (U+2014) and en dash (U+2013) with a plain
        /// hyphen. Returns true when anything changed, so the loader can report it.
        /// </summary>
        public static bool RepairDashes(string text, out string repaired)
        {
            repaired = text;

            if (string.IsNullOrEmpty(text))
                return false;

            repaired = text.Replace((char)0x2014, '-').Replace((char)0x2013, '-');

            return repaired != text;
        }

        /// <summary>Parses a ward's damage kinds (melee, missile, magic; case-insensitive). False for anything else or an empty list.</summary>
        public static bool TryParseCombatKinds(IEnumerable<string> names, out WorldEventCombatKinds kinds)
        {
            kinds = WorldEventCombatKinds.None;

            if (names == null)
                return false;

            foreach (var name in names)
            {
                switch (name?.Trim().ToLowerInvariant())
                {
                    case "melee": kinds |= WorldEventCombatKinds.Melee; break;
                    case "missile": kinds |= WorldEventCombatKinds.Missile; break;
                    case "magic": kinds |= WorldEventCombatKinds.Magic; break;
                    default: return false;
                }
            }

            return kinds != WorldEventCombatKinds.None;
        }

        /// <summary>protect.finaleBossPrefix: blank, or letters and spaces up to 20 characters (D6 - pure).</summary>
        public static bool IsValidBossPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return true;

            return prefix.Length <= 20 && prefix.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == ' ');
        }

        /// <summary>
        /// protect.bossEffect (D6 - pure): a PlayScript name (case-insensitive) or "none" (and null or blank,
        /// which read as none). False for anything else.
        /// </summary>
        public static bool TryParseBossEffect(string name, out ACE.Entity.Enum.PlayScript script)
        {
            script = ACE.Entity.Enum.PlayScript.Invalid;

            if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                return true;

            var trimmed = name.Trim();

            if (!trimmed.All(char.IsLetterOrDigit) || !Enum.TryParse(trimmed, true, out script))
                return false;

            return Enum.IsDefined(typeof(ACE.Entity.Enum.PlayScript), script) && script != ACE.Entity.Enum.PlayScript.Invalid;
        }

        /// <summary>Parses a special wave type name (case-insensitive). False for anything unknown or "normal".</summary>
        public static bool TryParseWaveType(string name, out HoldWaveType type)
        {
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().All(char.IsLetter) && Enum.TryParse(name.Trim(), true, out type) && type != HoldWaveType.Normal && type != HoldWaveType.Mixed
                && Enum.IsDefined(typeof(HoldWaveType), type))
                return true;

            type = HoldWaveType.Normal;
            return false;
        }
    }

    public static partial class WorldEventHoldDefense
    {
        /// <summary>The goals.json check (D6 - pure). Null when valid.</summary>
        public static string Validate(DefendPoseDef pose)
        {
            if (pose == null)
                return null;

            if (!double.IsFinite(pose.Reach) || pose.Reach < 0.5 || pose.Reach > 6)
                return "has an invalid protect.defendPose.reach (0.5 to 6)";

            if (!double.IsFinite(pose.SwingSeconds) || !double.IsFinite(pose.SwingMaxSeconds) || pose.SwingSeconds < 1
                || pose.SwingMaxSeconds < pose.SwingSeconds || pose.SwingMaxSeconds > 10)
                return "has an invalid protect.defendPose swing rhythm (swingSeconds 1 to 10, swingMaxSeconds not below it and up to 10)";

            if (!double.IsFinite(pose.TurnBackSeconds) || pose.TurnBackSeconds < 0 || pose.TurnBackSeconds > 30)
                return "has an invalid protect.defendPose.turnBackSeconds (0 to 30)";

            if (!double.IsFinite(pose.FaceDegrees) || pose.FaceDegrees < 5 || pose.FaceDegrees > 90)
                return "has an invalid protect.defendPose.faceDegrees (5 to 90)";

            return null;
        }
    }

    public static partial class WorldEventTint
    {
        /// <summary>The client palette range (DAT file type 0x04).</summary>
        public const uint PaletteMin = 0x04000000, PaletteMax = 0x0400FFFF;

        /// <summary>
        /// goals.json's tintPalette (D6 - pure): "0x04001381", "67113857", or "none" / "" / "0" for no tint (0). False for
        /// anything that is not a palette id.
        /// </summary>
        public static bool TryParsePalette(string text, out uint palette)
        {
            palette = 0;

            if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || text.Trim() == "0")
                return true;

            var t = text.Trim();
            uint value;

            var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);

            if (!ok || value < PaletteMin || value > PaletteMax)
                return false;

            palette = value;
            return true;
        }
    }
}