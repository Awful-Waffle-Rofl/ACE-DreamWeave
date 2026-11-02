using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.MonsterEffects;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Player half of Elemental Proficiency (Archmage T3, ClassAbilityId.ElementalRend, ranks 1-3): the
    /// INSTANT per-element spell-damage effects - Fire (damage), Slash (crit chance) and Pierce (resistance
    /// ignore or a flat bonus, whichever gives more damage on that hit). They act INSIDE SpellProjectile.CalculateDamage
    /// rather than through ISpellHitAbility, because that hook fires after the damage is applied. The timed
    /// effects (Lightning, Frost, Acid, Bludgeon) are the ability handler's job and live elsewhere.
    ///
    /// Every method here returns the neutral value (1.0 multiplier, 0 bonus) for rank 0, a suppressed or
    /// disabled class-ability system, and any target that is not ElementalProficiencyMath.IsEligibleTarget
    /// (players, pets and game pieces never take an effect). The arithmetic is ElementalProficiencyMath's.
    /// </summary>
    partial class Player
    {
        /// <summary>The held Elemental Proficiency rank against this target: 0 for an ineligible target or no rank.</summary>
        public int GetElementalProficiencyRank(Creature target)
        {
            if (!ElementalProficiencyMath.IsEligibleTarget(target))
                return 0;

            return TryGetClassAbility(ClassAbilityId.ElementalRend, out var rank) ? rank : 0;
        }

        /// <summary>
        /// The effect magnitude for a landed spell of <paramref name="type"/> against <paramref name="target"/>:
        /// War Magic affinity and the Elemental Rend gear amplifier applied exactly where
        /// ElementalProficiencyMath.IsAffinityScaled says. 0 when no effect applies.
        /// </summary>
        public double GetElementalProficiencyMagnitude(DamageType type, Creature target)
        {
            if (!ElementalProficiencyMath.HasEffect(type))
                return 0.0;

            var rank = GetElementalProficiencyRank(target);
            if (rank <= 0)
                return 0.0;

            var scaled = ElementalProficiencyMath.IsAffinityScaled(type);

            return ElementalProficiencyMath.Magnitude(type, rank,
                scaled ? GetClassAbilityAffinityMultiplier(Skill.WarMagic) : 1.0,
                scaled ? GetEquippedModValue(EquipmentModId.ElementalRend) : 0.0);
        }

        /// <summary>
        /// The final-damage multiplier from the Fire effect (1 + fire magnitude), or from the Pierce flat bonus
        /// (1 + pierce magnitude) when this hit's Pierce decision chose the flat shape. <paramref name="pierceShape"/>
        /// is the decision GetArchmageInnateOverride made for this same hit, so the resistance site and this site
        /// can never both apply Pierce. 1.0 for every other damage type.
        /// </summary>
        public float GetElementalProficiencyDamageMod(DamageType type, Creature target, ElementalProficiencySpellMath.PierceShape pierceShape)
        {
            if (type != DamageType.Fire && type != DamageType.Pierce)
                return 1.0f;

            return (float)ElementalProficiencySpellMath.DamageMultiplier(type, GetElementalProficiencyMagnitude(type, target), pierceShape);
        }

        /// <summary>
        /// Slash: the crit chance ADDED to the landing spell's chance, as a fraction (0.05 = 5 percentage
        /// points per rank). Never scaled by affinity or gear. 0 for every other damage type.
        /// </summary>
        public float GetElementalProficiencySlashCritBonus(DamageType type, Creature target)
        {
            if (type != DamageType.Slash)
                return 0.0f;

            return (float)GetElementalProficiencyMagnitude(type, target);
        }

        /// <summary>
        /// Pierce: the effect magnitude for a pierce-typed resistance lookup, handed to the per-hit decision in
        /// GetArchmageInnateOverride, which picks EXACTLY ONE of the two shapes (resistance ignore or flat bonus),
        /// whichever gives more damage (owner ruling 2026-11-02). 0 for any other resistance type.
        /// </summary>
        public double GetElementalProficiencyPierceMagnitude(ResistanceType type, Creature target)
        {
            if (type != ResistanceType.Pierce)
                return 0.0;

            return GetElementalProficiencyMagnitude(DamageType.Pierce, target);
        }

        // ---- The timed effects, applied after the hit (ElementalRendAbility.OnSpellHit calls this) ----------

        // Keyed by target guid inside the tracker. Deliberately NOT the Rogue Acid Proc's table
        // (Player_ClassAbilityBuffs.cs): the two never share an entry or a loop, though they follow the same refresh
        // rule (one tick counter; a new hit resets the duration, never the cadence). Mutated only inside actions on
        // this player's own queue (StartElementalAcidBurn, the tick chain, and the reset); cleared by
        // ResetClassAbilityTransientState.
        private readonly ElementalAcidBurnTracker elementalAcidBurns = new ElementalAcidBurnTracker();

        private void ClearElementalProficiencyTransientState()
        {
            elementalAcidBurns.Clear();
        }

        /// <summary>
        /// Applies the TIMED Elemental Proficiency effect for a damaging spell that just landed on a monster:
        /// Lightning (target takes more damage), Frost (slow), Acid (burn) or Bludgeon (stun). Fire, Pierce and
        /// Slash act inside the damage calculation and are NOT applied here. No roll. Does nothing for an
        /// ineligible target (players, pets, game pieces) or an unlearned ability.
        ///
        /// The caster is told in chat ONLY when an effect newly lands (the Creature methods return true only
        /// for a new landing), through the same channel as Weakened Blood's "takes hold" line.
        /// </summary>
        public void ApplyElementalProficiencySpellHit(Creature target, SpellProjectile projectile)
        {
            if (target == null || projectile?.Spell == null)
                return;

            var type = projectile.Spell.DamageType;
            var rank = GetElementalProficiencyRank(target);

            if (!ElementalProficiencyMath.ShouldApplyTimedEffect(ElementalProficiencyMath.IsEligibleTarget(target), target is Player, rank, type))
                return;

            switch (type)
            {
                case DamageType.Electric:
                    if (target.TryApplyElementalLightning(GetElementalProficiencyMagnitude(type, target), ElementalProficiencyMath.LightningSeconds()))
                    {
                        SendClassAbilityBuffMessage($"Elemental Proficiency: Your lightning leaves {target.Name} exposed to all damage!");

                        // the visual a cast Lightning Vulnerability plays, at the landed spell's level
                        var vuln = ElementalRendAbility.GetVulnerabilitySpell(type, projectile.Spell.Level);
                        if (vuln != null)
                        {
                            var vulnSpell = new Spell(vuln.Value);
                            if (!vulnSpell.NotFound && vulnSpell.TargetEffect != 0)
                                target.EnqueueBroadcast(new GameMessageScript(target.Guid, vulnSpell.TargetEffect, vulnSpell.Formula.Scale));
                        }
                    }
                    break;

                case DamageType.Cold:
                    var factor = (float)ElementalProficiencyMath.FrostSpeedFactor(GetElementalProficiencyMagnitude(type, target));
                    if (target.TryApplyClassAbilityFrostSlow(ElementalProficiencyMath.FrostSeconds(), factor))
                        SendClassAbilityBuffMessage($"Elemental Proficiency: Your frost slows {target.Name}!");
                    break;

                case DamageType.Acid:
                    ApplyElementalAcidBurn(target, projectile, GetElementalProficiencyMagnitude(type, target));
                    break;

                case DamageType.Bludgeon:
                    if (target.TryApplyClassAbilityStun(ElementalProficiencyMath.StunSeconds(rank), ElementalProficiencyMath.StunImmunitySeconds()))
                        SendClassAbilityBuffMessage($"Elemental Proficiency: Your bludgeoning spell stuns {target.Name}!");
                    break;
            }
        }

        private void ApplyElementalAcidBurn(Creature target, SpellProjectile projectile, double fraction)
        {
            if (target.IsDead)
                return;

            var ticks = ElementalProficiencyMath.AcidTicks();
            var perTick = ElementalProficiencyMath.AcidDamagePerTick(projectile.LastAppliedDamage, fraction, ticks);
            if (perTick <= 0.0)
                return;

            // Everything the caster-side work needs is captured NOW; the dictionary itself is only ever
            // touched inside an action on THIS player's own queue (the same one the tick chain runs on),
            // because this method is reached from the projectile's collision, which may be another thread.
            var tickAmount = (uint)Math.Max(1.0, Math.Round(perTick));
            var interval = ElementalProficiencyMath.AcidSeconds() / ticks;

            var chain = new ActionChain();
            chain.AddAction(this, () => StartElementalAcidBurn(target, tickAmount, ticks, interval));
            chain.EnqueueChain();
        }

        /// <summary>
        /// Runs on the caster's own queue (see ApplyElementalAcidBurn). With no running burn on the target, starts
        /// one whose first tick lands one interval from now, and tells the caster. With one running, refreshes it
        /// (new per-tick amount, full tick budget) without rescheduling, so the next tick keeps the loop's cadence,
        /// and says nothing. ElementalAcidBurnTracker owns both rules.
        /// </summary>
        private void StartElementalAcidBurn(Creature target, uint tickAmount, int ticks, double intervalSeconds)
        {
            var plan = elementalAcidBurns.Apply(target.Guid.Full, tickAmount, ticks, intervalSeconds,
                () => target != null && !target.IsDead,
                // a DoT tick: never reflected, and not routed through the enchantment registry, so the Focus DoT bonus never applies
                amount => target.TakeDamage(this, DamageType.Base, amount, false, IncomingDamageOrigin.Secondary),
                ScheduleElementalAcidTick);

            if (plan == ElementalAcidBurnTracker.Plan.StartNew)
                SendClassAbilityBuffMessage($"Elemental Proficiency: Your acid burns {target.Name}!");
        }

        /// <summary>The tracker's scheduler: runs <paramref name="action"/> on this player's own queue after <paramref name="delaySeconds"/>.</summary>
        private void ScheduleElementalAcidTick(double delaySeconds, Action action)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delaySeconds);
            chain.AddAction(this, action);
            chain.EnqueueChain();
        }
    }
}
