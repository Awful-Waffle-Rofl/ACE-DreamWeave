using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2 game-changer, the stamina-side twin of the Archmage mana surcharges: +8/16/24% melee
    /// damage, but each swing costs extra stamina and does nothing if the player can't pay it. Scales with
    /// Weapon Tinkering (a tuned weapon transfers force). Always-on and reliable - a strong damage GC on par
    /// with Deadeye (Frenzy took the Berserker T1 GC slot, user 2026-07-16).
    ///
    /// The stamina surcharge is paid ONCE per swing, in Player.ApplySavageBlowsStamina (called from
    /// Player_Melee.Attack before the swing's strike loop), not per hit here - a cleave, a multi-strike
    /// swing, or Kinetic Charge's extra cleave all land free of any further charge once the swing itself
    /// has paid. ModifyOutgoingDamage below just reads whether that payment succeeded.
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
            Description = "Your melee attacks hit for 8% more per rank but each swing costs extra stamina. " +
                          "Higher Weapon Tinkering increases the bonus; with insufficient stamina the swing lands normally.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // Berserker T2 GC: 1 flat, on par with the other T2 damage GCs (Deadeye)
            Implemented = true,
            AffinitySkill = Skill.WeaponTinkering,
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

            // stamina is paid once per swing via Player.ApplySavageBlowsStamina (called from
            // Player_Melee.Attack, before this swing's strike loop) - this just reads whether that swing
            // paid it. A cleave or a later strike of the same multi-strike swing reads the same flag, so
            // the bonus (not the charge) applies to every hit the paid-for swing lands.
            if (attacker.SavageBlowsSwingPaid)
            {
                var rankBonus = rank * PropertyManager.GetDouble("class_ability_savageblows_percent_per_rank").Item;

                // Weapon Tinkering is MULTIPLICATIVE on this ability's OWN rank bonus (2026-09-12 overhaul),
                // not an additive rider beside it. At zero effective Weapon Tinkering the multiplier is
                // exactly 1.0, leaving the hit bit-identical to rank alone.
                var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.WeaponTinkering);

                abilityBonus = rankBonus * affinity;
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
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective bonus.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank * PropertyManager.GetDouble("class_ability_savageblows_percent_per_rank").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.WeaponTinkering);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

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
