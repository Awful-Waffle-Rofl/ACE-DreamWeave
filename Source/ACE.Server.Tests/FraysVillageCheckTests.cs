using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>/frays admin village check: what kind each expected piece is.</summary>
    [TestClass]
    public class FraysVillageCheckTests
    {
        [TestMethod]
        public void KindOf_SortsNpcsChestsPortalsSignsAndDecor()
        {
            Assert.AreEqual("NPC", FraysVillage.KindOf(WeenieType.Creature));
            Assert.AreEqual("NPC", FraysVillage.KindOf(WeenieType.Vendor));
            Assert.AreEqual("chest", FraysVillage.KindOf(WeenieType.Chest));
            Assert.AreEqual("portal", FraysVillage.KindOf(WeenieType.Portal));
            Assert.AreEqual("sign", FraysVillage.KindOf(WeenieType.Book));
            Assert.AreEqual("decor", FraysVillage.KindOf(WeenieType.Generic));
        }

        [TestMethod]
        public void TheBuiltInLayout_HasTheArrivalNpcsAndTheGuideSign()
        {
            var wcids = FraysVillage.Defaults().Select(p => p.Wcid).ToList();
            CollectionAssert.Contains(wcids, FraysDefs.GuideSignWcid);
            CollectionAssert.Contains(wcids, FraysDefs.SkyFrayPortal);
            Assert.IsTrue(FraysVillage.Usage.Contains("/frays admin village check"));
        }
    }
}
