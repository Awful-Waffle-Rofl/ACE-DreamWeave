using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The offensive role a roster creature plays, as decided by <see cref="ArchetypeClassifier"/>.</summary>
    public enum CreatureArchetype
    {
        /// <summary>No usable attack skill on any family (or no qualifying evidence for one).</summary>
        Unclassified,

        /// <summary>
        /// Casts a HARMFUL spell (damage, or a hostile enchantment, debuff or drain) on at least
        /// <see cref="ArchetypeClassifier.CasterShare"/> of its attacks.
        /// </summary>
        Caster,

        /// <summary>At least <see cref="ArchetypeClassifier.RangedShare"/> of its attacks are missile attacks.</summary>
        Ranged,

        /// <summary>Melee-led, and its toughness against the band standard meets or exceeds its lethality.</summary>
        MeleeTank,

        /// <summary>Melee-led, and its lethality against the band standard exceeds its toughness.</summary>
        MeleeDps,

        /// <summary>No single attack mode reaches its threshold but the top two each reach <see cref="ArchetypeClassifier.HybridFloor"/>.</summary>
        Hybrid,
    }

    /// <summary>
    /// The facts about one weenie that <see cref="ArchetypeClassifier"/> needs and <see cref="DungeonStatProfile"/>'s
    /// band-standard axes do not carry. Immutable; <see cref="Empty"/> when unread. Nothing in the runtime reads this
    /// yet - it exists so an offline census can classify the roster.
    /// </summary>
    public sealed class ArchetypeFacts
    {
        private static readonly IReadOnlyDictionary<Skill, uint> NoRanks = new Dictionary<Skill, uint>();
        private static readonly IReadOnlyDictionary<Skill, int> NoSpells = new Dictionary<Skill, int>();
        private static readonly IReadOnlyList<SpellRoll> NoRolls = new SpellRoll[0];

        /// <summary>The "nothing read" value.</summary>
        public static readonly ArchetypeFacts Empty = new ArchetypeFacts(null, false, false, null);

        /// <summary>Authored PropertiesSkill.LevelFromPP per skill (the Ranks term of a skill's Current).</summary>
        public IReadOnlyDictionary<Skill, uint> SkillRanks { get; }

        /// <summary>True when a create-list row wields an item that is a missile launcher or a thrown (Missile) weapon.</summary>
        public bool WieldsLauncher { get; }

        /// <summary>True when PropertyDataId.WieldedTreasureType is set (the weenie rolls wielded gear at creation).</summary>
        public bool HasWieldedTreasure { get; }

        /// <summary>
        /// Count of spell-book entries per magic skill (War, Life, Void) that are harmful damaging spells of that
        /// school. A skill with none is absent.
        /// </summary>
        public IReadOnlyDictionary<Skill, int> DamagingSpellCounts { get; }

        /// <summary>
        /// The spell book in the order the engine walks it (Monster_Magic.TryRollSpell_Unconditional), one entry per
        /// spell: its per-attack cast probability and, when it is a harmful damaging spell, the school skill it needs.
        /// Order matters because the engine takes the FIRST successful Bernoulli trial, so an earlier spell shadows a
        /// later one. Never null.
        /// </summary>
        public IReadOnlyList<SpellRoll> SpellRolls { get; }

        /// <summary>
        /// Probability that WieldedTreasureType rolls a set containing a launcher or thrown weapon (resolved offline
        /// by <see cref="WieldedTreasureMath"/>). 0 when no treasure type is set or no resolver was supplied.
        /// </summary>
        public double TreasureMissileProbability { get; }

        /// <summary>PropertyInt.DefaultCombatStyle, 0 when absent. WorldObject.IsCaster is this equal to CombatStyle.Magic.</summary>
        public int DefaultCombatStyle { get; }

        /// <summary>True when <see cref="DefaultCombatStyle"/> is CombatStyle.Magic (WorldObject.IsCaster).</summary>
        public bool IsDefaultStyleCaster => DefaultCombatStyle == (int)CombatStyle.Magic;

        /// <summary>
        /// The creature's best melee or missile MAX damage as authored on the weenie, in the units the engine's damage
        /// roll reads: the largest of its best body-part DVal, the Damage of any wielded (create-list Wield row) melee or
        /// thrown weapon, and the Damage of any wielded ammunition times a wielded launcher's DamageMod (the engine
        /// scales ammo by the launcher, Monster_Missile / GetMissileDamage). Wielded TREASURE is not resolved - it is a
        /// random roll per instance - so this is the authored floor of the creature's weapon damage. 0 = no data.
        /// Read only by the Threads archetype uplift (DungeonArchetypeStandards, ArchetypeUplift).
        /// </summary>
        public uint EffectiveMaxDamage { get; }

        /// <summary>
        /// The mean base damage ((min + max) / 2) of the best single-projectile war or void PROJECTILE spell in the book,
        /// or 0 when there is none (or no resolver was supplied). The archetype uplift's spell-output axis multiplies it
        /// by (1 + DamageRating / 100).
        /// </summary>
        public double BestProjectileMean { get; }

        /// <summary>Authored PropertyInt.DamageRating (0 when absent).</summary>
        public int DamageRating { get; }

        /// <summary>Authored PropertyInt.DamageResistRating (0 when absent).</summary>
        public int DamageResistRating { get; }

        /// <summary>Authored PropertyInt.CritRating (0 when absent).</summary>
        public int CritRating { get; }

        /// <summary>Authored PropertyInt.CritDamageRating (0 when absent).</summary>
        public int CritDamageRating { get; }

        /// <summary>
        /// The creature's spell output as the archetype uplift measures it: <see cref="BestProjectileMean"/> x
        /// (1 + max(0, DamageRating) / 100). 0 when it carries no war/void projectile.
        /// </summary>
        public double SpellOutput => BestProjectileMean > 0 ? BestProjectileMean * (1.0 + Math.Max(0, DamageRating) / 100.0) : 0.0;

        /// <summary>
        /// The eight elemental resist properties in the fixed order every resist list on this type and on
        /// <see cref="ArchetypeAxes"/> uses: slash, pierce, bludgeon, fire, cold, acid, electric, nether.
        /// </summary>
        public static readonly PropertyFloat[] ResistProperties =
        {
            PropertyFloat.ResistSlash, PropertyFloat.ResistPierce, PropertyFloat.ResistBludgeon, PropertyFloat.ResistFire,
            PropertyFloat.ResistCold, PropertyFloat.ResistAcid, PropertyFloat.ResistElectric, PropertyFloat.ResistNether,
        };

        /// <summary>The eight ArmorModVs properties, in the same damage-type order as <see cref="ResistProperties"/>.</summary>
        public static readonly PropertyFloat[] ArmorModProperties =
        {
            PropertyFloat.ArmorModVsSlash, PropertyFloat.ArmorModVsPierce, PropertyFloat.ArmorModVsBludgeon, PropertyFloat.ArmorModVsFire,
            PropertyFloat.ArmorModVsCold, PropertyFloat.ArmorModVsAcid, PropertyFloat.ArmorModVsElectric, PropertyFloat.ArmorModVsNether,
        };

        /// <summary>
        /// The authored ResistX per <see cref="ResistProperties"/> entry, an absent row read as 1.0 (Creature.GetResistanceMod's
        /// <c>ResistX ?? 1.0</c>). Null when the facts were not read off a weenie - such a member carries no resist data.
        /// Read only by the Threads uplift resist floor (dynamic_dungeons_uplift_resist_floor).
        /// </summary>
        public IReadOnlyList<double> Resists { get; }

        /// <summary>The authored ArmorModVsX per <see cref="ArmorModProperties"/> entry, absent read as 1.0; null when not read.</summary>
        public IReadOnlyList<double> ArmorMods { get; }

        public ArchetypeFacts(IReadOnlyDictionary<Skill, uint> skillRanks, bool wieldsLauncher, bool hasWieldedTreasure,
            IReadOnlyDictionary<Skill, int> damagingSpellCounts, IReadOnlyList<SpellRoll> spellRolls = null,
            double treasureMissileProbability = 0.0, int defaultCombatStyle = 0, uint effectiveMaxDamage = 0,
            double bestProjectileMean = 0.0, int damageRating = 0, int damageResistRating = 0, int critRating = 0, int critDamageRating = 0,
            IReadOnlyList<double> resists = null, IReadOnlyList<double> armorMods = null)
        {
            Resists = resists != null && resists.Count == ResistProperties.Length ? resists : null;
            ArmorMods = armorMods != null && armorMods.Count == ArmorModProperties.Length ? armorMods : null;
            SkillRanks = skillRanks ?? NoRanks;
            WieldsLauncher = wieldsLauncher;
            HasWieldedTreasure = hasWieldedTreasure;
            DamagingSpellCounts = damagingSpellCounts ?? NoSpells;
            SpellRolls = spellRolls ?? NoRolls;
            TreasureMissileProbability = double.IsNaN(treasureMissileProbability) ? 0.0 : Math.Clamp(treasureMissileProbability, 0.0, 1.0);
            DefaultCombatStyle = defaultCombatStyle;
            EffectiveMaxDamage = effectiveMaxDamage;
            BestProjectileMean = double.IsNaN(bestProjectileMean) || double.IsInfinity(bestProjectileMean) || bestProjectileMean < 0 ? 0.0 : bestProjectileMean;
            DamageRating = damageRating;
            DamageResistRating = damageResistRating;
            CritRating = critRating;
            CritDamageRating = critDamageRating;
        }


        /// <summary>Total damaging harmful spells across the three attack schools.</summary>
        public int TotalDamagingSpells => DamagingSpellCounts.Values.Sum();

        /// <summary>
        /// Reads the facts off one weenie. <paramref name="weenieOf"/> resolves a create-list item to its weenie;
        /// <paramref name="damagingSpellSkillOf"/> maps a spell id to the school skill it is a harmful damaging spell
        /// of (null when it is not one, or the spell is unknown).
        /// </summary>
        public static ArchetypeFacts FromWeenie(ACE.Entity.Models.Weenie weenie, Func<uint, ACE.Entity.Models.Weenie> weenieOf,
            Func<int, Skill?> damagingSpellSkillOf, Func<uint, double> wieldedMissileProbabilityOf = null)
            => FromWeenie(weenie, new ArchetypeFactsResolver(weenieOf, damagingSpellSkillOf, null, wieldedMissileProbabilityOf, null));

        /// <summary>
        /// The facts off one weenie through a <see cref="ArchetypeFactsResolver"/>. With the resolver's
        /// HarmfulSpellSkillOf null every spell roll's harmful skill is its damaging skill (the pre-2026-10-23 rule);
        /// with ProjectileMeanOf null <see cref="BestProjectileMean"/> is 0.
        /// </summary>
        public static ArchetypeFacts FromWeenie(ACE.Entity.Models.Weenie weenie, ArchetypeFactsResolver resolver)
        {
            if (weenie == null || resolver == null)
                return Empty;

            var weenieOf = resolver.WeenieOf;
            var damagingSpellSkillOf = resolver.DamagingSpellSkillOf;
            var wieldedMissileProbabilityOf = resolver.WieldedMissileProbabilityOf;

            var ranks = new Dictionary<Skill, uint>();

            if (weenie.PropertiesSkill != null)
            {
                foreach (var kvp in weenie.PropertiesSkill)
                {
                    if (kvp.Value != null && kvp.Value.LevelFromPP > 0)
                        ranks[kvp.Key] = kvp.Value.LevelFromPP;
                }
            }

            var launcher = false;

            // The wielded create-list items, for the launcher flag and the effective max damage. A wielded launcher
            // also supplies the DamageMod its ammunition is scaled by.
            var wielded = new List<ACE.Entity.Models.Weenie>();

            if (weenie.PropertiesCreateList != null && weenieOf != null)
            {
                foreach (var row in weenie.PropertiesCreateList)
                {
                    if (row == null || !row.DestinationType.HasFlag(DestinationType.Wield))
                        continue;

                    var item = weenieOf(row.WeenieClassId);

                    if (item == null)
                        continue;

                    wielded.Add(item);

                    if (IsLauncherType(item.WeenieType))
                        launcher = true;
                }
            }

            var spells = new Dictionary<Skill, int>();
            var rolls = new List<SpellRoll>();
            var harmfulSpellSkillOf = resolver.HarmfulSpellSkillOf;
            var bestMean = 0.0;

            // The POST-strip book (orchestrator ruling 2026-10-23): what the spawner's AoE/drain strip removes is never
            // cast, so it neither makes a creature a Caster nor shadows a later spell in the walk.
            var stripped = resolver.StrippedSpellsOf(weenie);

            if (weenie.PropertiesSpellBook != null && damagingSpellSkillOf != null)
            {
                // Dictionary enumeration order, which is the order Monster_Magic.TryRollSpell_Unconditional walks.
                foreach (var kvp in weenie.PropertiesSpellBook)
                {
                    if (stripped.Contains(kvp.Key))
                        continue;

                    var skill = damagingSpellSkillOf(kvp.Key);

                    if (skill.HasValue)
                        spells[skill.Value] = spells.TryGetValue(skill.Value, out var n) ? n + 1 : 1;

                    rolls.Add(new SpellRoll(CastProbability(kvp.Value), skill, harmfulSpellSkillOf != null ? harmfulSpellSkillOf(kvp.Key) : skill));
                }
            }

            if (weenie.PropertiesSpellBook != null && resolver.ProjectileMeanOf != null)
            {
                foreach (var id in weenie.PropertiesSpellBook.Keys)
                    if (!stripped.Contains(id))
                        bestMean = Math.Max(bestMean, resolver.ProjectileMeanOf(id));
            }

            var treasureType = weenie.GetProperty(PropertyDataId.WieldedTreasureType).GetValueOrDefault();
            var treasureProbability = treasureType != 0 && wieldedMissileProbabilityOf != null ? wieldedMissileProbabilityOf(treasureType) : 0.0;

            return new ArchetypeFacts(ranks, launcher, treasureType != 0, spells, rolls, treasureProbability,
                weenie.GetProperty(PropertyInt.DefaultCombatStyle) ?? 0, EffectiveMaxDamageOf(weenie, wielded), bestMean,
                weenie.GetProperty(PropertyInt.DamageRating) ?? 0, weenie.GetProperty(PropertyInt.DamageResistRating) ?? 0,
                weenie.GetProperty(PropertyInt.CritRating) ?? 0, weenie.GetProperty(PropertyInt.CritDamageRating) ?? 0,
                FloatsOf(weenie, ResistProperties), FloatsOf(weenie, ArmorModProperties));
        }

        /// <summary>The weenie's value of each property, absent (or not finite) read as 1.0 (pure).</summary>
        internal static double[] FloatsOf(ACE.Entity.Models.Weenie weenie, PropertyFloat[] properties)
        {
            var values = new double[properties.Length];

            for (var i = 0; i < properties.Length; i++)
            {
                var v = weenie?.GetProperty(properties[i]);
                values[i] = v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v.Value : 1.0;
            }

            return values;
        }

        /// <summary>
        /// <see cref="EffectiveMaxDamage"/> (pure over the weenie and its resolved wielded items): the largest of the best
        /// body-part DVal, every wielded melee weapon's and thrown weapon's Damage, and every wielded ammunition's Damage
        /// times the best wielded launcher's DamageMod (1.0 when the creature wields none). Negative values read as 0.
        /// </summary>
        public static uint EffectiveMaxDamageOf(ACE.Entity.Models.Weenie weenie, IReadOnlyList<ACE.Entity.Models.Weenie> wielded)
        {
            if (weenie == null)
                return 0;

            double best = 0;

            if (weenie.PropertiesBodyPart != null)
                foreach (var part in weenie.PropertiesBodyPart.Values)
                    if (part != null)
                        best = Math.Max(best, part.DVal);

            if (wielded != null)
            {
                var launcherMod = 0.0;

                foreach (var item in wielded)
                    if (item != null && item.WeenieType == WeenieType.MissileLauncher)
                        launcherMod = Math.Max(launcherMod, item.GetProperty(PropertyFloat.DamageMod) ?? 1.0);

                if (launcherMod <= 0)
                    launcherMod = 1.0;

                foreach (var item in wielded)
                {
                    if (item == null)
                        continue;

                    var damage = item.GetProperty(PropertyInt.Damage) ?? 0;

                    if (damage <= 0)
                        continue;

                    if (item.WeenieType == WeenieType.MeleeWeapon || item.WeenieType == WeenieType.Missile)
                        best = Math.Max(best, damage);
                    else if (item.WeenieType == WeenieType.Ammunition)
                        best = Math.Max(best, damage * launcherMod);
                }
            }

            return (uint)Math.Clamp(Math.Round(best, MidpointRounding.AwayFromZero), 0, uint.MaxValue);
        }

        /// <summary>
        /// The magic skill a spell is HARMFUL in, or null: any spell that is not Beneficial, in the War, Life, Void,
        /// Creature Enchantment or Item Enchantment school. That covers the damaging spells (<see cref="DamagingSpellSkill"/>)
        /// AND the hostile enchantments, debuffs and drains a Virindi or a Shadow spends its turns on, which the engine
        /// rolls on exactly the same TryRollSpell trial as a bolt (owner design 2026-10-23: MagicShare counts every
        /// harmful spell so those casters read Caster).
        /// </summary>
        public static Skill? HarmfulSpellSkill(MagicSchool school, bool beneficial)
        {
            if (beneficial)
                return null;

            switch (school)
            {
                case MagicSchool.WarMagic: return Skill.WarMagic;
                case MagicSchool.LifeMagic: return Skill.LifeMagic;
                case MagicSchool.VoidMagic: return Skill.VoidMagic;
                case MagicSchool.CreatureEnchantment: return Skill.CreatureEnchantment;
                case MagicSchool.ItemEnchantment: return Skill.ItemEnchantment;
                default: return null;
            }
        }

        /// <summary>
        /// The per-attack chance one spell-book entry fires, exactly as Monster_Magic.TryRollSpell_Unconditional reads
        /// it: a stored value above 2.0 is 2.0 + probability (2.05 is 5%), anything else is a percentage (2.0 is 2%).
        /// </summary>
        public static double CastProbability(float stored)
            => stored > 2.0f ? stored - 2.0f : stored / 100.0f;

        /// <summary>
        /// The magic skill a spell is a harmful DAMAGING spell of, or null. Damaging means a projectile-family meta type
        /// (Projectile: War bolts/arcs/streaks/blasts and Void nether; LifeProjectile: Life harm; EnchantmentProjectile:
        /// the damage-over-time curses), harmful (not Beneficial), in the War, Life or Void school. Life drains
        /// (Transfer / Boost) are NOT counted.
        /// </summary>
        public static Skill? DamagingSpellSkill(MagicSchool school, SpellType metaType, bool beneficial)
        {
            if (beneficial)
                return null;

            if (metaType != SpellType.Projectile && metaType != SpellType.LifeProjectile && metaType != SpellType.EnchantmentProjectile)
                return null;

            switch (school)
            {
                case MagicSchool.WarMagic: return Skill.WarMagic;
                case MagicSchool.LifeMagic: return Skill.LifeMagic;
                case MagicSchool.VoidMagic: return Skill.VoidMagic;
                default: return null;
            }
        }

        /// <summary>A missile launcher (bow, crossbow, sling, atlatl family) or a thrown weapon (WeenieType.Missile).</summary>
        public static bool IsLauncherType(WeenieType type) => type == WeenieType.MissileLauncher || type == WeenieType.Missile;
    }

    /// <summary>
    /// The cross-archetype band standard at one level, in the units <see cref="ArchetypeClassifier"/> divides by:
    /// median melee max body damage, median max body armour, median health, and the per-skill median EFFECTIVE
    /// attack skills. Built from a <see cref="DungeonBandStandard"/> plus the band's median health (the standard
    /// deliberately carries none).
    /// </summary>
    public sealed class ArchetypeBandStandard
    {
        public double Damage { get; }
        public double Armor { get; }
        public double Health { get; }
        public IReadOnlyDictionary<Skill, uint> EffectiveSkills { get; }

        public ArchetypeBandStandard(double damage, double armor, double health, IReadOnlyDictionary<Skill, uint> effectiveSkills)
        {
            Damage = damage;
            Armor = armor;
            Health = health;
            EffectiveSkills = effectiveSkills ?? new Dictionary<Skill, uint>();
        }

        public static ArchetypeBandStandard From(DungeonBandStandard standard, uint medianHealth)
            => standard == null
                ? new ArchetypeBandStandard(0, 0, medianHealth, null)
                : new ArchetypeBandStandard(standard.MaxBodyDamage, standard.MaxBaseArmor, medianHealth, standard.EffectiveSkillMedians);

        /// <summary>
        /// The standard skill to divide a creature's best melee skill by: the median for that very skill, or the
        /// highest melee median when the band holds none for it. 0 means no standard (the ln term is then 0).
        /// </summary>
        public double MeleeSkillStandard(Skill skill)
        {
            if (EffectiveSkills.TryGetValue(skill, out var v) && v > 0)
                return v;

            var best = 0u;

            foreach (var melee in ArchetypeClassifier.MeleeSkills)
                if (EffectiveSkills.TryGetValue(melee, out var m) && m > best)
                    best = m;

            return best;
        }
    }

    /// <summary>One spell-book entry as the engine rolls it: its per-attack chance and, if damaging or harmful, the skill it needs.</summary>
    public readonly struct SpellRoll
    {
        public double Probability { get; }

        /// <summary>The magic skill (War, Life, Void) this spell is a harmful damaging spell of, or null when it is not one.</summary>
        public Skill? DamagingSkill { get; }

        /// <summary>
        /// The magic skill this spell is HARMFUL in (<see cref="ArchetypeFacts.HarmfulSpellSkill"/>: damaging, or a hostile
        /// enchantment, debuff or drain), or null when it is beneficial or unknown. Defaults to <see cref="DamagingSkill"/>
        /// when the caller does not say, so a hand-built damaging roll is also a harmful one.
        /// </summary>
        public Skill? HarmfulSkill { get; }

        public SpellRoll(double probability, Skill? damagingSkill, Skill? harmfulSkill = null)
        {
            Probability = probability;
            DamagingSkill = damagingSkill;
            HarmfulSkill = harmfulSkill ?? damagingSkill;
        }
    }

    /// <summary>One classification, with the numbers behind it so a census can print them.</summary>
    public sealed class ArchetypeResult
    {
        public CreatureArchetype Archetype { get; set; }

        /// <summary>Best melee attack skill, effective (attribute term + InitLevel + Ranks). 0 when none.</summary>
        public double Om { get; set; }

        /// <summary>Best missile skill, effective. Informational only; the classification reads <see cref="MissileShare"/>.</summary>
        public double Or { get; set; }

        /// <summary>Best of War/Void/Life, effective. Informational only; the classification reads <see cref="MagicShare"/>.</summary>
        public double Oc { get; set; }

        /// <summary>Per-attack chance of casting a HARMFUL spell (see <see cref="ArchetypeClassifier.MagicShareOf"/>). The magic mode of the mix.</summary>
        public double MagicShare { get; set; }

        /// <summary>
        /// Per-attack chance of casting a DAMAGING spell (<see cref="ArchetypeClassifier.DamagingShareOf"/>), at most
        /// <see cref="MagicShare"/>. Informational: the mix reads MagicShare; the spell-output axis is about this part.
        /// </summary>
        public double DamagingShare { get; set; }

        /// <summary>
        /// For a <see cref="CreatureArchetype.Hybrid"/>, the archetype of its PRIMARY (largest) mode - Caster, Ranged, or
        /// the melee split - which the archetype uplift falls back to when the Hybrid sample is too small. Equal to
        /// <see cref="Archetype"/> for every other archetype.
        /// </summary>
        public CreatureArchetype PrimaryArchetype { get; set; }

        /// <summary>Chance the creature's equipped weapon is a missile weapon, given a usable missile skill.</summary>
        public double MissileShare { get; set; }

        /// <summary>Share of attacks that are missile: (1 - MagicShare) x MissileShare.</summary>
        public double MissileMix { get; set; }

        /// <summary>The remainder: 1 - MagicShare - MissileMix.</summary>
        public double MeleeMix { get; set; }

        /// <summary>Melee split terms; 0 unless the melee branch was reached.</summary>
        public double Toughness { get; set; }

        public double Lethality { get; set; }
    }

    /// <summary>
    /// Pure archetype classifier. Nothing in the runtime reads it yet.
    ///
    /// It classifies by what the ENGINE makes the creature do each attack (Monster_Combat.GetNextAttackType): roll
    /// TryRollSpell first (magic), else a missile attack if the creature has an equipped missile weapon
    /// (Monster_Missile.IsRanged), else melee. Skill magnitude is deliberately NOT used to pick the mode, because
    /// retail monsters carry a high melee skill whatever they do (census 2026-10-23: a magnitude rule classed
    /// Virindi Inquisitors and Corrupt Sorcerers as melee, and only 8 of 1661 roster creatures as ranged).
    ///
    /// <see cref="MagicShareOf"/> is the per-attack probability of casting a HARMFUL spell (damaging, or a hostile
    /// enchantment, debuff or drain; it was damaging-only until 2026-10-23, and the threshold provenance below was
    /// measured on that older share) under TryRollSpell_Unconditional's semantics; <see cref="MissileShareOf"/> is the probability the creature is armed
    /// with a missile weapon. The attack mix is magic = MagicShare, missile = (1 - MagicShare) x MissileShare, melee =
    /// the remainder.
    ///
    /// Caster if magic &gt;= <see cref="CasterShare"/>; otherwise Ranged if missile &gt;= <see cref="RangedShare"/>;
    /// otherwise Hybrid if the two largest of the three modes are each &gt;= <see cref="HybridFloor"/>; otherwise melee,
    /// split into MeleeTank or MeleeDps against the cross-archetype band standard S at the creature's own level:
    /// toughness = ln(armor/S.armor) + ln(health/S.health) + ln(damage/S.damage),
    /// lethality = ln(damage/S.damage) + ln(Om/S.Om). toughness - lethality &gt;= 0 is a MeleeTank, else MeleeDps.
    /// A ln term whose numerator or denominator is 0 (missing data) contributes 0. A creature with no usable melee,
    /// missile or magic offense at all is Unclassified.
    /// </summary>
    public static class ArchetypeClassifier
    {
        // Provenance of the three thresholds (census 2026-10-23: archetype-census over the 1661 role-0/role-1 roster
        // wcids of Content/events/axes/species, ace_world_uplift = ace_world + the repo content manifest). The census
        // started from the provisional 0.40 / 0.50 / 0.25 and the distributions below kept all three, so they are the
        // owner-brief values confirmed by data rather than tuned to it.
        //
        // MagicShare over the roster: 741 creatures exactly 0; 0.05-wide bins from 0.00 hold 91, 184, 189, 131, 81, 96,
        // 43, 14, 18, 25, 18, 12, 6, 5 ... : a broad hump of incidental casters (retail melee monsters that carry a few
        // bolts, mostly 0.05-0.30) and a sparse tail of real casters (K'nath 0.75, Rynthid Sorcerer 0.66, Zefir Thorn
        // Stalker 0.60), with the minimum between them in the 0.35-0.40 bin (14).
        // MissileShare is bimodal: 1300 creatures at 0, 116 at 0.9 or more, the rest a continuum from wielded-treasure sets.
        // MissileMix (after the magic roll) 0.5 or more holds 196 creatures.

        /// <summary>
        /// Magic mix at or above which a creature is a Caster. 0.40 sits at the MagicShare valley (0.35-0.40 bin, 14
        /// creatures) between the incidental-spell hump (815 creatures with a nonzero share below 0.35) and the caster tail
        /// (91 at 0.40 or more).
        /// </summary>
        public const double CasterShare = 0.40;

        /// <summary>
        /// Missile mix at or above which a creature is Ranged: a majority of its attacks. 0.50 splits the continuum of
        /// wielded-treasure chances at its midpoint; 196 creatures reach it.
        /// </summary>
        public const double RangedShare = 0.50;

        /// <summary>
        /// Each of the top two modes must reach this for a Hybrid: a quarter of attacks is a mode a player will
        /// recognise. 0.25 is where the incidental hump (0.05-0.25 holds about 585 creatures) gives way to the
        /// 0.25-0.35 shelf of deliberate 0.30-style books (139 creatures); 211 creatures are Hybrid.
        /// </summary>
        public const double HybridFloor = 0.25;

        /// <summary>The melee members of <see cref="DungeonCombatNormalizer.AttackSkills"/>.</summary>
        public static readonly Skill[] MeleeSkills =
        {
            Skill.TwoHandedCombat, Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.DualWield,
            Skill.Axe, Skill.Dagger, Skill.Mace, Skill.Spear, Skill.Staff, Skill.Sword, Skill.UnarmedCombat,
        };

        /// <summary>The missile members of <see cref="DungeonCombatNormalizer.AttackSkills"/>.</summary>
        public static readonly Skill[] MissileSkills =
        {
            Skill.MissileWeapons, Skill.Bow, Skill.Crossbow, Skill.Sling, Skill.ThrownWeapon,
        };

        /// <summary>The three attack schools.</summary>
        public static readonly Skill[] CasterSkills = { Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic };

        /// <summary>
        /// Effective value of one skill: the attribute-formula term (0 when the profile carries no attributes or the
        /// formula is unknown) + authored InitLevel + authored Ranks. 0 when the creature does not carry the skill.
        /// </summary>
        public static double EffectiveSkill(DungeonStatProfile profile, Skill skill, Func<Skill, SkillFormula> formulaOf)
        {
            if (profile == null || !profile.Skills.TryGetValue(skill, out var init) || init == 0)
                return 0;

            double attributeTerm = 0;
            var formula = formulaOf?.Invoke(skill);

            if (formula != null && profile.Attributes.Count > 0)
                attributeTerm = AttributeFormula.Compute(formula, attr => profile.Attributes.TryGetValue(attr, out var v) ? v : 0u);

            var ranks = profile.Archetype.SkillRanks.TryGetValue(skill, out var r) ? r : 0u;

            return attributeTerm + init + ranks;
        }

        /// <summary>
        /// P(an attack is a HARMFUL spell cast) under TryRollSpell_Unconditional: the book is walked in order, one
        /// Bernoulli trial per spell, first success wins. So a spell's chance is its own probability times the chance
        /// every EARLIER spell failed, and every spell (harmful or not) consumes a trial. A harmful spell only counts
        /// when the creature has a usable (InitLevel &gt; 0) skill in its school; otherwise it contributes 0 but still
        /// shadows the spells after it.
        ///
        /// HARMFUL, not only damaging (owner design 2026-10-23): a hostile enchantment, debuff or drain is the creature
        /// spending its attack on a spell at the player exactly as a bolt is, so the Virindi, Mu-miyah and Shadow casters
        /// whose books are mostly debuffs classify as casters. <see cref="DamagingShareOf"/> keeps the damaging-only
        /// share for the spell-output axis.
        /// </summary>
        public static double MagicShareOf(DungeonStatProfile profile) => ShareOf(profile, roll => roll.HarmfulSkill);

        /// <summary>
        /// The pre-2026-10-23 MagicShare: P(an attack is a DAMAGING spell cast), on the same sequential walk as
        /// <see cref="MagicShareOf"/>. At most MagicShare, since every damaging spell is harmful.
        /// </summary>
        public static double DamagingShareOf(DungeonStatProfile profile) => ShareOf(profile, roll => roll.DamagingSkill);

        private static double ShareOf(DungeonStatProfile profile, Func<SpellRoll, Skill?> skillOf)
        {
            if (profile == null)
                return 0.0;

            var remaining = 1.0;
            var magic = 0.0;

            foreach (var roll in profile.Archetype.SpellRolls)
            {
                var p = Math.Clamp(roll.Probability, 0.0, 1.0);
                var skill = skillOf(roll);

                if (skill.HasValue && profile.Skills.TryGetValue(skill.Value, out var init) && init > 0)
                    magic += remaining * p;

                remaining *= 1.0 - p;
            }

            return magic;
        }

        /// <summary>
        /// P(the creature's equipped weapon is a missile weapon): 1.0 when a Wield create-list row is a launcher or
        /// thrown weapon, else the resolved wielded-treasure probability, else 0 - and 0 whenever the creature has no
        /// usable missile skill.
        /// </summary>
        public static double MissileShareOf(DungeonStatProfile profile)
        {
            if (profile == null || !MissileSkills.Any(s => profile.Skills.TryGetValue(s, out var v) && v > 0))
                return 0.0;

            return profile.Archetype.WieldsLauncher ? 1.0 : profile.Archetype.TreasureMissileProbability;
        }

        /// <summary>Classifies one creature. <paramref name="standard"/> may be null (the melee split then reads every ln term as 0, a Tank).</summary>
        public static ArchetypeResult Classify(DungeonStatProfile profile, ArchetypeBandStandard standard, Func<Skill, SkillFormula> formulaOf)
        {
            var result = new ArchetypeResult();

            if (profile == null)
                return result;

            Skill? bestMelee = null;
            double om = 0;

            foreach (var skill in MeleeSkills)
            {
                var v = EffectiveSkill(profile, skill, formulaOf);

                if (v > om)
                {
                    om = v;
                    bestMelee = skill;
                }
            }

            result.Om = om;
            result.Or = MissileSkills.Max(s => EffectiveSkill(profile, s, formulaOf));
            result.Oc = CasterSkills.Max(s => EffectiveSkill(profile, s, formulaOf));

            var magic = MagicShareOf(profile);
            var missileShare = MissileShareOf(profile);
            var missile = (1.0 - magic) * missileShare;
            var melee = Math.Max(0.0, 1.0 - magic - missile);

            result.MagicShare = magic;
            result.DamagingShare = DamagingShareOf(profile);
            result.MissileShare = missileShare;
            result.MissileMix = missile;
            result.MeleeMix = melee;

            if (magic <= 0 && missile <= 0 && om <= 0)
            {
                result.Archetype = CreatureArchetype.Unclassified;
                result.PrimaryArchetype = CreatureArchetype.Unclassified;
                return result;
            }

            if (magic >= CasterShare && (missile < RangedShare || magic >= missile))
            {
                result.Archetype = CreatureArchetype.Caster;
                result.PrimaryArchetype = CreatureArchetype.Caster;
                return result;
            }

            if (missile >= RangedShare)
            {
                result.Archetype = CreatureArchetype.Ranged;
                result.PrimaryArchetype = CreatureArchetype.Ranged;
                return result;
            }

            var modes = new[] { magic, missile, melee };
            Array.Sort(modes);

            // The melee split's terms, computed for a Hybrid too (for its PrimaryArchetype) but only RECORDED on the
            // result for the melee branch, so Toughness/Lethality keep their "0 unless melee" meaning.
            var s = standard ?? new ArchetypeBandStandard(0, 0, 0, null);
            var damage = (double)profile.MaxBodyDamage;
            var lnDamage = LnRatio(damage, s.Damage);
            var toughness = LnRatio(profile.MaxBaseArmor, s.Armor) + LnRatio(profile.Health, s.Health) + lnDamage;
            var lethality = lnDamage + LnRatio(om, bestMelee.HasValue ? s.MeleeSkillStandard(bestMelee.Value) : 0);
            var meleeSplit = toughness - lethality >= 0 ? CreatureArchetype.MeleeTank : CreatureArchetype.MeleeDps;

            // modes[2] is the largest, modes[1] the runner-up.
            if (modes[1] >= HybridFloor)
            {
                result.Archetype = CreatureArchetype.Hybrid;

                // The primary mode: the largest of the three, ties resolved magic, then missile, then melee (the order
                // GetNextAttackType tries them in).
                result.PrimaryArchetype = magic >= missile && magic >= melee ? CreatureArchetype.Caster
                    : missile >= melee ? CreatureArchetype.Ranged
                    : meleeSplit;
                return result;
            }

            // Melee split.
            result.Toughness = toughness;
            result.Lethality = lethality;
            result.Archetype = meleeSplit;
            result.PrimaryArchetype = meleeSplit;

            return result;
        }

        /// <summary>ln(a / b), or 0 when either side is not positive.</summary>
        public static double LnRatio(double a, double b)
            => a > 0 && b > 0 ? Math.Log(a / b) : 0.0;
    }
}
