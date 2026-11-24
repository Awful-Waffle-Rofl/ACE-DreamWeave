using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Place's up-front course check (JumpEncounterType.ProbePlacement, faked here) and its bounded seed retry: an
    /// obstructed pad or goal on every layout refuses the point with the obstructed reason and creates no Trialmaster; a
    /// probe accepting everything places on the request seed and probes exactly Form's positions; a layout refused on the
    /// request seed and accepted on the next one places, and Form then builds THAT layout. Every seam and the fall-zone
    /// list are restored in a finally. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class JumpPlaceProbeTests
    {
        private const int PickSeed = 7;
        private static uint nextGuid = 0x7F0E5001;

        private sealed class Seams : IDisposable
        {
            private readonly Func<uint, WorldObject> create = JumpEncounterType.CreateObject;
            private readonly Func<ThreadEncounterPlaceRequest, Func<float, float, float?>> ground = JumpEncounterType.GroundFor;
            private readonly Func<WorldObject, bool> enter = JumpEncounterType.EnterWorld;
            private readonly Action<WorldObject> destroy = JumpEncounterType.DestroyObject;
            private readonly Func<ThreadDungeonRun, List<Player>> members = JumpEncounterType.MembersInside;
            private readonly Func<uint, Position, bool, string> probe = JumpEncounterType.ProbePlacement;
            private readonly JumpEncounterType.GenerateFn generate = JumpEncounterType.GenerateLayout;

            public List<WorldObject> Created { get; } = new List<WorldObject>();
            public List<(uint Wcid, Position At, bool Exact)> Probed { get; } = new List<(uint, Position, bool)>();

            /// <summary>Decides each probe by its 1-based call number; clear by default.</summary>
            public Func<int, uint, bool, string> Verdict { get; set; } = (n, wcid, exact) => null;

            public Seams()
            {
                JumpFallGuard.Clear();
                JumpEncounterType.CreateObject = wcid =>
                {
                    var wo = new GenericObject(new Weenie
                    {
                        WeenieClassId = wcid,
                        WeenieType = WeenieType.Generic,
                        PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, $"fake {wcid}" } },
                    }, new ObjectGuid(nextGuid++));
                    Created.Add(wo);
                    return wo;
                };
                JumpEncounterType.GroundFor = _ => (x, y) => 40f;
                JumpEncounterType.EnterWorld = _ => true;
                JumpEncounterType.DestroyObject = _ => { };
                JumpEncounterType.MembersInside = _ => new List<Player>();
                JumpEncounterType.ProbePlacement = (wcid, at, exact) =>
                {
                    Probed.Add((wcid, new Position(at), exact));
                    return Verdict(Probed.Count, wcid, exact);
                };
            }

            public void Dispose()
            {
                JumpEncounterType.CreateObject = create;
                JumpEncounterType.GroundFor = ground;
                JumpEncounterType.EnterWorld = enter;
                JumpEncounterType.DestroyObject = destroy;
                JumpEncounterType.MembersInside = members;
                JumpEncounterType.ProbePlacement = probe;
                JumpEncounterType.GenerateLayout = generate;
                JumpFallGuard.Clear();
            }
        }

        private static JsonElement EmptyConfig()
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }

        private static (ThreadDungeonRun Run, ThreadEncounterPick Pick, DungeonSpawnPointDef Point) Setup()
        {
            var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, 40f, kind: "outdoor");
            var run = OutdoorRun(new[] { point }, max: 1);
            var def = Type("jump", kind: JumpEncounterType.KindName);
            def.Config = EmptyConfig();
            return (run, new ThreadEncounterPick(1, def, Candidate(point, outdoor: true), PickSeed), point);
        }

        /// <summary>The layout the generator makes for <paramref name="seed"/> on the flat 40 m test ground.</summary>
        private static JumpLayout LayoutFor(ThreadDungeonRun run, DungeonSpawnPointDef point, ThreadEncounterPick pick, int seed)
        {
            var instance = run.NewEncounterInstance(pick, new JumpEncounterType());
            var request = new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext);
            var layout = JumpCourse.Generate(new JumpConfig(), new Random(seed), new Vector3(point.X, point.Y, point.Z), (x, y) => 40f, JumpEncounterType.AllowedFor(request), out var why);
            Assert.IsNotNull(layout, why);
            return layout;
        }

        [TestMethod]
        public void A_pad_obstructed_on_every_layout_refuses_the_point_and_creates_no_Trialmaster()
        {
            using var seams = new Seams();
            var config = new JumpConfig();
            const int blockedPad = 3;
            var padInLayout = 0;

            seams.Verdict = (n, wcid, exact) =>
            {
                if (wcid != config.PlatformWcid)
                    return null;

                if (++padInLayout < blockedPad)
                    return null;

                padInLayout = 0;
                return "collides with the environment";
            };

            var (run, pick, _) = Setup();
            var placed = ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType());

            Assert.IsNull(placed);
            Assert.AreEqual(0, run.PlacedEncounters.Count);
            Assert.IsFalse(seams.Created.Any(wo => wo.WeenieClassId == config.NpcWcid), "no Trialmaster was created");
            Assert.AreEqual(0, seams.Created.Count, "nothing at all was created by Place itself");
            Assert.AreEqual(JumpEncounterType.LayoutAttempts * blockedPad, seams.Probed.Count, "every layout probed up to its blocked pad, then the next layout");

            var line = run.EncounterOutcomes.SingleOrDefault(l => l.Contains("the course is obstructed after"));
            Assert.IsNotNull(line, string.Join("\n", run.EncounterOutcomes));
            StringAssert.Contains(line, $"the course is obstructed after {JumpEncounterType.LayoutAttempts} layouts: pad {blockedPad} at 0x5456");
            StringAssert.Contains(line, "(collides with the environment)");
        }

        [TestMethod]
        public void A_goal_obstructed_on_every_layout_refuses_the_point_and_creates_no_Trialmaster()
        {
            using var seams = new Seams();
            var config = new JumpConfig();
            seams.Verdict = (n, wcid, exact) => wcid == config.GoalWcid ? "collides" : null;

            var (run, pick, _) = Setup();
            Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType()));

            Assert.AreEqual(0, seams.Created.Count, "no Trialmaster was created");
            Assert.AreEqual(JumpEncounterType.LayoutAttempts * (config.Platforms + 1), seams.Probed.Count);
            Assert.IsTrue(seams.Probed.Where(p => p.Wcid == config.GoalWcid).All(p => !p.Exact), "the goal is held to TryEnter only, as Form holds it");
            Assert.IsTrue(seams.Probed.Where(p => p.Wcid == config.PlatformWcid).All(p => p.Exact), "a pad must also land within the tolerance");
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains($"the course is obstructed after {JumpEncounterType.LayoutAttempts} layouts: the goal at 0x5456")),
                string.Join("\n", run.EncounterOutcomes));
        }

        [TestMethod]
        public void A_clear_course_places_on_the_request_seed_and_probes_Forms_exact_positions()
        {
            using var seams = new Seams();
            var config = new JumpConfig();
            var (run, pick, point) = Setup();

            var placed = ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType());

            Assert.IsNotNull(placed, string.Join("\n", run.EncounterOutcomes));
            var state = (JumpState)placed.State;
            Assert.AreEqual(PickSeed, state.LayoutSeed, "the request seed's layout was accepted");
            Assert.AreEqual(1, seams.Created.Count(wo => wo.WeenieClassId == config.NpcWcid), "one Trialmaster");
            Assert.IsFalse(run.EncounterOutcomes.Any(l => l.Contains("course layout")), "no retry note on the first layout");

            var layout = LayoutFor(run, point, pick, PickSeed);
            var expected = layout.Pads.Select(p => new Vector3(p.X, p.Y, p.Bottom)).Concat(new[] { layout.Goal }).ToList();

            Assert.AreEqual(expected.Count, seams.Probed.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual(expected[i], seams.Probed[i].At.Pos, $"probe {i + 1} is at Form's position");
                Assert.AreEqual(run.Instance, seams.Probed[i].At.Instance, "in the run copy");
                Assert.AreEqual((ushort)0x5456, seams.Probed[i].At.LandblockId.Landblock);
            }
        }

        [TestMethod]
        public void A_layout_refused_on_the_request_seed_and_clear_on_the_next_places_and_Form_builds_the_next_one()
        {
            using var seams = new Seams();
            var config = new JumpConfig();

            // Layout 1 fails on its first pad; every later probe is clear, so layout 2 (the first derived seed) is accepted.
            seams.Verdict = (n, wcid, exact) => n == 1 ? "collides with the environment" : null;

            var (run, pick, point) = Setup();
            var placed = ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType());

            Assert.IsNotNull(placed, string.Join("\n", run.EncounterOutcomes));
            var state = (JumpState)placed.State;
            var seed1 = JumpEncounterType.LayoutSeedFor(PickSeed, 1);
            Assert.AreEqual(seed1, state.LayoutSeed);
            Assert.AreNotEqual(PickSeed, seed1);
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains($"course layout 2 of {JumpEncounterType.LayoutAttempts} accepted (seed {seed1})")),
                string.Join("\n", run.EncounterOutcomes));

            var accepted = LayoutFor(run, point, pick, seed1);
            var original = LayoutFor(run, point, pick, PickSeed);
            var acceptedPads = accepted.Pads.Select(p => new Vector3(p.X, p.Y, p.Bottom)).ToList();
            Assert.IsFalse(acceptedPads.SequenceEqual(original.Pads.Select(p => new Vector3(p.X, p.Y, p.Bottom))), "the two seeds make different layouts, so the check below discriminates");

            // The Use: Form regenerates from the stored seed. The NPC is detached so Form schedules nothing (no landblock here).
            state.Npc = null;
            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var gen));
            var before = seams.Created.Count;

            JumpEncounterType.Form(placed, state, gen, null);

            Assert.AreEqual(JumpPhase.Running, state.Phase, string.Join("\n", run.EncounterOutcomes));
            var pads = seams.Created.Skip(before).Take(config.Platforms).ToList();
            CollectionAssert.AreEqual(acceptedPads, pads.Select(p => p.Location.Pos).ToList(), "Form built the accepted layout, not the request seed's");
            Assert.AreEqual(accepted.Goal, seams.Created.Skip(before).Last().Location.Pos, "and its goal");
        }

        [TestMethod]
        public void No_room_on_three_layouts_in_a_row_stops_early_with_the_no_room_reason()
        {
            using var seams = new Seams();
            var groundCalls = 0;
            JumpEncounterType.GroundFor = _ => (x, y) => { groundCalls++; return null; };

            var (run, pick, _) = Setup();
            Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType()));

            Assert.AreEqual(6, JumpEncounterType.NoRoomStopAfter);
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains($"no room for the course after {JumpEncounterType.NoRoomStopAfter} layouts: ")),
                string.Join("\n", run.EncounterOutcomes));
            Assert.AreEqual(0, seams.Probed.Count, "nothing to probe");
            Assert.AreEqual(0, seams.Created.Count, "no Trialmaster");
            Assert.IsTrue(groundCalls > 0, "control: the generator did consult the ground");
        }

        [TestMethod]
        public void An_obstruction_then_no_room_reports_no_clear_layout_and_the_obstruction_does_not_count_toward_the_stop()
        {
            using var seams = new Seams();
            var probed = false;

            // Layout 1 generates (the ground is there) and its first pad is blocked; from then on the ground is gone.
            JumpEncounterType.GroundFor = _ => (x, y) => probed ? (float?)null : 40f;
            seams.Verdict = (n, wcid, exact) => { probed = true; return "collides with the environment"; };

            var (run, pick, _) = Setup();
            Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType()));

            Assert.AreEqual(1, seams.Probed.Count, "only layout 1 reached the probe");
            Assert.AreEqual(0, seams.Created.Count, "no Trialmaster");

            // 1 obstructed + 3 no-room in a row: the stop comes after 4 layouts, not 3.
            var line = run.EncounterOutcomes.SingleOrDefault(l => l.Contains("no clear layout after"));
            Assert.IsNotNull(line, string.Join("\n", run.EncounterOutcomes));
            StringAssert.Contains(line, $"no clear layout after {1 + JumpEncounterType.NoRoomStopAfter} layouts: ");
            Assert.IsFalse(line.Contains("pad 1 at"), "the LAST refusal (no room) is reported, not the first");
        }

        [TestMethod]
        public void An_obstruction_resets_the_no_room_run_so_the_search_does_not_stop_early()
        {
            using var seams = new Seams();
            seams.Verdict = (n, wcid, exact) => "collides with the environment";

            // Attempts 1-5 no room, attempt 6 generates (and is obstructed), attempts 7+ no room. With the reset the run
            // after the obstruction needs NoRoomStopAfter (6) more no-room layouts: the stop comes at attempt 12. Without the
            // reset it would come at attempt 7 (5 + 1).
            var generateCalls = 0;
            JumpEncounterType.GenerateLayout = (JumpConfig c, Random rng, Vector3 npc, Func<float, float, float?> ground, Func<float, float, bool> allowed, out string why) =>
            {
                generateCalls++;

                if (generateCalls == 6)
                    return JumpCourse.Generate(c, rng, npc, (x, y) => 40f, allowed, out why);

                why = "scripted no room";
                return null;
            };

            var (run, pick, _) = Setup();
            Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, pick, new JumpEncounterType()));

            Assert.IsTrue(seams.Probed.Count >= 1, "control: attempt 6 generated and reached the probe");
            Assert.IsTrue(generateCalls >= 11, $"the search reached attempt 11 or later, not an early stop (it stopped at attempt {generateCalls})");
            Assert.AreEqual(5 + 1 + JumpEncounterType.NoRoomStopAfter, generateCalls, "it stops exactly NoRoomStopAfter no-room layouts after the obstruction");
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains($"no clear layout after {generateCalls} layouts: scripted no room")), string.Join("\n", run.EncounterOutcomes));
        }

        [TestMethod]
        public void The_refusal_kind_names_what_every_refused_layout_had_in_common()
        {
            Assert.AreEqual("the course is obstructed", JumpEncounterType.RefusalKind(sawObstruction: true, sawNoRoom: false));
            Assert.AreEqual("no room for the course", JumpEncounterType.RefusalKind(sawObstruction: false, sawNoRoom: true));
            Assert.AreEqual("no clear layout", JumpEncounterType.RefusalKind(sawObstruction: true, sawNoRoom: true));
        }

        [TestMethod]
        public void The_derived_seeds_are_distinct_and_deterministic()
        {
            var seeds = Enumerable.Range(0, JumpEncounterType.LayoutAttempts).Select(k => JumpEncounterType.LayoutSeedFor(434432745, k)).ToList();

            Assert.AreEqual(434432745, seeds[0], "attempt 0 is the request seed");
            Assert.AreEqual(seeds.Count, seeds.Distinct().Count());
            CollectionAssert.AreEqual(seeds, Enumerable.Range(0, JumpEncounterType.LayoutAttempts).Select(k => JumpEncounterType.LayoutSeedFor(434432745, k)).ToList());
            Assert.AreEqual(1, JumpEncounterType.LayoutSeedStride & 1, "the stride is odd");
            Assert.AreEqual(16, JumpEncounterType.LayoutAttempts, "the request seed plus up to 15 derived seeds");
        }

        [TestMethod]
        public void A_state_built_without_Place_uses_the_request_seed()
        {
            var (run, pick, _) = Setup();
            var instance = run.NewEncounterInstance(pick, new JumpEncounterType());
            var state = new JumpState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new JumpConfig());

            Assert.AreEqual(PickSeed, state.LayoutSeed);
            Assert.AreEqual(0, new JumpState(null, new JumpConfig()).LayoutSeed);
        }
    }
}
