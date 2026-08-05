using System;

using ACE.Entity.Enum;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for BLOOD PRICE (Blood Mage T3): a damaging spell of any school also costs health -
    /// 3/4/5% of CURRENT health by rank - and deals +8/16/24% damage. Below 20% current health the cast
    /// lands normally with no bonus and no health cost. BLOOD-MAGE-DESIGN sec 3.
    ///
    /// Kept as a static so the cost/bonus arithmetic and the qualifying-spell rule are unit-testable
    /// without a live Player. The stateful half - spending the health at cast time and carrying the
    /// resulting multiplier to the damage sites - lives in Player_ClassAbilityBuffs.cs.
    /// </summary>
    public static class BloodPriceMath
    {
        /// <summary>The outcome of one cast: what it costs and what it multiplies damage by.</summary>
        public readonly struct Charge
        {
            public readonly uint HealthCost;
            public readonly float DamageMultiplier;

            public Charge(uint healthCost, float damageMultiplier)
            {
                HealthCost = healthCost;
                DamageMultiplier = damageMultiplier;
            }
        }

        /// <summary>A cast that pays nothing and gains nothing - the low-health case and every non-owner.</summary>
        public static readonly Charge None = new Charge(0, 1.0f);

        /// <summary>
        /// Fraction of CURRENT health one cast spends: 3/4/5% at ranks 1-3. Rank 0 or below returns 0; a
        /// rank above 3 clamps to rank 3. Negative tunables are floored to 0 so a mis-set value cannot
        /// heal the caster.
        /// </summary>
        public static double HealthCostFraction(int rank, double costR1, double costR2, double costR3)
        {
            if (rank <= 0)
                return 0.0;

            var cost = rank switch
            {
                1 => costR1,
                2 => costR2,
                _ => costR3,
            };

            return cost < 0.0 ? 0.0 : cost;
        }

        /// <summary>
        /// Damage bonus one cast gains, as a fraction: +8/16/24% at ranks 1-3. Same clamping rules as
        /// <see cref="HealthCostFraction"/>.
        ///
        /// <paramref name="gear"/> is the BLOOD PRICE equipment mod (EquipmentModId.BloodPrice), added to
        /// the rank's bonus on the same additive axis (DESIGN.md 3.3). Defaults to 0, and adding 0.0 is
        /// exact, so an unmodded cast is bit-identical.
        ///
        /// GEAR AMPLIFIES THE PAYOFF AND NEVER DISCOUNTS THE PRICE. <see cref="HealthCostFraction"/>
        /// deliberately takes NO gear term: the health cost is the ability's own bargain, exactly as Savage
        /// Blows' stamina surcharge is (DESIGN.md 2.3, "two cost mechanics are deliberately NOT moddable").
        /// Do not mirror this parameter onto the cost.
        /// </summary>
        public static double DamageBonus(int rank, double bonusR1, double bonusR2, double bonusR3, double gear = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var bonus = (rank switch
            {
                1 => bonusR1,
                2 => bonusR2,
                _ => bonusR3,
            }) + gear;

            return bonus < 0.0 ? 0.0 : bonus;
        }

        /// <summary>
        /// Resolves one cast against the caster's health.
        ///
        /// THE LOW-HEALTH FLOOR IS ALL-OR-NOTHING AND IT IS CHECKED ONCE, HERE. At or below
        /// <paramref name="minHealthFraction"/> of maximum health, the cast costs nothing AND gains
        /// nothing - never one without the other. That pairing is the whole safety property: it is why the
        /// caller must resolve at cast time and carry the multiplier to the damage site rather than
        /// re-deriving it there, since the cost itself moves current health and could otherwise push the
        /// caster below the floor between the two reads and bill them for a bonus they never got.
        ///
        /// BLOOD PRICE CAN NEVER KILL ITS OWN CASTER: the cost is clamped to leave at least 1 health. With
        /// the 20% floor this clamp is unreachable at the design tunables (5% of current health, taken at
        /// 20%+ of max), and it is here so a retuned cost fraction cannot turn the ability into a suicide.
        /// </summary>
        public static Charge Resolve(uint currentHealth, uint maxHealth, double costFraction, double damageBonus, double minHealthFraction)
        {
            if (currentHealth == 0 || maxHealth == 0)
                return None;

            if (costFraction <= 0.0 && damageBonus <= 0.0)
                return None;

            // strictly below the floor pays nothing and gains nothing; exactly at the floor still qualifies
            if (currentHealth < minHealthFraction * maxHealth)
                return None;

            var cost = (uint)Math.Round(currentHealth * Math.Max(0.0, costFraction));

            if (cost >= currentHealth)
                cost = currentHealth - 1;

            return new Charge(cost, (float)(1.0 + Math.Max(0.0, damageBonus)));
        }

        /// <summary>
        /// Whether a spell is one Blood Price charges for. "Damaging spells of ANY SCHOOL" in the design
        /// text means war, void AND life - not every harmful spell, and not every life spell.
        ///
        /// Qualifying:
        ///  - <see cref="SpellType.Projectile"/> - every war and void bolt/blast/volley/arc/streak;
        ///  - <see cref="SpellType.LifeProjectile"/> - Martyr's Hecatomb and Curse of Raven Fury;
        ///  - a harmful <see cref="SpellType.Boost"/> on Health - Harm.
        ///
        /// Deliberately NOT qualifying:
        ///  - debuffs and dispels (Enchantment, EnchantmentProjectile, Dispel) - harmful, but they deal no
        ///    damage for a bonus to act on, so charging health for them would be pure loss;
        ///  - <see cref="SpellType.Transfer"/>, i.e. DRAIN. This is a mechanical exclusion, not a school
        ///    one: Drain's damage is bounded by spell.TransferCap, so a multiplier on its roll is invisible
        ///    against anything worth draining, and a multiplier on its CAP is the exact shape
        ///    BLOOD-MAGE-DESIGN rejected when it took Drain off the life-vulnerability axis. Charging
        ///    health for an increase the player cannot receive would be strictly worse than not owning the
        ///    ability. See the note at the drainMod site in WorldObject_Magic.HandleCastSpell_Transfer.
        ///
        /// <paramref name="affectsHealth"/> is the spell's vital: the Boost branch must not fire for a
        /// Mana or Stamina drain-boost, whose "damage" is not health at all.
        /// </summary>
        public static bool SpellQualifies(bool isHarmful, SpellType metaSpellType, bool affectsHealth)
        {
            if (!isHarmful)
                return false;

            switch (metaSpellType)
            {
                case SpellType.Projectile:
                case SpellType.LifeProjectile:
                    return true;

                case SpellType.Boost:
                case SpellType.FellowBoost:
                    return affectsHealth;

                default:
                    return false;
            }
        }
    }
}
