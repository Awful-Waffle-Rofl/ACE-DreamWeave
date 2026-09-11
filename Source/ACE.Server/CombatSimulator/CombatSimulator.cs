using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Pure. Given an attacker and a defender profile, returns the expected damage and the
    /// derived time to kill. No database, no Player, no world-thread hazard.
    /// </summary>
    public static class CombatSimulator
    {
        /// <summary>
        /// The crit chance as a PROBABILITY. This is not defensive programming: it reproduces the
        /// engine's own consumer, which saturates. DamageEvent decides a crit with
        /// "if (CriticalChance > ThreadSafeRandom.Next(0.0f, 1.0f))" (Entity\DamageEvent.cs:302), so
        /// a chance of 1.1 crits exactly as often as 1.0 and a negative one never crits.
        ///
        /// The engine therefore never needs a clamp of its own, and does not have one - a crit rating
        /// is added raw as "rating * 0.01f" with no bound (WorldObjects\WorldObject_Weapon.cs:365),
        /// and a CritRating of 100 on a weenie yields 1.1. A closed form is not so forgiving: the
        /// non-crit term below is (1 - CritChance), which goes NEGATIVE above 1.0 and quietly
        /// subtracts from the mean. Saturating here keeps every caller - Analytic, Sample and
        /// TimeToKillSolver through them - in step with what the engine's roll would actually do.
        /// </summary>
        private static float EffectiveCritChance(AttackerSpec spec)
        {
            if (spec.CritChance < 0.0f)
                return 0.0f;

            if (spec.CritChance > 1.0f)
                return 1.0f;

            return spec.CritChance;
        }

        public static SimResult Analytic(AttackerSpec spec, DefenderProfile profile)
        {
            var hitChance = HitChance.For(spec, profile);

            var mitigation = MeanMitigation(spec, profile);

            var expectedBase = (spec.BaseDamageMin + spec.BaseDamageMax) / 2.0f;

            var critChance = EffectiveCritChance(spec);

            // the crit branch recomputes from MAX damage, not from the rolled value
            var normalTerm = (1.0f - critChance) * expectedBase * spec.PreMitigationMod;
            var critTerm = critChance * spec.BaseDamageMax * spec.PreMitigationMod * spec.CritDamageMod;

            var meanDamage = (normalTerm + critTerm) * mitigation;

            var landedHitsToKill = meanDamage > 0.0f ? profile.MaxHealth / meanDamage : float.PositiveInfinity;
            var swingsToKill = hitChance > 0.0f ? landedHitsToKill / hitChance : float.PositiveInfinity;

            return new SimResult
            {
                Label = profile.Label,
                Level = profile.Level,
                MaxHealth = profile.MaxHealth,
                HitChance = hitChance,
                MeanDamagePerLandedHit = meanDamage,
                LandedHitsToKill = landedHitsToKill,
                SwingsToKill = swingsToKill,
            };
        }

        /// <summary>
        /// Monte Carlo over the same factors, for percentiles and distribution shape. The mean
        /// it produces must agree with Analytic; that agreement is the test that the closed form
        /// is right. Takes an explicit Random so runs are reproducible - do not use
        /// ThreadSafeRandom here.
        /// </summary>
        public static SimResult Sample(AttackerSpec spec, DefenderProfile profile, int iterations, System.Random rng)
        {
            var bodyParts = BodyPartsFor(spec.AttackHeight);

            var shieldMod = MitigationMath.ShieldMod(profile, spec);
            var resistanceMod = MitigationMath.ResistanceMod(profile, spec);
            var drrMod = MitigationMath.DamageResistRatingMod(profile, spec);

            var samples = new List<float>(iterations);

            // same saturation as Analytic, and for the same reason - see EffectiveCritChance
            var critChance = EffectiveCritChance(spec);

            for (var i = 0; i < iterations; i++)
            {
                var bodyPart = bodyParts[rng.Next(bodyParts.Count)];
                var armorMod = MitigationMath.ArmorMod(profile, spec, bodyPart);

                var isCrit = rng.NextDouble() < critChance;

                float preMitigation;

                if (isCrit)
                    preMitigation = spec.BaseDamageMax * spec.PreMitigationMod * spec.CritDamageMod;
                else
                    preMitigation = (spec.BaseDamageMin + (float)rng.NextDouble() * (spec.BaseDamageMax - spec.BaseDamageMin)) * spec.PreMitigationMod;

                samples.Add(preMitigation * armorMod * shieldMod * resistanceMod * drrMod);
            }

            samples.Sort();

            var mean = samples.Sum() / samples.Count;
            var hitChance = HitChance.For(spec, profile);

            var landedHitsToKill = mean > 0.0f ? profile.MaxHealth / mean : float.PositiveInfinity;

            return new SimResult
            {
                Label = profile.Label,
                Level = profile.Level,
                MaxHealth = profile.MaxHealth,
                HitChance = hitChance,
                MeanDamagePerLandedHit = mean,
                P50Damage = samples[samples.Count / 2],
                P90Damage = samples[(int)(samples.Count * 0.9)],
                LandedHitsToKill = landedHitsToKill,
                SwingsToKill = hitChance > 0.0f ? landedHitsToKill / hitChance : float.PositiveInfinity,
            };
        }

        /// <summary>
        /// The magic path has NO armor term. Never consult the physical armor table here - doing
        /// so would credit the defender with armor the engine does not apply to spell damage.
        ///
        /// THE MAGIC CRIT IS A DIFFERENT SHAPE FROM THE MELEE ONE, and this is the whole reason
        /// the method exists rather than a flag on Analytic. The melee rule replaces the rolled
        /// base with BaseDamageMax * CritDamageMod. Magic does not replace anything: it ADDS a
        /// bonus to the ordinary roll. In PvE the bonus is half the spell's MAXIMUM damage
        /// (SpellProjectile.cs:638; the PvP arm at :636 uses half the MINIMUM and is not modelled
        /// here), scaled by the weapon crit damage mod (:646-648), and the sum is
        /// "finalDamage = baseDamage + critDamageBonus + skillBonus" (:683) over a base rolled
        /// uniformly across the whole range regardless of the crit (:665).
        ///
        /// So the closed form is
        ///
        ///     E[damage] = ( E[base] + critChance * BaseDamageMax * 0.5 * MagicCritDamageMod )
        ///                 * PreMitigationMod * mitigation
        ///
        /// with NO (1 - critChance) weighting on the base term, because the base roll happens on
        /// every hit. Using the melee shape here overstated the mean: for a 100-200 spell at 10
        /// percent crit it charged the crit branch 200 * CritDamageMod in place of the roll,
        /// instead of adding 200 * 0.5 * MagicCritDamageMod on top of it.
        ///
        /// MagicCritDamageMod is a SEPARATE field from CritDamageMod and the two are not
        /// interchangeable - see AttackerSpec for the +1.0 base that one carries and this one
        /// does not.
        ///
        /// WHAT THIS DOES NOT MODEL, stated plainly because the mitigation product below is
        /// shorter than the engine's and a reader will otherwise assume parity. Mitigation here is
        /// exactly two terms: the defender's resistance (SpellProjectile.cs:672) and the damage
        /// resist rating (:880, applied at :900). Everything else in the engine's chain is absent:
        ///
        /// - The magic ABSORB term, absorbMod = GetAbsorbMod(target) (:534), a terminal factor of
        ///   the same multiply (:685). It is NOT neutral for the defender a DefenderProfile
        ///   describes: ProfileBuilder deliberately profiles a defender in MELEE STANCE, and in
        ///   melee stance a shield carrying AbsorbMagicDamage returns GetShieldMod, which is
        ///   Math.Min(1.0f, 1.0f - reduction) and so at most 1.0 (:711-720, :782). DefenderProfile
        ///   carries no absorb axis at all, so this is not a lookup that fails soft - there is
        ///   nothing to look up. Direction of the error: magic damage OVERSTATED against any
        ///   roster character holding a magic-absorbing shield.
        /// - The ELEMENTAL damage mod and the creature SLAYER mod (:549, :552), both attacker-side
        ///   factors of that same multiply, and the war-magic SKILL BONUS (:662), which is
        ///   added to the sum for a player caster. A caller could fold the first two into
        ///   PreMitigationMod, but nothing on this branch does, and there is no field for the
        ///   third. Direction: magic damage UNDERSTATED by whatever those would have contributed,
        ///   since each is at or above its neutral value.
        /// - The crit-path RECOMPOSITION of the two rating mods (:884-888), which additively
        ///   combines the crit damage rating and the defender's crit damage resist rating on a
        ///   crit only. The same structural limit the physical path has, and for the same reason:
        ///   AttackerSpec carries one PreMitigationMod for both branches.
        ///
        /// These are omissions of the SHIPPED implementation, not of the design - see
        /// Docs/CombatSimulator/DESIGN.md, "Damage composition", which names absorb, elemental and
        /// slayer as part of the magic stack.
        /// </summary>
        public static SimResult AnalyticMagic(AttackerSpec spec, DefenderProfile profile)
        {
            var hitChance = HitChance.For(spec, profile);

            var mitigation = MitigationMath.ResistanceMod(profile, spec)
                           * MitigationMath.DamageResistRatingMod(profile, spec);

            var expectedBase = (spec.BaseDamageMin + spec.BaseDamageMax) / 2.0f;

            // same saturation as Analytic, and for the same reason - see EffectiveCritChance
            var critChance = EffectiveCritChance(spec);

            // the base roll is UNCONDITIONAL: a magic crit adds to it rather than replacing it,
            // so there is no (1 - critChance) weighting on this term
            var baseTerm = expectedBase * spec.PreMitigationMod;
            var critTerm = critChance * spec.BaseDamageMax * 0.5f * spec.MagicCritDamageMod * spec.PreMitigationMod;

            var meanDamage = (baseTerm + critTerm) * mitigation;

            var landedHitsToKill = meanDamage > 0.0f ? profile.MaxHealth / meanDamage : float.PositiveInfinity;

            return new SimResult
            {
                Label = profile.Label,
                Level = profile.Level,
                MaxHealth = profile.MaxHealth,
                HitChance = hitChance,
                MeanDamagePerLandedHit = meanDamage,
                LandedHitsToKill = landedHitsToKill,
                SwingsToKill = hitChance > 0.0f ? landedHitsToKill / hitChance : float.PositiveInfinity,
            };
        }

        /// <summary>
        /// Armor depends on which body part is struck, and the engine picks uniformly within the
        /// attack height's band, so the expected mitigation is the unweighted mean across that
        /// band's parts. Collapsing to a single armor number would be wrong: a head hit and a
        /// chest hit can see disjoint armor sets.
        /// </summary>
        private static float MeanMitigation(AttackerSpec spec, DefenderProfile profile)
        {
            var bodyParts = BodyPartsFor(spec.AttackHeight);

            var shieldMod = MitigationMath.ShieldMod(profile, spec);
            var resistanceMod = MitigationMath.ResistanceMod(profile, spec);
            var drrMod = MitigationMath.DamageResistRatingMod(profile, spec);

            var total = 0.0f;

            foreach (var bodyPart in bodyParts)
                total += MitigationMath.ArmorMod(profile, spec, bodyPart);

            var meanArmorMod = bodyParts.Count > 0 ? total / bodyParts.Count : 1.0f;

            return meanArmorMod * shieldMod * resistanceMod * drrMod;
        }

        private static List<BodyPart> BodyPartsFor(AttackHeight attackHeight)
        {
            switch (attackHeight)
            {
                case AttackHeight.High: return BodyParts.GetFlags(BodyParts.Upper);
                case AttackHeight.Medium: return BodyParts.GetFlags(BodyParts.Mid);
                case AttackHeight.Low:
                default: return BodyParts.GetFlags(BodyParts.Lower);
            }
        }
    }
}
