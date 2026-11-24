using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// /pinnacle enter: the pure admin routing (PinnacleEncounter.DecideAdmin) and the source-level promises that an admin
    /// entry never charges a Knot, spends an entry or records a clear. No world, no PropertyManager.
    /// </summary>
    [TestClass]
    public class PinnacleAdminEntryTests
    {
        private static PinnacleEntryAction Decide(bool inside, bool exists, PinnacleFightState s, uint knotGuid = 0, bool sameKind = true,
            bool awaiting = false, bool collecting = false, bool copyLive = true)
            => PinnacleEncounter.DecideAdmin(inside, exists, s, awaiting, collecting, copyLive, knotGuid, sameKind, out _);

        [TestMethod]
        public void InsideAnInstance_IsRefused_EvenWithNoFight()
        {
            var a = PinnacleEncounter.DecideAdmin(true, false, PinnacleFightState.NotStarted, false, false, false, 0u, false, out var refusal);
            Assert.AreEqual(PinnacleEntryAction.Refuse, a);
            Assert.AreEqual(PinnacleEncounter.AdminInsideInstanceLine, refusal);
        }

        [TestMethod]
        public void NoFight_OrDeadCopy_OpensAFreshCopy()
        {
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(false, false, PinnacleFightState.NotStarted));
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(false, true, PinnacleFightState.Running, copyLive: false));
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(false, true, PinnacleFightState.Lost));
        }

        [TestMethod]
        public void OwnAdminFight_ReturnsWithoutOpeningANewOne()
        {
            Assert.AreEqual(PinnacleEntryAction.ReuseArrival, Decide(false, true, PinnacleFightState.NotStarted));
            Assert.AreEqual(PinnacleEntryAction.ResumeHeld, Decide(false, true, PinnacleFightState.Held));
            Assert.AreEqual(PinnacleEntryAction.ResumeHeld, Decide(false, true, PinnacleFightState.Running));
            Assert.AreEqual(PinnacleEntryAction.ReturnToCollect, Decide(false, true, PinnacleFightState.Won, awaiting: true));
        }

        [TestMethod]
        public void KnotBoundFight_OrAnotherKind_IsNeverResumed()
        {
            var knotBound = PinnacleEncounter.DecideAdmin(false, true, PinnacleFightState.Running, false, false, true, 0x50000001u, true, out var refusal);
            Assert.AreEqual(PinnacleEntryAction.Refuse, knotBound);
            Assert.AreEqual(PinnacleEntryRules.OtherKnotRefusal, refusal);

            Assert.AreEqual(PinnacleEntryAction.Refuse, Decide(false, true, PinnacleFightState.Held, sameKind: false));
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(false, true, PinnacleFightState.NotStarted, sameKind: false));
        }

        [TestMethod]
        public void AdminOverrides_DispatchByKind()
        {
            Assert.IsTrue(PinnacleEncounter.TryBuildOverrideScript("harbinger", ACE.Server.HazardMechanics.HazardArgs.Parse("acid_rate=0.05"), out var h, out var refusal), refusal);
            Assert.IsInstanceOfType(h, typeof(ACE.Server.Pinnacles.Harbinger.HarbingerScript));

            Assert.IsTrue(PinnacleEncounter.TryBuildOverrideScript("matron", ACE.Server.HazardMechanics.HazardArgs.Parse("flow_rate=0.05"), out var m, out refusal), refusal);
            Assert.IsInstanceOfType(m, typeof(ACE.Server.Pinnacles.Matron.MatronScript));

            Assert.IsFalse(PinnacleEncounter.TryBuildOverrideScript("grael", ACE.Server.HazardMechanics.HazardArgs.Parse("x=1"), out var g, out refusal));
            Assert.IsNull(g);
            StringAssert.Contains(refusal, "grael");

            Assert.IsFalse(PinnacleEncounter.TryBuildOverrideScript("harbinger", ACE.Server.HazardMechanics.HazardArgs.Parse("flow_rate=0.05"), out _, out refusal), "a matron key is not a harbinger key");
            Assert.IsFalse(string.IsNullOrEmpty(refusal));
            Assert.IsTrue(refusal.Length <= 250, "refusal is " + refusal.Length);

            Assert.IsFalse(PinnacleEncounter.TryBuildOverrideScript("matron", ACE.Server.HazardMechanics.HazardArgs.Parse("acid_rate=0.05"), out _, out refusal), "a harbinger key is not a matron key");
        }

        [TestMethod]
        public void TryEnterAdmin_RoutesOverridesThroughTheKindSwitch()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleEncounter.cs");
            var body = PooledLootSourceText.MethodBody(source, "internal static bool TryEnterAdmin(Player admin, string kind, int level, HazardArgs overrides,");

            StringAssert.Contains(body, "if (!TryBuildOverrideScript(kind, overrides, out script, out refusal))");
        }

        [TestMethod]
        public void TryEnterAdmin_NeverChargesAKnotOrSpendsAnEntry()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleEncounter.cs");
            var body = PooledLootSourceText.MethodBody(source, "internal static bool TryEnterAdmin(Player admin, string kind, int level, HazardArgs overrides,");

            StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex("SpendEntry|RecordCopyOpen|RefundEntry"));
            StringAssert.Contains(body, "encounter.CreateCopy(admin, 0u, null, level, def, adminEntry: true, script");
            Assert.IsFalse(body.Contains("def.Enabled"), "an admin entry ignores the row's Enabled flag");
            Assert.IsFalse(body.Contains("PinnacleTunables.Enabled"), "an admin entry ignores the kill switch");

            var create = PooledLootSourceText.MethodBody(source, "private bool CreateCopy(");
            StringAssert.Contains(create, "if (adminEntry)");
            StringAssert.Contains(create, "fight.MarkAdminEntry();");
            StringAssert.Contains(create, "if (knotOrNull != null)");
            StringAssert.Contains(create, "excludedRows: data.ExcludeRows");
        }
    }
}