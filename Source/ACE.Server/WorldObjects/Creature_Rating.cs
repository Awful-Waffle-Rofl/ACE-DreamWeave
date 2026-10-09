using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;
using ACE.Server.WeaponMods;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        // Ratings:
        // http://asheron.wikia.com/wiki/Rating

        private static float GetRatingMod(int rating)
        {
            if (rating == 0) return 1.0f;

            if (rating >= 0)
                return GetPositiveRatingMod(rating);
            else
                return GetNegativeRatingMod(-rating);
        }

        /// <summary>
        /// Returns a 1.xx rating modifier by default,
        /// or a 0.xx rating modifier if negative
        /// </summary>
        public static float GetPositiveRatingMod(int rating)
        {
            if (rating < 0) return GetNegativeRatingMod(-rating);

            // formula: (100 + rating) / 100 = 1.xx modifier
            var ratingMod = (100 + rating) / 100.0f;
            return ratingMod;
        }

        /// <summary>
        /// Returns a 0.xx rating modifier by default,
        /// or a 1.xx rating modifier if negative
        /// </summary>
        public static float GetNegativeRatingMod(int rating, bool allowBug = false)
        {
            if (rating < 0 && !allowBug)
                return GetPositiveRatingMod(-rating);

            if (allowBug)
            {
                // with the bug allowed for DRR reduction from void dots,
                // this method will produce unbalanced modifiers for negative ratings
                // as negative rating approaches -100, it ramps up in a curve to infinity, eventually getting a divide by 0 crash for -100
                // values less than -100 would produce negative multipliers, which would result in undefined behavior throughout the system,
                // such as negative damage numbers. even with the bug enabled, we still limit to -99 on the lower end to prevent system failure

                rating = Math.Max(rating, -99);
            }

            // formula: 100 / (100 + rating) = 0.xx modifier
            var ratingMod = 100.0f / (100 + rating);
            return ratingMod;
        }

        // - Damage rating - increases all damage done to a target, including critical hits
        // Formula: (100 + <total damage rating>) / 100 = 1.xx modifier to damage
        public float GetDamageRating(int damageRating)
        {
            return GetPositiveRatingMod(damageRating);
        }

        // - Critical damage rating - increases critical hit damage done to a target
        // Formula: (100 + <total critical damage rating>) / 100 = 1.xx modifier to critical damage
        public float GetCriticalDamageRating(int criticalDmgRating)
        {
            return GetPositiveRatingMod(criticalDmgRating);
        }

        // - Damage resistance rating - decreases the amount of all incoming damage, including critical hits
        // Formula: 100 / (100 + <total damage resistance rating>) = 0.xxx modifier to incoming damage
        public float GetDamageResistanceRating(int damageResistanceRating)
        {
            return GetNegativeRatingMod(damageResistanceRating);
        }

        // - Critical damage resistance rating - decreases critical hit damage received
        // Formula: 100 / (100 + <total critical damage resistance rating>) = 0.xxx modifier to critical hit damage received
        public float GetCriticalDamageResistanceRating(int criticalDmgResistanceRating)
        {
            return GetNegativeRatingMod(criticalDmgResistanceRating);
        }

        // - DoT resistance rating - decreases the effectiveness of damage over time (DoT) spells cast upon the user
        // Formula: 100 / (100 + <total DoT reduction rating> = 0.xxx modifier to outgoing incoming DoT attacks
        public float GetDamageOverTimeResistanceRating(int dotResistanceRating)
        {
            return GetNegativeRatingMod(dotResistanceRating);
        }

        // - Health drain resistance rating - decreases the effect of drain spells cast upon the target
        // Formula: 100 / (100 + <total health drain resistance rating>) = 0.xxx modifier to incoming health drains
        public float GetHealthDrainResistanceRating(int healthDrainResistRating)
        {
            return GetNegativeRatingMod(healthDrainResistRating);
        }

        // - Healing boost rating - increases the amount of health received from consumables, healing kits, and life magic
        // Formula: (100 + <total healing rating>) / 100 = 1.xx modifier to healing
        public float GetHealingBoostRating(int healingBoostRating)
        {
            return GetPositiveRatingMod(healingBoostRating);
        }

        // - Aetheria Surge rating - increases the chance that Aetheria will surge
        // Formula: (100 + <aetheria surge rating>) / 100 = 1.xx modifier to Aetheria Surges
        public float GetAetheriaSurgeRating(int aetheriaSurgeRating)
        {
            return GetPositiveRatingMod(aetheriaSurgeRating);
        }

        // - Mana charge rating - increases the amount of mana released from mana stones into magical items
        // Formula: (100 + <mana charge rating>) / 100 = 1.xx modifier to mana charges
        public float GetManaChargeRating(int manaChargeRating)
        {
            return GetPositiveRatingMod(manaChargeRating);
        }

        // - Mana reduction rating - decreases the amount of mana consumed in equipped magical items
        // Formula: 100 / (100 + <mana reduction rating>) = 0.xxx modifier to magical item mana consumed
        public float GetManaReductionRating(int manaReductionRating)
        {
            return GetNegativeRatingMod(manaReductionRating);
        }

        // - Damage reduction rating - debuff applied to decrease the effective damage rating of the target
        // Formula: 100 / (100 + <total damage reduction rating>) = 0.xxx modifier to outgoing damage
        public float GetDamageReductionRating(int damageReductionRating)
        {
            return GetNegativeRatingMod(damageReductionRating);
        }

        // - Healing reduction rating - debuff applied to decrease the effective healing rate of the target
        // Formula: 100 / (100 + <total healing reduction rating>) = 0.xxx modifier to healing
        public float GetHealingReductionRating(int healingReductionRating)
        {
            return GetNegativeRatingMod(healingReductionRating);
        }

        // - Damage resistance reduction rating - debuff applied to decrease the effective damage resistance rating of the target
        // Formula: 100 / (100 + <total damage resistance reduction rating>) = 0.xxx modifier to incoming damage
        public float GetDamageResistanceReductionRating(int damageResistReduceRating)
        {
            return GetNegativeRatingMod(damageResistReduceRating);
        }

        // - Player Killer damage rating - increases all damage done to other players, including critical hits
        // Formula: (100 + <total PK damage rating> / 100 = 1.xx modifier to PK damage
        public float GetPKDamageRating(int pkDamageRating)
        {
            return GetPositiveRatingMod(pkDamageRating);
        }

        // - Player Killer damage resistance rating - decreases all incoming damage done by other players, including critical hits
        // Formula: 100 / (100 + <total PK damage resistance rating>) = 0.xxx modifier to incoming PK damage
        public float GetPKDamageResistanceRating(int pkDamageResistRating)
        {
            return GetNegativeRatingMod(pkDamageResistRating);
        }

        /// <summary>
        /// Converts a 1.xx modifier to a +x rating,
        /// or a 0.xx modifier to a -x rating.
        /// </summary>
        public static int ModToRating(float mod)
        {
            if (mod >= 1.0f)
                return (int)Math.Round(mod * 100 - 100);
            else
                return (int)Math.Round(-100 / mod + 100);
        }

        /// <summary>
        /// Returns a modifier to an implicitly negative rating (ie. damage resistance)
        /// </summary>
        public static int NegativeModToRating(float mod)
        {
            return -ModToRating(mod);
        }

        /// <summary>
        /// Combines rating modifiers additively
        /// </summary>
        /// <param name="mods">A list of rating modifiers</param>
        public static float AdditiveCombine(params float[] mods)
        {
            int totalRating = 0;

            foreach (var mod in mods)
                totalRating += ModToRating(mod);

            return GetRatingMod(totalRating);
        }

        public int GetDamageRating()
        {
            // get from base properties (monsters)?
            var damageRating = DamageRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.DamageRating);

            // equipment ratings
            // less any Tier A weapon mod amount (Bloodthirst) while this player's weapon mods are suppressed
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearDamage) - WeaponModSuppression.RatingOffsetFor(this, PropertyInt.GearDamage);

            // weakness as negative damage rating?
            // TODO: this should be factored in as a separate weakness rating...
            var weaknessRating = EnchantmentManager.GetRating(PropertyInt.WeaknessRating);

            var augBonus = 0;
            var lumAugBonus = 0;

            if (this is Player player)
            {
                augBonus = player.AugmentationDamageBonus * 3;
                lumAugBonus = player.LumAugDamageRating;
            }

            // Damage Rating class ability (Berserker T3): +3/6/10 into the same additive pool
            var classAbilityBonus = this is Player drPlayer ? drPlayer.GetClassAbilityRating(ClassAbilityId.DamageRating) : 0;

            // heritage / weapon type bonus factored in elsewhere?
            return damageRating + equipment + enchantments - weaknessRating + augBonus + lumAugBonus + classAbilityBonus;
        }

        public int GetDamageResistRating(CombatType? combatType = null, bool directDamage = true)
        {
            // get from base properties (monsters)?
            var damageResistRating = DamageResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.DamageResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearDamageResist);

            // nether DoTs as negative DRR?
            // TODO: this should be factored in as a separate nether damage rating...
            var netherDotDamageRating = directDamage ? EnchantmentManager.GetNetherDotDamageRating() : 0;

            var augBonus = 0;
            var lumAugBonus = 0;
            var specBonus = 0;

            if (this is Player player)
            {
                augBonus = player.AugmentationDamageReduction * 3;
                lumAugBonus = player.LumAugDamageReductionRating;
                specBonus = GetSpecDefenseBonus(combatType);
            }

            // Damage Resist Rating class ability (Vanguard T2): +3/6/10 into the same additive pool
            var classAbilityBonus = this is Player drrPlayer ? drrPlayer.GetClassAbilityRating(ClassAbilityId.DamageResistRating) : 0;

            return damageResistRating + equipment + enchantments - netherDotDamageRating + augBonus + lumAugBonus + specBonus + classAbilityBonus;
        }

        public float GetDamageResistRatingMod(CombatType? combatType = null, bool directDamage = true, WorldObject attacker = null)
        {
            return GetDamageResistRatingMod(combatType, directDamage, attacker, 1.0, out _, out _);
        }

        /// <summary>
        /// As the overload above, with the summed damage resist rating scaled by <paramref name="ratingScale"/>
        /// (PvpRatingScales.ScaleResistRating: signed, so a net-negative DRR stays negative, and never deepened by a
        /// scale above 1.0) BEFORE it is converted to a
        /// multiplier. The PvP rating lever (choke points R1 / R2) passes pvp_damage_rating_scale on a PvP hit;
        /// every other caller goes through the overload above, which passes 1.0 - the identity for every int, so
        /// PvE is unchanged by construction. The raw and scaled ratings come back so the caller can report a change.
        /// Battle Hardened and Rallying Presence multiply the mod afterwards, unscaled.
        /// </summary>
        public float GetDamageResistRatingMod(CombatType? combatType, bool directDamage, WorldObject attacker, double ratingScale, out int rawRating, out int scaledRating)
        {
            rawRating = GetDamageResistRating(combatType, directDamage);

            scaledRating = PvpRatingScales.ScaleResistRating(rawRating, ratingScale);

            var damageResistRating = scaledRating;

            var allowBug = PropertyManager.GetBool("allow_negative_rating_curve").Item;

            var mod = GetNegativeRatingMod(damageResistRating, allowBug);

            // Battle Hardened class ability: a silent, Strength-scaled reduction to all incoming damage,
            // applied on top of the normal damage-resist rating. Only players can learn it, and it is
            // PvE-only - a player attacker (PvP) is excluded, consistent with the other class abilities.
            // This is the single choke point every incoming-damage path (physical, magic, DoT) passes
            // through; each caller supplies the attacker so PvP can be distinguished here.
            if (this is Player battleHardenedPlayer && attacker is not Player)
                mod *= battleHardenedPlayer.GetBattleHardenedDamageResistMod();

            // Rallying Presence (Vanguard T1): a nearby fellow's/pet's own aura, not a self-only reduction,
            // so it is resolved separately from Battle Hardened above rather than folded into the same
            // branch - it applies to CombatPets too, which are never a Player. PvE-gated inside
            // RallyingPresenceProvider itself, the same way Battle Hardened is gated here.
            mod *= RallyingPresenceProvider.GetIncomingDamageMod(this, attacker);

            return mod;
        }

        public int GetSpecDefenseBonus(CombatType? combatType)
        {
            // https://asheron.fandom.com/wiki/Announcements_-_2013/02_-_Balance_of_Power

            // New bonus added to specialized defenses against damage of their respective attack type. (Applied in both PvE & PvP)

            // - Specialized Melee Defense skill now adds 1 Damage Rating Resist for every 60 pts against melee attacks
            // - Specialized Missile Defense skill now adds 1 Damage Rating Resist for every 50 pts against missile attacks
            // - Specialized Magic Defense skill now adds 1 Damage Rating Resist for every 50 pts against magic attacks

            // only applies to players
            if (combatType == null || !(this is Player player))
                return 0;

            var skill = GetDefenseSkill(combatType.Value);
            var creatureSkill = player.GetCreatureSkill(skill);

            // ensure defense skill is specialized
            if (creatureSkill.AdvancementClass != SkillAdvancementClass.Specialized)
                return 0;

            var divisor = skill == Skill.MeleeDefense ? 60 : 50;

            // floor?
            return (int)creatureSkill.Base / divisor;
        }

        public int GetCritRating()
        {
            // crit chance

            // get from base properties (monsters)?
            var critChanceRating = CritRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritRating);

            // equipment ratings
            // less any Tier A weapon mod amount (Weak Point) while this player's weapon mods are suppressed
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCrit) - WeaponModSuppression.RatingOffsetFor(this, PropertyInt.GearCrit);

            // augmentations
            var augBonus = 0;

            if (this is Player player)
                augBonus = player.AugmentationCriticalExpertise;

            // Crit Rating class ability (Archer T3): +3/6/10 into the same additive pool
            var classAbilityBonus = this is Player crPlayer ? crPlayer.GetClassAbilityRating(ClassAbilityId.CritRating) : 0;

            return critChanceRating + enchantments + equipment + augBonus + classAbilityBonus;
        }

        public int GetCritDamageRating()
        {
            // get from base properties (monsters)?
            var critDamageRating = CritDamageRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritDamageRating);

            // equipment ratings
            // less any Tier A weapon mod amount (Devastation) while this player's weapon mods are suppressed
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCritDamage) - WeaponModSuppression.RatingOffsetFor(this, PropertyInt.GearCritDamage);

            // augmentations
            var augBonus = 0;
            var lumAugBonus = 0;

            if (this is Player player)
            {
                augBonus = player.AugmentationCriticalPower * 3;
                lumAugBonus = player.LumAugCritDamageRating;
            }

            // Crit Damage Rating class ability (Rogue T3): +3/6/10 into the same additive pool
            var classAbilityBonus = this is Player cdrPlayer ? cdrPlayer.GetClassAbilityRating(ClassAbilityId.CritDamageRating) : 0;

            return critDamageRating + equipment + enchantments + augBonus + lumAugBonus + classAbilityBonus;
        }

        public int GetCritResistRating()
        {
            // crit resist chance

            // get from base properties (monsters)?
            var critResistRating = CritResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCritResist);

            // Crit Resist Rating class ability (Vanguard T3): +3/6/10 into the same additive pool
            var classAbilityBonus = this is Player crrPlayer ? crrPlayer.GetClassAbilityRating(ClassAbilityId.CritResistRating) : 0;

            // no augs / lum augs?
            return critResistRating + enchantments + equipment + classAbilityBonus;
        }

        public int GetCritDamageResistRating()
        {
            // get from base properties (monsters)?
            var critDamageResistRating = CritDamageResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritDamageResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCritDamageResist);

            var lumAugBonus = 0;
            if (this is Player player)
                lumAugBonus = player.LumAugCritReductionRating;

            return critDamageResistRating + equipment + enchantments + lumAugBonus;
        }

        public int GetHealingBoostRating()
        {
            // get from base properties (monsters)?
            var healBoostRating = HealingBoostRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.HealingBoostRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearHealingBoost);

            var lumAugBonus = 0;
            if (this is Player player)
                lumAugBonus = player.LumAugHealingRating;

            // Heal Boost Rating class ability (Blood Mage T2): +3/6/10 into the same additive pool
            var classAbilityBonus = this is Player hbrPlayer ? hbrPlayer.GetClassAbilityRating(ClassAbilityId.HealBoostRating) : 0;

            return healBoostRating + equipment + enchantments + lumAugBonus + classAbilityBonus;
        }

        public int GetHealingResistRating()
        {
            // debuff?
            var healResistRating = HealingResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.HealingResistRating);

            return healResistRating + enchantments;
        }

        public float GetHealingRatingMod()
        {
            var boostMod = GetPositiveRatingMod(GetHealingBoostRating());
            var resistMod = GetNegativeRatingMod(GetHealingResistRating());

            return boostMod * resistMod;
        }

        public int GetLifeResistRating()
        {
            // only affects health drain?
            // only cast by Sigil of Perserverance (Aetheria)?

            // get from base properties (monsters)?
            var lifeResistRating = LifeResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.LifeResistRating);

            return lifeResistRating + enchantments;
        }

        public float GetLifeResistRatingMod()
        {
            return GetNegativeRatingMod(GetLifeResistRating());
        }

        public int GetDotResistanceRating()
        {
            // get from base properties (monsters)?
            var dotResistRating = DotResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.DotResistRating);

            return dotResistRating + enchantments;
        }

        public int GetNetherResistRating()
        {
            // there is a property defined for this,
            // but does anything use this?

            // get from base properties (monsters)?
            var netherResistRating = NetherResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.NetherResistRating);

            return netherResistRating + enchantments;
        }

        public int GetGearMaxHealth()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearMaxHealth);
        }

        public int GetPKDamageRating()
        {
            var pkDamageRating = PKDamageRating ?? 0;

            // additive enchantments?
            var enchantments = EnchantmentManager.GetRating(PropertyInt.PKDamageRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearPKDamageRating);

            return pkDamageRating + equipment + enchantments;
        }

        public int GetPKDamageResistRating()
        {
            var pkDamageResistRating = PKDamageResistRating ?? 0;

            // additive enchantments?
            var enchantments = EnchantmentManager.GetRating(PropertyInt.PKDamageResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearPKDamageResistRating);

            return pkDamageResistRating + equipment + enchantments;
        }

        public int GetGearPKDamageRating()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearPKDamageRating);
        }

        public int GetGearPKDamageResistRating()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearPKDamageResistRating);
        }

        public int GetItemManaReductionRating()
        {
            // only comes from luminance aug?
            var lumAugBonus = 0;

            if (this is Player player)
                lumAugBonus = player.LumAugItemManaUsage;

            return lumAugBonus;
        }

        public int GetManaChargeRating()
        {
            // only comes from luminance aug?
            var lumAugBonus = 0;

            if (this is Player player)
                lumAugBonus = player.LumAugItemManaGain;

            return lumAugBonus;
        }
    }
}
