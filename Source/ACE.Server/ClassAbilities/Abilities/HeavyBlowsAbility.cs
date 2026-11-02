using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2 (Berserker pass 2026-10-24, owner ruling; replaces the retired Savage Blows): stretches the
    /// melee power bar. Retail's melee power mod is 0.5 + PowerLevel (Player.GetPowerMod); with Heavy Blows it
    /// becomes 0.5 + PowerLevel x (1 + 0.25 x rank x affinity), so a full-power swing at rank 3 with no
    /// affinity hits at 0.5 + 1.75 = 2.25 instead of 1.5, while a zero-power swing is unchanged. Weapon
    /// Tinkering affinity multiplies the stretch. Melee only.
    ///
    /// APPLIED PvE-ONLY THROUGH THE OUTGOING-DAMAGE HOOK, NOT BY EDITING THE SHARED Player.GetPowerMod (owner
    /// ruling): GetPowerMod also feeds PvP and every other reader, and class abilities are PvE only. Damage is
    /// linear in PowerMod (DamageEvent: BaseDamage x AttributeMod x PowerMod x ... and the crit path likewise),
    /// so scaling the landed Damage by stretched / retail power mod is exactly the stretched swing.
    ///
    /// EQUIPMENT MOD 17 (was Savage Blows, repurposed in place): the same flat melee percent it always was,
    /// applied on top as Damage x (1 + gear). A non-owner gets it through the rank-0 path in
    /// Player.ApplyEquipmentModOutgoingDamage; an owner gets it here, so it is never counted twice.
    /// </summary>
    public class HeavyBlowsAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public const string StretchKey = "class_ability_heavyblows_stretch_per_rank";

        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.HeavyBlows,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "heavy_blows",
            DisplayName = "Heavy Blows",
            Description = "Your power bar hits harder in melee. A full-power swing normally deals 150% damage; Heavy " +
                          "Blows raises that to 175% / 200% / 225% at ranks 1 / 2 / 3. Lower power settings gain " +
                          "less, and a zero-power swing is unchanged. Higher Weapon Tinkering increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.WeaponTinkering,
        };

        /// <summary>
        /// The damage ratio of a stretched swing to the retail one, for a retail power mod (0.5 + PowerLevel).
        /// 1.0 for a non-positive power mod, rank 0 or a non-positive stretch. Pure.
        /// </summary>
        public static double PowerRatio(double retailPowerMod, int rank, double stretchPerRank, double affinity)
        {
            if (retailPowerMod <= 0.0 || rank <= 0 || stretchPerRank <= 0.0)
                return 1.0;

            var powerLevel = Math.Max(0.0, retailPowerMod - 0.5);

            var stretched = 0.5 + powerLevel * (1.0 + stretchPerRank * rank * affinity);

            return stretched / retailPowerMod;
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Melee)
                return;

            var ratio = PowerRatio(damageEvent.PowerMod, rank,
                PropertyManager.GetDouble(StretchKey).Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.WeaponTinkering));

            var gearMod = attacker.GetEquippedModValue(EquipmentModId.SavageBlows);

            var total = ratio * (1.0 + gearMod);

            if (total <= 1.0)
                return;

            damageEvent.Damage *= (float)total;
        }

        /// <summary>
        /// Reports the bonus of a FULL-POWER swing (PowerLevel 1.0, retail power mod 1.5) as the primary, the
        /// same number ModifyOutgoingDamage produces at full power. Affinity is reported as the amount it adds.
        /// Gear is the flat mod-17 percent.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var stretch = PropertyManager.GetDouble(StretchKey).Item;
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.WeaponTinkering);

            var skill = (PowerRatio(1.5, rank, stretch, 1.0) - 1.0) * 100.0;
            var withAffinity = (PowerRatio(1.5, rank, stretch, multiplier) - 1.0) * 100.0;
            var affinity = withAffinity - skill;
            var gear = player.GetEquippedModValue(EquipmentModId.SavageBlows) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = skill + affinity + gear,
                Unit = "%",
                Label = "full-power melee dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
