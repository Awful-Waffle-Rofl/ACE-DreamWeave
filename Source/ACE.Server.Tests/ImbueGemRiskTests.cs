using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>The shared imbue-risk rules of the Aspect Gem, Mantle and Amplifier. Pure math; no Player is built.</summary>
    [TestClass]
    public class ImbueGemRiskTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void Tunable_DefaultsTo33Percent()
        {
            Assert.AreEqual(0.33, DefaultPropertyManager.DefaultDoubleProperties[ImbueGemRisk.ChanceKey].Item, 1e-12);
        }

        [TestMethod]
        public void Chance_BaseAugAndClamp()
        {
            Assert.AreEqual(0.33, ImbueGemRisk.Chance(0.33, 0), 1e-12);
            Assert.AreEqual(0.38, ImbueGemRisk.Chance(0.33, 1), 1e-12);
            Assert.AreEqual(1.0, ImbueGemRisk.Chance(0.99, 5), 1e-12, "clamped high");
            Assert.AreEqual(0.0, ImbueGemRisk.Chance(-0.5, 0), 1e-12, "clamped low");
            Assert.AreEqual(0.0, ImbueGemRisk.Chance(double.NaN, 3), 1e-12, "NaN base is zero");
            Assert.AreEqual(0.33, ImbueGemRisk.Chance(0.33, -4), 1e-12, "negative ranks ignored");
        }

        [TestMethod]
        public void Succeeds_Edges()
        {
            Assert.IsTrue(ImbueGemRisk.Succeeds(0.33, 0.0));
            Assert.IsTrue(ImbueGemRisk.Succeeds(0.33, 0.3299));
            Assert.IsFalse(ImbueGemRisk.Succeeds(0.33, 0.33), "draw at the chance fails");
            Assert.IsFalse(ImbueGemRisk.Succeeds(0.33, 0.99));
            Assert.IsTrue(ImbueGemRisk.Succeeds(1.0, 1.0), "chance 1 always succeeds, even on the inclusive max draw");
            Assert.IsFalse(ImbueGemRisk.Succeeds(0.0, 0.0));
            Assert.IsFalse(ImbueGemRisk.Succeeds(-1.0, 0.0));
            Assert.IsFalse(ImbueGemRisk.Succeeds(double.NaN, 0.0));
        }

        [TestMethod]
        public void DisplayPercent_33And38()
        {
            Assert.AreEqual(33, ImbueGemRisk.DisplayPercent(0.33));
            Assert.AreEqual(38, ImbueGemRisk.DisplayPercent(ImbueGemRisk.Chance(0.33, 1)));
        }

        [TestMethod]
        public void SkillGate_UntrainedRefused_TrainedPasses()
        {
            Assert.AreEqual("You must be trained in Magic Item Tinkering to use the Jewelers Aspect Gem.",
                ImbueGemRisk.SkillRefusal(ImbueGemRisk.AspectSkill, SkillAdvancementClass.Untrained, "Jewelers Aspect Gem"));
            Assert.AreEqual("You must be trained in Item Tinkering to use the Rear Guard Mantle.",
                ImbueGemRisk.SkillRefusal(ImbueGemRisk.CloakGemSkill, SkillAdvancementClass.Inactive, "Rear Guard Mantle"));
            Assert.IsNull(ImbueGemRisk.SkillRefusal(ImbueGemRisk.AspectSkill, SkillAdvancementClass.Trained, "x"));
            Assert.IsNull(ImbueGemRisk.SkillRefusal(ImbueGemRisk.CloakGemSkill, SkillAdvancementClass.Specialized, "x"));
        }

        [TestMethod]
        public void Resolve_ApplyDestroyAbort()
        {
            Assert.AreEqual(ImbueGemOutcome.Apply, ImbueGemRisk.Resolve(true, true));
            Assert.AreEqual(ImbueGemOutcome.Destroy, ImbueGemRisk.Resolve(true, false));
            Assert.AreEqual(ImbueGemOutcome.Abort, ImbueGemRisk.Resolve(false, true));
            Assert.AreEqual(ImbueGemOutcome.Abort, ImbueGemRisk.Resolve(false, false));
        }

        [TestMethod]
        public void Text_Verbatim()
        {
            Assert.AreEqual(" You have a 33% chance to succeed. If it fails, the Test Ring is destroyed.", ImbueGemRisk.ChanceSuffix(33, "Test Ring"));
            Assert.AreEqual("The Jewelers Aspect Gem fails to take and the Test Ring is destroyed.", ImbueGemRisk.FailureText("Jewelers Aspect Gem", "Test Ring"));
        }
    }
}