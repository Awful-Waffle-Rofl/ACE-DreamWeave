using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T2: a share of every incoming hit is paid out of Mana instead of Health - 10/17.5/25% by
    /// rank, plus a Magic Defense rider, capped by a server tunable. Implemented as a REDUCTION applied
    /// BEFORE the health write, exactly like Sanguine Ward: the barrier spends Mana and hands the caller a
    /// smaller damage figure, and the caller writes that smaller figure to the vital. It never calls
    /// UpdateVitalDelta(Health, +x) and never puts health back.
    ///
    /// IT WAS A REFUND UNTIL 2026-09-08 AND THAT WAS THE BUG. Every health write clamps at zero, so the
    /// number the old refund was handed was the victim's CURRENT HEALTH on any overkill hit, never the hit
    /// that was actually thrown. A player with 477 Health took a 942-damage critical, the write clamped
    /// `damageTaken` to 477, the barrier refunded its share of 477, and the player survived above zero.
    /// clamp(H - amount, 0) + share*amount is strictly greater than H - amount*(1 - share) whenever
    /// amount > H, so a refund applied after a clamped write can never be correct: with any Mana at all,
    /// NO single hit of any size could kill a barrier carrier. Feeding the refund the true pre-clamp
    /// amount would have made it worse, not better - the defect was the convention, not the number. Do not
    /// reintroduce a refund shape here for any reason.
    ///
    /// COVERAGE IS FIVE CALL SITES, and none of them is redundant. There is no single choke point through
    /// which a player loses health. Player.TakeDamage covers melee, missile and hotspots; the four magic
    /// paths write Health straight to the vital and never pass through it. All five call
    /// Player.AbsorbWithManaBarrier (or AbsorbWithManaBarrierDot for the tick path, which carries no
    /// attacker) and assign the result back over their own damage figure:
    ///
    ///   - Player.TakeDamage                          - melee, missile, hotspots
    ///   - SpellProjectile.DamageTarget               - war, void and life bolts
    ///   - WorldObject_Magic.HandleCastSpell_Boost    - Harm
    ///   - WorldObject_Magic.HandleCastSpell_Transfer - Drain Health
    ///   - EnchantmentManager.ApplyDamageTick         - DoT ticks
    ///
    /// The DoT site is the ACCUMULATION point, not Player.TakeDamageOverTime where the barrier first
    /// landed. ApplyDamageTick used to cap its accumulated tick total to the victim's current Health before
    /// calling TakeDamageOverTime, which defeated a pre-write absorb just as thoroughly as the old
    /// post-write refund did: an absorb applied to a figure already capped at current health always leaves
    /// the victim strictly alive. The cap now happens after the absorb, and only for the per-damager credit
    /// accounting. Never call the barrier from TakeDamageOverTime again.
    ///
    /// The magic sites call the narrow barrier method rather than dispatching the whole incoming-damage
    /// hook, on purpose: Thorns rides that hook and must not begin reflecting on magic damage. Do not
    /// "simplify" any of the four away - deleting one silently restores the physical-only behaviour a
    /// player reported as a bug on 2026-09-03 ("Mana Barrier doesn't absorb magic damage; only physical").
    ///
    /// THE BARRIER NO LONGER RIDES IIncomingDamageAbility AT ALL. It is an IPassiveStatAbility marker, the
    /// same shape as Sanguine Ward: read directly off the player at each damage site, dispatched from no
    /// combat event. A pre-write reduction cannot be delivered by a hook that fires after the write, and
    /// the physical site now calls it by name alongside the ward.
    ///
    /// Sanguine Ward (Blood Mage T3) sits immediately above each of these calls and the two now share one
    /// convention. They still GATE differently and that part must not be unified: the barrier reproduces
    /// the old hook's preconditions (enabled, learned, live non-player attacker other than the victim), the
    /// ward is called unconditionally. Order at every site is: cloak proc, ward, barrier, health write.
    ///
    /// If Mana cannot pay the whole diverted share, it pays what it can (partial) and never goes negative.
    /// </summary>
    public class ManaBarrierAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ManaBarrier,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 2,
            Name = "manabarrier",
            DisplayName = "Mana Barrier",
            Description = "10/17.5/25% of the damage you take is paid out of your Mana instead of your Health " +
                          "(60% cap). Higher Magic Defense increases the share. If your Mana runs short it pays " +
                          "what it can.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
            AffinitySkill = Skill.MagicDefense,
        };

        /// <summary>
        /// The fraction of incoming damage diverted to Mana at a given rank: base + (rank-1)*step, plus the
        /// Magic Defense rider, plus the Mana Barrier equipment mod, clamped to <paramref name="cap"/>.
        /// <paramref name="gearModFraction"/> is the Mana Barrier equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to just the (capped) gear share. Defaults
        /// to 0, which reproduces the pre-equipment-mod behavior exactly. Pure for testability.
        /// </summary>
        public static double DivertShare(int rank, double baseShare, double stepPerRank, double magicDefFraction, double cap, double gearModFraction = 0.0)
        {
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (rank <= 0 && gearBonus <= 0.0)
                return 0.0;

            var abilityShare = rank <= 0 ? 0.0 : baseShare + (rank - 1) * stepPerRank + Math.Max(0.0, magicDefFraction);

            return Math.Clamp(abilityShare + gearBonus, 0.0, Math.Max(0.0, cap));
        }

        /// <summary>
        /// How much of the hit the barrier soaks up and how much Mana that costs, given a diverted share
        /// and the Mana actually on hand. Pure for testability.
        ///
        /// The field is DamageAbsorbed, not "HealthRestored": nothing restores Health anywhere any more.
        /// Every consumer subtracts this from the incoming damage BEFORE the vital write.
        /// </summary>
        public readonly struct Divert
        {
            public uint DamageAbsorbed { get; init; }
            public uint ManaSpent { get; init; }
        }

        /// <summary>
        /// Resolves the diverted share against the Mana available: the full amount when it is affordable,
        /// otherwise as much as <paramref name="currentMana"/> can buy at <paramref name="manaPerHealth"/>
        /// mana per point of health. Never spends more Mana than is present, and never returns a negative
        /// or unaffordable result. Pure.
        /// </summary>
        public static Divert Resolve(uint damageTaken, double share, uint currentMana, double manaPerHealth)
        {
            if (damageTaken == 0 || share <= 0.0 || currentMana == 0)
                return default;

            var rate = manaPerHealth > 0.0 ? manaPerHealth : 1.0;

            var wantedHealth = (uint)Math.Floor(damageTaken * Math.Max(0.0, share));
            if (wantedHealth == 0)
                return default;

            var manaNeeded = (uint)Math.Ceiling(wantedHealth * rate);

            if (manaNeeded <= currentMana)
                return new Divert { DamageAbsorbed = wantedHealth, ManaSpent = manaNeeded };

            // partial: buy as much absorb as the remaining mana covers
            var affordableHealth = (uint)Math.Floor(currentMana / rate);
            if (affordableHealth == 0)
                return default;

            var manaSpent = (uint)Math.Ceiling(affordableHealth * rate);
            if (manaSpent > currentMana)
                manaSpent = currentMana;

            return new Divert { DamageAbsorbed = affordableHealth, ManaSpent = manaSpent };
        }

        /// <summary>
        /// What the barrier leaves of an incoming hit, and what that cost: DamageAfter is the number the
        /// caller writes to Health, DamageAbsorbed is the number reported in the "absorbs N points" line,
        /// ManaSpent is what leaves the Mana pool. DamageAfter + DamageAbsorbed == incomingDamage always.
        /// </summary>
        public readonly struct Absorbed
        {
            public uint DamageAfter { get; init; }
            public uint DamageAbsorbed { get; init; }
            public uint ManaSpent { get; init; }
        }

        /// <summary>
        /// Resolves an incoming hit against the barrier and returns the damage that should still reach
        /// Health. Wraps <see cref="Resolve"/>; the caller spends ManaSpent and applies DamageAfter.
        ///
        /// THIS FUNCTION TAKES NO HEALTH INPUT, BY DESIGN. That is the core of the 2026-09-08 overkill fix
        /// and it is structural, not a tuning change. The barrier used to run as a refund AFTER the health
        /// write, which meant it was handed a damage figure that had already been clamped against the
        /// victim's remaining health - so a 942-damage hit on a 477-health player arrived here as 477, the
        /// barrier refunded a share of 477, and the player survived a hit that was twice their health bar.
        ///
        /// TAKING NO HEALTH INPUT IS NECESSARY AND NOT SUFFICIENT, and the first version of this comment
        /// overclaimed by saying the error class "cannot recur". It can, from any CALLER that clamps before
        /// it calls: a figure already capped at current health leaves the victim strictly alive after any
        /// nonzero absorb, wherever the capping happened. Exactly one caller did that -
        /// EnchantmentManager.ApplyDamageTick capped its accumulated DoT total to current Health before
        /// handing it over - and it was fixed the same day by moving the absorb above the cap. The honest
        /// statement is the pair: this function reads no health-clamped figure at any of the five sites,
        /// AND the one upstream caller that used to hand it one no longer does. No residual path is known
        /// as of 2026-09-08. A new caller must feed this the damage as thrown, never a health-capped share
        /// of it.
        ///
        /// THE CALLER MUST APPLY THE RESULT BEFORE ITS VITAL WRITE. Applying it afterwards reintroduces
        /// exactly the bug this shape exists to prevent.
        ///
        /// <paramref name="share"/> is clamped into [0, 1] here: the barrier can absorb all of a hit but
        /// never more than was thrown, whatever an out-of-range tunable says. In normal operation the
        /// share arrives already capped by <see cref="DivertShare"/>, so this clamp is inert.
        /// </summary>
        public static Absorbed AbsorbDamage(uint incomingDamage, double share, uint currentMana, double manaPerHealth)
        {
            var divert = Resolve(incomingDamage, Math.Clamp(share, 0.0, 1.0), currentMana, manaPerHealth);

            return new Absorbed
            {
                DamageAfter = incomingDamage - divert.DamageAbsorbed,
                DamageAbsorbed = divert.DamageAbsorbed,
                ManaSpent = divert.ManaSpent,
            };
        }

        /// <summary>
        /// Mirrors the terms fed into DivertShare above (x100 for display, "%" since it's a share of
        /// damage) - the two must stay in step. Affinity carries the RAW (unclamped) Magic Defense rider
        /// like AcidProc's affinity-cap pattern; Effective applies the same Math.Clamp(abilityShare +
        /// gearBonus, 0.0, cap) DivertShare uses. CapNote is set only when the clamp actually reduced this
        /// call's value.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseShare = PropertyManager.GetDouble("class_ability_manabarrier_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_manabarrier_step").Item;
            var cap = PropertyManager.GetDouble("class_ability_manabarrier_max_share").Item;

            var skillShare = rank <= 0 ? 0.0 : baseShare + (rank - 1) * stepPerRank;

            var rawAffinity = Math.Max(0.0, player.GetClassAbilityScaling(Skill.MagicDefense,
                PropertyManager.GetDouble("class_ability_manabarrier_magicdef_per_trained").Item,
                PropertyManager.GetDouble("class_ability_manabarrier_magicdef_per_spec").Item) * 0.01);

            var gearBonus = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.ManaBarrier));

            var abilityShare = skillShare + rawAffinity;
            var uncapped = abilityShare + gearBonus;
            var clamped = Math.Clamp(uncapped, 0.0, Math.Max(0.0, cap));

            var skill = skillShare * 100.0;
            var affinity = rawAffinity * 100.0;
            var gear = gearBonus * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "dmg to mana",
                Per = null,
                CapNote = clamped < uncapped - 0.0000001 ? "barrier cap" : null,
            };
        }
    }
}
