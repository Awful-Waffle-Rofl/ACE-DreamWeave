using System;
using System.Collections.Generic;
using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The wind-up motion a monster plays when it casts, for motion tables that have no CastSpell animation.
    ///
    /// PreCastMotion broadcasts MotionCommand.CastSpell. A table with no CastSpell link for the creature's stance
    /// plays nothing and reports a length of 0, so the caster just stands there. The lugian table 0x09000006 is the
    /// known case: it has no Magic stance and no CastSpell in HandCombat or NonCombat. Rather than swap the body,
    /// this maps such a table to a gesture it does have, played as the cast wind-up.
    ///
    /// The map is narrow on purpose: a table is only consulted when its CastSpell length is 0, and only tables
    /// listed here are affected. Every other creature keeps CastSpell exactly as before.
    /// </summary>
    public static class MonsterCastGesture
    {
        /// <summary>Motion table id -> wind-up gesture used when that table has no CastSpell animation.</summary>
        public static readonly IReadOnlyDictionary<uint, MotionCommand> Fallbacks = new Dictionary<uint, MotionCommand>
        {
            // Lugian: Twitch1 (0x10000051) is linked from Ready in HandCombat and NonCombat, 2.27 s unscaled
            { 0x09000006, MotionCommand.Twitch1 },
        };

        /// <summary>
        /// CastSpell when the table has that animation (length &gt; 0) or has no fallback entry; otherwise the table's fallback gesture.
        /// </summary>
        public static MotionCommand Select(uint motionTableId, float castSpellLength)
        {
            if (castSpellLength > 0.0f)
                return MotionCommand.CastSpell;

            return Fallbacks.TryGetValue(motionTableId, out var fallback) ? fallback : MotionCommand.CastSpell;
        }

        /// <summary>
        /// The wind-up length PreCastMotion returns: the CastSpell length when CastSpell is what plays, otherwise the
        /// length of the fallback gesture (lengthOf is the table lookup for that motion).
        /// </summary>
        public static float PreCastLength(MotionCommand castMotion, float castSpellLength, Func<MotionCommand, float> lengthOf)
        {
            if (castMotion == MotionCommand.CastSpell)
                return castSpellLength;

            return lengthOf(castMotion);
        }
    }
}
