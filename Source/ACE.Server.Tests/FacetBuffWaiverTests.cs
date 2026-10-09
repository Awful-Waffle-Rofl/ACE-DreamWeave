using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins <see cref="Player.IsBuffDependentWieldRequirement"/>, the pure decision behind the facet
    /// restore's wield waiver: a player who geared up buffed, swapped facets away, lost the buff and
    /// swapped back should not be refused re-equipping gear they were provably already wearing
    /// (CaptureFacetEquip only ever records EquippedObjects). Only the three kinds that compare a
    /// Current/MaxValue (buffed) reading - Skill, Attrib, SecondaryAttrib - are waivable; every other
    /// kind, including their Raw* (base-value) siblings, must stay strict.
    /// </summary>
    [TestClass]
    public class FacetBuffWaiverTests
    {
        [TestMethod]
        public void WaivableKinds_AreExactlyTheThreeBuffedComparisons()
        {
            Assert.IsTrue(Player.IsBuffDependentWieldRequirement(WieldRequirement.Skill));
            Assert.IsTrue(Player.IsBuffDependentWieldRequirement(WieldRequirement.Attrib));
            Assert.IsTrue(Player.IsBuffDependentWieldRequirement(WieldRequirement.SecondaryAttrib));
        }

        [TestMethod]
        public void RawCounterpartsOfWaivableKinds_AreNotWaivable()
        {
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.RawSkill));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.RawAttrib));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.RawSecondaryAttrib));
        }

        [TestMethod]
        public void EveryOtherKind_IsNotWaivable()
        {
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.Invalid));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.Level));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.Training));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.IntStat));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.BoolStat));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.CreatureType));
            Assert.IsFalse(Player.IsBuffDependentWieldRequirement(WieldRequirement.HeritageType));
        }
    }
}
