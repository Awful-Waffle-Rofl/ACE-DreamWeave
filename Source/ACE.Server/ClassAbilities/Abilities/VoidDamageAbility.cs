using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T2: a flat void-spell damage bonus per rank (+8%/rank, +24% at rank 3) - the deep
    /// damage payoff of the void line. Bespoke: applied as a multiplier on the finalDamage of a void
    /// SpellProjectile via Player.GetClassAbilitySpellDamageMod. Carries IPassiveStatAbility.
    ///
    /// AFFINITY: Void Magic MULTIPLIES this ability's own rank bonus, at the SHARED affinity rate
    /// (class_ability_affinity_rate_per_trained / _per_spec) via the single-argument
    /// GetClassAbilityAffinityMultiplier(Skill) overload. Void Damage used to carry its own off-standard
    /// rate pair (class_ability_affinity_voiddamage_rate_per_trained / _per_spec, 0.20 / 0.28); the
    /// 2026-10-02 owner ruling retired those keys rather than merely restating the shared values in
    /// them, specifically so a future change to the shared pair reaches Void Damage with no per-ability
    /// follow-up - see
    /// Database/Updates/Shard/2026-10-02-01-Retire-SoulJump-VoidDamage-Affinity-Rate-Keys.sql. At zero
    /// effective Void Magic the factor is exactly 1.0 and the damage is bit-identical to rank alone.
    ///
    /// Wired 2026-09-12 on the repo owner's call. Those two tunables had been registered since the overhaul
    /// landed but had NO consumer, while this handler declared no AffinitySkill and described itself as
    /// deliberately unscaled - the signed-off design (tools/ca-workbench/workspace.json rev 50) declares
    /// affinitySkill VoidMagic for it, so the code was the side that was wrong.
    ///
    /// STILL SHORT OF rev 50 in two respects, both deliberately left alone rather than folded in here: that
    /// design also raises the base to 10%/rank (+30% at rank 3) and adds a 12%/rank variant against targets
    /// carrying one of the caster's void damage-over-time spells. Neither is implemented; only the affinity
    /// was authorised. Do not "finish" either one without asking - they move damage numbers.
    /// </summary>
    public class VoidDamageAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.VoidDamage,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 2,
            Name = "voiddamage",
            DisplayName = "Void Damage",
            Description = "Increases your void-magic damage by 8% per rank (+24% at rank 3). " +
                          "Higher Void Magic increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.VoidMagic,
        };

        /// <summary>
        /// The void-damage multiplier at a given rank (1.0 = none): 1 + rank*perRank + the Void Magic
        /// affinity term + the equipment-mod term.
        ///
        /// <paramref name="voidMagicFraction"/> is the AMOUNT the affinity multiplier adds to the rank
        /// bonus (rankBonus * multiplier - rankBonus), not the multiplier itself, so this stays a pure sum
        /// in one unit - the same shape OverchannelAbility.DamageMultiplier and
        /// EmpoweredSummonsAbility.StatMultiplier use.
        ///
        /// <paramref name="gearModFraction"/> is the Void Damage equipment mod, a STANDALONE mod: same
        /// axis, additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction. Both the
        /// rank bonus and the affinity term are rank-gated together, so an unlearned ability contributes
        /// nothing however high Void Magic is. Defaults to 0, which reproduces the pre-equipment-mod
        /// behavior exactly.
        /// </summary>
        public static float DamageMultiplier(int rank, double percentPerRank, double voidMagicFraction = 0.0, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, voidMagicFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms fed into DamageMultiplier above (x100 for display) - the
        /// three must stay in step.
        ///
        /// Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three displayed
        /// terms stay in one unit and still sum to Effective. Nothing clamps this ability, so CapNote is
        /// always null.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_voiddamage_percent_per_rank").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.VoidMagic);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.VoidDamage) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "void dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
