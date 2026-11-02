using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HAZARDS-PLAN A2, wisps: the run's LiveCount resizes the squad at the first glide of each cycle and plans the
    /// formation for it; null (the default) leaves every call the director makes exactly as before. Driven through the
    /// recording fake port of WispDirectorTests; pure, no PropertyManager.
    /// </summary>
    [TestClass]
    public class WispDirectorLiveCountTests
    {
        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static string F(System.Numerics.Vector3 v) => $"({F(v.X)},{F(v.Y)},{F(v.Z)})";

        /// <summary>Every observable call the director made, in a stable text form (handles by id).</summary>
        internal static string Trace(WispDirectorTests.FakePort port)
        {
            var sb = new StringBuilder();

            foreach (var m in port.Moves)
                sb.Append($"move t={F(m.At)} s={F(m.Seconds)} f={m.Options.Follow} [{string.Join(";", m.Group.Select(g => g.Handle.Id + "->" + F(g.End)))}]\n");

            foreach (var s in port.Spawns)
                sb.Append($"pillar t={F(s.At)} {F(s.Point)} {F(s.Seconds)} ok={s.Ok}\n");

            foreach (var d in port.DestMarks)
                sb.Append($"mark t={F(d.At)} {F(d.Point)} {F(d.Seconds)}\n");

            foreach (var l in port.Legs)
                sb.Append($"leg t={F(l.At)} {l.Handle.Id} {F(l.End)} {F(l.Seconds)}\n");

            foreach (var s in port.Strikes)
                sb.Append($"strike t={F(s.At)} {s.Source.Id} {F(s.Centre)} {F(s.Radius)} {F(s.Window)}\n");

            foreach (var d in port.Destroyed)
                sb.Append($"destroy t={F(d.At)} {d.Handle.Id}\n");

            foreach (var s in port.Said)
                sb.Append("say " + s + "\n");

            foreach (var l in port.Logs)
                sb.Append("log " + l + "\n");

            sb.Append($"end {port.Outcome} {port.Reason} {F(port.EndedAt)} x{port.Ends}\n");
            return sb.ToString();
        }

        internal static string Fingerprint(string trace)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace)));

        private static readonly (string Name, string Args, string Pattern)[] Scenarios =
        {
            ("scatter", "cycles=3 move=2 hold=1.5 burst=1 rest=0.5 n=6 strike_delay=0", "scatter"),
            ("row", "cycles=2 move=2 hold=1.5 burst=1 rest=0.5 dir=n n=7 strike_delay=0", "row"),
            ("ring", "cycles=2 move=2 hold=1.2 burst=1 rest=0.5 n=8 fire=sequence strike_delay=0", "ring"),
            ("sweep", "cycles=2 move=2 hold=1.2 burst=1 rest=0.5 n=8 dir=n strike_delay=0", "sweep"),
            ("mixed", "cycles=4 move=2 hold=1.2 burst=1 rest=0.5 n=8 fire=random strike_delay=0", "mixed"),
            ("follow", "cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=6 follow=1 mark_dest=1 strike_delay=0", "scatter"),
        };

        /// <summary>The fingerprints of the traces as they were BEFORE A2 (computed from the S0 director, 965cbda57, with the same harness).</summary>
        private static readonly Dictionary<string, string> Baseline = new Dictionary<string, string>
        {
            ["scatter"] = "21957EDD5B1915AEE5D0203F3C9896AE1439BAD216F24169A00CB006C9994F50",
            ["row"] = "403B62556C4D882160540022429ACCF9503E14B956DC985E6BED0F96CF93D3E0",
            ["ring"] = "BB96293999FF16CD1322DB7E3B2311700F33DF52C52B88312499F2CC76B9F695",
            ["sweep"] = "0310C28C6314A33174830571676217D28A2E17E98B490B8389C0F8D7D141C37A",
            ["mixed"] = "9D701D776A194CD355DA8E83BBBB655598E3AF89E527F5C3F0EABD41C917A312",
            ["follow"] = "5F2BBF7412BF801C2E2DE9363E217E915CB5850501F56AB3FBC28F9163F4F756",
        };

        private static WispDirectorTests.FakePort RunScenario(string args, string pattern, Action<WispDirectorTests.FakePort> arrange = null, int squad = -1, List<WispDirectorTests.FakeHandle> wisps = null)
        {
            var s = WispDirectorTests.S(args);
            var port = new WispDirectorTests.FakePort();
            var list = wisps ?? WispDirectorTests.Squad(squad < 0 ? s.Count : squad);
            var follower = s.Follow ? new WispDirectorTests.FakeHandle { Id = 99, Pos = new System.Numerics.Vector3(0f, -30f, -51f) } : null;
            var director = new WispDirector<WispDirectorTests.FakeHandle>(WispDirectorTests.Pinnacle(), s, pattern, port, list, follower);

            if (follower != null)
                port.Target = t => new System.Numerics.Vector3(5f + (float)(t % 5.0), -40f, -51f);

            arrange?.Invoke(port);
            WispDirectorTests.Run(director, port, 0.1);
            return port;
        }

        [TestMethod]
        public void NullLiveCount_TraceIsIdenticalToBeforeA2_ForEveryPattern()
        {
            var changed = new List<string>();

            foreach (var (name, args, pattern) in Scenarios)
            {
                var port = RunScenario(args, pattern);
                var trace = Trace(port);

                Assert.AreEqual(0, port.WispSpawns.Count, name + ": a null live count never spawns a wisp");
                Assert.AreEqual(0, port.Destroyed.Count(d => !port.MarkHandles.Contains(d.Handle)), name + ": and never destroys one");
                Assert.IsFalse(trace.Contains("live count"), name + ": and logs nothing about it");

                if (Baseline[name] != Fingerprint(trace))
                    changed.Add(name + "=" + Fingerprint(trace));
            }

            Assert.AreEqual(0, changed.Count, "the fake-port action trace changed: " + string.Join(" ", changed));
        }

        [TestMethod]
        public void LiveCount_IsReadOnlyAtTheFirstGlideOfEachCycle()
        {
            // sweep: several steps per cycle, so a read per glide would be seen
            var port = RunScenario("cycles=3 move=2 hold=1.2 burst=1 rest=0.5 n=8 dir=n", "sweep");
            var glidesPerRun = port.Moves.Count;

            Assert.IsTrue(glidesPerRun > 3, "the sweep has more than one glide per cycle");
            Assert.AreEqual(3, port.LiveCountReads, "one read per cycle, at its first glide, not per tick or per step");
        }

        [TestMethod]
        public void LiveCount_Above_N_GrowsTheSquad_AtSpawnPoints_AndPlansForIt()
        {
            var wisps = WispDirectorTests.Squad(8);
            var port = RunScenario("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=8", "scatter", p => p.LiveCountValue = 12, wisps: wisps);

            Assert.AreEqual(4, port.WispSpawns.Count, "12 asked, 8 alive: four spawned");
            Assert.AreEqual(12, wisps.Count, "the squad now holds 12");
            Assert.IsTrue(port.WispSpawns.All(w => w.Point.Z == -51f), "spawned at the hazard plane (PointFor)");
            Assert.IsTrue(port.Logs.Any(l => l.StartsWith("live count 12: squad 8 -> 12")), string.Join("|", port.Logs));

            var crew = port.Moves[0].Group.Count;
            Assert.IsTrue(crew > 8 && crew <= 12, "the formation was planned for 12 wisps, not 8: crew " + crew);
            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
        }

        [TestMethod]
        public void LiveCount_Below_N_DestroysTheExtras()
        {
            var wisps = WispDirectorTests.Squad(8);
            var port = RunScenario("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=8", "scatter", p => p.LiveCountValue = 3, wisps: wisps);

            Assert.AreEqual(0, port.WispSpawns.Count);
            Assert.AreEqual(5, port.Destroyed.Count, "five extras destroyed");
            Assert.AreEqual(3, wisps.Count);
            Assert.IsTrue(port.Moves[0].Group.Count <= 3, "the crew is at most the three left");
        }

        [TestMethod]
        public void LiveCount_IsClamped_OneToThirtyTwo()
        {
            var low = WispDirectorTests.Squad(8);
            var lowRun = RunScenario("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=8", "scatter", p => p.LiveCountValue = -5, wisps: low);

            Assert.AreEqual(1, low.Count, "zero and below read as 1");
            Assert.AreEqual(7, lowRun.Destroyed.Count);

            var high = WispDirectorTests.Squad(8);
            var highRun = RunScenario("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=8", "scatter", p => p.LiveCountValue = 500, wisps: high);

            Assert.AreEqual(32, high.Count, "above 32 reads as 32");
            Assert.AreEqual(24, highRun.WispSpawns.Count);
        }

        [TestMethod]
        public void LiveCount_IsReReadEachCycle_RampingTheSquad()
        {
            var wisps = WispDirectorTests.Squad(8);

            var port = RunScenario("cycles=3 move=2 hold=1.2 burst=1 rest=0.5 n=8", "scatter", p =>
            {
                var read = 0;
                p.LiveCountAt = _ => new int?[] { 4, 10, 6 }[read++];
            }, wisps: wisps);

            Assert.AreEqual(3, port.LiveCountReads);
            Assert.AreEqual(6, wisps.Count, "the last cycle's count");
            Assert.AreEqual(6, port.WispSpawns.Count, "4 -> 10 spawned six");
            Assert.AreEqual(4 + 4, port.Destroyed.Count, "8 -> 4, then 10 -> 6");
        }

        [TestMethod]
        public void LiveCount_DropsTheDeadAndRefillsTheSquad_AndASpawnThatFailsLeavesItShort()
        {
            var wisps = WispDirectorTests.Squad(8);
            wisps[0].Dead = true;
            wisps[1].Dead = true;

            var port = RunScenario("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=8", "scatter", p =>
            {
                p.LiveCountValue = 8;
                p.WispSpawnFails = _ => true;
            }, wisps: wisps);

            Assert.AreEqual(2, port.WispSpawns.Count, "two dead, two asked for");
            Assert.AreEqual(6, wisps.Count, "refused spawns leave the squad short; the run carries on");
            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
        }

        [TestMethod]
        public void LiveCount_EqualToTheSquad_ChangesNothingButTheRead()
        {
            var wisps = WispDirectorTests.Squad(6);
            var port = RunScenario("cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=6", "scatter", p => p.LiveCountValue = 6, wisps: wisps);
            var plain = RunScenario("cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=6", "scatter");

            Assert.AreEqual(0, port.WispSpawns.Count);
            Assert.AreEqual(0, port.Destroyed.Count);
            Assert.AreEqual(Trace(plain), Trace(port), "a count equal to the squad is the same run");
        }
    }
}