using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>BattlegroundMapCatalog: the 0x016C layout, its spawn mirror, pens, seals and the pure seating helpers.</summary>
    [TestClass]
    public class BattlegroundMapCatalogTests
    {
        private static BattlegroundLayout Map => BattlegroundMapCatalog.Bg016c;

        private static List<PvpTeam> Teams(int west, int east)
        {
            PvpTeam T(int idx, int n, uint baseId) =>
                new PvpTeam(idx, Enumerable.Range(0, n).Select(i => new PvpParticipant(baseId + (uint)i, 1500)).ToList());
            return new List<PvpTeam> { T(0, west, 100), T(1, east, 200) };
        }

        /// <summary>
        /// Each team spawns in the same room as its pen (within 12 m of it) but outside the sealed pen cell (at least 3 m from every
        /// one of its pen's seal pieces), on the map side of the pen, facing out toward the map: east for team 0, west for team 1.
        /// </summary>
        [TestMethod]
        public void Spawns_ShareThePensRoom_OutsideTheSeals_FacingTheMap()
        {
            for (var team = 0; team < 2; team++)
            {
                var pen = Map.PenFor(team);
                var seals = Map.Seals.Skip(team * 2).Take(2).ToList();

                Assert.AreEqual(2, seals.Count);

                foreach (var s in Map.SpawnsFor(team))
                {
                    Assert.IsTrue(Math.Sqrt(Math.Pow(s.X - pen.X, 2) + Math.Pow(s.Y - pen.Y, 2)) <= 12.0, $"{s.Label} is in the pen's room");

                    foreach (var seal in seals)
                        Assert.IsTrue(Math.Sqrt(Math.Pow(s.X - seal.X, 2) + Math.Pow(s.Y - seal.Y, 2)) >= 3.0, $"{s.Label} is outside the seal at ({seal.X}, {seal.Y})");

                    Assert.IsTrue(team == 0 ? s.X > pen.X : s.X < pen.X, $"{s.Label} is on the map side of its pen");
                    Assert.IsTrue(team == 0 ? s.RotationZ < 0 : s.RotationZ > 0, $"{s.Label} faces the map");
                }
            }
        }

        [TestMethod]
        public void Catalog_HasOneMap_WithExpectedIdentity()
        {
            // The 016C identity below is unchanged; Attack/Defend added the second map, 0x003C (AttackDefendCatalogTests pins it).
            Assert.AreEqual(2, BattlegroundMapCatalog.All.Count);
            Assert.AreSame(Map, BattlegroundMapCatalog.Find("bg_016c"));
            Assert.AreEqual("bg_016c", Map.MapKey);
            Assert.AreEqual(0x016Cu, Map.LandblockId);
            Assert.AreEqual(ArenaMapCatalog.ArenaRealmId, Map.RealmId);
            Assert.AreEqual("Marketplace", Map.DisplayName);
            Assert.IsTrue(BattlegroundMapCatalog.Landblocks.Contains(0x016Cu));
            Assert.IsNull(BattlegroundMapCatalog.Find("nope"));
        }

        [TestMethod]
        public void EachTeam_HasExactlySixSpawns()
        {
            Assert.AreEqual(2, Map.TeamSpawns.Count);
            Assert.AreEqual(6, Map.SpawnsFor(0).Count);
            Assert.AreEqual(6, Map.SpawnsFor(1).Count);
            Assert.AreEqual(0, Map.SpawnsFor(2).Count);
        }

        [TestMethod]
        public void Team1Spawns_AreTheXMirrorOfTeam0()
        {
            var west = Map.SpawnsFor(0);
            var east = Map.SpawnsFor(1);

            for (var i = 0; i < 6; i++)
            {
                Assert.AreEqual(110f - west[i].X, east[i].X, 0.0005f, $"x {i}");
                Assert.AreEqual(west[i].Y, east[i].Y, 0.0005f, $"y {i}");
                Assert.AreEqual(west[i].Z, east[i].Z, 0.0005f, $"z {i}");
                Assert.AreEqual(west[i].RotationW, east[i].RotationW, 0.0005f, $"W {i}");
                Assert.AreEqual(-west[i].RotationZ, east[i].RotationZ, 0.0005f, $"Z {i}");
            }
        }

        [TestMethod]
        public void EveryPosition_IsInLandblock016c_WithAnEnvCell()
        {
            foreach (var p in Map.TeamSpawns.SelectMany(s => s).Concat(Map.TeamPens))
                Assert.IsTrue(p.CellLow >= 0x0100, p.Label);

            foreach (var s in Map.Seals)
            {
                Assert.AreEqual(0x016Cu, s.CellId >> 16);
                Assert.IsTrue((s.CellId & 0xFFFF) >= 0x0100);
            }

            // Spawn positions build through the shared builder for a realm-1 ephemeral instance.
            var instance = ACE.Entity.Position.InstanceIDFromVars(1, 7, true);
            foreach (var p in Map.TeamSpawns.SelectMany(s => s))
            {
                var pos = ArenaSpawnPosition.Build(new ArenaMap(Map.MapKey, Map.LandblockId, Map.RealmId, null), p, instance);
                Assert.AreEqual(0x016Cu, pos.Cell >> 16);
            }
        }

        [TestMethod]
        public void Pens_AreAsMeasured()
        {
            var w = Map.PenFor(0);
            var e = Map.PenFor(1);
            Assert.AreEqual((ushort)0x0134, w.CellLow);
            Assert.AreEqual((0f, -60f, 0.005f), (w.X, w.Y, w.Z));
            Assert.AreEqual((0.707107f, -0.707107f), (w.RotationW, w.RotationZ));
            Assert.AreEqual((ushort)0x0274, e.CellLow);
            Assert.AreEqual((110f, -60f, 0.005f), (e.X, e.Y, e.Z));
            Assert.AreEqual((0.707107f, 0.707107f), (e.RotationW, e.RotationZ));
            Assert.IsNull(Map.PenFor(5));
        }

        [TestMethod]
        public void Seals_AreTheFourBarrierPieces()
        {
            var expected = new[]
            {
                (0x016C0134u, -1.72f, -60f, 0.707107f, -0.707107f),
                (0x016C0134u, 1.72f, -60f, 0.707107f, 0.707107f),
                (0x016C0274u, 108.28f, -60f, 0.707107f, -0.707107f),
                (0x016C0274u, 111.72f, -60f, 0.707107f, 0.707107f),
            };

            Assert.AreEqual(4, Map.Seals.Count);
            for (var i = 0; i < 4; i++)
            {
                var s = Map.Seals[i];
                Assert.AreEqual(1001088u, s.Wcid);
                Assert.AreEqual(expected[i], (s.CellId, s.X, s.Y, s.RotationW, s.RotationZ));
                Assert.AreEqual(0.005f, s.Z);
            }
        }

        [TestMethod]
        public void ZoneCentre_IsAsMeasured()
        {
            Assert.AreEqual((55f, -35f, 0.005f), (Map.ZoneX, Map.ZoneY, Map.ZoneZ));
        }

        [TestMethod]
        public void AssignSpawns_SeatsMemberIOfTeamTOnSpawnI()
        {
            var teams = Teams(6, 6);
            var seats = BattlegroundMapCatalog.AssignSpawns(Map, teams);

            Assert.IsNotNull(seats);
            Assert.AreEqual(12, seats.Count);
            foreach (var s in seats)
            {
                var team = teams[s.TeamIndex];
                var i = team.Members.IndexOf(s.Participant);
                Assert.AreSame(Map.SpawnsFor(s.TeamIndex)[i], s.Spawn);
            }
        }

        [TestMethod]
        public void AssignSpawns_RejectsBadShapes()
        {
            Assert.IsNull(BattlegroundMapCatalog.AssignSpawns(Map, Teams(7, 1)));
            Assert.IsNull(BattlegroundMapCatalog.AssignSpawns(Map, null));
            Assert.IsNull(BattlegroundMapCatalog.AssignSpawns(null, Teams(1, 1)));
            Assert.IsNull(BattlegroundMapCatalog.AssignSpawns(Map, Teams(1, 1).Take(1).ToList()));
            Assert.IsNull(BattlegroundMapCatalog.AssignSpawns(Map, new List<PvpTeam> { Teams(1, 1)[0], Teams(1, 1)[0] }));
        }

        [TestMethod]
        public void RespawnSpawn_RoundRobinWraps()
        {
            for (var c = 0; c < 20; c++)
                Assert.AreSame(Map.SpawnsFor(1)[c % 6], BattlegroundMapCatalog.RespawnSpawn(Map, 1, c));

            Assert.AreSame(Map.SpawnsFor(0)[5], BattlegroundMapCatalog.RespawnSpawn(Map, 0, -1));
            Assert.IsNull(BattlegroundMapCatalog.RespawnSpawn(Map, 3, 0));
        }

        [TestMethod]
        public void ArenaCatalog_IsUnchanged()
        {
            Assert.AreEqual(2, ArenaMapCatalog.All.Count);
            CollectionAssert.AreEqual(new uint[] { 0x0066, 0x0067 }, ArenaMapCatalog.All.Select(m => m.LandblockId).ToArray());
            Assert.IsFalse(ArenaMapCatalog.All.Any(m => m.LandblockId == 0x016C));
        }

        /// <summary>
        /// BattlegroundModes.All(dials), the counterpart of PvpModes.All(dials): King of the Hill FIRST, then Attack/Defend, in the one bg
        /// room on the battleground ladder; King of the Hill is objective, no overtime, no arena Blood (it pays Marks via PayBattlegroundMarks), played on the 0x016C space map. Literals,
        /// not the constants, so a renamed key fails here. (Attack/Defend is pinned in AttackDefendCatalogTests.)
        /// </summary>
        [TestMethod]
        public void BattlegroundModesAll_IsKothThenAttackDefend()
        {
            var all = BattlegroundModes.All(BattlegroundTunables.Defaults);

            Assert.AreEqual(2, all.Count);
            Assert.AreEqual("bg_ad", all[1].ModeKey);

            var koth = all[0];
            Assert.AreEqual("bg_koth", koth.ModeKey);
            Assert.AreEqual("bg", koth.RoomKey);
            Assert.AreEqual("battleground", koth.LadderKey);
            Assert.IsTrue(koth.IsObjective);
            Assert.IsFalse(koth.UsesOvertime);
            Assert.IsFalse(koth.PaysBlood);
            Assert.AreEqual((2, 2), koth.TeamCountRange);
            Assert.AreEqual(1, koth.MapPool.Count);
            Assert.AreEqual(0x016Cu, koth.MapPool[0].LandblockId);
        }
    }
}