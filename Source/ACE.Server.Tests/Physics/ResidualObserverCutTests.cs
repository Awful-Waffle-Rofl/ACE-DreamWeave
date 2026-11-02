using System;
using System.Collections.Generic;
using System.IO;

using ACE.Server.Entity;
using ACE.Server.Physics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Physics
{
    /// <summary>
    /// The teleport half of the one-way visibility fix. ObjectMaint.ExpireObject keeps both broadcast links after a
    /// one-way expiry, but every teleport flush arm walks only KnownObjects, so those links would survive the
    /// teleporter's move into another instance and leak its broadcasts back. Player.Teleport therefore runs
    /// Player.CutResidualBroadcastLinks after the flush on every scope: ObjectMaint.CutResidualObservers cuts the links
    /// both ways, and TeleportFlushSteps.ApplyResidualObservers sends the observer delete when the scope's own flush
    /// would (TeleportFlushPolicy.ResidualObserverDeletes).
    ///
    /// No test can build a live Player, so these run the same three production pieces Player.CutResidualBroadcastLinks
    /// calls, on bare PhysicsObjs, and a source check binds that method and its call site in Teleport.
    /// </summary>
    [TestClass]
    public class ResidualObserverCutTests
    {
        private const uint MatchInstance = 0x800165AE;

        private const uint RozId = 0x50000045;
        private const uint KestrelId = 0x5A000100;

        // Kestrel leaving the battleground instance (0x003C) for his exit stamp in instance 0 (0x016C)
        private static readonly ulong MatchLandblock = ((ulong)MatchInstance << 32) | 0x003CFFFFu;
        private static readonly ulong ExitLandblock = ((ulong)0 << 32) | 0x016CFFFFu;

        private static PhysicsObj MakePlayer(uint id, uint instance)
        {
            return new PhysicsObj { ID = id, KnownInstance = instance };
        }

        private static bool KnowsPlayer(PhysicsObj owner, PhysicsObj player)
        {
            return owner.ObjMaint.GetKnownPlayersWhere(kvp => kvp.Key == player.ID).Count == 1;
        }

        private static void MakeMutuallyVisible(PhysicsObj a, PhysicsObj b)
        {
            a.ObjMaint.AddVisibleObject(b);
            a.ObjMaint.AddKnownObject(b);
            b.ObjMaint.AddVisibleObject(a);
            b.ObjMaint.AddKnownObject(a);
        }

        private static void ExpireFromQueue(PhysicsObj owner, PhysicsObj target)
        {
            var savedTicks = Timers.PortalYearTicks;
            try
            {
                Timers.PortalYearTicks = 1000.0;
                owner.ObjMaint.AddObjectToBeDestroyed(target);
                Timers.PortalYearTicks = 1000.0 + 26.0;
                CollectionAssert.Contains(owner.ObjMaint.DestroyObjects(), target);
            }
            finally
            {
                Timers.PortalYearTicks = savedTicks;
            }
        }

        /// <summary>
        /// Player.CutResidualBroadcastLinks's gate and its two pieces, recording the client half. Defaults to Kestrel
        /// leaving the match instance for his exit in instance 0.
        /// </summary>
        private static int Teleport(PhysicsObj teleporter, TeleportFlushScope scope, List<PhysicsObj> deleted, List<PhysicsObj> forgotten, ulong? destination = null)
        {
            var to = destination ?? ExitLandblock;

            if (!TeleportFlushPolicy.CutsResidualObservers(MatchLandblock, to))
                return 0;

            var cut = teleporter.ObjMaint.CutResidualObservers();

            TeleportFlushSteps.ApplyResidualObservers(cut, TeleportFlushPolicy.ResidualObserverDeletes(scope, false, MatchLandblock, to),
                deleted.Add, forgotten.Add);

            return cut.Count;
        }

        private static readonly TeleportFlushScope[] AllScopes = { TeleportFlushScope.Off, TeleportFlushScope.Legacy, TeleportFlushScope.Ledger };

        // the same match instance, the neighbouring landblock: a landblock change that is NOT an instance change
        private static readonly ulong SameInstanceOtherLandblock = ((ulong)MatchInstance << 32) | 0x003DFFFFu;

        /// <summary>Roz still sees Kestrel; Kestrel's one-way expiry kept both links (ObjectMaintOneWayVisibilityTests).</summary>
        private static (PhysicsObj roz, PhysicsObj kestrel) OneWayPair()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);
            ExpireFromQueue(kestrel, roz);

            Assert.IsTrue(KnowsPlayer(kestrel, roz) && KnowsPlayer(roz, kestrel), "precondition: the expiry kept both links");
            Assert.IsFalse(kestrel.ObjMaint.KnownObjectsContainsKey(roz.ID), "precondition: so the flush, which walks KnownObjects, never sees Roz");

            return (roz, kestrel);
        }

        [TestMethod]
        public void TeleportAfterAOneWayExpiry_CutsBothLinks_AndQueuesRozADelete()
        {
            var (roz, kestrel) = OneWayPair();
            var deleted = new List<PhysicsObj>();
            var forgotten = new List<PhysicsObj>();

            Assert.AreEqual(1, Teleport(kestrel, TeleportFlushScope.Legacy, deleted, forgotten));

            Assert.IsFalse(KnowsPlayer(kestrel, roz), "Kestrel's broadcasts must no longer reach Roz across instances");
            Assert.IsFalse(KnowsPlayer(roz, kestrel), "Roz's broadcasts must no longer reach Kestrel across instances");
            Assert.IsFalse(roz.ObjMaint.KnownObjectsContainsKey(kestrel.ID));
            Assert.IsFalse(roz.ObjMaint.VisibleObjectsContainsKey(kestrel.ID));

            CollectionAssert.AreEqual(new[] { roz }, deleted, "Roz's client still drew Kestrel, so scope 1 deletes him there");
            Assert.AreEqual(0, forgotten.Count);
        }

        [TestMethod]
        public void TeleportAfterAOneWayExpiry_UnderScope0_StillCutsBothLinks_ButSendsNoDelete()
        {
            var (roz, kestrel) = OneWayPair();
            var deleted = new List<PhysicsObj>();
            var forgotten = new List<PhysicsObj>();

            Assert.AreEqual(1, Teleport(kestrel, TeleportFlushScope.Off, deleted, forgotten));

            // the links are a correctness issue: cut on every scope
            Assert.IsFalse(KnowsPlayer(kestrel, roz));
            Assert.IsFalse(KnowsPlayer(roz, kestrel));
            Assert.IsFalse(roz.ObjMaint.KnownObjectsContainsKey(kestrel.ID));

            // only the delete follows the scope: scope 0's silent purge sends observers none, it only forgets
            Assert.AreEqual(0, deleted.Count);
            CollectionAssert.AreEqual(new[] { roz }, forgotten);
        }

        [TestMethod]
        public void Teleport_WithNothingLeftOver_CutsNothing_AndSendsNoExtraDelete()
        {
            // a normal, mutually known pair: the scope's own flush handles it, so the residual pass must not
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);

            var deleted = new List<PhysicsObj>();
            var forgotten = new List<PhysicsObj>();

            Assert.AreEqual(0, Teleport(kestrel, TeleportFlushScope.Legacy, deleted, forgotten));

            Assert.AreEqual(0, deleted.Count);
            Assert.AreEqual(0, forgotten.Count);
            Assert.IsTrue(KnowsPlayer(kestrel, roz) && KnowsPlayer(roz, kestrel), "the pass must not touch a pair the flush owns");
            Assert.IsTrue(kestrel.ObjMaint.KnownObjectsContainsKey(roz.ID));
        }

        // one row per scope, so each scope passes or fails on its own and none can mask another
        [DataTestMethod]
        [DataRow(TeleportFlushScope.Off)]
        [DataRow(TeleportFlushScope.Legacy)]
        [DataRow(TeleportFlushScope.Ledger)]
        public void CutLeftoverObserver_ThatNoLongerDrawsTheTeleporter_GetsNoDelete(TeleportFlushScope scope)
        {
            // a fresh pair: Kestrel's broadcast list names Roz, but Roz does not hold Kestrel, so there is nothing to
            // delete on her client
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            Assert.IsTrue(kestrel.ObjMaint.EnsureKnownPlayer(roz));

            var deleted = new List<PhysicsObj>();
            var forgotten = new List<PhysicsObj>();

            Assert.AreEqual(1, Teleport(kestrel, scope, deleted, forgotten), "the leftover link is cut on this scope");

            Assert.AreEqual(0, deleted.Count);
            Assert.AreEqual(0, forgotten.Count);
            Assert.IsFalse(KnowsPlayer(kestrel, roz));
        }

        [TestMethod]
        public void SameInstanceLandblockChange_CutsNothing_OnAnyScope()
        {
            foreach (var scope in AllScopes)
            {
                // the leftover link is valid inside one instance: the self-heal and the expiry manage it
                var (roz, kestrel) = OneWayPair();
                var deleted = new List<PhysicsObj>();
                var forgotten = new List<PhysicsObj>();

                Assert.AreEqual(0, Teleport(kestrel, scope, deleted, forgotten, SameInstanceOtherLandblock), scope.ToString());

                Assert.IsTrue(KnowsPlayer(kestrel, roz) && KnowsPlayer(roz, kestrel), scope.ToString());
                Assert.AreEqual(0, deleted.Count, scope.ToString());
                Assert.AreEqual(0, forgotten.Count, scope.ToString());
            }
        }

        [TestMethod]
        public void ResidualObserverDeletes_FollowsEachScopesOwnObserverDeleteRule()
        {
            var sameInstanceOtherLandblock = SameInstanceOtherLandblock;

            // the cut: an instance change on every scope, never a same-instance landblock change or a move inside one copy
            foreach (var scope in AllScopes)
            {
                Assert.IsTrue(TeleportFlushPolicy.CutsResidualObservers(MatchLandblock, ExitLandblock), scope.ToString());
                Assert.IsFalse(TeleportFlushPolicy.CutsResidualObservers(MatchLandblock, sameInstanceOtherLandblock), scope.ToString());
                Assert.IsFalse(TeleportFlushPolicy.CutsResidualObservers(MatchLandblock, MatchLandblock), scope.ToString());
            }

            // scope 1: any landblock change, paced or not (observer deletes are never paced)
            Assert.IsTrue(TeleportFlushPolicy.ResidualObserverDeletes(TeleportFlushScope.Legacy, false, MatchLandblock, ExitLandblock));
            Assert.IsTrue(TeleportFlushPolicy.ResidualObserverDeletes(TeleportFlushScope.Legacy, true, MatchLandblock, ExitLandblock));
            Assert.IsTrue(TeleportFlushPolicy.ResidualObserverDeletes(TeleportFlushScope.Legacy, false, MatchLandblock, sameInstanceOtherLandblock));

            // scope 2: an instance change only
            Assert.IsTrue(TeleportFlushPolicy.ResidualObserverDeletes(TeleportFlushScope.Ledger, false, MatchLandblock, ExitLandblock));
            Assert.IsFalse(TeleportFlushPolicy.ResidualObserverDeletes(TeleportFlushScope.Ledger, false, MatchLandblock, sameInstanceOtherLandblock));

            // scope 0: never
            Assert.IsFalse(TeleportFlushPolicy.ResidualObserverDeletes(TeleportFlushScope.Off, false, MatchLandblock, ExitLandblock));
        }

        // ---------------- source binding: production runs exactly these pieces ----------------

        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");
        }

        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"method not found: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"signature is ambiguous: {signature}");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        private static bool InOrder(string body, params string[] needles)
        {
            var from = 0;

            foreach (var needle in needles)
            {
                var at = body.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0)
                    return false;

                from = at + needle.Length;
            }

            return true;
        }

        [TestMethod]
        public void Heartbeat_RunsTheSelfHeal_RightAfterTheCull()
        {
            var heartbeat = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Tick.cs"), "public override void Heartbeat(double currentUnixTime)");

            var ordered = new[]
            {
                "OnClientObjectsCulled(PhysicsObj.ObjMaint.DestroyObjects());",
                "PhysicsObj.ObjMaint.RepairInverseKnownPlayers();",
            };

            Assert.IsTrue(InOrder(heartbeat, ordered), "the heartbeat must expire, then self-heal the inverse broadcast links");
            Assert.AreEqual(1, heartbeat.Split(new[] { "RepairInverseKnownPlayers(" }, StringSplitOptions.None).Length - 1, "exactly one self-heal per heartbeat");

            // discrimination: a missing self-heal, or one moved ahead of the cull, is caught
            Assert.IsFalse(InOrder(heartbeat.Replace("PhysicsObj.ObjMaint.RepairInverseKnownPlayers();", ""), ordered));

            var swapped = heartbeat
                .Replace("PhysicsObj.ObjMaint.RepairInverseKnownPlayers();", "@@HEAL@@")
                .Replace("OnClientObjectsCulled(PhysicsObj.ObjMaint.DestroyObjects());", "PhysicsObj.ObjMaint.RepairInverseKnownPlayers();")
                .Replace("@@HEAL@@", "OnClientObjectsCulled(PhysicsObj.ObjMaint.DestroyObjects());");
            Assert.IsFalse(InOrder(swapped, ordered));
        }

        [TestMethod]
        public void Teleport_CutsResidualLinks_OnEveryScope_AfterTheFlush_BeforeTheRelocate()
        {
            var teleport = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Location.cs"),
                "public void Teleport(Position _newPosition, bool fromPortal = false, string source = null)");

            var ordered = new[]
            {
                "if (flushSettings.Scope == TeleportFlushScope.Legacy && !flushSettings.Pacing)",
                "flushCounts = FlushKnownObjectsForInstanceChange();",
                "RunTeleportFlushVariant(flushAction, originInstancedLandblock, newPosition.InstancedLandblock, out var ledgerFired);",
                "var residualCut = CutResidualBroadcastLinks(flushSettings.Scope, flushSettings.Pacing, originInstancedLandblock, newPosition.InstancedLandblock);",
                "var landblockUpdate = UpdatePlayerPosition(new Position(newPosition), true);",
                "residual={residualCut}",
            };

            Assert.IsTrue(InOrder(teleport, ordered));

            // once, outside both scope branches: the call must sit after the variant branch's closing brace
            var call = teleport.IndexOf("CutResidualBroadcastLinks(", StringComparison.Ordinal);
            Assert.AreEqual(-1, teleport.IndexOf("CutResidualBroadcastLinks(", call + 1, StringComparison.Ordinal));
            var ledgerNote = teleport.IndexOf("ledgerNote = (flushAction & TeleportFlushAction.LedgerFire) != 0", StringComparison.Ordinal);
            var branchClose = teleport.IndexOf("\n            }\n", ledgerNote, StringComparison.Ordinal);
            Assert.IsTrue(ledgerNote >= 0 && branchClose > ledgerNote && call > branchClose, "the residual cut must not be inside a scope branch");

            var helper = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_TeleportFlush.cs"),
                "private int CutResidualBroadcastLinks(TeleportFlushScope scope, bool pacing, ulong originInstancedLandblock, ulong destinationInstancedLandblock)");

            var helperOrder = new[]
            {
                "TeleportFlushPolicy.CutsResidualObservers(originInstancedLandblock, destinationInstancedLandblock)",
                "PhysicsObj.ObjMaint.CutResidualObservers();",
                "TeleportFlushSteps.ApplyResidualObservers(cut, TeleportFlushPolicy.ResidualObserverDeletes(scope, pacing, originInstancedLandblock, destinationInstancedLandblock),",
                "observerPlayer.RemoveTrackedObject(this, false);",
                "observerPlayer.ForgetObjectAndEquipped(this);",
            };

            Assert.IsTrue(InOrder(helper, helperOrder));

            // discrimination
            Assert.IsFalse(InOrder(teleport.Replace("var residualCut = CutResidualBroadcastLinks(", "var residualCut = Nothing("), ordered));
            Assert.IsFalse(InOrder(helper.Replace("observerPlayer.RemoveTrackedObject(this, false);", ""), helperOrder));
        }
    }
}
