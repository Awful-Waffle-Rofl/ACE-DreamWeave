using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Craftwork Golem encounter type: its config, the round draw, the flat-ground ring rule, the locked lifecycle
    /// (GolemState: begin, open, pick, awaken, death), the stacking maths, the health rule, the shipped encounters.json
    /// row, and source-scan pins for the registration, the tunable wiring and the kill hook. Player-free: the state is
    /// driven with guids. Placement into a live world (EnterWorld, the wisps, the swap, the aura) needs a loaded landblock
    /// and is left to the in-game checks. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class GolemEncounterTypeTests
    {
        private const uint WispA = 0x80001001u, WispB = 0x80001002u, WispC = 0x80001003u;

        private static JsonElement Json(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        private static GolemState NewState(int rounds = 3) => new GolemState(null, new GolemConfig { Rounds = rounds }, null);

        /// <summary>A state shaping round 0 with Might, Frenzy, Vigor on three wisps; returns the round's generation.</summary>
        private static (GolemState State, int Gen) Shaping(int rounds = 3)
        {
            var state = NewState(rounds);
            Assert.AreEqual(GolemUseOutcome.Begun, state.TryBegin(false, true));
            var gen = state.OpenRound(0, new[] { GolemBuff.Might, GolemBuff.Frenzy, GolemBuff.Vigor }, new[] { WispA, WispB, WispC });
            Assert.AreNotEqual(0, gen);
            return (state, gen);
        }

        private static int[] Counts(params (GolemBuff Buff, int N)[] picks)
        {
            var counts = new int[GolemEncounterType.BankSize];

            foreach (var (buff, n) in picks)
                counts[(int)buff] = n;

            return counts;
        }

        // ---- config ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void An_empty_or_absent_config_parses_to_the_shipped_defaults()
        {
            foreach (var element in new[] { Json("{}"), default(JsonElement) })
            {
                var problems = new List<string>();
                var config = GolemEncounterType.ParseConfig(element, problems, out var error);

                Assert.IsNull(error);
                Assert.AreEqual(0, problems.Count);
                Assert.AreEqual(1007970u, config.DormantWcid);
                Assert.AreEqual(1007971u, config.GolemWcid);
                CollectionAssert.AreEqual(new uint[] { 1007972, 1007973, 1007974, 1007975, 1007976, 1007977, 1007978, 1007979 }, config.WispWcids.ToArray());
                Assert.AreEqual(3, config.Rounds);
                Assert.AreEqual(30.0, config.RoundSeconds);
                Assert.AreEqual(3.0f, config.RingRadius);
                Assert.AreEqual(0.5f, config.HeightTolerance);
            }
        }

        [TestMethod]
        public void A_bad_config_is_refused_with_a_reason()
        {
            foreach (var (text, fragment) in new[]
            {
                ("[]", "must be an object"),
                ("{\"rounds\": \"three\"}", "config.rounds must be a whole number"),
                ("{\"rounds\": 2.5}", "config.rounds must be a whole number"),
                ("{\"roundSeconds\": \"soon\"}", "config.roundSeconds must be a number"),
                ("{\"roundSeconds\": null}", "config.roundSeconds must be a number"),
                ("{\"heightTolerance\": true}", "config.heightTolerance must be a number"),
                ("{\"ringRadius\": []}", "config.ringRadius must be a number"),
                ("{\"golemWcid\": 0}", "golemWcid"),
                ("{\"wispWcids\": [1, 2, 3]}", "wispWcids must be 8"),
                ("{\"wispWcids\": [1, 2, 3, 4, 5, 6, 7, 0]}", "wispWcids must be 8"),
            })
            {
                var config = GolemEncounterType.ParseConfig(Json(text), null, out var error);
                Assert.IsNull(config, text);
                StringAssert.Contains(error, fragment, text);
            }
        }

        [TestMethod]
        public void An_out_of_range_number_is_clamped_and_noted()
        {
            var problems = new List<string>();
            var config = GolemEncounterType.ParseConfig(Json("{\"rounds\": 9, \"roundSeconds\": 1, \"heightTolerance\": 7, \"ringRadius\": 0.5}"), problems, out var error);

            Assert.IsNull(error);
            Assert.AreEqual(6, config.Rounds);
            Assert.AreEqual(5.0, config.RoundSeconds);
            Assert.AreEqual(3.0f, config.HeightTolerance);
            Assert.AreEqual(1.85f, config.RingRadius, "the floor keeps adjacent ring slots MinWispSpacing apart");
            Assert.AreEqual(4, problems.Count, string.Join("\n", problems));

            var low = GolemEncounterType.ParseConfig(Json("{\"rounds\": 0, \"roundSeconds\": 999, \"heightTolerance\": 0}"), null, out _);
            Assert.AreEqual((1, 300.0, 0.1f), (low.Rounds, low.RoundSeconds, low.HeightTolerance));
        }

        [TestMethod]
        public void Bad_rounds_or_round_seconds_refuse_the_placement_before_anything_is_created()
        {
            var savedCreate = GolemEncounterType.CreateObject;
            var savedGround = GolemEncounterType.GroundFor;

            try
            {
                var created = 0;
                GolemEncounterType.CreateObject = _ => { created++; return null; };
                GolemEncounterType.GroundFor = (r, c) => (new GolemGroundHit(40f, 0), (x, y) => new GolemGroundHit(40f, 0), null);

                var type = new GolemEncounterType();
                using var seams = new Seams(Store(), type);
                var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, 40f, kind: "outdoor");

                foreach (var (config, fragment) in new[]
                {
                    ("{\"rounds\": \"x\"}", "config.rounds must be a whole number"),
                    ("{\"roundSeconds\": \"x\"}", "config.roundSeconds must be a number"),
                })
                {
                    var run = OutdoorRun(new[] { point }, max: 1);
                    var def = Type("golem", kind: GolemEncounterType.KindName);
                    def.Config = Json(config);

                    Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, new ThreadEncounterPick(1, def, Candidate(point, outdoor: true), 7), type), config);
                    Assert.AreEqual(0, run.PlacedEncounters.Count, config);
                    Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains(fragment)), config + ": " + string.Join("\n", run.EncounterOutcomes));
                }

                Assert.AreEqual(0, created, "a refused config never reaches the factory");
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
                GolemEncounterType.GroundFor = savedGround;
            }
        }

        // ---- the flat-ground rule ------------------------------------------------------------------------------------

        [TestMethod]
        public void A_flat_ring_is_accepted_and_every_wisp_takes_its_own_probed_height()
        {
            // Gentle ground: each slot 0.01 m per metre of x off the golem's 40 m, all within 0.5 m.
            var ring = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => new GolemGroundHit(40f + (x - 96f) * 0.1f, 0x5456000Au), out var refusal);

            Assert.IsNull(refusal);
            Assert.AreEqual(9, ring.Count);

            foreach (var slot in ring)
            {
                var dx = slot.X - 96f;
                var dy = slot.Y - 96f;
                Assert.AreEqual(3.0, Math.Sqrt(dx * dx + dy * dy), 1e-4, $"slot {slot.Index} is on the ring");
                Assert.AreEqual(40f + dx * 0.1f, slot.Z, 1e-4, $"slot {slot.Index} sits at its own probe, not the golem's z");
                Assert.AreEqual(0x5456000Au, slot.Cell);
            }

            Assert.AreNotEqual(ring[1].Z, ring[0].Z, "the heights differ, so the per-slot z is real");

            // Bearing 0 is north (+y), 90 east (+x).
            Assert.AreEqual(96f, ring[0].X, 1e-4);
            Assert.AreEqual(99f, ring[0].Y, 1e-4);
        }

        [TestMethod]
        public void A_partly_broken_ring_widens_the_search_instead_of_refusing()
        {
            // One slot (bearing 160) on a ledge 0.6 m up: the eight other ring slots stay, and a spot on the next ring out
            // fills the ninth place (owner ruling 2026-11-11: widen before giving up; the in-between bearings on the same
            // ring sit 1.04 m from a kept slot, inside the spacing).
            var ledgeX = 96f + 3f * (float)Math.Sin(160 * Math.PI / 180);
            var ledge = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f,
                (x, y) => new GolemGroundHit(Math.Abs(x - ledgeX) < 0.01f && y < 96f ? 40.6f : 40f, 0), out var why, out var tol);
            Assert.IsNull(why);
            Assert.AreEqual(0.5f, tol, "no relaxation was needed");
            Assert.AreEqual(9, ledge.Count);
            Assert.IsFalse(ledge.Any(s => s.Index == 4), "the ledge slot (index 4, bearing 160) is not kept");
            Assert.AreEqual(8, ledge.Count(s => s.Index < 9), "the other eight ring slots are kept");

            // A wall to the south (no ground there): the kept spots all lie north of it, and there are still enough.
            var wall = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => y < 95f ? (GolemGroundHit?)null : new GolemGroundHit(40f, 0), out why);
            Assert.IsNull(why);
            Assert.IsTrue(wall.Count >= GolemEncounterType.WispsPerRound);
            Assert.IsTrue(wall.All(s => s.Y >= 95f));

            // Every kept spot is within the config's ring bounds, clear of the golem, and spaced from the others.
            foreach (var ring in new[] { ledge, wall })
            {
                foreach (var s in ring)
                {
                    var r = Math.Sqrt((s.X - 96f) * (s.X - 96f) + (s.Y - 96f) * (s.Y - 96f));
                    Assert.IsTrue(r >= GolemEncounterType.MinWispGolemDistance - 1e-3 && r <= GolemConfig.MaxRingRadius + 1e-3, $"spot {s.Index} at {r:0.00} m");
                }

                for (var i = 0; i < ring.Count; i++)
                    for (var j = i + 1; j < ring.Count; j++)
                        Assert.IsTrue(new System.Numerics.Vector2(ring[i].X - ring[j].X, ring[i].Y - ring[j].Y).Length() >= GolemEncounterType.MinWispSpacing - 1e-3f);
            }
        }

        [TestMethod]
        public void A_hill_relaxes_the_tolerance_stepwise_up_to_one_and_a_half_metres()
        {
            // Every spot 0.8 m above the golem's ground: refused at 0.5 and 0.75, accepted at 1.0 with the full nine.
            var raised = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => new GolemGroundHit(40.8f, 0), out var why, out var tol);
            Assert.IsNull(why);
            Assert.AreEqual(1.0f, tol, 1e-4f);
            Assert.AreEqual(9, raised.Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, 9).ToArray(), raised.Select(s => s.Index).ToArray(), "the nine ring slots");

            // 1.5 m is the cap: 1.5 off is accepted, 1.6 off is refused.
            Assert.IsNotNull(GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => new GolemGroundHit(41.5f, 0), out _, out tol));
            Assert.AreEqual(1.5f, tol, 1e-4f);
            Assert.IsNull(GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => new GolemGroundHit(41.6f, 0), out why));
            StringAssert.Contains(why, "only 0 wisp spot(s) within 1.50 m");

            // A configured tolerance above the cap is kept, never lowered.
            Assert.IsNotNull(GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 2.0f, (x, y) => new GolemGroundHit(41.9f, 0), out _, out tol));
            Assert.AreEqual(2.0f, tol);

            // The tolerance boundary is inclusive, and no ground under the golem refuses.
            Assert.IsNotNull(GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => new GolemGroundHit(40.5f, 0), out _, out tol));
            Assert.AreEqual(0.5f, tol);
            Assert.IsNull(GolemEncounterType.BuildRing(96f, 96f, float.NaN, 3f, 0.5f, (x, y) => new GolemGroundHit(40f, 0), out _));
        }

        [TestMethod]
        public void Fewer_than_two_spots_even_after_relaxing_refuses_the_point()
        {
            // A pillar top: ground only at one spot (the north ring slot), everything else 3 m down.
            var one = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f,
                (x, y) => new GolemGroundHit(Math.Abs(x - 96f) < 0.01f && Math.Abs(y - 99f) < 0.01f ? 40f : 37f, 0), out var why);
            Assert.IsNull(one);
            StringAssert.Contains(why, "only 1 wisp spot(s)");
            StringAssert.Contains(why, "need 2");

            // Two spots far enough apart are enough (rounds of two).
            var two = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f,
                (x, y) => new GolemGroundHit(Math.Abs(x - 96f) < 0.01f && (Math.Abs(y - 99f) < 0.01f || Math.Abs(y - 93f) < 0.01f) ? 40f : 37f, 0), out why, out var tol);
            Assert.IsNull(why);
            Assert.AreEqual(2, two.Count);
            Assert.AreEqual(0.5f, tol, 1e-4f, "relaxing found no third, so the 0.5 m pool stands");

            // Two candidate spots closer than the spacing count as one.
            var close = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f,
                (x, y) => new GolemGroundHit(Math.Abs(x - 96f) < 0.01f && (Math.Abs(y - 99f) < 0.01f || Math.Abs(y - 99.75f) < 0.01f) ? 40f : 37f, 0), out why);
            Assert.IsNull(close);
            StringAssert.Contains(why, "only 1 wisp spot(s)");
        }

        [TestMethod]
        public void Relaxing_never_swaps_a_pool_for_a_smaller_one()
        {
            // Code review 2026-11-11. Spots B (bearing 20) and C (bearing 340) on the 3 m ring are flat and 2.05 m apart;
            // ring slot A (bearing 0) sits 0.7 m up, 1.04 m from each. At 0.5 m the pool is { B, C }; at 0.75 m A is kept
            // first and the spacing then rejects both B and C, a pool of one. The two-spot pool must stand.
            (float X, float Y) At(float bearing) => (96f + 3f * (float)Math.Sin(bearing * Math.PI / 180), 96f + 3f * (float)Math.Cos(bearing * Math.PI / 180));
            bool Near((float X, float Y) p, float x, float y) => Math.Abs(p.X - x) < 0.01f && Math.Abs(p.Y - y) < 0.01f;
            var a = At(0f);
            var b = At(20f);
            var c = At(340f);

            var ring = GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f,
                (x, y) => new GolemGroundHit(Near(a, x, y) ? 40.7f : Near(b, x, y) || Near(c, x, y) ? 40f : 37f, 0), out var why, out var tol);

            Assert.IsNull(why, "two spaced spots stand");
            Assert.AreEqual(2, ring.Count);
            CollectionAssert.AreEquivalent(new[] { 9, 17 }, ring.Select(s => s.Index).ToArray(), "B and C, not A");
            Assert.AreEqual(0.5f, tol, 1e-4f, "the tolerance reported is the one that produced the pool");
        }

        [TestMethod]
        public void The_candidate_spots_start_with_the_nine_ring_slots_and_stay_in_bounds()
        {
            var spots = GolemEncounterType.CandidateSpots(3f);
            CollectionAssert.AreEqual(GolemEncounterType.SlotBearings().ToArray(), spots.Take(9).Select(s => s.Bearing).ToArray());
            Assert.IsTrue(spots.Take(18).All(s => s.Radius == 3f), "the in-between bearings are on the same ring");
            CollectionAssert.AreEqual(Enumerable.Range(0, 9).Select(k => 20f + k * 40f).ToArray(), spots.Skip(9).Take(9).Select(s => s.Bearing).ToArray());
            CollectionAssert.AreEqual(new[] { 3.75f, 2.25f, 4.5f, 5.25f, 6.0f }, spots.Skip(18).Select(s => s.Radius).Distinct().ToArray(), "nearest rings first, outward before inward, 2 m to 6 m");
            Assert.AreEqual(18 + 5 * 18, spots.Count);
            Assert.AreEqual(spots.Count, spots.Distinct().Count(), "no duplicate spot");

            // A ring at the bound still yields only in-bound extra rings.
            Assert.IsTrue(GolemEncounterType.CandidateSpots(6f).Skip(18).All(s => s.Radius >= 2.0f && s.Radius <= 6.0f));
            Assert.IsTrue(GolemEncounterType.CandidateSpots(1.5f).Skip(18).All(s => s.Radius >= 2.0f && s.Radius <= 6.0f));
        }

        [TestMethod]
        public void The_ground_probe_runs_once_per_candidate_and_stops_early_on_a_flat_ring()
        {
            var calls = 0;
            GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => { calls++; return new GolemGroundHit(40f, 0); }, out _);
            Assert.AreEqual(9, calls, "a flat ring probes only its nine slots");

            calls = 0;
            GolemEncounterType.BuildRing(96f, 96f, 40f, 3f, 0.5f, (x, y) => { calls++; return new GolemGroundHit(45f, 0); }, out _);
            Assert.AreEqual(GolemEncounterType.CandidateSpots(3f).Count, calls, "relaxing re-uses each probe instead of repeating it");
        }

        // ---- the spawn plan (owner ruling 2026-11-11: never a one-wisp round) ----------------------------------------

        [TestMethod]
        public void A_wisp_that_cannot_be_built_never_eats_the_slot_pool()
        {
            // The 2026-11-11 playtest shape: the round's second buff has no weenie. Before, every remaining slot was
            // tried for it and the third buff found none, so the round showed one wisp.
            var buffs = new[] { GolemBuff.Might, GolemBuff.Frenzy, GolemBuff.Vigor, GolemBuff.Ruin, GolemBuff.Arcana };
            var tries = new List<(int, GolemBuff)>();
            var plan = GolemEncounterType.PlanWisps(Enumerable.Range(0, 9).ToList(), buffs, 3, (pos, b) =>
            {
                tries.Add((pos, b));
                return b == GolemBuff.Frenzy ? GolemWispTry.BuffFailed : GolemWispTry.Placed;
            });

            CollectionAssert.AreEqual(new[] { (0, GolemBuff.Might), (1, GolemBuff.Vigor), (2, GolemBuff.Ruin) }, plan.ToArray(), "slot 1 is not lost, and the next bank buff stands in");
            Assert.AreEqual(1, tries.Count(t => t.Item2 == GolemBuff.Frenzy), "a buff that cannot be built is tried once");
        }

        [TestMethod]
        public void A_slot_that_refuses_gets_one_more_buff_then_the_next_slot()
        {
            var buffs = new[] { GolemBuff.Might, GolemBuff.Frenzy, GolemBuff.Vigor, GolemBuff.Ruin };
            var plan = GolemEncounterType.PlanWisps(new[] { 0, 1, 2, 3, 4 }, buffs, 3, (pos, b) => pos == 1 ? GolemWispTry.SlotFailed : GolemWispTry.Placed);

            CollectionAssert.AreEqual(new[] { (0, GolemBuff.Might), (2, GolemBuff.Frenzy), (3, GolemBuff.Vigor) }, plan.ToArray(),
                "slot 1 refuses Frenzy and Vigor, then is given up; Frenzy keeps its turn on slot 2");

            // A buff whose wisp lands off its spot everywhere leaves the round after three slots, so it cannot eat the pool.
            var tries = 0;
            var stubborn = GolemEncounterType.PlanWisps(Enumerable.Range(0, 9).ToList(), buffs, 3, (pos, b) =>
            {
                if (b == GolemBuff.Might) { tries++; return GolemWispTry.SlotFailed; }
                return GolemWispTry.Placed;
            });
            Assert.AreEqual(3, stubborn.Count);
            Assert.IsFalse(stubborn.Any(p => p.Buff == GolemBuff.Might));
            Assert.AreEqual(GolemEncounterType.MaxSlotFailuresPerBuff, tries);
        }

        [TestMethod]
        public void The_plan_stops_at_the_wanted_count_and_reports_a_short_round_honestly()
        {
            var buffs = GolemEncounterType.RoundBuffOrder(7, 0);
            Assert.AreEqual(3, GolemEncounterType.PlanWisps(Enumerable.Range(0, 9).ToList(), buffs, 3, (p, b) => GolemWispTry.Placed).Count);
            Assert.AreEqual(1, GolemEncounterType.PlanWisps(new[] { 0, 1, 2 }, buffs, 3, (p, b) => p == 0 ? GolemWispTry.Placed : GolemWispTry.SlotFailed).Count);
            Assert.AreEqual(0, GolemEncounterType.PlanWisps(new[] { 0, 1, 2 }, buffs, 3, (p, b) => GolemWispTry.BuffFailed).Count);
            Assert.AreEqual(0, GolemEncounterType.PlanWisps(Array.Empty<int>(), buffs, 3, (p, b) => GolemWispTry.Placed).Count);
        }

        [TestMethod]
        public void The_round_buff_order_starts_with_the_draw_and_holds_the_whole_bank()
        {
            for (var seed = 0; seed < 200; seed++)
                for (var round = 0; round < 6; round++)
                {
                    var order = GolemEncounterType.RoundBuffOrder(seed, round);
                    CollectionAssert.AreEqual(GolemEncounterType.DrawRound(seed, round), order.Take(3).ToArray());
                    Assert.AreEqual(GolemEncounterType.BankSize, order.Distinct().Count());
                }
        }

        [TestMethod]
        public void The_round_slot_order_is_the_fallback_order_on_a_full_pool_and_skips_missing_slots()
        {
            var full = Enumerable.Range(0, 9).Select(i => new GolemWispSlot(i, 0f, 0f, 0f, 0f, 0)).ToList();
            for (var round = 0; round < 4; round++)
                CollectionAssert.AreEqual(GolemEncounterType.FallbackOrder(round).ToArray(), GolemEncounterType.RoundSlotOrder(round, full).ToArray());

            // Pool without ring slot 4, with an extra spot (candidate 9) at the end: round 1's own set is slots 3 and 5.
            var partial = new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9 }.Select(i => new GolemWispSlot(i, 0f, 0f, 0f, 0f, 0)).ToList();
            CollectionAssert.AreEqual(new[] { 3, 4, 0, 1, 2, 5, 6, 7, 8 }, GolemEncounterType.RoundSlotOrder(1, partial).ToArray(), "pool positions, not candidate indices");
        }

        [TestMethod]
        public void Place_refuses_a_sloped_ring_and_reaches_the_factory_only_for_a_flat_one()
        {
            var savedCreate = GolemEncounterType.CreateObject;
            var savedGround = GolemEncounterType.GroundFor;

            try
            {
                var created = 0;
                GolemEncounterType.CreateObject = _ => { created++; return null; };

                var type = new GolemEncounterType();
                using var seams = new Seams(Store(), type);
                var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, 40f, kind: "outdoor");

                foreach (var (ground, fragment, reaches) in new (Func<float, float, GolemGroundHit?>, string, bool)[]
                {
                    ((x, y) => new GolemGroundHit(42f, 0), "the wisp ring is not flat: only 0 wisp spot(s)", false),
                    ((x, y) => null, "the wisp ring is not flat", false),
                    ((x, y) => new GolemGroundHit(40.2f, 0), "did not create", true),
                })
                {
                    GolemEncounterType.GroundFor = (r, c) => (new GolemGroundHit(40f, 0), ground, null);
                    var before = created;
                    var run = OutdoorRun(new[] { point }, max: 1);
                    var def = Type("golem", kind: GolemEncounterType.KindName);
                    def.Config = Json("{}");

                    Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, new ThreadEncounterPick(1, def, Candidate(point, outdoor: true), 7), type));
                    Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains(fragment)), fragment + ": " + string.Join("\n", run.EncounterOutcomes));
                    Assert.AreEqual(reaches ? before + 1 : before, created, fragment);
                }

                // No ground under the golem itself refuses too.
                GolemEncounterType.GroundFor = (r, c) => (null, (x, y) => new GolemGroundHit(40f, 0), null);
                var bare = OutdoorRun(new[] { point }, max: 1);
                var bareDef = Type("golem", kind: GolemEncounterType.KindName);
                bareDef.Config = Json("{}");
                Assert.IsNull(ThreadEncounterPass.PlaceOne(bare, null, new ThreadEncounterPick(1, bareDef, Candidate(point, outdoor: true), 7), type));
                Assert.IsTrue(bare.EncounterOutcomes.Any(l => l.Contains("no walkable ground under the golem")), string.Join("\n", bare.EncounterOutcomes));
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
                GolemEncounterType.GroundFor = savedGround;
            }
        }

        [TestMethod]
        public void The_ground_probe_is_always_released()
        {
            var savedCreate = GolemEncounterType.CreateObject;
            var savedGround = GolemEncounterType.GroundFor;

            try
            {
                var released = 0;
                GolemEncounterType.CreateObject = _ => null;
                GolemEncounterType.GroundFor = (r, c) => (new GolemGroundHit(40f, 0), (x, y) => throw new InvalidOperationException("probe threw"), () => released++);

                var type = new GolemEncounterType();
                using var seams = new Seams(Store(), type);
                var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, 40f, kind: "outdoor");
                var run = OutdoorRun(new[] { point }, max: 1);
                var def = Type("golem", kind: GolemEncounterType.KindName);
                def.Config = Json("{}");

                Assert.IsNull(ThreadEncounterPass.PlaceOne(run, null, new ThreadEncounterPick(1, def, Candidate(point, outdoor: true), 7), type));
                Assert.AreEqual(1, released, "the dungeon probe body is disposed even when a probe throws");
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
                GolemEncounterType.GroundFor = savedGround;
            }
        }

        [TestMethod]
        public void The_bearings_are_fixed_per_round_and_the_fallback_tries_the_rest()
        {
            CollectionAssert.AreEqual(new[] { 0f, 120f, 240f, 40f, 160f, 280f, 80f, 200f, 320f }, GolemEncounterType.SlotBearings().ToArray());

            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, GolemEncounterType.FallbackOrder(0).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 4, 5, 0, 1, 2, 6, 7, 8 }, GolemEncounterType.FallbackOrder(1).ToArray());
            CollectionAssert.AreEqual(new[] { 6, 7, 8, 0, 1, 2, 3, 4, 5 }, GolemEncounterType.FallbackOrder(2).ToArray());
            CollectionAssert.AreEqual(GolemEncounterType.FallbackOrder(0).ToArray(), GolemEncounterType.FallbackOrder(3).ToArray(), "round 4 reuses round 1's set");
        }

        [TestMethod]
        public void RejectPoint_keeps_the_ring_on_the_points_landblock_outdoors()
        {
            var type = new GolemEncounterType();
            var def = Type("golem", kind: GolemEncounterType.KindName);
            def.Config = Json("{}");

            Assert.IsNull(type.RejectPoint(def, Candidate(Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor"), outdoor: true), true));
            Assert.IsNull(type.RejectPoint(def, Candidate(Point(OutdoorCell(0x5456, 3.5f, 188.5f), 3.5f, 188.5f, kind: "outdoor"), outdoor: true), true));
            StringAssert.Contains(type.RejectPoint(def, Candidate(Point(OutdoorCell(0x5456, 3.4f, 96), 3.4f, 96, kind: "outdoor"), outdoor: true), true), "landblock");
            Assert.IsNull(type.RejectPoint(def, Candidate(Point(DungeonCell(), 1f, 1f)), false), "a dungeon has no landblock edge");

            var bad = Type("golem", kind: GolemEncounterType.KindName);
            bad.Config = Json("{\"rounds\": \"x\"}");
            StringAssert.Contains(type.RejectPoint(bad, Candidate(Point(DungeonCell(), 0, 0)), false), "rounds");
        }

        // ---- the round draw ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Each_round_draws_three_distinct_buffs_deterministically_by_seed()
        {
            var seen = new HashSet<GolemBuff>();
            var repeats = 0;

            for (var seed = 0; seed < 200; seed++)
            {
                for (var round = 0; round < 6; round++)
                {
                    var draw = GolemEncounterType.DrawRound(seed, round);

                    Assert.AreEqual(3, draw.Length);
                    Assert.AreEqual(3, draw.Distinct().Count(), $"seed {seed} round {round}");
                    CollectionAssert.AreEqual(draw, GolemEncounterType.DrawRound(seed, round), "deterministic");
                    seen.UnionWith(draw);
                }

                if (GolemEncounterType.DrawRound(seed, 0).Intersect(GolemEncounterType.DrawRound(seed, 1)).Any())
                    repeats++;
            }

            Assert.AreEqual(8, seen.Count, "every buff of the bank is reachable");
            Assert.IsTrue(repeats > 0, "a buff can be offered again in a later round");
            Assert.IsFalse(Enumerable.Range(0, 50).All(s => GolemEncounterType.DrawRound(s, 0).SequenceEqual(GolemEncounterType.DrawRound(0, 0))), "the seed matters");
        }

        [TestMethod]
        public void The_draw_is_roughly_uniform_over_the_bank()
        {
            var tally = new int[8];

            for (var seed = 0; seed < 4000; seed++)
                foreach (var b in GolemEncounterType.DrawRound(seed, seed % 6))
                    tally[(int)b]++;

            // 12000 draws over 8 buffs: 1500 each expected.
            foreach (var n in tally)
                Assert.IsTrue(n > 1300 && n < 1700, string.Join(",", tally));
        }

        [TestMethod]
        public void The_timeout_choice_is_one_of_the_offered_and_deterministic()
        {
            var hits = new int[3];

            for (var seed = 0; seed < 300; seed++)
            {
                var i = GolemEncounterType.TimeoutChoice(seed, 1, 3);
                Assert.IsTrue(i >= 0 && i < 3);
                Assert.AreEqual(i, GolemEncounterType.TimeoutChoice(seed, 1, 3));
                hits[i]++;
            }

            Assert.IsTrue(hits.All(h => h > 50), string.Join(",", hits));
            Assert.AreEqual(0, GolemEncounterType.TimeoutChoice(5, 0, 1), "a one-wisp round");
        }

        // ---- the lifecycle -------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_non_member_is_refused_and_two_uses_in_one_tick_begin_one_shaping()
        {
            var state = NewState();
            Assert.AreEqual(GolemUseOutcome.NotInside, state.TryBegin(false, false));
            Assert.AreEqual(GolemUseOutcome.RunOver, state.TryBegin(true, true));
            Assert.AreEqual(GolemPhase.Dormant, state.Phase);

            for (var round = 0; round < 20; round++)
            {
                var racing = NewState();
                var outcomes = new ConcurrentBag<GolemUseOutcome>();
                using var go = new ManualResetEventSlim(false);
                var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { go.Wait(); outcomes.Add(racing.TryBegin(false, true)); })).ToArray();
                go.Set();
                Task.WaitAll(tasks);

                Assert.AreEqual(1, outcomes.Count(o => o == GolemUseOutcome.Begun));
                Assert.AreEqual(7, outcomes.Count(o => o == GolemUseOutcome.Shaping));
            }
        }

        [TestMethod]
        public void Two_picks_in_one_tick_resolve_to_exactly_one()
        {
            for (var round = 0; round < 30; round++)
            {
                var (state, gen) = Shaping();
                var wins = new ConcurrentBag<GolemBuff>();
                using var go = new ManualResetEventSlim(false);

                // Three wisp uses and the timeout, all at once.
                var picks = new[] { GolemBuff.Might, GolemBuff.Frenzy, GolemBuff.Vigor, GolemBuff.Frenzy, GolemBuff.Might, GolemBuff.Vigor };
                var tasks = picks.Select(b => Task.Run(() => { go.Wait(); if (state.TryPick(gen, b, out _, out _)) wins.Add(b); })).ToArray();
                go.Set();
                Task.WaitAll(tasks);

                Assert.AreEqual(1, wins.Count, $"round {round}");
                Assert.AreEqual(1, state.Counts.Sum());
                Assert.AreEqual(1, state.Round);
                Assert.IsFalse(state.RoundOpen);
            }
        }

        [TestMethod]
        public void A_pick_must_be_offered_and_of_the_open_round()
        {
            var (state, gen) = Shaping();

            Assert.IsFalse(state.TryPick(gen, GolemBuff.Ruin, out _, out _), "not offered this round");
            Assert.IsFalse(state.TryPick(gen + 1, GolemBuff.Might, out _, out _), "another generation");
            Assert.IsTrue(state.TryGetWispBuff(WispB, out var buff));
            Assert.AreEqual(GolemBuff.Frenzy, buff);
            Assert.IsFalse(state.TryGetWispBuff(0x80009999u, out _), "not a wisp of this round");

            Assert.IsTrue(state.TryPick(gen, GolemBuff.Frenzy, out var pickedRound, out var last));
            Assert.AreEqual(0, pickedRound);
            Assert.IsFalse(last);
            Assert.IsFalse(state.TryPick(gen, GolemBuff.Might, out _, out _), "the round is spent");
            Assert.IsFalse(state.TryGetWispBuff(WispA, out _), "the spent round's wisps carry nothing");
            Assert.AreEqual(0, state.OpenRound(0, new[] { GolemBuff.Might }, new[] { WispA }), "round 0 cannot reopen");
        }

        [TestMethod]
        public void The_timeout_pick_spends_the_round_like_a_wisp_use_and_repeat_picks_stack()
        {
            var state = NewState(rounds: 3);
            Assert.AreEqual(GolemUseOutcome.Begun, state.TryBegin(false, true));

            for (var r = 0; r < 3; r++)
            {
                var offered = new[] { GolemBuff.Might, GolemBuff.Arcana, GolemBuff.Mending };
                var gen = state.OpenRound(r, offered, Array.Empty<uint>());
                Assert.AreNotEqual(0, gen, $"round {r} opens (a round with no placed wisps still opens)");

                // A member picks Might every round; the round's timeout (one of the offered, chosen by the seed) then fires late.
                var choice = offered[GolemEncounterType.TimeoutChoice(7, r, offered.Length)];
                Assert.IsTrue(state.TryPick(gen, GolemBuff.Might, out var pr, out var last));
                Assert.AreEqual(r, pr);
                Assert.AreEqual(r == 2, last);
                Assert.IsFalse(state.TryPick(gen, choice, out _, out _), "a late timeout after the pick is a no-op");
            }

            CollectionAssert.AreEqual(new[] { 3, 0, 0, 0, 0, 0, 0, 0 }, state.Counts, "the same buff picked three times stacks");
            Assert.AreEqual(GolemUseOutcome.Shaping, state.TryBegin(false, true), "still shaping until the swap");
            Assert.AreEqual(0, state.OpenRound(3, new[] { GolemBuff.Might }, null), "no fourth round");

            Assert.IsTrue(state.MarkAwakened(0x80002000u));
            Assert.AreEqual(GolemPhase.Awakened, state.Phase);
            Assert.AreEqual(GolemUseOutcome.Spent, state.TryBegin(false, true));
            Assert.IsFalse(state.MarkAwakened(0x80002001u), "one swap");
        }

        [TestMethod]
        public void A_timeout_alone_resolves_a_round_to_one_of_its_offered_buffs()
        {
            var (state, gen) = Shaping(rounds: 1);
            var offered = state.Offered;
            var choice = offered[GolemEncounterType.TimeoutChoice(11, 0, offered.Count)];

            Assert.IsTrue(state.TryPick(gen, choice, out var pickedRound, out var last), "nobody picked: the timeout spends the round");
            Assert.AreEqual(0, pickedRound);
            Assert.IsTrue(last);
            CollectionAssert.AreEqual(new[] { choice }, state.Picked.ToArray());
            Assert.IsTrue(new[] { GolemBuff.Might, GolemBuff.Frenzy, GolemBuff.Vigor }.Contains(choice));
            Assert.IsFalse(state.TryPick(gen, choice, out _, out _), "a wisp use racing in after the timeout is refused");
        }

        [TestMethod]
        public void The_golem_cannot_awaken_before_the_last_round()
        {
            var (state, gen) = Shaping(rounds: 2);
            Assert.IsFalse(state.MarkAwakened(1), "round 1 is open");
            Assert.IsTrue(state.TryPick(gen, GolemBuff.Vigor, out _, out var last));
            Assert.IsFalse(last);
            Assert.IsFalse(state.MarkAwakened(1), "one round left");
        }

        [TestMethod]
        public void The_death_latch_fires_once_and_the_prize_goes_once_to_every_member_inside()
        {
            var (state, gen) = Shaping(rounds: 1);
            Assert.IsTrue(state.TryPick(gen, GolemBuff.Might, out _, out var last));
            Assert.IsTrue(last);
            Assert.IsFalse(state.TryMarkDead(0x80002000u), "not awakened yet");
            Assert.IsTrue(state.MarkAwakened(0x80002000u));

            Assert.IsFalse(state.TryMarkDead(0x80002001u), "another creature's death");

            var wins = 0;
            Parallel.For(0, 16, _ => { if (state.TryMarkDead(0x80002000u)) Interlocked.Increment(ref wins); });
            Assert.AreEqual(1, wins, "one death, however it is reported");
            Assert.AreEqual(GolemPhase.Done, state.Phase);

            var members = new[] { "Ana", "Bo", "Cy" };
            var granted = new List<string>();
            Assert.AreEqual(3, GolemEncounterType.GrantToAll(members, m => { granted.Add(m); return true; }));
            CollectionAssert.AreEqual(members, granted, "one grant per member inside");
            Assert.AreEqual(2, GolemEncounterType.GrantToAll(members, m => m != "Bo"), "a refused grant is not counted");
            Assert.AreEqual(0, GolemEncounterType.GrantToAll<string>(null, m => true));
        }

        [TestMethod]
        public void The_death_hook_grants_only_after_the_latch_and_reaches_every_member_inside()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var hook = PooledLootSourceText.MethodBody(src, "public void OnTrackedCreatureDied(ThreadEncounterInstance instance, Creature creature)");

            var latch = hook.IndexOf("if (!state.TryMarkDead(creature.Guid.Full))", StringComparison.Ordinal);
            var line = hook.IndexOf("SendAll(run, DeathLine);", StringComparison.Ordinal);
            var grant = hook.IndexOf("GrantToAll(MembersInside(run), winner => GrantPrize(run, winner, KindName))", StringComparison.Ordinal);
            var record = hook.IndexOf("run.RecordEncounterOutcome(", StringComparison.Ordinal);

            Assert.IsTrue(latch >= 0 && latch < line && line < grant && grant < record, hook);
            StringAssert.Contains(src, "internal static Func<ThreadDungeonRun, List<Player>> MembersInside = run => ThreadRunPresence.OnlineMembersInside(run);");
            StringAssert.Contains(src, "internal static Func<ThreadDungeonRun, Player, string, bool> GrantPrize = ThreadEncounterPrize.Grant;");
            Assert.AreEqual("golem", GolemEncounterType.KindName);
        }

        [TestMethod]
        public void The_kill_hook_hands_tracked_deaths_to_the_type_before_the_bonus_route()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var died = PooledLootSourceText.MethodBody(src, "public static void OnRunCreatureDied(Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager = null, ACE.Server.Entity.DamageHistoryInfo topDamager = null)");

            var live = died.IndexOf("!ReferenceEquals(live, run)", StringComparison.Ordinal);
            var tracked = died.IndexOf("ThreadEncounterPass.OnTrackedCreatureDied(creature);", StringComparison.Ordinal);
            var bonus = died.IndexOf("if (TryRecordBonusKill(run, creature))", StringComparison.Ordinal);
            Assert.IsTrue(live >= 0 && live < tracked && tracked < bonus);

            var pass = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPass.cs");
            var dispatch = PooledLootSourceText.MethodBody(pass, "public static void OnTrackedCreatureDied(ACE.Server.WorldObjects.Creature creature)");
            StringAssert.Contains(dispatch, "instance.Type is IThreadEncounterDeathAware aware");
            StringAssert.Contains(dispatch, "catch (Exception ex)");

            Assert.IsInstanceOfType(new GolemEncounterType(), typeof(IThreadEncounterDeathAware));
        }

        [TestMethod]
        public void OnRunEnded_releases_the_request()
        {
            var run = OutdoorRun(new[] { Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor") });
            var type = new GolemEncounterType();
            var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("golem", kind: "golem"), Candidate(Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor"), outdoor: true), 7), type);
            var state = new GolemState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new GolemConfig(), null);
            instance.State = state;

            Assert.IsFalse(GolemEncounterType.RunOver(run, state));
            type.OnRunEnded(instance, "test");
            type.OnRunEnded(instance, "again");
            Assert.IsNull(state.Request);
            Assert.IsTrue(GolemEncounterType.RunOver(run, state), "every queued action is silent now");
        }

        // ---- the buffs -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Every_buff_stacks_ratings_additively_and_factors_multiplicatively()
        {
            var none = GolemEncounterType.Compute(new int[8], 1.0);
            Assert.AreEqual(new GolemBuffEffect(0, 0, 0, 0, 1, 1, 1, 1, 1), none);

            var one = GolemEncounterType.Compute(Enumerable.Repeat(1, 8).ToArray(), 1.0);
            Assert.AreEqual(10, one.DamageRating, "Might");
            Assert.AreEqual(10, one.CritRating, "Keen Edge");
            Assert.AreEqual(15, one.CritDamageRating, "Ruin");
            Assert.AreEqual(10, one.DamageResistRating, "Bulwark");
            Assert.AreEqual(1.20, one.AttackSpeedMult, 1e-9, "Frenzy");
            Assert.AreEqual(1.25, one.HealthMult, 1e-9, "Vigor");
            Assert.AreEqual(1.50, one.SpellChanceMult, 1e-9, "Arcana chance");
            Assert.AreEqual(1.15, one.CastSpeedMult, 1e-9, "Arcana cast");
            Assert.AreEqual(2.00, one.HealthRateMult, 1e-9, "Mending");

            var three = GolemEncounterType.Compute(Enumerable.Repeat(3, 8).ToArray(), 1.0);
            Assert.AreEqual((30, 30, 45, 30), (three.DamageRating, three.CritRating, three.CritDamageRating, three.DamageResistRating), "ratings add");
            Assert.AreEqual(Math.Pow(1.20, 3), three.AttackSpeedMult, 1e-9, "factors multiply");
            Assert.AreEqual(Math.Pow(1.25, 3), three.HealthMult, 1e-9);
            Assert.AreEqual(Math.Pow(1.50, 3), three.SpellChanceMult, 1e-9);
            Assert.AreEqual(Math.Pow(1.15, 3), three.CastSpeedMult, 1e-9);
            Assert.AreEqual(8.0, three.HealthRateMult, 1e-9);

            // Only the picked buff moves.
            var mightTwice = GolemEncounterType.Compute(Counts((GolemBuff.Might, 2)), 1.0);
            Assert.AreEqual(new GolemBuffEffect(20, 0, 0, 0, 1, 1, 1, 1, 1), mightTwice);
        }

        [TestMethod]
        public void The_pick_scale_sizes_every_pick_and_clamps_to_zero_to_three()
        {
            var all = Enumerable.Repeat(2, 8).ToArray();

            var half = GolemEncounterType.Compute(all, 0.5);
            Assert.AreEqual((10, 10, 15, 10), (half.DamageRating, half.CritRating, half.CritDamageRating, half.DamageResistRating));
            Assert.AreEqual(1.10 * 1.10, half.AttackSpeedMult, 1e-9);
            Assert.AreEqual(1.125 * 1.125, half.HealthMult, 1e-9);
            Assert.AreEqual(1.25 * 1.25, half.SpellChanceMult, 1e-9);
            Assert.AreEqual(1.075 * 1.075, half.CastSpeedMult, 1e-9);
            Assert.AreEqual(1.5 * 1.5, half.HealthRateMult, 1e-9);

            Assert.AreEqual(new GolemBuffEffect(0, 0, 0, 0, 1, 1, 1, 1, 1), GolemEncounterType.Compute(all, 0.0), "scale 0: picks do nothing");

            var high = GolemEncounterType.Compute(Counts((GolemBuff.Ruin, 1), (GolemBuff.Vigor, 1)), 10.0);
            Assert.AreEqual(45, high.CritDamageRating, "clamped to 3");
            Assert.AreEqual(1.75, high.HealthMult, 1e-9);
            Assert.AreEqual(GolemEncounterType.Compute(Counts((GolemBuff.Might, 1)), 1.0), GolemEncounterType.Compute(Counts((GolemBuff.Might, 1)), double.NaN), "NaN reads as 1");
        }

        [TestMethod]
        public void Health_is_the_larger_of_the_shaped_value_and_the_floor_then_vigor_on_top()
        {
            Assert.AreEqual(15000, GolemEncounterType.HealthTarget(8000, 10000, 1.5, 1.0), "the floor wins");
            Assert.AreEqual(20000, GolemEncounterType.HealthTarget(20000, 10000, 1.5, 1.0), "the shaped value wins");
            Assert.AreEqual(18750, GolemEncounterType.HealthTarget(8000, 10000, 1.5, 1.25), "Vigor multiplies the result");
            Assert.AreEqual(8000, GolemEncounterType.HealthTarget(8000, 0, 1.5, 1.0), "no observed max (tests)");
            Assert.AreEqual(10000, GolemEncounterType.HealthTarget(8000, 10000, 0.2, 1.0), "the mult clamps to at least 1");
            Assert.AreEqual(100000, GolemEncounterType.HealthTarget(8000, 10000, 50, 1.0), "the mult clamps to at most 10");
            Assert.AreEqual(int.MaxValue, GolemEncounterType.HealthTarget(uint.MaxValue, 0, 1.5, 2.0));
        }

        [TestMethod]
        public void Spell_chance_scales_in_the_spell_book_encoding_and_caps_at_certain()
        {
            Assert.AreEqual(2.03f, GolemEncounterType.ScaleSpellProbability(2.02f, 1.5), 1e-5);
            Assert.AreEqual(2.0675f, GolemEncounterType.ScaleSpellProbability(2.045f, 1.5), 1e-5);
            Assert.AreEqual(3.0f, GolemEncounterType.ScaleSpellProbability(2.9f, 2.0), 1e-5, "capped at a chance of 1");
            Assert.AreEqual(1.5f, GolemEncounterType.ScaleSpellProbability(1.0f, 1.5), 1e-5, "a percent-form value stays a percent while small");
            Assert.AreEqual(2.02f, GolemEncounterType.ScaleSpellProbability(2.02f, 1.0), 1e-5);
        }

        [TestMethod]
        public void The_speed_overlay_is_one_record_per_moved_axis()
        {
            Assert.AreEqual("", GolemEncounterType.SpeedOverlay(GolemEncounterType.Compute(new int[8], 1.0)));
            Assert.AreEqual("speed axis=attack pct=0.2", GolemEncounterType.SpeedOverlay(GolemEncounterType.Compute(Counts((GolemBuff.Frenzy, 1)), 1.0)));
            Assert.AreEqual("speed axis=attack pct=0.44; speed axis=cast pct=0.15",
                GolemEncounterType.SpeedOverlay(GolemEncounterType.Compute(Counts((GolemBuff.Frenzy, 2), (GolemBuff.Arcana, 1)), 1.0)));
        }

        [TestMethod]
        public void The_aura_cycles_the_distinct_picks_in_bank_order_with_colours_the_golem_table_carries()
        {
            CollectionAssert.AreEqual(new[] { GolemBuff.Might, GolemBuff.Bulwark }, GolemEncounterType.AuraOrder(Counts((GolemBuff.Bulwark, 1), (GolemBuff.Might, 2))).ToArray());
            Assert.AreEqual(0, GolemEncounterType.AuraOrder(new int[8]).Count);

            CollectionAssert.AreEqual(new[]
            {
                ACE.Entity.Enum.PlayScript.EnchantUpRed, ACE.Entity.Enum.PlayScript.EnchantUpOrange, ACE.Entity.Enum.PlayScript.EnchantUpYellow,
                ACE.Entity.Enum.PlayScript.EnchantUpGreen, ACE.Entity.Enum.PlayScript.EnchantUpBlue, ACE.Entity.Enum.PlayScript.EnchantUpPurple,
                ACE.Entity.Enum.PlayScript.ShieldUpGrey, ACE.Entity.Enum.PlayScript.EnchantUpWhite,
            }, GolemEncounterType.AuraScripts.ToArray());
        }

        // ---- text, shipped data, wiring --------------------------------------------------------------------------

        [TestMethod]
        public void The_player_text_is_ascii_and_verbatim()
        {
            Assert.AreEqual("Only members of this thread can shape the golem.", GolemEncounterType.RefusedLine);
            // Owner ruling 2026-11-11: the round lines say how many wisps stand, spelled out.
            Assert.AreEqual("The golem's core flickers. Three wisps drift free - choose one to bind into it.", GolemEncounterType.BeginLine(3));
            Assert.AreEqual("The golem's core flickers. Two wisps drift free - choose one to bind into it.", GolemEncounterType.BeginLine(2));
            Assert.AreEqual("The golem is already being shaped.", GolemEncounterType.ShapingLine);
            Assert.AreEqual("Three more wisps drift free.", GolemEncounterType.NextRoundLine(3));
            Assert.AreEqual("Two more wisps drift free.", GolemEncounterType.NextRoundLine(2));
            Assert.AreEqual("No wisp can settle here. The golem chooses for itself: Wisp of the Bulwark.", GolemEncounterType.NoWispLine(GolemBuff.Bulwark));
            Assert.AreEqual("The Craftwork Golem awakens!", GolemEncounterType.AwakenLine);
            Assert.AreEqual("The Craftwork Golem falls apart.", GolemEncounterType.DeathLine);
            Assert.AreEqual("Pell binds the Wisp of the Keen Edge into the golem. (Round 2 of 3)", GolemEncounterType.PickLine("Pell", GolemBuff.KeenEdge, 2, 3));
            Assert.AreEqual("The golem draws the Wisp of Mending into itself. (Round 3 of 3)", GolemEncounterType.TimeoutLine(GolemBuff.Mending, 3, 3));

            CollectionAssert.AreEqual(new[]
            {
                "Wisp of Might", "Wisp of the Keen Edge", "Wisp of Frenzy", "Wisp of Vigor",
                "Wisp of Arcana", "Wisp of Ruin", "Wisp of the Bulwark", "Wisp of Mending",
            }, GolemEncounterType.WispNames.ToArray());

            var lines = new[] { GolemEncounterType.RefusedLine, GolemEncounterType.BeginLine(2), GolemEncounterType.BeginLine(3), GolemEncounterType.ShapingLine,
                GolemEncounterType.NextRoundLine(2), GolemEncounterType.NextRoundLine(3), GolemEncounterType.NoWispLine(GolemBuff.Might),
                GolemEncounterType.AwakenLine, GolemEncounterType.DeathLine }.Concat(GolemEncounterType.WispNames);
            Assert.IsTrue(lines.All(l => l.All(c => c >= 0x20 && c < 0x7F)));
        }

        [TestMethod]
        public void The_wisp_weenies_carry_the_bank_names_in_bank_order()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);

            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Content", "sql", "weenies")))
                root = root.Parent;

            if (root == null)
                Assert.Fail($"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}");

            var dir = Path.Combine(root.FullName, "Content", "sql", "weenies");

            for (var i = 0; i < 8; i++)
            {
                var wcid = 1007972 + i;
                var file = Directory.GetFiles(dir, $"{wcid} *.sql").Single();
                StringAssert.Contains(File.ReadAllText(file), $"({wcid},   1, '{GolemEncounterType.WispNames[i]}') /* Name */", file);
            }
        }

        // ---- plate skin --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Skin_defaults_to_copper_bronze_and_each_valid_value_parses()
        {
            Assert.AreEqual(GolemSkin.CopperBronze, GolemEncounterType.ParseConfig(Json("{}"), null, out _).Skin);
            Assert.AreEqual(GolemSkin.CopperBronze, GolemEncounterType.ParseConfig(default, null, out _).Skin);

            foreach (var (text, skin, palette) in new[]
            {
                ("copperBronze", GolemSkin.CopperBronze, 0x0400068Cu),
                ("silver", GolemSkin.Silver, 0x04000487u),
                ("gold", GolemSkin.Gold, 0x0400044Fu),
            })
            {
                var config = GolemEncounterType.ParseConfig(Json("{\"skin\": \"" + text + "\"}"), null, out var error);
                Assert.IsNull(error, text);
                Assert.AreEqual(skin, config.Skin, text);
                Assert.AreEqual(palette, GolemEncounterType.SkinPaletteId(config.Skin), text);
            }
        }

        [TestMethod]
        public void An_unknown_or_mistyped_skin_is_refused_with_a_reason()
        {
            foreach (var text in new[] { "{\"skin\": \"bronze\"}", "{\"skin\": \"Silver\"}", "{\"skin\": \"\"}", "{\"skin\": 2}", "{\"skin\": null}", "{\"skin\": true}" })
            {
                var config = GolemEncounterType.ParseConfig(Json(text), null, out var error);
                Assert.IsNull(config, text);
                StringAssert.Contains(error, "config.skin must be one of copperBronze, silver, gold", text);
            }
        }

        private static ACE.Server.WorldObjects.Creature PlateGolem(uint guid) => PlateGolem(guid, out _, true);

        private static ACE.Server.WorldObjects.Creature PlateGolem(uint guid, out ACE.Entity.Models.Weenie weenie, bool plateBase)
        {
            TestGameTables.EnsureInitialized();
            weenie = new ACE.Entity.Models.Weenie { WeenieClassId = 1007971, WeenieType = ACE.Entity.Enum.WeenieType.Creature };

            if (plateBase)
                weenie.PropertiesDID = new Dictionary<ACE.Entity.Enum.Properties.PropertyDataId, uint> { { ACE.Entity.Enum.Properties.PropertyDataId.PaletteBase, GolemEncounterType.SkinPaletteBase } };

            weenie.PropertiesPalette = new List<ACE.Entity.Models.PropertiesPalette>
            {
                new ACE.Entity.Models.PropertiesPalette { SubPaletteId = 0x0400068Cu, Offset = 0, Length = 128 },
                new ACE.Entity.Models.PropertiesPalette { SubPaletteId = 0x0400068Cu, Offset = 128, Length = 128 },
            };
            return new ACE.Server.WorldObjects.Creature(weenie, new ACE.Entity.ObjectGuid(guid));
        }

        [TestMethod]
        public void A_created_golem_gets_the_selected_skins_palette_rows_as_two_byte_safe_halves()
        {
            var savedCreate = GolemEncounterType.CreateObject;
            try
            {
                uint nextGuid = 0x50000F00u;
                ACE.Entity.Models.Weenie weenie = null;
                IList<ACE.Entity.Models.PropertiesPalette> listBefore = null;
                GolemEncounterType.CreateObject = _ =>
                {
                    var created = PlateGolem(nextGuid++, out weenie, true);
                    listBefore = created.Biota.PropertiesPalette;
                    return created;
                };

                foreach (var (skin, palette) in new[] { (GolemSkin.Silver, 0x04000487u), (GolemSkin.Gold, 0x0400044Fu) })
                {
                    foreach (var wcid in new[] { GolemEncounterType.DefaultDormantWcid, GolemEncounterType.DefaultGolemWcid })
                    {
                        var wo = GolemEncounterType.CreateSkinned(wcid, new GolemConfig { Skin = skin });

                        Assert.IsTrue(weenie.PropertiesPalette.Count == 2 && weenie.PropertiesPalette.All(r => r.SubPaletteId == 0x0400068Cu),
                            $"{skin} {wcid}: the weenie (the world cache's template) still holds copper-bronze");
                        Assert.AreNotSame(weenie.PropertiesPalette, wo.Biota.PropertiesPalette, $"{skin} {wcid}: the object's rows are not the weenie's list");
                        Assert.AreNotSame(listBefore, wo.Biota.PropertiesPalette, $"{skin} {wcid}: the rows are replaced with a new list, never edited in place");
                        Assert.IsTrue(listBefore.All(r => r.SubPaletteId == 0x0400068Cu), $"{skin} {wcid}: the replaced list still holds the authored rows");

                        var rows = wo.Biota.PropertiesPalette.ToList();

                        Assert.AreEqual(2, rows.Count, $"{skin} {wcid}");
                        Assert.IsTrue(rows.All(r => r.SubPaletteId == palette), $"{skin} {wcid}");
                        CollectionAssert.AreEqual(new[] { (0, 128), (128, 128) }, rows.Select(r => ((int)r.Offset, (int)r.Length)).ToArray(), $"{skin} {wcid}");
                        Assert.IsTrue(rows.All(r => r.Offset + r.Length <= 256), "the span stays within 256 palette entries");
                    }
                }
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
            }
        }

        [TestMethod]
        public void A_golem_without_the_plate_palette_base_keeps_its_authored_rows_under_any_skin()
        {
            var savedCreate = GolemEncounterType.CreateObject;
            try
            {
                var golem = PlateGolem(0x50000FC0u, out _, false);
                var before = golem.Biota.PropertiesPalette;
                GolemEncounterType.CreateObject = _ => golem;

                var wo = GolemEncounterType.CreateSkinned(GolemEncounterType.DefaultGolemWcid, new GolemConfig { Skin = GolemSkin.Silver });

                Assert.AreSame(golem, wo);
                Assert.AreSame(before, wo.Biota.PropertiesPalette, "no PaletteBase: rows untouched");
                Assert.IsTrue(wo.Biota.PropertiesPalette.All(r => r.SubPaletteId == 0x0400068Cu));
                Assert.IsFalse(GolemEncounterType.ApplySkinRows(wo.Biota, wo.BiotaDatabaseLock, GolemSkin.Gold));
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
            }
        }

        [TestMethod]
        public void The_default_skin_leaves_the_weenies_copper_bronze_rows_alone_and_a_null_object_passes_through()
        {
            var savedCreate = GolemEncounterType.CreateObject;
            try
            {
                var golem = PlateGolem(0x50000F80u);
                var before = golem.Biota.PropertiesPalette;
                GolemEncounterType.CreateObject = _ => golem;

                var wo = GolemEncounterType.CreateSkinned(GolemEncounterType.DefaultGolemWcid, new GolemConfig());

                Assert.AreSame(golem, wo);
                Assert.AreSame(before, wo.Biota.PropertiesPalette, "copper-bronze is the weenie default: rows untouched");
                Assert.IsTrue(wo.Biota.PropertiesPalette.All(r => r.SubPaletteId == 0x0400068Cu));

                GolemEncounterType.CreateObject = _ => null;
                Assert.IsNull(GolemEncounterType.CreateSkinned(GolemEncounterType.DefaultGolemWcid, new GolemConfig { Skin = GolemSkin.Gold }));
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
            }
        }

        [TestMethod]
        public void Both_golem_creation_sites_go_through_the_skinning_seam()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");

            StringAssert.Contains(src, "var dormant = CreateSkinned(config.DormantWcid, config);");
            StringAssert.Contains(src, "var wo = CreateSkinned(state.Config.GolemWcid, state.Config);");
            Assert.IsFalse(src.Contains("CreateObject(config.DormantWcid)") || src.Contains("CreateObject(state.Config.GolemWcid)"), "no golem body bypasses the skin");
            StringAssert.Contains(src, "ApplySkinRows(wo.Biota, wo.BiotaDatabaseLock, config?.Skin ?? GolemSkin.CopperBronze)");
        }

        /// <summary>The text of one INSERT statement (header to its terminating semicolon); fails naming the file when the header or terminator is missing.</summary>
        private static string InsertStatement(string sql, string table, string file)
        {
            var start = sql.IndexOf("INSERT INTO `" + table + "`", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{file} has no INSERT INTO `{table}` statement");

            var end = sql.IndexOf(';', start);
            Assert.IsTrue(end > start, $"{file}: the INSERT INTO `{table}` statement has no terminating semicolon");

            return sql.Substring(start, end - start);
        }

        [TestMethod]
        public void The_golem_weenies_carry_the_plate_texture_swap_the_plate_palette_base_and_copper_bronze_rows()
        {
            var expectedParts = new[] { 0, 1, 2, 4, 5, 7, 8, 9, 10, 11, 12, 13 };

            foreach (var (wcid, file) in new[] { (1007970, "1007970 Dormant Craftwork Golem.sql"), (1007971, "1007971 Craftwork Golem.sql") })
            {
                var sql = PooledLootSourceText.Read("Content/sql/weenies/" + file);

                StringAssert.Contains(sql, $"({wcid},   6, 0x{GolemEncounterType.SkinPaletteBase:X8}) /* PaletteBase", file);
                Assert.IsFalse(sql.Contains("0x04000B9D) /* PaletteBase"), file);

                var texBlock = InsertStatement(sql, "weenie_properties_texture_map", file);
                var parts = System.Text.RegularExpressions.Regex.Matches(texBlock, $@"\({wcid}, (\d+), 83890960, 83886238\)")
                    .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
                CollectionAssert.AreEqual(expectedParts, parts, file + " texture_map parts (0x05001310 -> 0x0500009E)");
                Assert.AreEqual(12, System.Text.RegularExpressions.Regex.Matches(texBlock, @"\(\d+, \d+, \d+, \d+\)").Count, file + " carries only the 12 plate rows");

                var palBlock = InsertStatement(sql, "weenie_properties_palette", file);
                StringAssert.Contains(palBlock, $"({wcid}, {GolemEncounterType.CopperBronzePaletteId}, 0, 128)", file);
                StringAssert.Contains(palBlock, $"({wcid}, {GolemEncounterType.CopperBronzePaletteId}, 128, 128)", file);
                Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(palBlock, @"\(\d+, \d+, \d+, \d+\)").Count, file + " palette rows");
            }
        }

        [TestMethod]
        public void Both_golem_bodies_are_scaled_alike_and_the_awake_one_runs_at_a_normal_pace()
        {
            // Owner playtest 2026-11-11: 25% larger (DefaultScale 1.25 on both, so the swap keeps its size), and no longer
            // "incredibly slow" (Run 320, see the 1007971 header SPEED note).
            foreach (var (wcid, file) in new[] { (1007970, "1007970 Dormant Craftwork Golem.sql"), (1007971, "1007971 Craftwork Golem.sql") })
            {
                var floats = InsertStatement(PooledLootSourceText.Read("Content/sql/weenies/" + file), "weenie_properties_float", file);
                var scale = System.Text.RegularExpressions.Regex.Matches(floats, $@"\({wcid},\s+39,\s+([0-9.]+)\)");
                Assert.AreEqual(1, scale.Count, file + " carries one DefaultScale row");
                Assert.AreEqual("1.25", scale[0].Groups[1].Value, file);
            }

            var skills = InsertStatement(PooledLootSourceText.Read("Content/sql/weenies/1007971 Craftwork Golem.sql"), "weenie_properties_skill", "1007971");
            StringAssert.Contains(skills, "(1007971, 24, 0, 2, 0, 320, 0, 0) /* Run");
        }

        [TestMethod]
        public void The_shipped_row_is_open_to_the_slot_roll_both_run_kinds_and_parses_clean()
        {
            var text = File.ReadAllText(Path.Combine(ThreadDungeonStoreTests.FindDynamicDir(), ThreadEncounterStore.FileName));
            var diagnostics = new List<string>();
            var store = ThreadEncounterStore.Parse(text, null, diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));

            var row = store.Types.Single(t => t.Kind == GolemEncounterType.KindName);
            var defense = store.Types.Single(t => t.Kind == DefenseEncounterType.KindName);

            Assert.AreEqual("golem", row.Id);
            Assert.IsTrue(row.Enabled);
            Assert.AreEqual(1.0, row.Weight, "open to the slot roll (owner ruling 2026-11-11)");
            Assert.AreEqual(3.5f, row.Placement.MinClearance);
            Assert.AreEqual(defense.Placement.MinEntryWalk, row.Placement.MinEntryWalk);
            Assert.AreEqual(defense.Placement.MinBossDist, row.Placement.MinBossDist);
            Assert.AreEqual(defense.Placement.MinSpacing, row.Placement.MinSpacing);
            Assert.IsTrue(row.Placement.AllowsRunKind(true));
            Assert.IsTrue(row.Placement.AllowsRunKind(false));

            var problems = new List<string>();
            var config = GolemEncounterType.ParseConfig(row.Config, problems, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.AreEqual(3, config.Rounds);
            Assert.AreEqual(30.0, config.RoundSeconds);
            Assert.AreEqual(0.5f, config.HeightTolerance);
            Assert.AreEqual(GolemSkin.CopperBronze, config.Skin);
            Assert.IsTrue(row.Config.TryGetProperty("skin", out var skinKey) && skinKey.GetString() == "copperBronze", "the shipped row names its skin explicitly");
            Assert.IsTrue(config.RingRadius < row.Placement.MinClearance, "the dungeon clearance covers the ring");
        }

        [TestMethod]
        public void The_tunables_clamp_and_an_unstamped_run_uses_the_defaults()
        {
            Assert.AreEqual(1.0, ThreadDungeonRun.ClampGolemPickScale(double.NaN));
            Assert.AreEqual(0.0, ThreadDungeonRun.ClampGolemPickScale(-1));
            Assert.AreEqual(3.0, ThreadDungeonRun.ClampGolemPickScale(4));
            Assert.AreEqual(1.5, ThreadDungeonRun.ClampGolemHealthMult(double.NaN));
            Assert.AreEqual(1.0, ThreadDungeonRun.ClampGolemHealthMult(0.5));
            Assert.AreEqual(10.0, ThreadDungeonRun.ClampGolemHealthMult(11));
            Assert.AreEqual(4.0, ThreadDungeonRun.ClampGolemAuraSeconds(double.NaN));
            Assert.AreEqual(1.0, ThreadDungeonRun.ClampGolemAuraSeconds(0));
            Assert.AreEqual(60.0, ThreadDungeonRun.ClampGolemAuraSeconds(61));

            var run = OutdoorRun(new[] { Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor") });
            Assert.AreEqual((1.0, 1.5, 4.0), (run.GolemPickScale, run.GolemHealthMult, run.GolemAuraSeconds));
        }

        [TestMethod]
        public void The_type_is_registered_and_the_tunables_are_read_and_stamped_by_TryStart()
        {
            var type = new GolemEncounterType();
            Assert.AreEqual("golem", type.Kind);
            Assert.IsInstanceOfType(type, typeof(IThreadEncounterUsable));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var init = PooledLootSourceText.MethodBody(src, "public static void Initialize()");
            var register = init.IndexOf("ThreadEncounterTypes.Register(new GolemEncounterType());", StringComparison.Ordinal);
            Assert.IsTrue(register >= 0 && register < init.IndexOf("Store = store;", StringComparison.Ordinal), "registered before the store is published");

            var pm = PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs");
            var metadata = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv").Replace("\r\n", "\n");
            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv").Replace("\r\n", "\n");

            foreach (var (key, clamp, def, prop, tsv) in new[]
            {
                ("dynamic_dungeons_golem_pick_scale", "ClampGolemPickScale", "DefaultGolemPickScale", "GolemPickScale", "1"),
                ("dynamic_dungeons_golem_health_mult", "ClampGolemHealthMult", "DefaultGolemHealthMult", "GolemHealthMult", "1.5"),
                ("dynamic_dungeons_golem_aura_seconds", "ClampGolemAuraSeconds", "DefaultGolemAuraSeconds", "GolemAuraSeconds", "4"),
            })
            {
                var local = char.ToLowerInvariant(prop[0]) + prop.Substring(1);
                StringAssert.Contains(src, $"var {local} = ThreadDungeonRun.{clamp}(PropertyManager.GetDouble(\"{key}\", ThreadDungeonRun.{def}).Item);");
                StringAssert.Contains(src, $"{prop} = {local},");
                StringAssert.Contains(pm, $"(\"{key}\", new Property<double>(ACE.Server.ThreadDungeons.ThreadDungeonRun.{def},");
                StringAssert.Contains(metadata, $"\n{key}\tthreads\tgolem\tfork\tno\n");
                StringAssert.Contains(defaults, $"\n{key}\tdouble\t{tsv}\t{tsv}\t\n");
            }

            // The encounter reads the stamped values, never PropertyManager, at use time.
            var golem = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            Assert.IsFalse(golem.Contains("PropertyManager"), "GolemEncounterType reads no PropertyManager key");
            StringAssert.Contains(golem, "Compute(state.Counts, run.GolemPickScale)");
            StringAssert.Contains(golem, "run.GolemHealthMult");
            StringAssert.Contains(golem, "ThreadDungeonRun.ClampGolemAuraSeconds(instance.Run.GolemAuraSeconds)");
        }

        [TestMethod]
        public void The_awakened_golem_is_shaped_as_an_excluded_elite_with_every_buff_before_EnterWorld()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var awaken = PooledLootSourceText.MethodBody(src, "private static void AwakenCore(ThreadEncounterInstance instance, GolemState state, Action<Creature> onCreated)");

            var entry = awaken.IndexOf("new DungeonSpawnPlanEntry(state.Config.GolemWcid, DungeonRole.Elite, point, run.Spec?.Level ?? 0)", StringComparison.Ordinal);
            var shape = awaken.IndexOf("request.ShapeCreature(golem, entry,", StringComparison.Ordinal);
            var excluded = awaken.IndexOf("ExcludedFromClear = true,", StringComparison.Ordinal);
            var buffs = awaken.IndexOf("ApplyBuffs(golem, effect,", StringComparison.Ordinal);
            var bind = awaken.IndexOf("request.BindToRunPerimeter(golem, point);", StringComparison.Ordinal);
            var destroyDormant = awaken.IndexOf("dormant.Destroy()", StringComparison.Ordinal);
            var enter = awaken.IndexOf("golem.EnterWorld()", StringComparison.Ordinal);
            var burst = awaken.IndexOf("golem.PlayParticleEffect(PlayScript.Create, golem.Guid);", StringComparison.Ordinal);

            Assert.IsTrue(entry >= 0 && entry < shape && shape < excluded && excluded < buffs && buffs < bind && bind < destroyDormant && destroyDormant < enter && enter < burst, awaken);
        }

        [TestMethod]
        public void A_throwing_awakening_spends_the_encounter_instead_of_stranding_it_in_shaping()
        {
            var savedCreate = GolemEncounterType.CreateObject;

            try
            {
                GolemEncounterType.CreateObject = _ => throw new InvalidOperationException("factory threw");

                var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor");
                var run = OutdoorRun(new[] { point });
                var type = new GolemEncounterType();
                var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("golem", kind: "golem"), Candidate(point, outdoor: true), 7), type);
                var state = new GolemState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new GolemConfig { Rounds = 1 }, null);
                instance.State = state;

                Assert.AreEqual(GolemUseOutcome.Begun, state.TryBegin(false, true));
                var gen = state.OpenRound(0, new[] { GolemBuff.Might }, new[] { WispA });
                Assert.IsTrue(state.TryPick(gen, GolemBuff.Might, out _, out var last));
                Assert.IsTrue(last);
                Assert.AreEqual(GolemPhase.Shaping, state.Phase, "waiting to awaken");

                GolemEncounterType.Awaken(instance, state);

                Assert.AreEqual(GolemPhase.Done, state.Phase, "the throw spent the encounter");
                Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("awakening failed")), string.Join("\n", run.EncounterOutcomes));
                Assert.AreEqual(GolemUseOutcome.Spent, state.TryBegin(false, true));
            }
            finally
            {
                GolemEncounterType.CreateObject = savedCreate;
            }
        }

        [TestMethod]
        public void A_throwing_wisp_placement_still_opens_the_round_for_its_timeout()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var start = PooledLootSourceText.MethodBody(src, "private static void StartRound(ThreadEncounterInstance instance, GolemState state, int round)");

            var draw = start.IndexOf("var drawn = buffOrder.Take(WispsPerRound).ToArray();", StringComparison.Ordinal);
            var guard = start.IndexOf("try", draw, StringComparison.Ordinal);
            var place = start.IndexOf("TryPlaceWisp(instance, state, request, state.Slots[pos], buff, out var wisp)", StringComparison.Ordinal);
            var caught = start.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            var open = start.IndexOf("state.OpenRound(round, offered, guids)", StringComparison.Ordinal);
            var timer = start.IndexOf("OnGolemQueue(state, seconds,", StringComparison.Ordinal);
            var line = start.IndexOf("SendAll(run, round == 0 ? BeginLine(guids.Count) : NextRoundLine(guids.Count));", StringComparison.Ordinal);

            Assert.IsTrue(draw >= 0 && draw < guard && guard < place && place < caught && caught < open && open < timer && timer < line, start);
        }

        [TestMethod]
        public void A_round_short_of_two_wisps_shows_none_and_the_golem_draws_at_once()
        {
            // Owner ruling 2026-11-11: a round never shows fewer than two wisps.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var start = PooledLootSourceText.MethodBody(src, "private static void StartRound(ThreadEncounterInstance instance, GolemState state, int round)");

            // The decision itself (code review 2026-11-11: pure, so a weakened threshold fails here).
            Assert.IsTrue(GolemEncounterType.IsShortRound(0));
            Assert.IsTrue(GolemEncounterType.IsShortRound(1));
            Assert.IsFalse(GolemEncounterType.IsShortRound(2));
            Assert.IsFalse(GolemEncounterType.IsShortRound(3));

            // The two guarded blocks, exactly (whitespace-normalised), so neither can lose its guard or its return.
            string Flat(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
            var flat = Flat(start);
            StringAssert.Contains(flat, "var shortRound = IsShortRound(placedBuffs.Count); if (shortRound) { ClearWisps(state); guids.Clear(); } var offered = shortRound ? drawn.ToList() : placedBuffs;");
            StringAssert.Contains(flat, "if (generation == 0) { ClearWisps(state); return; } if (shortRound) { var auto = offered[TimeoutChoice(request.Seed, round, offered.Count)]; Resolve(instance, state, generation, auto, null, NoWispLine(auto)); return; }");

            // Every round announces itself: the short one through Resolve's NoWispLine (any round, not only the first),
            // the others through the count-aware line, which is unconditional.
            StringAssert.Contains(flat, "OnGolemQueue(state, seconds, () => {");
            StringAssert.Contains(flat, "}); SendAll(run, round == 0 ? BeginLine(guids.Count) : NextRoundLine(guids.Count));");

            var caught = start.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            var shortTest = start.IndexOf("var shortRound = IsShortRound(placedBuffs.Count);", StringComparison.Ordinal);
            var clear = start.IndexOf("if (shortRound)\r\n            {\r\n                ClearWisps(state);\r\n                guids.Clear();", StringComparison.Ordinal);
            if (clear < 0)
                clear = start.IndexOf("if (shortRound)\n            {\n                ClearWisps(state);\n                guids.Clear();", StringComparison.Ordinal);
            var offered = start.IndexOf("var offered = shortRound ? drawn.ToList() : placedBuffs;", StringComparison.Ordinal);
            var open = start.IndexOf("state.OpenRound(round, offered, guids)", StringComparison.Ordinal);
            var auto = start.IndexOf("var auto = offered[TimeoutChoice(request.Seed, round, offered.Count)];", StringComparison.Ordinal);
            var resolve = start.IndexOf("Resolve(instance, state, generation, auto, null, NoWispLine(auto));", StringComparison.Ordinal);
            var shown = start.IndexOf("SendAll(run, round == 0 ? BeginLine(guids.Count) : NextRoundLine(guids.Count));", StringComparison.Ordinal);
            var timer = start.IndexOf("OnGolemQueue(state, seconds,", StringComparison.Ordinal);

            Assert.IsTrue(caught >= 0 && caught < shortTest && shortTest < clear && clear < offered && offered < open && open < auto && auto < resolve && resolve < timer && timer < shown, start);

            // The begin line waits for the first round, so it never promises wisps that did not settle.
            var use = PooledLootSourceText.MethodBody(src, "private static void UseDormant(ThreadEncounterInstance instance, GolemState state, Player player)");
            Assert.IsFalse(use.Contains("BeginLine"), "UseDormant sends no begin line");
            Assert.AreEqual(2, GolemEncounterType.MinWispsPerRound);
        }

        [TestMethod]
        public void Mending_writes_the_vitals_regen_rate_not_only_the_property()
        {
            // CreatureVital copies HealthRate into Health.RegenRate once at construction and regen reads only the vital,
            // so a property-only write is inert. Pinned in source: a Creature cannot be built in this harness.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var apply = PooledLootSourceText.MethodBody(src, "internal static void ApplyBuffs(Creature golem, GolemBuffEffect effect, uint observedMaxNonBoss, double healthMult)");

            var guard = apply.IndexOf("if (rate > 0)", StringComparison.Ordinal);
            var scaled = apply.IndexOf("var scaled = rate * effect.HealthRateMult;", StringComparison.Ordinal);
            var property = apply.IndexOf("golem.SetProperty(PropertyFloat.HealthRate, scaled);", StringComparison.Ordinal);
            var vital = apply.IndexOf("golem.Health.RegenRate = scaled;", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && guard < scaled && scaled < property && property < vital, apply);
        }

        [TestMethod]
        public void A_speed_overlay_warns_when_monster_effects_are_off()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var apply = PooledLootSourceText.MethodBody(src, "internal static void ApplyBuffs(Creature golem, GolemBuffEffect effect, uint observedMaxNonBoss, double healthMult)");

            var off = apply.IndexOf("if (overlay.Length > 0 && !MonsterEffectCaps.Enabled)", StringComparison.Ordinal);
            var attach = apply.IndexOf("golem.ApplyMonsterEffectOverlay(overlay)", StringComparison.Ordinal);
            Assert.IsTrue(off >= 0 && off < attach, apply);
            StringAssert.Contains(apply, "monster_effects_enabled is false");
        }

        [TestMethod]
        public void A_wisp_pick_reads_its_buff_and_generation_under_one_lock()
        {
            var (state, _) = Shaping();

            Assert.IsFalse(state.TryPickWisp(0x80009999u, out _, out _, out _), "not a wisp of this round");
            Assert.IsTrue(state.TryPickWisp(WispB, out var buff, out var pickedRound, out var last));
            Assert.AreEqual(GolemBuff.Frenzy, buff);
            Assert.AreEqual(0, pickedRound);
            Assert.IsFalse(last);
            Assert.IsFalse(state.TryPickWisp(WispA, out _, out _, out _), "the round is spent");
            CollectionAssert.AreEqual(new[] { GolemBuff.Frenzy }, state.Picked.ToArray());

            // A stale wisp of round 1 cannot pick in round 2.
            var gen2 = state.OpenRound(1, new[] { GolemBuff.Ruin, GolemBuff.Arcana, GolemBuff.Mending }, new[] { 0x80001011u, 0x80001012u, 0x80001013u });
            Assert.AreNotEqual(0, gen2);
            Assert.IsFalse(state.TryPickWisp(WispC, out _, out _, out _), "a round 1 wisp in round 2");

            for (var trial = 0; trial < 30; trial++)
            {
                var (s, _) = Shaping();
                var wins = new ConcurrentBag<GolemBuff>();
                using var go = new ManualResetEventSlim(false);
                var tasks = new[] { WispA, WispB, WispC, WispA, WispB, WispC }
                    .Select(g => Task.Run(() => { go.Wait(); if (s.TryPickWisp(g, out var b, out _, out _)) wins.Add(b); })).ToArray();
                go.Set();
                Task.WaitAll(tasks);
                Assert.AreEqual(1, wins.Count, $"trial {trial}");
                Assert.AreEqual(1, s.Counts.Sum());
            }

            var srcText = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var use = PooledLootSourceText.MethodBody(srcText, "private static void UseWisp(ThreadEncounterInstance instance, GolemState state, Player player, uint wispGuid)");
            StringAssert.Contains(use, "state.TryPickWisp(wispGuid, out var buff, out var pickedRound, out var wasLast)");
            Assert.IsFalse(use.Contains("state.Generation"), "the generation is read inside the lock, never beside it");
        }

        [TestMethod]
        public void The_shrine_guardian_comment_sits_directly_above_the_bonus_kill_route()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs").Replace("\r\n", "\n");
            StringAssert.Contains(src, "// clear - no RecordKill, no armed notice, no progress line. Its pooled loot was already banked by Die.\n            if (TryRecordBonusKill(run, creature))");
            StringAssert.Contains(src, "// it takes the bonus-kill route like any encounter creature.\n            ThreadEncounterPass.OnTrackedCreatureDied(creature);");
        }

        [TestMethod]
        public void The_announcement_prefers_the_supplied_line_then_the_pick_then_the_timeout()
        {
            // Review R3 (2026-11-11): a short round's NoWispLine must win over the timeout line it would otherwise get.
            var buff = (GolemBuff)0;
            var noWisp = GolemEncounterType.NoWispLine(buff);

            Assert.AreEqual(noWisp, GolemEncounterType.AnnounceText(noWisp, null, buff, 2, 3));
            Assert.AreEqual(noWisp, GolemEncounterType.AnnounceText(noWisp, "Tester", buff, 2, 3));
            Assert.AreEqual(GolemEncounterType.TimeoutLine(buff, 2, 3), GolemEncounterType.AnnounceText(null, null, buff, 2, 3));
            Assert.AreEqual(GolemEncounterType.PickLine("Tester", buff, 2, 3), GolemEncounterType.AnnounceText(null, "Tester", buff, 2, 3));
            Assert.AreNotEqual(noWisp, GolemEncounterType.TimeoutLine(buff, 2, 3), "control: the two lines differ");

            // Announce sends exactly that text, with the round made 1-based.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static void Announce(ThreadEncounterInstance instance, GolemState state, GolemBuff buff, int pickedRound, bool wasLast, string pickerName, string line = null)");
            StringAssert.Contains(body, "SendAll(run, AnnounceText(line, pickerName, buff, pickedRound + 1, total));");
            Assert.AreEqual(1, body.Split("SendAll(").Length - 1, "Announce sends one line");
        }
    }
}
