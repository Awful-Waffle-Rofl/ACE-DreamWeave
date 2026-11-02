using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Grael;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The Grael script against the recording FakePinnacleFightPort. The fake delivers no events and applies no
    /// AdvancePhase: each test calls OnEvent itself and asserts on what the script asked the port to do.
    /// </summary>
    [TestClass]
    public class GraelScriptTests
    {
        private GraelScript script;
        private FakePinnacleFightPort port;

        [TestInitialize]
        public void Init()
        {
            script = new GraelScript();
            port = new FakePinnacleFightPort(seed: 7);
        }

        // ---- helpers ----
        private void Enter(int phase)
        {
            port.Phase = phase;
            script.EnterPhase(phase, port);

            foreach (var s in port.Spawns)
                known[s.Tag] = s.Result;
        }

        // handles survive ClearRecordings (which empties port.Spawns)
        private readonly System.Collections.Generic.Dictionary<string, PinnacleHandle> known = new System.Collections.Generic.Dictionary<string, PinnacleHandle>();

        private static PinnacleEvent Died(string tag, PinnacleHandle h = default)
            => new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = h };

        private static PinnacleEvent Ended(string tag, HazardOutcome outcome)
            => new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = tag, Outcome = outcome };

        private static PinnacleEvent Timer(string tag)
            => new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = tag };

        private PinnacleHandle Spawned(string tag)
            => port.Spawns.Any(s => s.Tag == tag) ? port.Spawns.Last(s => s.Tag == tag).Result : known[tag];

        /// <summary>Kills the variant on the fake and delivers its death.</summary>
        private void KillVariant(string tag)
        {
            var h = Spawned(tag);
            port.Kill(h);
            script.OnEvent(Died(tag, h), port);
        }

        private double Arg(PinnacleHandle _, FakePinnacleFightPort.HazardCall call, string key)
            => call.Args.GetDouble(key, double.NaN, double.MinValue, double.MaxValue);

        // ================================================================ Start / P1 (with the wisps)

        [TestMethod]
        public void Start_SaysHeadLine()
        {
            script.OnStart(port);

            CollectionAssert.AreEqual(new[] { GraelData.LineStart }, port.Said);
            Assert.AreEqual("You climbed all this way to hear a dead thing speak. Then hear this: the Unsung do not stay buried.", port.Said[0]);
        }

        // Round 4 playtest (owner): the scatter wisps that were P2 run DURING the Grael body fight, looping until the body
        // dies; its death ends both. P2 is retired as a pass-through to P3.

        [TestMethod]
        public void P1_SpawnsGrael_AndStartsTheWisps()
        {
            Enter(1);

            Assert.AreEqual(1, port.Spawns.Count);
            Assert.AreEqual("grael", port.Spawns[0].Tag);
            Assert.AreEqual(1006904u, port.Spawns[0].Wcid);
            Assert.AreEqual(GraelData.GraelSpawn, port.Spawns[0].At);
            Assert.AreEqual(PinnacleTier.Boss, port.Spawns[0].Tier);

            Assert.AreEqual(1, port.Hazards.Count);
            var h = port.Hazards[0];
            Assert.AreEqual("p2", h.Tag);
            Assert.AreEqual("wisps", h.Kind);
            Assert.AreEqual("scatter", h.Pattern);
            Assert.IsTrue(h.Origin.IsNone);
            Assert.AreEqual("6", h.Args.GetString("cycles"));
            Assert.AreEqual("1.5", h.Args.GetString("move_end"));
            Assert.AreEqual("0.5", h.Args.GetString("hold_end"));
            Assert.AreEqual("0.5", h.Args.GetString("rest_end"));
            Assert.AreEqual("0", h.Args.GetString("hold_floor"));
            Assert.AreEqual("1", h.Args.GetString("follow"));
            Assert.AreEqual("2", h.Args.GetString("follow_lag"));
            Assert.IsFalse(h.Args.Has("follow_every"), "retired: the follower bursts on the crew's beat");
            Assert.IsFalse(h.Args.Has("cx"));
            Assert.IsFalse(h.Args.Has("cy"));

            // the wisps' existing line, now said as the Grael fight opens
            CollectionAssert.AreEqual(new[] { GraelData.LineP2 }, port.Said);
            CollectionAssert.AreEqual(new[] { "SpawnAdd", "Say", "StartHazard", "SetTimer" }, port.CallNames.Where(n => n != "SetAttackable" && n != "SetImmune").ToArray(), "the last is the head's early cast timer");
        }

        [TestMethod]
        public void P1_GraelDied_Advances()
        {
            Enter(1);
            script.OnEvent(Died("grael", Spawned("grael")), port);

            Assert.AreEqual(1, port.AdvanceCount);
        }

        [TestMethod]
        public void P1_WispsCompleted_LoopWhileGraelStands_AndNeverAdvance()
        {
            Enter(1);
            port.ClearRecordings();

            for (var i = 0; i < 3; i++)
                script.OnEvent(Ended("p2", HazardOutcome.Completed), port);

            Assert.AreEqual(0, port.AdvanceCount, "only the Grael body's death ends P1");
            Assert.AreEqual(3, port.Hazards.Count);
            Assert.IsTrue(port.Hazards.All(h => h.Tag == "p2" && h.Pattern == "scatter"));
        }

        [TestMethod]
        public void P1_FirstWispRunRamps_EveryRunAfterACompletionHoldsTheHardestValuesFlat()
        {
            // round 4 ruling: the first run ramps easy -> hard; every restarted run after it holds the end values flat
            Enter(1);
            var first = port.Hazards.Single().Args;
            Assert.AreEqual("2.5", first.GetString("move"));
            Assert.AreEqual("1.5", first.GetString("move_end"));
            Assert.AreEqual("1.5", first.GetString("hold"));
            Assert.AreEqual("1.0", first.GetString("rest"));

            // a retry before any run completed still ramps
            script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            script.OnEvent(Timer(GraelScript.TimerP1WispRetry), port);
            Assert.AreEqual("2.5", port.Hazards.Last().Args.GetString("move"), "the ramp has not been played yet");

            script.OnEvent(Ended("p2", HazardOutcome.Completed), port);

            foreach (var expect in new[] { ("move", "1.5"), ("hold", "0.5"), ("rest", "0.5") })
            {
                Assert.AreEqual(expect.Item2, port.Hazards.Last().Args.GetString(expect.Item1), expect.Item1 + " held at its end value");
                Assert.AreEqual(expect.Item2, port.Hazards.Last().Args.GetString(expect.Item1 + "_end"), expect.Item1 + "_end pinned to the same value");
            }

            Assert.AreEqual("6", port.Hazards.Last().Args.GetString("cycles"), "everything else unchanged");
            Assert.AreEqual(first.GetString("pct"), port.Hazards.Last().Args.GetString("pct"));

            // and it stays flat: the next loop, a retry and a restart after a hold
            script.OnEvent(Ended("p2", HazardOutcome.Completed), port);
            Assert.AreEqual("1.5", port.Hazards.Last().Args.GetString("move"));
            script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            script.OnEvent(Timer(GraelScript.TimerP1WispRetry), port);
            Assert.AreEqual("1.5", port.Hazards.Last().Args.GetString("move"));
            script.RestartPhase(port);
            Assert.AreEqual("1.5", port.Hazards.Last().Args.GetString("move"));

            // a fresh P1 ramps again
            port.ClearRecordings();
            Enter(1);
            Assert.AreEqual("2.5", port.Hazards.Single().Args.GetString("move"));
        }

        [TestMethod]
        public void P1_WispsAborted_RetryAfterAPause_Capped_AndNeverAdvance()
        {
            Enter(1);
            port.ClearRecordings();

            script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            Assert.AreEqual(0, port.Hazards.Count, "no immediate restart");
            Assert.AreEqual((GraelScript.TimerP1WispRetry, GraelScript.P5WispRetrySeconds), port.TimerSets.Single(t => t.Tag == GraelScript.TimerP1WispRetry));

            script.OnEvent(Timer(GraelScript.TimerP1WispRetry), port);
            Assert.AreEqual(1, port.Hazards.Count);
            Assert.AreEqual("p2", port.Hazards[0].Tag);

            // a second firing of the same timer starts nothing more
            script.OnEvent(Timer(GraelScript.TimerP1WispRetry), port);
            Assert.AreEqual(1, port.Hazards.Count);

            for (var i = 1; i < GraelScript.MaxP5WispRetries + 3; i++)
                script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);

            Assert.AreEqual(GraelScript.MaxP5WispRetries, port.TimerSets.Count(t => t.Tag == GraelScript.TimerP1WispRetry), "the retries are capped");
            Assert.AreEqual(0, port.AdvanceCount);

            // the cap counts CONSECUTIVE aborts only: a completed run resets it, even after the cap was reached
            script.OnEvent(Ended("p2", HazardOutcome.Completed), port);
            script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            Assert.AreEqual(GraelScript.MaxP5WispRetries + 1, port.TimerSets.Count(t => t.Tag == GraelScript.TimerP1WispRetry), "a completion resets the cap");

            // Aborted x4, Completed, Aborted: still a retry (4 + 1 = 5 timers in a fresh phase), then the cap again
            port.ClearRecordings();
            Enter(1);
            for (var i = 0; i < 4; i++)
                script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            Assert.AreEqual(4, port.TimerSets.Count(t => t.Tag == GraelScript.TimerP1WispRetry));

            script.OnEvent(Ended("p2", HazardOutcome.Completed), port);
            script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            Assert.AreEqual(5, port.TimerSets.Count(t => t.Tag == GraelScript.TimerP1WispRetry), "the abort after a completion still schedules a retry");

            for (var i = 0; i < GraelScript.MaxP5WispRetries + 2; i++)
                script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            Assert.AreEqual(4 + GraelScript.MaxP5WispRetries, port.TimerSets.Count(t => t.Tag == GraelScript.TimerP1WispRetry), "five consecutive aborts after the reset, then capped");
            Assert.AreEqual(0, port.AdvanceCount);
        }

        [TestMethod]
        public void P1_Restart_RestartsTheWisps_SpawnsNothing_AndDropsAPendingRetry()
        {
            Enter(1);
            script.OnEvent(Ended("p2", HazardOutcome.Aborted), port);
            port.ClearRecordings();

            script.RestartPhase(port);

            CollectionAssert.AreEqual(new[] { "StopHazards", "StartHazard", "SetTimer" }, port.CallNames.ToArray(), "the wisps, then the head's early cast timer");
            Assert.AreEqual("p2", port.Hazards[0].Tag);
            Assert.AreEqual("6", port.Hazards[0].Args.GetString("cycles"));
            Assert.AreEqual(0, port.Spawns.Count, "the Grael body keeps standing with its health");

            // the retry timer that survived the hold must not start a second run
            script.OnEvent(Timer(GraelScript.TimerP1WispRetry), port);
            Assert.AreEqual(1, port.Hazards.Count);
        }

        [TestMethod]
        public void P2_IsRetired_APassThroughToP3()
        {
            Enter(2);

            Assert.AreEqual(1, port.AdvanceCount, "entering P2 moves straight on");
            Assert.AreEqual(0, port.Hazards.Count);
            Assert.AreEqual(0, port.Spawns.Count);
            Assert.AreEqual(0, port.Said.Count);

            port.ClearRecordings();
            script.RestartPhase(port);
            script.OnEvent(Ended("p2", HazardOutcome.Completed), port);
            Assert.AreEqual(0, port.Calls.Count, "P2 owns nothing to restart or answer");
        }
        // ================================================================ P3

        // Owner ruling 2026-10-20: round 1 memory only; rounds 2/3/4 a memory then 5 Blue / Red / Green, immune until the
        // sequence passes; round 5 no memory, 3 of each; a failed round dispels and repeats at the same length.

        private System.Collections.Generic.List<FakePinnacleFightPort.SpawnCall> SpawnsSince(int from) => port.Spawns.Skip(from).ToList();

        private void Pass(HazardOutcome outcome = HazardOutcome.Completed) => script.OnEvent(Ended("p3-round", outcome), port);

        /// <summary>Kills every variant spawned since <paramref name="from"/> (spawn index) and delivers each death.</summary>
        private void KillAll(int from)
        {
            foreach (var s in SpawnsSince(from))
            {
                port.Kill(s.Result);
                script.OnEvent(Died(s.Tag, s.Result), port);
            }
        }

        private static readonly string[] WaveColours = { "blue", "red", "green" };
        private static readonly uint[] WaveWcids = { 1006907u, 1006905u, 1006906u };

        [TestMethod]
        public void P3_Round1_MemoryOnly_L4_NoSpawns()
        {
            Enter(3);

            Assert.AreEqual(0, port.Spawns.Count, "round 1 is a memory sequence only");
            Assert.AreEqual(1, port.Hazards.Count);
            Assert.AreEqual("p3-round", port.Hazards[0].Tag);
            Assert.AreEqual("memory", port.Hazards[0].Kind);
            Assert.AreEqual("4", port.Hazards[0].Args.GetString("len"));
            Assert.AreEqual("4", port.Hazards[0].Args.GetString("max_len"));
            Assert.IsTrue(port.Hazards[0].Origin.IsNone);
            CollectionAssert.AreEqual(new[] { GraelData.LineP3, "Round 1 of 4." }, port.Said, "the script names its own rounds (C12)");
        }

        [TestMethod]
        public void P3_Rounds2To4_FiveOfOneColour_ImmuneUntilPassed_NextWhenAllDead()
        {
            Enter(3);
            Pass();   // round 1

            for (var r = 0; r < 3; r++)
            {
                var round = r + 2;
                var from = port.Spawns.Count - GraelData.P3WaveSize;
                var wave = SpawnsSince(from);

                Assert.AreEqual(GraelData.P3WaveSize, wave.Count, $"round {round}: five spawned");
                CollectionAssert.AreEqual(Enumerable.Range(1, 5).Select(i => $"{WaveColours[r]}-{i}").ToArray(), wave.Select(s => s.Tag).ToArray(), $"round {round} tags");
                Assert.IsTrue(wave.All(s => s.Wcid == WaveWcids[r]), $"round {round} is all {WaveColours[r]}");
                Assert.IsTrue(wave.All(s => s.Tier == PinnacleTier.Elite), "Elite");
                CollectionAssert.AreEqual(GraelData.VariantPoints.Take(5).ToArray(), wave.Select(s => s.At).ToArray(), "the first five variant points");
                Assert.IsTrue(wave.All(s => port.Immune[s.Result]), $"round {round}: immune during the memory");

                var memory = port.Hazards.Last();
                Assert.AreEqual("p3-round", memory.Tag);
                Assert.AreEqual((3 + round).ToString(), memory.Args.GetString("len"), $"round {round}: L = 4 + round - 1");
                Assert.AreEqual($"Round {round} of 4.", port.Said.Last());

                Pass();
                Assert.IsTrue(wave.All(s => !port.Immune[s.Result]), $"round {round}: killable once passed");

                var spawnsBefore = port.Spawns.Count;
                var hazardsBefore = port.Hazards.Count;

                // four of five dead: the round holds
                foreach (var s in wave.Take(4))
                {
                    port.Kill(s.Result);
                    script.OnEvent(Died(s.Tag, s.Result), port);
                }

                Assert.AreEqual(spawnsBefore, port.Spawns.Count, "no next round while one lives");
                Assert.AreEqual(hazardsBefore, port.Hazards.Count);

                port.Kill(wave[4].Result);
                script.OnEvent(Died(wave[4].Tag, wave[4].Result), port);
            }

            Assert.AreEqual(0, port.AdvanceCount, "round 5 still to come");
        }

        [TestMethod]
        public void P3_Round5_NoMemory_ThreeOfEach_NotImmune_AdvancesWhenAllNineDead()
        {
            ReachRound(5);
            var from = port.Spawns.Count - 9;
            var hazards = port.Hazards.Count;
            var final = SpawnsSince(from);

            Assert.AreEqual(9, final.Count);
            CollectionAssert.AreEqual(new[] { "red-1", "red-2", "red-3", "green-1", "green-2", "green-3", "blue-1", "blue-2", "blue-3" }, final.Select(s => s.Tag).ToArray());
            CollectionAssert.AreEqual(new[] { 1006905u, 1006905u, 1006905u, 1006906u, 1006906u, 1006906u, 1006907u, 1006907u, 1006907u }, final.Select(s => s.Wcid).ToArray());
            CollectionAssert.AreEqual(GraelData.VariantPoints.ToArray(), final.Select(s => s.At).ToArray(), "all nine variant points");
            Assert.IsTrue(final.All(s => s.Tier == PinnacleTier.Elite));
            Assert.IsTrue(final.All(s => !port.Immune.TryGetValue(s.Result, out var immune) || !immune), "round 5 is killable at once");
            Assert.AreEqual(GraelData.LineP3FinalRound, port.Said.Last());

            KillAll(from);
            Assert.AreEqual(hazards, port.Hazards.Count, "round 5 starts no memory");
            Assert.AreEqual(1, port.AdvanceCount, "all nine dead: P3 ends");
        }

        [TestMethod]
        public void P3_Round5_EightDead_DoesNotAdvance()
        {
            ReachRound(5);
            var final = SpawnsSince(port.Spawns.Count - 9);

            foreach (var s in final.Take(8))
            {
                port.Kill(s.Result);
                script.OnEvent(Died(s.Tag, s.Result), port);
            }

            Assert.AreEqual(0, port.AdvanceCount);
        }

        /// <summary>Plays P3 to the START of <paramref name="round"/> (2..5), passing every memory and killing every wave.</summary>
        private void ReachRound(int round)
        {
            Enter(3);
            Pass();

            for (var r = 2; r < round; r++)
            {
                var from = port.Spawns.Count - GraelData.P3WaveSize;
                Pass();
                KillAll(from);
            }
        }

        [TestMethod]
        public void P3_FailedRound_DispelsAndRepeatsSameLength_VariantsStayAliveAndImmune()
        {
            ReachRound(3);   // 5 Red, memory at L=6
            var wave = SpawnsSince(port.Spawns.Count - 5);
            port.ClearRecordings();

            Pass(HazardOutcome.Failed);

            CollectionAssert.AreEqual(new[] { 5 }, port.Dispels);
            CollectionAssert.AreEqual(new[] { GraelData.LineMemoryFailed, "Round 3 of 4." }, port.Said, "the repeated round keeps its number");
            Assert.AreEqual(1, port.Hazards.Count);
            Assert.AreEqual("6", port.Hazards[0].Args.GetString("len"), "the same length");
            Assert.AreEqual(0, port.Spawns.Count, "no new spawns: the variants stay alive");
            Assert.AreEqual(0, port.Calls.Count(c => c.Name == "SetImmune" && (bool)c.Args[1] == false), "immunity never lifted");
            Assert.IsTrue(wave.All(s => port.Immune[s.Result] && port.IsAlive(s.Result)));
            Assert.AreEqual(0, port.AdvanceCount);
        }

        [TestMethod]
        public void P3_FailedRound1_RepeatsL4()
        {
            Enter(3);
            port.ClearRecordings();

            Pass(HazardOutcome.Failed);

            CollectionAssert.AreEqual(new[] { 5 }, port.Dispels);
            Assert.AreEqual("4", port.Hazards.Single().Args.GetString("len"));
            Assert.AreEqual(0, port.Spawns.Count, "round 2 does not begin");
        }

        [TestMethod]
        public void P3_AbortedCountsAsPassed()
        {
            Enter(3);
            Pass(HazardOutcome.Aborted);

            Assert.AreEqual(5, port.Spawns.Count, "round 2 began");
            Assert.AreEqual(0, port.Dispels.Count);

            Pass(HazardOutcome.Aborted);
            Assert.IsTrue(port.Spawns.All(s => !port.Immune[s.Result]), "an Aborted round 2 memory lifts immunity like a pass");
        }

        [TestMethod]
        public void P3_SpawnFailed_CountsAsDead()
        {
            Enter(3);
            port.FailNextSpawns = 2;   // two of round 2's five fail (the core reports each as AddDied SpawnFailed)
            Pass();

            var wave = SpawnsSince(0);
            Assert.AreEqual(2, wave.Count(s => s.Result.IsNone));

            foreach (var s in wave.Where(s => s.Result.IsNone))
                script.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = s.Tag, Handle = PinnacleHandle.None, SpawnFailed = true }, port);

            Pass();
            var spawnsBefore = port.Spawns.Count;

            foreach (var s in wave.Where(s => !s.Result.IsNone))
            {
                port.Kill(s.Result);
                script.OnEvent(Died(s.Tag, s.Result), port);
            }

            Assert.AreEqual(spawnsBefore + 5, port.Spawns.Count, "the three real kills plus two failed spawns cleared the round: round 3 began");
        }

        [TestMethod]
        public void P3_WholeWaveFailedToSpawn_StillWaitsForTheMemoryPass()
        {
            Enter(3);
            port.FailNextSpawns = 5;
            Pass();   // round 2 begins: all five spawns fail

            foreach (var s in SpawnsSince(0))
                script.OnEvent(new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = s.Tag, Handle = PinnacleHandle.None, SpawnFailed = true }, port);

            Assert.AreEqual(5, port.Spawns.Count, "no round 3 before round 2's memory is passed");

            Pass();
            Assert.AreEqual(10, port.Spawns.Count, "passed with nothing left alive: round 3 begins");
            Assert.AreEqual("6", port.Hazards.Last().Args.GetString("len"));
        }

        [TestMethod]
        public void P3_StaleDeath_FromAnotherHandle_IsIgnored()
        {
            ReachRound(2);
            Pass();
            var spawnsBefore = port.Spawns.Count;

            // five deaths carrying the right tags but a handle that is not the round's
            for (var i = 1; i <= 5; i++)
                script.OnEvent(Died($"blue-{i}", new PinnacleHandle(9999)), port);

            Assert.AreEqual(spawnsBefore, port.Spawns.Count, "a stale death clears nothing");
        }

        [TestMethod]
        public void P3_Restart_DuringMemory_RestartsMemoryOnly_NoSpawns()
        {
            ReachRound(2);   // 5 Blue, memory L=5 active
            port.ClearRecordings();

            script.RestartPhase(port);

            Assert.AreEqual("StopHazards", port.CallNames.First());
            Assert.AreEqual(0, port.Spawns.Count, "no spawns on a restart");
            Assert.AreEqual(1, port.Hazards.Count);
            Assert.AreEqual("5", port.Hazards[0].Args.GetString("len"), "the same length");
            CollectionAssert.AreEqual(new[] { "Round 2 of 4." }, port.Said);
            Assert.AreEqual(5, port.Calls.Count(c => c.Name == "SetImmune" && (bool)c.Args[1]), "the five stay immune");
        }

        [TestMethod]
        public void P3_Restart_AfterThePass_DoesNothing()
        {
            ReachRound(2);
            Pass();
            port.ClearRecordings();

            script.RestartPhase(port);

            CollectionAssert.AreEqual(new[] { "SetTimer" }, port.CallNames.ToArray(), "only the head's early cast timer is re-armed");
            Assert.AreEqual(GraelScript.TimerEarlyCast, port.TimerSets.Single().Tag);
        }

        [TestMethod]
        public void P3_PulseIs1p0AtL4And0p5AtL7()
        {
            Enter(3);
            Assert.AreEqual(1.0, Arg(default, port.Hazards[0], "pulse_on"), 1e-9);
            Assert.AreEqual(0.5, Arg(default, port.Hazards[0], "pulse_off"), 1e-9);

            ReachRound(4);
            var last = port.Hazards.Last();
            Assert.AreEqual("7", last.Args.GetString("len"));
            Assert.AreEqual(0.5, Arg(default, last, "pulse_on"), 1e-9);
            Assert.AreEqual(0.25, Arg(default, last, "pulse_off"), 1e-9);
        }

        [TestMethod]
        public void P3_MemoryPct_IsPinnedAndScaledByTheKnotLevel_WithThePadPillarOn()
        {
            port.Level = 300;
            Enter(3);

            Assert.AreEqual(GraelSettings.Defaults.ScaledPct(300, GraelSettings.MemoryPct), port.Hazards[0].Args.GetDouble("pct", -1, -1, 9), 1e-6, "the fire pillar hits like every other Grael hazard");
            Assert.AreEqual("1", port.Hazards[0].Args.GetString("pad_pillar"));
        }

        // ================================================================ P4

        // round 3 playtest (owner): the random stream runs BEFORE the rows and the cross
        private static readonly string[] P4Patterns = { "fan", "ring-alt", "random", "row", "cross" };

        private static readonly string[] P4Tags = { "p4-fan", "p4-rings", "p4-stream", "p4-row", "p4-cross" };

        [TestMethod]
        public void P4_SeriesOrder()
        {
            Enter(4);
            Assert.AreEqual("p4-fan", port.Hazards.First(h => h.Kind == "balls").Tag);

            for (var i = 0; i < P4Tags.Length - 1; i++)
                script.OnEvent(Ended(P4Tags[i], i % 2 == 0 ? HazardOutcome.Completed : HazardOutcome.Aborted), port);

            // the corner pillars are the one non-ball run (P4_Corners_*); the ball series are checked here
            CollectionAssert.AreEqual(new[] { GraelScript.TagP4Corners }, port.Hazards.Where(h => h.Kind != "balls").Select(h => h.Tag).ToArray());
            var all = port.Hazards;
            var balls = all.Where(h => h.Kind == "balls").ToList();

            CollectionAssert.AreEqual(P4Patterns, balls.Select(h => h.Pattern).ToArray());
            CollectionAssert.AreEqual(P4Tags, balls.Select(h => h.Tag).ToArray());
            CollectionAssert.AreEqual(P4Patterns, GraelScript.P4Order.ToArray(), "the script's own order");
            Assert.AreEqual(0, port.AdvanceCount);
            AssertBallSeries(balls);
        }

        private static void AssertBallSeries(System.Collections.Generic.List<FakePinnacleFightPort.HazardCall> hazards)
        {
            var byTag = hazards.ToDictionary(h => h.Tag);
            FakePinnacleFightPort.HazardCall H(string tag) => byTag[tag];

            var fan = H("p4-fan");
            var rings = H("p4-rings");
            var stream = H("p4-stream");
            var row = H("p4-row");
            var cross = H("p4-cross");

            // fan, row and cross carry the ball args (6 waves); the ONE ring series carries twice the waves, so the ring
            // waves total the 12 the old ring-out and ring-in series ran together; the stream carries the stream args
            Assert.IsTrue(new[] { fan, row, cross }.All(h => h.Args.GetString("waves") == "6" && !h.Args.Has("stream")));
            Assert.AreEqual("12", rings.Args.GetString("waves"));
            Assert.IsFalse(rings.Args.Has("stream"));
            Assert.AreEqual("60", stream.Args.GetString("stream"));
            // owner ruling 2026-10-22: 60 balls over the same 60 s at 6 -> 9 m/s, occupancy 1.25 relaxing the random bound
            Assert.AreEqual("60", stream.Args.GetString("count"));
            Assert.AreEqual("6", stream.Args.GetString("speed"));
            Assert.AreEqual("9", stream.Args.GetString("speed_end"));
            Assert.AreEqual("1.25", stream.Args.GetString("occupancy"));
            Assert.IsFalse(stream.Args.Has("waves"));

            var ballSeries = new[] { fan, rings, row, cross };

            // 2026-10-19 playtest: no pillar telegraphs anywhere in P4, and the ball series launch on the cadence
            Assert.IsTrue(hazards.All(h => h.Args.GetString("marker") == "0"), "every P4 series pins marker=0");
            Assert.IsTrue(ballSeries.All(h => h.Args.GetString("ray_markers") == "0"), "every ball series pins ray_markers=0");
            Assert.IsTrue(new[] { fan, rings, cross }.All(h => h.Args.GetString("wave_every") == "3" && h.Args.GetString("wave_every_end") == "1.5"), "fan, rings and cross launch on the 3.0 -> 1.5 s cadence");

            // round 3: the rows have their own string, spaced out early and dense late under the in-flight cap
            Assert.AreEqual("6", row.Args.GetString("wave_every"));
            Assert.AreEqual("2", row.Args.GetString("wave_every_end"));
            Assert.AreEqual("2", row.Args.GetString("max_in_flight"));
            Assert.AreEqual("3", row.Args.GetString("max_in_flight_end"));
            Assert.AreEqual("2", cross.Args.GetString("max_in_flight"), "the cross is capped at 2 (distinct_dirs)");
            Assert.AreEqual("2", cross.Args.GetString("max_in_flight_end"));
            Assert.IsTrue(new[] { fan, rings }.All(h => !h.Args.Has("max_in_flight")), "the fan and the rings are not capped");

            // owner rulings 2026-10-20: rows and cross never overlap and never share a wall; fan and rings clear the head
            foreach (var h in new[] { row, cross })
            {
                Assert.AreEqual("1", h.Args.GetString("no_overlap"), h.Tag);
                Assert.AreEqual("1", h.Args.GetString("distinct_dirs"), h.Tag);
            }

            Assert.AreEqual("random", row.Args.GetString("dir"));
            Assert.AreEqual("random", cross.Args.GetString("dirs"));

            foreach (var h in new[] { fan, rings })
            {
                Assert.AreEqual("3", h.Args.GetString("origin_clear"), h.Tag);
                // review 2026-10-20: pinned OFF, so a shard row that turns them on cannot refuse the series
                Assert.AreEqual("0", h.Args.GetString("no_overlap"), h.Tag);
                Assert.AreEqual("0", h.Args.GetString("distinct_dirs"), h.Tag);
            }

            Assert.IsTrue(new[] { row, cross, stream }.All(h => !h.Args.Has("origin_clear")));

            // every ball series carries the level-scaled pct: the fake's Level 250 on the default clamp
            var expected = HazardLevelScale.Scaled(250, GraelSettings.BallPct, 0.25, 0.75);
            Assert.IsTrue(hazards.All(h => System.Math.Abs(h.Args.GetDouble("pct", -1, -1, 9) - expected) < 1e-6), $"pct {expected}");

            // round 3 playtest: a 90 degree fan, two openings per row wall, one per cross wall
            Assert.AreEqual("90", fan.Args.GetString("spread"), "the fan is 90 degrees wide");
            Assert.AreEqual("2", row.Args.GetString("gaps"), "the row walls have two openings");
            Assert.AreEqual("1", cross.Args.GetString("gaps"), "the cross keeps one opening per wall");
            Assert.AreEqual("1", rings.Args.GetString("gaps"));
            Assert.AreEqual("1", fan.Args.GetString("gaps"), "the fan carries the pinned gaps=1 too");

            // owner, 2026-10-19: no hover on the fan or the rings; rows and cross hover exactly 2 s; the stream keeps the floor
            foreach (var (h, lead) in new[] { (fan, "0"), (rings, "0"), (row, "2"), (cross, "2") })
            {
                Assert.AreEqual("0", h.Args.GetString("fair_lead"), h.Tag + " pins its lead, not the floor");
                Assert.AreEqual(lead, h.Args.GetString("lead"), h.Tag);
                Assert.AreEqual(lead, h.Args.GetString("lead_end"), h.Tag + " is flat");
            }

            Assert.AreEqual("1", stream.Args.GetString("fair_lead"), "the stream keeps the fair-lead floor");
        }

        [TestMethod]
        public void P4_FanAndRings_OriginHead()
        {
            Enter(4);

            for (var i = 0; i < P4Tags.Length - 1; i++)
                script.OnEvent(Ended(P4Tags[i], HazardOutcome.Completed), port);

            var byTag = port.Hazards.ToDictionary(h => h.Tag);
            Assert.AreEqual(port.Head, byTag["p4-fan"].Origin);
            Assert.AreEqual(port.Head, byTag["p4-rings"].Origin);
            Assert.IsTrue(byTag["p4-row"].Origin.IsNone);
            Assert.IsTrue(byTag["p4-cross"].Origin.IsNone);
            Assert.IsTrue(byTag["p4-stream"].Origin.IsNone);
            Assert.IsTrue(port.Hazards.All(h => !h.Args.Has("cx") && !h.Args.Has("cy")), "the core fills cx/cy from the origin");
        }

        [TestMethod]
        public void P4_HeadCastsOnTimer()
        {
            Enter(4);

            Assert.IsTrue(port.TimerSets.Contains(("p4-cast", 4.0)));
            CollectionAssert.AreEqual(new[] { GraelData.LineP4 }, port.Said);

            for (var i = 0; i < 40; i++)
                script.OnEvent(Timer("p4-cast"), port);

            Assert.AreEqual(40, port.Casts.Count);
            Assert.IsTrue(port.Casts.All(c => c.Caster == port.Head));

            // round 6: a fresh fight's first head cast and every fifth after it is Fire Vulnerability (4481)
            AssertVulnAtCounterPositions(port.Casts.Select(c => c.SpellId).ToList(), firstCounter: 0, "P4");

            // round 3 playtest: Cassius' Ring of Fire II (6191) joins the bolt, the arc and the streak
            var cast = port.Casts.Select(c => c.SpellId).Where(id => id != 4481u).Distinct().ToArray();
            CollectionAssert.IsSubsetOf(cast, new[] { 4439u, 4423u, 4440u, 6191u });
            CollectionAssert.Contains(cast, 6191u, "32 draws from 4 spells: the ring is cast");
            CollectionAssert.AreEquivalent(new[] { 4439u, 4423u, 4440u, 6191u }, GraelData.P4Spells.ToArray());
            Assert.AreEqual(41, port.TimerSets.Count(t => t.Tag == "p4-cast"), "re-armed after each cast");
        }

        // ---- the head casts from the start (round 4 playtest, owner) ----

        /// <summary>
        /// Round 6 (owner ask 2026-10-20): every head cast, whatever the phase, goes through one picker with one counter
        /// for the whole fight. Counter position k (0 = the fight's first head cast) is 4481 when k % 5 == 0, otherwise a
        /// P4 war spell. <paramref name="firstCounter"/> is the counter position of <paramref name="spells"/>[0].
        /// </summary>
        private static void AssertVulnAtCounterPositions(System.Collections.Generic.IList<uint> spells, int firstCounter, string what)
        {
            Assert.AreEqual(4481u, GraelData.HeadVulnSpell);
            Assert.AreEqual(5, GraelData.HeadVulnEvery);

            for (var i = 0; i < spells.Count; i++)
            {
                var k = firstCounter + i;

                if (k % 5 == 0)
                    Assert.AreEqual(4481u, spells[i], $"{what}: head cast #{k + 1} of the fight is Fire Vulnerability");
                else
                    CollectionAssert.Contains(GraelData.P4Spells.ToArray(), spells[i], $"{what}: head cast #{k + 1} of the fight is a P4 war spell");
            }
        }

        [TestMethod]
        public void HeadCasts_OneVulnCounterAcrossTheWholeFight_P1ThroughP5()
        {
            // P1: three early casts (counter 0, 1, 2): the first is 4481
            Enter(1);
            for (var i = 0; i < 3; i++)
                script.OnEvent(Timer(GraelScript.TimerEarlyCast), port);
            AssertVulnAtCounterPositions(port.Casts.Select(c => c.SpellId).ToList(), 0, "P1");
            Assert.AreEqual(4481u, port.Casts[0].SpellId, "the fight's first head cast");

            // P3 continues the SAME counter (3, 4, 5, 6): its third cast is the second 4481, not its first
            port.ClearRecordings();
            Enter(3);
            for (var i = 0; i < 4; i++)
                script.OnEvent(Timer(GraelScript.TimerEarlyCast), port);
            AssertVulnAtCounterPositions(port.Casts.Select(c => c.SpellId).ToList(), 3, "P3");
            Assert.AreNotEqual(4481u, port.Casts[0].SpellId, "a phase change does not reset the counter");
            Assert.AreEqual(4481u, port.Casts[2].SpellId);

            // P4 (7, 8, 9, 10, 11): its fourth cast is 4481
            port.ClearRecordings();
            Enter(4);
            for (var i = 0; i < 5; i++)
                script.OnEvent(Timer(GraelScript.TimerP4Cast), port);
            AssertVulnAtCounterPositions(port.Casts.Select(c => c.SpellId).ToList(), 7, "P4");
            Assert.AreEqual(4481u, port.Casts[3].SpellId);

            // a restart (a hold resumed) does not reset it either
            script.RestartPhase(port);

            // P5: the entry cast is counter 12 (a war spell, not 4481), then the timer: 13, 14, 15 (4481)
            port.ClearRecordings();
            Enter(5);
            for (var i = 0; i < 3; i++)
                script.OnEvent(Timer(GraelScript.TimerP5Cast), port);
            AssertVulnAtCounterPositions(port.Casts.Select(c => c.SpellId).ToList(), 12, "P5");
            Assert.AreNotEqual(4481u, port.Casts[0].SpellId, "the P5 entry cast is no longer always 4481: it is wherever the counter is");
            Assert.AreEqual(4481u, port.Casts[3].SpellId);
            Assert.IsTrue(port.Casts.All(c => c.Caster == port.Head));
        }

        [TestMethod]
        public void HeadCasts_FromP1_AndInP3_OnTheEarlyTimer_WithTheP4Spells_NotAttackable()
        {
            var counter = 0;

            foreach (var phase in new[] { 1, 3 })
            {
                port.ClearRecordings();
                Enter(phase);

                Assert.IsTrue(port.TimerSets.Contains((GraelScript.TimerEarlyCast, GraelScript.EarlyCastSeconds)), "phase " + phase + " arms the early cast timer");
                Assert.AreEqual(0, port.Casts.Count, "the first cast waits one interval");

                var sets = port.Calls.Where(c => c.Name == "SetAttackable").ToList();
                Assert.IsFalse((bool)sets.Single().Args[1], "phase " + phase + ": the head stays unattackable while it casts");

                for (var i = 0; i < 40; i++)
                    script.OnEvent(Timer(GraelScript.TimerEarlyCast), port);

                Assert.AreEqual(40, port.Casts.Count, "phase " + phase);
                Assert.IsTrue(port.Casts.All(c => c.Caster == port.Head));

                // round 6: 4481 at the fight-wide counter positions (P3 carries on from P1's 40 casts), war spells between
                AssertVulnAtCounterPositions(port.Casts.Select(c => c.SpellId).ToList(), counter, "phase " + phase);
                CollectionAssert.Contains(port.Casts.Select(c => c.SpellId).ToArray(), 4481u, "phase " + phase + " casts Fire Vulnerability");
                counter += port.Casts.Count;

                Assert.AreEqual(41, port.TimerSets.Count(t => t.Tag == GraelScript.TimerEarlyCast), "re-armed after each cast");
                Assert.AreEqual(0, port.AdvanceCount, "a cast never moves the phase");
            }

            Assert.AreEqual(2 * GraelSettings.DefaultP4CastSeconds, GraelScript.EarlyCastSeconds, "P1-P3 cast at half the P4 rate");
        }

        [TestMethod]
        public void HeadEarlyCast_OnlyInP1AndP3_P4AndP5KeepTheirOwnCadence()
        {
            foreach (var phase in new[] { 2, 4, 5 })
            {
                port.ClearRecordings();
                Enter(phase);
                Assert.IsFalse(port.TimerSets.Any(t => t.Tag == GraelScript.TimerEarlyCast), "phase " + phase + " never arms the early timer");

                var before = port.Casts.Count;
                script.OnEvent(Timer(GraelScript.TimerEarlyCast), port);
                Assert.AreEqual(before, port.Casts.Count, "phase " + phase + ": a stray early timer casts nothing");
            }

            // P4 and P5 unchanged: their own timers at their own settings
            port.ClearRecordings();
            Enter(4);
            Assert.IsTrue(port.TimerSets.Contains((GraelScript.TimerP4Cast, (double)GraelSettings.DefaultP4CastSeconds)));
            port.ClearRecordings();
            Enter(5);
            Assert.IsTrue(port.TimerSets.Contains((GraelScript.TimerP5Cast, (double)GraelSettings.DefaultP5CastSeconds)));
        }

        [TestMethod]
        public void HeadEarlyCast_RestartRearmsTheTimer_InP1AndP3()
        {
            foreach (var phase in new[] { 1, 3 })
            {
                port.ClearRecordings();
                Enter(phase);
                port.ClearRecordings();

                script.RestartPhase(port);

                Assert.AreEqual((GraelScript.TimerEarlyCast, GraelScript.EarlyCastSeconds), port.TimerSets.Single(t => t.Tag == GraelScript.TimerEarlyCast), "phase " + phase);
                Assert.AreEqual(0, port.Casts.Count, "a restart does not cast at once");
            }
        }

        [TestMethod]
        public void P4_LastSeriesEnded_Advances()
        {
            Enter(4);
            for (var i = 0; i < P4Tags.Length - 1; i++)
                script.OnEvent(Ended(P4Tags[i], HazardOutcome.Completed), port);

            Assert.AreEqual(0, port.AdvanceCount);
            script.OnEvent(Ended("p4-cross", HazardOutcome.Completed), port);

            Assert.AreEqual(1, port.AdvanceCount, "the cross is the last series now");
        }

        [TestMethod]
        public void P4_Restart_FromFan()
        {
            Enter(4);
            script.OnEvent(Ended("p4-fan", HazardOutcome.Completed), port);
            script.OnEvent(Ended("p4-rings", HazardOutcome.Completed), port);   // now on row
            port.ClearRecordings();

            script.RestartPhase(port);

            CollectionAssert.AreEqual(new[] { "StopHazards", "StartHazard", "StartHazard", "SetTimer" }, port.CallNames.ToArray());
            Assert.AreEqual(GraelScript.TagP4Corners, port.Hazards[0].Tag, "the corners come back with the fan");
            Assert.AreEqual("p4-fan", port.Hazards[1].Tag);
            Assert.AreEqual(port.Head, port.Hazards[1].Origin);
        }

        // ---- the corner pillars (owner ruling 2026-10-20: punish hiding during the fan and the rings) ----

        [TestMethod]
        public void P4_Corners_StartWithTheFan_ScaledPct()
        {
            port.Level = 300;
            Enter(4);

            var corners = port.Hazards[0];
            Assert.AreEqual(GraelScript.TagP4Corners, corners.Tag);
            Assert.AreEqual(HazardMechanicRegistry.Zones, corners.Kind);
            Assert.AreEqual("corners", corners.Pattern);
            Assert.IsTrue(corners.Origin.IsNone);
            Assert.IsFalse(corners.Args.Has("pattern"));
            Assert.AreEqual("3", corners.Args.GetString("radius"));
            Assert.AreEqual("1", corners.Args.GetString("tick"));
            Assert.AreEqual("5", corners.Args.GetString("ramp"));
            Assert.AreEqual("1", corners.Args.GetString("reset"));
            Assert.AreEqual(0.05 / 0.60 * 0.50, corners.Args.GetDouble("pct", -1, -1, 9), 1e-6, "L300 benchmark 0.50 x 0.05 / 0.60");
            Assert.AreEqual(0.35 / 0.60 * 0.50, corners.Args.GetDouble("pct_end", -1, -1, 9), 1e-6);
            Assert.AreEqual("p4-fan", port.Hazards[1].Tag, "the corners start just before the fan");
        }

        [TestMethod]
        public void P4_Corners_OnThroughTheRings_StoppedBeforeTheStream()
        {
            Enter(4);
            port.ClearRecordings();

            script.OnEvent(Ended("p4-fan", HazardOutcome.Completed), port);
            Assert.AreEqual(0, port.StoppedHazardTags.Count, "still on during the rings");
            Assert.AreEqual("p4-rings", port.Hazards.Single().Tag, "no second corners run");

            port.ClearRecordings();
            script.OnEvent(Ended("p4-rings", HazardOutcome.Completed), port);

            // round 3: the random stream follows the rings now, and the corners were only asked for the fan and the rings
            CollectionAssert.AreEqual(new[] { GraelScript.TagP4Corners }, port.StoppedHazardTags);
            var names = port.CallNames.ToList();
            Assert.IsTrue(names.IndexOf("StopHazard") < names.IndexOf("StartHazard"), "stopped BEFORE the stream starts");
            Assert.AreEqual("p4-stream", port.Hazards.Single().Tag);

            port.ClearRecordings();
            script.OnEvent(Ended("p4-stream", HazardOutcome.Completed), port);
            script.OnEvent(Ended("p4-row", HazardOutcome.Completed), port);
            Assert.AreEqual(0, port.StoppedHazardTags.Count, "stopped once only");
            Assert.IsFalse(port.Hazards.Any(h => h.Tag == GraelScript.TagP4Corners), "never back on for the stream, the rows or the cross");
            CollectionAssert.AreEqual(new[] { "p4-row", "p4-cross" }, port.Hazards.Select(h => h.Tag).ToArray());
        }

        [TestMethod]
        public void P4_CornersEnding_DoesNotAdvanceTheSeries()
        {
            Enter(4);
            port.ClearRecordings();

            script.OnEvent(Ended(GraelScript.TagP4Corners, HazardOutcome.Aborted), port);

            Assert.AreEqual(0, port.Hazards.Count);
            Assert.AreEqual(0, port.AdvanceCount);
        }

        [TestMethod]
        public void P4_Restart_AfterDrainAlreadyStartedASeries_LeavesOneRun()
        {
            Enter(4);
            // the drain at resume delivered a completion that started the next series ...
            script.OnEvent(Ended("p4-fan", HazardOutcome.Completed), port);
            port.ClearRecordings();

            // ... then RestartPhase: it stops everything BEFORE starting, so exactly one run is live afterwards
            script.RestartPhase(port);

            var names = port.CallNames.ToList();
            Assert.IsTrue(names.IndexOf("StopHazards") < names.IndexOf("StartHazard"));
            Assert.AreEqual(2, names.Count(n => n == "StartHazard"), "exactly the corners and the fan");
            CollectionAssert.AreEqual(new[] { GraelScript.TagP4Corners, "p4-fan" }, port.Hazards.Select(h => h.Tag).ToArray());
        }

        // ================================================================ P5

        [TestMethod]
        public void P5_HeadAttackable_OneVariantEach_AndTheGraelBody()
        {
            Enter(5);

            Assert.IsTrue(port.Attackable[port.Head]);

            // round 3 playtest: one of each variant at Elite, and the original Grael body at Boss
            CollectionAssert.AreEqual(new[] { "red", "green", "blue", "grael" }, port.Spawns.Select(s => s.Tag).ToArray());
            CollectionAssert.AreEqual(new[] { 1006905u, 1006906u, 1006907u, 1006904u }, port.Spawns.Select(s => s.Wcid).ToArray());
            CollectionAssert.AreEqual(new[] { PinnacleTier.Elite, PinnacleTier.Elite, PinnacleTier.Elite, PinnacleTier.Boss }, port.Spawns.Select(s => s.Tier).ToArray());
            Assert.AreEqual(GraelData.GraelSpawn, port.Spawns[3].At);
            Assert.IsTrue(port.Spawns.All(s => !port.Immune.ContainsKey(s.Result) || !port.Immune[s.Result]), "the adds spawn non-immune");

            CollectionAssert.AreEqual(new[] { "p5", "p5-balls" }, port.Hazards.Select(h => h.Tag).ToArray());
            Assert.AreEqual("wisps", port.Hazards[0].Kind);
            Assert.AreEqual("scatter", port.Hazards[0].Pattern);
            Assert.AreEqual("4", port.Hazards[0].Args.GetString("n"));
            Assert.AreEqual("0", port.Hazards[0].Args.GetString("follow"), "P5 pins follow=0: no follower");

            // the scattered balls: one random stream of 8 over 60 s, flat 4 m/s, fewer than the P4 stream's 20
            var balls = port.Hazards[1];
            Assert.AreEqual(HazardMechanicRegistry.Balls, balls.Kind);
            Assert.AreEqual(BallPatterns.Random, balls.Pattern);
            Assert.IsTrue(balls.Origin.IsNone);
            Assert.AreEqual("60", balls.Args.GetString("stream"));
            Assert.AreEqual("8", balls.Args.GetString("count"));
            Assert.AreEqual("4", balls.Args.GetString("speed"));
            Assert.AreEqual("4", balls.Args.GetString("speed_end"), "flat: speed_end pinned to speed");
            Assert.AreEqual("0", balls.Args.GetString("marker"));
            Assert.AreEqual("1", balls.Args.GetString("fair_lead"));
            Assert.AreEqual("0", balls.Args.GetString("distinct_dirs"));
            Assert.AreEqual("0", balls.Args.GetString("no_overlap"));
            Assert.AreEqual(HazardLevelScale.Scaled(250, GraelSettings.BallPct, 0.25, 0.75), balls.Args.GetDouble("pct", -1, -1, 9), 1e-6, "pct scaled like every ball");
            Assert.IsTrue(int.Parse(balls.Args.GetString("count")) < int.Parse(GraelSettings.Defaults.P4StreamArgs().GetString("count")), "fewer than the P4 stream over the same 60 s");

            CollectionAssert.AreEqual(new[] { GraelData.LineP5 }, port.Said);
            Assert.IsTrue(port.TimerSets.Contains(("p5-cast", 3.0)));
        }

        [TestMethod]
        public void P5_GraelBodyDied_DoesNothing_NeverRespawned()
        {
            Enter(5);
            var body = port.Spawns.Single(s => s.Tag == GraelData.GraelTag).Result;
            port.ClearRecordings();

            script.OnEvent(Died(GraelData.GraelTag, body), port);

            Assert.AreEqual(0, port.AdvanceCount);
            Assert.AreEqual(0, port.WinCount, "only the head's death ends the fight");
            Assert.AreEqual(0, port.TimerSets.Count, "no respawn timer");
            Assert.AreEqual(0, port.Spawns.Count);
        }

        [TestMethod]
        public void P5_HeadDied_WinsWithTheAddsAlive()
        {
            Enter(5);
            port.ClearRecordings();

            // the variants and the Grael body are all still alive: the head alone decides
            script.OnEvent(Died(PinnacleEvent.HeadTag, port.Head), port);

            Assert.AreEqual(1, port.WinCount);
        }

        [TestMethod]
        public void P5_BallsLoopOnCompleted_RetryAfterAbort()
        {
            Enter(5);
            port.ClearRecordings();

            script.OnEvent(Ended("p5-balls", HazardOutcome.Completed), port);
            CollectionAssert.AreEqual(new[] { "p5-balls" }, port.Hazards.Select(h => h.Tag).ToArray(), "started again, the wisps untouched");

            port.ClearRecordings();
            script.OnEvent(Ended("p5-balls", HazardOutcome.Aborted), port);
            Assert.AreEqual(0, port.Hazards.Count, "an Aborted run is not restarted at once");
            CollectionAssert.AreEqual(new[] { ("p5-ball-retry", 10.0) }, port.TimerSets);

            script.OnEvent(Timer("p5-ball-retry"), port);
            CollectionAssert.AreEqual(new[] { "p5-balls" }, port.Hazards.Select(h => h.Tag).ToArray());

            // capped at five like the wisps
            for (var i = 0; i < 4; i++)
            {
                script.OnEvent(Ended("p5-balls", HazardOutcome.Aborted), port);
                script.OnEvent(Timer("p5-ball-retry"), port);
            }

            port.ClearRecordings();
            script.OnEvent(Ended("p5-balls", HazardOutcome.Aborted), port);
            Assert.AreEqual(0, port.TimerSets.Count, "sixth abort: the cap is reached");
        }

        [TestMethod]
        public void P5_BallRetryTimerAfterRestart_DoesNotStartASecondRun()
        {
            Enter(5);
            script.OnEvent(Ended("p5-balls", HazardOutcome.Aborted), port);   // retry pending
            script.RestartPhase(port);                                         // restarts the balls itself
            port.ClearRecordings();

            script.OnEvent(Timer("p5-ball-retry"), port);

            Assert.AreEqual(0, port.Hazards.Count);
        }

        [TestMethod]
        public void P5_OpeningCastIsFireVulnerability_ThenEveryFifth()
        {
            Enter(5);
            Assert.AreEqual(4481u, port.Casts[0].SpellId);

            for (var i = 0; i < 5; i++)
                script.OnEvent(Timer("p5-cast"), port);

            // a fresh script entered straight at P5 (an admin jump): its entry cast is the fight's first head cast, so
            // casts: #1 at entry (4481), #2..#5 war spells, #6 (every fifth after the first) is 4481 again
            Assert.AreEqual(6, port.Casts.Count);
            Assert.IsTrue(port.Casts.Skip(1).Take(4).All(c => c.SpellId != 4481u));
            Assert.AreEqual(4481u, port.Casts[5].SpellId);
            Assert.IsTrue(port.Casts.All(c => c.Caster == port.Head));
        }

        [TestMethod]
        public void P5_KilledVariant_RespawnsAfter20s_AtMostOne()
        {
            Enter(5);
            port.ClearRecordings();

            KillVariant("red");

            CollectionAssert.AreEqual(new[] { ("respawn-red", 20.0) }, port.TimerSets);
            Assert.AreEqual(0, port.Spawns.Count, "not yet");

            script.OnEvent(Timer("respawn-red"), port);
            Assert.AreEqual(1, port.Spawns.Count);
            Assert.AreEqual("red", port.Spawns[0].Tag);
            Assert.AreEqual(1006905u, port.Spawns[0].Wcid);
            Assert.AreEqual(PinnacleTier.Elite, port.Spawns[0].Tier, "a P5 respawn is Elite too");

            // a second respawn timer while the new one lives spawns nothing
            script.OnEvent(Timer("respawn-red"), port);
            Assert.AreEqual(1, port.Spawns.Count);

            // a live green/blue is never doubled either
            script.OnEvent(Timer("respawn-green"), port);
            Assert.AreEqual(1, port.Spawns.Count);
        }

        [TestMethod]
        public void P5_WispsLoopOnCompleted()
        {
            Enter(5);
            port.ClearRecordings();

            script.OnEvent(Ended("p5", HazardOutcome.Completed), port);

            Assert.AreEqual(1, port.Hazards.Count);
            Assert.AreEqual("p5", port.Hazards[0].Tag);

            port.ClearRecordings();
            script.OnEvent(Ended("p5", HazardOutcome.Aborted), port);
            Assert.AreEqual(0, port.Hazards.Count, "an Aborted run is not restarted at once");
            CollectionAssert.AreEqual(new[] { ("p5-wisp-retry", 10.0) }, port.TimerSets);
        }

        [TestMethod]
        public void P5_WispsAborted_RetriesAfter10s_CappedAtFive_ResetByEnterPhase()
        {
            Enter(5);

            for (var i = 0; i < 5; i++)
            {
                port.ClearRecordings();
                script.OnEvent(Ended("p5", HazardOutcome.Aborted), port);
                CollectionAssert.AreEqual(new[] { ("p5-wisp-retry", 10.0) }, port.TimerSets, "retry " + (i + 1));
                Assert.AreEqual(0, port.Hazards.Count);

                script.OnEvent(Timer("p5-wisp-retry"), port);
                Assert.AreEqual(1, port.Hazards.Count, "restarted when the timer fires");
                Assert.AreEqual("p5", port.Hazards[0].Tag);
            }

            port.ClearRecordings();
            script.OnEvent(Ended("p5", HazardOutcome.Aborted), port);
            Assert.AreEqual(0, port.TimerSets.Count, "sixth abort: the cap is reached");
            script.OnEvent(Timer("p5-wisp-retry"), port);
            Assert.AreEqual(0, port.Hazards.Count);

            // a fresh P5 entry clears the counter
            Enter(5);
            port.ClearRecordings();
            script.OnEvent(Ended("p5", HazardOutcome.Aborted), port);
            Assert.AreEqual(1, port.TimerSets.Count);
        }

        [TestMethod]
        public void P5_WispRetryTimerAfterRestart_DoesNotStartASecondRun()
        {
            Enter(5);
            script.OnEvent(Ended("p5", HazardOutcome.Aborted), port);   // retry pending
            script.RestartPhase(port);                                   // restarts the wisps itself
            port.ClearRecordings();

            script.OnEvent(Timer("p5-wisp-retry"), port);

            Assert.AreEqual(0, port.Hazards.Count);
        }

        [TestMethod]
        public void P5_HeadDied_Wins()
        {
            Enter(5);
            port.ClearRecordings();

            script.OnEvent(Died(PinnacleEvent.HeadTag, port.Head), port);

            Assert.AreEqual(1, port.WinCount);
            CollectionAssert.AreEqual(new[] { "The song... ends..." }, port.Said);
            Assert.AreEqual(0, port.AdvanceCount);
        }

        [TestMethod]
        public void Head_ImmuneInEveryPhaseButFive()
        {
            // C4: spell-proof outside P5, and only attackable in P5
            for (var n = 1; n <= 5; n++)
            {
                Init();
                Enter(n);

                Assert.AreEqual(n != 5, port.Immune[port.Head], $"phase {n} head immunity");
                Assert.AreEqual(1, port.Calls.Count(c => c.Name == "SetImmune" && c.Args[0].Equals(port.Head)), $"phase {n}: one head toggle");
            }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        public void HeadDied_InAnyPhase_Wins(int phase)
        {
            // C4: a dead head can never strand a running fight
            Enter(phase);
            port.ClearRecordings();

            script.OnEvent(Died(PinnacleEvent.HeadTag, port.Head), port);

            Assert.AreEqual(1, port.WinCount);
            CollectionAssert.AreEqual(new[] { GraelData.LineHeadDied }, port.Said);
            Assert.AreEqual(0, port.AdvanceCount);
        }

        [TestMethod]
        public void Tiers_BodyBoss_VariantsElite_HeadHead()
        {
            // E2 (owner ruling 2026-10-20): the script data carries the tier the core passes to the head spawn
            Assert.AreEqual(PinnacleTier.Head, GraelData.HeadTier);
            Assert.AreEqual(PinnacleTier.Head, script.Data.HeadTier);
            Assert.AreEqual(PinnacleTier.Boss, GraelData.GraelTier);
            Assert.AreEqual(PinnacleTier.Elite, GraelData.VariantTier);
        }

        [TestMethod]
        public void MemoryRoundLine_IsShortAscii()
        {
            Assert.AreEqual("Round 2 of 4.", GraelData.LineMemoryRound(2, 4));
        }

        [TestMethod]
        public void P5_Restart_WispsRestart_TimersKept()
        {
            Enter(5);
            KillVariant("blue");
            port.ClearRecordings();

            script.RestartPhase(port);

            CollectionAssert.AreEqual(new[] { "StopHazards", "StartHazard", "StartHazard", "SetTimer" }, port.CallNames.ToArray());
            CollectionAssert.AreEqual(new[] { "p5", "p5-balls" }, port.Hazards.Select(h => h.Tag).ToArray());
            Assert.AreEqual(("p5-cast", 3.0), port.TimerSets.Single());
            Assert.AreEqual(0, port.CancelledTimers.Count, "respawn timers are never cancelled or reset");
            Assert.AreEqual(0, port.Spawns.Count);
        }

        // ================================================================ every phase

        [TestMethod]
        public void EveryEnterPhase_HeadAttackableOnlyInP5()
        {
            for (var phase = 1; phase <= script.PhaseCount; phase++)
            {
                port.ClearRecordings();
                Enter(phase);

                var sets = port.Calls.Where(c => c.Name == "SetAttackable").ToList();
                Assert.AreEqual(1, sets.Count, "phase " + phase);
                Assert.AreEqual(port.Head, (PinnacleHandle)sets[0].Args[0]);
                Assert.AreEqual(phase == 5, (bool)sets[0].Args[1], "phase " + phase);
            }
        }

        [TestMethod]
        public void Script_IdentityAndData()
        {
            Assert.AreEqual("grael", script.Kind);
            Assert.AreEqual(5, script.PhaseCount);
            Assert.AreEqual(0x0069, script.Data.Landblock);
            Assert.AreEqual("pinnacle", script.Data.HazardRegionPreset);
            Assert.AreEqual(1006909u, script.Data.HeadWcid);
            Assert.AreEqual(1006900u, script.Data.SealWcid);
            Assert.AreEqual(1, script.Data.SealPoints.Count);
            Assert.AreEqual(new PinnaclePoint(0x0069015A, 41.93f, -52.50f, -6.00f), script.Data.ArrivalPoint);
            Assert.AreEqual(0x00690113u, script.Data.HeadPoint.Cell);
            Assert.IsTrue(HazardRegionPresets.TryGet(script.Data.HazardRegionPreset, 0, out _));
        }
    }
}
