using System;
using System.Text.RegularExpressions;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// T4 of the Harbinger plan: the live bodies of the hazard options (on-hit slow), the grounded-owner read and the
    /// melee reach in LivePinnacleWorld. That class needs a live landblock, so ORDER and PRESENCE are pinned on source
    /// text, like MatronLiveSourceTests. Reads no PropertyManager value.
    /// </summary>
    [TestClass]
    public class HarbingerLiveSourceTests
    {
        private static string Live() => PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/LivePinnacleWorld.cs");

        private static string Body(string signature) => PooledLootSourceText.MethodBody(Live(), signature);

        [TestMethod]
        public void StartHazard_RecordsTheHitSlowByRunId_OnlyWhenOptionsCarryOne()
        {
            var body = Body("public int StartHazard(string kind, string pattern, HazardArgs args, PinnacleHazardOptions options, out string error)");

            var fail = body.IndexOf("if (run == null)", StringComparison.Ordinal);
            var gate = body.IndexOf("if (options?.HitSlow != null)", StringComparison.Ordinal);
            var record = body.IndexOf("hitSlows[run.Id] = options.HitSlow;", StringComparison.Ordinal);

            Assert.IsTrue(fail > 0 && gate > fail && record > gate, $"fail={fail} gate={gate} record={record}");
        }

        [TestMethod]
        public void OnPlayerHit_AppliesOnlyARecordedSlow_OnThisCopy()
        {
            var body = Body("void IHazardHost.OnPlayerHit(HazardRun run, Player player, int amount)");

            var head = body.IndexOf("if (!hitSlows.TryGetValue(run.Id, out var slow) || player?.Location == null || player.Location.Instance != instance) return;", StringComparison.Ordinal);
            var apply = body.IndexOf("player.ApplyPinnacleHitSlow(slow);", StringComparison.Ordinal);

            Assert.IsTrue(head >= 0 && apply > head, $"head={head} apply={apply}");
        }

        /// <summary>The body with every whitespace run collapsed to one space, so a pin can name a whole statement.</summary>
        private static string Flat(string signature) => Regex.Replace(Body(signature), @"\s+", " ");

        [TestMethod]
        public void StopHazard_ForgetsTheSlow_InsideTheKnownRunBlock_BeforeTheStop()
        {
            var body = Flat("public void StopHazard(int token)");

            var gate = body.IndexOf("if (runs.TryGetValue(token, out var run)) { runs.Remove(token);", StringComparison.Ordinal);
            var forget = body.IndexOf("hitSlows.Remove(token);", StringComparison.Ordinal);
            var stop = body.IndexOf("HazardManager.Stop(run, HazardOutcome.Stopped,", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0 && forget > gate && stop > forget, $"gate={gate} forget={forget} stop={stop}");
        }

        [TestMethod]
        public void OnEnded_ForgetsTheSlow_InsideTheLandblockAction_BeforeTheFightHears()
        {
            var body = Flat("void IHazardHost.OnEnded(HazardRun run, HazardOutcome outcome)");

            var marshal = body.IndexOf("landblock.EnqueueAction(new ActionEventDelegate(() => { runs.Remove(id);", StringComparison.Ordinal);
            var forget = body.IndexOf("hitSlows.Remove(id);", StringComparison.Ordinal);
            var tell = body.IndexOf("f?.OnHazardEnded(id, outcome); }));", StringComparison.Ordinal);

            Assert.IsTrue(marshal >= 0 && forget > marshal && tell > forget, $"marshal={marshal} forget={forget} tell={tell}");
        }

        [TestMethod]
        public void IsOwnerGrounded_RequiresNotTeleportingNotJumping()
        {
            StringAssert.Contains(Flat("public bool IsOwnerGrounded()"),
                "{ return FindOwnerOnCopy() is Player p && p.PhysicsObj != null && !p.Teleporting && !p.IsJumping; }");
        }

        [TestMethod]
        public void SetMeleeReach_WritesTheCreatureReach()
        {
            StringAssert.Contains(Flat("public void SetMeleeReach(PinnacleHandle h, float? metres)"),
                "{ if (Get(h) is Creature c) c.PinnacleMeleeReach = metres; }");
        }
    }
}
