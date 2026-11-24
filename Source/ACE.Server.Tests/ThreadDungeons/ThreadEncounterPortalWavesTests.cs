using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using log4net;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The shared portal-wave machinery's pure contract (ThreadEncounterPortalWaves, extracted from Defense): portal pick
    /// and refusal, the reach prefilter, the stream plan, the outcome table and the wake classifier, driven through a
    /// config that is NOT Defense's, so a second wave encounter can rely on it. Defense's own forwarders are checked to give
    /// the same answers. The world-touching members (layer placement, enemy spawn, engage) need a loaded landblock; their
    /// threading invariants are pinned by source scan. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadEncounterPortalWavesTests
    {
        /// <summary>A wave config that is not Defense's: the contract a second consumer builds on.</summary>
        private sealed class WaveConfig : IPortalWaveConfig
        {
            public int PortalsMin { get; init; } = 1;
            public int PortalsMax { get; init; } = 2;
            public int PerPortalMin { get; init; } = 2;
            public int PerPortalMax { get; init; } = 4;
            public double StaggerMin { get; init; } = 1.0;
            public double StaggerMax { get; init; } = 3.0;
            public float PortalMinDistance { get; init; } = 5f;
            public float PortalMaxDistance { get; init; } = 20f;
            public double ScaleDungeon { get; init; } = 0.25;
            public double ScaleOutdoor { get; init; } = 0.5;
            public double PortalScale(bool isOutdoor) => isOutdoor ? ScaleOutdoor : ScaleDungeon;
        }

        private static readonly Vector3 Anchor = new Vector3(50f, 50f, 0f);

        private static PortalWaveCandidate At(float x, float y, float? walk = null, uint? cell = null)
            => new PortalWaveCandidate(Point(cell ?? DungeonCell(), x, y), new Vector3(x, y, 0f), walk);

        private static List<DungeonSpawnPlanEntry> Plan(params (uint Wcid, DungeonRole Role)[] entries)
            => entries.Select(e => new DungeonSpawnPlanEntry(e.Wcid, e.Role, Point(DungeonCell(), 0, 0), upliftLevel: 0, stampLevel: 66)).ToList();

        /// <summary>A planned spawn by value (each draw builds a fresh entry, so the record's own equality is by reference).</summary>
        private static (int Portal, double Delay, uint Wcid, DungeonRole Role, DungeonSpawnPointDef Point) Shape(PortalWaveSpawn s)
            => (s.Portal, s.Delay, s.Entry.Wcid, s.Entry.Role, s.Entry.Point);

        private static ThreadEncounterPortalWaves Waves(string label)
            => new ThreadEncounterPortalWaves(LogManager.GetLogger(typeof(ThreadEncounterPortalWavesTests)), label, _ => null);

        // ---- portal pick ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void PickPortals_reads_any_wave_config_and_names_the_anchor_in_its_refusal()
        {
            var config = new WaveConfig { PortalsMin = 2, PortalsMax = 2 };
            var inBand = At(60f, 50f);                 // 10 m
            var tooNear = At(52f, 50f);                // 2 m
            var tooFar = At(75f, 50f);                 // 25 m
            var behindWall = At(50f, 62f, walk: 30f);  // 12 m line, 30 m walk
            var walkOk = At(40f, 50f, walk: 14f);      // 10 m line, 14 m walk

            var picked = ThreadEncounterPortalWaves.PickPortals(Anchor, DungeonLb, new[] { inBand, tooNear, tooFar, behindWall, walkOk },
                false, config, new Random(1), "chest", out var refusal);

            Assert.IsNull(refusal);
            CollectionAssert.AreEquivalent(new[] { inBand.Point, walkOk.Point }, picked);

            Assert.IsNull(ThreadEncounterPortalWaves.PickPortals(Anchor, DungeonLb, new[] { inBand }, false, config, new Random(1), "chest", out refusal));
            Assert.AreEqual("1 portal point(s) between 5 and 20 m on the chest's landblock; 2 needed", refusal);

            Assert.IsNull(ThreadEncounterPortalWaves.PickPortals(Anchor, DungeonLb, new[] { inBand }, false, null, new Random(1), "chest", out refusal));
            Assert.AreEqual("no config", refusal);
        }

        [TestMethod]
        public void Defense_PickPortals_is_the_shared_pick_with_its_refusal_text_unchanged()
        {
            var ring = Enumerable.Range(0, 8).Select(i =>
            {
                var a = i * Math.PI / 4;
                return At(50f + 15f * (float)Math.Sin(a), 50f + 15f * (float)Math.Cos(a));
            }).ToList();

            var config = new DefenseConfig { PortalsMin = 1, PortalsMax = 3 };

            // Golden picks (ring indices, in pick order), captured by running origin/master's own DefenseEncounterType
            // PickPortals text (b349b36ed, compiled as a throwaway probe) on this ring. Any change to the draw order fails here.
            var golden = new Dictionary<int, int[]>
            {
                [1] = new[] { 0 },
                [2] = new[] { 3, 7, 1 },
                [3] = new[] { 5 },
            };

            foreach (var (seed, expected) in golden)
            {
                var shared = ThreadEncounterPortalWaves.PickPortals(Anchor, DungeonLb, ring, false, config, new Random(seed), "pillar", out _);
                var defense = DefenseEncounterType.PickPortals(Anchor, DungeonLb, ring, false, config, new Random(seed), out _);

                CollectionAssert.AreEqual(expected, shared.Select(p => ring.FindIndex(c => ReferenceEquals(c.Point, p))).ToArray(), $"seed {seed}: shared");
                CollectionAssert.AreEqual(expected, defense.Select(p => ring.FindIndex(c => ReferenceEquals(c.Point, p))).ToArray(), $"seed {seed}: Defense");
            }

            // The pre-extraction refusal, byte for byte.
            Assert.IsNull(DefenseEncounterType.PickPortals(Anchor, DungeonLb, new[] { At(60f, 50f) }, false, new DefenseConfig { PortalsMin = 2, PortalsMax = 3 }, new Random(1), out var refusal));
            Assert.AreEqual("1 portal point(s) between 8 and 25 m on the pillar's landblock; 2 needed", refusal);
        }

        // ---- reach prefilter -----------------------------------------------------------------------------------------

        [TestMethod]
        public void RejectPointAmong_prefixes_the_callers_label_and_passes_with_enough_points()
        {
            var anchor = Candidate(Point(DungeonCell(), 50f, 50f));
            var near = Candidate(Point(DungeonCell(), 60f, 50f));         // 10 m
            var far = Candidate(Point(DungeonCell(), 90f, 50f));          // 40 m
            var points = new[] { anchor, near, far };
            var config = new WaveConfig { PortalsMin = 2 };

            Assert.AreEqual(1, ThreadEncounterPortalWaves.PortalPointsInReach(anchor, points, false, config), "the anchor itself and the far point never count");
            Assert.AreEqual("chest: fewer than 2 portal point(s) 5 to 20 m away", Waves("chest").RejectPointAmong(anchor, points, false, config));
            Assert.IsNull(Waves("chest").RejectPointAmong(anchor, points, false, new WaveConfig { PortalsMin = 1 }));
            Assert.IsNull(Waves("chest").RejectPointAmong(null, points, false, config), "no candidate, nothing to reject");
            Assert.IsNull(Waves("chest").RejectPointAmong(anchor, points, false, null), "no config: no throw, the caller owns that refusal");
        }

        // ---- stream --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void PlanStream_follows_the_wave_config_and_matches_Defense_for_the_same_seed()
        {
            var portals = new[] { Point(DungeonCell(), 60, 50), Point(DungeonCell(), 40, 50) };
            var plan = Plan((100, DungeonRole.Trash), (200, DungeonRole.Elite), (101, DungeonRole.Trash));
            var config = new WaveConfig();
            var counts = new HashSet<int>();

            for (var seed = 0; seed < 200; seed++)
            {
                var stream = ThreadEncounterPortalWaves.PlanStream(portals, plan, config, new Random(seed));

                for (var i = 1; i < stream.Count; i++)
                    Assert.IsTrue(stream[i].Delay >= stream[i - 1].Delay, $"seed {seed}: in delay order");

                for (var p = 0; p < portals.Length; p++)
                {
                    var mine = stream.Where(s => s.Portal == p).ToList();
                    counts.Add(mine.Count);

                    Assert.IsTrue(mine.Count >= 2 && mine.Count <= 4, $"seed {seed} portal {p}: {mine.Count}");
                    Assert.AreEqual(ThreadEncounterPortalWaves.FirstSpawnDelaySeconds, mine[0].Delay, 1e-12);

                    for (var k = 1; k < mine.Count; k++)
                    {
                        var gap = mine[k].Delay - mine[k - 1].Delay;
                        Assert.IsTrue(gap >= 1.0 - 1e-9 && gap <= 3.0 + 1e-9, $"seed {seed} portal {p}: stagger {gap}");
                    }

                    Assert.IsTrue(mine.All(s => ReferenceEquals(s.Entry.Point, portals[p])), "each stands on its own portal's point");
                }

                Assert.IsTrue(stream.All(s => s.Entry.Role == DungeonRole.Trash), "trash only");
            }

            CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, counts.ToList(), "every count in range is reachable");
            Assert.AreEqual(0, ThreadEncounterPortalWaves.PlanStream(portals, Plan((200, DungeonRole.Elite)), config, new Random(1)).Count, "no trash, no stream");
            Assert.AreEqual(0, ThreadEncounterPortalWaves.PlanStream(null, plan, config, new Random(1)).Count);

            Assert.AreEqual(ThreadEncounterPortalWaves.FirstSpawnDelaySeconds, DefenseEncounterType.FirstSpawnDelaySeconds);
            Assert.AreEqual(ThreadEncounterPortalWaves.WalkRatio, DefenseEncounterType.WalkRatio);
        }

        [TestMethod]
        public void PlanStream_draws_exactly_the_pre_extraction_Defense_stream()
        {
            var portals = new[] { Point(DungeonCell(), 60, 50), Point(DungeonCell(), 40, 50) };
            var plan = Plan((100, DungeonRole.Trash), (200, DungeonRole.Elite), (101, DungeonRole.Trash));

            // Golden streams (portal, delay, wcid), captured by running origin/master's own DefenseEncounterType PlanStream
            // text (b349b36ed, compiled as a throwaway probe) with the default DefenseConfig. Any change to the rng draw order
            // (count, stagger, trash draw) moves a delay or a wcid here.
            var golden = new Dictionary<int, (int Portal, double Delay, uint Wcid)[]>
            {
                [1] = new[]
                {
                    (0, 2.0, 100u), (1, 2.0, 101u), (1, 4.2025329071108875, 101u), (0, 4.9340213597444915, 101u),
                    (1, 6.25980653253375, 100u), (0, 8.249059147317455, 100u), (1, 8.900026795873432, 101u),
                },
                [2] = new[]
                {
                    (0, 2.0, 100u), (1, 2.0, 100u), (1, 4.015122381046005, 101u), (0, 4.331997340699656, 101u),
                    (0, 6.550006607803519, 100u), (1, 6.779312350218795, 100u), (0, 10.154287530181133, 100u),
                    (1, 10.185488938440331, 100u), (0, 12.604253838120147, 100u), (1, 13.648132782265606, 100u),
                },
            };

            foreach (var (seed, expected) in golden)
            {
                foreach (var (who, stream) in new[]
                {
                    ("shared", ThreadEncounterPortalWaves.PlanStream(portals, plan, new DefenseConfig(), new Random(seed))),
                    ("Defense", DefenseEncounterType.PlanStream(portals, plan, new DefenseConfig(), new Random(seed))),
                })
                {
                    Assert.AreEqual(expected.Length, stream.Count, $"seed {seed} {who}: count");

                    for (var i = 0; i < expected.Length; i++)
                    {
                        var s = Shape(stream[i]);
                        Assert.AreEqual(expected[i].Portal, s.Portal, $"seed {seed} {who} [{i}]: portal");
                        Assert.AreEqual(expected[i].Delay, s.Delay, 1e-9, $"seed {seed} {who} [{i}]: delay");
                        Assert.AreEqual(expected[i].Wcid, s.Wcid, $"seed {seed} {who} [{i}]: wcid");
                        Assert.AreEqual(DungeonRole.Trash, s.Role);
                        Assert.AreSame(portals[expected[i].Portal], s.Point, $"seed {seed} {who} [{i}]: stands on its portal");
                    }
                }
            }
        }

        // ---- outcome -------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Decide_is_the_wave_outcome_table_and_Defense_maps_it_one_to_one()
        {
            Assert.AreEqual(PortalWaveOutcome.AnchorLost, ThreadEncounterPortalWaves.Decide(false, true, 0, 0, 300), "the anchor down wins over everything");
            Assert.AreEqual(PortalWaveOutcome.Cleared, ThreadEncounterPortalWaves.Decide(true, true, 0, 10, 300));
            Assert.AreEqual(PortalWaveOutcome.None, ThreadEncounterPortalWaves.Decide(true, false, 0, 10, 300), "stream still coming");
            Assert.AreEqual(PortalWaveOutcome.None, ThreadEncounterPortalWaves.Decide(true, true, 2, 10, 300), "enemies alive");
            Assert.AreEqual(PortalWaveOutcome.TimedOut, ThreadEncounterPortalWaves.Decide(true, true, 2, 300, 300));
            Assert.AreEqual(PortalWaveOutcome.Cleared, ThreadEncounterPortalWaves.Decide(true, true, 0, 300, 300), "cleared beats timed out");

            // An anchorless caller passes the anchor as standing: it can only clear, time out, or keep going.
            Assert.AreEqual(PortalWaveOutcome.Cleared, ThreadEncounterPortalWaves.Decide(true, true, -1, 0, 300));

            Assert.AreEqual(DefenseOutcome.Failure, DefenseEncounterType.Decide(false, true, 0, 0, 300));
            Assert.AreEqual(DefenseOutcome.Success, DefenseEncounterType.Decide(true, true, 0, 10, 300));
            Assert.AreEqual(DefenseOutcome.Timeout, DefenseEncounterType.Decide(true, true, 2, 300, 300));
            Assert.AreEqual(DefenseOutcome.None, DefenseEncounterType.Decide(true, false, 2, 10, 300));
        }

        // ---- wake ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ClassifyWake_and_IsStanding_hold_for_a_caller_without_a_pillar()
        {
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.Woke, ThreadEncounterPortalWaves.ClassifyWake(0, true));
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.NoMember, ThreadEncounterPortalWaves.ClassifyWake(0, false));
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.MemberUntargetable, ThreadEncounterPortalWaves.ClassifyWake(3, false));
            Assert.IsFalse(ThreadEncounterPortalWaves.IsStanding(null));

            // A null creature is not engaged and wakes nothing.
            Assert.IsFalse(ThreadEncounterPortalWaves.EngageEnemy(null, null, out var wake));
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.MemberUntargetable, wake, "a guard miss never reads as NoMember");
        }

        // ---- threading invariants (source) ---------------------------------------------------------------------------

        [TestMethod]
        public void Every_object_is_refused_off_the_queues_landblock_and_Tracked_before_EnterWorld()
        {
            var src = WorldEventHooksCallSiteTests.StripComments(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPortalWaves.cs"));

            var layer = PooledLootSourceText.MethodBody(src, "public bool TryPlacePortalPart(ThreadEncounterInstance instance, ThreadEncounterPlaceRequest request, Position centre,");
            AssertInOrder(layer, "if (request.Landblock != null && position.LandblockId.Landblock != request.Landblock.Id.Landblock)", "createObject(part.Wcid)", "instance.TrackDecor(wo);", "entered = wo.EnterWorld();", "onPlaced?.Invoke(wo);");

            var enemy = PooledLootSourceText.MethodBody(src, "public Creature TrySpawnEnemy(ThreadEncounterPlaceRequest request, DungeonSpawnPlanEntry entry, bool excludedFromClear)");
            AssertInOrder(enemy, "if (request.Landblock != null && creature.Location.LandblockId.Landblock != request.Landblock.Id.Landblock)", "request.ShapeCreature(creature, entry,", "request.Instance.Track(creature);", "entered = creature.EnterWorld();");

            Assert.IsFalse(src.Contains("PropertyManager"), "the caller stamps every tunable; nothing here reads a property");
            Assert.IsFalse(src.Contains("ActionChain"), "nothing here schedules: the caller owns its queues");
        }

        private static void AssertInOrder(string body, params string[] fragments)
        {
            var at = -1;

            foreach (var f in fragments)
            {
                var next = body.IndexOf(f, at + 1, StringComparison.Ordinal);
                Assert.IsTrue(next > at, $"'{f}' missing or out of order");
                at = next;
            }
        }
    }
}
