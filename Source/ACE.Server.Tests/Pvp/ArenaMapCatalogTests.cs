using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// ArenaMapCatalog: the two v1 maps, their spawn sets, and the pure seating rule. Distances and facings are
    /// computed here from the catalog data itself, so a mistyped coordinate or quaternion fails a test.
    /// </summary>
    [TestClass]
    public class ArenaMapCatalogTests
    {
        private const float CentreX = 30f;
        private const float CentreY = -25f;
        private const float MinSeparation = 8f;

        private static List<PvpTeam> Solos(int n) =>
            Enumerable.Range(0, n).Select(i => new PvpTeam(i, new List<PvpParticipant> { new PvpParticipant((uint)(100 + i), 1500) })).ToList();

        private static List<PvpTeam> Pairs() => new List<PvpTeam>
        {
            new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500), new PvpParticipant(2, 1500) }),
            new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500), new PvpParticipant(4, 1500) }),
        };

        private static double Distance(PvpSpawnPoint a, PvpSpawnPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        private static double MinPairwise(IReadOnlyList<PvpSpawnPoint> points)
        {
            var min = double.MaxValue;
            for (var i = 0; i < points.Count; i++)
                for (var j = i + 1; j < points.Count; j++)
                    min = Math.Min(min, Distance(points[i], points[j]));
            return min;
        }

        [TestMethod]
        public void Catalog_HasBothMapsInRealm1()
        {
            Assert.AreEqual(2, ArenaMapCatalog.All.Count);
            Assert.AreEqual("arena_0066", ArenaMapCatalog.Arena0066.MapKey);
            Assert.AreEqual(0x0066u, ArenaMapCatalog.Arena0066.LandblockId);
            Assert.AreEqual("arena_0067", ArenaMapCatalog.Arena0067.MapKey);
            Assert.AreEqual(0x0067u, ArenaMapCatalog.Arena0067.LandblockId);

            foreach (var map in ArenaMapCatalog.All)
                Assert.AreEqual((ushort)1, map.RealmId, map.MapKey);

            Assert.AreSame(ArenaMapCatalog.Arena0067, ArenaMapCatalog.Find("ARENA_0067"));
            Assert.IsNull(ArenaMapCatalog.Find("arena_0068"));
        }

        [TestMethod]
        public void EveryMap_CarriesEverySetAtTheAuthoredSize()
        {
            foreach (var map in ArenaMapCatalog.All)
            {
                Assert.AreEqual(2, map.SpawnPointsFor("1v1").Count, map.MapKey);
                Assert.AreEqual(4, map.SpawnPointsFor("2v2").Count, map.MapKey);
                Assert.AreEqual(15, map.SpawnPointsFor("ffa").Count, map.MapKey);
                Assert.AreEqual(0, map.SpawnPointsFor("koth").Count, map.MapKey);
                Assert.AreEqual(0, map.SpawnPointsFor(null).Count, map.MapKey);
            }
        }

        [TestMethod]
        public void EverySet_HasUniqueLabelsEnvCellsAndTheRetailZ()
        {
            foreach (var set in new[] { ArenaMapCatalog.OneVOneSet, ArenaMapCatalog.TwoVTwoSet, ArenaMapCatalog.FfaSet })
            {
                Assert.AreEqual(set.Count, set.Select(p => p.Label).Distinct().Count());

                foreach (var p in set)
                {
                    Assert.IsTrue(p.CellLow >= 0x0100, p.Label);
                    Assert.AreEqual(0.005f, p.Z, p.Label);
                }
            }
        }

        [TestMethod]
        public void EveryPoint_IsAUnitYawQuaternionFacingTheRoomCentre()
        {
            foreach (var set in new[] { ArenaMapCatalog.OneVOneSet, ArenaMapCatalog.TwoVTwoSet, ArenaMapCatalog.FfaSet })
            {
                foreach (var p in set)
                {
                    var norm = Math.Sqrt(p.RotationW * p.RotationW + p.RotationZ * p.RotationZ);
                    Assert.AreEqual(1.0, norm, 1e-5, $"{p.Label} is not a unit quaternion");

                    // Yaw about Z; a zero yaw faces +Y, and a positive yaw turns counter-clockwise (the retail 1v1
                    // point A: W=0, Z=1, a 180 degree turn, faces -Y from north of the centre).
                    var yaw = 2.0 * Math.Atan2(p.RotationZ, p.RotationW);
                    var fx = -Math.Sin(yaw);
                    var fy = Math.Cos(yaw);

                    var dx = CentreX - p.X;
                    var dy = CentreY - p.Y;
                    var len = Math.Sqrt(dx * dx + dy * dy);

                    var dot = (fx * dx + fy * dy) / len;
                    Assert.IsTrue(dot > 0.999, $"{p.Label} faces {dot:F4} of the way to the centre");
                }
            }
        }

        [TestMethod]
        public void BothMaps_ShareTheSameSetObjects()
        {
            Assert.AreSame(ArenaMapCatalog.Arena0066.SpawnPointsFor("ffa"), ArenaMapCatalog.Arena0067.SpawnPointsFor("ffa"));
            Assert.AreSame(ArenaMapCatalog.FfaSet, ArenaMapCatalog.Arena0066.SpawnPointsFor("ffa"));
        }

        [TestMethod]
        public void OneVOne_TeamListPositionMapsToSide()
        {
            var seats = ArenaMapCatalog.AssignSpawns("1v1", Solos(2));

            Assert.AreEqual(2, seats.Count);
            Assert.AreEqual("A", seats[0].Spawn.Label);
            Assert.AreEqual(100u, seats[0].Participant.CharacterId);
            Assert.AreEqual(0, seats[0].TeamIndex);
            Assert.AreEqual("B", seats[1].Spawn.Label);
            Assert.AreEqual(1, seats[1].TeamIndex);
            Assert.IsTrue(Distance(seats[0].Spawn, seats[1].Spawn) >= MinSeparation);
        }

        [TestMethod]
        public void TwoVTwo_TeamsTakeTheirOwnSideInListedOrder()
        {
            var seats = ArenaMapCatalog.AssignSpawns("2v2", Pairs());

            CollectionAssert.AreEqual(new[] { "A1", "A2", "B1", "B2" }, seats.Select(s => s.Spawn.Label).ToArray());
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, seats.Select(s => s.Participant.CharacterId).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, seats.Select(s => s.TeamIndex).ToArray());
            Assert.IsTrue(MinPairwise(seats.Select(s => s.Spawn).ToList()) >= MinSeparation);
        }

        [TestMethod]
        public void Ffa_EveryLobbySize5To15_GivesDistinctPointsAtLeast8mApart()
        {
            for (var n = 5; n <= 15; n++)
            {
                var seats = ArenaMapCatalog.AssignSpawns("ffa", Solos(n));

                Assert.IsNotNull(seats, $"n={n}");
                Assert.AreEqual(n, seats.Count, $"n={n}");

                var points = seats.Select(s => s.Spawn).ToList();
                Assert.AreEqual(n, points.Distinct().Count(), $"n={n}: a point was used twice");
                Assert.AreEqual(n, seats.Select(s => s.Participant.CharacterId).Distinct().Count(), $"n={n}");

                var min = MinPairwise(points);
                Assert.IsTrue(min >= MinSeparation, $"n={n}: closest pair is {min:F2} m");
            }
        }

        [TestMethod]
        public void Ffa_SmallLobbySpreadsAroundTheRingBeforeTheInnerPoints()
        {
            CollectionAssert.AreEqual(new[] { "F1", "F3", "F5", "F8", "F10" }, ArenaMapCatalog.AssignSpawns("ffa", Solos(5)).Select(s => s.Spawn.Label).ToArray());
            CollectionAssert.AreEqual(new[] { "F1", "F3", "F5", "F7", "F9", "F11" }, ArenaMapCatalog.AssignSpawns("ffa", Solos(6)).Select(s => s.Spawn.Label).ToArray());

            for (var n = 1; n <= 12; n++)
            {
                var labels = ArenaMapCatalog.AssignSpawns("ffa", Solos(n)).Select(s => s.Spawn.Label);
                Assert.IsTrue(labels.All(l => l.StartsWith("F")), $"n={n} used an inner point before the ring was full");
            }

            var twelve = ArenaMapCatalog.AssignSpawns("ffa", Solos(12)).Select(s => s.Spawn.Label).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(1, 12).Select(i => $"F{i}").ToArray(), twelve);

            var thirteen = ArenaMapCatalog.AssignSpawns("ffa", Solos(13)).Select(s => s.Spawn.Label).ToArray();
            Assert.AreEqual("I1", thirteen[12]);

            var fifteen = ArenaMapCatalog.AssignSpawns("ffa", Solos(15)).Select(s => s.Spawn.Label).ToArray();
            CollectionAssert.AreEqual(new[] { "I1", "I2", "I3" }, fifteen.Skip(12).ToArray());
        }

        [TestMethod]
        public void Ffa_SpreadIsEvenForEveryRingSize()
        {
            // Ring gaps (in ring steps, wrapping) differ by at most one for every n up to 12.
            for (var n = 1; n <= 12; n++)
            {
                var seats = ArenaMapCatalog.FfaSeatOrder(n);
                var gaps = Enumerable.Range(0, n).Select(i => ((i + 1 < n ? seats[i + 1] : seats[0] + 12) - seats[i])).ToList();
                Assert.IsTrue(gaps.Max() - gaps.Min() <= 1, $"n={n}: gaps {string.Join(",", gaps)}");
            }
        }

        [TestMethod]
        public void AssignSpawns_ShapesThatDoNotFit_ReturnNull()
        {
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("1v1", Solos(3)));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("1v1", Pairs()));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("2v2", Solos(2)));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("2v2", Solos(4)));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("ffa", Solos(16)));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("ffa", Solos(0)));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("ffa", Pairs()));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("koth", Solos(2)));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("1v1", null));
            Assert.IsNull(ArenaMapCatalog.AssignSpawns("1v1", new List<PvpTeam> { new PvpTeam(0, new List<PvpParticipant> { null }), Solos(1)[0] }));
            Assert.IsNull(ArenaMapCatalog.FfaSeatOrder(0));
            Assert.IsNull(ArenaMapCatalog.FfaSeatOrder(16));
        }
    }
}
