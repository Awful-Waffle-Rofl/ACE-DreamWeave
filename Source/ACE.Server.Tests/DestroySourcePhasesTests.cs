using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The phased Destroy the Source machine, driven through its port with no engine object. Every expected number is written out, not
    /// recomputed from the code under test.
    /// </summary>
    [TestClass]
    public class DestroySourcePhasesTests
    {
        private const uint BossGuid = 900;

        private sealed class FakePort : IDestroySourcePhasePort
        {
            public readonly Dictionary<uint, (uint Cur, uint Max)> Health = new Dictionary<uint, (uint, uint)>();
            public readonly List<(uint Guid, bool Immune, uint Floor)> Guards = new List<(uint, bool, uint)>();
            public readonly List<string> Lines = new List<string>();

            public void SetGuard(uint guid, bool immune, uint floorHp) => Guards.Add((guid, immune, floorHp));

            public bool TryGetHealth(uint guid, out uint current, out uint max)
            {
                if (Health.TryGetValue(guid, out var h))
                {
                    current = h.Cur;
                    max = h.Max;
                    return true;
                }

                current = 0;
                max = 0;
                return false;
            }

            public void Announce(string line) => Lines.Add(line);

            public (uint Guid, bool Immune, uint Floor) LastGuard(uint guid) => Guards.Last(g => g.Guid == guid);
        }

        private static DestroySourcePhaseSettings Settings(bool enabled = true, int phases = 10, double soft = 90, double hard = 180, int bank = 1) =>
            new DestroySourcePhaseSettings
            {
                Enabled = enabled, PhaseCount = phases, QuotaFraction = 0.75, SoftTimeoutSeconds = soft, HardTimeoutSeconds = hard, BankMax = bank
            };

        /// <summary>waveCount 4 gives quota ceil(4 x 0.75) = 3.</summary>
        private static DestroySourcePhases Build(FakePort port, DestroySourcePhaseSettings settings = null, int waveCount = 4, params uint[] sources)
        {
            var machine = new DestroySourcePhases(settings ?? Settings(), port, () => waveCount);

            foreach (var s in sources)
            {
                port.Health[s] = (10000, 10000);
                machine.RegisterSource(s, guardApplied: true);
            }

            return machine;
        }

        private static void Kill(DestroySourcePhases m, int count)
        {
            for (var i = 0; i < count; i++)
                m.OnDeath((uint)(1000 + i), wasEventCreature: true, isSource: false, isBoss: false);
        }

        // ---- the floor ----------------------------------------------------------------------------------

        [TestMethod]
        public void FloorFor_TenPhasesOfEightThousand_StepsByEightHundredAndEndsAtZero()
        {
            var expected = new uint[] { 8000, 7200, 6400, 5600, 4800, 4000, 3200, 2400, 1600, 800, 0 };

            for (var k = 0; k <= 10; k++)
                Assert.AreEqual(expected[k], DestroySourcePhaseRules.FloorFor(8000, k, 10), $"phase {k}");
        }

        [TestMethod]
        public void FloorFor_FinalPhaseIsZeroNotOne_AndEarlierPhasesNeverZero()
        {
            Assert.AreEqual(0u, DestroySourcePhaseRules.FloorFor(1, 10, 10));
            Assert.AreEqual(0u, DestroySourcePhaseRules.FloorFor(7, 11, 10));
            Assert.AreEqual(1u, DestroySourcePhaseRules.FloorFor(1, 9, 10), "phase 9 of 10 on a 1 hp source still leaves 1");
        }

        [TestMethod]
        public void FloorFor_RoundsUp()
        {
            // 10001 x 9/10 = 9000.9 -> 9001
            Assert.AreEqual(9001u, DestroySourcePhaseRules.FloorFor(10001, 1, 10));
            Assert.AreEqual(10001u, DestroySourcePhaseRules.FloorFor(10001, 0, 10));
        }

        // ---- quota --------------------------------------------------------------------------------------

        [TestMethod]
        public void QuotaFor_IsCeilOfWaveCountTimesFraction_MinOne()
        {
            Assert.AreEqual(9, DestroySourcePhaseRules.QuotaFor(12, 0.75));
            Assert.AreEqual(3, DestroySourcePhaseRules.QuotaFor(4, 0.75));
            Assert.AreEqual(14, DestroySourcePhaseRules.QuotaFor(18, 0.75));
            Assert.AreEqual(1, DestroySourcePhaseRules.QuotaFor(0, 0.75));
            Assert.AreEqual(1, DestroySourcePhaseRules.QuotaFor(1, 0.1));
        }

        [TestMethod]
        public void Quota_OnlyRatchetsUp()
        {
            var waves = 12;
            var port = new FakePort();
            var m = new DestroySourcePhases(Settings(), port, () => waves);

            m.Tick(0);
            Assert.AreEqual(9, m.Quota);

            waves = 4;
            m.Tick(1);
            Assert.AreEqual(9, m.Quota, "a smaller audience never lowers it");

            waves = 20;
            m.Tick(2);
            Assert.AreEqual(15, m.Quota);
        }

        // ---- the run of phases ----------------------------------------------------------------------------

        [TestMethod]
        public void Spawns_Sealed_TheFirstTickPushesTheShieldOnceForAnAlreadyGuardedSource()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });

            m.Tick(0);

            Assert.AreEqual(0, m.Phase);
            CollectionAssert.AreEqual(new[] { (1u, true, 10000u) }, port.Guards, "one push: the shield cue plays once the source is in the world");
            Assert.AreEqual(0, port.Lines.Count);

            m.Tick(1);
            Assert.AreEqual(1, port.Guards.Count, "and only once");
        }

        [TestMethod]
        public void UnguardedRegistration_IsSealedOnTheFirstTick()
        {
            var port = new FakePort();
            var m = new DestroySourcePhases(Settings(), port, () => 4);
            port.Health[1] = (10000, 10000);
            m.RegisterSource(1, guardApplied: false);

            m.Tick(0);

            Assert.AreEqual((1u, true, 10000u), port.LastGuard(1));
        }

        [TestMethod]
        public void QuotaOfKills_OpensPhaseOne_FloorDropsTenPercent_ImmuneOff_AnnounceVerbatim()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);

            Kill(m, 2);
            m.Tick(1);
            Assert.AreEqual(0, m.Phase, "2 of 3");

            Kill(m, 1);
            m.Tick(2);

            Assert.AreEqual(1, m.Phase);
            Assert.AreEqual((1u, false, 9000u), port.LastGuard(1));
            CollectionAssert.AreEqual(new[] { "The ward falters (1 of 10)! Strike while it is down." }, port.Lines);
        }

        [TestMethod]
        public void SourceAtItsFloor_GoesImmuneAgain_AndTheRiseLineNamesTheKillsStillNeeded()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);
            Kill(m, 3);
            m.Tick(1);
            port.Lines.Clear();

            port.Health[1] = (9000, 10000);
            Kill(m, 1);
            m.Tick(2);

            Assert.AreEqual((1u, true, 9000u), port.LastGuard(1));
            CollectionAssert.AreEqual(new[] { "The ward rises again. Slay 2 more invaders to weaken it." }, port.Lines);

            m.Tick(3);
            Assert.AreEqual(1, port.Lines.Count, "the rise line comes once per phase");
        }

        [TestMethod]
        public void ImmuneIsSticky_ARegeneratingSourceDoesNotFlap()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);
            Kill(m, 3);
            m.Tick(1);

            port.Health[1] = (9000, 10000);
            m.Tick(2);
            var pushes = port.Guards.Count;

            port.Health[1] = (9100, 10000);
            m.Tick(3);
            m.Tick(4);

            Assert.AreEqual(pushes, port.Guards.Count);
            Assert.IsTrue(port.LastGuard(1).Immune);
        }

        [TestMethod]
        public void LastPhase_FloorIsExplicitZero_NotOne_AndImmuneOff_WithTheFinalLine()
        {
            var port = new FakePort();
            var m = Build(port, Settings(bank: 5), sources: new uint[] { 1 });

            // Ten boss kills = ten full phases, one per tick, draining the source to each floor in between.
            for (var k = 1; k <= 10; k++)
            {
                m.OnDeath(BossGuid, true, false, true);
                m.Tick(k);
            }

            Assert.IsTrue(m.IsFinalPhase);
            Assert.AreEqual((1u, false, 0u), port.LastGuard(1));
            Assert.AreEqual("The ward is broken. Destroy it!", port.Lines.Last());
        }

        [TestMethod]
        public void Phase_ReportsKillsPastTheFinalPhaseAsNothing()
        {
            var port = new FakePort();
            var m = Build(port, Settings(phases: 1), sources: new uint[] { 1 });
            m.OnDeath(BossGuid, true, false, true);
            m.Tick(1);
            Assert.IsTrue(m.IsFinalPhase);

            Kill(m, 30);
            m.Tick(2);

            Assert.AreEqual(1, m.Phase);
            Assert.AreEqual(0, m.PendingPhases);
        }

        // ---- banking ----------------------------------------------------------------------------------

        [TestMethod]
        public void Bank_AtMostOnePhaseAheadOfTheSlowestSource()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);

            Kill(m, 3);
            m.Tick(1);
            Assert.AreEqual(1, m.Phase);

            Kill(m, 3);
            m.Tick(2);
            Assert.AreEqual(2, m.Phase, "one phase banked ahead of a source that has used none");

            Kill(m, 3);
            m.Tick(3);
            Assert.AreEqual(2, m.Phase, "a third would be two ahead");
            Assert.AreEqual(1, m.PendingPhases);

            Kill(m, 30);
            m.Tick(4);
            Assert.AreEqual(1, m.PendingPhases, "kills past the bank earn nothing more");

            port.Health[1] = (8000, 10000);
            m.Tick(5);
            Assert.AreEqual(3, m.Phase, "the source used up phase 2, so the pending phase opens");
            Assert.AreEqual(0, m.PendingPhases);
        }

        [TestMethod]
        public void Bank_ZeroOpensOnlyAfterTheSlowestSourceUsedTheLast()
        {
            var port = new FakePort();
            var m = Build(port, Settings(bank: 0), sources: new uint[] { 1 });
            m.Tick(0);

            Kill(m, 3);
            m.Tick(1);
            Kill(m, 3);
            m.Tick(2);
            Assert.AreEqual(1, m.Phase);

            port.Health[1] = (9000, 10000);
            m.Tick(3);
            Assert.AreEqual(2, m.Phase);
        }

        [TestMethod]
        public void Bank_SlowestSourceDecides_AndAGoneSourceDoesNotBlock()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1, 2 });
            m.Tick(0);
            Kill(m, 3);
            m.Tick(1);
            port.Health[1] = (9000, 10000);
            Kill(m, 3);
            m.Tick(2);
            Assert.AreEqual(2, m.Phase);

            port.Health[1] = (8000, 10000);
            Kill(m, 3);
            m.Tick(3);
            Assert.AreEqual(2, m.Phase, "source 2 has used nothing");

            m.OnDeath(2, true, true, false);
            port.Health.Remove(2);
            m.Tick(4);
            Assert.AreEqual(3, m.Phase, "a dead source no longer holds the phase back");
        }

        // ---- timeouts ---------------------------------------------------------------------------------

        [TestMethod]
        public void SoftTimeout_QuotaFallsToTheKillsMade_NeedsAtLeastOne()
        {
            var port = new FakePort();
            var m = Build(port, waveCount: 12, sources: new uint[] { 1 });
            m.Tick(0);
            Assert.AreEqual(9, m.Quota);

            m.Tick(91);
            Assert.AreEqual(0, m.Phase, "no kill at all: the soft timeout cannot open a phase");

            Kill(m, 2);
            m.Tick(92);
            Assert.AreEqual(1, m.Phase, "two kills against a quota of nine, past the soft timeout");
        }

        [TestMethod]
        public void SoftTimeout_NotYet_NothingOpens()
        {
            var port = new FakePort();
            var m = Build(port, waveCount: 12, sources: new uint[] { 1 });
            m.Tick(0);
            Kill(m, 2);
            m.Tick(89);

            Assert.AreEqual(0, m.Phase);
        }

        [TestMethod]
        public void HardTimeout_OpensWithNoKills()
        {
            var port = new FakePort();
            var m = Build(port, waveCount: 12, sources: new uint[] { 1 });
            m.Tick(0);
            m.Tick(179);
            Assert.AreEqual(0, m.Phase);

            m.Tick(180);
            Assert.AreEqual(1, m.Phase);
            Assert.AreEqual((1u, false, 9000u), port.LastGuard(1));
        }

        [TestMethod]
        public void Timeouts_RestartFromEachEarnedPhase()
        {
            var port = new FakePort();
            var m = Build(port, waveCount: 12, sources: new uint[] { 1 });
            m.Tick(0);
            m.Tick(180);
            Assert.AreEqual(1, m.Phase);

            m.Tick(200);
            m.Tick(359);
            Assert.AreEqual(1, m.Phase, "the clock restarted at 180");

            m.Tick(360);
            Assert.AreEqual(2, m.Phase);
        }

        [TestMethod]
        public void Timeouts_DoNotRunWhileAnEarnedPhaseIsWaitingOnTheBank()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);
            Kill(m, 3);
            m.Tick(1);
            Kill(m, 3);
            m.Tick(2);
            Kill(m, 3);
            m.Tick(3);
            Assert.AreEqual(2, m.Phase);
            Assert.AreEqual(1, m.PendingPhases);

            m.Tick(1000);
            Assert.AreEqual(2, m.Phase);
            Assert.AreEqual(1, m.PendingPhases, "a hard timeout does not stack a second pending phase");
        }

        [TestMethod]
        public void BankHeldCredit_OpensAtTheUnblock_AndTheHardTimeoutRunsFromThatOpening()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);
            Kill(m, 3);
            m.Tick(1);
            Kill(m, 3);
            m.Tick(2);
            Kill(m, 3);
            m.Tick(3);
            Assert.AreEqual(2, m.Phase);
            Assert.AreEqual(1, m.PendingPhases, "a credit is held by the bank");

            m.Tick(200);
            Assert.AreEqual(2, m.Phase, "still held at t=200");

            port.Health[1] = (8000, 10000);
            m.Tick(200.5);
            Assert.AreEqual(3, m.Phase, "the unblock opens the held phase");

            m.Tick(201);
            Assert.AreEqual(3, m.Phase, "the hard timeout does not open another phase one second later");

            port.Health[1] = (7200, 10000);
            m.Tick(200.5 + 179);
            Assert.AreEqual(3, m.Phase);
            m.Tick(200.5 + 180);
            Assert.AreEqual(4, m.Phase, "the clock ran from the opening");
        }

        // ---- threading ----------------------------------------------------------------------------------

        [TestMethod]
        public void OnDeath_FromManyThreads_LosesNoCountAndOverEarnsNothing()
        {
            var port = new FakePort();
            var m = Build(port, Settings(bank: 5), sources: new uint[] { 1 });
            m.Tick(0);

            // quota 3, credit cap 5: exactly 15 of 6000 racing kills are accepted, leaving no remainder.
            System.Threading.Tasks.Parallel.For(0, 6000, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 },
                i => m.OnDeath((uint)(2000 + i), true, false, false));

            Assert.AreEqual(5, m.PendingPhases);
            Assert.AreEqual(0, m.KillsInPhase);
        }

        // ---- sources, the boss, disabled ----------------------------------------------------------------

        [TestMethod]
        public void PerSource_EachDropsTenPercentOfItsOwnMax()
        {
            var port = new FakePort();
            var m = new DestroySourcePhases(Settings(), port, () => 4);
            port.Health[1] = (10000, 10000);
            port.Health[2] = (4000, 4000);
            m.RegisterSource(1, true);
            m.RegisterSource(2, true);
            m.Tick(0);

            Kill(m, 3);
            m.Tick(1);

            Assert.AreEqual((1u, false, 9000u), port.LastGuard(1));
            Assert.AreEqual((2u, false, 3600u), port.LastGuard(2));

            port.Health[2] = (3600, 4000);
            m.Tick(2);

            Assert.IsTrue(port.LastGuard(2).Immune, "source 2 is at its floor");
            Assert.IsFalse(port.LastGuard(1).Immune, "source 1 is not");
        }

        [TestMethod]
        public void BossKill_IsOneFullPhase_WithoutKills_AndIgnoresTheBank()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });
            m.Tick(0);
            Kill(m, 3);
            m.Tick(1);
            Kill(m, 3);
            m.Tick(2);
            Assert.AreEqual(2, m.Phase);

            m.OnDeath(BossGuid, true, false, true);
            m.Tick(2);

            Assert.AreEqual(3, m.Phase, "the bank would refuse a kill-earned third phase");
        }

        [TestMethod]
        public void SourceDeath_IsNotAKill()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1, 2 });
            m.Tick(0);

            m.OnDeath(1, true, true, false);
            m.OnDeath(2, true, true, false);

            Assert.AreEqual(0, m.KillsInPhase);
        }

        [TestMethod]
        public void NonEventCreature_IsNotAKill()
        {
            var port = new FakePort();
            var m = Build(port, sources: new uint[] { 1 });

            m.OnDeath(55, wasEventCreature: false, isSource: false, isBoss: false);

            Assert.AreEqual(0, m.KillsInPhase);
        }

        [TestMethod]
        public void Disabled_DoesNothing_AtAll()
        {
            var port = new FakePort();
            var m = new DestroySourcePhases(Settings(enabled: false), port, () => 4);
            port.Health[1] = (10000, 10000);
            m.RegisterSource(1, false);

            Kill(m, 50);
            m.OnDeath(BossGuid, true, false, true);
            m.Tick(0);
            m.Tick(1000);

            Assert.AreEqual(0, m.Phase);
            Assert.AreEqual(0, port.Guards.Count);
            Assert.AreEqual(0, port.Lines.Count);
        }

        // ---- text -------------------------------------------------------------------------------------

        [TestMethod]
        public void Text_IsVerbatim_AsciiOnly()
        {
            Assert.AreEqual("The ward falters ({phase} of {phases})! Strike while it is down.", DestroySourcePhaseRules.PhaseOpenText);
            Assert.AreEqual("The ward rises again. Slay {need} more invaders to weaken it.", DestroySourcePhaseRules.ReShieldText);
            Assert.AreEqual("The ward is broken. Destroy it!", DestroySourcePhaseRules.FinalPhaseText);

            foreach (var t in new[] { DestroySourcePhaseRules.PhaseOpenText, DestroySourcePhaseRules.ReShieldText, DestroySourcePhaseRules.FinalPhaseText })
                Assert.IsTrue(t.All(c => c < 128), t);

            Assert.AreEqual("The ward falters (3 of 10)! Strike while it is down.",
                DestroySourcePhaseRules.Format(DestroySourcePhaseRules.PhaseOpenText, 3, 10, 0));
            Assert.AreEqual("The ward rises again. Slay 7 more invaders to weaken it.",
                DestroySourcePhaseRules.Format(DestroySourcePhaseRules.ReShieldText, 3, 10, 7));
        }

        // ---- settings -----------------------------------------------------------------------------------

        [TestMethod]
        public void Settings_Defaults_AreTheApprovedValues()
        {
            var d = DestroySourcePhaseSettings.Defaults;

            Assert.IsTrue(d.Enabled);
            Assert.AreEqual(10, d.PhaseCount);
            Assert.AreEqual(0.75, d.QuotaFraction);
            Assert.AreEqual(90.0, d.SoftTimeoutSeconds);
            Assert.AreEqual(180.0, d.HardTimeoutSeconds);
            Assert.AreEqual(1, d.BankMax);
        }

        [TestMethod]
        public void Settings_Read_FallsBackToDefaultsWhenTheReaderThrows_AndSanitizesWhenItDoesNot()
        {
            var saved = DestroySourcePhaseSettings.Reader;

            try
            {
                DestroySourcePhaseSettings.Reader = () => throw new InvalidOperationException("cache miss");
                Assert.AreEqual(10, DestroySourcePhaseSettings.Read().PhaseCount);
                Assert.IsTrue(DestroySourcePhaseSettings.Read().Enabled);

                DestroySourcePhaseSettings.Reader = () => new DestroySourcePhaseSettings
                {
                    Enabled = false, PhaseCount = 500, QuotaFraction = double.NaN, SoftTimeoutSeconds = 400, HardTimeoutSeconds = 100, BankMax = -3
                };

                var s = DestroySourcePhaseSettings.Read();
                Assert.IsFalse(s.Enabled);
                Assert.AreEqual(100, s.PhaseCount);
                Assert.AreEqual(0.75, s.QuotaFraction);
                Assert.AreEqual(400.0, s.SoftTimeoutSeconds);
                Assert.AreEqual(400.0, s.HardTimeoutSeconds, "the hard timeout is never below the soft one");
                Assert.AreEqual(0, s.BankMax);
            }
            finally
            {
                DestroySourcePhaseSettings.Reader = saved;
            }
        }

        [TestMethod]
        public void Registry_HasTheSixRows_ScopedToDestroySource_WithTheShippedDefaults()
        {
            var rows = WorldEventSettingsRegistry.Default.Rows.Where(r => r.Scope.Equals(WorldEventSettingsRegistry.DestroySourceScope)).ToList();

            Assert.AreEqual(6, rows.Count);

            var byKey = rows.ToDictionary(r => r.PropertyName, r => r.CodeDefault);

            Assert.AreEqual("True", byKey["world_events_destroy_source_phases_enabled"]);
            Assert.AreEqual("10", byKey["world_events_destroy_source_phase_count"]);
            Assert.AreEqual("0.75", byKey["world_events_destroy_source_quota_fraction"]);
            Assert.AreEqual("90", byKey["world_events_destroy_source_soft_timeout_seconds"]);
            Assert.AreEqual("180", byKey["world_events_destroy_source_hard_timeout_seconds"]);
            Assert.AreEqual("1", byKey["world_events_destroy_source_bank_max"]);
        }
    }
}
