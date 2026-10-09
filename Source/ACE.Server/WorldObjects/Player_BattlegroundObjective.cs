using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The player half of the Attack/Defend crystal damage gate (Docs/Pvp/ATTACK-DEFEND.md "Who may damage it"). Consulted
    /// FIRST in <see cref="CheckPKStatusVsTarget"/>, ahead of every PvP and retail PK rule, whenever the target is a creature
    /// carrying a <see cref="BattlegroundObjectiveTag"/>. The decision itself is the pure <see cref="BattlegroundObjectiveGate"/>
    /// over this player's live binding.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The crystal refusal line's rate limit, per player: the line repeats at most every FriendlyFireNoticeIntervalSeconds (3 s).
        /// Created on first use rather than by a field initializer, because the player gate tests seed a Player without running
        /// constructors or initializers; only this player's own landblock thread touches it.
        /// </summary>
        private CrystalNoticeThrottle pvpCrystalRefusalThrottle;

        /// <summary>True once a refusal line has been rate-limited for this player (the throttle exists). A test seam: the silent probe check must leave it false.</summary>
        internal bool PvpCrystalRefusalThrottleStarted => pvpCrystalRefusalThrottle != null;

        /// <summary>
        /// Returns false when the target is no match objective (the gate has no say and the rules below run unchanged). Returns
        /// true when it decided: <paramref name="result"/> is null to allow, or the PvP refusal pair every caller already reports.
        /// Beneficial spells are refused too (only a harmful action by an attacker bound to the crystal's own Live match, not
        /// respawning, may proceed). A refusal also sends the gate's line, rate-limited because an AoE, a cleave or a multi-shot
        /// checks every creature in range at once.
        /// </summary>
        internal bool TryBattlegroundObjectiveGate(Creature targetCreature, Spell spell, out List<WeenieErrorWithString> result)
        {
            result = null;

            var tag = targetCreature?.BattlegroundObjective;

            if (tag == null)
                return false;

            var reason = BattlegroundObjectiveGate.Evaluate(tag, pvpBinding, IsHarmfulAction(spell));

            if (BattlegroundObjectiveGate.IsAllowed(reason))
                return true;

            SendBattlegroundObjectiveRefusal(reason, tag.Index);

            result = BattlegroundObjectiveRefusalErrors();
            return true;
        }

        /// <summary>
        /// The SILENT form of the gate, for candidate filters (cleave, multi-shot, the AoE and cascade abilities): true when this
        /// player's weapon attack on <paramref name="creature"/> would be refused because it is a match objective the gate refuses.
        /// Sends nothing and touches no throttle, so scanning the crystals in range never tells a player about a crystal they did not
        /// aim at. False for any creature that is no objective (the normal rules then decide).
        /// </summary>
        internal bool IsBattlegroundObjectiveRefused(Creature creature)
        {
            var tag = creature?.BattlegroundObjective;

            if (tag == null)
                return false;

            return !BattlegroundObjectiveGate.IsAllowed(BattlegroundObjectiveGate.Evaluate(tag, pvpBinding, harmful: true));
        }

        /// <summary>
        /// <see cref="IsBattlegroundObjectiveRefused"/> for a caster typed as a WorldObject: false unless it is a player. Candidate
        /// filters call this BEFORE <c>CheckPKStatusVsTarget</c>, which would send the refusal line.
        /// </summary>
        internal static bool IsObjectiveProbeRefused(WorldObject caster, Creature creature) =>
            caster is Player player && player.IsBattlegroundObjectiveRefused(creature);

        /// <summary>
        /// Whether the action the gate is asked about is harmful: no spell is a weapon attack (melee, missile, cleave, multi-shot), which
        /// is always harmful; a spell is harmful unless it carries the Beneficial flag (Spell.IsHarmful).
        /// </summary>
        internal static bool IsHarmfulAction(Spell spell) => spell == null || spell.IsHarmful;

        /// <summary>
        /// The error pair a refused crystal hit reports. Element 0 goes to the attacker with the crystal's name, so it is the neutral
        /// "{name} is an invalid target." rather than a PK-type line about a creature that has no PK type; the gate's own chat line
        /// says why. Element 1 is sent only when the target is a player, which a crystal never is; it keeps the PvP pair's partner.
        /// </summary>
        internal static List<WeenieErrorWithString> BattlegroundObjectiveRefusalErrors() =>
            new List<WeenieErrorWithString>() { WeenieErrorWithString._IsAnInvalidTarget, WeenieErrorWithString._FailsToAffectYou_NotSamePKType };

        private void SendBattlegroundObjectiveRefusal(ObjectiveGateReason reason, int crystalIndex)
        {
            // A sealed crystal names itself and the one to destroy first, read from the sequence on the match this player is bound to.
            var line = BattlegroundText.CrystalRefusal(reason, pvpBinding?.Match?.CrystalSequence, crystalIndex);

            if (line == null)
                return;

            if (!(pvpCrystalRefusalThrottle ??= new CrystalNoticeThrottle()).TryAcquire(DateTime.UtcNow, PvpArenaHookSettings.FriendlyFireNoticeIntervalSeconds))
                return;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }
    }
}
