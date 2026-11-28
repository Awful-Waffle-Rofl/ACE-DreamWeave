using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Requires an equipped shield, and reflects a fraction of the shield's effective armor level
    /// (per rank) back at the attacker on every landed hit, so the skill scales with shield
    /// quality (gear synergy) rather than damage taken. Reflected damage is attributed to the player,
    /// so kill credit and the attacker's DamageHistory work through the normal path.
    ///
    /// A CLOSE-RANGE PUNISH FOR EVERY ATTACK TYPE (user ruling 2026-10-07, mirroring the monster reflect
    /// effect's on=hit ruling). Melee, missile and direct spell hits (spell projectiles, Harm, Drain Health)
    /// and blocked hits all trigger it, but ONLY when the attacker is within class_ability_thorns_max_range
    /// metres of the player when the hit lands (edge-to-edge cylinder distance; see
    /// Player.ApplyThornsReflect, which owns the gate for every trigger). A long-range archer or caster is
    /// never answered. DoT ticks never trigger it. Physical hits arrive through this class's OnDamageTaken
    /// (the post-write IIncomingDamageAbility hook, from Player.TakeDamage); magic hits do not reach that
    /// hook and are wired by name at their own sites through Player.ApplyThornsOnMagicHit. A projectile the
    /// Reflect ability bounces reaches neither, so Player.TryReflectProjectile calls the reflect itself -
    /// synchronously at the bounce and outside the reflect latch (owner ruling 2026-10-23), so the firer's
    /// wards and immunities apply to it like any other Thorns hit.
    ///
    /// REFLECT LOOPS ARE NO LONGER RULED OUT BY THE MONSTER SIDE. Monsters cannot learn Thorns, which is
    /// what this comment used to rest on, but a monster CAN now carry a reflect effect of its own
    /// (PropertyString 9015 MonsterCombatEffects), and monster reflect plus this ability is a genuine
    /// two-system loop: the monster answers a hit by calling player.TakeDamage, which dispatches Thorns,
    /// which calls attacker.TakeDamage straight back. What breaks the loop is the reflect latch -
    /// Creature.TryEnterReflect / ExitReflect, held across the whole monster incoming-damage dispatch in
    /// Creature_MonsterEffects.cs - not anything on this side. Nothing here needs to change; do not remove
    /// the latch on the belief that this ability is one-sided.
    /// </summary>
    public class ThornsAbility : IIncomingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Thorns,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 1,
            Name = "thorns",
            DisplayName = "Thorns",
            // Live text: rank * class_ability_thorns_percent_per_rank (ComputeReflectDamage); the range gate is
            // class_ability_thorns_max_range, read in Player.ApplyThornsReflect (Player_ClassAbilityCombat).
            DescriptionTemplate = "While you have a shield equipped, creatures within {v:class_ability_thorns_max_range|num} metres " +
                          "that hit you with melee, missiles or spells take damage back, based on your shield's armor level. While " +
                          "a Runic Ward or Sanguine Ward stands, its shield level adds to that armor level. " +
                          "Shield modifiers and the Critical Strike and Crippling Blow imbues on your shield apply to the reflect, which uses your Damage Rating and can land critical hits.",
            RankTailTemplate = "+{per:class_ability_thorns_percent_per_rank|pct}% of shield armor",
            TunableSourceFiles = new[] { "WorldObjects/Player_ClassAbilityCombat.cs" },
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // flat 1/rank (class ability overhaul repricing, 2026-09-12)
            Implemented = true,
            AffinitySkill = Skill.Shield,
            AffinityRate = AffinityRateSpec.LegacyAdditive("class_ability_thorns_shield_per_trained", "class_ability_thorns_shield_per_spec"),
        };

        public void OnDamageTaken(Player defender, int rank, Creature attacker, DamageType damageType, uint damageTaken)
        {
            // full-strength reflect on any landed hit (contact) - the range gate, shield lookup, Shield-skill
            // rider, reflect math, and messaging live in Player.ApplyThornsReflect (shared with the block
            // path and the magic sites). Fires even when the hit is fully mitigated to 0 (still enemy
            // contact - user ruling), provided the attacker is within range.
            defender.ApplyThornsReflect(attacker, damageType, 1.0);
        }

        /// <summary>
        /// The final reflect: the base reflect x the trigger's strength x the Critical Thorns multiplier (1.0
        /// outside Cloaked in Power's window) x the Damage Resist Rating multiplier (1.0 without that rating),
        /// rounded to the nearest whole point. All three factors multiply, none adds, and each is floored at 0
        /// so no factor can turn a reflect into a heal. With both multipliers at 1.0 this is exactly the
        /// pre-2026-11-06 <c>Math.Round(baseReflect * strengthMultiplier)</c>.
        /// </summary>
        public static uint ScaleReflect(uint baseReflect, double strengthMultiplier, double critMultiplier, double drrMultiplier)
        {
            var scaled = baseReflect * Math.Max(0.0, strengthMultiplier) * Math.Max(0.0, critMultiplier) * Math.Max(0.0, drrMultiplier);

            return (uint)Math.Round(scaled);
        }

        /// <summary>
        /// The shield Thorns reflect (Docs/WeaponMods/SHIELDS-DESIGN.md "The Thorns formula after this change"):
        /// (baseReflect + Heft) x strength x the total Damage Rating mod x the crit multiplier x the Damage Resist
        /// Rating multiplier, rounded to the nearest whole point. Heft is flat damage on the shield, added BEFORE the
        /// multipliers the way Heft enters a weapon's base damage. Heft and every factor are floored at 0, so nothing
        /// turns a reflect into a heal. With no Heft and a neutral damage rating this equals the four-factor overload.
        /// </summary>
        public static uint ScaleReflect(uint baseReflect, double heft, double strengthMultiplier, double damageRatingMultiplier, double critMultiplier, double drrMultiplier)
        {
            var scaled = (baseReflect + Math.Max(0.0, heft)) * Math.Max(0.0, strengthMultiplier) * Math.Max(0.0, damageRatingMultiplier)
                * Math.Max(0.0, critMultiplier) * Math.Max(0.0, drrMultiplier);

            return (uint)Math.Round(scaled);
        }

        /// <summary>
        /// Test seam: the Thorns crit draw, uniform in [0, 1) (ThreadSafeRandom.Next(float, float) excludes its max), drawn the same way a weapon hit draws its crit
        /// (DamageEvent). Production never reassigns it.
        /// </summary>
        internal static Func<double> CritDraw = () => ThreadSafeRandom.Next(0.0f, 1.0f);

        /// <summary>
        /// The crit comparison a weapon hit uses (DamageEvent: <c>CriticalChance &gt; ThreadSafeRandom.Next(0.0f, 1.0f)</c>):
        /// strictly greater, same as DamageEvent. Production never draws 1.0 (the draw's max is exclusive), so a roll
        /// of 1.0 is only a test value meaning "never crits".
        /// </summary>
        public static bool RollsCritical(double critChance, double roll) => critChance > roll;

        /// <summary>
        /// The crit multiplier of a critical Thorns reflect: (1 + the shield's crit damage mod) x (1 + Execution).
        /// <paramref name="weaponCritDamageMod"/> is WorldObject.GetWeaponCritDamageMod with the shield as the weapon
        /// (1.0 base, Crippling Blow raises it), the same term DamageEvent adds 1.0 to. With no Crippling Blow and no
        /// Execution this is 2.0. The Crit Damage Rating is NOT in here: on a crit it combines additively with the Damage
        /// Rating in <see cref="DamageRatingMultiplier"/> (owner ruling 2026-11-25). A negative Execution is floored at 0.
        /// </summary>
        public static double CritDamageMultiplier(float weaponCritDamageMod, double execution)
        {
            return (1.0 + weaponCritDamageMod) * (1.0 + Math.Max(0.0, execution));
        }

        /// <summary>
        /// The rating multiplier of a Thorns reflect. Without a crit it is the Damage Rating mod alone. On a crit the
        /// Damage Rating mod and the Crit Damage Rating mod combine through Creature.AdditiveCombine, the same helper
        /// DamageEvent uses to combine DamageRatingBaseMod and CriticalDamageRatingMod on a weapon crit (owner ruling
        /// 2026-11-25): each mod is turned back into a rating (Creature.ModToRating), the ratings are summed, and the sum
        /// becomes one mod again, so DR 20 and CDR 20 give 1.4 rather than 1.2 x 1.2 = 1.44.
        /// <paramref name="damageRatingMod"/> is the positive rating mod of the total Damage Rating (blessing taken out).
        /// </summary>
        public static double DamageRatingMultiplier(float damageRatingMod, bool critical, int critDamageRating)
        {
            if (!critical)
                return damageRatingMod;

            return Creature.AdditiveCombine(damageRatingMod, Creature.GetPositiveRatingMod(critDamageRating));
        }

        /// <summary>
        /// Pure reflect-damage math (extracted for testability):
        /// (rank * percentPerRank + shieldScale + gearModPercent) of the shield's effective armor level,
        /// rounded. shieldScale is the Shield-skill rider fraction (0 when the skill is untrained). A
        /// negative effective AL (e.g. from a brittlemail debuff) clamps to zero so the skill can never heal
        /// the attacker.
        ///
        /// <paramref name="gearModPercent"/> is the Thorns equipment mod, a STANDALONE mod: same axis,
        /// additive, and reachable at rank 0 (where rank * percentPerRank is 0 and the reflect is the mod
        /// alone). A shield is still required - that is the ability's own condition, kept for the mod.
        /// Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static uint ComputeReflectDamage(double effectiveShieldArmorLevel, int rank, double percentPerRank, double shieldScale = 0.0, double gearModPercent = 0.0)
        {
            var percent = Math.Max(0, rank) * percentPerRank + shieldScale + Math.Max(0.0, gearModPercent);
            return (uint)Math.Round(Math.Max(effectiveShieldArmorLevel, 0) * Math.Max(0.0, percent));
        }

        /// <summary>
        /// Reports the reflect FRACTION (not a damage amount - ComputeReflectDamage above returns damage,
        /// not a percentage): rank term + Shield-skill rider + gear, x100 for display. Mirrors the exact
        /// terms ApplyThornsReflect (Player_ClassAbilityCombat.cs) feeds into ComputeReflectDamage - the two
        /// must stay in step. No cap on the percent itself (only a floor at zero on a negative armor level,
        /// which never triggers here since this is a fraction, not the armor level), so Effective always
        /// equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.Shield,
                PropertyManager.GetDouble("class_ability_thorns_shield_per_trained").Item,
                PropertyManager.GetDouble("class_ability_thorns_shield_per_spec").Item) * 0.01 * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Thorns) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "shield AL",
                Per = null,
                CapNote = null,
            };
        }
    }
}
