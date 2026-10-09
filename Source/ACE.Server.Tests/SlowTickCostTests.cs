using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;

using ACE.Server.Managers;

using log4net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: what the instrumentation costs the world loop.
    ///
    /// The allocation test is the hard gate: the every-iteration path (BeginIteration, the 13 production laps,
    /// EndIteration, then SlowTickReporter.Process = RecordTickPhases + detector Evaluate/AddSample below the
    /// floor) must allocate nothing, with a MeterListener enabled on the new instruments standing in for the
    /// dotnet-monitor sidecar. The timing tests only REPORT (TestContext output); they assert no bound, because
    /// a wall-clock bound is flaky on shared CI runners.
    ///
    /// The same gate covers the per-opcode attribution of the inbound-message phase (InboundOpcodeProfile),
    /// which is charged PER HANDLER INVOCATION rather than per tick, so it is measured per invocation and the
    /// report converts to a per-tick figure at a stated message rate.
    ///
    /// The facade is driven from the test thread directly: BeginIteration records the calling thread as the
    /// world thread, so no seam was needed and the world-thread check is exercised as shipped.
    /// </summary>
    [TestClass]
    public class SlowTickCostTests
    {
        public TestContext TestContext { get; set; }

        [TestCleanup]
        public void Teardown()
        {
            InboundOpcodeProfile.Enabled = false;
            InboundOpcodeProfile.BeginPhase();
            ACE.Server.Command.CommandProfileIds.ResetForTests();
        }

        private static double sink;

        // Which phase instruments the listener has heard from, and whether any measurement carried a tag. Written
        // from the measurement callback; HashSet.Add of a name already present allocates nothing, and every name is
        // first added during the warm-up pass, so neither disturbs the measured window.
        private static readonly HashSet<string> heardFrom = new HashSet<string>(StringComparer.Ordinal);
        private static bool sawTag;

        // Enabled with a floor no simulated iteration can reach even if the test thread is preempted, so the gate
        // tests cannot flake into the trip path; the floor compare is still the first thing Evaluate does.
        private static readonly SlowTickSettings Enabled = new SlowTickSettings(true, 60_000, 3.0, 10_000);

        private static MeterListener StartListener()
        {
            // force ServerMetrics' static initialisation so its instruments exist before the listener starts
            ServerMetrics.RecordTickPhases(new double[WorldTickPhases.Count], false);

            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == ServerMetrics.MeterName && instrument.Name.StartsWith("ace.world.tick.phase", StringComparison.Ordinal))
                        l.EnableMeasurementEvents(instrument);
                },
            };

            listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            {
                sink += value;

                if (tags.Length > 0)
                    sawTag = true;

                heardFrom.Add(instrument.Name);
            });
            listener.Start();

            return listener;
        }

        /// <summary>One simulated world-loop iteration: the production lap sequence of an updated iteration.</summary>
        private static SlowTickVerdict Iterate(SlowTickDetector detector, bool worldUpdated, long nowMs, in SlowTickSettings settings)
        {
            WorldTickProfile.BeginIteration();

            WorldTickProfile.Lap(WorldTickPhase.PlayerManager);
            WorldTickProfile.Lap(WorldTickPhase.InboundMessages);
            WorldTickProfile.Lap(WorldTickPhase.ActionQueue);
            WorldTickProfile.Lap(WorldTickPhase.DelayManager);
            WorldTickProfile.Lap(WorldTickPhase.LbPhysics);
            WorldTickProfile.Lap(WorldTickPhase.LbMultiThreaded);
            WorldTickProfile.Lap(WorldTickPhase.LbSingleThreaded);
            WorldTickProfile.Lap(WorldTickPhase.LbUnload);
            WorldTickProfile.Lap(WorldTickPhase.HouseManager);
            WorldTickProfile.Lap(WorldTickPhase.WorldManagers);
            WorldTickProfile.Lap(WorldTickPhase.ThreadDungeons);
            WorldTickProfile.Lap(WorldTickPhase.WorldManagers);
            WorldTickProfile.Lap(WorldTickPhase.SessionWork);

            WorldTickProfile.EndIteration();

            return SlowTickReporter.Process(detector, WorldTickProfile.Accumulator, worldUpdated, nowMs, in settings).Verdict;
        }

        private static void Run(SlowTickDetector detector, int iterations, in SlowTickSettings settings)
        {
            for (var i = 0; i < iterations; i++)
            {
                if (Iterate(detector, (i & 1) == 0, i, in settings) != SlowTickVerdict.None)
                    throw new AssertFailedException("a simulated iteration tripped the detector; the hot-path measurement is invalid");
            }
        }

        [TestMethod]
        public void HotPathAllocatesNothingWithAListenerAttached()
        {
            using var listener = StartListener();

            var detector = new SlowTickDetector();

            Run(detector, 5_000, in Enabled); // warm-up: JIT, tiering, the ring filling past MinSamplesForRelative

            var before = GC.GetAllocatedBytesForCurrentThread();
            Run(detector, 50_000, in Enabled);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(sink > 0, "control: the listener must actually have received measurements");
            Assert.AreEqual(0L, allocated, $"the every-iteration path allocated {allocated} bytes over 50,000 iterations");

            // control: the zero above was measured with all 26 per-phase instruments live, not a subset
            CollectionAssert.AreEquivalent(ExpectedPhaseInstrumentNames(), heardFrom.ToList(), "every per-phase counter and histogram must have been recorded");
            Assert.IsFalse(sawTag, "per-phase instruments must be untagged: dotnet-monitor keys its store by instrument name alone");
        }

        private static List<string> ExpectedPhaseInstrumentNames()
        {
            var names = new List<string>();

            foreach (var tag in WorldTickPhases.TagNames)
            {
                names.Add("ace.world.tick.phase." + tag + ".time");
                names.Add("ace.world.tick.phase." + tag + ".duration");
            }

            return names;
        }

        [TestMethod]
        public void PhaseInstrumentsAreTheTwentySixUntaggedNames()
        {
            // force ServerMetrics' static initialisation before enumerating what the Meter publishes
            ServerMetrics.RecordTickPhases(new double[WorldTickPhases.Count], false);

            var published = new List<string>();
            var units = new List<string>();

            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == ServerMetrics.MeterName && instrument.Name.StartsWith("ace.world.tick.phase", StringComparison.Ordinal))
                    {
                        published.Add(instrument.Name);
                        units.Add(instrument.Unit);
                    }
                },
            };
            listener.Start();

            CollectionAssert.AreEquivalent(ExpectedPhaseInstrumentNames(), published,
                "exactly one .time counter and one .duration histogram per WorldTickPhases tag; the tagged ace.world.tick.phase.time/.duration pair must be gone");
            Assert.IsTrue(units.All(u => u == "ms"), "every phase instrument is in ms: dotnet-monitor appends the unit to the exported name");
        }

        [TestMethod]
        public void HotPathAllocatesNothingWithNoListener()
        {
            var detector = new SlowTickDetector();

            Run(detector, 5_000, in Enabled);

            var before = GC.GetAllocatedBytesForCurrentThread();
            Run(detector, 50_000, in Enabled);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated);
        }

        [TestMethod]
        public void KillSwitchSkipsTheDetectorEntirely()
        {
            var slow = new TickPhaseAccumulator(1000);
            slow.Begin(0);
            slow.Lap(WorldTickPhase.LbPhysics, 5_000);
            slow.End(5_000); // a 5-second iteration

            var off = new SlowTickSettings(false, 100, 3.0, 10_000);
            var detector = new SlowTickDetector();

            for (var i = 0; i < 100; i++)
                Assert.AreEqual(SlowTickVerdict.None, SlowTickReporter.Process(detector, slow, true, i, in off).Verdict);

            Assert.AreEqual(0, detector.SampleCount, "disabled: the ring must not even be fed");

            // control: the same input with the switch on does trip, so the assertion above is not vacuous
            Assert.AreEqual(SlowTickVerdict.Emit, SlowTickReporter.Process(detector, slow, true, 0, new SlowTickSettings(true, 100, 3.0, 10_000)).Verdict);
            Assert.AreEqual(1, detector.SampleCount);
        }

        // ---- per-opcode attribution of the inbound-message phase (InboundOpcodeProfile) ----

        /// <summary>
        /// One simulated inbound-message phase: the BeginPhase reset the world loop does before RunActions,
        /// then <paramref name="messages"/> handler invocations through the production scope API. Every one is
        /// re-labelled as a game action - the dominant shape of real traffic, and the longer of the two paths -
        /// spread over <paramref name="distinct"/> opcodes, which is what sets the aggregator's scan depth.
        /// </summary>
        private static void InboundPhase(int messages, int distinct)
        {
            InboundOpcodeProfile.BeginPhase();

            for (var m = 0; m < messages; m++)
            {
                var scope = InboundOpcodeProfile.BeginMessage(0xF7B1); // GameMessageOpcode.GameAction

                InboundOpcodeProfile.RelabelAsGameAction(m % distinct);

                InboundOpcodeProfile.EndInvoke(scope);
            }
        }

        [TestMethod]
        public void InboundOpcodeCaptureAllocatesNothing()
        {
            WorldTickProfile.BeginIteration(); // claims this thread as the world thread, as the world loop does

            InboundOpcodeProfile.Enabled = true;

            try
            {
                for (var i = 0; i < 200; i++) // warm-up: JIT and tiering
                    InboundPhase(200, InboundOpcodeProfile.Capacity);

                var before = GC.GetAllocatedBytesForCurrentThread();

                for (var i = 0; i < 1_000; i++)
                    InboundPhase(200, InboundOpcodeProfile.Capacity);

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.AreEqual(0L, allocated, $"the inbound-opcode capture allocated {allocated} bytes over 200,000 invocations");

                // control: the zero above was measured with the capture actually recording, not switched off
                Assert.AreEqual(InboundOpcodeProfile.Capacity, InboundOpcodeProfile.FilledCount, "the aggregator must have been full, so the worst-case scan depth is what was measured");
            }
            finally
            {
                InboundOpcodeProfile.Enabled = false;
                InboundOpcodeProfile.BeginPhase();
            }
        }

        [TestMethod]
        public void ReportInboundOpcodeCost()
        {
            const int messages = 200;
            const int ticks = 20_000;

            WorldTickProfile.BeginIteration();

            double MeasurePerInvokeNs(bool enabled, int distinct)
            {
                InboundOpcodeProfile.Enabled = enabled;

                for (var i = 0; i < 500; i++)
                    InboundPhase(messages, distinct);

                var sw = Stopwatch.StartNew();
                for (var i = 0; i < ticks; i++)
                    InboundPhase(messages, distinct);
                sw.Stop();

                return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (ticks * (double)messages);
            }

            try
            {
                var worst = MeasurePerInvokeNs(true, InboundOpcodeProfile.Capacity);
                var typical = MeasurePerInvokeNs(true, 8);
                var off = MeasurePerInvokeNs(false, InboundOpcodeProfile.Capacity);

                // The cost is PER INVOCATION, so the per-tick figure depends on the message rate: 20/tick for a
                // quiet tick, 200/tick for a burst. The benchmark walks the opcodes round-robin, which is the
                // worst case for the aggregator's scan (never the slot it looked at last).
                TestContext.WriteLine(
                    $"SLOWTICK_COST inbound opcode capture, {messages} invocations x {ticks:N0} phases, round-robin opcodes: " +
                    $"on, {InboundOpcodeProfile.Capacity} distinct (full aggregator) {worst:F1} ns/invoke = {worst * 20 / 1000.0:F2} us/tick at 20 msgs, {worst * 200 / 1000.0:F2} us/tick at 200; " +
                    $"on, 8 distinct {typical:F1} ns/invoke = {typical * 20 / 1000.0:F2} us/tick at 20 msgs, {typical * 200 / 1000.0:F2} us/tick at 200; " +
                    $"off {off:F1} ns/invoke = {off * 20 / 1000.0:F2} us/tick at 20 msgs, {off * 200 / 1000.0:F2} us/tick at 200");
            }
            finally
            {
                InboundOpcodeProfile.Enabled = false;
                InboundOpcodeProfile.BeginPhase();
            }
        }

        private double MeasureHotPathNs(int iterations, in SlowTickSettings settings)
        {
            var detector = new SlowTickDetector();

            Run(detector, 10_000, in settings);

            var sw = Stopwatch.StartNew();
            Run(detector, iterations, in settings);
            sw.Stop();

            return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
        }

        [TestMethod]
        public void ReportHotPathCost()
        {
            const int n = 100_000;

            var noListener = MeasureHotPathNs(n, in Enabled);

            var disabled = MeasureHotPathNs(n, new SlowTickSettings(false, 100, 3.0, 10_000));

            double withListener;
            using (StartListener())
                withListener = MeasureHotPathNs(n, in Enabled);

            TestContext.WriteLine($"SLOWTICK_COST hot path, {n:N0} iterations: no listener {noListener:F0} ns/iter; listener enabled {withListener:F0} ns/iter; slow log disabled, no listener {disabled:F0} ns/iter");
        }

        [TestMethod]
        public void ReportEmitCost()
        {
            const int n = 2_000;

            // a full ring, so the median sorts all 1024 samples, as it would on a live server
            var detector = new SlowTickDetector();
            var rng = new Random(1);
            for (var i = 0; i < SlowTickDetector.Capacity; i++)
                detector.AddSample(10 + rng.NextDouble() * 10);

            var repo = LogManager.CreateRepository("slowtick-cost-" + Guid.NewGuid().ToString("N"));
            var nullLog = LogManager.GetLogger(repo.Name, "SlowTickCostTests"); // unconfigured repository: no appenders

            var snapshot = new SlowTickSnapshot { WorldUpdated = true, Groups = 40, GroupPhysicsMaxMs = 12, GroupMultiMaxMs = 30 };
            for (var i = 0; i < WorldTickPhases.Count; i++)
                snapshot.PhaseMs[i] = i * 1.37;

            var sb = new StringBuilder(1024);
            var median = 0.0;

            for (var warm = 0; warm < 200; warm++)
                median += EmitOnce(detector, snapshot, sb, nullLog);

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
                median += EmitOnce(detector, snapshot, sb, nullLog);
            sw.Stop();

            var perEmitUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;

            // the landblock walk proxy on its own: 150 offers into the top-5 slots
            var swOffer = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
                OfferLandblocks(snapshot, 150);
            swOffer.Stop();

            var perWalkUs = swOffer.Elapsed.TotalMilliseconds * 1000.0 / n;

            // the median alone: copy + sort of the full 1024-sample ring
            var swMedian = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
                median += detector.Median();
            swMedian.Stop();

            var perMedianUs = swMedian.Elapsed.TotalMilliseconds * 1000.0 / n;

            sink += median;

            TestContext.WriteLine($"SLOWTICK_COST one emit (median sort of 1024 + 150 landblock offers with sections + 13 opcode offers + line format + null-logger Warn): {perEmitUs:F1} us; of which median alone {perMedianUs:F1} us, 150 landblock offers alone {perWalkUs:F2} us; line length {sb.Length} chars");
            TestContext.WriteLine("SLOWTICK_LINE " + sb);
        }

        private static double EmitOnce(SlowTickDetector detector, SlowTickSnapshot snapshot, StringBuilder sb, ILog log)
        {
            var median = detector.Median();

            snapshot.MedianMs = median;
            OfferLandblocks(snapshot, 150);
            OfferOpcodes(snapshot, 12);

            sb.Clear();
            SlowTickLine.Append(sb, snapshot);

            log.Warn(sb.ToString());

            return median;
        }

        private static void OfferLandblocks(SlowTickSnapshot snapshot, int count)
        {
            snapshot.ResetLandblocks();
            snapshot.Groups = 40;
            snapshot.GroupPhysicsMaxMs = 12;
            snapshot.GroupMultiMaxMs = 30;

            for (var i = 0; i < count; i++)
                snapshot.OfferLandblock((uint)(i << 16) | 0xFFFF, 0, (i * 7919 % 97) * 0.13, i % 4, 200 + i, SampleSections[i % SampleSections.Length]);
        }

        /// <summary>Per-landblock section readings for the emit-cost line, so the lbN_s* keys are in what it measures.</summary>
        private static readonly double[][] SampleSections =
        {
            new double[] { 2.1, 0.3, 0, 8.4, 0.02, 0.4, 0, 0.9, 1.7 },
            new double[] { 0, 0.1, 0, 0.05, 0, 11.3, 0, 0, 0.6 },
            new double[] { 4.4, 0, 3.2, 0, 0, 0, 6.8, 1.1, 0 },
        };

        // ---- per-landblock tick timer (LandblockTickTimer): event span + section capture ----

        private static readonly ACE.Common.Performance.RateMonitor baseline5m = new ACE.Common.Performance.RateMonitor();
        private static readonly ACE.Common.Performance.RateMonitor baseline1h = new ACE.Common.Performance.RateMonitor();

        /// <summary>
        /// One active landblock tick through LandblockTickTimer exactly as Landblock drives it: physics, the six
        /// multi-threaded sections, the two single-threaded sections, then the close that snapshots them.
        /// </summary>
        private static void TimerTick(ACE.Server.Entity.LandblockTickTimer timer, long iteration)
        {
            timer.BeginPhysics(iteration);
            timer.EndPhysics();

            timer.BeginMultiThreaded(iteration);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.RunActions, 1e-6);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.Monster, 1e-6);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.GenUpdate, 1e-6);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.GenRegen, 1e-6);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.LbHeartbeat, 1e-6);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.DbSave, 1e-6);
            timer.EndMultiThreaded();

            timer.BeginSingleThreaded(iteration);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.PlayerTick, 1e-6);
            timer.AddSection(ACE.Server.Entity.LandblockTickSection.WoHeartbeat, 1e-6);
            timer.EndSingleThreaded();
        }

        /// <summary>The monitor calls one active landblock tick made before LandblockTickTimer existed - the baseline.</summary>
        private static void BaselineTick()
        {
            baseline5m.Restart(); baseline1h.Restart();
            baseline5m.Pause(); baseline1h.Pause();
            baseline5m.Resume(); baseline1h.Resume();
            baseline5m.Pause(); baseline1h.Pause();
            baseline5m.Resume(); baseline1h.Resume();
            baseline5m.RegisterEventEnd(); baseline1h.RegisterEventEnd();
        }

        [TestMethod]
        public void LandblockTickTimerAllocatesNothing()
        {
            var timer = new ACE.Server.Entity.LandblockTickTimer();

            for (var i = 0; i < 5_000; i++)
                TimerTick(timer, i);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 100_000; i++)
                TimerTick(timer, i);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated, $"LandblockTickTimer allocated {allocated} bytes over 100,000 landblock ticks");

            // control: the sections really were captured and snapshotted
            Assert.IsTrue(timer.LastSectionMs[(int)ACE.Server.Entity.LandblockTickSection.WoHeartbeat] > 0);
        }

        [TestMethod]
        public void ReportLandblockTickTimerCost()
        {
            const int n = 200_000;

            var timer = new ACE.Server.Entity.LandblockTickTimer();

            for (var i = 0; i < 20_000; i++)
            {
                TimerTick(timer, i);
                BaselineTick();
            }

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
                TimerTick(timer, i);
            sw.Stop();

            var withSectionsNs = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / n;

            sw.Restart();
            for (var i = 0; i < n; i++)
                BaselineTick();
            sw.Stop();

            var baselineNs = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / n;

            TestContext.WriteLine(
                $"SLOWTICK_COST landblock tick timer, {n:N0} active landblock ticks: LandblockTickTimer (event span + 9 sections + snapshot) {withSectionsNs:F0} ns/tick; " +
                $"monitor-only baseline (the pre-change Restart/Pause/Resume/RegisterEventEnd sequence) {baselineNs:F0} ns/tick; " +
                $"added {withSectionsNs - baselineNs:F0} ns/landblock tick = {(withSectionsNs - baselineNs) * 150 / 1000.0:F2} us/world tick at 150 loaded landblocks");
        }

        // ---- chat @command attribution (InboundOpcodeProfile.RelabelAsCommand) ----

        private static readonly ACE.Server.Command.CommandHandlerInfo costProbeCommand = new ACE.Server.Command.CommandHandlerInfo
        {
            Attribute = new ACE.Server.Command.CommandHandlerAttribute("slowtickcostprobe", ACE.Entity.Enum.AccessLevel.Player, ACE.Server.Command.CommandHandlerFlag.None, "", ""),
        };

        /// <summary>
        /// As <see cref="InboundPhase"/>, but every message is a chat @command: re-labelled as Talk, then as
        /// cmd_unknown on entering the command branch, then as the resolved command - the three relabels
        /// GameActionTalk makes for a command that runs.
        /// </summary>
        private static void CommandPhase(int messages, bool relabelCommand)
        {
            InboundOpcodeProfile.BeginPhase();

            for (var m = 0; m < messages; m++)
            {
                var scope = InboundOpcodeProfile.BeginMessage(0xF7B1);

                InboundOpcodeProfile.RelabelAsGameAction(0x0015); // GameActionType.Talk

                if (relabelCommand)
                {
                    InboundOpcodeProfile.RelabelAsCommand(null);
                    InboundOpcodeProfile.RelabelAsCommand(costProbeCommand);
                }

                InboundOpcodeProfile.EndInvoke(scope);
            }
        }

        [TestMethod]
        public void CommandRelabelAllocatesNothing()
        {
            WorldTickProfile.BeginIteration();

            InboundOpcodeProfile.Enabled = true;

            try
            {
                for (var i = 0; i < 200; i++) // warm-up, including the command's one-time id assignment
                    CommandPhase(200, true);

                var before = GC.GetAllocatedBytesForCurrentThread();

                for (var i = 0; i < 1_000; i++)
                    CommandPhase(200, true);

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.AreEqual(0L, allocated, $"the command relabel allocated {allocated} bytes over 200,000 invocations");

                // control: the charge really went to the command, not to Talk
                Assert.AreEqual(1, InboundOpcodeProfile.FilledCount);
                Assert.AreEqual(InboundOpcodeProfile.CommandKey(ACE.Server.Command.CommandProfileIds.IdFor(costProbeCommand)), InboundOpcodeProfile.KeyAt(0));
            }
            finally
            {
                InboundOpcodeProfile.Enabled = false;
                InboundOpcodeProfile.BeginPhase();
            }
        }

        [TestMethod]
        public void ReportCommandRelabelCost()
        {
            const int messages = 200;
            const int ticks = 20_000;

            WorldTickProfile.BeginIteration();

            double Measure(bool enabled, bool relabelCommand)
            {
                InboundOpcodeProfile.Enabled = enabled;

                for (var i = 0; i < 500; i++)
                    CommandPhase(messages, relabelCommand);

                var sw = Stopwatch.StartNew();
                for (var i = 0; i < ticks; i++)
                    CommandPhase(messages, relabelCommand);
                sw.Stop();

                return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (ticks * (double)messages);
            }

            try
            {
                var talkOnly = Measure(true, false);
                var command = Measure(true, true);
                var off = Measure(false, true);

                TestContext.WriteLine(
                    $"SLOWTICK_COST command relabel, {messages} invocations x {ticks:N0} phases: ga_Talk scope {talkOnly:F1} ns/invoke; " +
                    $"cmd_<name> scope (two extra relabels) {command:F1} ns/invoke, added {command - talkOnly:F1} ns per command; capture off {off:F1} ns/invoke");
            }
            finally
            {
                InboundOpcodeProfile.Enabled = false;
                InboundOpcodeProfile.BeginPhase();
            }
        }

        /// <summary>A plausible mix for the emit-cost line: a few named game actions, one plain message, one unnamed opcode.</summary>
        private static void OfferOpcodes(SlowTickSnapshot snapshot, int count)
        {
            snapshot.ResetOpcodes();

            var keys = new[]
            {
                InboundOpcodeProfile.ActionKey(0xF61E), // GameActionType.DoMovementCommand
                InboundOpcodeProfile.ActionKey(0x0019), // GameActionType.PutItemInContainer
                InboundOpcodeProfile.ActionKey(0x0015), // GameActionType.Talk
                InboundOpcodeProfile.ActionKey(0x0008), // GameActionType.TargetedMeleeAttack
                InboundOpcodeProfile.MessageKey(0xF657), // GameMessageOpcode.CharacterEnterWorld
                InboundOpcodeProfile.ActionKey(0x0BAD), // not named by the enum
            };

            for (var i = 0; i < count; i++)
                snapshot.OfferOpcode(keys[i % keys.Length], 1 + i * 3, (i * 7919 % 53) * 0.37, (i * 31 % 17) * 0.11);

            // a chat @command, offered once more on top of the original 12 so it lands in a top slot
            snapshot.OfferOpcode(InboundOpcodeProfile.CommandKey(ACE.Server.Command.CommandProfileIds.IdFor(costProbeCommand)), 2, 15.5, 14.2);

            snapshot.AddOpcodeOverflow(4, 0.9);
        }
    }
}
