using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// World Events spawn-time npcs (WaffleACE, WP-15 follow-up): let an npc placed by
    /// WorldEventSpawner.PlaceNpcs stand in, and idle in, the stance its own HeartBeat emote set names.
    ///
    /// Why this exists. Stock ACE only ever plays a styled idle motion in the NonCombat stance:
    /// Creature.cs starts every creature in NonCombat, EmoteManager.GetEmoteSet only selects a HeartBeat
    /// set whose Style matches the current stance, and EmoteManager's Motion branch only plays a styled
    /// motion when that style is NonCombat. A HeartBeat set styled Magic (the weave_spiral Loz attendants,
    /// 1002639-1002641, whose casting loop is MagicPowerUp10 then MagicSelfHead - motions their motion
    /// table 0x0900020E carries only under the Magic stance) is therefore never selected and could never
    /// play.
    ///
    /// Why it is gated. ace_world carries thousands of retail HeartBeat sets styled for a combat stance
    /// (HandCombat, SwordCombat and others) that are dormant today for exactly the reasons above. Relaxing
    /// either EmoteManager rule for everyone would wake all of them. So the relaxation is keyed on
    /// <see cref="WorldEventStyledIdle"/>, a runtime-only flag that ONLY WorldEventSpawner.PlaceNpcs sets;
    /// it is never persisted (a world-event npc is never saved to the shard anyway) and no other code path
    /// sets it, so every other creature in the world behaves exactly as before.
    ///
    /// Nothing here makes the npc hostile, attackable, or a monster: IsMonster is decided by Attackable and
    /// TargetingTactic (Monster.SetMonsterState), neither of which is touched, and CombatMode is left alone.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Runtime-only: this creature was placed by the World Events npc path and may play a styled idle
        /// in a stance other than NonCombat. Set only by <see cref="EnableWorldEventStyledIdle"/>.
        /// </summary>
        public bool WorldEventStyledIdle { get; private set; }

        /// <summary>
        /// The stance an npc's HeartBeat emote sets ask for, when they ask for one that is not NonCombat -
        /// or null when the creature should keep the stock NonCombat start.
        /// </summary>
        public static MotionStance? StyledIdleStance(IEnumerable<PropertiesEmote> emotes)
        {
            return StyledIdleStance(emotes, out _);
        }

        /// <summary>
        /// Returns a stance only when BOTH hold:
        ///
        ///   1. every HeartBeat set that names a Style names the SAME non-NonCombat, non-Invalid stance (a
        ///      creature with any NonCombat-styled HeartBeat set keeps NonCombat, so its existing idles are
        ///      never switched off by this); and
        ///   2. the creature carries NO emote set, in any category, that would knock it out of that stance or
        ///      be silently lost in it: a set with a Motion action whose Style is null or NonCombat, or any
        ///      Vendor set with a Motion action. EmoteManager plays those through its "vendor / other motions"
        ///      branch, which resets the stance to NonCombat and would end the styled idle for good; and a
        ///      NonCombat-styled Motion emote is simply dropped while the creature stands in another stance.
        ///
        /// <paramref name="blockedByMotionEmote"/> is true only when rule 1 found a stance and rule 2 vetoed
        /// it, so the caller can say why an npc that asked for a styled idle did not get one.
        /// </summary>
        public static MotionStance? StyledIdleStance(IEnumerable<PropertiesEmote> emotes, out bool blockedByMotionEmote)
        {
            blockedByMotionEmote = false;

            if (emotes == null)
                return null;

            MotionStance? stance = null;
            var stanceEscape = false;

            foreach (var emote in emotes)
            {
                if (emote == null)
                    continue;

                if (HasMotionAction(emote)
                    && (emote.Style == null || emote.Style == MotionStance.NonCombat || emote.Category == EmoteCategory.Vendor))
                    stanceEscape = true;

                if (emote.Category != EmoteCategory.HeartBeat || emote.Style == null)
                    continue;

                var style = emote.Style.Value;

                if (style == MotionStance.NonCombat || style == MotionStance.Invalid)
                    return null;

                if (stance != null && stance.Value != style)
                    return null;

                stance = style;
            }

            if (stance != null && stanceEscape)
            {
                blockedByMotionEmote = true;
                return null;
            }

            return stance;
        }

        private static bool HasMotionAction(PropertiesEmote emote)
        {
            if (emote.PropertiesEmoteAction == null)
                return false;

            foreach (var action in emote.PropertiesEmoteAction)
            {
                if (action != null && action.Type == (uint)EmoteType.Motion)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Called by WorldEventSpawner.PlaceNpcs BEFORE EnterWorld. Marks the creature and, when its
        /// HeartBeat sets name a styled stance (<see cref="StyledIdleStance"/>), starts it in that stance
        /// with Ready, so GetEmoteSet selects the set.
        ///
        /// Set before EnterWorld on purpose: a non-player creature's stance reaches the client through the
        /// MovementData block of its CreateObject, which is serialized from CurrentMotionState
        /// (WorldObject_Networking.SerializeCreateObject) every time the object comes into a player's view -
        /// so every viewer, including one who walks up later, sees the stance with no separate broadcast.
        ///
        /// Returns the stance applied, or null when the creature kept its stock NonCombat start.
        /// <paramref name="blockedByMotionEmote"/> reports the <see cref="StyledIdleStance(IEnumerable{PropertiesEmote}, out bool)"/>
        /// veto, for the caller's warning.
        /// </summary>
        public MotionStance? EnableWorldEventStyledIdle(out bool blockedByMotionEmote)
        {
            WorldEventStyledIdle = true;
            blockedByMotionEmote = false;

            if (MotionTableId == 0)
                return null;

            var stance = StyledIdleStance(Biota.PropertiesEmote, out blockedByMotionEmote);

            if (stance == null)
                return null;

            CurrentMotionState = new Motion(stance.Value, MotionCommand.Ready);

            return stance;
        }
    }
}
