using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2 game-changer, the stamina-side twin of the Archmage mana surcharges: +6/12/18% melee
    /// damage, but each swing costs extra stamina and does nothing if the player can't pay it. Scales with
    /// Weapon Tinkering (a tuned weapon transfers force). Always-on and reliable - a strong damage GC on par
    /// with Deadeye (Frenzy took the Berserker T1 GC slot, user 2026-07-16).
    /// </summary>
    public class SavageBlowsAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SavageBlows,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "savageblows",
            DisplayName = "Savage Blows",
            Description = "Your melee attacks hit for 6% more per rank but each swing costs extra stamina. " +
                          "Higher Weapon Tinkering increases the bonus; with insufficient stamina the swing lands normally.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },   // Berserker T2 GC: 3 flat, on par with the other T2 damage GCs (Deadeye)
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Melee)
                return;

            // Savage Blows equipment mod (STANDALONE): deliberately read OUTSIDE the stamina gate below.
            // The surcharge is the ABILITY's cost mechanic, not the mod's - a modded player who never learned
            // Savage Blows pays nothing for it (the rank-0 path in Player.ApplyEquipmentModOutgoingDamage
            // charges no stamina), so an owner running dry must not silently lose their gear bonus too.
            // Contrast Heavy Draw, whose mod IS machinery and therefore sits inside its stamina gate.
            var gearMod = attacker.GetEquippedModValue(EquipmentModId.SavageBlows);

            var abilityBonus = 0.0;

            var cost = (int)PropertyManager.GetDouble("class_ability_savageblows_stamina_cost").Item;
            if (cost <= 0 || attacker.Stamina.Current >= cost)
            {
                if (cost > 0)
                    attacker.UpdateVitalDelta(attacker.Stamina, -cost);

                abilityBonus = rank * PropertyManager.GetDouble("class_ability_savageblows_percent_per_rank").Item
                    + attacker.GetClassAbilityScaling(Skill.WeaponTinkering,
                        PropertyManager.GetDouble("class_ability_savageblows_wtink_per_trained").Item,
                        PropertyManager.GetDouble("class_ability_savageblows_wtink_per_spec").Item) * 0.01;
            }

            var bonus = abilityBonus + gearMod;
            if (bonus <= 0.0)
                return;

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms in ModifyOutgoingDamage above exactly (x100 for display,
        /// no unit conversion beyond that) - the two must stay in step. Deliberately ignores the stamina
        /// gate: this is a "how strong is the ability" readout, not a "will it fire on the next swing"
        /// simulation, so it always reports the bonus as if the stamina cost were paid.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_savageblows_percent_per_rank").Item * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.WeaponTinkering,
                PropertyManager.GetDouble("class_ability_savageblows_wtink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_savageblows_wtink_per_spec").Item) * 0.01 * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.SavageBlows) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "melee dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
