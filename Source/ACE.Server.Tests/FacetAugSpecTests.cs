using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins FacetAugSpec.NormalizeHeldAugSpecSkills against the player report it fixes: "I learned the
    /// spec salvaging aug on facet 2, switched back to facet 1, and my salvaging isn't spec'd." Facet 1's
    /// stored row was captured before the augmentation and still holds Salvaging Trained; the augmentation
    /// itself is global, so the live character has it Specialized the whole time.
    ///
    /// specRanks is a small local stand-in, matching FacetPoolsTests' convention of not going through
    /// Player.CalcSkillRank (this assembly has no client dat files) - it need only be a deterministic
    /// function of Pp for these tests to tell a rewritten Ranks from an untouched one.
    /// </summary>
    [TestClass]
    public class FacetAugSpecTests
    {
        private static FacetSkillEntry Entry(Skill skill, SkillAdvancementClass sac, ushort ranks, uint pp, uint initLevel)
            => new FacetSkillEntry { Skill = skill, Sac = sac, Ranks = ranks, Pp = pp, InitLevel = initLevel };

        private static ushort SpecRanksFromPp(uint pp) => (ushort)(pp / 100);

        [TestMethod]
        public void TrainedAndAugHeld_IsRewrittenToSpecialized()
        {
            var skills = new List<FacetSkillEntry>
            {
                Entry(Skill.Salvaging, SkillAdvancementClass.Trained, ranks: 7, pp: 5300, initLevel: 0),
            };

            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(skills, skill => skill == Skill.Salvaging, SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Specialized, result[0].Sac);
            Assert.AreEqual(10u, result[0].InitLevel);
            Assert.AreEqual(SpecRanksFromPp(5300), result[0].Ranks);
            Assert.AreEqual(53u, result[0].Ranks);
            Assert.AreEqual(5300u, result[0].Pp, "Pp must never move - the augmentation specializes for free, it grants no experience.");
        }

        [TestMethod]
        public void TrainedAndAugNotHeld_IsUnchanged()
        {
            var skills = new List<FacetSkillEntry>
            {
                Entry(Skill.Salvaging, SkillAdvancementClass.Trained, ranks: 7, pp: 5300, initLevel: 0),
            };

            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(skills, skill => false, SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Trained, result[0].Sac);
            Assert.AreEqual(0u, result[0].InitLevel);
            Assert.AreEqual((ushort)7, result[0].Ranks);
            Assert.AreEqual(5300u, result[0].Pp);
        }

        [TestMethod]
        public void UntrainedAndAugHeld_IsUnchanged()
        {
            // A build that never trained the skill was never specialized by the augmentation either -
            // TrainSkill only auto-specializes on the Untrained -> Trained transition. Rewriting this would
            // grant a specialization the character never earned on this facet.
            var skills = new List<FacetSkillEntry>
            {
                Entry(Skill.Salvaging, SkillAdvancementClass.Untrained, ranks: 0, pp: 0, initLevel: 0),
            };

            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(skills, skill => true, SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Untrained, result[0].Sac);
            Assert.AreEqual(0u, result[0].InitLevel);
            Assert.AreEqual((ushort)0, result[0].Ranks);
            Assert.AreEqual(0u, result[0].Pp);
        }

        [TestMethod]
        public void AlreadySpecialized_IsUnchanged()
        {
            var skills = new List<FacetSkillEntry>
            {
                Entry(Skill.Salvaging, SkillAdvancementClass.Specialized, ranks: 53, pp: 5300, initLevel: 10),
            };

            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(skills, skill => true, SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Specialized, result[0].Sac);
            Assert.AreEqual(10u, result[0].InitLevel);
            Assert.AreEqual((ushort)53, result[0].Ranks);
            Assert.AreEqual(5300u, result[0].Pp);
        }

        [TestMethod]
        public void NonAugSkill_IsUnchangedEvenIfPredicateWouldMatchOthers()
        {
            var skills = new List<FacetSkillEntry>
            {
                Entry(Skill.WarMagic, SkillAdvancementClass.Trained, ranks: 9, pp: 8100, initLevel: 0),
            };

            // heldAugSpec here reports true for everything EXCEPT the skill under test, standing in for a
            // caller (Player.NormalizeIncomingFacetSkills) whose predicate is scoped to AugSpecSkills -
            // WarMagic is not one of the five and must never be rewritten by this helper regardless of what
            // the predicate says about it.
            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(skills, skill => skill != Skill.WarMagic, SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Trained, result[0].Sac);
            Assert.AreEqual(0u, result[0].InitLevel);
            Assert.AreEqual((ushort)9, result[0].Ranks);
            Assert.AreEqual(8100u, result[0].Pp);
        }

        [TestMethod]
        public void DoesNotMutateTheInputList()
        {
            var original = Entry(Skill.Salvaging, SkillAdvancementClass.Trained, ranks: 7, pp: 5300, initLevel: 0);
            var skills = new List<FacetSkillEntry> { original };

            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(skills, skill => true, SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Trained, original.Sac, "the caller's original entry must be left untouched");
            Assert.AreNotSame(original, result[0]);
        }

        [TestMethod]
        public void MultipleEntries_OnlyTheStaleAugSpecOneIsRewritten()
        {
            var skills = new List<FacetSkillEntry>
            {
                Entry(Skill.Salvaging, SkillAdvancementClass.Trained, ranks: 7, pp: 5300, initLevel: 0),
                Entry(Skill.ArmorTinkering, SkillAdvancementClass.Specialized, ranks: 40, pp: 4000, initLevel: 10),
                Entry(Skill.WarMagic, SkillAdvancementClass.Trained, ranks: 9, pp: 8100, initLevel: 0),
                Entry(Skill.ItemTinkering, SkillAdvancementClass.Untrained, ranks: 0, pp: 0, initLevel: 0),
            };

            var result = FacetAugSpec.NormalizeHeldAugSpecSkills(
                skills,
                skill => skill == Skill.Salvaging || skill == Skill.ArmorTinkering || skill == Skill.ItemTinkering,
                SpecRanksFromPp);

            Assert.AreEqual(SkillAdvancementClass.Specialized, result[0].Sac); // Salvaging: rewritten
            Assert.AreEqual(SkillAdvancementClass.Specialized, result[1].Sac); // ArmorTinkering: already correct
            Assert.AreEqual(SkillAdvancementClass.Trained, result[2].Sac);     // WarMagic: not an aug-spec skill
            Assert.AreEqual(SkillAdvancementClass.Untrained, result[3].Sac);   // ItemTinkering: never trained
        }
    }
}
