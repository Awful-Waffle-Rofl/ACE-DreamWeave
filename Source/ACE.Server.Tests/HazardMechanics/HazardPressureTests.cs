using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Hazard pressure (HazardPressureHarness): %maxHP lost per player-minute, and the worst-off player's share, for each World
    /// Event hazard kind, by behaviour (S / D perfect / D50 degraded / K) x group size (1/4/12/24) x live count (4 = ramp low,
    /// 10 = ramp high). The tests REPORT; they assert only sanity, never a target band.
    ///
    /// THE BIG TABLES RUN ONLY WITH THE ENVIRONMENT VARIABLE ACE_HAZARD_PRESSURE=1 (they report Inconclusive otherwise, so a
    /// normal CI run skips them). Run them with
    ///   $env:ACE_HAZARD_PRESSURE = "1"; dotnet test Source\ACE.Server.Tests --test-adapter-path:. --filter "FullyQualifiedName~HazardPressureTests" --logger "console;verbosity=detailed"
    /// Each table is printed to the test output and written to HazardPressure/&lt;name&gt;.csv under the test output directory.
    /// Always on (cheap): a small sanity grid, the args mutation check, the ledger parity against the real HitPlayers, the
    /// config copy, linearity, and the source pins on every piece of RollingBallMechanic the approximate ball model copies.
    /// </summary>
    [TestClass]
    [TestCategory("HazardPressure")]
    public class HazardPressureTests
    {
        public TestContext TestContext { get; set; }

        public const string EnvVar = "ACE_HAZARD_PRESSURE";

        private static readonly int[] GroupSizes = { 1, 4, 12, 24 };
        private static readonly int[] LiveCounts = { 4, 10 };

        private const double Seconds = 300.0;

        /// <summary>Seeds per cell: small groups are noisy (one player's luck), so they get more.</summary>
        private static int SeedsFor(int players) => players <= 4 ? 20 : 5;

        /// <summary>Candidate rows (laid over the shipped World Event row of their kind).</summary>
        public const string RainCandidate = "target_share=0.25 target_spread=8 duration=60";
        public const string WispCandidate = "move=2.5 hold=2 rest=2 follow=1 follow_lag=1.5";
        public const string BallCandidate = "waves=10";

        private static void RequireTables()
        {
            if (Environment.GetEnvironmentVariable(EnvVar) != "1")
                Assert.Inconclusive($"hazard pressure tables run only with {EnvVar}=1");
        }

        private static List<PressureSummary> Grid(PressureConfig template, IEnumerable<int> groups = null, IEnumerable<int> counts = null)
        {
            var rows = new List<PressureSummary>();

            foreach (var count in counts ?? LiveCounts)
                foreach (var n in groups ?? GroupSizes)
                    foreach (var b in HazardPressureHarness.AllBehaviours)
                        rows.Add(HazardPressureHarness.Measure(template with { Players = n, Behaviour = b, LiveCount = count }, SeedsFor(n)));

            return rows;
        }

        private static PressureConfig Of(string kind, string args = null, string pattern = null, double seconds = Seconds)
            => new PressureConfig { Kind = kind, Args = args, Pattern = pattern, Seconds = seconds, Seed = 1 };

        private void Report(string name, string title, List<PressureSummary> rows)
        {
            TestContext.WriteLine(HazardPressureHarness.Table(title, rows));

            var dir = Path.Combine(AppContext.BaseDirectory, "HazardPressure");
            Directory.CreateDirectory(dir);

            var csv = new StringBuilder();
            csv.AppendLine(HazardPressureHarness.CsvHeader);

            foreach (var r in rows)
                csv.AppendLine(HazardPressureHarness.CsvRow(r));

            var path = Path.Combine(dir, name + ".csv");
            File.WriteAllText(path, csv.ToString());
            TestContext.WriteLine("csv: " + path);
        }

        /// <summary>
        /// Non-negative and finite everywhere, and every player's own total sums to the group's. With
        /// <paramref name="kiterBelowStationary"/> (rain: every aimed drop lands where its target stood when it was aimed) the
        /// kiter is also not significantly worse off than the stationary player. Untargeted hazards are measured to be WORSE
        /// for a mover (a 4 m/s kiter walks into pillars that are already burning and across ball lanes), so it is not asserted
        /// for them: that is a finding, not a harness fault.
        /// </summary>
        private static void AssertSane(List<PressureSummary> rows, bool kiterBelowStationary)
        {
            foreach (var r in rows)
            {
                foreach (var run in r.Runs)
                {
                    Assert.IsTrue(double.IsFinite(run.PctPerPlayerMinute) && run.PctPerPlayerMinute >= 0.0, $"{Describe(r)}: pct {run.PctPerPlayerMinute}");
                    Assert.IsTrue(double.IsFinite(run.HitsPerPlayerMinute) && run.HitsPerPlayerMinute >= 0.0, $"{Describe(r)}: hits {run.HitsPerPlayerMinute}");
                    Assert.IsTrue(run.RunsStarted >= 1, $"{Describe(r)}: no hazard run started");
                    Assert.AreEqual(run.PctLost, run.PlayerPct.Sum(), 1e-6, $"{Describe(r)}: per-player totals do not sum to the group's");
                    Assert.IsTrue(run.MaxPlayerPctPerMinute + 1e-9 >= run.PctPerPlayerMinute, $"{Describe(r)}: max player below the mean");
                }
            }

            if (!kiterBelowStationary)
                return;

            foreach (var g in rows.GroupBy(r => (Label: HazardPressureHarness.RowLabel(r.Config), r.Config.CrowdRadius, r.Config.LiveCount, r.Config.Players)))
            {
                var s = g.Single(r => r.Config.Behaviour == PressureBehaviour.Stationary);
                var k = g.Single(r => r.Config.Behaviour == PressureBehaviour.Kiter);
                var noise = 3.0 * Math.Sqrt(s.SePct * s.SePct + k.SePct * k.SePct) + 1.0;

                Assert.IsTrue(k.MeanPct <= s.MeanPct + noise,
                    $"{Describe(k)}: kiter {k.MeanPct:0.00} > stationary {s.MeanPct:0.00} + noise {noise:0.00}");
            }
        }

        private static string Describe(PressureSummary r)
            => $"{r.Config.Kind} {HazardPressureHarness.RowLabel(r.Config)} {HazardPressureHarness.Letter(r.Config.Behaviour)} N={r.Config.Players} count={r.Config.LiveCount} crowd={r.Config.CrowdRadius}";

        // =============================================================================================================
        // always on
        // =============================================================================================================

        /// <summary>A cheap sanity grid over every kind (60 s, 2 seeds): the harness runs, sums and stays finite.</summary>
        [TestMethod]
        public void Sanity_SmallGrid_EveryKind()
        {
            foreach (var template in new[]
            {
                Of(HazardPressureHarness.Rain, seconds: 60),
                Of(HazardPressureHarness.Wisps, seconds: 60),
                Of(HazardPressureHarness.Balls, pattern: "fan", seconds: 60),
                Of(HazardPressureHarness.Balls, seconds: 60) with { EventLevel = true },
            })
            {
                var rows = HazardPressureHarness.AllBehaviours
                    .Select(b => HazardPressureHarness.Measure(template with { Players = 4, Behaviour = b, LiveCount = 10 }, 2))
                    .ToList();

                AssertSane(rows, kiterBelowStationary: false);
                Assert.IsTrue(rows.Single(r => r.Config.Behaviour == PressureBehaviour.Stationary).Runs.Sum(r => r.Events) > 0, template.Kind + ": no hazard events at all");
            }
        }

        /// <summary>Mutation check: rain with no aimed drops (target_share=0) must press a stationary group less than the default (aimed) rain.</summary>
        [TestMethod]
        public void Rain_TargetShareZero_LowersStationaryPressure()
        {
            PressureSummary M(string args) => HazardPressureHarness.Measure(
                Of(HazardPressureHarness.Rain, args, seconds: 120) with { Players = 4, Behaviour = PressureBehaviour.Stationary, LiveCount = 10 }, 5);

            var aimed = M(null);
            var uniform = M(HazardPressureHarness.Overlay(HazardPressureHarness.Rain, "target_share=0"));

            TestContext.WriteLine($"rain S N=4 count=10: default {aimed.MeanPct:0.00} %/min, target_share=0 {uniform.MeanPct:0.00} %/min");

            Assert.IsTrue(uniform.MeanPct < aimed.MeanPct, $"target_share=0 {uniform.MeanPct:0.00} should be below the default {aimed.MeanPct:0.00}");
        }

        /// <summary>Damage is linear in pct (same hits, double the damage, exactly), and a bigger burst never presses a stationary group less.</summary>
        [TestMethod]
        public void Linearity_PctDoubles_AndBurstRadiusDoesNotLowerRain()
        {
            var basis = Of(HazardPressureHarness.Rain, seconds: 60) with { Players = 4, Behaviour = PressureBehaviour.Stationary, LiveCount = 10 };

            for (var seed = 1; seed <= 3; seed++)
            {
                var single = HazardPressureHarness.Run(basis with { Seed = seed });
                var twice = HazardPressureHarness.Run(basis with { Seed = seed, Pct = basis.Pct * 2.0 });

                Assert.AreEqual(single.Hits, twice.Hits, "pct must not change who is hit");
                Assert.AreEqual(2.0 * single.PctPerPlayerMinute, twice.PctPerPlayerMinute, 1e-9, "2x pct must give exactly 2x %/min");
            }

            var narrow = HazardPressureHarness.Measure(basis with { Args = HazardPressureHarness.Overlay(HazardPressureHarness.Rain, "burst_radius=3") }, 5);
            var wide = HazardPressureHarness.Measure(basis with { Args = HazardPressureHarness.Overlay(HazardPressureHarness.Rain, "burst_radius=5") }, 5);

            TestContext.WriteLine($"rain S N=4 count=10: burst 3 -> {narrow.MeanPct:0.00}, burst 5 -> {wide.MeanPct:0.00} %/min");
            Assert.IsTrue(wide.MeanPct >= narrow.MeanPct, $"burst_radius 5 ({wide.MeanPct:0.00}) pressed less than 3 ({narrow.MeanPct:0.00})");
        }

        /// <summary>WithSeed copies every other property (a field added to PressureConfig and forgotten by a copy would show here).</summary>
        [TestMethod]
        public void PressureConfig_WithSeed_KeepsEveryOtherProperty()
        {
            var props = typeof(PressureConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToList();
            var c = new PressureConfig();
            var i = 0;

            foreach (var p in props)
            {
                i++;
                object v;

                if (p.PropertyType == typeof(string)) v = "v" + i;
                else if (p.PropertyType == typeof(int)) v = 100 + i;
                else if (p.PropertyType == typeof(uint)) v = (uint)(100 + i);
                else if (p.PropertyType == typeof(double)) v = 100.5 + i;
                else if (p.PropertyType == typeof(double?)) v = (double?)(200.5 + i);
                else if (p.PropertyType == typeof(float)) v = 100.25f + i;
                else if (p.PropertyType == typeof(bool)) v = true;
                else if (p.PropertyType == typeof(PressureBehaviour)) v = PressureBehaviour.Kiter;
                else
                {
                    Assert.Fail($"PressureConfig.{p.Name} has a type ({p.PropertyType.Name}) this test does not fill: extend it");
                    return;
                }

                p.SetValue(c, v);
            }

            var copy = c.WithSeed(4242);

            Assert.AreEqual(4242, copy.Seed);

            foreach (var p in props.Where(p => p.Name != nameof(PressureConfig.Seed)))
                Assert.AreEqual(p.GetValue(c), p.GetValue(copy), $"{p.Name} lost by WithSeed");
        }

        // ---- ledger parity with the real HazardContext.HitPlayers ----------------------------------------------

        private const ushort ParityAnchor = 0x7F7F;

        private sealed class ParityPlayers : HazardContext.IPlayerSource
        {
            public Landblock Lb;
            public List<HazardContext.PlayerSighting> List = new List<HazardContext.PlayerSighting>();

            public int Count(Landblock landblock) => ReferenceEquals(landblock, Lb) ? List.Count : 0;

            public bool TryGet(Landblock landblock, int index, out HazardContext.PlayerSighting sighting)
            {
                sighting = List[index];
                return true;
            }
        }

        private readonly record struct ParityStrike(double At, long Source, int? Group, Vector3 Centre, float Radius, double Window, Vector3[] Players);

        /// <summary>A seeded strike sequence: 8 moving players, 6 sources, shared-group volleys (two sources, one group, same tick, one player in reach of both), windows 0.5 / 1 / infinity.</summary>
        private static List<ParityStrike> ParitySequence(int seed)
        {
            var rng = new Random(seed);
            var players = Enumerable.Range(0, 8).Select(_ => new Vector3(80f + (float)rng.NextDouble() * 32f, 80f + (float)rng.NextDouble() * 32f, 5f)).ToArray();
            var list = new List<ParityStrike>();
            var t = 1.0;
            double[] windows = { 0.5, 1.0, double.PositiveInfinity };

            for (var i = 0; i < 400; i++)
            {
                t += rng.NextDouble() * 0.3;

                for (var p = 0; p < players.Length; p++)
                    players[p] = new Vector3(Math.Clamp(players[p].X + (float)(rng.NextDouble() - 0.5) * 2f, 76f, 116f), Math.Clamp(players[p].Y + (float)(rng.NextDouble() - 0.5) * 2f, 76f, 116f), 5f);

                var snapshot = (Vector3[])players.Clone();
                var window = windows[rng.Next(windows.Length)];

                if (i % 10 == 0)
                {
                    // a volley: two pillars under one group, both covering player 0
                    var group = 1000 + i;
                    var target = snapshot[0];
                    list.Add(new ParityStrike(t, 1 + rng.Next(6), group, target + new Vector3(1f, 0f, 0f), 3f, double.PositiveInfinity, snapshot));
                    list.Add(new ParityStrike(t, 1 + rng.Next(6), group, target - new Vector3(1f, 0f, 0f), 3f, double.PositiveInfinity, snapshot));
                    continue;
                }

                int? g = rng.NextDouble() < 0.25 ? 2000 + rng.Next(4) : null;
                var centre = new Vector3(80f + (float)rng.NextDouble() * 32f, 80f + (float)rng.NextDouble() * 32f, 5f);
                list.Add(new ParityStrike(t, 1 + rng.Next(6), g, centre, 1f + (float)rng.NextDouble() * 4f, window, snapshot));
            }

            return list;
        }

        private static HazardRegion ParityRegion() => HazardRegion.ArenaCircle("parity", ParityAnchor, 0, 96f, 96f, 30f, 1.5f, 0f, 20f);

        private static int[] RealHits(List<ParityStrike> seq)
        {
            var region = ParityRegion();
            var world = new HazardSpanTests.FakeWorld();
            var lb = world.Add(ParityAnchor, new object());
            var run = new HazardRun(1, "test", null, region, HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow);
            var ctx = run.Context;
            var span = HazardSpan.For(region, lb, world);
            span.Refresh();
            ctx.Span = span;

            var players = new ParityPlayers { Lb = lb };
            ctx.PlayerSource = players;

            var hits = new int[8];
            ctx.DamageForTest = (src, seen, fraction, type, text) => { hits[seen.Guid - 1]++; return 7; };

            var handles = new Dictionary<long, HazardHandle>();

            foreach (var s in seq)
            {
                ctx.SetNow(s.At);
                players.List = s.Players.Select((p, i) => new HazardContext.PlayerSighting(null, (uint)(i + 1),
                    new Position(((uint)ParityAnchor << 16) | 0x0001, p.X, p.Y, p.Z, 0f, 0f, 0f, 1f, 0), false, false)).ToList();

                if (!handles.TryGetValue(s.Source, out var h))
                {
                    var wo = (GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject));
                    handles[s.Source] = h = new HazardHandle((int)s.Source, wo, new HazardSpawnSpec { Wcid = 1006951, Tag = "src" }, Vector3.Zero);
                }

                ctx.HitPlayers(h, s.Centre, s.Radius, s.Window, hitGroup: s.Group);
            }

            return hits;
        }

        private static int[] LedgerHits(List<ParityStrike> seq, bool dropHitGroup)
        {
            var region = ParityRegion();
            var ledger = new PressureLedger { DropHitGroup = dropHitGroup };
            var hits = new int[8];
            var playerRadius = HazardArgs.Empty.GetDouble(HazardDefaults.PlayerRadiusKey, HazardDefaults.PlayerRadius, HazardDefaults.MinPlayerRadius, HazardDefaults.MaxPlayerRadius);

            foreach (var s in seq)
                ledger.Strike(s.Source, s.Group, s.Centre, s.Radius, s.Window, s.At, s.Players, playerRadius, p => region.Contains(p.X, p.Y), i => hits[i]++);

            return hits;
        }

        [TestMethod]
        public void Ledger_MatchesTheRealHitPlayers_OnARandomisedSequence_AndFailsWithoutTheHitGroup()
        {
            foreach (var seed in new[] { 1, 2, 3 })
            {
                var seq = ParitySequence(seed);
                var real = RealHits(seq);

                Assert.IsTrue(real.Sum() > 50, $"seed {seed}: the sequence must actually hit ({real.Sum()})");
                CollectionAssert.AreEqual(real, LedgerHits(seq, dropHitGroup: false), $"seed {seed}: ledger differs from HitPlayers");

                // the mutation: a ledger keyed by source alone double-hits volleys, and this sequence must notice
                CollectionAssert.AreNotEqual(real, LedgerHits(seq, dropHitGroup: true), $"seed {seed}: dropping the hit group went unnoticed");
            }
        }

        // ---- source pins: the code the approximate ball model and the fake ports copy --------------------------

        private static string SourceText(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n");
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static int Occurrences(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }

        /// <summary>(file, exact needle, expected count): what the harness copies, so a change to the original shows here.</summary>
        private static readonly (string File, string Needle, int Count)[] Pins =
        {
            // the lagged sweep step, in SweepWave and in RollGroup's OnLegEnd
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "p.Reach * 0.5f", 2),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "if (BallHits.Tick(clock, id, h.Alive, h.Path, b.Tail, ctx.Now, p.HitLag, p.Reach * 0.5f, pt => Hit(ctx, b, pt)))", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "if (!BallHits.LegEnded(waveClock, h.Id, path, ref b.Tail, p.HitLag, p.Reach * 0.5f, pt => Hit(ctx, b, pt)))", 1),
            // the spawn groups (sequential and cadence)
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "foreach (var group in plan.Routes.GroupBy(r => Math.Round(r.Delay, 3)))", 2),
            // cadence planning, lead, budget, open
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "var waveParams = BallRules.CapToLiveCount(BallRules.ForWave(p, n), ctx.LiveCount);", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "if (!TryPlanWave(ctx, waveParams, n, out var plan, out var fanDistance, out var aimedAt, cadence.Distinct ? cadence.BusyWalls : null))", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "var waveLead = BallRules.LeadFor(ctx.Region, ctx.Pattern, waveParams, fanDistance);", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "plan = BallPatterns.PlanFanAlong(ctx.Region, waveParams, aim.Angle);", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "plan = BallPatterns.Plan(ctx.Region, ctx.Pattern, waveParams, ctx.Rng, waveNo, busyWalls);", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "() => BallBudget.For(ctx.LiveCount, p.MaxBalls),", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "Clock = cadence.Open(n, plan.Routes.Count, plan.Gaps.Select(g => g.Side))", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "ctx.Schedule(w.Lead, () =>", 1),
            // the roll and the hit
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "ctx.MoveLegs(members, BallRules.LegSeconds(legGroup.First().Route, waveParams.Speed), options);", 1),
            ("Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs", "var n = ctx.HitPlayers(ball.Handle, centre, p.BallRadius, HitWindowSeconds, damageType: ctx.DamageType);", 1),
            // the fake ports
            ("Source/ACE.Server/HazardMechanics/RainMechanic.cs", "=> ctx.HitPlayers(source, centre, radius, window, damageType: ctx.DamageType);", 1),
            ("Source/ACE.Server/HazardMechanics/WispSquadMechanic.cs", "hitGroup: volley == 0 ? (int?)null : volley", 1),
            ("Source/ACE.Server/HazardMechanics/WispSquadMechanic.cs", "position = inRegion[0].Position;", 1),
            ("Source/ACE.Server/HazardMechanics/WispSquadMechanic.cs", "foreach (var (player, position) in ctx.PlayersInRegion())\n                    result.Add((position, player.Guid.Full));", 1),
            ("Source/ACE.Server/HazardMechanics/WispSquadMechanic.cs", "var spots = WispFormations.SpawnPoints(ctx.Region, settings.Count, ctx.EdgeInset, ctx.Rng,", 1),
            ("Source/ACE.Server/HazardMechanics/HazardContext.cs", "var key = (hitGroup ?? source.Id, seen.Guid);", 1),
            // the World Event start
            ("Source/ACE.Server/WorldEvents/WorldEvent.cs", "var kind = WorldEventHazards.NextRunKind(hazardPlan, hazardRng);", 1),
            ("Source/ACE.Server/WorldEvents/WorldEvent.cs", "var start = WorldEventHazards.BuildStart(hazardPlan, kind, hazardElement.Value, anchorZ, hazardRng, hazardHost);", 1),
        };

        /// <summary>One character changed inside the needle: what a real edit to the copied line looks like.</summary>
        private static string Mutate(string needle) => needle.Substring(0, needle.Length / 2) + "#" + needle.Substring(needle.Length / 2 + 1);

        [TestMethod]
        public void BallCopy_SourcePins_HoldAndAreMutationProven()
        {
            foreach (var (file, needle, count) in Pins)
            {
                var text = SourceText(file);

                Assert.AreEqual(count, Occurrences(text, needle), $"{file}: '{needle}' should appear {count}x; the harness copies it, so update the harness with it");

                // mutation proof: the same check on the file with that line edited fails
                var mutated = text.Replace(needle, Mutate(needle));
                Assert.AreNotEqual(count, Occurrences(mutated, needle), $"{file}: the pin on '{needle}' cannot fail");
            }

            // HazardManager.TickRun order: motion (leg ends), then due steps, then the mechanic's tick
            var manager = SourceText("Source/ACE.Server/HazardMechanics/HazardManager.cs");

            bool InOrder(string t)
            {
                var a = t.IndexOf("ctx.AdvanceMotion();", StringComparison.Ordinal);
                var b = t.IndexOf("ctx.RunDue();", a < 0 ? 0 : a, StringComparison.Ordinal);
                var c = t.IndexOf("run.Mechanic.Tick(ctx);", b < 0 ? 0 : b, StringComparison.Ordinal);
                return a >= 0 && b > a && c > b;
            }

            Assert.IsTrue(InOrder(manager), "HazardManager.TickRun no longer runs AdvanceMotion, RunDue, Mechanic.Tick in that order");
            Assert.IsFalse(InOrder(manager.Replace("ctx.AdvanceMotion();", "")), "the order pin cannot fail");
        }

        // =============================================================================================================
        // the tables (ACE_HAZARD_PRESSURE=1)
        // =============================================================================================================

        [TestMethod]
        public void Rain_CurrentDefaults_Table()
        {
            RequireTables();
            var rows = Grid(Of(HazardPressureHarness.Rain));
            Report("rain-defaults", "RAIN, current World Event row [" + HazardPressureHarness.DefaultArgs(HazardPressureHarness.Rain) + "]", rows);
            AssertSane(rows, kiterBelowStationary: true);
        }

        [TestMethod]
        public void Wisps_CurrentDefaults_Table()
        {
            RequireTables();
            var rows = Grid(Of(HazardPressureHarness.Wisps));
            Report("wisps-defaults", "WISPS (pattern mixed), current World Event row [" + HazardPressureHarness.DefaultArgs(HazardPressureHarness.Wisps) + "]", rows);
            AssertSane(rows, kiterBelowStationary: false);
        }

        /// <summary>Balls: per pattern and the pool (rolling runs only), then the event level (rolling and rain alternating per run).</summary>
        private List<PressureSummary> BallTables(string name, string args, string title)
        {
            var pool = HazardPressureHarness.BallPool(args);
            Assert.IsTrue(pool.Count > 0, "the World Event plan has no ball pattern");

            var rolling = pool.SelectMany(pattern => Grid(Of(HazardPressureHarness.Balls, args, pattern))).ToList();

            // the pool with the per-run pick needs several runs per seed to mix patterns (a waves=50 run lasts 300 s)
            rolling.AddRange(Grid(Of(HazardPressureHarness.Balls, args, seconds: 1800)));
            Report(name, title + " ROLLING RUNS ONLY (APPROXIMATE geometric model); pool " + string.Join(",", pool) + "; 'pool' = BuildStart's per-run pick over 1800 s", rolling);
            AssertSane(rolling, kiterBelowStationary: false);

            var eventRows = Grid(Of(HazardPressureHarness.Balls, args, seconds: 1800) with { EventLevel = true });
            Report(name + "-event", title + " EVENT LEVEL: rolling and rain alternate per run (WorldEventHazards.NextRunKind), 1800 s; rolling halves APPROXIMATE", eventRows);
            AssertSane(eventRows, kiterBelowStationary: false);

            var rainShare = eventRows.Sum(r => r.Runs.Sum(x => x.RainRuns)) / (double)eventRows.Sum(r => r.Runs.Sum(x => x.RunsStarted));
            TestContext.WriteLine($"event level: {rainShare:P0} of the runs were rain");
            Assert.IsTrue(rainShare > 0.2 && rainShare < 0.8, $"the event level should mix rolling and rain, saw {rainShare:P0} rain");

            return rolling.Concat(eventRows).ToList();
        }

        [TestMethod]
        public void Balls_CurrentDefaults_Tables()
        {
            RequireTables();
            BallTables("balls-defaults", null, "BALLS, current World Event row [" + HazardPressureHarness.DefaultArgs(HazardPressureHarness.Balls) + "]");
        }

        /// <summary>How much the crowd's spread matters: crowd radius 10 / 20 / 40 at N=12 for rain, wisps and the fan.</summary>
        [TestMethod]
        public void CrowdRadius_Sensitivity_Table()
        {
            RequireTables();

            var rows = new List<PressureSummary>();

            foreach (var radius in new[] { 10f, 20f, 40f })
            {
                foreach (var template in new[] { Of(HazardPressureHarness.Rain), Of(HazardPressureHarness.Wisps), Of(HazardPressureHarness.Balls, pattern: "fan") })
                    rows.AddRange(Grid(template with { CrowdRadius = radius }, groups: new[] { 12 }));
            }

            Report("crowd-radius", "CROWD RADIUS 10/20/40 m, N=12, current defaults (rain '-', wisps 'mixed', balls 'fan' APPROXIMATE)", rows);
            AssertSane(rows, kiterBelowStationary: false);
        }

        /// <summary>
        /// Tuning probe: ACE_HAZARD_PROBE names a text file, one row per line, "kind TAB row" where kind is rain | wisps |
        /// balls-event (its row is "balls row || rain row"; an empty side is the shipped default). Each row is measured at
        /// the owner's band point (N=12, count 10, stationary) and printed as one line: mean (se) / worst player / p90.
        /// With ACE_HAZARD_PROBE_FULL=1 every row also gets the full N x count x behaviour grid, printed and written to CSV.
        /// </summary>
        [TestMethod]
        public void Tuning_Probe()
        {
            var file = Environment.GetEnvironmentVariable("ACE_HAZARD_PROBE");

            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                Assert.Inconclusive("set ACE_HAZARD_PROBE to a file of 'kind<TAB>row' lines");

            var full = Environment.GetEnvironmentVariable("ACE_HAZARD_PROBE_FULL") == "1"; var probeSeeds = int.TryParse(Environment.GetEnvironmentVariable("ACE_HAZARD_PROBE_SEEDS"), out var ps) && ps > 0 ? ps : 5;
            var lineNo = 0;

            foreach (var raw in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;

                lineNo++;
                var parts = raw.Split('\t');
                var kind = parts[0].Trim();
                var row = parts.Length > 1 ? parts[1].Trim() : "";
                PressureConfig template;

                if (kind == "balls-event")
                {
                    var sides = row.Split(new[] { "||" }, StringSplitOptions.None);
                    var balls = sides[0].Trim();
                    var rain = sides.Length > 1 ? sides[1].Trim() : "";
                    template = Of(HazardPressureHarness.Balls, balls.Length > 0 ? HazardPressureHarness.Overlay(HazardPressureHarness.Balls, balls) : null, seconds: 1800)
                        with { EventLevel = true, RainArgs = rain.Length > 0 ? HazardPressureHarness.Overlay(HazardPressureHarness.Rain, rain) : null };
                }
                else
                    template = Of(kind, row.Length > 0 ? HazardPressureHarness.Overlay(kind, row) : null);

                var band = HazardPressureHarness.Measure(template with { Players = 12, Behaviour = PressureBehaviour.Stationary, LiveCount = 10 }, probeSeeds);
                TestContext.WriteLine($"PROBE {lineNo} {kind} [{row}]: N12 c10 S mean {band.MeanPct:0.0} (se {band.SePct:0.0}) worst {band.MeanMaxPct:0.0} p90 {band.MeanP90Pct:0.0} runs {band.Runs.Sum(r => r.RunsStarted)} rain {band.Runs.Sum(r => r.RainRuns)} events/min {band.EventsPerMinute:0.0} patterns {string.Join(",", band.Runs[0].Patterns)}");

                if (full)
                {
                    var rows = Grid(template);
                    Report("probe-" + lineNo, $"PROBE {lineNo} {kind} [{row}]", rows);
                    AssertSane(rows, kiterBelowStationary: false);
                }
            }
        }

        /// <summary>The proposed rows (laid over the shipped rows) for the owner's band: N=12, count 10, stationary, mean 35-50 and worst player &lt;= 100 %/min.</summary>
        public const string ProposedRain = "target_share=0.3 target_spread=8";
        public const string ProposedWisps = "pillar_radius=5 follow=1 follow_target=random follow_lag=2.4 pct=0.23";
        public const string ProposedBalls = "";

        /// <summary>
        /// The proposed rain, wisps and event-level balls (rolling alternating with the PROPOSED rain), full grids at 20 seeds
        /// for N &lt;= 4. Also an element-portal row, 50/50 by time wisps vs the balls event: its mean is the exact average of
        /// the two per-minute rates; its worst / p90 are the averages of the two, an UPPER bound on the mixture's (a player's
        /// mixed rate is the average of their two rates, and the max of averages is at most the average of maxes).
        /// </summary>
        [TestMethod]
        public void Proposals_Tables()
        {
            RequireTables();

            var rain = HazardPressureHarness.Overlay(HazardPressureHarness.Rain, ProposedRain);
            var wisps = HazardPressureHarness.Overlay(HazardPressureHarness.Wisps, ProposedWisps);
            var balls = HazardPressureHarness.Overlay(HazardPressureHarness.Balls, ProposedBalls);

            var rainRows = Grid(Of(HazardPressureHarness.Rain, rain));
            Report("proposed-rain", "PROPOSED RAIN [" + rain + "]", rainRows);
            AssertSane(rainRows, kiterBelowStationary: true);

            var wispRows = Grid(Of(HazardPressureHarness.Wisps, wisps));
            Report("proposed-wisps", "PROPOSED WISPS [" + wisps + "]", wispRows);
            AssertSane(wispRows, kiterBelowStationary: false);

            var eventRows = Grid(Of(HazardPressureHarness.Balls, balls, seconds: 1800) with { EventLevel = true, RainArgs = rain });
            Report("proposed-balls-event", "PROPOSED BALLS EVENT LEVEL [" + balls + "] alternating with rain [" + rain + "], 1800 s; rolling halves APPROXIMATE", eventRows);
            AssertSane(eventRows, kiterBelowStationary: false);

            var sb = new StringBuilder();
            sb.AppendLine("ELEMENT PORTAL, 50/50 by time wisps vs balls event: mean exact; worst / p90 = average of the two (upper bound)");
            sb.AppendLine("count   N  behaviour   mean   worst<=   p90<=");

            foreach (var w in wispRows.OrderBy(r => r.Config.LiveCount).ThenBy(r => r.Config.Players).ThenBy(r => r.Config.Behaviour))
            {
                var e = eventRows.Single(r => r.Config.LiveCount == w.Config.LiveCount && r.Config.Players == w.Config.Players && r.Config.Behaviour == w.Config.Behaviour);
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,5} {1,3}  {2,-9} {3,6:0.0} {4,8:0.0} {5,7:0.0}",
                    w.Config.LiveCount, w.Config.Players, HazardPressureHarness.Letter(w.Config.Behaviour),
                    (w.MeanPct + e.MeanPct) / 2.0, (w.MeanMaxPct + e.MeanMaxPct) / 2.0, (w.MeanP90Pct + e.MeanP90Pct) / 2.0));
            }

            TestContext.WriteLine(sb.ToString());
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "HazardPressure", "proposed-element-portal.txt"), sb.ToString());
        }

        [TestMethod]
        public void Candidates_Tables()
        {
            RequireTables();

            var rain = HazardPressureHarness.Overlay(HazardPressureHarness.Rain, RainCandidate);
            var rainRows = Grid(Of(HazardPressureHarness.Rain, rain));
            Report("rain-candidate", "RAIN candidate [" + rain + "]", rainRows);
            AssertSane(rainRows, kiterBelowStationary: true);

            // As written the wisp candidate is REFUSED by WispSquadMechanic.Validate (follow_lag 1.5 is under its fair-warning
            // floor 0.5 + (pillar_radius + player_radius) / escape_speed = 1.667 s), so a World Event could never start it.
            // Pin that, then measure the nearest row that does start.
            var wisps = HazardPressureHarness.Overlay(HazardPressureHarness.Wisps, WispCandidate);
            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => HazardPressureHarness.Run(Of(HazardPressureHarness.Wisps, wisps, seconds: 1)));
            StringAssert.Contains(refused.Message, "follow_lag");
            TestContext.WriteLine("WISPS candidate [" + wisps + "] refused: " + refused.Message);

            var wispsLag = HazardPressureHarness.Overlay(HazardPressureHarness.Wisps, WispCandidate, "follow_lag=1.7");
            var lagRows = Grid(Of(HazardPressureHarness.Wisps, wispsLag));
            Report("wisps-candidate-lag17", "WISPS candidate, follow_lag raised to 1.7 [" + wispsLag + "] (follow_target=random not on master: the follower trails the first player)", lagRows);
            AssertSane(lagRows, kiterBelowStationary: false);

            var balls = HazardPressureHarness.Overlay(HazardPressureHarness.Balls, BallCandidate);
            BallTables("balls-candidate", balls, "BALLS candidate [" + balls + "]");
        }
    }
}
