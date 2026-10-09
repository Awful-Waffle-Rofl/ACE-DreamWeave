using System.Collections.Generic;
using System.Linq;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ThreadPuzzleSitePicker: the pure, seeded choice of a run's puzzle sites and types. Sites are built as the
    /// store leaves them after validation (Kind, Types and GateModel.Kind already parsed), with no store and no world.
    /// </summary>
    [TestClass]
    public class ThreadPuzzleSitePickerTests
    {
        private static readonly PuzzleGateType[] AllTypes = { PuzzleGateType.Sigil, PuzzleGateType.Beam, PuzzleGateType.Odd, PuzzleGateType.Shuffle };

        private static int nextSite;

        /// <summary>Every fixture site gets its own anchor 40 m from the last, so the spatial exclusion never interferes unless a test asks for it.</summary>
        private static PuzzleSiteDef Site(string id, int maxN, PuzzleSiteKind kind = PuzzleSiteKind.Gate, PuzzleGateModelKind model = PuzzleGateModelKind.Door,
            int spots = 2, params PuzzleGateType[] types)
        {
            var x = 40f * System.Threading.Interlocked.Increment(ref nextSite);

            return new PuzzleSiteDef
            {
                Id = id,
                Kind = kind,
                Anchor = new PuzzleSitePointDef { Cell = 0x0150018A, X = x, Y = 10, Z = 0 },
                MaxN = maxN,
                Types = (types.Length == 0 ? AllTypes : types).ToList(),
                GateModel = new PuzzleGateModelDef { Kind = model, Wcid = model == PuzzleGateModelKind.Resident ? 0u : 1006850u, Scale = 1f, Panels = 1, ResidentGuid = 0x70000001 },
                ShuffleSpots = Enumerable.Range(0, spots).Select(i => new PuzzleSitePointDef { Cell = 0x0150018A, X = i, Y = 0, Z = 0 }).ToList(),
            };
        }

        private static List<PuzzleSiteDef> ManySites() => Enumerable.Range(0, 8).Select(i => Site("g" + i, 5)).ToList();

        [TestMethod]
        public void Same_seed_same_picks_and_a_different_seed_moves_them()
        {
            var sites = ManySites();

            var a = ThreadPuzzleSitePicker.Pick(sites, 3, false, 4242);
            var b = ThreadPuzzleSitePicker.Pick(sites, 3, false, 4242);

            Assert.AreEqual(3, a.Count);
            CollectionAssert.AreEqual(a.Select(p => (p.Site.Id, p.Type, p.N, p.Seed)).ToList(), b.Select(p => (p.Site.Id, p.Type, p.N, p.Seed)).ToList(), "deterministic per seed");

            // Discriminating control: across a spread of seeds the picks are not all identical, so the seed is
            // actually consumed (a picker that ignored it would pass the equality above trivially).
            var distinct = Enumerable.Range(1, 20)
                .Select(s => string.Join("|", ThreadPuzzleSitePicker.Pick(sites, 3, false, s).Select(p => p.Site.Id + ":" + p.Type)))
                .Distinct().Count();
            Assert.IsTrue(distinct > 1, "different run seeds must be able to pick differently");
        }

        [TestMethod]
        public void No_type_repeats_within_a_run_including_the_reward()
        {
            var sites = ManySites();
            sites.Add(Site("r1", 5, PuzzleSiteKind.Reward));

            for (var seed = 1; seed <= 200; seed++)
            {
                var picks = ThreadPuzzleSitePicker.Pick(sites, 8, true, seed);

                Assert.AreEqual(4, picks.Count, $"seed {seed}: 4 types, so at most 4 puzzles however many sites (3 gates + 1 reward, or 4 gates)");
                Assert.AreEqual(picks.Count, picks.Select(p => p.Type).Distinct().Count(), $"seed {seed}: a type repeated");
                Assert.AreEqual(picks.Count, picks.Select(p => p.Site.Id).Distinct().Count(), $"seed {seed}: a site was used twice");
            }
        }

        [TestMethod]
        public void A_site_below_maxN_4_never_gets_sigil_or_odd_and_lever_counts_respect_maxN()
        {
            var narrow = Site("narrow", 3);

            CollectionAssert.AreEquivalent(new[] { PuzzleGateType.Beam, PuzzleGateType.Shuffle }, ThreadPuzzleSitePicker.EligibleTypes(narrow));

            var four = Site("four", 4, types: new[] { PuzzleGateType.Sigil, PuzzleGateType.Odd });

            for (var seed = 1; seed <= 100; seed++)
            {
                foreach (var pick in ThreadPuzzleSitePicker.Pick(new[] { narrow }, 1, false, seed))
                {
                    Assert.IsTrue(pick.Type == PuzzleGateType.Beam || pick.Type == PuzzleGateType.Shuffle);
                    Assert.IsTrue(pick.N <= 3, $"n {pick.N} over maxN 3");
                }

                foreach (var pick in ThreadPuzzleSitePicker.Pick(new[] { four }, 1, false, seed))
                    Assert.AreEqual(4, pick.N, "maxN 4 caps a pick type at 4 levers");
            }

            // maxN 5 really does draw both 4 and 5 (control: the cap above is the maxN, not a constant 4).
            var counts = Enumerable.Range(0, 64).Select(r => ThreadPuzzleSitePicker.LeverCount(PuzzleGateType.Sigil, 5, (uint)r)).Distinct().OrderBy(n => n).ToList();
            CollectionAssert.AreEqual(new[] { 4, 5 }, counts);

            Assert.AreEqual(2, ThreadPuzzleSitePicker.LeverCount(PuzzleGateType.Beam, 2, 0));
            Assert.AreEqual(ThreadPuzzleSitePicker.BeamN, ThreadPuzzleSitePicker.LeverCount(PuzzleGateType.Beam, 5, 0));
            Assert.AreEqual(1, ThreadPuzzleSitePicker.LeverCount(PuzzleGateType.Shuffle, 5, 0));
        }

        [TestMethod]
        public void Shuffle_needs_two_spots_and_a_site_with_nothing_eligible_is_skipped()
        {
            var oneSpot = Site("one", 1, spots: 1, types: new[] { PuzzleGateType.Shuffle, PuzzleGateType.Sigil });

            Assert.AreEqual(0, ThreadPuzzleSitePicker.EligibleTypes(oneSpot).Count, "maxN 1 holds no sigil, one spot holds no shuffle");
            Assert.AreEqual(0, ThreadPuzzleSitePicker.Pick(new[] { oneSpot }, 1, false, 7).Count);
        }

        [TestMethod]
        public void Resident_sites_are_never_picked()
        {
            var resident = Site("res", 5, model: PuzzleGateModelKind.Resident);
            var door = Site("door", 5);

            Assert.IsFalse(ThreadPuzzleSitePicker.IsPlaceable(resident));

            for (var seed = 1; seed <= 50; seed++)
            {
                var picks = ThreadPuzzleSitePicker.Pick(new[] { resident, door }, 2, false, seed);
                Assert.AreEqual(1, picks.Count);
                Assert.AreEqual("door", picks[0].Site.Id);
            }
        }

        [TestMethod]
        public void The_reward_site_is_picked_only_when_wanted_and_never_counts_as_a_gate()
        {
            var sites = new List<PuzzleSiteDef> { Site("g", 5), Site("r", 5, PuzzleSiteKind.Reward) };

            var without = ThreadPuzzleSitePicker.Pick(sites, 1, false, 9);
            Assert.AreEqual(1, without.Count);
            Assert.IsFalse(without[0].IsReward);

            var with = ThreadPuzzleSitePicker.Pick(sites, 1, true, 9);
            Assert.AreEqual(2, with.Count);
            Assert.AreEqual(1, with.Count(p => p.IsReward));
            Assert.AreEqual("r", with.Single(p => p.IsReward).Site.Id);

            // A gate count of 0 still allows the reward scene.
            Assert.AreEqual(1, ThreadPuzzleSitePicker.Pick(sites, 0, true, 9).Count(p => p.IsReward));
        }

        // ---- spatial exclusion (review F3) ----------------------------------------------------------------

        private const string ShippedSitesPath = "Content/dungeons/dynamic/puzzle-gates.json";

        /// <summary>The shipped puzzle-gates.json, parsed by the store's own loader, every dungeon given a boss anchor.</summary>
        internal static Dictionary<string, IReadOnlyList<PuzzleSiteDef>> ShippedSites() => ShippedSites(out _);

        internal static Dictionary<string, IReadOnlyList<PuzzleSiteDef>> ShippedSites(out List<string> diagnostics)
        {
            var json = PooledLootSourceText.Read(ShippedSitesPath);
            var dungeons = new Dictionary<string, DungeonEntryDef>();

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                foreach (var d in doc.RootElement.GetProperty("dungeons").EnumerateObject())
                    dungeons[d.Name] = new DungeonEntryDef { Id = d.Name, BossAnchor = new DungeonSpawnPointDef() };
            }

            diagnostics = new List<string>();
            var sites = ThreadDungeonStore.ParsePuzzleSites(json, dungeons, diagnostics);
            Assert.IsTrue(sites.Count > 0, "the shipped file parsed: " + string.Join("; ", diagnostics));
            return sites;
        }

        [TestMethod]
        public void The_exclusion_radius_is_derived_from_the_prompt_radius_and_the_widest_lever_row()
        {
            Assert.AreEqual(16f, ThreadPuzzleSitePicker.SiteExclusionMeters, 1e-5f);
            Assert.AreEqual(PuzzleGateTunables.PromptRadius + PuzzleGateTunables.LeverSpacing * (PuzzleGateTunables.MaxN - 1) / 2f + 1f, ThreadPuzzleSitePicker.SiteExclusionMeters, 1e-5f);
        }

        [TestMethod]
        public void Two_adjacent_shipped_sites_are_never_picked_together()
        {
            var filos = ShippedSites()["filos_doom"];
            var s1 = filos.Single(s => s.Id == "s1");
            var s2 = filos.Single(s => s.Id == "s2");

            Assert.IsTrue(ThreadPuzzleSitePicker.TooClose(s1, s2), "s1 and s2 share a cell 3.3 m apart");

            for (var seed = 1; seed <= 100; seed++)
            {
                var picks = ThreadPuzzleSitePicker.Pick(new[] { s1, s2 }, 2, false, seed);
                Assert.AreEqual(1, picks.Count, $"seed {seed}: adjacent sites must not both be picked");
            }
        }

        [TestMethod]
        public void No_shipped_dungeon_ever_gets_two_puzzles_within_the_exclusion()
        {
            var rewardPicks = 0;

            foreach (var (dungeon, sites) in ShippedSites())
            {
                for (var seed = 1; seed <= 25; seed++)
                {
                    // Production shape (1 gate + the reward) and the stress shape (every gate the cap allows + the reward).
                    foreach (var gateCount in new[] { 1, ThreadPuzzleSitePicker.MaxGatesPerRun })
                    {
                        var picks = ThreadPuzzleSitePicker.Pick(sites, gateCount, true, seed);
                        rewardPicks += picks.Count(p => p.IsReward);

                        for (var i = 0; i < picks.Count; i++)
                            for (var j = i + 1; j < picks.Count; j++)
                                Assert.IsFalse(ThreadPuzzleSitePicker.TooClose(picks[i].Site, picks[j].Site), $"{dungeon} seed {seed} gates {gateCount}: {picks[i].Site.Id} and {picks[j].Site.Id}");
                    }
                }
            }

            // The reward sites take part, so gate-vs-reward spacing is actually exercised, not vacuously true.
            Assert.IsTrue(rewardPicks > 0, "no reward site was ever picked from the shipped file");
        }

        [TestMethod]
        public void A_reward_site_next_to_a_picked_gate_is_skipped_and_a_different_level_is_not_excluded()
        {
            var gate = Site("g", 5, types: PuzzleGateType.Beam);
            var reward = Site("r", 5, PuzzleSiteKind.Reward, types: PuzzleGateType.Sigil);
            reward.Anchor.X = gate.Anchor.X + 5f;

            Assert.AreEqual(0, ThreadPuzzleSitePicker.Pick(new[] { gate, reward }, 1, true, 3).Count(p => p.IsReward), "reward 5 m from the gate is excluded");

            reward.Anchor.Z = gate.Anchor.Z + ThreadPuzzleSitePicker.SameLevelHeight + 0.5f;
            Assert.AreEqual(1, ThreadPuzzleSitePicker.Pick(new[] { gate, reward }, 1, true, 3).Count(p => p.IsReward), "a level above is not excluded");
        }

        [TestMethod]
        public void Segment_distance_is_exact_for_parallel_crossing_and_end_to_end_cases()
        {
            System.Numerics.Vector2 V(float x, float y) => new System.Numerics.Vector2(x, y);

            Assert.AreEqual(3f, ThreadPuzzleSitePicker.SegmentDistance(V(0, 0), V(0, 7), V(3, 0), V(3, 7)), 1e-5f, "parallel");
            Assert.AreEqual(0f, ThreadPuzzleSitePicker.SegmentDistance(V(-1, 0), V(1, 0), V(0, -1), V(0, 1)), 1e-5f, "crossing");
            Assert.AreEqual(5f, ThreadPuzzleSitePicker.SegmentDistance(V(0, 0), V(0, 7), V(0, 12), V(0, 20)), 1e-5f, "end to end");
        }

        [TestMethod]
        public void Every_pick_builds_valid_options_with_its_lever_count()
        {
            var sites = ManySites();

            for (var seed = 1; seed <= 50; seed++)
            {
                foreach (var pick in ThreadPuzzleSitePicker.Pick(sites, 4, false, seed))
                {
                    Assert.IsTrue(ThreadPuzzleSitePicker.TryBuildOptions(pick, out var options, out var error), error);
                    Assert.AreEqual(pick.Type, options.Type);
                    Assert.AreEqual(0u, options.AmbushWcid, "run options never carry an ambush");

                    if (pick.Type != PuzzleGateType.Shuffle)
                        Assert.AreEqual(pick.N, options.N);
                }
            }
        }
    }
}
