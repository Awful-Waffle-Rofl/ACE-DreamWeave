using System;
using System.Globalization;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Thread-Guide tag a guide fragment carries in its DungeonGemSpec, as the optional
    /// guide=&lt;rung&gt;:&lt;serial&gt;:&lt;ownerGuid&gt; field. Pure value type, no engine references.
    ///
    ///   Rung      - the ladder level this item was issued for; one of ThreadGuideLadder's rungs (50..375 in 25s).
    ///   Serial    - the issue counter for this player, >= 1. An item whose serial is not the player's current
    ///               one is a superseded copy (ThreadGuideRules.IsCurrent).
    ///   OwnerGuid - the player the item was issued to. Non-zero, written as a plain decimal uint exactly like
    ///               the spec's owner= field, because a guide item is always issued to a specific player.
    ///
    /// VALID BY CONSTRUCTION: the constructor and TryParse share one rule (<see cref="IsLegal"/>), so
    /// DungeonGemSpec.Serialize can never write a guide= that TryParse would refuse.
    ///
    /// Detection belongs to ThreadGuideRules, not to callers reading this type's fields.
    /// </summary>
    public sealed class GuideTag : IEquatable<GuideTag>
    {
        public int Rung { get; }
        public int Serial { get; }
        public uint OwnerGuid { get; }

        /// <summary>Throws ArgumentException when the values break the rule in <see cref="IsLegal"/>.</summary>
        public GuideTag(int rung, int serial, uint ownerGuid)
        {
            if (!IsLegal(rung, serial, ownerGuid, out var error))
                throw new ArgumentException(error);

            Rung = rung;
            Serial = serial;
            OwnerGuid = ownerGuid;
        }

        /// <summary>
        /// The one guide rule: the rung is a ladder rung (ThreadGuideLadder.IsRungLevel), the serial is at
        /// least 1, and the owner is non-zero.
        /// </summary>
        public static bool IsLegal(int rung, int serial, uint ownerGuid, out string error)
        {
            error = null;

            if (!ThreadGuideLadder.IsRungLevel(rung))
            {
                error = $"guide rung '{rung.ToString(CultureInfo.InvariantCulture)}' must be {ThreadGuideLadder.FirstRung}..{ThreadGuideLadder.LastRung} in steps of {ThreadGuideLadder.RungStep}";
                return false;
            }

            if (serial < 1) { error = $"guide serial '{serial.ToString(CultureInfo.InvariantCulture)}' must be >= 1"; return false; }
            if (ownerGuid == 0) { error = "guide owner must be a non-zero uint"; return false; }

            return true;
        }

        /// <summary>The field VALUE, without the "guide=" key: "rung:serial:owner", invariant decimal.</summary>
        public string Format()
            => Rung.ToString(CultureInfo.InvariantCulture) + ":" + Serial.ToString(CultureInfo.InvariantCulture) + ":" + OwnerGuid.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses the field value. Strict: exactly three colon-separated parts, each plain decimal digits
        /// (NumberStyles.None, so no sign, whitespace or thousands separator), then <see cref="IsLegal"/>.
        /// </summary>
        public static bool TryParse(string text, out GuideTag tag, out string error)
        {
            tag = null;
            error = null;

            var parts = (text ?? string.Empty).Split(':');
            if (parts.Length != 3) { error = $"guide '{text}' must be rung:serial:owner"; return false; }

            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rung))
            { error = $"guide rung '{parts[0]}' is not an integer"; return false; }

            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var serial))
            { error = $"guide serial '{parts[1]}' is not an integer"; return false; }

            if (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var owner))
            { error = $"guide owner '{parts[2]}' is not a uint"; return false; }

            if (!IsLegal(rung, serial, owner, out error)) return false;

            tag = new GuideTag(rung, serial, owner);
            return true;
        }

        public bool Equals(GuideTag other)
            => other != null && Rung == other.Rung && Serial == other.Serial && OwnerGuid == other.OwnerGuid;

        public override bool Equals(object obj) => Equals(obj as GuideTag);

        public override int GetHashCode() => HashCode.Combine(Rung, Serial, OwnerGuid);

        public override string ToString() => Format();
    }
}
