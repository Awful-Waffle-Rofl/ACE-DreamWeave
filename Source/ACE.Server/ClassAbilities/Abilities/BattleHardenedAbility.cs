using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// A single-tier, expensive passive: reduces all incoming damage (every type - melee, missile,
    /// magic projectiles, and damage-over-time) by a flat fraction scaled off the player's Strength -
    /// 0.05% per point by default. The reduction is silent: it is applied at the one damage-resist
    /// modifier choke point (Creature.GetDamageResistRatingMod) that every incoming-damage path
    /// multiplies through, so there is no chat message and it never appears as a Damage Resist Rating
    /// on the character panel (it multiplies the mod, not the integer rating).
    ///
    /// This is a "bespoke integration" rather than a combat hook: its effect is read directly off the
    /// player at the mitigation site via Player.GetBattleHardenedDamageResistMod, so it carries the
    /// IPassiveStatAbility marker (Implemented, but hooks nothing) like the Enhanced stat family.
    /// </summary>
    public class BattleHardenedAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.BattleHardened,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 3,
            Name = "battlehardened",
            DisplayName = "Battle Hardened",
            Category = "Combat",
            Description = "Passively reduces all incoming damage of every type from monsters by 0.05% per point " +
                          "of your Strength (a hidden reduction, capped; does not apply in PvP). Scales with " +
                          "Strength buffs and Enhanced Strength.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },   // Vanguard T3 GC: 3 flat (was a pre-redesign standalone 30, which exceeded the point cap)
            Implemented = true,
        };

        /// <summary>
        /// The multiplicative damage-resist factor this skill contributes (1.0 = no reduction). Applied
        /// on top of the player's normal damage-resist rating mod. Pure so it can be unit-tested without
        /// a live player: the caller supplies the tunables and the current Strength.
        ///
        /// <paramref name="gearModReduction"/> is the Bulwark equipment mod, a STANDALONE mod: it is summed
        /// with the Strength-scaled reduction and the two are clamped TOGETHER by maxReduction, so the mod
        /// can never push total damage reduction past the ability's own ceiling. A player without the
        /// ability reaches this with reductionPerStrength = 0, leaving the mod standing alone. Defaults to
        /// 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float GetDamageResistMod(uint strength, double reductionPerStrength, double maxReduction, double gearModReduction = 0.0)
        {
            var reduction = Math.Clamp(reductionPerStrength * strength + Math.Max(0.0, gearModReduction), 0.0, Math.Max(0.0, maxReduction));
            return (float)(1.0 - reduction);
        }

        /// <summary>
        /// Strength-scaled, so Skill is the Strength-derived reduction and Affinity is 0.0 (no legacy-skill
        /// rider exists for this ability). Mirrors the terms Player.GetBattleHardenedDamageResistMod feeds
        /// into GetDamageResistMod above - the two must stay in step. CapNote is set only when the raw
        /// (unclamped) reduction actually exceeds class_ability_battlehardened_max_reduction this call.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var reductionPerStrength = PropertyManager.GetDouble("class_ability_battlehardened_reduction_per_strength").Item;
            var maxReduction = PropertyManager.GetDouble("class_ability_battlehardened_max_reduction").Item;
            var gear = player.GetEquippedModValue(EquipmentModId.Bulwark);

            var skill = reductionPerStrength * player.Strength.Current;
            var rawReduction = skill + Math.Max(0.0, gear);
            var effectiveReduction = Math.Clamp(rawReduction, 0.0, Math.Max(0.0, maxReduction));
            var capBit = rawReduction > maxReduction;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill * 100.0,
                Affinity = 0.0,
                Gear = gear * 100.0,
                Effective = effectiveReduction * 100.0,
                Unit = "%",
                Label = "dmg reduce",
                Per = null,
                CapNote = capBit ? "max reduction" : null,
            };
        }
    }
}
