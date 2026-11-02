using System;

using ACE.Server.HazardMechanics;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Review C12: a host-started run (a boss fight, IHazardHost.PlayerFacing true) never broadcasts the "[hazard #N]"
    /// developer text; the /hazard admin host (the default, false) is unchanged. A run with no landblock broadcasts
    /// nothing either way, so the gate itself is read through HazardContext.PlayerFacing and the two channel bodies
    /// are pinned in source.
    /// </summary>
    [TestClass]
    public class HazardPlayerFacingTests
    {
        private sealed class Host : IHazardHost
        {
            private readonly Func<bool> facing;

            public Host(Func<bool> facing) => this.facing = facing;

            public void OnEnded(HazardRun run, HazardOutcome outcome) { }

            public bool PlayerFacing => facing();
        }

        private sealed class DefaultHost : IHazardHost
        {
            public void OnEnded(HazardRun run, HazardOutcome outcome) { }
        }

        private static HazardContext ContextFor(IHazardHost host)
        {
            HazardMechanicRegistry.TryCreate(HazardMechanicRegistry.Wisps, out var mechanic);
            var region = HazardRegion.Rect("t", 0x0069, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);
            var run = new HazardRun(1, HazardMechanicRegistry.Wisps, "scatter", region, HazardArgs.Empty, 1, host, mechanic, null, DateTime.UtcNow);
            return run.Context;
        }

        [TestMethod]
        public void PlayerFacing_FollowsTheHost_DefaultIsTheAdminChannel()
        {
            Assert.IsFalse(ContextFor(new DefaultHost()).PlayerFacing, "an IHazardHost that says nothing is the admin host");
            Assert.IsFalse(ContextFor(null).PlayerFacing, "the null host");
            Assert.IsTrue(ContextFor(new Host(() => true)).PlayerFacing);
            Assert.IsFalse(ContextFor(new Host(() => false)).PlayerFacing);
            Assert.IsFalse(ContextFor(new Host(() => throw new InvalidOperationException("boom"))).PlayerFacing, "a throwing host is the admin default");
        }

        [TestMethod]
        public void Say_IsSilentForAPlayerFacingHost_SayPlayerOnlySpeaksForOne()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/HazardContext.cs");

            var say = PooledLootSourceText.MethodBody(src, "public void Say(string line, ChatMessageType type = ChatMessageType.WorldBroadcast)");
            var gate = say.IndexOf("if (PlayerFacing)", StringComparison.Ordinal);
            var send = say.IndexOf("Broadcast(line, type);", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0 && send > gate, "Say returns before broadcasting for a player-facing host");

            var player = PooledLootSourceText.MethodBody(src, "public void SayPlayer(string line, ChatMessageType type = ChatMessageType.Broadcast)");
            var notGate = player.IndexOf("if (!PlayerFacing)", StringComparison.Ordinal);
            var sendPlayer = player.IndexOf("Broadcast(line, type);", StringComparison.Ordinal);
            Assert.IsTrue(notGate >= 0 && sendPlayer > notGate, "SayPlayer returns before broadcasting for the admin host");
        }

        [TestMethod]
        public void Memory_KeepsItsTwoPlayerLines()
        {
            // the pinnacle host that turns these on is pinned in PinnacleLiveSourceTests
            var memory = PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/MemoryMechanic.cs");
            StringAssert.Contains(memory, "w.SayPlayer(\"Your turn.\")");
            StringAssert.Contains(memory, "w.SayPlayer($\"{MemoryRules.ColourName(e.Pad)} - correct, step {e.Step} of {e.Length}.\")");
        }
    }
}
