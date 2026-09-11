using System;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 2: a share of incoming damage is paid out of this monster's own Mana instead of its Health -
    /// reuses ManaBarrierAbility.Resolve wholesale (the Archmage T2 ability this mirrors), as a FILTER
    /// applied BEFORE any vital write: the effect returns a smaller amount and never touches Health
    /// directly. The player-side ability now works the same way, at all five of its call sites.
    ///
    /// THIS SIDE WAS ALWAYS CORRECT AND THE PLAYER SIDE WAS NOT. An earlier revision of this comment
    /// claimed the pre-write filter and the player ability's post-write refund were "mathematically the
    /// same outcome, reached from opposite ends of the write". That was FALSE, and the falsehood is what
    /// let a bug ship: a vital write CLAMPS at zero, so a refund applied after it is handed the victim's
    /// remaining health rather than the damage thrown. clamp(H - amount, 0) + share*amount is strictly
    /// greater than H - amount*(1 - share) whenever amount > H, which made a barrier carrier unkillable by
    /// any single hit. The two shapes agree only while the hit is smaller than the victim's health; they
    /// diverge on exactly the case that matters. The player ability was converted to this shape on
    /// 2026-09-08 - do not "restore symmetry" by moving either side back after a write.
    ///
    /// pct= is capped by monster_effect_manabarrier_cap in addition to Validate's (0, 1] authoring range,
    /// mirroring the player-side ability's own cap parameter (class_ability_manabarrier_max_share, see
    /// ManaBarrierAbility.DivertShare) - without it, a weenie authoring pct=1.0 makes the monster immune to
    /// damage for as long as its Mana lasts.
    ///
    /// Spends Mana through UpdateVitalDelta, never TakeDamage - this is the monster paying its OWN cost, not
    /// damage dealt to anyone, the same distinction LeechEffect's self-heal makes.
    /// </summary>
    public sealed class ManaBarrierEffect : IMonsterEffect, IMonsterIncomingDamage
    {
        public string Kind => "manabarrier";

        /// <summary>
        /// Runs ahead of every reader on its hooks: this effect CHANGES the damage figure they size
        /// themselves from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var pct = spec.GetDouble("pct", 0.0);
            if (pct <= 0.0 || pct > 1.0)
            {
                error = "manabarrier requires pct= in (0, 1]";
                return false;
            }

            if (spec.GetDouble("rate", 1.0) <= 0.0)
            {
                error = "manabarrier requires rate= > 0";
                return false;
            }

            error = null;
            return true;
        }

        public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (amount == 0)
                return amount;

            var pct = spec.GetDouble("pct", 0.0);
            if (pct <= 0.0)
                return amount;

            pct = Math.Min(pct, MonsterEffectCaps.ManaBarrierCap);
            if (pct <= 0.0)
                return amount;

            var mana = defender.Mana;
            if (mana == null || mana.Current == 0)
                return amount;

            var rate = spec.GetDouble("rate", 1.0);

            var divert = ManaBarrierAbility.Resolve(amount, pct, mana.Current, rate);

            if (divert.DamageAbsorbed == 0)
                return amount;

            defender.UpdateVitalDelta(mana, -(int)divert.ManaSpent);

            Announce(defender, source, spec, ref state);

            return amount - divert.DamageAbsorbed;
        }

        /// <summary>
        /// Unless silent=true, tells the attacking player once per this creature's lifetime (state.Announced)
        /// that its damage is being partly absorbed - players cannot see a monster's Mana bar, so an
        /// unannounced barrier reads as a hidden health multiplier rather than a mechanic. "Once" here means
        /// once for as long as this effect's state persists (the creature's life), not once per encounter -
        /// there is no fight-boundary hook to reset it against, the same limit WardEffect's on=spawn sentinel
        /// and FlatDamageEffect's proc sentinel already live with.
        /// </summary>
        private static void Announce(Creature defender, WorldObject source, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (state.Announced || spec.GetBool("silent", false))
                return;

            state.Announced = true;

            if (!(source is Player sourcePlayer) || sourcePlayer.Session == null)
                return;

            if (sourcePlayer.SquelchManager.Squelches.Contains(defender, ChatMessageType.CombatEnemy))
                return;

            sourcePlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"{defender.Name} shields itself from some of your damage with a barrier of mana!", ChatMessageType.CombatEnemy));
        }
    }
}
