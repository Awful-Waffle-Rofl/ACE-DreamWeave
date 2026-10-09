using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T3 game-changer: a constant +5% attack-animation-speed increase per rank (+15% at rank 3),
    /// weapon-agnostic - which is exactly why it sits at Tier 3. Scales with Lockpick (nimble fingers).
    /// Stacks with Frenzy under the shared attack-speed ceiling (class_ability_attack_speed_ceiling).
    ///
    /// Bespoke, not a hook: like Frenzy it is applied in Creature.GetAnimSpeed (via
    /// Player.ApplyClassAbilityAttackSpeed, which composes it with Frenzy under the one ceiling). Carries
    /// IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class AttackSpeedAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.AttackSpeed,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 3,
            Name = "attackspeed",
            DisplayName = "Attack Speed",
            Description = "Permanently increases your attack speed by 6% per rank (+18% at rank 3), any weapon. " +
                          "Higher Lockpick increases the bonus. Stacks with Frenzy up to the attack-speed ceiling.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            AffinitySkill = Skill.Lockpick,
        };

        /// <summary>
        /// The constant attack-speed multiplier at a given rank (1.0 = none): 1 + rank*perRank + the
        /// Lockpick rider fraction + the equipment-mod term. Pure for testability.
        ///
        /// <paramref name="gearModFraction"/> is the Attack Speed equipment mod, a STANDALONE mod: it sits
        /// inside the same parenthesis as the ability's own terms (same axis, additive) and is deliberately
        /// NOT gated on rank, so at rank 0 this degenerates to 1 + gearModFraction. The ability's per-rank
        /// base and its Lockpick rider remain rank-gated, so a player without the ability gains nothing but
        /// the mod itself. Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float AttackSpeedMultiplier(int rank, double percentPerRank, double lockpickFraction, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, lockpickFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the terms Player.GetAttackSpeedSkillMod (Player_ClassAbilityBuffs.cs) feeds into
        /// AttackSpeedMultiplier above - the two must stay in step. Composes with Frenzy under the same
        /// shared attack-speed ceiling (class_ability_attack_speed_ceiling); as with FrenzyAbility's own
        /// GetReadout, this calls the real composed methods and ApplyClassAbilityAttackSpeed against a
        /// reference base anim speed rather than restating the clamp, so CapNote reflects whether the
        /// ceiling is ACTUALLY clipping for this player right now.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_attackspeed_percent_per_rank").Item;

            var rankBonus = rank * perRank;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three
            // displayed terms stay in one unit and still sum to Effective.
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Lockpick);

            var gearMod = player.GetEquippedModValue(EquipmentModId.AttackSpeed);

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            var gear = gearMod * 100.0;
            var total = skill + affinity + gear;

            var referenceBaseAnimSpeed = (float)Creature.MaxAttackSpeed;
            var composedMod = player.GetFrenzyAttackSpeedMod() * player.GetAttackSpeedSkillMod() * player.GetWeaponModAttackSpeedMod();
            var uncappedSpeed = referenceBaseAnimSpeed * composedMod;
            var appliedSpeed = player.ApplyClassAbilityAttackSpeed(referenceBaseAnimSpeed);
            var ceilingBit = appliedSpeed < uncappedSpeed - 0.0001f;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "atk speed",
                Per = null,
                CapNote = ceilingBit ? "anim ceiling" : null,
            };
        }
    }
}
