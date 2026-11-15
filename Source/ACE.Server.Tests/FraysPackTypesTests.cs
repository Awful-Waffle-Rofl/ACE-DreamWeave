using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Brute and swarm pack points (pack_types): equal health, an alternating ring, one Gilded of each.</summary>
    [TestClass]
    public class FraysPackTypesTests
    {
        private Func<string, string> previousSource;
        private Dictionary<string, string> values;

        [TestInitialize]
        public void Init()
        {
            previousSource = FraysSettings.StringSource;
            values = new Dictionary<string, string>();
            FraysSettings.StringSource = p => values.TryGetValue(p, out var v) ? v : null;
            FraysSettings.ResetCacheForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FraysSettings.StringSource = previousSource;
            FraysSettings.ResetCacheForTest();
        }

        private static readonly Vector2 Centre = new Vector2(FraysRules.ZoneCentre, FraysRules.ZoneCentre);

        private static double Flat(Vector3 a, Vector3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private static IEnumerable<FraysFireDef> SoloCamps => FraysDefs.All.Where(f => f.Outdoor && !f.Group);

        private static FraysCampLayout LayoutOf(FraysFireDef d)
        {
            var radius = FraysSettings.Number(FraysSettings.ZoneRadius) - 18;
            var gap = FraysSettings.Number(FraysSettings.LayoutGapAggro) * FraysSettings.Number(FraysSettings.AggroRange);
            var points = d.SpawnPoints.Select(p => FraysRules.InsideCircle(p.Pos, radius)).ToList();
            return FraysLayout.Build(d.Id, points, d.FirePos, Centre, radius, gap, FraysSettings.Int(FraysSettings.RichPoints), FraysSettings.Toggle(FraysSettings.ShrineEnabled),
                FraysSettings.Int(FraysSettings.LayoutPoints), FraysSettings.Toggle(FraysSettings.LayoutEdges), FraysSettings.Toggle(FraysSettings.PackTypes));
        }

        [TestMethod]
        public void BruteAndSwarmPacks_HoldTheSameHealth_AtEveryHeat()
        {
            for (var heat = 0; heat <= 25; heat++)
            {
                var swarm = FraysRules.TypedPack(FraysPackType.Swarm, heat, 0, out var swarmEach);
                var brute = FraysRules.TypedPack(FraysPackType.Brute, heat, 0, out var bruteEach);

                Assert.IsTrue(swarm.All(r => r == FraysRole.Swarm) && swarm.Count >= 6 && swarm.Count <= 8, $"Heat {heat}: 6 to 8 swarm creatures");
                Assert.IsTrue(brute.All(r => r == FraysRole.Standard) && brute.Count >= 1 && brute.Count <= 2, $"Heat {heat}: 1 or 2 brutes");
                Assert.AreEqual(1, swarmEach);
                Assert.AreEqual(swarm.Count * 0.4, brute.Count * bruteEach, 1e-9, $"Heat {heat}: the same total health");
                Assert.AreEqual(FraysRules.TypedWorth(heat, 0), brute.Count * bruteEach, 1e-9);
            }

            Assert.AreEqual(6, FraysRules.SwarmCount(0));
            Assert.AreEqual(7, FraysRules.SwarmCount(10));
            Assert.AreEqual(8, FraysRules.SwarmCount(20));
            Assert.AreEqual(1, FraysRules.BruteCount(9));
            Assert.AreEqual(2, FraysRules.BruteCount(10));

            // A flaring quarter (one more swarm creature's worth): the swarm pack gets a creature, the brute pack the health.
            var hotSwarm = FraysRules.TypedPack(FraysPackType.Swarm, 0, 1, out _);
            FraysRules.TypedPack(FraysPackType.Brute, 0, 1, out var hotEach);
            Assert.AreEqual(7, hotSwarm.Count);
            Assert.AreEqual(2.8, hotEach, 1e-9);
        }

        [TestMethod]
        public void SwarmHits_AreFortyPercent_BrutesHitByTheirShare()
        {
            Assert.AreEqual(0.4, FraysRules.HitShare(FraysRole.Swarm, 1), 1e-9);
            Assert.AreEqual(2.4, FraysRules.HitShare(FraysRole.Standard, 2.4), 1e-9);
            Assert.AreEqual(1, FraysRules.HitShare(FraysRole.Standard, 1), 1e-9, "an untyped creature hits as before");

            // The same total hits a round for a pull of each type.
            FraysRules.TypedPack(FraysPackType.Brute, 0, 0, out var each);
            Assert.AreEqual(6 * FraysRules.HitShare(FraysRole.Swarm, 1), 1 * FraysRules.HitShare(FraysRole.Standard, each), 1e-9);

            values["frays_brute_hit_by_share"] = "false";
            FraysSettings.ResetCacheForTest();
            Assert.AreEqual(1, FraysRules.HitShare(FraysRole.Standard, 2.4), 1e-9);
        }

        [TestMethod]
        public void BrutePull_CostsTheSameDamage_AsASwarmPull_KilledOneAtATime()
        {
            foreach (var heat in new[] { 0, 10, 20 })
            {
                var swarms = FraysRules.TypedPack(FraysPackType.Swarm, heat, 0, out _).Count;
                var brutes = FraysRules.TypedPack(FraysPackType.Brute, heat, 0, out var each).Count;
                var swarmHit = FraysRules.HitShare(FraysRole.Swarm, 1, heat);
                var bruteHit = FraysRules.HitShare(FraysRole.Standard, each, heat);

                // Hits' worth over the fight: each creature alive hits while the ones before it die (same total health).
                var swarmDamage = swarmHit * 0.4 * swarms * (swarms + 1) / 2.0;
                var bruteDamage = bruteHit * each * brutes * (brutes + 1) / 2.0;
                Assert.AreEqual(swarmDamage, bruteDamage, 1e-9, $"Heat {heat}");
            }

            Assert.AreEqual(1.4, FraysRules.HitShare(FraysRole.Standard, 2.4, 0), 1e-9, "a lone brute at Heat 0");
            Assert.AreEqual(1, FraysRules.HitShare(FraysRole.Standard, 1, 5), 1e-9, "an untyped creature hits as before");
        }

        [TestMethod]
        public void LootRollExact_DropsTheShare_OnAverage()
        {
            foreach (var per in new[] { 0.4, 1.0, 1.2, 1.25, 2.6, 5.15 })
            {
                double sum = 0;
                const int n = 1000;
                for (var i = 0; i < n; i++)
                {
                    var (chance, min, max) = FraysRules.LootRollExact(per, (i + 0.5) / n);
                    Assert.AreEqual(min, max);
                    sum += chance / 100.0 * min;
                }
                Assert.AreEqual(per, sum / n, 0.002, $"share {per}");
            }
        }

        [TestMethod]
        public void EveryCamp_AlternatesBruteAndSwarm_RoundTheRing_WithTwoGildedOfEach()
        {
            foreach (var d in SoloCamps)
            {
                var l = LayoutOf(d);
                var plain = Enumerable.Range(0, l.Roles.Count).Where(i => l.RoleOf(i) == FraysPointRole.Pack).ToList();

                Assert.IsTrue(plain.Count > 0 && plain.Count % 2 == 0, $"{d.Id}: an even count of plain points ({plain.Count})");
                Assert.AreEqual(plain.Count / 2, plain.Count(i => l.TypeOf(i) == FraysPackType.Brute), $"{d.Id}: as many brute points as swarm");

                // Round the ring from the shrine, the types alternate.
                var start = FraysLayout.AngleOf(l.Positions[l.Shrine], Centre);
                var loop = plain.OrderBy(i => (FraysLayout.AngleOf(l.Positions[i], Centre) - start + 360) % 360).ToList();
                for (var k = 1; k < loop.Count; k++)
                    Assert.AreNotEqual(l.TypeOf(loop[k - 1]), l.TypeOf(loop[k]), $"{d.Id}: neighbours round the ring differ");

                Assert.AreEqual(4, l.Rich.Count, d.Id);
                CollectionAssert.AreEquivalent(new[] { FraysPackType.Brute, FraysPackType.Swarm, FraysPackType.Brute, FraysPackType.Swarm }, l.Rich.Select(l.TypeOf).ToList(), $"{d.Id}: two Gilded brute, two Gilded swarm");
                Assert.IsTrue(FraysLayout.Turn(l.Positions[l.Rich[0]], l.Positions[l.Rich[1]], Centre) >= 120 - 1e-6, $"{d.Id}: Gilded 120 degrees apart");
                Assert.AreEqual(FraysPackType.None, l.TypeOf(l.Shrine), "the shrine fields nothing");

                // 40 m between every two points that are used.
                var used = Enumerable.Range(0, l.Roles.Count).Where(i => l.RoleOf(i) != FraysPointRole.Off).ToList();
                foreach (var a in used)
                    foreach (var b in used.Where(b => b > a))
                        Assert.IsTrue(Flat(l.Positions[a], l.Positions[b]) >= 40 - 1e-3, $"{d.Id}: points {a} and {b}");
            }
        }

        [TestMethod]
        public void SameLayout_EveryBuild()
        {
            foreach (var d in SoloCamps.Take(3))
            {
                var a = LayoutOf(d);
                var b = LayoutOf(d);
                CollectionAssert.AreEqual(a.Types.ToList(), b.Types.ToList(), "each point always spawns the same type");
            }
        }

        [TestMethod]
        public void TypedRests_FieldTheSpawnTarget()
        {
            // 8 plain points at Heat 10: rests so the points field spawn_target_per_hour standard creatures' worth.
            var scale = FraysRules.TypedRestScale(8, 10, 0, false, 600);
            var rest = FraysRules.PointRest(FraysPointKind.Normal, false) * scale;
            Assert.AreEqual(600, 8 * FraysRules.TypedWorth(10, 0) / rest * 3600, 1e-6);
            Assert.AreEqual(1, FraysRules.TypedRestScale(0, 10, 0, false, 600));
        }

        [TestMethod]
        public void Tether_AndSplash_StayInsideTheLane()
        {
            // A pulled creature stays within the tether of its point; a neighbour's creature within 1.5 m of its point;
            // the nearest a primary target gets to a neighbour's creature is 40 - 15 - 1.5 m, past Multishot's 20 m.
            var gap = FraysSettings.Number(FraysSettings.LayoutGapAggro) * FraysSettings.Number(FraysSettings.AggroRange);
            var tether = FraysSettings.Number(FraysSettings.PackTether);
            Assert.AreEqual(15, tether);
            Assert.IsTrue(gap - tether - 1.5 > 20, "Multishot's 20 m from the primary never reaches the next pack");
            Assert.IsTrue(gap - tether > FraysSettings.Number(FraysSettings.AggroRange), "a tethered fight never enters the next pack's aggro");
        }
    }
}
