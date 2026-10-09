using System;

using ACE.Entity.Enum;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for the Blood Mage's Blood Charge transient stack counter, owned by SANGUINE RESERVE
    /// (Blood Mage T1 game-changer). Kept out of Player so the stack-cap, idle-expiry and damage-ramp
    /// arithmetic used by Player_ClassAbilityBuffs.cs (bloodChargeStacks) is unit-testable without
    /// touching the Player type - the same split FrenzyAbility/NetherRushAbility use for their own stack
    /// math. Not an IClassAbility: <see cref="Abilities.SanguineReserveAbility"/> carries the definition,
    /// and the effect is read directly at the life-damage sites rather than dispatched from a hook.
    ///
    /// The burst half - Exsanguinate consuming the whole pool at a multiple of the per-charge value -
    /// lives in <see cref="ExsanguinateMath"/>.
    /// </summary>
    public static class BloodChargeMath
    {
        /// <summary>
        /// Maximum Blood Charge stacks at a given rank, from the class_ability_bloodmage_charge_stack_cap_rN
        /// tunables (0 for rank 0 / unlearned or any rank outside 1-3).
        /// </summary>
        public static int StackCap(int rank, long capR1, long capR2, long capR3) => rank switch
        {
            1 => (int)capR1,
            2 => (int)capR2,
            3 => (int)capR3,
            _ => 0,
        };

        /// <summary>
        /// TRUE once more than expireSeconds have elapsed since the last stack was added - the same
        /// "now minus last touch, compared against the window" shape as FrenzyAbility's expiry check.
        /// </summary>
        public static bool IsExpired(double now, double lastAddTime, double expireSeconds) =>
            now - lastAddTime > expireSeconds;

        /// <summary>
        /// Sanguine Reserve's life-magic damage multiplier for one strike: 1.0 plus perStack per held
        /// charge (default +7% each, so a full 5-charge pool is 1.35). 1.0 when there are no charges.
        ///
        /// Negative inputs are floored to zero rather than rejected, so a mis-set tunable can never turn
        /// the ability into a damage PENALTY.
        ///
        /// <paramref name="gearPerStack"/> is the BLOOD CHARGE equipment mod (EquipmentModId.BloodCharge),
        /// added to the PER-CHARGE rate INSIDE the ramp - 1.0 + stacks * (perStack + gear) - so it sits on
        /// the ability's own additive axis and is multiplied by the same stack count rather than becoming a
        /// separate factor (DESIGN.md 3.3, the axis rule). It defaults to 0, and adding 0.0 to the rate is
        /// exact in IEEE arithmetic, so an unmodded build reproduces the previous multiplier bit-for-bit.
        ///
        /// The "no bonus at all" test is on the SUM rather than on perStack alone: gear must still pay out
        /// if the ability's own tunable is ever set to zero, and a negative sum must still floor to 1.0.
        /// </summary>
        public static float DamageMultiplier(int stacks, double perStack, double gearPerStack = 0.0)
        {
            if (stacks <= 0)
                return 1.0f;

            var perCharge = perStack + gearPerStack;

            if (perCharge <= 0.0)
                return 1.0f;

            return (float)(1.0 + stacks * perCharge);
        }

        /// <summary>
        /// The whole-number percentage a damage multiplier represents, for PLAYER-FACING TEXT ONLY:
        /// 1.35 -> 35, 2.05 -> 105. Never read by a damage calculation.
        ///
        /// It exists because the Blood Charge feedback the player now gets on every accumulating cast, the
        /// peak line, and the Exsanguinate burst line all have to quote the same number, and three separate
        /// inline round-and-scale expressions is how the three drift apart. A multiplier at or below 1.0
        /// reports 0 rather than a negative "bonus".
        /// </summary>
        public static int BonusPercent(double multiplier)
        {
            if (multiplier <= 1.0)
                return 0;

            return (int)Math.Round((multiplier - 1.0) * 100.0);
        }

        // ---- The LIFE HEALTH BOLT: a harmful life spell delivered as a plain Projectile ------------

        /// <summary>
        /// TRUE for a LIFE HEALTH BOLT: a Life Magic spell whose MetaSpellType is the plain
        /// <see cref="SpellType.Projectile"/> (not <see cref="SpellType.LifeProjectile"/>) and whose damage
        /// type carries <see cref="DamageType.Health"/>. Bloodstone Bolt I-VII (5525-5531) is the case this
        /// exists for: probed from portal.dat 2026-10-01 it is School LifeMagic, MetaSpellType Projectile,
        /// Category HealthLowering, and its ace_world spell row carries e_Type 128 (Health) with a fixed
        /// base_Intensity/variance damage roll rather than Hecatomb's drain_Percentage/damage_Ratio.
        ///
        /// WHY IT NEEDS ITS OWN PREDICATE. Every Sanguine Reserve hook keyed on LifeProjectile, and a
        /// Projectile-type spell is routed by SpellProjectile.CalculateDamage into the war/void branch, so
        /// Bloodstone Bolt neither built nor took Blood Charges (player report 2026-10-01). Owner ruling: it
        /// must do both - and it must NOT exsanguinate (2026-08-03: Exsanguinate is Hecatomb / Raven Fury).
        ///
        /// A property of the spell, not an id list. The same shape also matches the retail creature spells
        /// Blood Bolt (4266) and Exsanguinating Wave (3940, 3999); a player only reaches them through a
        /// cast it can actually perform, and they are the same kind of spell, so they get the same rules.
        /// War Magic, Void Magic and mana/stamina life spells are all false.
        /// </summary>
        public static bool IsLifeHealthBolt(MagicSchool school, SpellType metaSpellType, DamageType damageType) =>
            school == MagicSchool.LifeMagic
            && metaSpellType == SpellType.Projectile
            && (damageType & DamageType.Health) != 0;

        /// <summary>
        /// The Blood Charge factor a life health bolt applies at the war/void branch of
        /// SpellProjectile.CalculateDamage: the CHARGE TERM ONLY of the projectile's launch-time stamp
        /// (<see cref="LifeProjectileCastStamp.ChargeMultiplier"/>), never
        /// <see cref="LifeProjectileCastStamp.DamageMultiplier"/>. The stamp is constructed with Blood Price
        /// folded in, and that branch already applies Blood Price (and Resonance, Spellweave, Adrenaline)
        /// through Player.GetClassAbilitySpellDamageMod - taking DamageMultiplier here would apply Blood
        /// Price twice.
        ///
        /// 1.0 for every other spell, so a war or void bolt is untouched whatever its stamp says.
        /// </summary>
        public static float LifeHealthBoltChargeMultiplier(bool isLifeHealthBolt, LifeProjectileCastStamp castStamp) =>
            isLifeHealthBolt ? castStamp.ChargeMultiplier : 1.0f;

        /// <summary>
        /// Whether a landed life health bolt grants its caster a Blood Charge: only a life health bolt, only
        /// the aimed non-echo projectile (<paramref name="aimedNonEchoProjectile"/>, the caller's
        /// SpellProjectile.GrantsLifeProjectileCharge answer - one charge per CAST, the Hecatomb rule), and
        /// only when the cast's stamped outcome accrues. A life health bolt is resolved with
        /// exsanguinateEligible false, so its outcome is always Accrue; the outcome test is kept anyway so
        /// the grant rule reads the same at every site.
        /// </summary>
        public static bool LifeHealthBoltGrantsCharge(bool isLifeHealthBolt, bool aimedNonEchoProjectile, LifeProjectileCastStamp castStamp) =>
            isLifeHealthBolt && aimedNonEchoProjectile && castStamp.GrantsCharge;
    }
}
