using System;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Round 6 (owner ruling 2026-10-20): the click-to-use return portal a won Pinnacle stands beside the owner is the
    /// Thread's summoned exit, 1003604, and its use returns a Pinnacle owner to the EphemeralRealmExitTo stamp the Knot
    /// wrote, with nothing keyed on a Thread run refusing, holding or misrouting it. The live summon and Portal.ActOnUse
    /// need a live world and Player, so the call path is pinned on source and content text; the Thread exit gate's
    /// decisions are driven through their pure halves. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class PinnacleReturnPortalTests
    {
        private static string Squash(string s) => Regex.Replace(s, @"\s+", " ");

        [TestMethod]
        public void ExitWeenie_IsClickOnly_AndRoutesToTheStampedExit()
        {
            Assert.AreEqual(1003604u, ThreadDungeonRewardSpawner.SummonedExitWcid);

            var sql = Squash(PooledLootSourceText.Read("Content/sql/weenies/1003604 Thread Exit (Summoned).sql"));

            StringAssert.Contains(sql, "VALUES (1003604, 'ace1003604-dungeonexitportalclick', 7,", "WeenieType 7 = Portal");
            StringAssert.Contains(sql, "(1003604, 9025, 1) /* PortalExitInstance", "PortalExitInstance (PropertyInt 9025) routes the use to EphemeralRealmExitTo");
            StringAssert.Contains(sql, "(1003604, 12, False) /* ReportCollisions", "click-only: no walk-in collision");
            StringAssert.Contains(sql, "(1003604, 16, 32) /* ItemUseable - Remote", "usable by a click");
            StringAssert.Contains(sql, "(1003604, 54, 3.0) /* UseRadius", "a radius the owner can stand in");
            Assert.IsFalse(sql.Contains("(1003604, 9021,"), "no PortalInstancing (PropertyInt 9021): a use never spins up a new copy");
            Assert.AreEqual(9025, (int)ACE.Entity.Enum.Properties.PropertyInt.PortalExitInstance);
            Assert.AreEqual(9021, (int)ACE.Entity.Enum.Properties.PropertyInt.PortalInstancing);
        }

        [TestMethod]
        public void PortalUse_InsideAnEphemeralCopy_GoesToEphemeralRealmExitTo()
        {
            var body = Squash(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Portal.cs"),
                "public override void ActOnUse(WorldObject activator)"));

            StringAssert.Contains(body, "if ((GetProperty(PropertyInt.PortalExitInstance) ?? 0) == 1 && player.Location.IsEphemeralRealm) {");
            StringAssert.Contains(body, "var exitTo = player.GetPosition(PositionType.EphemeralRealmExitTo); if (exitTo != null) { player.SetPosition(PositionType.EphemeralRealmExitTo, null); portalDest = new Position(exitTo);");
        }

        [TestMethod]
        public void EveryKnotEntry_StampsEphemeralRealmExitTo_BeforeTheTeleport()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleEncounter.cs");
            var teleport = Squash(PooledLootSourceText.MethodBody(source, "private static void Teleport(Player player, Position to, Action onArrival = null)"));

            var stamp = teleport.IndexOf("player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(player.Location));", StringComparison.Ordinal);
            var go = teleport.IndexOf("WorldManager.ThreadSafeTeleport(player, to,", StringComparison.Ordinal);
            Assert.IsTrue(stamp >= 0 && go > stamp, $"stamp={stamp} teleport={go}");

            // both ways in (a fresh copy and every re-entry) go through that one Teleport
            var create = PooledLootSourceText.MethodBody(source, "private bool CreateCopy(Player player, uint knotGuid, WorldObject knotOrNull, int knotLevel, PinnacleDef def, bool adminEntry, IPinnacleBossScript scriptOverride, out string refusal)");
            var reenter = PooledLootSourceText.MethodBody(source, "private static bool Reenter(Player player, LiveFight lf, PinnaclePoint to, bool arm, out string refusal)");
            StringAssert.Contains(create, "Teleport(player, arrival);");
            StringAssert.Contains(reenter, "Teleport(player, ToPosition(to, lf.Fight.Instance), () =>");
        }

        /// <summary>
        /// Portal.CheckUseRequirements calls ThreadExitGuard.TryHoldExit for every portal standing in the world. For a
        /// Pinnacle owner it finds either no run at all (no Thread), or their Thread run in ANOTHER instance; in that case
        /// the prompt needs the player inside that run's instance, so it never holds, and the willing-exit marker it arms
        /// retires nothing because no run is at the instance being left. Exactly what any other portal outside a Thread does.
        /// </summary>
        [TestMethod]
        public void ThreadExitGuard_NeverHoldsOrRetiresForAPlayerOutsideTheirRun()
        {
            Assert.IsNull(ThreadDungeonManager.GetRunForMemberCore(Array.Empty<ThreadDungeonRun>(), 0x50000001u, 0x7001u),
                "no Thread run: TryHoldExit returns false at once");

            foreach (var pooled in new[] { true, false })
            foreach (var state in new[] { ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared })
            foreach (var unclaimed in new[] { true, false })
                Assert.IsFalse(ThreadExitRules.ShouldPromptExit(pooled, state, isOwner: true, insideRunInstance: false, unclaimed, anyCacheHoldsItems: true, bypassActive: false),
                    $"pooled={pooled} state={state} unclaimed={unclaimed}: never held outside the run's own instance");

            Assert.IsTrue(ThreadExitRules.ShouldPromptExit(true, ThreadDungeonRunState.Cleared, true, insideRunInstance: true, true, true, false),
                "control: the same exit inside the run's instance IS held");

            var now = DateTime.UtcNow;
            Assert.IsFalse(ThreadWillingExit.ShouldRetire(true, 0x7001u, now, now, fromInstance: 0x7001u, toInstance: 0x0001u,
                runFound: false, isGroup: false, ThreadDungeonRunState.Cleared, isOwner: true), "no run at the Pinnacle copy: no gem is retired");
            Assert.IsTrue(ThreadWillingExit.ShouldRetire(true, 0x7001u, now, now, 0x7001u, 0x0001u, true, false, ThreadDungeonRunState.Cleared, true),
                "control: leaving a cleared solo run's own copy does retire");

            var check = Squash(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadExitGuard.cs"),
                "public static bool TryHoldExit(Player player, Action reissue)"));
            StringAssert.Contains(check, "var run = ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance); if (run == null) return false;");
            StringAssert.Contains(check, "ThreadCachePlacer.IsOwnerInside(run, player)");
        }

        [TestMethod]
        public void LiveSummon_UsesTheThreadExit_AtTheOwner_NeverRots_AndCarriesNoThreadRunId()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/LivePinnacleWorld.cs"),
                "public bool SummonReturnPortal()");

            StringAssert.Contains(body, "WorldObjectFactory.CreateNewWorldObject(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.SummonedExitWcid)");
            StringAssert.Contains(body, "portal.Location = returnPortalPoint.HasValue ? ToPosition(returnPortalPoint.Value) : new Position(owner.Location, instance);");

            var rot = body.IndexOf("portal.TimeToRot = -1;", StringComparison.Ordinal);
            var enter = body.IndexOf("portal.EnterWorld()", StringComparison.Ordinal);
            var name = body.IndexOf("portal.Name = ReturnPortalName;", StringComparison.Ordinal);
            Assert.IsTrue(rot > 0 && name > 0 && enter > rot && enter > name, $"rot={rot} name={name} enter={enter}: both before EnterWorld");

            Assert.IsFalse(body.Contains("ThreadDungeonRunId"), "nothing of a Thread run keys on this portal");
            Assert.IsFalse(body.Contains("SummonedExitGuid"), "no Thread run records it");
            StringAssert.Contains(body, "ReturnPortalLine");

            Assert.AreEqual("A return portal opens beside you.", ACE.Server.Pinnacles.LivePinnacleWorld.ReturnPortalLine);
            foreach (var s in new[] { ACE.Server.Pinnacles.LivePinnacleWorld.ReturnPortalLine, ACE.Server.Pinnacles.LivePinnacleWorld.ReturnPortalAtPointLine, ACE.Server.Pinnacles.LivePinnacleWorld.ReturnPortalName })
                Assert.IsTrue(s.All(ch => ch >= 32 && ch < 127), s);
        }
    }
}
