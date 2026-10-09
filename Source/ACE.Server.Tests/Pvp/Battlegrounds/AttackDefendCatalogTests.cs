using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The Attack/Defend half of the catalogue: the bg_003c layout (Docs/Pvp/ATTACK-DEFEND.md "Map: Abandoned Mines, 0x003C"), the mode
    /// definition, the derived map pools, the mode lookups that now throw for an unknown key, and the contracts phase B builds on.
    /// Literals, not constants, where a rename must fail here.
    /// </summary>
    [TestClass]
    public class AttackDefendCatalogTests
    {
        private static BattlegroundLayout Mines => BattlegroundMapCatalog.Bg003c;

        // ---------------- the layout ----------------

        [TestMethod]
        public void Bg003c_HasItsIdentity()
        {
            Assert.AreSame(Mines, BattlegroundMapCatalog.Find("bg_003c"));
            Assert.AreEqual("bg_003c", Mines.MapKey);
            Assert.AreEqual(0x003Cu, Mines.LandblockId);
            Assert.AreEqual(ArenaMapCatalog.ArenaRealmId, Mines.RealmId);
            Assert.AreEqual("Abandoned Mines", Mines.DisplayName);
            CollectionAssert.AreEqual(new[] { "bg_ad" }, Mines.Modes.ToArray());
            Assert.IsTrue(BattlegroundMapCatalog.Landblocks.Contains(0x003Cu));
            Assert.IsTrue(BattlegroundMapCatalog.Landblocks.Contains(0x016Cu));
            Assert.AreEqual(2, BattlegroundMapCatalog.Landblocks.Count);
        }

        [TestMethod]
        public void Bg003c_HasOneToFourCrystalSites_AndADefaultCountInRange()
        {
            Assert.IsTrue(Mines.CrystalSites.Count >= 1 && Mines.CrystalSites.Count <= 4, "1 to 4 sites");
            Assert.IsTrue(Mines.DefaultCrystalCount >= 1 && Mines.DefaultCrystalCount <= Mines.CrystalSites.Count, "the default count is within the site count");
            Assert.AreEqual(3, Mines.DefaultCrystalCount);
            Assert.AreEqual(3, Mines.CrystalSites.Count);
        }

        [TestMethod]
        public void Bg003c_CrystalSites_AreAsSurveyed()
        {
            var expected = new[]
            {
                ("Great Hall", (ushort)0x01EE, 110f, -70f, -6f),
                ("West Cavern", (ushort)0x0161, 54.5f, -55.4f, -30f),
                ("Pit Hall", (ushort)0x013E, 89f, -164f, -42f),
            };

            for (var i = 0; i < expected.Length; i++)
            {
                var s = Mines.CrystalSites[i];
                Assert.AreEqual(expected[i], (s.Name, s.CellLow, s.X, s.Y, s.Z), $"site {i}");
            }

            CollectionAssert.AreEqual(new[] { "upper level, south-east of the octagon", "lower level, south from the octagon past the Defender room, then down the stairs", "bottom of the spiral stair, south-east of the West Cavern" }, Mines.CrystalSites.Select(s => s.Hint).ToArray(), "owner-facing hints, verbatim");
            Assert.AreEqual((84.5f, -96.47f), (Mines.ZoneX, Mines.ZoneY), "the compass centroid of the three sites");
            Assert.AreEqual(3, Mines.CrystalSites.Select(s => s.Name).Distinct().Count(), "site names are distinct, alerts name them");
            Assert.IsTrue(Mines.CrystalSites.All(s => s.CellLow >= 0x0100), "every site is in an EnvCell");
        }

        [TestMethod]
        public void Bg003c_EachSideHasSixSpawns_FacingOffTheUnitCircle()
        {
            Assert.AreEqual(2, Mines.TeamSpawns.Count);
            Assert.AreEqual(6, Mines.SpawnsFor(0).Count, "attackers (side 0)");
            Assert.AreEqual(6, Mines.SpawnsFor(1).Count, "defenders (side 1)");
            Assert.AreEqual(0, Mines.SpawnsFor(2).Count);

            foreach (var s in Mines.TeamSpawns.SelectMany(t => t))
            {
                Assert.AreEqual(1.0, s.RotationW * s.RotationW + s.RotationZ * s.RotationZ, 0.0005, $"{s.Label} is a unit yaw quaternion");
                Assert.IsTrue(s.CellLow >= 0x0100, s.Label);
            }
        }

        [TestMethod]
        public void Bg003c_AttackerSpawns_AreInRoom0216_AndDefenderSpawnsInRoom01D1()
        {
            // Room 0x0216's lower floor (z -6) spans x 155..195, y -25..5; the attackers' six points are the 3 m grid in cell 0x022A.
            foreach (var s in Mines.SpawnsFor(0))
            {
                Assert.AreEqual(0x022A, s.CellLow, s.Label);
                Assert.AreEqual(-5.995f, s.Z, s.Label);
                Assert.IsTrue(s.X >= 185f && s.X <= 195f && s.Y >= -15f && s.Y <= -5f, $"{s.Label} is inside cell 0x022A's footprint");
            }

            // Room 0x01D1 is the cells 0x01D1 (x 55..65) and 0x01D7 (x 65..75), y -85..-75, floor z -6; two rows at y -82.5 and -77.5. D2 and D5 sit at x 65.5 (in 0x01D7), clear of the baked statics 0x01000E5F at (60.80, -77.38) and (61.90, -82.43).
            var expected = new[] { (0x01D1, 59f, -82.5f), (0x01D7, 65.5f, -82.5f), (0x01D7, 68f, -82.5f), (0x01D1, 59f, -77.5f), (0x01D7, 65.5f, -77.5f), (0x01D7, 68f, -77.5f) };

            for (var i = 0; i < 6; i++)
            {
                var s = Mines.SpawnsFor(1)[i];
                Assert.AreEqual(expected[i], ((int)s.CellLow, s.X, s.Y), $"defender spawn {i}");
                Assert.AreEqual(-5.995f, s.Z, $"defender spawn {i}");
                // Not a protection: every spawn is visible from the corridor at (80, -80). The rows only keep spawns off the door's axis.
                Assert.IsTrue(Math.Abs(s.Y + 80f) >= 2.35f, $"defender spawn {i} is off the door's axis along y = -80");
            }
        }

        [TestMethod]
        public void Bg003c_EverySpawnIsInItsOwnTeamsSpawnRoom_AndNoPenIs()
        {
            Assert.AreEqual(2, Mines.SpawnRoomCells.Count);

            for (var team = 0; team < 2; team++)
            {
                var room = Mines.SpawnRoomSetFor(team);
                Assert.IsNotNull(room, $"team {team} has spawn-room cells");

                foreach (var s in Mines.SpawnsFor(team))
                    Assert.IsTrue(room.Contains(0x003C0000u | s.CellLow), $"{s.Label} is in team {team}'s spawn room");

                Assert.IsFalse(room.Contains(0x003C0000u | Mines.PenFor(team).CellLow), $"team {team}'s pen cell is not in its spawn room");
                Assert.IsFalse(Mines.SpawnRoomSetFor(1 - team).Contains(0x003C0000u | Mines.PenFor(team).CellLow));
            }

            CollectionAssert.AreEquivalent(new uint[] { 0x003C01D1, 0x003C01D7 }, Mines.SpawnRoomSetFor(1).ToList(), "defenders: the room's own two floor cells; immunity ends at the doorway, the stub 0x01DE is outside");
            CollectionAssert.AreEquivalent(new uint[] { 0x003C0216, 0x003C021E, 0x003C0226, 0x003C0227, 0x003C0229, 0x003C022A, 0x003C022B }, Mines.SpawnRoomSetFor(0).ToList(), "attackers: the L room's floor cells; the doorway cell 0x021F is outside");
            Assert.IsFalse(Mines.SpawnRoomSetFor(1).Contains(0x003C01DE));
            Assert.IsFalse(Mines.SpawnRoomSetFor(0).Contains(0x003C021F));

            Assert.IsNull(BattlegroundMapCatalog.Bg016c.SpawnRoomSetFor(0), "the KotH map has no spawn rooms (plain N seconds)");
            Assert.IsNull(BattlegroundMapCatalog.Bg016c.SpawnRoomSetFor(1));
        }

        /// <summary>The footprint backs the cell list: every spawn point lies inside its own team's footprint, every pen and the doorways do not.</summary>
        [TestMethod]
        public void Bg003c_SpawnRoomFootprints_ContainTheSpawns_AndNotThePensOrDoorways()
        {
            for (var team = 0; team < 2; team++)
            {
                var area = Mines.SpawnRoomAreaFor(team);
                Assert.IsNotNull(area);
                Assert.IsTrue(area.Rects.Count > 0, $"team {team} has a footprint");

                foreach (var s in Mines.SpawnsFor(team))
                    Assert.IsTrue(area.Contains(new ACE.Entity.Position(0x003C0000u | s.CellLow, s.X, s.Y, s.Z, 0f, 0f, 0f, 1f, 0)), $"{s.Label} is in team {team}'s room");

                var pen = Mines.PenFor(team);
                Assert.IsFalse(area.Contains(new ACE.Entity.Position(0x003C0000u | pen.CellLow, pen.X, pen.Y, pen.Z, 0f, 0f, 0f, 1f, 0)), $"team {team}'s pen");
            }

            var defenders = Mines.SpawnRoomAreaFor(1);
            Assert.IsFalse(defenders.Contains(new ACE.Entity.Position(0x003C01DE, 77f, -80f, -5.995f, 0f, 0f, 0f, 1f, 0)), "the stub is outside");
            Assert.IsFalse(defenders.Contains(new ACE.Entity.Position(0x003C01D7, 75.5f, -80f, -5.995f, 0f, 0f, 0f, 1f, 0)), "past the doorway line by coordinates, even on the room's cell id");
            Assert.IsFalse(defenders.Contains(new ACE.Entity.Position(0x003C0230, 70f, -80f, 0.5f, 0f, 0f, 0f, 1f, 0)), "the ceiling cap above the room");
        }

        [TestMethod]
        public void Bg003c_DefendersFaceTheRoomsDoorway()
        {
            foreach (var s in Mines.SpawnsFor(1))
            {
                var h = 2.0 * Math.Atan2(s.RotationZ, s.RotationW);
                var fx = -Math.Sin(h);
                var fy = Math.Cos(h);
                var toDoorX = 75.0 - s.X;
                var toDoorY = -80.0 - s.Y;
                var len = Math.Sqrt(toDoorX * toDoorX + toDoorY * toDoorY);

                Assert.AreEqual(1.0, (fx * toDoorX + fy * toDoorY) / len, 0.002, $"{s.Label} faces the doorway at (75, -80)");
            }
        }

        [TestMethod]
        public void Bg003c_NoSpawnIsWithinTheRespawnConfirmRadiusOfItsTeamsPenPoint()
        {
            // PvpMatchCoordinator.RespawnConfirmRadius: a respawn is confirmed when the player is within that distance of the target
            // spawn. A spawn closer than that to the pen point would read a refused respawn (player still in the pen) as confirmed.
            double radius = ACE.Server.Pvp.PvpMatchCoordinator.RespawnConfirmRadius;

            for (var team = 0; team < 2; team++)
            {
                var pen = Mines.PenFor(team);

                foreach (var s in Mines.SpawnsFor(team))
                {
                    var d = Math.Sqrt(Math.Pow(s.X - pen.X, 2) + Math.Pow(s.Y - pen.Y, 2) + Math.Pow(s.Z - pen.Z, 2));
                    Assert.IsTrue(d > radius, $"{s.Label} is {d:F1} m from its pen point, inside the {radius} m confirm radius");
                }
            }
        }

        [TestMethod]
        public void Bg003c_AttackerSpawnsFaceTheRoomsDoorway()
        {
            // heading 0 = +y, counter-clockwise positive: the facing direction of (W, Z) is (-sin h, cos h) with h = 2 atan2(Z, W).
            foreach (var s in Mines.SpawnsFor(0))
            {
                var h = 2.0 * Math.Atan2(s.RotationZ, s.RotationW);
                var fx = -Math.Sin(h);
                var fy = Math.Cos(h);
                var toDoorX = 175.0 - s.X;
                var toDoorY = -20.0 - s.Y;
                var len = Math.Sqrt(toDoorX * toDoorX + toDoorY * toDoorY);

                Assert.AreEqual(1.0, (fx * toDoorX + fy * toDoorY) / len, 0.002, $"{s.Label} faces the doorway at (175, -20)");
            }
        }

        [TestMethod]
        public void Bg003c_HasTwoPens_EachSealedInItsOwnCell()
        {
            Assert.AreEqual(2, Mines.TeamPens.Count);

            var attackerPen = Mines.PenFor(0);
            var defenderPen = Mines.PenFor(1);

            Assert.AreEqual((ushort)0x0223, attackerPen.CellLow);
            Assert.AreEqual((170f, -80f), (attackerPen.X, attackerPen.Y));
            Assert.AreEqual((ushort)0x01D4, defenderPen.CellLow);
            Assert.AreEqual((60f, -130f, -5.995f), (defenderPen.X, defenderPen.Y, defenderPen.Z));
            Assert.AreEqual((1f, 0f), (defenderPen.RotationW, defenderPen.RotationZ), "faces its doorway at y -125.5 (+y)");
            Assert.IsNull(Mines.PenFor(2));

            Assert.IsTrue(Mines.Seals.Any(s => s.CellId == 0x003C0223), "a seal in the attacker pen cell");
            Assert.IsTrue(Mines.Seals.Any(s => s.CellId == 0x003C01D4), "a seal in the defender pen cell");
            Assert.IsFalse(Mines.Seals.Any(s => s.CellId == 0x003C0231), "the old defender pen 0x0231 is an ordinary open room");
            Assert.IsFalse(Mines.Seals.Any(s => s.CellId == 0x003C01D1 || s.CellId == 0x003C01D7), "no seal anywhere in the defender spawn room");
            Assert.AreEqual(2, Mines.StartGates.Count, "start gates: one at each spawn room's only door, down at Live");
            Assert.IsFalse(Mines.StartGates.Any(g => Mines.Seals.Contains(g)), "the gates are not pen seals: those stay up all match");
        }

        [TestMethod]
        public void Bg003c_Seals_AreTheTwoPenSealsAndThePitSeal()
        {
            var expected = new[]
            {
                (0x003C0223u, 170f, -75.7f, -5.995f),
                (0x003C01D4u, 60f, -125.7f, -5.995f),
                (0x003C013Fu, 90f, -174.8f, -41.995f),
            };

            Assert.AreEqual(3, Mines.Seals.Count, "two pen seals and the one pit seal");

            for (var i = 0; i < 3; i++)
            {
                var s = Mines.Seals[i];

                Assert.AreEqual(1001088u, s.Wcid);
                Assert.AreEqual(expected[i], (s.CellId, s.X, s.Y, s.Z), $"seal {i}");
                Assert.AreEqual((1f, 0f), (s.RotationW, s.RotationZ), "no rotation: the 10 m width runs along x, across a doorway whose normal runs along y");
                Assert.AreEqual(0x003Cu, s.CellId >> 16);
            }
        }

        [TestMethod]
        public void Bg003c_NoSpawnSitsWithin3mOfASeal()
        {
            // Positive control: each pen seal stands within 6 m of its own pen point, so these are the seals the rule is about.
            for (var team = 0; team < 2; team++)
            {
                var pen = Mines.PenFor(team);
                var near = Mines.Seals.Min(s => Math.Sqrt(Math.Pow(pen.X - s.X, 2) + Math.Pow(pen.Y - s.Y, 2)));
                Assert.IsTrue(near <= 6.0, $"team {team}'s pen point is {near:F1} m from the nearest seal");
            }

            foreach (var spawn in Mines.TeamSpawns.SelectMany(t => t))
            {
                foreach (var seal in Mines.Seals)
                    Assert.IsTrue(Math.Sqrt(Math.Pow(spawn.X - seal.X, 2) + Math.Pow(spawn.Y - seal.Y, 2)) >= 3.0, $"{spawn.Label} is at least 3 m from the seal at ({seal.X}, {seal.Y})");
            }
        }

        [TestMethod]
        public void Bg003c_PositionsBuildThroughTheSharedBuilder_ForARealmOneInstance()
        {
            var instance = ACE.Entity.Position.InstanceIDFromVars(1, 7, true);

            foreach (var p in Mines.TeamSpawns.SelectMany(s => s).Concat(Mines.TeamPens))
            {
                var pos = ArenaSpawnPosition.Build(new ArenaMap(Mines.MapKey, Mines.LandblockId, Mines.RealmId, null), p, instance);

                Assert.AreEqual(0x003Cu, pos.Cell >> 16, p.Label);
            }
        }

        [TestMethod]
        public void Bg003c_SpaceMapCarriesEverySpawnAndBothPens()
        {
            var map = BattlegroundMapCatalog.Bg003cSpaceMap;

            Assert.AreEqual("bg_003c", map.MapKey);
            Assert.AreEqual(0x003Cu, map.LandblockId);
            Assert.AreEqual(14, map.SpawnPointsFor("bg").Count, "12 spawns and 2 pens");
            Assert.AreEqual("Abandoned Mines", map.DisplayName);
        }

        // ---------------- the catalogue as a whole ----------------

        [TestMethod]
        public void EveryLayoutListingKoth_HasAZone()
        {
            var kothMaps = BattlegroundMapCatalog.All.Where(l => l.Modes.Contains("bg_koth")).ToList();

            Assert.IsTrue(kothMaps.Count >= 1, "control: at least one map hosts King of the Hill");

            foreach (var l in kothMaps)
            {
                Assert.IsTrue(l.ZoneX != 0f || l.ZoneY != 0f, $"{l.MapKey} lists King of the Hill but has no zone centre");
                Assert.IsTrue(l.ZoneMarkerWcid > 0, $"{l.MapKey} lists King of the Hill but places no zone markers");
            }
        }

        [TestMethod]
        public void EveryLayoutListingAttackDefend_HasCrystalSites()
        {
            var adMaps = BattlegroundMapCatalog.All.Where(l => l.Modes.Contains("bg_ad")).ToList();

            Assert.AreEqual(1, adMaps.Count);

            foreach (var l in adMaps)
                Assert.IsTrue(l.CrystalSites.Count >= 1 && l.DefaultCrystalCount >= 1, l.MapKey);
        }

        [TestMethod]
        public void EveryLayout_NamesAtLeastOneKnownMode_AndEveryModeHasAMap()
        {
            var known = BattlegroundModes.All(BattlegroundTunables.Defaults).Select(m => m.ModeKey).ToHashSet();

            foreach (var l in BattlegroundMapCatalog.All)
            {
                Assert.IsTrue(l.Modes.Count >= 1, $"{l.MapKey} hosts no mode");
                Assert.IsTrue(l.Modes.All(known.Contains), $"{l.MapKey} names a mode that does not exist");
            }

            foreach (var key in known)
                Assert.IsTrue(BattlegroundModes.MapPoolFor(key).Count >= 1, $"{key} has no map");
        }

        [TestMethod]
        public void SpaceMapsAndLandblocks_AreDerivedFromTheCatalogue()
        {
            CollectionAssert.AreEqual(BattlegroundMapCatalog.All.Select(l => l.MapKey).ToArray(), BattlegroundMapCatalog.SpaceMaps.Select(m => m.MapKey).ToArray());
            CollectionAssert.AreEquivalent(BattlegroundMapCatalog.All.Select(l => l.LandblockId).ToArray(), BattlegroundMapCatalog.Landblocks.ToArray());
            Assert.AreSame(BattlegroundMapCatalog.Bg016cSpaceMap, BattlegroundMapCatalog.SpaceMapOf(BattlegroundMapCatalog.Bg016c), "the same instance every time");
            Assert.AreSame(BattlegroundMapCatalog.Bg003cSpaceMap, BattlegroundMapCatalog.SpaceMapOf(Mines));
            Assert.IsNull(BattlegroundMapCatalog.SpaceMapOf(null));
            Assert.IsTrue(PvpMatchLandblocks.IsPvpMapLandblock(0x003C));
        }

        [TestMethod]
        public void MapPoolFor_IsDerivedFromTheLayoutsModes()
        {
            CollectionAssert.AreEqual(new[] { "bg_016c" }, BattlegroundModes.MapPoolFor("bg_koth").Select(m => m.MapKey).ToArray());
            CollectionAssert.AreEqual(new[] { "bg_003c" }, BattlegroundModes.MapPoolFor("bg_ad").Select(m => m.MapKey).ToArray());
            Assert.AreEqual(0, BattlegroundModes.MapPoolFor("nope").Count);
        }

        // ---------------- the modes ----------------

        [TestMethod]
        public void BattlegroundModesAll_IsKothThenAttackDefend()
        {
            var all = BattlegroundModes.All(BattlegroundTunables.Defaults);

            CollectionAssert.AreEqual(new[] { "bg_koth", "bg_ad" }, all.Select(m => m.ModeKey).ToArray(), "KOTH stays first: the coordinator's default pick is the first enabled mode");

            var ad = all[1];
            Assert.AreEqual("bg", ad.RoomKey);
            Assert.AreEqual("battleground", ad.LadderKey);
            Assert.AreEqual("pvp_bg_ad_enabled", ad.EnabledTunableKey);
            Assert.AreEqual("pvp_bg_time_limit_seconds_ad", ad.TimeLimitTunableKey);
            Assert.IsTrue(ad.IsObjective);
            Assert.IsFalse(ad.UsesOvertime);
            Assert.IsFalse(ad.PaysBlood);
            Assert.IsTrue(ad.TimeoutRated, "a timeout is a rated defender win");
            Assert.IsTrue(ad.Templated);
            Assert.AreEqual((2, 2), ad.TeamCountRange);
            Assert.AreEqual(1, ad.MapPool.Count);
            Assert.AreEqual(0x003Cu, ad.MapPool[0].LandblockId);
            Assert.IsInstanceOfType(ad.WinCondition(), typeof(CrystalWinCondition));
            Assert.IsInstanceOfType(ad.Respawn, typeof(SidedRespawnPolicy));
            Assert.IsInstanceOfType(ad.TickHandler(), typeof(AttackDefendTickHandler));
            Assert.IsInstanceOfType(ad.TickHandler(), typeof(IObjectiveModeHandler));
        }

        [TestMethod]
        public void TheModeKeyRoomAndLadderAreTheOneConstants()
        {
            Assert.AreEqual("bg_ad", BattlegroundModes.AttackDefendModeKey);
            Assert.AreEqual("bg", BattlegroundModes.RoomKey);
            Assert.AreEqual("battleground", BattlegroundModes.LadderKey);
            Assert.IsTrue(BattlegroundModes.IsBattlegroundModeKey(BattlegroundModes.AttackDefendModeKey));
        }

        [TestMethod]
        public void TheAttackDefendDefinition_IsBuiltFromTheDialsSnapshot()
        {
            var snapshot = BattlegroundTunables.Defaults with { TimeLimitSecondsAd = 100, AdCrystalCount = 2 };
            var ad = BattlegroundModes.AttackDefend(snapshot);
            var wc = ad.WinCondition();
            var m = new FakeObjectiveContext();

            // The definition keeps ITS snapshot: a live setting changed afterwards, or a different snapshot, never reaches a match already formed.
            Assert.IsNull(wc.Evaluate(m, FakeObjectiveContext.Start.AddSeconds(99)));
            Assert.AreEqual(1, wc.Evaluate(m, FakeObjectiveContext.Start.AddSeconds(100)).WinningTeams.Single());

            m.ScoreBoard[0] = 2;
            Assert.AreEqual(0, wc.Evaluate(m, FakeObjectiveContext.Start.AddSeconds(5)).WinningTeams.Single(), "two crystals is every crystal for this snapshot");

            var ctx = new FakeObjectiveContext();

            Assert.AreEqual(2, ((AttackDefendTickHandler)ad.TickHandler()).Plan.Count);
            Assert.AreEqual(TimeSpan.FromSeconds(30), ((DeathDisposition.RespawnDisposition)ad.Respawn.OnDeath(ctx, ctx.Teams[0].Members[0])).Delay);
        }

        [TestMethod]
        public void IsEnabled_FollowsEachModesOwnSwitch_AndEveryModeInAllHasACase()
        {
            var d = BattlegroundTunables.Defaults;

            foreach (var mode in BattlegroundModes.All(d))
                Assert.IsTrue(BattlegroundModes.IsEnabled(mode.ModeKey, d), $"{mode.ModeKey} is enabled by default (a mode with no case would be silently disabled)");

            Assert.IsFalse(BattlegroundModes.IsEnabled("bg_ad", d with { AdEnabled = false }));
            Assert.IsTrue(BattlegroundModes.IsEnabled("bg_koth", d with { AdEnabled = false }));
            Assert.IsFalse(BattlegroundModes.IsEnabled("bg_koth", d with { KothEnabled = false }));
            Assert.IsTrue(BattlegroundModes.IsEnabled("bg_ad", d with { KothEnabled = false }));
            Assert.IsFalse(BattlegroundModes.IsEnabled("bg_ad", d with { Enabled = false }), "the master switch sits above both");
            Assert.IsFalse(BattlegroundModes.IsEnabled("bg_nope", d), "an unknown key stays disabled");
            Assert.IsFalse(BattlegroundModes.IsEnabled("bg_ad", null));
        }

        [TestMethod]
        public void TimeLimitSeconds_ReadsEachModesOwnSetting_AndThrowsForAnUnknownKey()
        {
            var d = BattlegroundTunables.Defaults with { TimeLimitSecondsKoth = 111, TimeLimitSecondsAd = 222 };

            Assert.AreEqual(111, BattlegroundModes.TimeLimitSeconds("bg_koth", d));
            Assert.AreEqual(222, BattlegroundModes.TimeLimitSeconds("bg_ad", d));
            Assert.AreEqual(600, BattlegroundModes.TimeLimitSeconds("bg_ad", BattlegroundTunables.Defaults));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BattlegroundModes.TimeLimitSeconds("bg_nope", d), "no silent fall back to King of the Hill");
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BattlegroundModes.TimeLimitSeconds(null, d));
        }

        [TestMethod]
        public void ScoreTarget_ReadsEachModesOwnSetting_AndThrowsForAnUnknownKey()
        {
            var d = BattlegroundTunables.Defaults with { KothScoreTarget = 77, AdCrystalCount = 2 };

            Assert.AreEqual(77, BattlegroundModes.ScoreTarget("bg_koth", d));
            Assert.AreEqual(2, BattlegroundModes.ScoreTarget("bg_ad", d), "a set crystal count, within the map's sites");
            Assert.AreEqual(BattlegroundMapCatalog.Bg003c.DefaultCrystalCount, BattlegroundModes.ScoreTarget("bg_ad", d with { AdCrystalCount = 0 }),
                "0 means the map default: the effective count, never the raw 0");
            Assert.AreEqual(BattlegroundMapCatalog.Bg003c.CrystalSites.Count, BattlegroundModes.ScoreTarget("bg_ad", d with { AdCrystalCount = 99 }),
                "clamped to the map's sites");

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BattlegroundModes.ScoreTarget("bg_nope", d));
        }

        [TestMethod]
        public void EveryModeInAll_HasATimeLimitAndAScoreTarget_SoNeitherLookupThrowsForARealMode()
        {
            var d = BattlegroundTunables.Defaults;

            foreach (var mode in BattlegroundModes.All(d))
            {
                Assert.IsTrue(BattlegroundModes.TimeLimitSeconds(mode.ModeKey, d) > 0, mode.ModeKey);
                BattlegroundModes.ScoreTarget(mode.ModeKey, d);
            }
        }

        // ---------------- templates, labels, intents ----------------

        [TestMethod]
        public void TheShortFormAd_ResolvesToBgAd()
        {
            Assert.AreEqual("bg_ad", PvpTemplateCatalog.CanonicalModeKey("ad"));
            Assert.AreEqual("bg_ad", PvpTemplateCatalog.CanonicalModeKey(" AD "));
            Assert.AreEqual("bg_ad", PvpTemplateCatalog.CanonicalModeKey("bg_ad"));
            Assert.AreEqual("bg_koth", PvpTemplateCatalog.CanonicalModeKey("koth"), "the KOTH short form is unchanged");
            CollectionAssert.Contains(PvpTemplateCatalog.ModeKeys.ToList(), "bg_ad");
        }

        [TestMethod]
        public void ModeLabels_ReadAttackDefend_NeverTheRawKey()
        {
            Assert.AreEqual("Attack/Defend", PvpArenaText.ModeLabel("bg_ad"));
            Assert.AreEqual("Attack/Defend", PvpArenaText.CrierModeLabel("bg_ad"));
            Assert.AreEqual("King of the Hill", PvpArenaText.ModeLabel("bg_koth"));
            Assert.AreEqual("King of the Hill", PvpArenaText.CrierModeLabel("bg_koth"));
            Assert.AreEqual("Battleground", PvpArenaText.ModeLabel("bg"));
        }

        [TestMethod]
        public void ObjectiveDestroyedIntent_CarriesMatchIndexAndKiller_AndAppendsToTheEnum()
        {
            var match = Guid.NewGuid();
            var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var intent = PvpMatchManager.ObjectiveDestroyed(match, crystalIndex: 2, killerGuid: 0x50000123u, utcNow: at);

            Assert.AreEqual(PvpIntentKind.ObjectiveDestroyed, intent.Kind);
            Assert.AreEqual(match, intent.MatchId);
            Assert.AreEqual(2, intent.Count);
            Assert.AreEqual(0x50000123u, intent.KillerId);
            Assert.AreEqual(0u, intent.CharacterId);
            Assert.AreEqual(at, intent.OccurredAtUtc);

            // Appended last: every earlier value keeps its number.
            Assert.AreEqual(0, (int)PvpIntentKind.Death);
            Assert.AreEqual(1, (int)PvpIntentKind.Forfeit);
            Assert.AreEqual(2, (int)PvpIntentKind.EntryFailed);
            Assert.AreEqual(3, (int)PvpIntentKind.BackstopFired);
            Assert.AreEqual(4, (int)PvpIntentKind.ObjectiveDestroyed);
        }
    }
}
