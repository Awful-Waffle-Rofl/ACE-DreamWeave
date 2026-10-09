using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Which flavor of stat an Enhanced class ability boosts.
    /// </summary>
    public enum EnhancedStatKind
    {
        Skill,
        Attribute,
        Vital,
    }

    /// <summary>
    /// A generated, passive "Enhanced X" class ability that adds a flat bonus to one trained skill,
    /// primary attribute, or secondary attribute (vital). Three tiers grant +10 / +25 / +50 - the
    /// value held AT that rank, not cumulative deltas (rank 2 is +25 total, not +10+25).
    ///
    /// The bonus is a *base value* increase, so it counts toward weapon/armor wield requirements and
    /// cascades through the attribute formulas: Enhanced Strength raises every Strength-derived skill
    /// and vital automatically. Attribute/vital bonuses live only in this class ability's rank - they are
    /// never written into the attribute record, so they are not redistributable by an attribute reset.
    ///
    /// Unlike the hand-written combat class abilities, this whole family needs no bespoke handler: it is
    /// generated from <see cref="SkillHelper.ValidSkills"/> and the attribute/vital enums, and the bonus
    /// is read directly by CreatureSkill/CreatureAttribute/CreatureVital via the Player.GetEnhanced*Bonus
    /// helpers. Adding a new skill/attribute/vital to the game grows this family for free. These implement
    /// <see cref="IPassiveStatAbility"/> (a marker) instead of any combat hook.
    /// </summary>
    public class EnhancedStatAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        // Synthetic ClassAbilityId ranges - disjoint from the hand-authored ids (1..) and from each other.
        // The underlying Skill / PropertyAttribute / PropertyAttribute2nd value is added to the base so
        // each generated skill gets a stable, collision-free id derived from what it enhances.
        public const int SkillIdBase     = 0x1000;
        public const int AttributeIdBase = 0x2000;
        public const int VitalIdBase     = 0x3000;

        public const int MaxTier = 3;

        /// <summary>
        /// Total bonus granted at each rank, indexed by rank (0 = unlearned). Fixed by design at
        /// +10 / +25 / +50; the point cost of each rank is the balance lever, not this.
        /// </summary>
        public static readonly int[] TierBonus = { 0, 10, 25, 50 };

        // Point cost of ranks 1/2/3. Flat 1/1/1 for skills, attributes, and the stamina/mana vitals (user,
        // 2026-07-18): the Enhanced family is priced as a uniform incremental buy, not an escalating one.
        private static readonly int[] SkillCost     = { 1, 1, 1 };
        private static readonly int[] AttributeCost = { 1, 1, 1 };
        private static readonly int[] VitalCost     = { 1, 1, 1 };

        // ENHANCED HEALTH ALONE IS PREMIUM (2026-09-13 repricing), which is why the vital family needs two
        // arrays rather than one. It is the only Enhanced entry that buys effective HP, so it is priced with
        // the other premium entries at 2/rank while Enhanced Stamina and Enhanced Mana stay at 1/rank by
        // explicit ruling. Selected per vital in GenerateAll below - do NOT collapse these back into one
        // array, and do not "tidy" Stamina/Mana up to match Health.
        private static readonly int[] HealthVitalCost = { 2, 2, 2 };

        public ClassAbilityDefinition Definition { get; }

        public EnhancedStatKind Kind { get; }
        public Skill TargetSkill { get; }
        public PropertyAttribute TargetAttribute { get; }
        public PropertyAttribute2nd TargetVital { get; }

        private EnhancedStatAbility(ClassAbilityDefinition definition, EnhancedStatKind kind,
            Skill skill = Skill.None,
            PropertyAttribute attribute = PropertyAttribute.Undef,
            PropertyAttribute2nd vital = PropertyAttribute2nd.Undef)
        {
            Definition = definition;
            Kind = kind;
            TargetSkill = skill;
            TargetAttribute = attribute;
            TargetVital = vital;
        }

        /// <summary>
        /// The flat base bonus for a given owned rank (clamped), 0 if unlearned.
        /// </summary>
        public static int BonusForRank(int rank) => TierBonus[Math.Clamp(rank, 0, MaxTier)];

        /// <summary>
        /// Mirrors BonusForRank() above - a flat base-value increase in the stat's own unit, so Unit is ""
        /// (flat, not a percentage). No affinity rider and no gear mod exists for this family in the
        /// shipped registry, and there is no cap.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (double)BonusForRank(rank);

            var label = Kind switch
            {
                EnhancedStatKind.Skill => "skills",
                EnhancedStatKind.Attribute => "attr",
                EnhancedStatKind.Vital => "vital",
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unhandled EnhancedStatKind"),
            };

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = skill,
                Unit = "",
                Prefix = "+",
                Label = label,
                Per = null,
                CapNote = null,
            };
        }

        public static ClassAbilityId ClassIdForSkill(Skill skill) => (ClassAbilityId)(SkillIdBase + (int)skill);
        public static ClassAbilityId ClassIdForAttribute(PropertyAttribute attribute) => (ClassAbilityId)(AttributeIdBase + (int)attribute);
        public static ClassAbilityId ClassIdForVital(PropertyAttribute2nd vital) => (ClassAbilityId)(VitalIdBase + (int)vital);

        // The primary attributes and vitals that get an Enhanced variant, in display order.
        // Vitals are the Max* forms (the values wield requirements and the client read).
        private static readonly PropertyAttribute[] Attributes =
        {
            PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination,
            PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        private static readonly PropertyAttribute2nd[] Vitals =
        {
            PropertyAttribute2nd.MaxHealth, PropertyAttribute2nd.MaxStamina, PropertyAttribute2nd.MaxMana,
        };

        private static string TierText =>
            $"Adds +{TierBonus[1]}, then +{TierBonus[2]}, then +{TierBonus[3]} at ranks 1-3";

        /// <summary>
        /// Which generated Enhanced skills the class-ability design (SKILL-TABLES-PREVIEW, as amended by the
        /// Phase 1 skill redistribution 2026-08-03) homes to a class and tier. Keyed by the synthetic
        /// ClassAbilityId. Only these (20 as of the Spellsword landing; the "17" this comment used to claim
        /// was already stale before that) are offered as trainer tokens; every other member of the generated
        /// family stays unhomed (AbilityClass.None) and is not tokenable. Homing lives here rather than in the
        /// token catalog so the runtime Tier-2/3 unlock gate and "points spent in class" (both read
        /// AbilityClass/Tier off the definition) apply to these skills too.
        ///
        /// Skill.LifeMagic (formerly Archmage tier 3) is deliberately unhomed as of 2026-08-03: it is
        /// handed off to the Blood Mage class (a parallel build) rather than staying with Archmage. This
        /// leaves Archmage temporarily without a tier-3 Enhanced-skill entry until the Blood Mage PR
        /// lands and re-homes it. No affinity is broken by this removal - affinities read the player's
        /// actual trained skill level (e.g. Withering reads Creature Enchantment, Transfusion reads
        /// Healing), not this class-ability bonus. What is lost is only the cheap class-ability way to
        /// boost Life Magic, until Blood Mage re-homes it.
        ///
        /// Skill.MeleeDefense IS HOMED TO ROGUE T2, AND IS BACK THERE RATHER THAN NEWLY THERE. It was Rogue
        /// tier 2 originally, was unhomed on 2026-08-03 and handed to the Spellsword class (then a parallel
        /// build) as a temporary loan while Spellsword had no defensive Enhanced entry of its own, and the
        /// 2026-09-12 class ability overhaul RETURNED IT to Rogue T2 - the single class-change row in that
        /// overhaul's signed-off delta table, worth 3 CAP out of Spellsword T2 and into Rogue T2. Rogue's
        /// tier-2 Enhanced-skill slot, which that loan had left open, is filled again by the move.
        /// Skill.LightWeapons - unhomed since Advanced Weaponry retired in the 2026-08-03 redistribution -
        /// stays homed to Spellsword T1 as that class's specialty Enhanced skill and was NOT part of the
        /// move. No affinity was broken by any of this, for the same reason as above.
        ///
        /// THE TOKEN CONTENT FOLLOWED THE MOVE. Enhanced Melee Defense's three trainer tokens (wcids
        /// 1000644-1000646) are stocked by the Rogue trainer 1001001 under its Tier 2 group in
        /// Content/realms/driftnetwork_hub.sql (no line citation - the create_list is edited often enough
        /// that a pinned line range goes stale fast; grep the file for the wcids instead), having moved out
        /// of the Spellsword trainer 1001960's create_list; the wcids themselves were never re-created, they
        /// simply follow their ability's home. ClassAbilityTokenTests.Catalog_HomedOfferings_AppearInCorrectTrainerCreateList
        /// is what enforces that pairing, and is what would catch a token left behind at an old home.
        /// </summary>
        private static readonly Dictionary<ClassAbilityId, (ClassAbilityClass Class, int Tier)> Homing = new()
        {
            // THE SIX CLASS ATTRIBUTES MOVED T1 -> T2 on 2026-09-12 (class ability overhaul): stat growth is
            // part of tier progression rather than a Tier 1 dump, so each class's attribute now sits at T2
            // (DESIGN.md sec 3, "the class attribute sits at T2"). Their cost is unchanged at 1/1/1.
            //
            // ENHANCED MANA AND ENHANCED STAMINA were re-homed to T1 on 2026-09-14 by owner ruling, because
            // players buying them from the class trainer reasonably expected them to count toward the class
            // tier unlock. Enhanced Missile Defense was RETIRED on 2026-09-16 (see RetiredClassAbilities'
            // "enhanced_missiledefense" entry and the exclusion in GenerateAll below) rather than homed: the
            // Archer already gets the skill through Archer Training, and the standalone entry double-dipped
            // with it.
            //
            // Archer
            [ClassIdForSkill(Skill.MissileWeapons)]              = (ClassAbilityClass.Archer, 1),
            [ClassIdForAttribute(PropertyAttribute.Coordination)] = (ClassAbilityClass.Archer, 2),
            // Rogue. Enhanced Melee Defense RETURNED here on 2026-09-12 after its 2026-08-03 loan to
            // Spellsword T2 - the class ability overhaul's single class-change row. See this dictionary's
            // doc comment; its trainer tokens moved to the Rogue trainer 1001001 to match.
            [ClassIdForSkill(Skill.FinesseWeapons)]             = (ClassAbilityClass.Rogue, 1),
            [ClassIdForAttribute(PropertyAttribute.Quickness)]  = (ClassAbilityClass.Rogue, 2),
            [ClassIdForSkill(Skill.MeleeDefense)]               = (ClassAbilityClass.Rogue, 2),
            // Vanguard
            [ClassIdForAttribute(PropertyAttribute.Endurance)]  = (ClassAbilityClass.Vanguard, 2),
            [ClassIdForSkill(Skill.HeavyWeapons)]                = (ClassAbilityClass.Vanguard, 1),
            [ClassIdForVital(PropertyAttribute2nd.MaxHealth)]   = (ClassAbilityClass.Vanguard, 3),
            // Berserker
            [ClassIdForAttribute(PropertyAttribute.Strength)]   = (ClassAbilityClass.Berserker, 2),
            [ClassIdForSkill(Skill.TwoHandedCombat)]            = (ClassAbilityClass.Berserker, 1),
            [ClassIdForVital(PropertyAttribute2nd.MaxStamina)]  = (ClassAbilityClass.Berserker, 1),
            // Archmage
            [ClassIdForSkill(Skill.WarMagic)]                   = (ClassAbilityClass.Archmage, 1),
            [ClassIdForAttribute(PropertyAttribute.Focus)]      = (ClassAbilityClass.Archmage, 2),
            [ClassIdForSkill(Skill.MagicDefense)]                = (ClassAbilityClass.Archmage, 2),
            [ClassIdForVital(PropertyAttribute2nd.MaxMana)]     = (ClassAbilityClass.Archmage, 1),
            // Void / Summon
            [ClassIdForSkill(Skill.VoidMagic)]                  = (ClassAbilityClass.VoidSummon, 1),
            [ClassIdForAttribute(PropertyAttribute.Self)]       = (ClassAbilityClass.VoidSummon, 2),
            [ClassIdForSkill(Skill.Summoning)]                  = (ClassAbilityClass.VoidSummon, 2),
            // Blood Mage. Enhanced Life Magic MOVED here from Archmage T3 (it was
            // [ClassIdForSkill(Skill.LifeMagic)] = (Archmage, 3) until 2026-08-03). Homing is a dictionary,
            // so a skill has exactly ONE home: BLOOD-MAGE-DESIGN.md sec 3a.1 rules that Blood Mage T1 is the
            // sole home and explicitly WITHDRAWS the earlier "allow it in both" recommendation. Archmage T3
            // is left one entry short by this move; the doc says it backfills with a rating, which is a
            // separate change and is NOT made here.
            [ClassIdForSkill(Skill.LifeMagic)]                  = (ClassAbilityClass.BloodMage, 1),
            // Spellsword. Enhanced Light Weapons is the class's specialty Enhanced skill at T1 (it had been
            // unhomed since Advanced Weaponry retired on 2026-08-03). Enhanced Melee Defense USED TO SIT
            // HERE at Spellsword T2 and no longer does - the 2026-09-12 overhaul returned it to Rogue T2
            // (see the Rogue block above and this dictionary's doc comment). Homing is a dictionary, so a
            // skill has exactly ONE home: re-adding a Spellsword row for Melee Defense would not double-home
            // it, it would silently take it back off Rogue and break that class's CAP total.
            [ClassIdForSkill(Skill.LightWeapons)]               = (ClassAbilityClass.Spellsword, 1),
        };

        /// <summary>Applies the class/tier homing (if any) to a freshly built Enhanced definition.</summary>
        private static void ApplyHoming(ClassAbilityDefinition definition)
        {
            if (Homing.TryGetValue(definition.Id, out var home))
            {
                definition.AbilityClass = home.Class;
                definition.Tier = home.Tier;
            }
        }

        /// <summary>
        /// Builds the entire Enhanced family: one definition per valid player skill, primary attribute,
        /// and vital. Called once by <see cref="ClassAbilityRegistry"/> at startup.
        /// </summary>
        public static IEnumerable<EnhancedStatAbility> GenerateAll()
        {
            foreach (var skill in SkillHelper.ValidSkills.OrderBy(s => (int)s))
            {
                // Enhanced Missile Defense RETIRED 2026-09-16 (Missile Defense ownership fix): Missile
                // Defense is owned solely by Archer Training's BundleStatAbility, and the standalone
                // Enhanced entry double-dipped with it. Its synthetic id stays reserved in
                // RetiredClassAbilities (keyed by ClassIdForSkill(Skill.MissileDefense)) so an orphaned
                // held rank or unused token still refunds; its token catalog slot stays reserved for wcid
                // stability (ClassAbilityTokenCatalog.TokenAbilities).
                if (skill == Skill.MissileDefense)
                    continue;

                var definition = new ClassAbilityDefinition
                {
                    Id = ClassIdForSkill(skill),
                    Name = "enhanced_" + skill.ToString().ToLowerInvariant(),
                    DisplayName = "Enhanced " + skill.ToSentence(),
                    Description = $"{TierText} to your base {skill.ToSentence()} skill. Counts toward wield requirements.",
                    MaxRank = MaxTier,
                    CostPerRank = SkillCost,
                    Implemented = true,
                    Category = "Enhanced Skill",
                };
                ApplyHoming(definition);
                yield return new EnhancedStatAbility(definition, EnhancedStatKind.Skill, skill: skill);
            }

            foreach (var attribute in Attributes)
            {
                var definition = new ClassAbilityDefinition
                {
                    Id = ClassIdForAttribute(attribute),
                    Name = "enhanced_" + attribute.ToString().ToLowerInvariant(),
                    DisplayName = "Enhanced " + attribute,
                    Description = $"{TierText} to your base {attribute} attribute. Not redistributable; counts toward " +
                                  "wield requirements and raises the skills and vitals it governs.",
                    MaxRank = MaxTier,
                    CostPerRank = AttributeCost,
                    Implemented = true,
                    Category = "Enhanced Attribute",
                };
                ApplyHoming(definition);
                yield return new EnhancedStatAbility(definition, EnhancedStatKind.Attribute, attribute: attribute);
            }

            foreach (var vital in Vitals)
            {
                // "MaxHealth" -> "Health" etc. for the player-facing token and name
                var shortName = vital.ToString().Replace("Max", "");

                var definition = new ClassAbilityDefinition
                {
                    Id = ClassIdForVital(vital),
                    Name = "enhanced_" + shortName.ToLowerInvariant(),
                    DisplayName = "Enhanced " + shortName,
                    Description = $"{TierText} to your base maximum {shortName}. Not redistributable; counts toward wield requirements.",
                    MaxRank = MaxTier,
                    // Health is the premium vital (2/rank); Stamina and Mana stay at 1/rank.
                    CostPerRank = vital == PropertyAttribute2nd.MaxHealth ? HealthVitalCost : VitalCost,
                    Implemented = true,
                    Category = "Enhanced Vital",
                };
                ApplyHoming(definition);
                yield return new EnhancedStatAbility(definition, EnhancedStatKind.Vital, vital: vital);
            }
        }
    }
}
