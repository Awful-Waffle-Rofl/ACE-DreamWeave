using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;
using ACE.Server.Pinnacles.Matron;
using ACE.Server.PuzzleGates;
using ACE.Server.Realms;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Dat-free pins of the Harbinger room data, the pure layout and the `harbinger` region preset. Reads no
    /// PropertyManager value. The dat-backed proof of every point is HazardMechanics.HarbingerRoomGeometryTests;
    /// every value that test checks is also pinned here.
    /// </summary>
    [TestClass]
    public class HarbingerLayoutTests
    {
        // ---- the region preset ----

        [TestMethod]
        public void Preset_FloorBandEndsFarBelowTheAlcoves()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var f));
            Assert.IsTrue(f.GroundPlaneZ + f.BandAbove < -11.2f - 4f);

            foreach (var a in HarbingerData.Alcoves)
                Assert.IsFalse(f.InBand(new Vector3(a.Centre, 0f)));

            // control: a point on the chamber floor IS in the band, so the check above discriminates
            Assert.IsTrue(f.InBand(new Vector3(20f, -20f, -35.5f)));
        }

        [TestMethod]
        public void Preset_IsTheChamberRect_FlatFloor_UnderTheCeiling_AndRegistered()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet(HazardRegionPresets.HarbingerName, 7, out var r));

            Assert.AreEqual("harbinger", HazardRegionPresets.HarbingerName);
            Assert.AreEqual((ushort)0x00BC, r.Landblock);
            Assert.AreEqual(7u, r.Instance);
            Assert.AreEqual(HazardShape.Rect, r.Shape);
            Assert.AreEqual((5f, 35f, -35f, -5f), (r.MinX, r.MaxX, r.MinY, r.MaxY));
            Assert.AreEqual(HazardGroundMode.Plane, r.GroundMode);
            Assert.AreEqual(-36.0f, r.GroundPlaneZ);
            Assert.IsNull(r.HazardPlaneZ);
            Assert.AreEqual(0.5f, r.HazardOffset);
            Assert.AreEqual(-11.7f, r.CeilingZ);
            Assert.AreEqual(0.5f, r.BandBelow);
            Assert.AreEqual(20.0f, r.BandAbove);

            CollectionAssert.Contains(HazardRegionPresets.Names.ToArray(), "harbinger");
            CollectionAssert.Contains(HazardRegionPresets.Names.ToArray(), "matron", "the Matron preset is untouched");
            Assert.AreEqual("harbinger", HazardRegionPresets.ForLandblock(0x00BC));
        }

        [TestMethod]
        public void Preset_HasNoPlatformRegion_TheCentrePlatformIsBlockedSpace()
        {
            Assert.IsFalse(HazardRegionPresets.TryGet("harbinger-platform", 0, out _));
        }

        // ---- hazard args ----

        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;

        private static HazardArgs AcidFill(double startZ, double rate) => S.AcidArgs(startZ, rate, 1.0, final: false);

        [TestMethod]
        public void Fill_FiveByFiveSheet_ValidatesUnderTheCeiling()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var r));

            Assert.AreEqual(-18.0f, HarbingerLayout.HailDropZ, "owner ruling: 0.75 m under the corner caps' roof so a 0.4 m ball (top -17.6) stays inside the cells; every octagon grid point has a cell there (HarbingerRoomGeometryTests)");
            Assert.AreEqual(25, FillRules.SheetTiles(r, (float)HarbingerSettings.AcidPanelSize, 0.3f).Count, "a 5x5 sheet (HarbingerFillSpawnTests: every octagon metre but the corner slivers is covered)");
            Assert.IsNull(FillRules.Validate(r, AcidFill(-35.95, 0.02)));
            Assert.IsNull(FillRules.Validate(r, AcidFill(-35.95, 0.01)), "the P5 rate");
        }

        /// <summary>
        /// The wall strips (round 2 A2), dat-free: the values HarbingerWallStripGeometryTests measures and spawns against the dat
        /// (it skips in CI). The wall plane is 15 m out, the panel origin the Matron's offset in front of it, 11 rows from the sill to
        /// the floor, every prop in its layer's measured cell, and the same strip turned for every alcove (no per-alcove branch).
        /// </summary>
        [TestMethod]
        public void WallStrip_IsTheMatronsStrip_OnTheMeasuredWall_ForEveryAlcove()
        {
            Assert.AreEqual(15.0f, HarbingerLayout.WallDistance, "measured: the wall plane under every doorway");
            Assert.AreEqual(11, HarbingerLayout.WallPanelRowZ.Count);
            Assert.AreEqual(-1.6667f, HarbingerLayout.WallPanelRowZ[0], 1e-3f, "half a row under the sill (z 0)");
            Assert.AreEqual(-35.0f, HarbingerLayout.WallPanelRowZ[10], 1e-3f, "the bottom row's centre, over the -36 floor");
            Assert.AreEqual(-35.995f, HarbingerLayout.WallFogZ, 1e-4f);

            var reference = HarbingerData.Alcoves[0];
            var refStrip = HarbingerLayout.WallStrip(reference, MatronLayout.WallFxBoth);

            foreach (var a in HarbingerData.Alcoves)
            {
                var strip = HarbingerLayout.WallStrip(a, MatronLayout.WallFxBoth);
                var o = a.Outward;
                var p = HarbingerLayout.Perp(o);

                Assert.AreEqual(11 + 2, strip.Count, a.Name);
                Assert.AreEqual(6, a.WallCells.Count, $"{a.Name}: one cell per 6 m layer from -36 to 0");

                for (var i = 0; i < strip.Count; i++)
                {
                    var h = strip[i];
                    var isPanel = i < 11;
                    var d = new Vector2(h.At.X, h.At.Y) - HarbingerLayout.ChamberCentre;

                    Assert.AreEqual(isPanel ? MatronData.AcidFlowWcid : MatronData.EffectHostWcid, h.Wcid, $"{a.Name} {i}");
                    Assert.AreEqual(isPanel ? HarbingerLayout.WallDistance - MatronLayout.WallPanelOffset : HarbingerLayout.WallDistance - MatronLayout.WallFogOffset, Vector2.Dot(d, o), 1e-4f, $"{a.Name} {i}: out along the outward vector");
                    Assert.AreEqual(0f, Vector2.Dot(d, p), 1e-4f, $"{a.Name} {i}: on the doorway's centre line");
                    Assert.AreEqual(a.WallCells[(int)Math.Floor((h.At.Z - HarbingerLayout.FloorZ) / HarbingerLayout.WallCellLayer)], h.At.Cell, $"{a.Name} {i}: its layer's measured cell");
                    Assert.AreEqual(isPanel ? MatronLayout.PanelScale : 1f, h.Scale, $"{a.Name} {i}");
                    Assert.AreEqual(refStrip[i].At.Z, h.At.Z, $"{a.Name} {i}: the same heights as the {reference.Name} wall");
                    Assert.AreEqual(refStrip[i].Script, h.Script, $"{a.Name} {i}");
                }

                var face = MatronLayout.WallPanelFacing(-o);
                Assert.AreEqual(new PinnaclePoint(a.WallCells[0], strip[10].At.X, strip[10].At.Y, strip[10].At.Z, face.W, face.X, face.Y, face.Z), strip[10].At, $"{a.Name}: the bottom panel faces the room");
            }

            CollectionAssert.AreEqual(new uint[] { 0x00BC0101, 0x00BC010A, 0x00BC0113, 0x00BC011C, 0x00BC0124, 0x00BC0129 }, HarbingerData.AlcoveOf(HarbingerElement.Acid).WallCells.ToArray(), "W, measured");
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HarbingerLayout.WallStrip(reference, 3));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HarbingerLayout.WallStrip(reference, MatronLayout.WallFxBoth, MatronLayout.AcidFogs.Count));
        }

        [TestMethod]
        public void Fill_AboveTheCeiling_IsRefused_SoTheValidationDiscriminates()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var r));

            var args = HazardArgs.Parse("element=acid panel_size=10.4 doubled=1 opacity=0.5 floor_z=-35.95 max_z=-5.0 tick=2 start_z=-35.95 rate=0.02");

            Assert.IsNotNull(FillRules.Validate(r, args), "max_z above the -11.7 ceiling is refused");
        }

        // The real builders (HarbingerSettings, T8): they carry cycles=4 (lightning: the default 8 cycles x 13 steps is 884 s, over the 600 s limit) and wave_every=6 (fire: max_in_flight is refused without it).
        private static HazardArgs Lightning(bool final) => S.LightningArgs(S.WispPct, final);

        private static HazardArgs Hail(bool final) => S.HailArgs(S.HailPct, final);

        private static HazardArgs Fire(bool final) => S.FireArgs(S.FirePct, final);

        [TestMethod]
        public void HazardArgs_EveryElementValidates_OnItsRegion()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var r));

            foreach (var final in new[] { false, true })
            {
                var label = final ? "final" : "phase";

                Assert.IsNull(new WispSquadMechanic().Validate(r, Lightning(final), "mixed"), $"lightning {label}");
                Assert.IsNull(new RainMechanic().Validate(r, Hail(final), null), $"frost hail {label}");
                Assert.IsNull(new RollingBallMechanic().Validate(r, Fire(final), BallPatterns.RingOut), $"fire ring {label}");
                Assert.IsNull(new RollingBallMechanic().Validate(r, Fire(final), BallPatterns.RingIn), $"fire ring-in {label}");
                Assert.IsNull(FillRules.Validate(r, S.AcidArgs(-35.95, final ? S.FinalAcidRate : S.AcidRate, 1.0, final)), $"acid {label}");
            }
        }

        [TestMethod]
        public void HazardArgs_Controls_ARefusedArgDoesFailEachValidator()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var r));

            Assert.IsNotNull(new WispSquadMechanic().Validate(r, HazardArgs.Parse("mixed_pool=row,zigzag"), "mixed"));
            Assert.IsNotNull(new RainMechanic().Validate(r, HazardArgs.Parse("drop_z=-12.0 ball_radius=0.4"), null), "drop_z -12.0 + 0.4 is over the -11.7 ceiling");
            Assert.IsNotNull(new RollingBallMechanic().Validate(r, HazardArgs.Parse("speed=fast"), BallPatterns.RingOut));
        }

        // ---- the retail rows ----

        [TestMethod]
        public void ExcludeRows_DropsExactlyTheTwoRetailRows()
        {
            Assert.AreEqual(new EphemeralRowExclusion(0x00BC, -10f, 50f, -50f, 10f, -40f, 10f), HarbingerData.ExcludeRows);

            // fixture: the retail landblock's two rows, both at BC0104 (20, -20, -35.985): the 10-minute generator (guid 1879818241,
            // wcid 7925) and the Harbinger (guid 1879818242, wcid 36178)
            LandblockInstance Row(uint guid, uint wcid, uint cell, float x, float y, float z)
                => new LandblockInstance { Guid = guid, WeenieClassId = wcid, ObjCellId = cell, OriginX = x, OriginY = y, OriginZ = z };

            var rows = new List<LandblockInstance>
            {
                Row(1879818241, 7925, 0x00BC0104, 20f, -20f, -35.985f),
                Row(1879818242, 36178, 0x00BC0104, 20f, -20f, -35.985f),
            };

            var kept = EphemeralRowExclusion.Filter(rows, HarbingerData.ExcludeRows, out var dropped);

            Assert.AreEqual(2, dropped);
            Assert.AreEqual(0, kept.Count);

            // controls: a row on another landblock, and one on this landblock outside the box, are kept
            var controls = new List<LandblockInstance>
            {
                Row(1, 100, 0x00BD0104, 20f, -20f, -35.985f),
                Row(2, 100, 0x00BC0104, 60f, -20f, -35.985f),
                Row(3, 100, 0x00BC0104, 20f, -20f, 11f),
            };

            kept = EphemeralRowExclusion.Filter(controls, HarbingerData.ExcludeRows, out dropped);

            Assert.AreEqual(0, dropped);
            Assert.AreEqual(3, kept.Count);
        }

        // ---- the alcoves ----

        // element, alcove name, outward, alcove cell, floor-portal cell, portal wcid, stand yaw (degrees CCW from north, facing -outward)
        private static readonly (HarbingerElement El, string Name, Vector2 O, uint Cell, uint PortalCell, uint PortalWcid, float StandYaw)[] Expected =
        {
            (HarbingerElement.Acid, "W", new Vector2(-1f, 0f), 0x00BC012D, 0x00BC0101, 1008264, 270f),
            (HarbingerElement.Lightning, "E", new Vector2(1f, 0f), 0x00BC0137, 0x00BC0107, 1008267, 90f),
            (HarbingerElement.Frost, "S", new Vector2(0f, -1f), 0x00BC0134, 0x00BC0105, 1008266, 0f),
            (HarbingerElement.Fire, "N", new Vector2(0f, 1f), 0x00BC0130, 0x00BC0103, 1008265, 180f),
        };

        [TestMethod]
        public void Alcoves_LayoutMatchesTheFormula()
        {
            Assert.AreEqual(4, HarbingerData.Alcoves.Count);
            CollectionAssert.AreEqual(new[] { HarbingerElement.Acid, HarbingerElement.Lightning, HarbingerElement.Frost, HarbingerElement.Fire }, HarbingerData.Alcoves.Select(a => a.Element).ToArray(), "element order");

            var chamber = new Vector2(20f, -20f);

            foreach (var e in Expected)
            {
                var a = HarbingerData.AlcoveOf(e.El);
                var c = chamber + 20f * e.O;
                var p = new Vector2(-e.O.Y, e.O.X);   // the outward vector turned +90 degrees

                Assert.AreSame(a, HarbingerData.Alcoves.Single(x => x.Element == e.El), e.Name);
                Assert.AreEqual(e.Name, a.Name);
                Assert.AreEqual(e.O, a.Outward, e.Name);
                Assert.AreEqual(c, a.Centre, e.Name);
                Assert.AreEqual(e.Cell, a.Cell, e.Name);
                Assert.AreEqual(e.PortalWcid, a.PortalWcid, e.Name);

                void Check(string what, PinnaclePoint at, Vector2 xy, float z, uint cell)
                {
                    Assert.AreEqual(cell, at.Cell, $"{e.Name} {what} cell");
                    Assert.AreEqual(xy.X, at.X, 1e-4f, $"{e.Name} {what} x");
                    Assert.AreEqual(xy.Y, at.Y, 1e-4f, $"{e.Name} {what} y");
                    Assert.AreEqual(z, at.Z, 1e-4f, $"{e.Name} {what} z");
                }

                Check("stand", a.Stand, c - 2.0f * e.O, 0f, e.Cell);
                Check("crystal", a.Crystal, c + 2.5f * e.O, 0f, e.Cell);
                Assert.AreEqual(2, a.Guards.Count, e.Name);
                Check("guard 0", a.Guards[0], c + 2.5f * p, 0f, e.Cell);
                Check("guard 1", a.Guards[1], c - 2.5f * p, 0f, e.Cell);
                Check("portal", a.Portal, chamber + 8.0f * e.O, -36.0f, e.PortalCell);

                // round 4: the portal faces the chamber centre (-outward), like the stand, whichever alcove it is
                var facing = PuzzleGateGenerator.YawQuaternion(e.StandYaw);

                Assert.AreEqual((facing.W, facing.X, facing.Y, facing.Z), (a.Portal.QW, a.Portal.QX, a.Portal.QY, a.Portal.QZ), $"{e.Name} portal faces the chamber centre");

                var q = PuzzleGateGenerator.YawQuaternion(e.StandYaw);

                Assert.AreEqual((q.W, q.X, q.Y, q.Z), (a.Stand.QW, a.Stand.QX, a.Stand.QY, a.Stand.QZ), $"{e.Name} stand yaw {e.StandYaw} (facing -outward)");
            }
        }

        [TestMethod]
        public void Alcoves_StandInsideTheAlcove_FloorPortalInsideTheChamber_AndEveryPointIsDistinct()
        {
            var all = new List<(float X, float Y, float Z)>();

            foreach (var a in HarbingerData.Alcoves)
            {
                // the alcove is round, about 9.3 m across: every alcove point is within 2.5 m of its centre (the stand 2.0, the crystal and guards 2.5), well inside the 4.65 m radius
                foreach (var at in new[] { a.Stand, a.Crystal, a.Guards[0], a.Guards[1] })
                {
                    Assert.IsTrue(Vector2.Distance(new Vector2(at.X, at.Y), a.Centre) <= 2.5f + 1e-4f, a.Name);
                    all.Add((at.X, at.Y, at.Z));
                }

                // the floor portal is on the chamber floor, inside the octagon rect
                Assert.IsTrue(a.Portal.X >= 5f && a.Portal.X <= 35f && a.Portal.Y >= -35f && a.Portal.Y <= -5f, a.Name);
                all.Add((a.Portal.X, a.Portal.Y, a.Portal.Z));
            }

            Assert.AreEqual(all.Count, all.Distinct().Count());
        }

        // ---- head, arrival, summon ring ----

        [TestMethod]
        public void HeadArrivalAndSummonRing_ArePinned()
        {
            var h = HarbingerData.HeadPoint;
            // design choice: he faces the arrival point; the retail row (ace_ro guid 1879818242) is yaw 0. Arrival is (+8.5, -8.5) from him, i.e. south-east.
            // The yaw convention is degrees CCW from north: facing +y is 0, -x is 90, -y is 180, +x is 270, so south-east (between -y 180 and +x 270) is 225.
            var hq = PuzzleGateGenerator.YawQuaternion(225f);

            Assert.AreEqual((0x00BC0104u, 20f, -20f, -35.985f), (h.Cell, h.X, h.Y, h.Z));
            Assert.AreEqual((hq.W, hq.X, hq.Y, hq.Z), (h.QW, h.QX, h.QY, h.QZ), "yaw 225: facing the arrival point");

            var a = HarbingerData.ArrivalPoint;
            var aq = PuzzleGateGenerator.YawQuaternion(45f);   // facing (20, -20) from (28.5, -28.5): north-west, 45 degrees CCW from north

            Assert.AreEqual((0x00BC0108u, 28.5f, -28.5f, -36.0f), (a.Cell, a.X, a.Y, a.Z));
            Assert.AreEqual((aq.W, aq.X, aq.Y, aq.Z), (a.QW, a.QX, a.QY, a.QZ), "facing the Harbinger");
            Assert.AreEqual(a, HarbingerData.FightEntryPoint);

            Assert.AreEqual(8, HarbingerData.SummonPoints.Count);
            Assert.AreEqual(10f, HarbingerLayout.SummonRingRadius);

            var cells = new uint[] { 0x00BC0107, 0x00BC0106, 0x00BC0103, 0x00BC0100, 0x00BC0101, 0x00BC0102, 0x00BC0105, 0x00BC0108 };

            for (var i = 0; i < 8; i++)
            {
                var p = HarbingerData.SummonPoints[i];
                var angle = i * Math.PI / 4.0;

                Assert.AreEqual(20f + 10f * (float)Math.Cos(angle), p.X, 1e-3f, $"ring {i} x");
                Assert.AreEqual(-20f + 10f * (float)Math.Sin(angle), p.Y, 1e-3f, $"ring {i} y");
                Assert.AreEqual(-36.0f, p.Z, $"ring {i} z");
                Assert.AreEqual(cells[i], p.Cell, $"ring {i} cell");
            }
        }

        [TestMethod]
        public void HeadScaleAndRadius_ArePinned_AndNameTheBodyTheyWereMeasuredOn()
        {
            Assert.AreEqual(6.2f, HarbingerLayout.HeadScale, "the measured placement ceiling: 6.4 is refused");
            Assert.AreEqual(4.2087f, HarbingerLayout.HeadRadius, "already includes scale (0.6788 x 6.2); T8 derives the melee reach from it, never x HeadScale again");
            Assert.AreEqual(0.6788f * HarbingerLayout.HeadScale, HarbingerLayout.HeadRadius, 2e-3f);
            Assert.AreEqual(0x02001731u, HarbingerLayout.HeadSetup);
        }

        // ---- the element table ----

        [TestMethod]
        public void SpellsAndWcids_MatchThePlan()
        {
            // (element, tag, war spells, vuln, crystal, guardian, summon, damage type, visuals)
            var expected = new (HarbingerElement El, string Tag, uint[] War, uint Vuln, uint Crystal, uint Guardian, uint Summon, DamageType Damage, WorldEventHazardElement Visual)[]
            {
                (HarbingerElement.Acid, "acid", new uint[] { 4433, 4421, 4432 }, 4473, 1008251, 1008255, 1008259, DamageType.Acid, WorldEventHazardElement.Acid),
                (HarbingerElement.Lightning, "lightning", new uint[] { 4451, 4426, 4452 }, 4483, 1008252, 1008256, 1008260, DamageType.Electric, WorldEventHazardElement.Lightning),
                (HarbingerElement.Frost, "frost", new uint[] { 4447, 4425, 4448 }, 4479, 1008253, 1008257, 1008261, DamageType.Cold, WorldEventHazardElement.Frost),
                (HarbingerElement.Fire, "fire", new uint[] { 4439, 4423, 4440 }, 4481, 1008254, 1008258, 1008262, DamageType.Fire, WorldEventHazardElement.Fire),
            };

            Assert.AreEqual(4, HarbingerData.Elements.Count);

            foreach (var e in expected)
            {
                var d = HarbingerData.Element(e.El);

                Assert.AreSame(d, HarbingerData.Elements.Single(x => x.Element == e.El));
                Assert.AreEqual(e.Tag, d.Tag);
                Assert.AreEqual("summon-" + e.Tag, d.SummonTag);
                Assert.AreEqual("guard-" + e.Tag, d.GuardTag);
                Assert.AreEqual("crystal-" + e.Tag, d.CrystalTag);
                Assert.AreEqual("portal-" + e.Tag, d.PortalTag);
                CollectionAssert.AreEqual(e.War, d.WarSpells.ToArray(), e.Tag);
                Assert.AreEqual(e.Vuln, d.VulnSpell, e.Tag);
                Assert.AreEqual(e.Crystal, d.CrystalWcid, e.Tag);
                Assert.AreEqual(e.Guardian, d.GuardianWcid, e.Tag);
                Assert.AreEqual(e.Summon, d.SummonWcid, e.Tag);
                Assert.AreEqual(e.Damage, d.DamageType, e.Tag);
                Assert.AreSame(WorldEventHazardElements.For(e.Visual), d.Visuals, e.Tag);
            }

            Assert.AreEqual(1008250u, HarbingerData.HeadWcid);
            Assert.AreEqual(1008263u, HarbingerData.HailstoneWcid);
            Assert.AreEqual("harbinger", HarbingerData.Kind);
            Assert.AreEqual(5, HarbingerData.CastEvery, "casts 1, 6, 11 ... are the vulnerability (Grael cadence)");
        }

        [TestMethod]
        public void VulnAndWarSpells_AreDistinct_AcrossElements()
        {
            var all = HarbingerData.Elements.SelectMany(e => e.WarSpells.Concat(new[] { e.VulnSpell })).ToArray();

            Assert.AreEqual(16, all.Length);
            Assert.AreEqual(16, all.Distinct().Count());
        }

        // ---- the script data ----

        [TestMethod]
        public void Script_CarriesTheRoom_TheHead_TheRegion_AndTheWaiver()
        {
            var s = HarbingerData.Script(HarbingerSettings.Defaults.HealthMultiplier, HarbingerSettings.Defaults.HeadHealthMultiplier);

            Assert.AreEqual((ushort)0x00BC, s.Landblock);
            Assert.AreEqual(1008250u, s.HeadWcid);
            Assert.AreEqual(PinnacleTier.Boss, s.HeadTier);
            Assert.AreEqual(HarbingerData.HeadPoint, s.HeadPoint);
            Assert.AreEqual(HarbingerData.ArrivalPoint, s.ArrivalPoint);
            Assert.AreEqual(HarbingerData.FightEntryPoint, s.FightEntryPoint);
            Assert.AreEqual(0u, s.SealWcid);
            Assert.IsTrue(s.SealPoints == null || s.SealPoints.Count == 0, "no seal blockers: the copy is sealed");
            Assert.AreEqual("harbinger", s.HazardRegionPreset);
            Assert.IsTrue(s.WaiveFallDamage);
            Assert.AreSame(HarbingerData.ExcludeRows, s.ExcludeRows);

            var required = s.RequiredWcids.ToArray();

            for (uint w = 1008251; w <= 1008267; w++)
                CollectionAssert.Contains(required, w);

            CollectionAssert.Contains(required, 1006963u, "Storm Wisp");
            CollectionAssert.Contains(required, 1006966u, "fire ball");
            CollectionAssert.Contains(required, HazardDefaults.EffectHostWcid);
            Assert.AreEqual(required.Length, required.Distinct().Count());
            CollectionAssert.Contains(required, HazardDefaults.PanelAcidWcid, "the acid fill spawns its panels");
            CollectionAssert.Contains(required, HazardDefaults.FillAcidWispWcid, "the acid fill spawns its wisps (wisps defaults to 16)");
            CollectionAssert.Contains(required, ACE.Server.Pinnacles.Matron.MatronData.AcidFlowWcid, "the wall strips spawn the Matron's acid flow panel");
            CollectionAssert.Contains(required, 1008281u, "round 3: the acid drip ball");
            Assert.AreEqual(17 + 1 + 3 + 3, required.Length);
            Assert.AreEqual(20.0, s.HealthMultiplierFor(s.HeadWcid), "the head multiplier given is laid on the head");
            Assert.AreEqual(2.0, s.HealthMultiplierFor(HarbingerData.Elements[0].CrystalWcid), "the crystal multiplier on the crystals");
        }
    }
}
