using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The pure arithmetic behind two Threads normalization passes, kept free of Creature, PropertyManager and
    /// log4net so every rule here is unit-testable:
    ///
    ///   1. BOSS NORMALIZATION (dynamic_dungeons_boss_normalize): the boss's offense and defense are SET to the
    ///      band standard - two-way, unlike the upward-only uplift in ThreadDungeonSpawner.ApplyUplift, because
    ///      an over-tuned boss has to come DOWN as well as an under-tuned one coming up.
    ///   2. THE COMBAT-TRAIT STRIP AND CATEGORY-IMMUNITY GUARD (dynamic_dungeons_strip_combat_traits), applied
    ///      to every run creature: bypass traits are removed, and no creature may be immune to a whole attack
    ///      category (melee, missile or magic). Single-element immunity stays authored (owner ruling
    ///      2026-09-13: players carry several elements).
    /// </summary>
    public static class DungeonCombatNormalizer
    {
        // ---- boss offense / defense --------------------------------------------------------------------

        /// <summary>
        /// Every skill a creature can attack with. The modern weapon and magic skills, plus the retired
        /// pre-MoA weapon skills a few old roster weenies still carry (measured 2026-09-13 over the roster:
        /// UnarmedCombat on 31 weenies, Sword on 20, and single digits for the rest).
        /// </summary>
        public static readonly Skill[] AttackSkills =
        {
            Skill.TwoHandedCombat, Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.MissileWeapons,
            Skill.DualWield, Skill.LifeMagic, Skill.WarMagic, Skill.VoidMagic, Skill.CreatureEnchantment,
            Skill.Axe, Skill.Bow, Skill.Crossbow, Skill.Dagger, Skill.Mace, Skill.Sling, Skill.Spear, Skill.Staff,
            Skill.Sword, Skill.ThrownWeapon, Skill.UnarmedCombat,
        };

        /// <summary>The three skills that decide whether an attack of each category lands at all.</summary>
        public static readonly Skill[] DefenseSkills = { Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense };

        /// <summary>
        /// When the band standard is more than this many times a boss's own authored maximum on a body-part
        /// axis, the boss's parts are SET to the standard directly instead of scaled by the ratio.
        ///
        /// Why: the two-way ratio is standard / ownMax, and a near-zero authored maximum makes it explode - wcid
        /// 49641 (Simulacrum Shifter, a bosses.json row) authors a best body-part DVal of 2 against a band
        /// standard in the hundreds, so a ratio of ~100x would turn its 1-damage tail swipe into 100 while its
        /// 2-damage part became 200, which is a shape nobody authored. Past this threshold the authored shape
        /// carries no information worth preserving, so every non-zero part lands on the standard.
        /// </summary>
        public const double TinyOwnMaxRatio = 10.0;

        /// <summary>
        /// The InitLevel a boss's attack skill is SET to: the band median for that skill times
        /// <paramref name="ratio"/>, or - where the band has no median for this skill - the HIGHEST attack-skill
        /// median in the standard, so a boss wielding a school the band's creatures do not use still lands on
        /// the band's attack standard rather than keeping an authored outlier. 0 means "leave this skill
        /// authored": an empty standard, a non-positive ratio, or a standard with no attack medians at all.
        /// </summary>
        public static uint AttackSkillTarget(DungeonBandStandard standard, Skill skill, double ratio)
        {
            if (standard == null || standard.IsEmpty || !IsPositive(ratio))
                return 0;

            var median = standard.MedianFor(skill);

            if (median == 0)
                median = HighestAttackMedian(standard);

            return median == 0 ? 0u : Scale(median, ratio);
        }

        /// <summary>The largest per-skill median over <see cref="AttackSkills"/>, or 0 when the standard holds none.</summary>
        public static uint HighestAttackMedian(DungeonBandStandard standard)
        {
            if (standard == null)
                return 0;

            var best = 0u;

            foreach (var skill in AttackSkills)
                best = Math.Max(best, standard.MedianFor(skill));

            return best;
        }

        /// <summary>
        /// The InitLevel a boss's defense skill is SET to: the band median times <paramref name="ratio"/>. 0
        /// means "leave it authored" - no median for this skill, or a non-positive ratio. Unlike attack skills
        /// there is no cross-skill fallback: the three defense skills guard three different categories, and
        /// borrowing one for another would invent a number the band never measured.
        /// </summary>
        public static uint DefenseSkillTarget(DungeonBandStandard standard, Skill skill, double ratio)
        {
            if (standard == null || standard.IsEmpty || !IsPositive(ratio))
                return 0;

            var median = standard.MedianFor(skill);

            return median == 0 ? 0u : Scale(median, ratio);
        }

        /// <summary>
        /// The non-boss defense ceiling: the band's median EFFECTIVE value for the skill
        /// (<see cref="DungeonBandStandard.EffectiveDefenseMedianFor"/> - attribute-formula contribution plus
        /// authored InitLevel, the same two terms CreatureSkill.Base/Current sum) plus
        /// <paramref name="offset"/>, or 0 ("no cap") when the standard is empty, the effective median is 0,
        /// or <paramref name="offset"/> is negative.
        ///
        /// UNLIKE the retired ratio-based cap this replaced, 0 is a VALID offset - "cap exactly at the band's
        /// effective median" - so this is not <see cref="IsPositive"/>'s "0 disables" rule; only a negative
        /// offset (or NaN/Infinity) disables. This is what makes the cap able to act on a creature's EFFECTIVE
        /// skill (attributes included) rather than only its authored InitLevel: a creature with all six primary
        /// attributes at 1000 can exceed any InitLevel-only cap through the attribute term alone (uint InitLevel
        /// floors at 0), which is exactly the failure this cap replaces (wcid 46700 Crazed Olthoi, melee defense
        /// ~987 from ~1000 Coordination/Quickness alone).
        ///
        /// A creature at or below the cap is untouched; enforcement is a runtime ceiling
        /// (Creature.DefenseSkillCeilings, applied in CreatureSkill.Base/Current), never a rewrite of InitLevel
        /// or attributes - see DungeonCreatureNormalizer.StripCombatTraits for why.
        /// </summary>
        public static uint DefenseSkillCap(DungeonBandStandard standard, Skill skill, double offset)
        {
            if (standard == null || standard.IsEmpty)
                return 0;

            if (double.IsNaN(offset) || double.IsInfinity(offset) || offset < 0)
                return 0;

            var median = standard.EffectiveDefenseMedianFor(skill);

            return median == 0 ? 0u : (uint)Math.Clamp(Math.Round(median + offset), 0, uint.MaxValue);
        }

        /// <summary>
        /// Clamps a creature's pre-enchantment skill sum (attribute-formula contribution + InitLevel + Ranks) to
        /// its per-instance defense ceiling, or returns it unchanged when no ceiling is set
        /// (<paramref name="ceiling"/> null). Called from CreatureSkill.Base/Current for a non-player creature,
        /// immediately after that sum is formed and before any enchantment (monster self-buff, player debuff
        /// such as Vulnerability) is applied - so a debuff still lands on top of the capped value exactly as it
        /// would on an authored one.
        /// </summary>
        public static uint ClampToDefenseCeiling(uint value, uint? ceiling)
            => ceiling.HasValue && value > ceiling.Value ? ceiling.Value : value;

        /// <summary>
        /// SETS every body part's DVal and BaseArmor onto the band standard (two-way), keeping each part's
        /// share of the creature's own maximum, and returns how many parts changed.
        ///
        /// CLONES THE COLLECTION AND EVERY PART before writing, for the reason ApplyBodyPartUplift documents:
        /// PropertiesBodyPart is shared BY REFERENCE with the cached weenie, so an in-place write would re-tune
        /// every instance of that creature server-wide. Nothing is cloned when nothing would change.
        ///
        /// An axis whose target is 0 (<paramref name="standardMaxDamage"/> or its ratio non-positive) is left
        /// authored. Per-axis zero handling, and why they differ:
        ///   - DAMAGE: a creature whose parts all author DVal 0 fights with a weapon or spells, so it keeps
        ///     none - inventing a bite is inventing content. A part with DVal 0 beside damaging parts also stays 0.
        ///   - ARMOUR: a creature with no body armour at all is simply soft, and a boss must not be softer than
        ///     its band, so every part is SET to the target.
        /// A near-zero own maximum (target above <see cref="TinyOwnMaxRatio"/> x own) sets every non-zero part
        /// to the target outright - see that constant.
        /// </summary>
        internal static int SetBossBodyParts(Biota biota, uint standardMaxDamage, uint standardMaxArmor, double offenseRatio, double defenseRatio)
        {
            var parts = biota?.PropertiesBodyPart;

            if (parts == null || parts.Count == 0)
                return 0;

            var damageTarget = IsPositive(offenseRatio) && standardMaxDamage > 0 ? (int)Math.Min(int.MaxValue, Scale(standardMaxDamage, offenseRatio)) : 0;
            var armorTarget = IsPositive(defenseRatio) && standardMaxArmor > 0 ? (int)Math.Min(int.MaxValue, Scale(standardMaxArmor, defenseRatio)) : 0;

            if (damageTarget <= 0 && armorTarget <= 0)
                return 0;

            var ownMaxDamage = 0;
            var ownMaxArmor = 0;

            foreach (var kvp in parts)
            {
                if (kvp.Value == null)
                    continue;

                ownMaxDamage = Math.Max(ownMaxDamage, kvp.Value.DVal);
                ownMaxArmor = Math.Max(ownMaxArmor, kvp.Value.BaseArmor);
            }

            var changed = 0;
            var fresh = new Dictionary<CombatBodyPart, PropertiesBodyPart>(parts.Count);

            foreach (var kvp in parts)
            {
                var part = kvp.Value?.Clone();

                if (part != null)
                {
                    var dval = TwoWayPartValue(part.DVal, ownMaxDamage, damageTarget, zeroOwnMaxStaysZero: true);
                    var armor = TwoWayPartValue(part.BaseArmor, ownMaxArmor, armorTarget, zeroOwnMaxStaysZero: false);

                    if (dval != part.DVal || armor != part.BaseArmor)
                        changed++;

                    part.DVal = dval;
                    part.BaseArmor = armor;
                }

                fresh[kvp.Key] = part;
            }

            if (changed > 0)
                biota.PropertiesBodyPart = fresh;

            return changed;
        }

        /// <summary>
        /// One part's value moved onto <paramref name="target"/>, two-way. See <see cref="SetBossBodyParts"/>
        /// for the zero rules. A part that was non-zero never lands on 0 from rounding alone - a creature's
        /// small parts keep at least 1 so the attack-part filter (DVal &gt; 0) sees the same set of parts.
        /// </summary>
        internal static int TwoWayPartValue(int value, int ownMax, int target, bool zeroOwnMaxStaysZero)
        {
            if (target <= 0)
                return value;

            if (ownMax <= 0)
                return zeroOwnMaxStaysZero ? value : target;

            if (value <= 0)
                return value;

            if (target > ownMax * TinyOwnMaxRatio)
                return target;

            return (int)Math.Clamp(Math.Round(value * (double)target / ownMax), 1, int.MaxValue);
        }

        // ---- boss attributes (code review 2026-09-13) -------------------------------------------------

        /// <summary>
        /// Attributes SET to the band standard x the OFFENSE ratio. Strength and Coordination drive the attack
        /// damage attribute mod (Creature_Combat.GetAttributeMod, Creature_Combat.cs:419-427) and, with Focus and
        /// Self, the attack skill formulas.
        /// </summary>
        public static readonly PropertyAttribute[] OffenseAttributes =
        {
            PropertyAttribute.Strength, PropertyAttribute.Coordination, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        /// <summary>Attributes SET to the band standard x the DEFENSE ratio: Endurance (health) and Quickness.</summary>
        public static readonly PropertyAttribute[] DefenseAttributes = { PropertyAttribute.Endurance, PropertyAttribute.Quickness };

        /// <summary>
        /// The base a normalized boss's attribute is SET to: the band median x the matching ratio (see
        /// <see cref="OffenseAttributes"/> / <see cref="DefenseAttributes"/>). 0 means "leave it authored": an empty
        /// standard, no median for this attribute, or a non-positive ratio.
        ///
        /// Why attributes at all: CreatureSkill.Current is AttributeFormula + InitLevel + Ranks
        /// (CreatureSkill.cs:170-179), so setting InitLevel alone left a boss authored at 500-600 in every attribute
        /// (wcids 43270, 51617) far above its band's skill standard at every gem.
        /// </summary>
        public static uint AttributeTarget(DungeonBandStandard standard, PropertyAttribute attribute, double offenseRatio, double defenseRatio)
        {
            if (standard == null || standard.IsEmpty)
                return 0;

            var ratio = Array.IndexOf(DefenseAttributes, attribute) >= 0 ? defenseRatio : offenseRatio;

            if (!IsPositive(ratio))
                return 0;

            var median = standard.AttributeMedianFor(attribute);

            return median == 0 ? 0u : Scale(median, ratio);
        }

        // ---- boss weapons (code review 2026-09-13) ----------------------------------------------------

        /// <summary>
        /// The PropertyInt.Damage a normalized boss's wielded weapon (or its ammunition) is SET to, so the weapon's
        /// effective maximum lands on the band standard's body-part maximum x <paramref name="offenseRatio"/>.
        ///
        /// Why a weapon needs its own pass: Monster_Melee.GetBaseDamage returns the equipped weapon's damage
        /// INSTEAD of the attack body part's DVal whenever a weapon is equipped, and the missile ammo's when attacking
        /// at range (Monster_Melee.cs:387-399), so a wielding boss never reads the body parts SetBossBodyParts wrote.
        ///
        /// The arithmetic, and why scaling is sound: on the non-player path DamageEvent.GetBaseDamage takes
        /// attacker.GetBaseDamage as-is (DamageEvent.cs:490-503) - no elemental bonus and no weapon-mod terms, which
        /// are added only on the player path (DamageEvent.cs:470-482) - so the max is
        /// (Damage + enchantment DamageBonus) x DamageMod (BaseDamageMod.cs:22, :42-62), where DamageMod is the
        /// equipped weapon's own for a melee or thrown weapon and the LAUNCHER's for ammunition
        /// (WorldObject.GetDamageMod passes wielder.GetEquippedWeapon(), WorldObject.cs:864-874). That is linear in
        /// Damage, so Damage = target / DamageMod puts the max exactly on target. DamageVariance is a fraction of the
        /// max (MinDamage = MaxDamage x (1 - Variance), BaseDamageMod.cs:33), so it is kept and the minimum follows.
        /// ElementalDamageBonus is not written: it is never read on this path.
        ///
        /// Returns <paramref name="damage"/> unchanged ("leave authored") for an item with no damage, an empty
        /// standard axis, or a non-positive ratio; a garbled DamageMod reads as 1; never below 1.
        /// </summary>
        public static int WeaponDamageTarget(int damage, double damageMod, uint standardMaxDamage, double offenseRatio)
        {
            if (damage <= 0 || standardMaxDamage == 0 || !IsPositive(offenseRatio))
                return damage;

            var target = Scale(standardMaxDamage, offenseRatio);

            if (target == 0)
                return damage;

            var mod = IsPositive(damageMod) ? damageMod : 1.0;

            return (int)Math.Clamp(Math.Round(target / mod, MidpointRounding.AwayFromZero), 1, int.MaxValue);
        }

        // ---- trait strip ------------------------------------------------------------------------------

        /// <summary>
        /// Bool bypass and immunity traits removed from every run creature, AND from every item it has
        /// equipped (DungeonCreatureNormalizer.StripItemCombatTraits) - not the creature's own flags alone.
        /// Each consumer, verified 2026-09-13:
        ///   - IgnoreMagicResist (65), IgnoreMagicArmor (66): read through HollowMath.Resolve(weapon, attacker, flag),
        ///     i.e. the ATTACKER OR ITS WEAPON (max of the two intensities), in Monster_Melee.GetArmorMod,
        ///     Creature_BodyPart.GetEffectiveArmorVsType, Creature.GetShieldMod and Creature.GetResistanceMod - they
        ///     strip the PLAYER's protections.
        ///     Live incident 2026-09-13: boss wcid 24496 General Garsh wielded axe wcid 24567, which carried
        ///     both bools on the WEAPON row, so every player armor buff, bane and protection was ignored
        ///     while the creature's own copies were already clean.
        ///   - IgnoreShieldsBySkill (70): no reader anywhere in ACE.Server (grep), stripped because it is a
        ///     bypass trait by name and removing an unread flag costs nothing.
        ///   - Invincible (98): DamageEvent.cs:206 returns 0 damage for any melee or missile hit.
        ///   - NonProjectileMagicImmune (103): WorldObject_Weapon.cs:1075 and Player_Magic.cs:1164 refuse every
        ///     non-projectile spell (all debuffs and life spells).
        ///   - Ethereal (13) and IgnoreCollisions (11): physics pass-through; see <see cref="PassThroughStates"/>.
        /// </summary>
        public static readonly PropertyBool[] BypassBools =
        {
            PropertyBool.IgnoreMagicResist, PropertyBool.IgnoreMagicArmor, PropertyBool.IgnoreShieldsBySkill,
            PropertyBool.Invincible, PropertyBool.NonProjectileMagicImmune,
        };

        /// <summary>
        /// Float bypass traits removed from every run creature and every item it has equipped:
        ///   - IgnoreShield (151): WorldObject_Weapon.GetIgnoreShieldMod (WorldObject_Weapon.cs:887-893) takes
        ///     the MAX of the attacker's own value and weapon.IgnoreShield - the attacker's share of the
        ///     defender's shield it ignores, readable from either source.
        ///   - IgnoreArmor (155): WorldObject_Weapon.GetArmorCleavingMod (WorldObject_Weapon.cs:860-867) takes
        ///     the MIN of the attacker's own mod and the weapon's.
        ///   - AbsorbMagicDamage (159): read off the DEFENDER's equipped shield/launcher/wand in
        ///     SpellProjectile.GetAbsorbMod (SpellProjectile.cs:747-777), never off a creature property. This
        ///     one is LIVE on items specifically, not merely stripped for completeness: since
        ///     DungeonCreatureNormalizer.StripItemCombatTraits now sweeps every run creature's own equipped
        ///     items too, removing it here takes away a run creature's OWN magic absorption (owner ruling
        ///     2026-09-13: strip traits from all run creatures; single-element immunity is the only kept
        ///     exception).
        ///   - HollowIntensity (9012, non-retail): the fraction of IgnoreMagicArmor/IgnoreMagicResist applied, read by
        ///     HollowMath.Resolve only alongside those bools. Inert on its own, but stripped with them so a creature or
        ///     item that loses its hollow bools carries no orphan fraction into a later re-flag.
        /// </summary>
        public static readonly PropertyFloat[] BypassFloats = { PropertyFloat.IgnoreShield, PropertyFloat.IgnoreArmor, PropertyFloat.AbsorbMagicDamage, PropertyFloat.HollowIntensity };

        /// <summary>Overpower (386): a successful roll skips the player's evade entirely (DamageEvent.cs:210-250).</summary>
        public static readonly PropertyInt[] BypassInts = { PropertyInt.Overpower };

        /// <summary>
        /// The physics bits that let a projectile or a swing pass through a creature. Stripped through the
        /// WorldObject property setters, which write both the PropertyBool and the PhysicsState bit.
        /// </summary>
        public static readonly PhysicsState[] PassThroughStates = { PhysicsState.Ethereal, PhysicsState.IgnoreCollisions };

        // ---- category-immunity guard -----------------------------------------------------------------

        public static readonly PropertyFloat[] PhysicalResists = { PropertyFloat.ResistSlash, PropertyFloat.ResistPierce, PropertyFloat.ResistBludgeon };

        public static readonly PropertyFloat[] MagicResists =
        {
            PropertyFloat.ResistFire, PropertyFloat.ResistCold, PropertyFloat.ResistAcid, PropertyFloat.ResistElectric, PropertyFloat.ResistNether,
        };

        public static readonly PropertyFloat[] PhysicalArmorMods = { PropertyFloat.ArmorModVsSlash, PropertyFloat.ArmorModVsPierce, PropertyFloat.ArmorModVsBludgeon };

        /// <summary>
        /// The resist writes that guarantee a creature is not immune to a whole damage GROUP, as (property,
        /// new value) pairs for the caller to apply. Empty when nothing changes.
        ///
        /// The rule, per group: when even the group's BEST resist (highest multiplier, i.e. the damage type that
        /// hurts most) is below <paramref name="floor"/>, every member below the floor is raised to it. A group
        /// with a single member at or above the floor is left entirely authored - that member is the element a
        /// player can switch to, which is exactly the variety the owner kept.
        ///
        /// AN ABSENT RESIST READS AS 1.0, never as 0: Creature.GetResistanceMod(ResistanceType) multiplies
        /// <c>ResistX ?? 1.0</c> (Creature_Properties.cs:198-217), so a missing row is full damage.
        /// </summary>
        public static List<(PropertyFloat Property, double Value)> CategoryResistWrites(Func<PropertyFloat, double?> get, double floor)
        {
            var writes = new List<(PropertyFloat, double)>();

            if (get == null || !IsPositive(floor))
                return writes;

            AddGroupFloor(writes, get, PhysicalResists, floor);
            AddGroupFloor(writes, get, MagicResists, floor);

            return writes;
        }

        private static void AddGroupFloor(List<(PropertyFloat, double)> writes, Func<PropertyFloat, double?> get, PropertyFloat[] group, double floor)
        {
            var best = group.Max(p => get(p) ?? 1.0);

            if (best >= floor)
                return;

            foreach (var p in group)
            {
                if ((get(p) ?? 1.0) < floor)
                    writes.Add((p, floor));
            }
        }

        /// <summary>
        /// The ArmorModVs writes that cap physical body armour: when the SMALLEST physical ArmorModVs exceeds
        /// <paramref name="ceiling"/>, all three are scaled by one factor so the smallest lands exactly on the
        /// ceiling. One factor rather than a per-type clamp, so the creature's relative weakness between slash,
        /// pierce and bludgeon survives. An absent row reads as 1.0 (Creature_Properties.cs:173-195).
        /// </summary>
        public static List<(PropertyFloat Property, double Value)> ArmorModWrites(Func<PropertyFloat, double?> get, double ceiling)
        {
            var writes = new List<(PropertyFloat, double)>();

            if (get == null || !IsPositive(ceiling))
                return writes;

            var smallest = PhysicalArmorMods.Min(p => get(p) ?? 1.0);

            if (smallest <= ceiling)
                return writes;

            var factor = ceiling / smallest;

            foreach (var p in PhysicalArmorMods)
                writes.Add((p, (get(p) ?? 1.0) * factor));

            return writes;
        }

        // ---- shared ----------------------------------------------------------------------------------

        private static bool IsPositive(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

        private static uint Scale(uint value, double ratio) => (uint)Math.Clamp(Math.Round(value * ratio), 0, uint.MaxValue);
    }
}
