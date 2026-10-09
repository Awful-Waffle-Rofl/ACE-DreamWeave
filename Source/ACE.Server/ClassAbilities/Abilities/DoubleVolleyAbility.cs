using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T3 game-changer: a 6/12/18% chance (by rank) that a missile volley immediately fires a full
    /// second volley at the same targets, at no ammo/stamina cost. Bespoke: rolled in Player.LaunchMissile
    /// via Player.TryClassAbilityDoubleVolley; the second volley reuses the cleave-style multi-shot arrow
    /// machinery. Carries IPassiveStatAbility because it hooks no combat event of its own.
    ///
    /// 2026-09-29: gained an UNCAPPED Run affinity rider (Fast Aim's redefinition companion change) -
    /// higher effective Run multiplies this ability's own rank bonus, same multiply-then-clamp-the-delta
    /// idiom as PinningShotAbility.Chance, but with the cap argument passed as 0 (uncapped) rather than the
    /// shared class_ability_affinity_chance_cap.
    /// </summary>
    public class DoubleVolleyAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.DoubleVolley,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 3,
            Name = "doublevolley",
            DisplayName = "Double Volley",
            Description = "Your missile volleys have a 6/12/18% chance (by rank) to immediately fire a full " +
                          "second volley at the same targets - no ammo, no stamina. Higher Run multiplies " +
                          "the chance, uncapped.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Run,
        };

        /// <summary>
        /// The re-fire chance at a given rank: (base + step*(rank-1)) scaled by the Run affinity multiplier,
        /// UNCAPPED (pass 0 for <paramref name="affinityCap"/>), plus the equipment-mod term. Ranks 1-3 with
        /// the 0.06 defaults and a neutral (1.0) multiplier -> 6/12/18%. Pure for testability. Returns 0 for
        /// rank &lt;= 0.
        ///
        /// <paramref name="affinityMultiplier"/> is what Player.GetClassAbilityAffinityMultiplier(Skill.Run)
        /// returns: a factor &gt;= 1.0, exactly 1.0 at zero effective Run. Floored at 1.0 here too (matching
        /// PinningShotAbility.Chance), so a caller handing over 0.0 - the old additive primitive's neutral
        /// value - degrades to rank-only rather than multiplying the whole bonus away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS. This ability is
        /// deliberately UNCAPPED: the caller always passes 0 here, which this method (and
        /// PinningShotAbility.Chance's own convention) treats as "no ceiling".
        ///
        /// <paramref name="gearModChance"/> is the Double Volley equipment mod, a MACHINERY mod: it
        /// amplifies the ability's own roll and is therefore gated behind the same rank check - a player
        /// without Double Volley gets nothing from it, because there is no volley re-fire to boost.
        /// Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;

            var added = rankBonus * Math.Max(1.0, affinityMultiplier) - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            var gearBonus = Math.Max(0.0, gearModChance);

            return (float)Math.Max(0.0, rankBonus + added + gearBonus);
        }

        /// <summary>
        /// Mirrors the terms Player.TryClassAbilityDoubleVolley (Player_ClassAbilities.cs) feeds into
        /// Chance above - the two must stay in step. Reports the (uncapped, so always equal to the raw)
        /// affinity contribution, so Skill + Affinity + Gear sums to Effective exactly.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.Run) ?? 1.0;
            var affinity = rank <= 0 ? 0.0 : Math.Max(0.0, rankBonus * Math.Max(1.0, multiplier) - rankBonus);

            var gear = player.GetEquippedModValue(EquipmentModId.DoubleVolley) * 100.0;

            var skill = rankBonus * 100.0;
            var affinityPct = affinity * 100.0;
            var effective = Chance(rank, chanceBase, chanceStep, multiplier, 0.0, player.GetEquippedModValue(EquipmentModId.DoubleVolley)) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinityPct,
                Gear = gear,
                Effective = effective,
                Unit = "%",
                Label = "re-fire",
                Per = null,
                CapNote = null,
            };
        }
    }
}
