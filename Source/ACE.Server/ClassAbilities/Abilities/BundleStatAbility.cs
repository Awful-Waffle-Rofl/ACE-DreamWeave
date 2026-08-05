using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// A passive "bundle" class ability that grants the Enhanced-stat bonus (+10/25/50 by rank, the same
    /// TierBonus the single Enhanced family uses) to SEVERAL legacy skills at once - the six class
    /// "Training" bundles. This is where a class's peripheral scaling sources live: e.g. Rogue Training
    /// bundles Lockpick (Attack Speed), Alchemy (feeds Poison Weapon), Sneak Attack, and Deception.
    ///
    /// Like EnhancedStatAbility it needs no combat hook - the bonus is read additively in
    /// Player.GetEnhancedSkillBonus, so a bundle stacks on top of the matching single Enhanced skill.
    /// Carries IPassiveStatAbility (marker) + IStatBundleAbility (the read contract).
    /// </summary>
    public class BundleStatAbility : IClassAbility, IPassiveStatAbility, IStatBundleAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; }

        public IReadOnlyList<Skill> BundledSkills { get; }

        /// <summary>
        /// Mirrors EnhancedStatAbility.BonusForRank() - the same TierBonus table (+10/25/50), applied to
        /// several skills at once rather than one. Unit is "" (flat, applied per bundled skill). No
        /// affinity rider and no gear mod exists for this family in the shipped registry, and there is no
        /// cap.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (double)EnhancedStatAbility.BonusForRank(rank);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = skill,
                Unit = "",
                Label = "skills",
                Per = null,
                CapNote = null,
            };
        }

        private BundleStatAbility(ClassAbilityDefinition definition, Skill[] skills)
        {
            Definition = definition;
            BundledSkills = skills;
        }

        // Flat 1/1/1 (user, 2026-07-18), matching the Enhanced skill/attribute/vital family.
        private static readonly int[] BundleCost = { 1, 1, 1 };

        private static BundleStatAbility Make(ClassAbilityId id, ClassAbilityClass abilityClass, int tier, string name,
            string displayName, Skill[] skills)
        {
            var list = string.Join(", ", skills.Select(s => s.ToSentence()));
            var definition = new ClassAbilityDefinition
            {
                Id = id,
                AbilityClass = abilityClass,
                Tier = tier,
                Name = name,
                DisplayName = displayName,
                Description = $"Adds +10, then +25, then +50 at ranks 1-3 to your base {list}. Counts toward wield requirements.",
                MaxRank = EnhancedStatAbility.MaxTier,
                CostPerRank = BundleCost,
                Implemented = true,
                Category = "Bundle",
            };
            return new BundleStatAbility(definition, skills);
        }

        /// <summary>
        /// Builds every bundle skill (called once by ClassAbilityRegistry). Bundle membership follows the
        /// Phase 1 skill redistribution (2026-08-03): Advanced Weaponry and Questionable Tactics are retired
        /// (see ClassAbilityDefinition.cs), Sneak Attack and Deception moved into Rogue Training, Assess
        /// Person moved into Vanguard Training, Missile Defense moved into Archer Training.
        ///
        /// Also as of 2026-08-03: Skill.Healing is removed from Vanguard Training and Skill.CreatureEnchantment
        /// is removed from Void Training, both handed off to the Blood Mage class (a parallel build) rather
        /// than staying bundled here. No affinity is broken by either removal - affinities read the player's
        /// actual trained skill level (Transfusion reads Healing, Withering reads Creature Enchantment), not
        /// this class-ability bonus. What is lost is only the cheap class-ability way to boost those two
        /// skills, until the Blood Mage PR re-homes them.
        ///
        /// Also as of 2026-08-03: Skill.ItemEnchantment is removed from Archmage Training, handed off to
        /// the Spellsword class (a parallel build) rather than staying bundled here. No affinity is broken
        /// by this removal, for the same reason as above. THAT HANDOFF HAS NOW LANDED - Item Enchantment is
        /// bundled into Spellsword Training below, so the partition holds again and no skill sits in two
        /// bundles.
        /// </summary>
        public static IEnumerable<BundleStatAbility> GenerateAll()
        {
            yield return Make(ClassAbilityId.ArcherTraining, ClassAbilityClass.Archer, 1, "archer_training", "Archer Training",
                new[] { Skill.Fletching, Skill.AssessCreature, Skill.Run, Skill.MissileDefense });

            yield return Make(ClassAbilityId.RogueTraining, ClassAbilityClass.Rogue, 1, "rogue_training", "Rogue Training",
                new[] { Skill.Lockpick, Skill.Alchemy, Skill.SneakAttack, Skill.Deception });

            yield return Make(ClassAbilityId.VanguardTraining, ClassAbilityClass.Vanguard, 1, "vanguard_training", "Vanguard Training",
                new[] { Skill.ArmorTinkering, Skill.Shield, Skill.AssessPerson });

            yield return Make(ClassAbilityId.BerserkerTraining, ClassAbilityClass.Berserker, 1, "berserker_training", "Berserker Training",
                new[] { Skill.DualWield, Skill.Recklessness, Skill.WeaponTinkering, Skill.Salvaging });

            yield return Make(ClassAbilityId.ArchmageTraining, ClassAbilityClass.Archmage, 1, "archmage_training", "Archmage Training",
                new[] { Skill.ManaConversion, Skill.ArcaneLore, Skill.MagicItemTinkering });

            yield return Make(ClassAbilityId.VoidTraining, ClassAbilityClass.VoidSummon, 1, "void_training", "Void Training",
                new[] { Skill.Loyalty, Skill.Leadership });

            // Blood Mage T1 (BLOOD-MAGE-DESIGN.md sec 3). Healing feeds Transfusion's surplus rider; Cooking
            // is homed here as pure utility (the Run/Jump precedent from Archer Training); Creature
            // Enchantment is owned utility with no rider job in this class since rev 3.
            //
            // The bundle-overlap warning that used to sit here is RESOLVED as of the 2026-08-03 merge with
            // master: that redistribution removed Healing from Vanguard Training and Creature Enchantment
            // from Void Training, so no skill sits in two bundles any more and the additive double-dip
            // through Player.GetEnhancedSkillBonus cannot occur. This is SKILL-DISTRIBUTION.md sec 8's
            // partition actually landing, quoted in BLOOD-MAGE-DESIGN.md sec 3a.2.
            yield return Make(ClassAbilityId.BloodMageTraining, ClassAbilityClass.BloodMage, 1, "bloodmage_training", "Blood Mage Training",
                new[] { Skill.Healing, Skill.CreatureEnchantment, Skill.Cooking });

            // Spellsword T1 (SPELLSWORD-DESIGN.md sec 3). Item Enchantment is the class's affinity source -
            // it rides Spellblade's proc CHANCE - and arrives here from Archmage Training (see the class
            // doc comment). Dirty Fighting and Jump were both left unhomed by the Phase 1 redistribution
            // (Dirty Fighting when Questionable Tactics retired); Item Tinkering is owned utility. Every
            // one of the four is a single-bundle member, so the additive double-dip through
            // Player.GetEnhancedSkillBonus cannot occur.
            yield return Make(ClassAbilityId.SpellswordTraining, ClassAbilityClass.Spellsword, 1, "spellsword_training", "Spellsword Training",
                new[] { Skill.ItemEnchantment, Skill.DirtyFighting, Skill.Jump, Skill.ItemTinkering });
        }
    }
}
