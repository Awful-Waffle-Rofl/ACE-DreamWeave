using System;
using System.Collections.Generic;

using ACE.Server.Knots;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>The script registry: factories, never shared instances. Pure; no world, no PropertyManager.</summary>
    [TestClass]
    public class PinnacleFightManagerTests
    {
        private sealed class StubScript : IPinnacleBossScript
        {
            public string Kind => "stub";
            public PinnacleScriptData Data => new PinnacleScriptData();
            public int PhaseCount => 1;
            public int Kills;   // per-fight state, the reason scripts must not be shared
            public void OnStart(IPinnacleFightPort port) { }
            public void EnterPhase(int phase, IPinnacleFightPort port) { }
            public void OnEvent(PinnacleEvent e, IPinnacleFightPort port) { }
            public void RestartPhase(IPinnacleFightPort port) { }
            public string Describe() => "stub";
        }

        [TestInitialize]
        public void Init()
        {
            PinnacleFightManager.ClearForTests();
            PinnacleEncounters.Clear();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PinnacleFightManager.ClearForTests();
            PinnacleEncounters.Clear();
        }

        [TestMethod]
        public void TwoFightsOfTheSameKind_GetDistinctScriptInstances()
        {
            PinnacleFightManager.Register("stub", () => new StubScript());

            var a = PinnacleFightManager.CreateScript("stub");
            var b = PinnacleFightManager.CreateScript("stub");

            Assert.IsNotNull(a);
            Assert.IsNotNull(b);
            Assert.AreNotSame(a, b);

            ((StubScript)a).Kills = 3;
            Assert.AreEqual(0, ((StubScript)b).Kills);
        }

        [TestMethod]
        public void RegisterShippedScripts_RegistersGrael_AndEachFightGetsItsOwnScript()
        {
            PinnacleFightManager.RegisterShippedScripts();

            Assert.IsTrue(PinnacleEncounters.TryGet("grael", out _));

            var a = PinnacleFightManager.CreateScript("grael");
            var b = PinnacleFightManager.CreateScript("grael");

            Assert.IsInstanceOfType(a, typeof(ACE.Server.Pinnacles.Grael.GraelScript));
            Assert.IsNotNull(b);
            Assert.AreNotSame(a, b);
        }

        [TestMethod]
        public void RegisterShippedScripts_RegistersMatron()
        {
            PinnacleFightManager.RegisterShippedScripts();

            Assert.IsTrue(PinnacleEncounters.TryGet("matron", out _));

            var a = PinnacleFightManager.CreateScript("matron");
            var b = PinnacleFightManager.CreateScript("matron");

            Assert.IsInstanceOfType(a, typeof(ACE.Server.Pinnacles.Matron.MatronScript));
            Assert.IsNotNull(b);
            Assert.AreNotSame(a, b);
        }

        [TestMethod]
        public void Tunables_Grael_NeverThrows_AndYieldsUsableSettings()
        {
            var s = PinnacleTunables.Grael();

            Assert.IsNotNull(s);
            Assert.IsFalse(string.IsNullOrWhiteSpace(s.P2Wisps));
            Assert.IsTrue(s.P4CastSeconds >= 1);
        }
        [TestMethod]
        public void Register_AddsTheKnotEncounterForTheKind()
        {
            PinnacleFightManager.Register("stub", () => new StubScript());

            Assert.IsTrue(PinnacleEncounters.TryGet("stub", out var enc));
            Assert.IsFalse(enc.SeamChargesEntry);
        }

        [TestMethod]
        public void Register_IgnoresBlankKindAndNullFactory()
        {
            PinnacleFightManager.Register(null, () => new StubScript());
            PinnacleFightManager.Register("  ", () => new StubScript());
            PinnacleFightManager.Register("stub", null);

            Assert.IsNull(PinnacleFightManager.CreateScript("stub"));
            Assert.IsFalse(PinnacleEncounters.TryGet("stub", out _));
        }

        [TestMethod]
        public void CreateScript_UnknownKind_IsNull()
            => Assert.IsNull(PinnacleFightManager.CreateScript("nope"));

        [TestMethod]
        public void CreateScript_FactoryThrows_IsNullNotThrown()
        {
            PinnacleFightManager.Register("stub", () => throw new InvalidOperationException("boom"));

            Assert.IsNull(PinnacleFightManager.CreateScript("stub"));
        }

        [TestMethod]
        public void ResumableAttempt_NoFight_IsNone()
            => Assert.AreEqual(KnotResume.None, PinnacleFightManager.ResumableAttempt(1u, 2u));

        [TestMethod]
        public void ResumeKind_ByState()
        {
            // R1: a running or held fight is a paid return; C1: a won fight is owed until delivery, then free
            Assert.AreEqual(KnotResume.Paid, PinnacleFightManager.ResumeKind(PinnacleFightState.Running, false, false));
            Assert.AreEqual(KnotResume.Paid, PinnacleFightManager.ResumeKind(PinnacleFightState.Held, false, false));
            Assert.AreEqual(KnotResume.Owed, PinnacleFightManager.ResumeKind(PinnacleFightState.Won, true, false));
            Assert.AreEqual(KnotResume.Free, PinnacleFightManager.ResumeKind(PinnacleFightState.Won, false, true));
            Assert.AreEqual(KnotResume.None, PinnacleFightManager.ResumeKind(PinnacleFightState.Won, false, false));
            Assert.AreEqual(KnotResume.None, PinnacleFightManager.ResumeKind(PinnacleFightState.Lost, false, false));
            Assert.AreEqual(KnotResume.None, PinnacleFightManager.ResumeKind(PinnacleFightState.NotStarted, false, false));
        }

        // ---- round 6: the death-penalty latch's lookup (owner ruling 2026-10-20) ----

        private static PinnacleFight NewFight(uint instance)
            => new PinnacleFight(new StubScript(), new FakePinnacleWorld(), new PinnacleFightConfig(), ownerGuid: 0x50000001, knotGuid: 0x80000002,
                instance: instance, knotLevel: 250, seed: 1);

        [TestMethod]
        public void IsInsideCopy_TrueOnlyForTheInstanceOfAnUnendedFight()
        {
            var live = NewFight(0x7001);
            var ended = NewFight(0x7002);
            ended.Reset("test");
            Assert.IsTrue(ended.IsEnded, "control: Reset ends the fight");

            var fights = new List<PinnacleFight> { live, ended, null };

            Assert.IsTrue(PinnacleFightManager.IsInsideCopy(0x7001, fights), "inside a live copy");
            Assert.IsFalse(PinnacleFightManager.IsInsideCopy(0x7003, fights), "another instance");
            Assert.IsFalse(PinnacleFightManager.IsInsideCopy(0x7002, fights), "an ended fight's copy");
            Assert.IsFalse(PinnacleFightManager.IsInsideCopy(0, new List<PinnacleFight> { NewFight(0) }), "instance 0 is never a copy");
            Assert.IsFalse(PinnacleFightManager.IsInsideCopy(0x7001, new List<PinnacleFight>()), "no fight");
            Assert.IsFalse(PinnacleFightManager.IsInsideCopy(0x7001, null), "no registry");
        }

        [TestMethod]
        public void IsInsidePinnacleCopy_EmptyRegistry_IsFalse_AndNeverThrows()
        {
            // the live registry: ClearForTests in Init leaves it empty, so every instance reads as outside
            Assert.IsFalse(PinnacleFightManager.IsInsidePinnacleCopy(0x7001));
            Assert.IsFalse(PinnacleFightManager.IsInsidePinnacleCopy(0));
        }

        /// <summary>
        /// Player.Die cannot run in this harness (it needs a live Player, session and landblock), so the latch's ORDER and
        /// each gate are pinned on source text: latched once in Die() beside the other latches and before the first
        /// penalty site; read at the vitae gate and both item-loss gates; NOT at the enchantment purge and NOT in the
        /// death teleport (the destination stays the lifestone).
        /// </summary>
        [TestMethod]
        public void PinnacleDeathLatch_WaivesVitaeAndItemLossOnly()
        {
            var death = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Death.cs");
            var die = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(death, "protected override void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)");

            const string latch = "pinnacleDeathInProgress = ACE.Server.Pinnacles.PinnacleFightManager.IsInsidePinnacleCopy(Location.Instance);";
            var at = die.IndexOf(latch, StringComparison.Ordinal);
            var pvpLatch = die.IndexOf("pvpMatchDeathInProgress = LatchPvpMatchDeath(", StringComparison.Ordinal);
            var vitae = die.IndexOf("&& !PinnacleDeathInProgress", StringComparison.Ordinal);
            var inflict = die.IndexOf("InflictVitaePenalty();", StringComparison.Ordinal);
            var purge = die.IndexOf("if (!arenaDeath && !worldEventDeathInProgress && !PvpPlayerRules.WaivesEnchantmentPurge(", StringComparison.Ordinal);

            Assert.IsTrue(at > pvpLatch && pvpLatch > 0, $"latched beside the others: pvpLatch={pvpLatch} latch={at}");

            // wrapped in its own try: the nearest try before the latch is after the PvP latch, and nothing but the
            // try's opening brace sits between them
            var latchTry = die.LastIndexOf("try\n", at, StringComparison.Ordinal);
            Assert.IsTrue(latchTry > pvpLatch, $"the latch has its own try: pvpLatch={pvpLatch} try={latchTry} latch={at}");
            Assert.AreEqual("{", die.Substring(latchTry + 4, at - latchTry - 4).Trim(), "the try wraps the latch directly");
            Assert.AreEqual(at, die.LastIndexOf("pinnacleDeathInProgress = ACE.Server.Pinnacles", StringComparison.Ordinal), "latched once");
            Assert.IsTrue(vitae > at && inflict > vitae, $"the vitae gate reads the latch: latch={at} gate={vitae} inflict={inflict}");
            Assert.IsTrue(purge > inflict, "control: the purge gate is found");
            var purgeLine = die.Substring(purge, die.IndexOf('\n', purge) - purge);
            Assert.IsFalse(purgeLine.Contains("Pinnacle"), "the enchantment purge still happens on a Pinnacle death");

            foreach (var signature in new[]
            {
                "public List<WorldObject> CalculateDeathItems(Corpse corpse)",
                "public List<WorldObject> CalculateDeathItems_Olthoi(Corpse corpse, bool hadVitae, bool killerIsOlthoiPlayer, bool killerIsPkPlayer)"
            })
            {
                var body = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(death, signature);
                var gate = body.IndexOf("PinnacleDeathInProgress", StringComparison.Ordinal);
                var waiver = body.IndexOf("|| PvpPlayerRules.WaivesItemLoss(", StringComparison.Ordinal);
                Assert.IsTrue(gate > 0 && waiver > gate, $"{signature}: the item-loss gate reads the latch (gate={gate} waiver={waiver})");
            }

            var teleport = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(death, "public void ThreadSafeTeleportOnDeath()");
            Assert.IsFalse(teleport.Contains("PinnacleDeathInProgress"), "the destination stays the lifestone, never EphemeralRealmExitTo");

            // cleared at the end of the death sequence beside its siblings (round 6 review F4)
            StringAssert.Contains(teleport, "pinnacleDeathInProgress = false;\n                    worldEventDeathInProgress = false;");
        }

        [TestMethod]
        public void KnotExitAppraisal_ShowsNoCoordinates_BecauseTheFallbackDestinationIsIndoors()
        {
            // 1003604's static Destination (the realm-1 Aerfalle Keep fallback, Content/sql/weenies/1003604) is cell
            // 0x01F50224: an indoor cell, so Portal.UpdatePortalDestination appends nothing and the appraisal is the bare
            // name ("Knot Exit"), never a misleading coordinate
            var fallback = new ACE.Entity.Position(0x01F50224, 50f, -132.618988f, 0.005f, 0f, 0f, 0f, 1f, 0u);
            Assert.IsNull(ACE.Server.Entity.PositionExtensions.GetMapCoordStr(fallback));

            var outdoor = new ACE.Entity.Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0u);
            Assert.IsNotNull(ACE.Server.Entity.PositionExtensions.GetMapCoordStr(outdoor), "control: an outdoor destination does show coordinates");
        }

        [TestMethod]
        public void StillOpenLoginMessage_IsTheExactAsciiLine()
        {
            // C16
            Assert.AreEqual("Your pinnacle trial is being held. Use your Knot to return before it closes.", PinnacleFightManager.StillOpenLoginLine);
            Assert.AreEqual(PinnacleFightManager.StillOpenLoginLine, PinnacleFightManager.StillOpenLoginMessage(true));
            Assert.IsNull(PinnacleFightManager.StillOpenLoginMessage(false));

            foreach (var ch in PinnacleFightManager.StillOpenLoginLine)
                Assert.IsTrue(ch >= 32 && ch < 127);
        }
    }
}
