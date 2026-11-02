using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The fill run, driven through a recording fake of the world (Grael pit, default args).</summary>
    [TestClass]
    public class FillDirectorTests
    {
        private sealed class Obj
        {
            public int Id;
            public string Kind;
            public bool Alive = true;
            public Vector3 At;
        }

        private sealed class FakePort : IFillPort<Obj, string>
        {
            private int next;

            public Random Rng { get; } = new Random(7);

            public readonly List<Obj> All = new List<Obj>();
            public readonly List<(string Kind, Vector3 At, bool FaceDown, bool Moving)> Spawns = new List<(string, Vector3, bool, bool)>();
            public readonly List<(double Seconds, List<(Obj Handle, Vector3 End)> Group)> Moves = new List<(double, List<(Obj, Vector3)>)>();
            public readonly List<Obj> Halts = new List<Obj>();
            public readonly List<Obj> Destroys = new List<Obj>();
            public readonly List<(Obj Source, string Player, double Flat)> HitFlats = new List<(Obj, string, double)>();
            public readonly List<string> Logs = new List<string>();
            public readonly List<string> Says = new List<string>();
            public readonly List<(HazardOutcome Outcome, string Reason)> Ends = new List<(HazardOutcome, string)>();
            public readonly Queue<HazardControl> Controls = new Queue<HazardControl>();
            public List<(string Player, Vector3 Feet)> PlayerList = new List<(string, Vector3)>();

            /// <summary>Makes every down-facing surface panel (a doubled tile's partner) fail to spawn.</summary>
            public bool FailBackSpawns;

            public readonly List<float> Edges = new List<float>();

            public Obj SpawnPanel(Vector3 centre, float edge, bool faceDown, bool moving)
            {
                Edges.Add(edge);

                if (FailBackSpawns && faceDown && moving)
                    return null;

                // surface panels move; a down-facing mover is a doubled tile's back panel, a down-facing stayer is the under sheet
                var o = Make(!faceDown ? "tile" : moving ? "back" : "under", centre);
                Spawns.Add((o.Kind, centre, faceDown, moving));
                return o;
            }

            public Obj SpawnWisp(Vector3 at)
            {
                var o = Make("wisp", at);
                Spawns.Add((o.Kind, at, false, true));
                return o;
            }

            private Obj Make(string kind, Vector3 at)
            {
                var o = new Obj { Id = ++next, Kind = kind, At = at };
                All.Add(o);
                return o;
            }

            /// <summary>Null = every point has a cell (the Grael pit as far as these tests care); else the cell test.</summary>
            public Func<Vector3, bool> CellRule;

            public readonly List<Vector3> CellQueries = new List<Vector3>();

            public bool HasCell(Vector3 point)
            {
                CellQueries.Add(point);
                return CellRule?.Invoke(point) ?? true;
            }

            public bool Alive(Obj h) => h != null && h.Alive;

            public int Move(IReadOnlyList<(Obj Handle, Vector3 End)> group, double seconds)
            {
                Moves.Add((seconds, group.ToList()));
                return group.Count;
            }

            public void Halt(Obj h) => Halts.Add(h);

            public void Destroy(Obj h)
            {
                h.Alive = false;
                Destroys.Add(h);
            }

            public IReadOnlyList<(string Player, Vector3 Feet)> Players() => PlayerList;

            public int HitFlat(Obj source, string player, double flat)
            {
                HitFlats.Add((source, player, flat));
                return (int)flat;
            }

            public IReadOnlyList<HazardControl> TakeControls()
            {
                var list = Controls.ToList();
                Controls.Clear();
                return list;
            }

            public void Say(string line) => Says.Add(line);

            public void Log(string line) => Logs.Add(line);

            public void End(HazardOutcome outcome, string reason) => Ends.Add((outcome, reason));

            public void Send(string key, double value) => Controls.Enqueue(new HazardControl(key, value));

            /// <summary>Tile-group legs (positive seconds) only.</summary>
            public List<(double Seconds, List<(Obj Handle, Vector3 End)> Group)> TileLegs()
                => Moves.Where(m => m.Seconds > 0 && m.Group.Count > 0 && m.Group[0].Handle.Kind == "tile").ToList();
        }

        /// <summary>The one-sided sheet (doubled=0): the long-standing tests below count single tiles. The default (doubled on) is covered by <see cref="MakeDoubled"/> and the Doubled_ tests.</summary>
        private static (FillDirector<Obj, string> Director, FakePort Port, FillSettings Settings) Make(string args = "")
            => MakeDoubled("doubled=0 " + args);

        private static (FillDirector<Obj, string> Director, FakePort Port, FillSettings Settings) MakeDoubled(string args = "")
        {
            var region = HazardRegionPresets.Pinnacle(0);
            var settings = FillSettings.From(HazardArgs.Parse(args), region);
            var port = new FakePort();

            return (new FillDirector<Obj, string>(region, settings, port), port, settings);
        }

        [TestMethod]
        public void Doubled_Start_SpawnsAnUpAndADownPanelPerTileAtTheSameXy()
        {
            var (d, p, s) = MakeDoubled();

            Assert.IsTrue(s.Doubled);

            d.Start(0);

            var up = p.Spawns.Where(x => x.Kind == "tile").ToList();
            var back = p.Spawns.Where(x => x.Kind == "back").ToList();

            Assert.AreEqual(12, up.Count);
            Assert.AreEqual(12, back.Count, "two panels per tile");
            Assert.IsTrue(up.All(t => !t.FaceDown && t.Moving));
            Assert.IsTrue(back.All(t => t.FaceDown && t.Moving), "the partner faces down and moves with the surface");
            Assert.AreEqual(0, p.Spawns.Count(x => x.Kind == "under"), "the under sheet is not spawned until the surface is over head height");

            for (var i = 0; i < 12; i++)
                Assert.AreEqual(up[i].At, back[i].At, "same centre, tile " + i);

            StringAssert.Contains(d.Describe(), "tiles=12/12");
        }

        [TestMethod]
        public void Doubled_EveryPanel_UpBackAndUnder_IsDrawnFromTheOneSheetPlan()
        {
            var (d, p, s) = MakeDoubled();
            p.CellRule = FloorAt(-47.75f);   // with the face-down lift active too

            d.Start(0);
            d.Tick(1.0);
            p.Send("set_z", -44.0);
            d.Tick(2.0);

            var (edge, tiles) = FillRules.Sheet(HazardRegionPresets.Pinnacle(0), s.PanelSize, s.SheetMargin);
            var panels = p.Spawns.Where(x => x.Kind != "wisp").ToList();

            Assert.AreEqual(3 * tiles.Count, panels.Count, "up + back + under per tile");
            Assert.AreEqual(panels.Count, p.Edges.Count);
            Assert.IsTrue(p.Edges.All(e => e == edge), $"every panel at the plan's edge {edge}, not panel_size {s.PanelSize}: {string.Join(",", p.Edges.Distinct())}");
            Assert.IsTrue(edge < s.PanelSize, "control: the plan's edge differs from panel_size here, so the check bites");

            foreach (var kind in new[] { "tile", "back", "under" })
            {
                var xy = panels.Where(x => x.Kind == kind).Select(x => new Vector2(x.At.X, x.At.Y)).ToList();
                CollectionAssert.AreEqual(tiles.Select(t => t.Centre).ToList(), xy, $"{kind} panels sit on the plan's centres");
            }
        }

        /// <summary>A solid floor at <paramref name="floor"/>: only points strictly above it have a cell.</summary>
        private static Func<Vector3, bool> FloorAt(float floor) => p => p.Z > floor;

        [TestMethod]
        public void Doubled_FloorZWithinFaceClearanceOfTheFloor_DrawsBothPanelsAtTheLowestPlaceableZ_AsOnePlane()
        {
            // the Matron case on the Grael preset: floor_z sits 0.05 m over a solid floor at the ground plane
            var (d, p, s) = MakeDoubled();
            p.CellRule = FloorAt(-47.75f);

            d.Start(0);

            var up = p.Spawns.Where(x => x.Kind == "tile").ToList();
            var back = p.Spawns.Where(x => x.Kind == "back").ToList();

            Assert.AreEqual(12, up.Count);
            Assert.AreEqual(12, back.Count);

            var expected = FillRules.LowestFaceDownZ(Vector2.Zero, (float)s.FloorZ, FloorAt(-47.75f));

            Assert.IsTrue(expected - HazardPanels.FaceClearance > -47.75f, "control: the lifted probe clears the floor");
            Assert.IsTrue(expected - HazardPanels.FaceClearance - -47.75f < FillRules.FaceDownLiftStep + 1e-3f, "control: and by no more than one step, so it is the lowest: " + expected);

            for (var i = 0; i < 12; i++)
            {
                Assert.AreEqual(up[i].At, back[i].At, "the pair stays together, tile " + i);
                Assert.AreEqual(expected, up[i].At.Z, 1e-4f, $"tile {i} at the lifted floor");
            }

            Assert.AreEqual(1, up.Select(t => t.At.Z).Distinct().Count(), "one plane: every tile at the same z");
            Assert.AreEqual(1, p.Logs.Count(l => l.Contains("draws its panels no lower than")), "the lift is logged once: " + string.Join(" | ", p.Logs));
            Assert.AreEqual(s.FloorZ, d.SurfaceAt(0), 1e-9, "the SURFACE is unchanged - only where the panels are drawn");
        }

        [TestMethod]
        public void Doubled_LiftedFloor_DrainLegEndsAtTheLiftedZ_ButARisingLegIsUntouched()
        {
            var (d, p, _) = MakeDoubled();
            p.CellRule = FloorAt(-47.75f);

            d.Start(0);
            var floorDrawn = p.Spawns.Where(x => x.Kind == "tile").Min(t => t.At.Z);

            Assert.IsTrue(floorDrawn - HazardPanels.FaceClearance > -47.75f, "control: the sheet was drawn lifted, at " + floorDrawn);

            d.Tick(1.0);

            var rising = p.TileLegs().Single();
            Assert.IsTrue(rising.Group.All(g => Math.Abs(g.End.Z - (-47.15f)) < 0.0101f), "a leg well above the floor goes where the surface goes");

            p.Send("rate", -0.1);
            d.Tick(20.0);

            var drain = p.TileLegs().Last();
            Assert.IsTrue(drain.Group.All(g => g.End.Z >= floorDrawn - 1e-4f), "a drain leg never sends a panel under the lifted floor");
            Assert.IsTrue(drain.Group.All(g => g.End.Z <= floorDrawn + 0.0101f));
        }

        [TestMethod]
        public void Doubled_AFloorWellBelowFloorZ_LiftsNothing_AndOneSidedNeverAsks()
        {
            // Grael's real case: its ground plane floats 0.25 m over the block tops, so floor_z -47.70 is 0.30 m clear
            var (d, p, s) = MakeDoubled();
            p.CellRule = FloorAt(-48.0f);

            d.Start(0);

            Assert.IsTrue(p.Spawns.Where(x => x.Kind != "wisp").All(t => Math.Abs(t.At.Z + 47.70f) < 1e-4f), "unchanged: at -47.70");
            Assert.AreEqual(0, p.Logs.Count(l => l.Contains("draws its panels")));

            var (d1, p1, _) = Make();
            p1.CellRule = FloorAt(-47.75f);
            d1.Start(0);

            Assert.IsTrue(p1.Spawns.Where(x => x.Kind == "tile").All(t => Math.Abs(t.At.Z + 47.70f) < 1e-4f), "a one-sided sheet has no face-down panel, so it is never lifted");
        }

        /// <summary>
        /// The 2026-10-27 seam lines: at EVERY moment the director sends - spawn, a rising leg, a snap, a drain leg and the
        /// under sheet - all up faces share one z and all face-down faces share one z (the same z), over a floor that is
        /// uneven under the sheet, so per-tile face-down floors would differ. Every tile group moves in ONE Move call (one
        /// start, one duration).
        /// </summary>
        [TestMethod]
        public void Doubled_OverAnUnevenFloor_EveryUpFaceAndEveryDownFaceShareOneZ_AtEveryMove()
        {
            var (d, p, s) = MakeDoubled();

            // the floor is -47.80 west of x 15 and -47.72 east of it (both under floor_z -47.70): per tile the face-down floors would be ~0.08 m apart
            Func<Vector3, bool> uneven = q => q.Z > (q.X < 15f ? -47.80f : -47.72f);
            p.CellRule = uneven;

            Assert.IsTrue(Math.Abs(FillRules.LowestFaceDownZ(new Vector2(0f, -20f), (float)s.FloorZ, uneven) - FillRules.LowestFaceDownZ(new Vector2(30f, -20f), (float)s.FloorZ, uneven)) > 0.05f,
                "control: per-tile floors really differ here, so a per-tile lift would step the sheet");

            void OnePlane(IEnumerable<(string Kind, float Z)> faces, string when)
            {
                var list = faces.ToList();
                var ups = list.Where(f => f.Kind == "tile").Select(f => f.Z).Distinct().ToList();
                var downs = list.Where(f => f.Kind == "back").Select(f => f.Z).Distinct().ToList();

                Assert.AreEqual(1, ups.Count, $"{when}: up faces at {string.Join(",", ups)}");
                Assert.AreEqual(1, downs.Count, $"{when}: down faces at {string.Join(",", downs)}");
                Assert.AreEqual(ups[0], downs[0], $"{when}: both faces on one plane");
            }

            d.Start(0);
            OnePlane(p.Spawns.Where(x => x.Kind != "wisp").Select(x => (x.Kind, x.At.Z)), "spawn");

            d.Tick(1.0);
            OnePlane(p.TileLegs().Last().Group.Select(g => (g.Handle.Kind, g.End.Z)), "rising leg");

            p.Send("set_z", -44.0);
            d.Tick(2.0);
            var snap = p.Moves.Last(m => m.Seconds <= 0);
            OnePlane(snap.Group.Where(g => g.Handle.Kind != "wisp").Select(g => (g.Handle.Kind, g.End.Z)), "snap");

            var under = p.Spawns.Where(x => x.Kind == "under").Select(x => x.At.Z).Distinct().ToList();
            Assert.AreEqual(1, under.Count, "the under sheet is one plane too: " + string.Join(",", under));

            p.Send("rate", -2.0);
            d.Tick(3.0);
            d.Tick(30.0);
            var drain = p.TileLegs().Last();
            OnePlane(drain.Group.Select(g => (g.Handle.Kind, g.End.Z)), "drain leg");
            Assert.IsTrue(drain.Group.All(g => g.End.Z > -47.72f + HazardPanels.FaceClearance - 1e-4f), "and the one floor clears the HIGHEST floor under the sheet");

            foreach (var m in p.Moves.Where(m => m.Group.Any(g => g.Handle.Kind == "tile")))
                Assert.AreEqual(m.Group.Count(g => g.Handle.Kind == "tile"), m.Group.Count(g => g.Handle.Kind == "back"), "every tile move carries both faces in the same call");
        }

        [TestMethod]
        public void LowestFaceDownZ_ClimbsToTheFirstClearZ_AndLeavesAPointWithNoCellAlone()
        {
            var xy = new Vector2(1f, 2f);

            Assert.AreEqual(-59.0f, FillRules.LowestFaceDownZ(xy, -59.0f, FloorAt(-60f)), 1e-6f, "already clear");

            var z = FillRules.LowestFaceDownZ(xy, -59.95f, FloorAt(-60f));
            Assert.IsTrue(z - HazardPanels.FaceClearance > -60f && z <= -59.95f + HazardPanels.FaceClearance + 1e-4f, "lifted just clear: " + z);
            Assert.AreEqual(-59.785f, z, 0.0101f, "-59.79 or -59.78, float rounding at the boundary");

            Assert.AreEqual(-61f, FillRules.LowestFaceDownZ(xy, -61f, FloorAt(-60f)), 1e-6f, "under the floor itself: no cell, unchanged (the spawn's own refusal reports it)");
            Assert.AreEqual(-59.95f, FillRules.LowestFaceDownZ(xy, -59.95f, null), 1e-6f, "no lookup: unchanged");
        }

        [TestMethod]
        public void Doubled_Leg_MovesBothPanelsOfEveryTileToTheSameEnd()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);
            d.Tick(1.0);

            var tileMoves = p.Moves.Single(m => m.Group.Any(g => g.Handle.Kind == "tile"));

            Assert.AreEqual(24, tileMoves.Group.Count, "12 up + 12 back");

            var ups = tileMoves.Group.Where(g => g.Handle.Kind == "tile").ToList();
            var backs = tileMoves.Group.Where(g => g.Handle.Kind == "back").ToList();

            Assert.AreEqual(12, ups.Count);
            Assert.AreEqual(12, backs.Count);

            for (var i = 0; i < 12; i++)
                Assert.AreEqual(ups[i].End, backs[i].End, "tile " + i);
        }

        [TestMethod]
        public void Doubled_RateZeroMidLeg_HaltsBothPanelsOfEveryTile()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);
            d.Tick(1.0);
            p.Send("rate", 0);
            d.Tick(5.0);

            Assert.AreEqual(40, p.Halts.Count, "12 up + 12 back + 16 wisps");
            Assert.AreEqual(40, p.Halts.Distinct().Count());
            Assert.AreEqual(12, p.Halts.Count(h => h.Kind == "back"));
        }

        [TestMethod]
        public void Doubled_SetZ_SnapsBothPanels_AndTheUnderSheetStaysSingle()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);
            d.Tick(1.0);
            p.Send("set_z", -44.0);
            d.Tick(2.0);

            var snap = p.Moves.Single(m => m.Seconds <= 0);

            Assert.AreEqual(40, snap.Group.Count, "24 surface panels + 16 wisps");
            Assert.IsTrue(snap.Group.Where(g => g.Handle.Kind != "wisp").All(g => Math.Abs(g.End.Z + 44.0f) < 0.0101f));
            Assert.AreEqual(12, p.Spawns.Count(x => x.Kind == "under"), "the under sheet is unchanged: one panel per tile");
        }

        [TestMethod]
        public void Doubled_ALostPanelIsALostTile_AndTheSurvivingHalfStillMoves()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);
            p.All.First(o => o.Kind == "back").Alive = false;
            d.Tick(1.0);

            StringAssert.Contains(d.Describe(), "tiles=11/12");
            Assert.AreEqual(0, p.Ends.Count, "one lost tile does not abort the run");

            var tileMoves = p.Moves.Single(m => m.Group.Any(g => g.Handle.Kind == "tile"));

            Assert.AreEqual(23, tileMoves.Group.Count, "the dead panel is skipped, its partner still rides");
        }

        [TestMethod]
        public void Doubled_ALostUpPanelWithASurvivingBack_IsALostTile_AndTheBackStillMoves()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);
            p.All.First(o => o.Kind == "tile").Alive = false;
            d.Tick(1.0);

            StringAssert.Contains(d.Describe(), "tiles=11/12");
            Assert.AreEqual(0, p.Ends.Count);

            var tileMoves = p.Moves.Single(m => m.Group.Any(g => g.Handle.Kind == "tile"));

            Assert.AreEqual(12, tileMoves.Group.Count(g => g.Handle.Kind == "back"), "the surviving back rides");
            Assert.AreEqual(11, tileMoves.Group.Count(g => g.Handle.Kind == "tile"));
        }

        [TestMethod]
        public void Doubled_UnderSheetOnThenOff_NeverDestroysASurfacePartner()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);
            d.Tick(1.0);
            p.Send("set_z", -44.0);
            d.Tick(2.0);
            Assert.AreEqual(12, p.Spawns.Count(x => x.Kind == "under"));

            p.Send("set_z", -47.0);
            d.Tick(3.0);

            Assert.AreEqual(12, p.Destroys.Count(o => o.Kind == "under"), "the under sheet came down");
            Assert.AreEqual(0, p.Destroys.Count(o => o.Kind == "back"), "no surface partner was destroyed");
            Assert.AreEqual(0, p.Destroys.Count(o => o.Kind == "tile"));
        }

        [TestMethod]
        public void Doubled_DamageStillLands_WhenTileZeroLosesItsBack()
        {
            var (d, p, _) = MakeDoubled();

            p.PlayerList.Add(("low", new Vector3(10, -20, -47.75f)));
            d.Start(0);

            var tile0 = p.All.First(o => o.Kind == "tile");
            p.All.First(o => o.Kind == "back").Alive = false;
            d.Tick(2.0);

            var hit = p.HitFlats.Single();

            Assert.AreEqual("tile", hit.Source.Kind);
            Assert.AreNotSame(tile0, hit.Source, "sourced from another whole tile");
            Assert.IsTrue(hit.Source.Alive);
        }

        [TestMethod]
        public void Doubled_EveryTileLosingOnePanel_Aborts()
        {
            var (d, p, _) = MakeDoubled();

            d.Start(0);

            foreach (var o in p.All.Where(o => o.Kind == "back"))
                o.Alive = false;

            d.Tick(1.0);

            Assert.AreEqual((HazardOutcome.Aborted, "every surface panel was lost"), p.Ends.Single());
        }

        [TestMethod]
        public void Doubled_APartnerThatFailsToSpawn_CountsTheTileMissed_AndTakesDownTheOtherHalf()
        {
            var (d, p, _) = MakeDoubled();

            p.FailBackSpawns = true;
            d.Start(0);

            Assert.AreEqual(12, d.TilesMissed, "every back panel failed, so every tile is missed");
            Assert.AreEqual(12, p.Destroys.Count(o => o.Kind == "tile"), "the up panel that did spawn is destroyed again");
            StringAssert.Contains(d.Describe(), "tiles=0/12");
        }

        [TestMethod]
        public void Start_SpawnsTwelveTilesAndSixteenWisps_MovesNothing()
        {
            var (d, p, _) = Make();

            d.Start(0);

            var tiles = p.Spawns.Where(s => s.Kind == "tile").ToList();

            Assert.AreEqual(12, tiles.Count);
            Assert.AreEqual(16, p.Spawns.Count(s => s.Kind == "wisp"));
            Assert.IsTrue(tiles.All(t => !t.FaceDown && t.Moving), "surface tiles face up and move");
            Assert.IsTrue(tiles.All(t => Math.Abs(t.At.Z + 47.70f) < 1e-4f), "all at -47.70, one plane");
            Assert.AreEqual(0, p.Moves.Count);
            Assert.AreEqual(0, p.Halts.Count);
        }

        [TestMethod]
        public void FirstTick_StartsTenSecondLegs()
        {
            var (d, p, s) = Make();

            d.Start(0);
            d.Tick(1.0);

            Assert.AreEqual(2, p.Moves.Count, "one group for the tiles, one for the wisps");
            Assert.IsTrue(p.Moves.All(m => Math.Abs(m.Seconds - 10.0) < 1e-9));

            var tiles = p.TileLegs().Single();

            foreach (var (h, end) in tiles.Group)
                Assert.IsTrue(Math.Abs(end.Z - (-47.15f)) < 0.0101f, "tile end z " + end.Z);

            var wisps = p.Moves.Single(m => m.Group[0].Handle.Kind == "wisp");

            foreach (var (_, end) in wisps.Group)
                Assert.AreEqual(FillRules.WispZ(-47.15, s.GroundZ, -42.05, s.WispScale), end.Z, 1e-4f);

            d.Tick(1.1);
            Assert.AreEqual(2, p.Moves.Count, "no new leg until the old one ends");
        }

        [TestMethod]
        public void FillDirector_RateZeroMidLeg_HaltsOnce()
        {
            var (d, p, _) = Make();

            d.Start(0);
            d.Tick(1.0);
            p.Send("rate", 0);
            d.Tick(5.0);

            Assert.AreEqual(28, p.Halts.Count, "12 tiles + 16 wisps");
            Assert.AreEqual(28, p.Halts.Distinct().Count());

            d.Tick(6.0);
            d.Tick(20.0);

            Assert.AreEqual(28, p.Halts.Count, "no second halt");
            Assert.AreEqual(2, p.Moves.Count, "and no new leg");
        }

        [TestMethod]
        public void NegativeRate_RunsATenSecondLegToTheFloor()
        {
            var (d, p, _) = Make();

            d.Start(0);
            d.Tick(1.0);
            p.Send("rate", -0.1);
            d.Tick(20.0);

            var leg = p.TileLegs().Last();

            Assert.AreEqual(10.0, leg.Seconds, 1e-9);
            Assert.IsTrue(leg.Group.All(g => Math.Abs(g.End.Z + 47.70f) < 0.0101f));
        }

        [TestMethod]
        public void Target_LegsTenTenFourThenNone()
        {
            var (d, p, _) = Make();

            d.Start(0);
            d.Tick(1.0);
            p.Send("target", -46.0);

            foreach (var t in new[] { 10.0, 20.0, 30.0, 34.0, 40.0, 50.0 })
                d.Tick(t);

            var legs = p.TileLegs().Select(l => l.Seconds).Skip(1).ToList();

            CollectionAssert.AreEqual(new[] { 10.0, 10.0, 4.0 }, legs.Select(x => Math.Round(x, 6)).ToList(),
                "from t=10 the surface is 24 s from the target: legs of 10, 10 and 4 s, then none: " + string.Join(",", legs));
            Assert.AreEqual(28, p.Halts.Count, "arrival halts every handle once");
        }

        [TestMethod]
        public void FillDirector_SetZ_SnapsAndShowsUnderSheet()
        {
            var (d, p, _) = Make();

            d.Start(0);
            d.Tick(1.0);
            p.Send("set_z", -44.0);
            d.Tick(2.0);

            var snap = p.Moves.Single(m => m.Seconds <= 0);

            Assert.AreEqual(28, snap.Group.Count);
            Assert.IsTrue(snap.Group.Where(g => g.Handle.Kind == "tile").All(g => Math.Abs(g.End.Z + 44.0f) < 0.0101f));

            var under = p.Spawns.Where(s => s.Kind == "under").ToList();

            Assert.AreEqual(12, under.Count);
            Assert.IsTrue(under.All(u => u.FaceDown && !u.Moving), "under tiles face down and stay put");
            Assert.IsTrue(under.All(u => Math.Abs(u.At.Z + 45.75f) < 1e-4f || Math.Abs(u.At.Z + 45.76f) < 1e-4f), "at -45.75 / -45.76");

            p.Send("set_z", -47.0);
            d.Tick(3.0);

            Assert.AreEqual(12, p.Destroys.Count(o => o.Kind == "under"));
        }

        [TestMethod]
        public void SetZ_SnapThenTheNextTickStartsANewTileLeg()
        {
            var (d, p, _) = Make();

            d.Start(0);
            d.Tick(1.0);
            p.Send("set_z", -44.0);
            d.Tick(2.0);

            var legsBefore = p.TileLegs().Count;

            d.Tick(2.1);

            Assert.AreEqual(legsBefore + 1, p.TileLegs().Count, "a new tile leg with seconds > 0 follows the snap");
        }

        [TestMethod]
        public void Damage_OneFlatHitPerTick_ForPlayersBelowTheSurface()
        {
            var (d, p, _) = Make();

            p.PlayerList.Add(("low", new Vector3(10, -20, -47.75f)));
            p.PlayerList.Add(("high", new Vector3(10, -20, -46.0f)));

            d.Start(0);
            d.Tick(2.0);

            Assert.AreEqual(1, p.HitFlats.Count);
            Assert.AreEqual("low", p.HitFlats[0].Player);
            Assert.AreEqual(43.75, p.HitFlats[0].Flat, 1e-6, "surface -47.60 over feet -47.75: 40 + 25 x 0.15");
            Assert.AreEqual("tile", p.HitFlats[0].Source.Kind);

            d.Tick(3.0);
            Assert.AreEqual(1, p.HitFlats.Count, "tick is 2 s");

            d.Tick(4.0);
            Assert.AreEqual(2, p.HitFlats.Count);
        }

        [TestMethod]
        public void DamageScaleControl_MultipliesTheHit()
        {
            var (d, p, _) = Make();

            p.PlayerList.Add(("low", new Vector3(10, -20, -47.75f)));
            d.Start(0);
            p.Send("damage_scale", 2);
            d.Tick(2.0);

            Assert.AreEqual(87.5, p.HitFlats.Single().Flat, 1e-6);
        }

        [TestMethod]
        public void Stop_EndsStopped_AndLaterTicksDoNothing()
        {
            var (d, p, _) = Make();

            d.Start(0);
            p.Send("stop", 1);
            d.Tick(1.0);

            Assert.AreEqual((HazardOutcome.Stopped, "control stop"), p.Ends.Single());

            d.Tick(2.0);
            Assert.AreEqual(1, p.Ends.Count);
            Assert.AreEqual(0, p.Moves.Count);
        }

        [TestMethod]
        public void Duration_EndsCompletedAtTheDeadline()
        {
            var (d, p, _) = Make("duration=30");

            d.Start(0);
            d.Tick(29.0);
            Assert.AreEqual(0, p.Ends.Count);

            d.Tick(30.0);
            Assert.AreEqual(HazardOutcome.Completed, p.Ends.Single().Outcome);
        }

        [TestMethod]
        public void EveryTileLost_Aborts()
        {
            var (d, p, _) = Make();

            d.Start(0);

            foreach (var o in p.All.Where(o => o.Kind == "tile"))
                o.Alive = false;

            d.Tick(1.0);

            Assert.AreEqual((HazardOutcome.Aborted, "every surface panel was lost"), p.Ends.Single());
        }

        [TestMethod]
        public void UnknownControl_LogsOnce_AndPlansNothing()
        {
            var (d, p, _) = Make();

            d.Start(0);
            d.Tick(1.0);

            var moves = p.Moves.Count;

            p.Send("bogus", 1);
            d.Tick(2.0);

            Assert.AreEqual(1, p.Logs.Count(l => l.Contains("bogus")));
            Assert.AreEqual(moves, p.Moves.Count, "an unknown key does not bump the surface version");
            Assert.AreEqual(0, p.Ends.Count);
        }

        [TestMethod]
        public void Describe_NamesTheSurface()
        {
            var (d, _, _) = Make();

            d.Start(0);

            StringAssert.Contains(d.Describe(), "tiles=12/12");
            StringAssert.Contains(d.Describe(), "wisps=16");
        }
    }
}
