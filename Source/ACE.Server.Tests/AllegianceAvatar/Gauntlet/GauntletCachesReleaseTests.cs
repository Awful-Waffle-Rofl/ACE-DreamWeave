using System;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The end-of-fight release of a held player to the caches, live path: a player still dying when Rewarding begins only has
    /// the destination recorded (the death chain finishes it), and the source sites that consume it are pinned, each by running
    /// its checker over the real source (null expected) and over a mutated copy (must fault).
    /// </summary>
    [TestClass]
    public class GauntletCachesReleaseTests
    {
        private const string DeathPath = "Source/ACE.Server/WorldObjects/Player_Death.cs";
        private const string HoldPath = "Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletDeathHold.cs";
        private const string TeleportSig = "public void ThreadSafeTeleportOnDeath()";

        private const string PendingBranch = "else if (GauntletDeathInProgress && PeekGauntletCachesPending() is Position gauntletCaches)";
        private const string PenBranch = "else if (GauntletDeathInProgress && GauntletDeathPen() is Position gauntletPen)";
        private const string ChainEnd = "gauntletDeathInProgress = false;\n                    CompleteGauntletCachesPending();";
        private const string EnqueueLine = "player.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() => player.ReleaseFromGauntletHoldAtCaches(caches)));";

        [ClassInitialize]
        public static void Init(TestContext _) => GauntletFighterFixtures.EnsureInitialized();

        private static Player Dying()
        {
            var p = SalvageRollerVendorTests.MakePlayer(0x50000123);
            p.IsInDeathProcess = true;
            return p;
        }

        [TestMethod]
        public void ReleaseWhileDying_RecordsTheDestination_NoTeleportNoVitals()
        {
            var p = Dying();
            var caches = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, 0x7F010001);

            Assert.IsNull(p.GauntletCachesPending, "fixture: nothing pending");
            p.ReleaseFromGauntletHoldAtCaches(caches);

            Assert.IsNotNull(p.GauntletCachesPending, "the destination waits for the death chain");
            Assert.AreEqual(84f, p.GauntletCachesPending.PositionX);
            Assert.IsFalse(p.UnderLifestoneProtection, "no arrival shield yet");
            // the bare Player has no vitals, session or world: a teleport or a vitals write here would have thrown
        }

        private sealed class StubRun : IGauntletHoldRun
        {
            public uint RunKey => 1;
            public uint Instance => 0x7F010001;
            public bool IsTerminal { get; set; }
            public bool FightOver => true;
            public Position CachesPosition => null;
            public Position PenPosition => null;
            public Position NextEntryPoint() => null;
            public double CentreGx => 0;
            public double CentreGy => 0;
            public double Radius => 0;
            public long DeathHoldSeconds => 60;
            public void OnMemberDied(Player player) { }
        }

        /// <summary>
        /// A pending destination is honoured only while its run exists and has not closed; otherwise both pending fields are cleared
        /// and the death teleport falls through to the ordinary exit branch (no false "brought to the caches" line).
        /// </summary>
        [TestMethod]
        public void PendingDestination_IsDropped_WhenTheRunIsGoneOrClosed_AndKept_WhenLive()
        {
            var at = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, 0x7F010001);
            var live = new StubRun();

            try
            {
                LiveGauntletDeathHooks.RunForInstance = _ => live;
                var p = Dying();
                p.SetGauntletCachesPending(at);
                Assert.IsNotNull(p.PeekGauntletCachesPending(), "a live run: the destination stands");
                Assert.IsNotNull(p.GauntletCachesPending, "and stays pending for the chain's last step");

                live.IsTerminal = true;
                Assert.IsNull(p.PeekGauntletCachesPending(), "a closed run: dropped");
                Assert.IsNull(p.GauntletCachesPending, "and cleared");

                LiveGauntletDeathHooks.RunForInstance = _ => null;
                p.SetGauntletCachesPending(at);
                Assert.IsNull(p.PeekGauntletCachesPending(), "no run: dropped");
                Assert.IsNull(p.GauntletCachesPending);
            }
            finally
            {
                LiveGauntletDeathHooks.ResetSeams();
            }
        }

        [TestMethod]
        public void PendingValidity_AndTheLatchReset_ArePinnedOnSource()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Gauntlet.cs");
            var peek = PooledLootSourceText.MethodBody(src, "internal Position PeekGauntletCachesPending()");
            var complete = PooledLootSourceText.MethodBody(src, "private void CompleteGauntletCachesPending()");
            var latch = PooledLootSourceText.MethodBody(src, "private bool LatchGauntletDeath()");

            Assert.IsTrue(peek.Contains("GauntletCachesPendingStillValid(at)", StringComparison.Ordinal), "the peek checks the run");
            Assert.IsTrue(complete.Contains("if (!GauntletCachesPendingStillValid(at))", StringComparison.Ordinal), "the chain's last step checks the run");
            Assert.IsFalse(complete.Replace("if (!GauntletCachesPendingStillValid(at))", "").Contains("GauntletCachesPendingStillValid", StringComparison.Ordinal), "mutation: removing the check is seen");

            var reset = latch.IndexOf("gauntletCachesPending = null;", StringComparison.Ordinal);
            var call = latch.IndexOf("LatchDeath(this", StringComparison.Ordinal);
            Assert.IsTrue(reset >= 0 && call > reset, "the latch clears a stale destination before it asks the run");
            Assert.IsTrue(latch.Contains("gauntletCachesPendingArrived = false;", StringComparison.Ordinal));
        }

        [TestMethod]
        public void DeathLatchAfterTheFight_SetsThePendingDestination_ThroughTheLiveEffect()
        {
            var p = Dying();
            var at = new Position(0xA9B40019u, 90f, 96f, 20f, 0f, 0f, 0f, 1f, 0x7F010001);
            p.SetGauntletCachesPending(at);
            Assert.AreEqual(90f, p.GauntletCachesPending.PositionX);

            var src = PooledLootSourceText.Read(HoldPath);
            Assert.IsTrue(src.Contains("public void DeferToCaches(Player player, Position caches) => player.SetGauntletCachesPending(caches);", StringComparison.Ordinal), "the live effect records it synchronously on the player");
            Assert.IsTrue(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Gauntlet.cs").Contains("var line = run.FightOver", StringComparison.Ordinal), "the player's latch reads the run's FightOver for its message");
        }

        internal static string PendingFault(string death)
        {
            var body = PooledLootSourceText.MethodBody(death, TeleportSig);
            var pending = body.IndexOf(PendingBranch, StringComparison.Ordinal);
            var pen = body.IndexOf(PenBranch, StringComparison.Ordinal);
            var fallback = body.IndexOf("else if (GauntletDeathInProgress)\n", StringComparison.Ordinal);

            if (pending < 0)
                return "ThreadSafeTeleportOnDeath does not consume the pending cache destination";

            if (pen < 0 || pending > pen || pending > fallback)
                return "the pending destination is not read before the pen and exit branches";

            if (Regex.Matches(death, Regex.Escape("CompleteGauntletCachesPending();")).Count != 1 || !death.Contains(ChainEnd, StringComparison.Ordinal))
                return "the arrival chain's last step does not finish a pending release right after the latch clears";

            return null;
        }

        [TestMethod]
        public void DeathTeleport_ConsumesThePendingDestination_BeforeThePenAndTheExit()
        {
            var death = PooledLootSourceText.Read(DeathPath);
            Assert.IsNull(PendingFault(death));

            var m = death.Replace(PendingBranch, "else if (false)");
            Assert.AreNotEqual(death, m);
            Assert.IsNotNull(PendingFault(m), "mutation: the branch deleted");

            m = death.Replace(ChainEnd, "gauntletDeathInProgress = false;");
            Assert.AreNotEqual(death, m);
            Assert.IsNotNull(PendingFault(m), "mutation: the chain's last step no longer completes it");
        }

        [TestMethod]
        public void LiveEffects_ReleaseToCaches_EnqueuesOnThePlayer()
        {
            var src = PooledLootSourceText.Read(HoldPath);
            Assert.IsTrue(src.Contains(EnqueueLine, StringComparison.Ordinal), "the live effect runs the release on the player's own queue");
            Assert.IsFalse(src.Replace(EnqueueLine, "player.ReleaseFromGauntletHoldAtCaches(caches);").Contains(EnqueueLine, StringComparison.Ordinal), "mutation: a direct call no longer matches the pin");
        }
    }
}
