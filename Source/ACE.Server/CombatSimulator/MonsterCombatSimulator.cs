using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// The analytic player-to-monster path: an AttackerSpec (normally from PlayerAttackerSpecs) against a
    /// MonsterDefenderProfile. Pure - no database, no WorldObject, no world-thread hazard. The damage shapes are the
    /// ones CombatSimulator.Analytic (physical: a crit REPLACES the roll with BaseDamageMax * CritDamageMod) and
    /// CombatSimulator.AnalyticMagic (magic: a crit ADDS BaseDamageMax * 0.5 * MagicCritDamageMod to every roll,
    /// plus the player skill bonus) use; only the mitigation lookups differ, because a creature defender takes the
    /// creature arms of DamageEvent.DoCalculateDamage and SpellProjectile.CalculateDamage.
    ///
    /// WHAT THIS APPROXIMATES, for the table to disclose:
    /// - Hit location. The engine rolls a body part from the quadrant "attack height | relative direction"
    ///   (DamageEvent.GetQuadrant), and the direction is whichever of left/right and front/back the attacker
    ///   stands at. This assumes a FRONTAL attack and averages the left-front and right-front quadrants of the
    ///   spec's attack height equally. A quadrant with no body parts is an engine evade, so it lowers the reported
    ///   hit chance rather than the damage.
    /// - Hollow is on or off (AttackerSpec booleans), so a partial-intensity weapon is simulated as full.
    /// - Not modelled at all: monster combat effect avoidance (rolled before the evade) and absorption (wards and
    ///   the like), Overpower, the magic absorb term (a monster's shield or caster AbsorbMagicDamage), anything in
    ///   Creature.TakeDamage, and the post-mitigation player terms listed on PlayerAttackerSpecs.
    /// </summary>
    public static class MonsterCombatSimulator
    {
        private static readonly QuadrantIndex[][] FrontalQuadrants =
        {
            new[] { QuadrantIndex.HLF, QuadrantIndex.HRF },
            new[] { QuadrantIndex.MLF, QuadrantIndex.MRF },
            new[] { QuadrantIndex.LLF, QuadrantIndex.LRF },
        };

        public static SimResult Analytic(AttackerSpec spec, MonsterDefenderProfile profile)
        {
            if (spec == null)
                throw new ArgumentNullException(nameof(spec));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var defenseSkill = profile.DefenseSkills != null && profile.DefenseSkills.TryGetValue(spec.CombatType, out var skill) ? skill : 0u;

            var hitChance = HitChance.Calculate(spec.AttackSkill, defenseSkill);

            var critChance = Math.Clamp(spec.CritChance, 0.0f, 1.0f);

            var expectedBase = (spec.BaseDamageMin + spec.BaseDamageMax) / 2.0f;

            float meanDamage;

            if (spec.CombatType == CombatType.Magic)
            {
                // SpellProjectile.CalculateDamage: no armor and no shield term on a spell; resistance, then the
                // damage resist rating in DamageTarget
                var mitigation = ResistanceMod(profile, spec) * DamageResistRatingMod(profile, spec);

                var baseTerm = (expectedBase + spec.MagicSkillBonus) * spec.PreMitigationMod;
                var critTerm = critChance * spec.BaseDamageMax * 0.5f * spec.MagicCritDamageMod * spec.PreMitigationMod;

                meanDamage = (baseTerm + critTerm) * mitigation;
            }
            else
            {
                var armor = MeanArmorMod(profile, spec, out var landingFraction);

                // a quadrant with no body part is an engine evade (DamageEvent.GetBodyPart sets Evaded)
                hitChance *= landingFraction;

                var mitigation = armor * ShieldMod(profile, spec) * ResistanceMod(profile, spec) * DamageResistRatingMod(profile, spec);

                var normalTerm = (1.0f - critChance) * expectedBase * spec.PreMitigationMod;
                var critTerm = critChance * spec.BaseDamageMax * spec.PreMitigationMod * spec.CritDamageMod;

                meanDamage = (normalTerm + critTerm) * mitigation;
            }

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
        /// The expected armor mod over the frontal quadrants of the spec's attack height, each part weighted by its
        /// share of that quadrant's total, exactly as BodyPartTable.RollBodyPart draws. landingFraction is the share
        /// of those quadrants that hold any body part at all; the mean is over the landing ones only.
        /// </summary>
        internal static float MeanArmorMod(MonsterDefenderProfile profile, AttackerSpec spec, out float landingFraction)
        {
            var heightIndex = spec.AttackHeight == AttackHeight.High ? 0 : spec.AttackHeight == AttackHeight.Low ? 2 : 1;

            var quadrants = FrontalQuadrants[heightIndex];

            var total = 0.0f;
            var landing = 0;

            foreach (var quadrant in quadrants)
            {
                var weights = profile.QuadrantWeights != null && (int)quadrant < profile.QuadrantWeights.Count ? profile.QuadrantWeights[(int)quadrant] : null;

                if (weights == null || weights.Count == 0)
                    continue;

                var sum = 0.0f;

                foreach (var w in weights)
                    sum += w.Value;

                if (sum <= 0.0f)
                    continue;

                var quadrantMean = 0.0f;

                foreach (var w in weights)
                    quadrantMean += w.Value / sum * ArmorMod(profile, spec, w.Key);

                total += quadrantMean;
                landing++;
            }

            landingFraction = (float)landing / quadrants.Length;

            return landing > 0 ? total / landing : 1.0f;
        }

        /// <summary>
        /// Creature_BodyPart.GetArmorMod: rending applied to a positive level, then CalcArmorMod; IgnoreAllArmor
        /// overrides to 1.0 (DamageEvent.DoCalculateDamage). Fails soft to 1.0 on a missing row.
        /// </summary>
        internal static float ArmorMod(MonsterDefenderProfile profile, AttackerSpec spec, CombatBodyPart bodyPart)
        {
            if (spec.IgnoreAllArmor)
                return 1.0f;

            if (profile.EffectiveArmorLevel == null || !profile.EffectiveArmorLevel.TryGetValue(new MonsterArmorKey(bodyPart, spec.DamageType, spec.IgnoreMagicArmor, spec.IgnoreMagicResist), out var effectiveAl))
                return 1.0f;

            if (effectiveAl > 0)
                effectiveAl *= spec.ArmorRendingMod;

            return SkillFormula.CalcArmorMod(effectiveAl);
        }

        /// <summary>The creature's GetShieldMod past the cap: the attacker's ignore-shield term, then the curve.</summary>
        internal static float ShieldMod(MonsterDefenderProfile profile, AttackerSpec spec)
        {
            if (!spec.WithinShieldArc || spec.IgnoreAllArmor)
                return 1.0f;

            if (profile.Shield == null || !profile.Shield.TryGetValue(new ShieldKey(spec.DamageType, spec.IgnoreMagicArmor), out var row))
                return 1.0f;

            return SkillFormula.CalcArmorMod(row.CappedEffectiveLevel * spec.IgnoreShieldMod);
        }

        /// <summary>
        /// Math.Max(0, Natural * inner), inner as documented on MonsterResistanceRow. Full hollow on a creature
        /// defender bypasses protection and vulnerability for any attacker (HollowMath.IsFullResistBypass, non-PvP).
        /// </summary>
        internal static float ResistanceMod(MonsterDefenderProfile profile, AttackerSpec spec)
        {
            if (profile.Resistance == null || !profile.Resistance.TryGetValue(spec.DamageType, out var row))
                return spec.WeaponResistanceMod;

            float inner;

            if (spec.IgnoreMagicResist)
            {
                inner = spec.WeaponResistanceMod * row.HuntersMark;
            }
            else
            {
                var vulnMod = row.V0;

                if (vulnMod < spec.WeaponResistanceMod)
                    vulnMod = spec.WeaponResistanceMod;

                inner = row.P0 * vulnMod * row.HuntersMark;
            }

            return Math.Max(0.0f, row.Natural * inner);
        }

        internal static float DamageResistRatingMod(MonsterDefenderProfile profile, AttackerSpec spec)
        {
            if (profile.DamageResistRatingMod == null || !profile.DamageResistRatingMod.TryGetValue(spec.CombatType, out var mod))
                return 1.0f;

            return mod;
        }
    }
}
