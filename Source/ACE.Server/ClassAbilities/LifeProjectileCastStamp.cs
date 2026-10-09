namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The Blood Mage per-CAST answer a life projectile (Martyr's Hecatomb, Curse of Raven Fury) carries from
    /// the cast that launched it to its own impact: Blood Price's damage multiplier, the Blood Charge ramp or
    /// Exsanguinate burst multiplier, and which of the two the cast resolved to
    /// (<see cref="BloodChargeCastOutcome"/>). A LIFE HEALTH BOLT (Bloodstone Bolt - see
    /// BloodChargeMath.IsLifeHealthBolt) carries the same stamp but reads only its charge term
    /// (<see cref="ChargeMultiplier"/>), because it lands through the war/void damage branch, which applies
    /// Blood Price on its own.
    ///
    /// WHY IT RIDES THE PROJECTILE (2026-09-22, PR #1271 review). Player.ApplyBloodPriceCost and
    /// Player.ApplyLifeProjectileBloodCharge resolve these once per cast into player fields, and every
    /// projectile used to read those LIVE fields at its own impact - so a projectile still in flight took the
    /// stamp of whatever the caster cast MOST RECENTLY. That was a known, documented race for ordinary
    /// projectiles, and Echo Cast widened it: an echo launches at its parent's impact, so its exposure is the
    /// parent's flight plus its own, and a second life cast in between (an Exsanguinate burst, say) handed the
    /// echo a burst multiplier and resistance-ignore it never earned. Now each projectile snapshots the stamp
    /// at launch (WorldObject_Magic.LaunchSpellProjectiles), the same pattern as LifeProjectileDamage and
    /// SpellweaveDamageMod, and an echo copies its parent's snapshot (EchoCastAbility.StampEchoCopy).
    ///
    /// A value type with a neutral <see cref="None"/>; a zero multiplier (default(LifeProjectileCastStamp))
    /// is read as the neutral 1.0 rather than zeroing the damage, so an unstamped projectile behaves as a cast
    /// with no Blood Mage terms at all.
    /// </summary>
    public readonly struct LifeProjectileCastStamp
    {
        public static readonly LifeProjectileCastStamp None = new LifeProjectileCastStamp(1.0f, 1.0f, BloodChargeCastOutcome.Accrue);

        public LifeProjectileCastStamp(float bloodPriceMod, float chargeMod, BloodChargeCastOutcome outcome)
        {
            BloodPriceMod = bloodPriceMod;
            ChargeMod = chargeMod;
            Outcome = outcome;
        }

        /// <summary>Blood Price's damage multiplier for the launching cast (1.0 = none).</summary>
        public float BloodPriceMod { get; }

        /// <summary>The launching cast's Blood Charge ramp, or its Exsanguinate burst (1.0 = none).</summary>
        public float ChargeMod { get; }

        /// <summary>Whether the launching cast built the Blood Charge pool or spent it.</summary>
        public BloodChargeCastOutcome Outcome { get; }

        /// <summary>
        /// The per-cast damage multiplier: Blood Price x the Blood Charge term. Unset (non-positive) factors
        /// read as 1.0.
        /// </summary>
        public float DamageMultiplier => (BloodPriceMod > 0.0f ? BloodPriceMod : 1.0f) * ChargeMultiplier;

        /// <summary>
        /// The Blood Charge term ALONE (unset reads as 1.0), without Blood Price. Read by a life health bolt
        /// (Bloodstone Bolt) at the war/void branch of SpellProjectile.CalculateDamage, which already applies
        /// Blood Price through Player.GetClassAbilitySpellDamageMod - see
        /// BloodChargeMath.LifeHealthBoltChargeMultiplier.
        /// </summary>
        public float ChargeMultiplier => ChargeMod > 0.0f ? ChargeMod : 1.0f;

        /// <summary>TRUE when the launching cast exsanguinated (so the resistance-ignore applies).</summary>
        public bool Bursts => ExsanguinateMath.Bursts(Outcome);

        /// <summary>TRUE when the launching cast may grant a Blood Charge on landing.</summary>
        public bool GrantsCharge => ExsanguinateMath.GrantsCharge(Outcome);
    }
}
