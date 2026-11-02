using System;

using ACE.Server.Pinnacles;
using ACE.Server.Realms;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// M2d: the four places the game calls into the Pinnacle core (Player.Die, Creature.ActOnUse, Program startup) and
    /// the realm hold the landblock heartbeat reads. Die, ActOnUse and Program need a live world or Player, so their
    /// ORDER and PRESENCE are pinned on source text; the realm hold is driven for real. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class PinnacleHookWiringTests
    {
        private sealed class FixedHold : IEphemeralHold
        {
            public TimeSpan? UnloadIntervalOverride { get; set; }
        }

        private static ThreadDungeonRun NewRun()
        {
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var spec = new DungeonGemSpec(DungeonGemSpec.Any, 185, 7, DungeonGemSpec.Any, 0, null, 0, 0).WithSeed(5);
            return new ThreadDungeonRun(0x80001234u, 0x50000010u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        [TestMethod]
        public void Die_CallsOnOwnerDied_BeforeDieChain()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Death.cs"),
                "protected override void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)");

            var threadLatch = body.IndexOf("dynamicDungeonDeathInProgress = ", StringComparison.Ordinal);
            var pvpLatch = body.IndexOf("pvpMatchDeathInProgress = LatchPvpMatchDeath(", StringComparison.Ordinal);
            var call = body.IndexOf("ACE.Server.Pinnacles.PinnacleFightManager.OnOwnerDied(this);", StringComparison.Ordinal);

            // anchored on the call (round 6 review F1): the Pinnacle penalty latch has its own try just before this one,
            // so a forward search from pvpLatch would find THAT try and stay green with OnOwnerDied's try deleted
            var guard = call > 0 ? body.LastIndexOf("try\n", call, StringComparison.Ordinal) : -1;
            var pinnacleLatch = body.IndexOf("pinnacleDeathInProgress = ACE.Server.Pinnacles.PinnacleFightManager.IsInsidePinnacleCopy(", StringComparison.Ordinal);
            Assert.IsTrue(pinnacleLatch > pvpLatch && guard > pinnacleLatch, $"OnOwnerDied's own try, after the Pinnacle latch: pinnacleLatch={pinnacleLatch} guard={guard}");
            var dieChain = body.IndexOf("var dieChain = new ActionChain();", StringComparison.Ordinal);

            Assert.IsTrue(threadLatch > 0 && pvpLatch > threadLatch, $"threadLatch={threadLatch} pvpLatch={pvpLatch}");
            Assert.IsTrue(call > pvpLatch, $"the hook runs after every penalty latch: pvpLatch={pvpLatch} call={call}");
            Assert.IsTrue(guard > pvpLatch && guard < call, $"the hook is wrapped in its own try: guard={guard} call={call}");
            Assert.IsTrue(dieChain > call, $"the hook runs before the delayed dieChain: call={call} dieChain={dieChain}");
            Assert.AreEqual(call, body.LastIndexOf("PinnacleFightManager.OnOwnerDied(", StringComparison.Ordinal) - "ACE.Server.Pinnacles.".Length, "called exactly once");
        }

        [TestMethod]
        public void EphemeralRealm_OverrideFallsBackToHold()
        {
            var realm = new EphemeralRealm(null);
            Assert.IsNull(realm.UnloadIntervalOverride, "no run, no hold: the default window");

            var hold = new FixedHold { UnloadIntervalOverride = TimeSpan.FromMinutes(11) };
            realm.Hold = hold;
            Assert.AreEqual(TimeSpan.FromMinutes(11), realm.UnloadIntervalOverride, "a hold supplies the window when there is no run");

            hold.UnloadIntervalOverride = null;
            Assert.IsNull(realm.UnloadIntervalOverride, "pulled on every read: a hold that stops qualifying lapses at once");

            hold.UnloadIntervalOverride = TimeSpan.FromMinutes(3);
            realm.Hold = null;
            Assert.IsNull(realm.UnloadIntervalOverride, "a released hold gives the default window back");

            // The run wins when both are present; Hold is only the fallback.
            var expr = PooledLootSourceText.ExpressionBody(PooledLootSourceText.Read("Source/ACE.Server/Realms/EphemeralRealm.cs"),
                "public TimeSpan? UnloadIntervalOverride");
            StringAssert.Contains(expr, "=> Run?.UnloadIntervalOverride ?? Hold?.UnloadIntervalOverride;");

            // And the landblock heartbeat pulls the realm's override on every heartbeat (not cached at load).
            var landblock = PooledLootSourceText.Read("Source/ACE.Server/Entity/Landblock.cs");
            StringAssert.Contains(landblock, "ShouldQueueForUnload(players.Count, lastActiveTime, thisHeartBeat, InnerRealmInfo?.UnloadIntervalOverride ?? UnloadInterval)");
        }

        [TestMethod]
        public void ActOnUse_RoutesHeadBeforeEmoteComment()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature.cs"),
                "public override void ActOnUse(WorldObject worldObject)");

            var guide = body.IndexOf("ThreadGuideStation.TryHandleUse(this, guided)", StringComparison.Ordinal);
            var head = body.IndexOf("if (worldObject is Player challenger && ACE.Server.Pinnacles.PinnacleFightManager.TryHandleHeadUse(this, challenger))", StringComparison.Ordinal);
            var generic = body.IndexOf("// handled in base.OnActivate -> EmoteManager.OnUse()", StringComparison.Ordinal);

            Assert.IsTrue(guide > 0 && head > guide && generic > head, $"guide={guide} head={head} generic={generic}");
            StringAssert.Contains(body.Substring(head), "return;", "a routed head use consumes the use");
        }

        [TestMethod]
        public void Program_InitializesAfterThreads()
        {
            var program = PooledLootSourceText.Read("Source/ACE.Server/Program.cs");

            var world = program.IndexOf("WorldManager.Initialize();", StringComparison.Ordinal);
            var threads = program.IndexOf("ACE.Server.ThreadDungeons.ThreadDungeonManager.Initialize();", StringComparison.Ordinal);
            var pinnacle = program.IndexOf("ACE.Server.Pinnacles.PinnacleFightManager.Initialize();", StringComparison.Ordinal);

            Assert.IsTrue(world > 0 && threads > world && pinnacle > threads, $"world={world} threads={threads} pinnacle={pinnacle}");
            Assert.AreEqual(pinnacle, program.LastIndexOf("PinnacleFightManager.Initialize();", StringComparison.Ordinal) - "ACE.Server.Pinnacles.".Length, "initialized exactly once");
        }

        [TestMethod]
        public void AttachHold_AssignsRealmHold()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleFightManager.cs");
            Assert.IsFalse(source.Contains("HoldAssigner"), "the pre-M2d delegate seam is gone");
            StringAssert.Contains(PooledLootSourceText.ExpressionBody(source, "internal static void AttachHold(Landblock landblock, IEphemeralHold hold)"),
                "AssignHold(landblock?.InnerRealmInfo, hold)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(source, "internal static bool AssignHold(EphemeralRealm realm, IEphemeralHold hold)"),
                "realm.Hold = hold;");

            var realm = new EphemeralRealm(null);
            var hold = new FixedHold { UnloadIntervalOverride = TimeSpan.FromMinutes(11) };

            Assert.IsTrue(PinnacleFightManager.AssignHold(realm, hold));
            Assert.AreSame(hold, realm.Hold);

            Assert.IsTrue(PinnacleFightManager.AssignHold(realm, null), "release");
            Assert.IsNull(realm.Hold);

            Assert.IsFalse(PinnacleFightManager.AssignHold(null, hold), "no realm, nothing to do");
            PinnacleFightManager.AttachHold(null, hold);   // a null landblock is a no-op, not a throw
        }

        [TestMethod]
        public void AssignHold_RefusesARealmThatHostsAThreadRun()
        {
            var realm = new EphemeralRealm(null) { Run = NewRun() };
            var hold = new FixedHold { UnloadIntervalOverride = TimeSpan.FromMinutes(11) };

            Assert.IsFalse(PinnacleFightManager.AssignHold(realm, hold), "Hold and Run are never set together");
            Assert.IsNull(realm.Hold);

            Assert.IsTrue(PinnacleFightManager.AssignHold(realm, null), "clearing is always allowed");
        }
    }
}
