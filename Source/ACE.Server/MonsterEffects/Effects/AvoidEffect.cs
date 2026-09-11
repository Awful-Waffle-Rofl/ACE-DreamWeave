using System;

using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// The token a "type=" arg names. Matched by <see cref="MonsterEffectSpec.GetEnum{T}"/> case-insensitively.
    /// </summary>
    public enum MonsterEffectAvoidType
    {
        Any,
        Melee,
    }

    /// <summary>
    /// Phase 3: a flat chance for this monster to avoid an incoming attack outright - a real avoidance
    /// GRANTER, unlike riposte and DebuffEffect/ReflectEffect's on=avoid riders, which answer an avoided
    /// attack but never cause one. This is the effect those reactors need on the same weenie: without a
    /// granter the pooled roll in Creature.RollMonsterEffectAvoidance can never succeed at all, since every
    /// reactor contributes a pure 0.0.
    ///
    /// ITS OWN OnAvoided FIRES ON EVERY POOLED WIN, not on a weighted share of them, because the dispatch
    /// site notifies every carried handler (see its doc comment for why the weighting it used to do made
    /// every reactor unreachable). For the one-granter monster, which is the whole authored shape today,
    /// that is exactly what the old weighting did as well - a lone contributor won the wheel every time - so
    /// nothing changes. Two avoid records on one monster now announce twice, and silent=true is the half
    /// that owns it: a weenie layering several avoidance sources authors silent=true on all but one. That
    /// throttle belongs here rather than at the dispatch site, which cannot tell a deliberate second
    /// announcement from an accidental one.
    ///
    /// type=melee restricts the contribution to melee attacks only (GetAvoidChance returns 0 for a missile
    /// or magic combatType); type=any (the default) contributes against any physical combatType the
    /// avoidance roll is asked about.
    /// </summary>
    public sealed class AvoidEffect : IMonsterEffect, IMonsterAvoidance
    {
        public string Kind => "avoid";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            if (spec.GetDouble("pct", 0.0) <= 0.0)
            {
                error = "avoid requires pct= > 0";
                return false;
            }

            var typeRaw = spec.GetString("type");
            if (typeRaw != null && (!Enum.TryParse<MonsterEffectAvoidType>(typeRaw, true, out var type) || !Enum.IsDefined(typeof(MonsterEffectAvoidType), type)))
            {
                error = "avoid type= must be any or melee";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// A pure read, per the interface contract - gated on type= but otherwise just the authored pct=,
        /// self-clamped by monster_effect_avoidance_cap so a single record's own contribution is meaningful
        /// in isolation (a direct caller, or a unit test) without changing what the pooled roll at the
        /// dispatch site ultimately allows through - it re-clamps the SUMMED total against the same cap.
        /// </summary>
        public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var type = spec.GetEnum("type", MonsterEffectAvoidType.Any);
            if (type == MonsterEffectAvoidType.Melee && combatType != CombatType.Melee)
                return 0.0;

            var pct = spec.GetDouble("pct", 0.0);
            if (pct <= 0.0)
                return 0.0;

            return Math.Min(pct, MonsterEffectCaps.AvoidanceCap);
        }

        /// <summary>
        /// An unexplained miss reads as a bug rather than a mechanic, so unless silent=true the attacking
        /// player is told what happened - the monster-side mirror of GameEventEvasionAttackerNotification's
        /// generic evade message, naming the monster explicitly. No-op for a non-player attacker (a monster
        /// vs. monster avoid has no one to tell) and for silent=true.
        /// </summary>
        public void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (spec.GetBool("silent", false))
                return;

            if (!(attacker is Player attackerPlayer) || attackerPlayer.Session == null)
                return;

            if (attackerPlayer.SquelchManager.Squelches.Contains(defender, ChatMessageType.CombatEnemy))
                return;

            attackerPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"{defender.Name} avoids your attack!", ChatMessageType.CombatEnemy));
        }
    }
}
