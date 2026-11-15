using System.Collections.Generic;
using ACE.Server.Frays;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>No Frays monster dispels or reflects. Spellbook dispels and the dispel/reflect effects come off at spawn.</summary>
    [TestClass]
    public class FraysNoDispelTests
    {
        [TestMethod]
        public void StripSpells_DropsOnlyThePicked()
        {
            var book = new Dictionary<int, float> { [1] = 2f, [2] = 2f, [3] = 2f };
            Assert.AreEqual(1, FraysRules.StripSpells(book, id => id == 2));
            CollectionAssert.AreEquivalent(new[] { 1, 3 }, new List<int>(book.Keys));
            Assert.AreEqual(0, FraysRules.StripSpells(null, id => true));
        }

        [TestMethod]
        public void BannedEffects_DispelReflectAndDispelCasts()
        {
            bool IsDispel(string name) => name == "NullifyLifeMagicOther";

            Assert.IsTrue(FraysRules.BannedMonsterEffect("dispel", "dispel chance=0.2 on=hit", IsDispel));
            Assert.IsTrue(FraysRules.BannedMonsterEffect("reflect", "reflect on=hit", IsDispel));
            Assert.IsTrue(FraysRules.BannedMonsterEffect("castspell", "castspell spell=NullifyLifeMagicOther chance=0.1", IsDispel));
            Assert.IsFalse(FraysRules.BannedMonsterEffect("castspell", "castspell spell=Fireball chance=0.1", IsDispel));
            Assert.IsFalse(FraysRules.BannedMonsterEffect("flatdamage", "flatdamage type=fire amount=30", IsDispel));
            Assert.IsFalse(FraysRules.BannedMonsterEffect("riposte", "riposte chance=0.2", IsDispel));
        }

        [TestMethod]
        public void FilterSpec_KeepsTheRest()
        {
            var spec = "ward amount=2000 secs=20 on=spawn; reflect on=hit; flatdamage type=electric amount=35 chance=0.35; dispel chance=0.2";
            var kept = Creature.FilterMonsterEffectSpec(spec, (kind, _) => kind is "dispel" or "reflect", out var removed);

            Assert.AreEqual(2, removed);
            Assert.AreEqual("ward amount=2000 secs=20 on=spawn; flatdamage type=electric amount=35 chance=0.35", kept);

            Assert.AreEqual("", Creature.FilterMonsterEffectSpec("reflect on=hit", (kind, _) => kind == "reflect", out removed));
            Assert.AreEqual(1, removed);

            Assert.AreEqual(spec, Creature.FilterMonsterEffectSpec(spec, (_, _) => false, out removed));
            Assert.AreEqual(0, removed);
        }
    }
}
