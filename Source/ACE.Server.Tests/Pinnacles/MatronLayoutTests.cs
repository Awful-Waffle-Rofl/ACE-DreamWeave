using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Matron;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Dat-free pins of the Matron room data, the pure layout, the fill preset and the settings record. Reads no
    /// PropertyManager value. The dat-backed proof of every point is HazardMechanics.MatronRoomGeometryTests.
    /// </summary>
    [TestClass]
    public class MatronLayoutTests
    {
        private static IEnumerable<MatronOpening> All => MatronData.Openings;

        [TestMethod]
        public void Platforms_FourWallFlushPads_AlternatingSides_EvenRisesFromTheFloor_TopPadOneHopUnderTheSill()
        {
            Assert.AreEqual(4, MatronLayout.PlatformCount);
            Assert.AreEqual(1.3333f, MatronLayout.PlatformTopUnderSill);
            Assert.AreEqual((-49.3333f - -60f) / 4f, MatronLayout.PlatformRise, 1e-5f, "(top pad top - floor) / 4");
            Assert.AreEqual(2.6667f, MatronLayout.PlatformRise, 1e-4f);
            Assert.AreEqual(2.0f, MatronLayout.PlatformGap);
            Assert.AreEqual(0f, MatronLayout.PlatformWallGap, "flush (measured, MatronRoomGeometryTests)");
            Assert.IsTrue(MatronLayout.PlatformWallGap >= 0f, "never into the wall");

            foreach (var o in All)
            {
                var q = MatronLayout.Platforms(o);
                var along = MatronLayout.Along(o);

                Assert.AreEqual(4, q.Count, o.Name);
                Assert.AreEqual(0f, Vector2.Dot(along, o.Outward), 1e-6f, $"{o.Name}: along runs in the wall plane");

                for (var k = 0; k < q.Count; k++)
                {
                    var c = new Vector2(q[k].X, q[k].Y);
                    var depth = Vector2.Dot(o.Centre - c, o.Outward);          // in front of the wall, into the room
                    var lateral = Vector2.Dot(c - o.Centre, along);           // along the wall from the centreline
                    var top = q[k].Z + MatronLayout.PlatformThickness;

                    Assert.AreEqual(1.0f, depth, 1e-4f, $"{o.Name} platform {k}: centre half a slab off the wall, its wall-side edge flush");
                    Assert.AreEqual(1.0f + 2.0f / 2f, Math.Abs(lateral), 1e-4f, $"{o.Name} platform {k}: half a slab plus half the gap off the centreline");
                    Assert.AreEqual(-60f + 2.6667f * (k + 1), top, 1e-3f, $"{o.Name} platform {k} top");

                    if (k > 0)
                    {
                        var prevLateral = Vector2.Dot(new Vector2(q[k - 1].X, q[k - 1].Y) - o.Centre, along);

                        Assert.IsTrue(Math.Sign(lateral) == -Math.Sign(prevLateral), $"{o.Name} platform {k}: alternates sides");
                        Assert.AreEqual(2.0f, Math.Abs(lateral - prevLateral) - 2f * MatronLayout.PlatformHalfSize, 1e-4f, $"{o.Name} platform {k}: 2 m from the one before along the wall");
                        Assert.AreEqual(2.6667f, top - (q[k - 1].Z + MatronLayout.PlatformThickness), 1e-3f, $"{o.Name} platform {k}: one rise above the one before");
                    }
                }

                // the first is one rise off the floor; the last is where today's top pad was, one hop under the sill, its inner edge inside the opening's span
                Assert.AreEqual(2.6667f, q[0].Z + 0.4f - (-60f), 1e-3f, o.Name);
                Assert.AreEqual(1.3333f, -48f - (q[3].Z + 0.4f), 1e-3f, o.Name);
                Assert.AreEqual(1, MatronLayout.PlatformSide(3), "the top pad on the +1 side, as before");
                var last = Math.Abs(Vector2.Dot(new Vector2(q[3].X, q[3].Y) - o.Centre, along));
                Assert.IsTrue(last - 1.0f < 3.33f / 2f, $"{o.Name}: the last platform's inner edge is under the sill");
            }

            // the NW opening spelled out (along = (-1, 0), wall at y -115): every pad's centre at y -116
            var nw = MatronLayout.Platforms(MatronData.Opening(1));
            var expected = new[] { (202f, -116f, -57.7333f), (198f, -116f, -55.0667f), (202f, -116f, -52.4f), (198f, -116f, -49.7333f) };
            for (var k = 0; k < 4; k++)
            {
                Assert.AreEqual(new Vector2(expected[k].Item1, expected[k].Item2), new Vector2(nw[k].X, nw[k].Y), $"NW platform {k}");
                Assert.AreEqual(expected[k].Item3, nw[k].Z, 1e-3f, $"NW platform {k} origin z");
            }
        }
        /// <summary>The smallest gap from a pad's top up to the underside of another pad overlapping its footprint (MaxValue when none overlaps above).</summary>
        private static float MinHeadRoom(IReadOnlyList<PinnaclePoint> q, out int stacked)
        {
            const float Half = MatronLayout.PlatformHalfSize;
            var min = float.MaxValue;
            stacked = 0;

            for (var a = 0; a < q.Count; a++)
            {
                var topA = q[a].Z + MatronLayout.PlatformThickness;

                for (var b = 0; b < q.Count; b++)
                {
                    if (a == b || q[b].Z <= topA)
                        continue;

                    if (Math.Abs(q[a].X - q[b].X) < 2f * Half && Math.Abs(q[a].Y - q[b].Y) < 2f * Half)
                    {
                        stacked++;
                        min = Math.Min(min, q[b].Z - topA);
                    }
                }
            }

            return min;
        }

        [TestMethod]
        public void Platforms_HeadClearance_NoPadOverAnotherWithin183PlusTheSlab()
        {
            // a player's collision height (1.83 m, `cells --modelbounds=0x02000001`) plus the slab's 0.4 m: no other pad's
            // footprint overlaps a pad's footprint with its underside within that of the pad's top
            const float Head = 1.83f + 0.4f;

            // the stricter jump rule: standing height plus the rise the jump must make. It still holds with the 2.67 m rises:
            // pads alternate sides, so the nearest pad overhead is two levels up, 2 x 2.6667 - 0.4 = 4.93 m over, against 4.50
            const float Jump = 1.83f + MatronLayout.PlatformRise;
            var anyStacked = 0;

            foreach (var o in MatronData.Openings)
            {
                var room = MinHeadRoom(MatronLayout.Platforms(o), out var stacked);
                anyStacked += stacked;

                Assert.IsTrue(room >= Head, $"{o.Name}: a pad stands {room:0.00} m over another's top, under {Head} m");
                Assert.IsTrue(room >= Jump, $"{o.Name}: a pad stands {room:0.00} m over another's top, under the {Jump:0.00} m a jump needs");
                Assert.AreEqual(2f * MatronLayout.PlatformRise - MatronLayout.PlatformThickness, room, 1e-3f, $"{o.Name}: the nearest pad overhead is two levels up");
            }

            Assert.IsTrue(anyStacked > 0, "control: some pads do stand over others, so the check is reached");

            // control: the same four pads at the old 1.3333 m rise put the pad two levels up 2.27 m over, which the jump rule
            // (1.83 + that rise) catches, so the rule discriminates
            const float OldRise = 1.3333f;
            var squashed = MatronLayout.Platforms(MatronData.Opening(1)).Select((p, k) => p with { Z = -60f + OldRise * (k + 1) - MatronLayout.PlatformThickness }).ToList();
            var tight = MinHeadRoom(squashed, out _);
            Assert.AreEqual(2f * OldRise - MatronLayout.PlatformThickness, tight, 1e-3f, "control: two levels up at the old rise is 2.27 m over");
            Assert.IsTrue(tight < 1.83f + OldRise, "control: the jump rule fails it");
        }
        [TestMethod]
        public void Platforms_StayInTheFloorPlan_OffTheWallStrip_AndClearHerBodyAtEveryStandPoint()
        {
            const float Half = MatronLayout.PlatformHalfSize;

            foreach (var o in All)
            {
                foreach (var p in MatronLayout.Platforms(o))
                {
                    foreach (var (dx, dy) in new[] { (-Half, -Half), (-Half, Half), (Half, -Half), (Half, Half) })
                        Assert.IsTrue(MatronData.InFloorPlan(p.X + dx, p.Y + dy), $"{o.Name}: footprint corner ({p.X + dx}, {p.Y + dy}) of the platform at ({p.X}, {p.Y}) is off the floor plan");

                    // never across the wall strip: the strip runs on the centre line, the pads beside it
                    var lateral = Vector2.Dot(new Vector2(p.X, p.Y) - o.Centre, MatronLayout.Along(o));
                    Assert.IsTrue(Math.Abs(lateral) - Half > 0f, $"{o.Name}: the platform at ({p.X}, {p.Y}) crosses the wall strip's centre line");
                }
            }

            // Her collision body is radius 1.50 at scale 1.5 (`cells --modelbounds` of her setup): every pad footprint
            // of every opening clears every stand point by that plus a 0.3 m margin. Control: the closest pair is under 2 m.
            const float BodyClearance = 1.50f + 0.3f;
            var closest = float.MaxValue;

            foreach (var stand in All.Select(x => x.Stand))
            {
                foreach (var o in All)
                {
                    foreach (var p in MatronLayout.Platforms(o))
                    {
                        var dx = Math.Max(0f, Math.Abs(stand.X - p.X) - Half);
                        var dy = Math.Max(0f, Math.Abs(stand.Y - p.Y) - Half);
                        var d = MathF.Sqrt(dx * dx + dy * dy);
                        closest = Math.Min(closest, d);
                        Assert.IsTrue(d >= BodyClearance - 1e-4f, $"platform at ({p.X}, {p.Y}) is {d:0.000} m from the stand point ({stand.X}, {stand.Y}), under {BodyClearance}");
                    }
                }
            }

            Assert.IsTrue(closest < 2.0f, $"control: the closest platform is {closest:0.000} m from a stand point; the check proves nothing if none is near");
        }
        [TestMethod]
        public void WallStrip_ModesPanelsFogBoth_RoomFacingFlowPanels_DownTheWall_FogAtTheFoot_ForEveryOpening()
        {
            // the constants as shipped (pinned so a change shows)
            Assert.AreEqual(0, MatronLayout.WallFxPanels);
            Assert.AreEqual(1, MatronLayout.WallFxFog);
            Assert.AreEqual(2, MatronLayout.WallFxBoth);
            Assert.AreEqual(MatronLayout.WallFxBoth, MatronLayout.WallFxDefault);
            Assert.AreEqual(1007555u, MatronData.LavaFlowWcid);
            Assert.AreEqual(1007556u, MatronData.AcidFlowWcid);
            Assert.AreEqual(0x33000C0Eu, MatronLayout.WallFogAcidScript, "the falling green fog (playtest 4: the swirling 0x33000D35 is gone)");
            Assert.AreEqual(0x330007A6u, MatronLayout.WallFogLavaScript, "the orange haze (playtest 5: the red 0x330010F3 was too small and too red)");
            CollectionAssert.AreEqual(new[] { 0x33000C0Eu, 0x33000A44u, 0x330012DDu }, MatronLayout.AcidFogs.Select(x => x.Script).ToArray());
            CollectionAssert.AreEqual(new[] { 0x330007A6u, 0x330010F3u, 0x3300131Au, 0x33000770u }, MatronLayout.LavaFogs.Select(x => x.Script).ToArray());
            CollectionAssert.AreEqual(new[] { 4.5f, 8.5f }, MatronLayout.AcidFogs[0].Lifts, "playtest 5: two falling sheets, the exit to the floor");
            CollectionAssert.AreEqual(new[] { 1f, 5f }, MatronLayout.LavaFogs[0].Lifts, "two orange hosts up the wall's foot");
            Assert.AreEqual(0f, MatronLayout.WallPanelSpinDeg);
            CollectionAssert.AreEqual(new[] { -49.6667f, -53.0f, -56.3333f, -59.6667f }, MatronLayout.WallPanelRowZ.ToArray());
            Assert.AreEqual(0.21f + 0.01f * 0.3333f, MatronLayout.WallPanelFaceOffset, 1e-6f, "the drawn face where the old curtain's stood");
            Assert.AreEqual(0.0467f, MatronLayout.WallPanelOffset, 1e-4f, "origin 0.0467 off the wall");

            foreach (var o in All)
            {
                foreach (var lava in new[] { false, true })
                {
                    var name0 = $"{o.Name} {(lava ? "lava" : "acid")}";
                    var panels = MatronLayout.WallStrip(o, lava, MatronLayout.WallFxPanels);
                    var fog = MatronLayout.WallStrip(o, lava, MatronLayout.WallFxFog);
                    var both = MatronLayout.WallStrip(o, lava, MatronLayout.WallFxBoth);

                    Assert.AreEqual(4, panels.Count, $"{name0}: four rows, one room-facing panel each");
                    Assert.AreEqual(2, fog.Count, $"{name0}: two fog hosts, stacked up the wall");
                    CollectionAssert.AreEqual(panels.Concat(fog).ToArray(), both.ToArray(), $"{name0}: both = the panels then the fog");

                    for (var i = 0; i < panels.Count; i++)
                    {
                        var h = panels[i];
                        var name = $"{name0} panel {i}";
                        var q = new Quaternion(h.At.QX, h.At.QY, h.At.QZ, h.At.QW);
                        var normal = Vector3.Transform(Vector3.UnitZ, q);
                        var down = Vector3.Transform(Vector3.UnitY, q);
                        var c = new Vector2(h.At.X, h.At.Y);

                        Assert.AreEqual(lava ? MatronData.LavaFlowWcid : MatronData.AcidFlowWcid, h.Wcid, name);
                        Assert.AreEqual(0.3333f, h.Scale, 1e-6f, name);
                        Assert.IsNull(h.Translucency, name);
                        Assert.AreEqual(0u, h.Script, name);
                        Assert.AreEqual(MatronLayout.WallPanelRowZ[i], h.At.Z, 1e-6f, name);
                        Assert.AreEqual(MatronData.HallCell(h.At.X, h.At.Y, h.At.Z), h.At.Cell, name);
                        Assert.AreEqual(0f, Vector2.Dot(c - o.Centre, MatronLayout.Along(o)), 1e-4f, $"{name}: on the centre line");
                        Assert.AreEqual(0.0467f, Vector2.Dot(o.Centre - c, o.Outward), 1e-4f, $"{name}: origin 0.0467 m in front of the wall");

                        // the face (local +Z) looks into the room, upright; local +Y points straight down the wall
                        Assert.AreEqual(-1f, Vector2.Dot(new Vector2(normal.X, normal.Y), o.Outward), 1e-4f, $"{name}: faces the room");
                        Assert.AreEqual(0f, normal.Z, 1e-4f, $"{name}: upright");
                        Assert.AreEqual(-1f, down.Z, 1e-4f, $"{name}: local +Y down the wall");

                        // the drawn quad (local z 0.5 x scale up the normal) lands where the old curtain's face stood
                        var face = new Vector2(h.At.X + normal.X * 0.5f * h.Scale, h.At.Y + normal.Y * 0.5f * h.Scale);
                        Assert.AreEqual(0.21f + 0.01f * 0.3333f, Vector2.Dot(o.Centre - face, o.Outward), 1e-4f, $"{name}: drawn face at the old curtain face");
                    }

                    var expectedZ = lava ? new[] { -58.995f, -54.995f } : new[] { -55.495f, -51.495f };
                    for (var j = 0; j < fog.Count; j++)
                    {
                        var h = fog[j];
                        var hc = new Vector2(h.At.X, h.At.Y);
                        Assert.AreEqual(MatronData.EffectHostWcid, h.Wcid);
                        Assert.AreEqual(1006952u, h.Wcid);
                        Assert.AreEqual(lava ? 0x330007A6u : 0x33000C0Eu, h.Script, name0);
                        Assert.AreEqual(1f, h.Scale, name0);
                        Assert.AreEqual(expectedZ[j], h.At.Z, 1e-5f, $"{name0} host {j}: stacked up the wall's foot");
                        var fy = Vector3.Transform(Vector3.UnitY, new Quaternion(h.At.QX, h.At.QY, h.At.QZ, h.At.QW));
                        Assert.AreEqual(-1f, Vector2.Dot(new Vector2(fy.X, fy.Y), o.Outward), 1e-4f, $"{name0} host {j}: local +Y faces the room");
                        Assert.AreEqual(1.0f, Vector2.Dot(o.Centre - hc, o.Outward), 1e-4f, $"{name0} host {j}: 1 m out from the wall");
                        Assert.AreEqual(0f, Vector2.Dot(hc - o.Centre, MatronLayout.Along(o)), 1e-4f, $"{name0} host {j}: on the centre line");
                        Assert.AreEqual(MatronData.HallCell(h.At.X, h.At.Y, h.At.Z), h.At.Cell, $"{name0} host {j}");
                    }

                    // every pick of the element: its script and lift; out of range throws
                    var fogs = lava ? MatronLayout.LavaFogs : MatronLayout.AcidFogs;
                    for (var k = 0; k < fogs.Count; k++)
                    {
                        var pick = MatronLayout.WallStrip(o, lava, MatronLayout.WallFxFog, k);
                        Assert.AreEqual(fogs[k].Lifts.Length, pick.Count, $"{name0} pick {k}: one host per lift");
                        for (var j = 0; j < pick.Count; j++)
                        {
                            Assert.AreEqual(fogs[k].Script, pick[j].Script, $"{name0} pick {k}");
                            Assert.AreEqual(-59.995f + fogs[k].Lifts[j], pick[j].At.Z, 1e-5f, $"{name0} pick {k} host {j}");
                        }
                    }
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronLayout.WallStrip(o, lava, MatronLayout.WallFxFog, fogs.Count));
                }
            }

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronLayout.WallStrip(MatronData.Opening(1), false, 3));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronLayout.WallStrip(MatronData.Opening(1), false, -1));
        }

        [TestMethod]
        public void WallPanelFacing_IsStandUpMinus90AboutXThenYaw_AndSpinFlipsTheScroll()
        {
            // facing +Y (the NW opening's room side is -Y; here the bare helper): stand-up alone, w 0.7071, x -0.7071
            var q = MatronLayout.WallPanelFacing(new Vector2(0f, 1f));
            Assert.AreEqual(0.7071f, q.W, 1e-4f);
            Assert.AreEqual(-0.7071f, q.X, 1e-4f);
            Assert.AreEqual(0f, q.Y, 1e-4f);
            Assert.AreEqual(0f, q.Z, 1e-4f);

            foreach (var n in new[] { new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f) })
            {
                var r = MatronLayout.WallPanelFacing(n);
                var normal = Vector3.Transform(Vector3.UnitZ, r);
                var down = Vector3.Transform(Vector3.UnitY, r);
                var right = Vector3.Transform(Vector3.UnitX, r);

                Assert.AreEqual(n.X, normal.X, 1e-4f, $"{n}: normal x");
                Assert.AreEqual(n.Y, normal.Y, 1e-4f, $"{n}: normal y");
                Assert.AreEqual(-1f, down.Z, 1e-4f, $"{n}: local +Y down");
                Assert.AreEqual(0f, right.Z, 1e-4f, $"{n}: local +X level");

                // the scroll's assumed local direction (-1, +1) has a downward component on every wall
                var scroll = Vector3.Transform(Vector3.Normalize(new Vector3(-1f, 1f, 0f)), r);
                Assert.AreEqual(-0.7071f, scroll.Z, 1e-4f, $"{n}: 45 degrees down the wall");
            }
        }
        [TestMethod]
        public void DoorAndBackPanel_StandInTheNiche_FacingTheRoom_IdenticallyShapedForEveryOpening()
        {
            foreach (var o in All)
            {
                var door = MatronLayout.Door(o);
                var expected = o.Centre + o.Outward * (3.33f - MatronLayout.DoorInset);

                Assert.AreEqual(o.NicheCell, door.Cell, o.Name);
                Assert.AreEqual(expected.X, door.X, 1e-4f, o.Name);
                Assert.AreEqual(expected.Y, door.Y, 1e-4f, o.Name);
                Assert.AreEqual(-48f, door.Z, 1e-4f, o.Name);

                // a door's local +Y is its front; after the yaw it points back down the niche, into the room
                var front = Vector3.Transform(Vector3.UnitY, new Quaternion(door.QX, door.QY, door.QZ, door.QW));
                Assert.AreEqual(-1f, Vector2.Dot(new Vector2(front.X, front.Y), o.Outward), 1e-4f, $"{o.Name}: door faces the room");

                var back = MatronLayout.BackPanel(o);
                var at = o.Centre + o.Outward * 3.20f;

                Assert.AreEqual(o.NicheCell, back.At.Cell, o.Name);
                Assert.AreEqual(at.X, back.At.X, 1e-4f, o.Name);
                Assert.AreEqual(at.Y, back.At.Y, 1e-4f, o.Name);
                Assert.AreEqual(-46.5f, back.At.Z, 1e-4f, o.Name);
                Assert.AreEqual(0.3333f, back.Scale, 1e-4f, o.Name);

                var normal = Vector3.Transform(Vector3.UnitZ, new Quaternion(back.At.QX, back.At.QY, back.At.QZ, back.At.QW));
                Assert.AreEqual(-1f, Vector2.Dot(new Vector2(normal.X, normal.Y), o.Outward), 1e-4f, $"{o.Name}: back panel faces the room");
            }

            // the lava variants take the same geometry
            var lava = MatronLayout.BackPanel(MatronData.Opening(3), MatronData.LavaPanelWcid);
            Assert.AreEqual(MatronData.LavaPanelWcid, lava.Wcid);
            Assert.AreEqual(MatronLayout.BackPanel(MatronData.Opening(3)).At, lava.At);
            Assert.AreEqual(0.25f, MatronLayout.NicheFloor(MatronData.Opening(3), MatronData.LavaPanelWcid, 0.25f)[0].Translucency.Value, 1e-6f);
        }


        [TestMethod]
        public void EveryLayoutPoint_HasACell_AndTheCellTableHolds70HallCells()
        {
            foreach (var o in All)
            {
                var points = new List<(string, PinnaclePoint)> { ("door", MatronLayout.Door(o)), ("back panel", MatronLayout.BackPanel(o).At) };

                points.AddRange(MatronLayout.WallStrip(o, lava: false, MatronLayout.WallFxBoth).Select((p, i) => ($"strip {i}", p.At)));

                points.AddRange(MatronLayout.Platforms(o).Select((p, i) => ($"platform {i}", p)));
                points.AddRange(MatronLayout.NicheFloor(o).Select((p, i) => ($"niche floor {i}", p.At)));
                points.Add(("rock", MatronLayout.Rock(o)));

                foreach (var (name, p) in points)
                    Assert.AreNotEqual(0u, p.Cell, $"{o.Name} {name} at ({p.X}, {p.Y}, {p.Z}) has no cell");
            }

            // control: a point off the cross has none
            Assert.AreEqual(0u, MatronData.HallCell(190f, -115f, -59f));
            Assert.AreEqual(0u, MatronData.HallCell(200f, -150f, -59f));
            Assert.AreEqual(0x586B0103u, MatronData.HallCell(200f, -118.5f, -59.995f));
            Assert.AreEqual(0x586B0110u, MatronData.HallCell(220.06f, -119.33f, -59.995f));
        }

        [TestMethod]
        public void Openings_MatchTheDesignTable()
        {
            var expected = new (int Id, string Name, float Cx, float Cy, float Ox, float Oy, uint Niche, uint StandCell, float Sx, float Sy, float Yaw)[]
            {
                (1, "NW", 200f, -115f, 0f, 1f, 0x586B0136, 0x586B0103, 200f, -118.5f, 0f),
                (2, "SW", 205f, -140f, -1f, 0f, 0x586B0139, 0x586B010B, 208.5f, -140f, 90f),
                (3, "N", 220f, -105f, 0f, 1f, 0x586B013E, 0x586B010F, 220f, -108.5f, 0f),
                (4, "SE", 235f, -140f, 1f, 0f, 0x586B014C, 0x586B0118, 231.5f, -140f, 270f),
                (5, "NE", 240f, -115f, 0f, 1f, 0x586B0149, 0x586B0119, 240f, -118.5f, 0f),
            };

            Assert.AreEqual(5, MatronData.Openings.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 4, 5 }, MatronData.PitIds.ToArray());
            Assert.AreEqual(3, MatronData.NorthId);

            foreach (var e in expected)
            {
                var o = MatronData.Opening(e.Id);

                Assert.AreEqual(e.Name, o.Name);
                Assert.AreEqual(new Vector2(e.Cx, e.Cy), o.Centre, e.Name);
                Assert.AreEqual(new Vector2(e.Ox, e.Oy), o.Outward, e.Name);
                Assert.AreEqual(e.Niche, o.NicheCell, e.Name);
                Assert.AreEqual(e.StandCell, o.Stand.Cell, e.Name);
                Assert.AreEqual(e.Sx, o.Stand.X, 1e-4f, e.Name);
                Assert.AreEqual(e.Sy, o.Stand.Y, 1e-4f, e.Name);
                Assert.AreEqual(-59.995f, o.Stand.Z, 1e-4f, e.Name);
                Assert.AreEqual(o.Centre - o.Outward * MatronData.StandDepth, new Vector2(o.Stand.X, o.Stand.Y), $"{e.Name}: StandDepth into the room");

                // the stand yaw is the direction from the stand point to the opening
                Assert.AreEqual(e.Yaw, MatronData.YawDegFacing(o.Outward), 1e-3f, $"{e.Name}: yaw of facing the opening");
                var facing = Vector3.Transform(Vector3.UnitY, new Quaternion(o.Stand.QX, o.Stand.QY, o.Stand.QZ, o.Stand.QW));
                Assert.AreEqual(o.Outward.X, facing.X, 1e-4f, e.Name);
                Assert.AreEqual(o.Outward.Y, facing.Y, 1e-4f, e.Name);
            }

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronData.Opening(6));
        }

        [TestMethod]
        public void RoomData_MatchesThePlan()
        {
            Assert.AreEqual(0x586B0114u, MatronData.ArrivalPoint.Cell);
            Assert.AreEqual(MatronData.ArrivalPoint, MatronData.FightEntryPoint);
            Assert.AreEqual(180f, MatronData.YawDegFacing(new Vector2(0f, -1f)), 1e-3f);
            Assert.AreEqual(0x586B0110u, MatronData.HeadPoint.Cell);
            Assert.AreEqual(0x586B0108u, MatronData.FinalePoint.Cell);
            Assert.AreEqual((210.83f, -109.23f), (MatronData.FinalePoint.X, MatronData.FinalePoint.Y));
            Assert.AreEqual((0x586B0108u, MatronData.FinalePoint.X, MatronData.FinalePoint.Y, MatronData.FloorZ), (MatronData.PillarHostPoint.Cell, MatronData.PillarHostPoint.X, MatronData.PillarHostPoint.Y, MatronData.PillarHostPoint.Z));

            // pillar hosts follow her body: each in the hall cell under it, her own cell off the table
            Assert.AreEqual(0x586B010Fu, MatronData.PillarHostAt(216.5f, -111f, 0x586B0108u).Cell, "the hall cell under her body");
            Assert.AreEqual(0x586B0999u, MatronData.PillarHostAt(190f, -111f, 0x586B0999u).Cell, "off the table: her own cell");

            Assert.AreEqual(1007554u, MatronData.RockWcid);
            Assert.AreEqual(PinnacleTier.Elite, MatronData.RockTier);
            CollectionAssert.Contains(MatronData.RequiredWcids.ToArray(), MatronData.RockWcid);
            Assert.AreEqual(8, MatronData.WaveSpawnPoints.Count);
            Assert.AreEqual(3, MatronData.LavaPoolPoints.Count);
            Assert.IsTrue(MatronData.LavaPoolPoints.All(p => p.Z == -59.8f));

            var ex = MatronData.ExcludeRows;
            Assert.AreEqual(new ACE.Server.Realms.EphemeralRowExclusion(0x586B, 194f, 246f, -166f, -100f, -61f, -44f), ex);

            Assert.AreEqual(1007550u, MatronData.HeadWcid);
            Assert.AreEqual(1007551u, MatronData.PlatformWcid);
            Assert.AreEqual(1007552u, MatronData.GrateWcid);
            Assert.AreEqual(1007553u, MatronData.DrainFloorWcid);
            Assert.AreEqual(1007557u, MatronData.DrainIrisWcid);
            Assert.AreEqual(4451u, MatronData.DoorWcid);

            Assert.AreEqual(PinnacleTier.Boss, MatronData.HeadTier);
            Assert.AreEqual(PinnacleTier.Elite, MatronData.AddTier);

            CollectionAssert.AreEqual(new[] { (213u, 4), (3u, 2) }, MatronData.Wave(1).ToArray());
            CollectionAssert.AreEqual(new[] { (214u, 3), (25750u, 2), (24959u, 1) }, MatronData.Wave(2).ToArray());
            CollectionAssert.AreEqual(new[] { (212u, 2), (25745u, 2), (23481u, 2), (24960u, 1) }, MatronData.Wave(3).ToArray());
            CollectionAssert.AreEqual(new[] { (1007558u, 2) }, MatronData.Wave(4).ToArray(), "the final wave: the two great sentinels alone (playtest round 6)");
            Assert.AreEqual(PinnacleTier.Boss, MatronData.WaveTier(4));
            Assert.AreEqual(PinnacleTier.Elite, MatronData.WaveTier(1));
            Assert.AreEqual(4, MatronData.Waves.Count);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MatronData.Wave(5));
        }

        [TestMethod]
        public void Drain_IsThreeLayers_BlackThenGrateThenIris_EachAtItsPinnedPlace()
        {
            var c = MatronData.DrainPoint;

            foreach (var spent in new[] { false, true })
            {
                var floor = MatronLayout.DrainFloor();
                var tiles = MatronLayout.DrainGrate();
                var iris = MatronLayout.DrainIris(spent);

                // the black panel: the grate's 8.07538 m ClipMap quad at the grate's 0.285 (2.3014 m), origin 3 cm over the floor, face up, not a door
                Assert.AreEqual(1007553u, floor.Wcid);
                Assert.AreEqual(0.285f, floor.Scale, 1e-6f);
                Assert.AreEqual(-59.97f, floor.At.Z, 1e-4f);
                Assert.AreEqual((c.Cell, c.X, c.Y), (floor.At.Cell, floor.At.X, floor.At.Y));
                Assert.AreEqual((0f, 0f, 0f, 1f), (floor.At.QX, floor.At.QY, floor.At.QZ, floor.At.QW), "face up is the identity rotation");
                Assert.IsNull(floor.DoorOpen, "the black panel is not a door");

                // the grate: four tiles at half the old single quad's scale (0.1425, 1.1507 m), origins 5.5 cm over the floor, face up, not doors
                Assert.AreEqual(4, tiles.Count);
                foreach (var t in tiles)
                {
                    Assert.AreEqual(1007552u, t.Wcid);
                    Assert.AreEqual(0.1425f, t.Scale, 1e-6f);
                    Assert.AreEqual(-59.945f, t.At.Z, 1e-4f);
                    Assert.AreEqual(c.Cell, t.At.Cell);
                    Assert.AreEqual((0f, 0f, 0f, 1f), (t.At.QX, t.At.QY, t.At.QZ, t.At.QW));
                    Assert.IsNull(t.DoorOpen, "a grate tile is not a door");
                }

                // the iris: 0.75, pitched +90 degrees about x, 1.2603 north of the drain point, the disc's lowest point on the tiles' origin plane
                Assert.AreEqual(1007557u, iris.Wcid);
                Assert.AreEqual(0.75f, iris.Scale);
                Assert.AreEqual(c.Cell, iris.At.Cell);
                Assert.AreEqual(c.X, iris.At.X);
                Assert.AreEqual(c.Y + 1.2603f, iris.At.Y, 1e-4f);
                Assert.AreEqual(-59.7441f, iris.At.Z, 1e-4f);
                Assert.AreEqual(0.7071f, iris.At.QX, 1e-4f);
                Assert.AreEqual(0f, iris.At.QY, 1e-6f);
                Assert.AreEqual(0f, iris.At.QZ, 1e-6f);
                Assert.AreEqual(0.7071f, iris.At.QW, 1e-4f);
                Assert.AreEqual(spent, iris.DoorOpen, "closed while unspent, open once spent");

                // stacked: black < grate < iris, the black panel's and the tiles' faces (0.0104 x scale under their origins) apart, the
                // iris's lowest point on the tiles' origin plane, nothing below the floor
                var blackFace = floor.At.Z - QuadFaceDrop * floor.Scale;
                var tileFace = tiles[0].At.Z - QuadFaceDrop * tiles[0].Scale;
                Assert.IsTrue(blackFace > MatronLayout.RoomFloorZ && blackFace + 0.02f < tileFace && tiles[0].At.Z < iris.At.Z, $"black face {blackFace}, tile face {tileFace}, iris {iris.At.Z}");
                Assert.AreEqual(tiles[0].At.Z, iris.At.Z - MatronLayout.IrisBelowOrigin, 1e-5f);
                Assert.AreEqual(0.3982f, MatronLayout.IrisHeight, 1e-4f, "the closed disc's measured height (0.2678 + 0.2631) x 0.75");
                Assert.IsTrue(floor.At.Z - QuadSphereRadius * floor.Scale > MatronLayout.RoomFloorZ, "the black panel's one sphere clears the floor");
                Assert.IsTrue(iris.At.Z - MatronLayout.IrisBelowOrigin > MatronLayout.RoomFloorZ, "nothing goes below the floor");
            }

            // control: the iris's face-up pitch turns the door's front (local +Y) to world up, as the plan's "face up"
            var q = new Quaternion(MatronLayout.DrainIris(false).At.QX, MatronLayout.DrainIris(false).At.QY, MatronLayout.DrainIris(false).At.QZ, MatronLayout.DrainIris(false).At.QW);
            Assert.AreEqual(1f, Vector3.Transform(Vector3.UnitY, q).Z, 1e-4f);
            Assert.AreNotEqual(1f, Vector3.Transform(Vector3.UnitZ, q).Z, 1e-4f, "control: local +Z does not point up, so the check discriminates");
        }

        /// <summary>The 0x02000E15 quad's polygon lies this far under its origin at scale 1 (dat; MatronRoomGeometryTests pins it).</summary>
        private const float QuadFaceDrop = 0.0104f;

        /// <summary>The 0x02000E15 quad's one physics sphere, r 0.1 at its origin at scale 1 (dat; MatronRoomGeometryTests pins it).</summary>
        private const float QuadSphereRadius = 0.1f;

        [TestMethod]
        public void DrainGrate_FourTilesCoverTheOldSingleQuadsSquare_EdgeToEdge_AtTwiceTheDensity()
        {
            var c = MatronData.DrainPoint;
            var tiles = MatronLayout.DrainGrate();
            var half = MatronLayout.GrateNativeSize * MatronLayout.GrateTileScale / 2f;

            // the union of the tiles is exactly the old single quad's 2.3014 m square, centred on the drain point
            Assert.AreEqual(8.07538f * 0.285f, MatronLayout.GrateSize, 1e-5f, "the footprint is unchanged");
            Assert.AreEqual(c.X - MatronLayout.GrateSize / 2f, tiles.Min(t => t.At.X) - half, 1e-4f);
            Assert.AreEqual(c.X + MatronLayout.GrateSize / 2f, tiles.Max(t => t.At.X) + half, 1e-4f);
            Assert.AreEqual(c.Y - MatronLayout.GrateSize / 2f, tiles.Min(t => t.At.Y) - half, 1e-4f);
            Assert.AreEqual(c.Y + MatronLayout.GrateSize / 2f, tiles.Max(t => t.At.Y) + half, 1e-4f);

            // edge to edge: the tiles stand one tile edge apart on a 2 x 2 grid, every grid slot filled once (no overlap, no gap)
            var slots = tiles.Select(t => ((int)MathF.Round((t.At.X - c.X) / (2f * half) + 0.5f), (int)MathF.Round((t.At.Y - c.Y) / (2f * half) + 0.5f))).ToList();
            CollectionAssert.AreEquivalent(new[] { (0, 0), (1, 0), (0, 1), (1, 1) }, slots);
            foreach (var t in tiles)
            {
                Assert.AreEqual(half, Math.Abs(t.At.X - c.X), 1e-4f, "each tile's centre is half a tile off the centre lines");
                Assert.AreEqual(half, Math.Abs(t.At.Y - c.Y), 1e-4f);
            }

            // density: the bars repeat with the quad, so a tile half the old quad's edge carries the bars at twice the density
            Assert.AreEqual(2f, MatronLayout.GrateScale / MatronLayout.GrateTileScale, 1e-6f);
        }

        [TestMethod]
        public void DrainIris_ClosedDisc_CoversTheGrate_CentredOnIt()
        {
            var c = MatronData.DrainPoint;
            var iris = MatronLayout.DrainIris(false);

            // centred: the origin stands the scaled disc-centre offset north, so the closed disc's centre is the drain point
            Assert.AreEqual(c.Y, iris.At.Y - MatronLayout.IrisDiscCentreY * iris.Scale, 1e-4f);
            Assert.AreEqual(c.X, iris.At.X, 1e-6f);

            // covers: the closed disc's unbroken covered diameter is at least 1.05 x the grate's edge
            var covered = 2f * MatronLayout.IrisCoveredRadius * iris.Scale;
            Assert.IsTrue(covered >= 1.05f * MatronLayout.GrateSize, $"covered disc {covered} m against the {MatronLayout.GrateSize} m grate");

            // control: the playtest-5 scale (0.5) fails the same relation, so it discriminates
            Assert.IsFalse(2f * MatronLayout.IrisCoveredRadius * 0.5f >= 1.05f * MatronLayout.GrateSize, "control: the old 0.5 iris was too small");
        }

        [TestMethod]
        public void Drain_EveryHiddenLayersSelectionSphere_LiesInsideTheIrisSelectionSphere()
        {
            // HYPOTHESIS under test (unverified in the client): the client picks the object whose selection sphere the cursor ray enters
            // first. A sphere strictly inside another is never entered first, so the iris (the use target) wins over every hidden layer.
            var iris = MatronLayout.DrainIris(false);
            var q = new Quaternion(iris.At.QX, iris.At.QY, iris.At.QZ, iris.At.QW);
            var irisCentre = new Vector3(iris.At.X, iris.At.Y, iris.At.Z) + Vector3.Transform(new Vector3(0f, 0f, MatronLayout.DoorSelectionCentreZ * iris.Scale), q);
            var irisRadius = MatronLayout.DoorSelectionRadius * iris.Scale;

            // the iris's own sphere sits on its petals: within 5 cm of the disc's centre, on the iris's origin plane
            var c = MatronData.DrainPoint;
            Assert.IsTrue(Vector2.Distance(new Vector2(irisCentre.X, irisCentre.Y), new Vector2(c.X, c.Y)) < 0.05f, $"iris sphere centre {irisCentre}");
            Assert.AreEqual(iris.At.Z, irisCentre.Z, 1e-4f);

            float Margin(Vector3 centre, float radius) => irisRadius - (Vector3.Distance(centre, irisCentre) + radius);

            var hidden = new List<(string Name, PinnacleProp Prop)> { ("black", MatronLayout.DrainFloor()) };
            hidden.AddRange(MatronLayout.DrainGrate().Select((t, i) => ($"grate tile {i}", t)));

            foreach (var (name, prop) in hidden)
            {
                var m = Margin(new Vector3(prop.At.X, prop.At.Y, prop.At.Z), MatronLayout.QuadSelectionRadius * prop.Scale);
                Assert.IsTrue(m > 0f, $"{name}: its selection sphere pokes {-m:0.000} m out of the iris's");
            }

            // control: the playtest-5 black panel (the 10 m panel 0x02000E6E at 0.23, sphere r 9.495 about local z 0.738, origin z -59.998)
            // was NOT inside, and enclosed the whole iris sphere, so the relation discriminates
            var oldCentre = new Vector3(c.X, c.Y, -59.998f + 0.738f * 0.23f);
            Assert.IsTrue(Margin(oldCentre, 9.495f * 0.23f) < 0f, "control: the old black panel's sphere was not inside the iris's");
            Assert.IsTrue(9.495f * 0.23f > Vector3.Distance(oldCentre, irisCentre) + irisRadius, "control: it enclosed the iris's sphere entirely");
        }

        [TestMethod]
        public void Drain_EveryLayerIsRequired_AndTheLayersHaveDistinctWcids()
        {
            var required = MatronData.RequiredWcids.ToList();

            foreach (var wcid in new[] { MatronData.GrateWcid, MatronData.DrainFloorWcid, MatronData.DrainIrisWcid })
                CollectionAssert.Contains(required, wcid);

            Assert.AreEqual(3, new[] { MatronData.GrateWcid, MatronData.DrainFloorWcid, MatronData.DrainIrisWcid }.Distinct().Count());
        }

        [TestMethod]
        public void MatronPlayerLines_AreFinalCopy()
        {
            var lines = new[]
            {
                MatronLines.LineStart, MatronLines.LineFlowOpens, MatronLines.LineSeal, MatronLines.LineDrainUsed, MatronLines.LineDrainEmpty,
                MatronLines.LineDrainSpent, MatronLines.LineRumble, MatronLines.LineLavaDoor, MatronLines.LineWin,
                MatronLines.LineTauntIntrude, MatronLines.LineTauntFeed, MatronLines.LineTauntSentinels, MatronLines.LineTauntBurn,
            };

            foreach (var line in lines)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(line));
                Assert.IsTrue(line.All(c => c >= 0x20 && c < 0x7F), "pure ASCII: " + line);
                Assert.IsFalse(line.Contains("TODO"), line);
                Assert.IsTrue(line.Length <= 250, "at most 250 chars: " + line);
            }

            Assert.AreEqual(lines.Length, lines.Distinct().Count(), "all thirteen lines are distinct");
        }


        [TestMethod]
        public void MatronPreset_FillArgs_Validate_At12_RefuseAt10()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("matron", 0, out var r));
            Assert.AreEqual((ushort)0x586B, r.Landblock);
            Assert.AreEqual(-38.0f, r.CeilingZ.Value, 1e-4f);
            Assert.AreEqual(-60.0f, r.GroundZ(220f, -135f), 1e-4f);
            Assert.AreEqual(195f, r.MinX, 1e-4f);
            Assert.AreEqual(245f, r.MaxX, 1e-4f);
            Assert.AreEqual(-165f, r.MinY, 1e-4f);
            Assert.AreEqual(-105f, r.MaxY, 1e-4f);
            Assert.AreEqual("matron", HazardRegionPresets.ForLandblock(0x586B));

            Assert.AreEqual(30, FillRules.SheetTiles(r, 12f, 0.3f).Count);
            Assert.IsNull(FillRules.Validate(r, MatronSettings.Defaults.FillArgs(-59.95, 0.03, 1.0)));
            StringAssert.Contains(FillRules.Validate(r, HazardArgs.Parse("panel_size=10 doubled=1 max_z=-44.5")), "84 surface panels");
        }

        [TestMethod]
        public void MatronSettings_Defaults_AndFillArgs_CarryThePinnedValues()
        {
            var d = MatronSettings.Defaults;

            Assert.AreEqual(0.03, d.FlowRate);
            Assert.AreEqual(0.12, d.FinaleRate);
            Assert.AreEqual(-1.0, d.DrainRate);
            Assert.AreEqual(-2.0, d.LavaClearRate);
            Assert.AreEqual(45.0, d.FlowEscalateSeconds);
            Assert.AreEqual(0.24, d.FlowRateCap);
            Assert.AreEqual(10.0, d.Damage);
            Assert.AreEqual(10.0, d.DamagePerM);
            Assert.AreEqual(250.0, d.DamageMax);
            Assert.AreEqual(2.0, d.FinaleDamageScale);
            Assert.AreEqual(-44.5, d.MaxZ);
            Assert.AreEqual(-59.95, d.FillFloorZ);

            var a = d.FillArgs(-58.0, 0.06, 2.0);
            var s = FillSettings.From(a, HazardRegionPresets.Matron(0));

            Assert.AreEqual("acid", s.Element);
            Assert.AreEqual(12f, s.PanelSize);
            Assert.IsTrue(s.Doubled);
            Assert.AreEqual(0.5, s.Opacity);
            Assert.AreEqual(-59.95, s.FloorZ, 1e-9);
            Assert.AreEqual(-44.5, s.MaxZ, 1e-9);
            Assert.AreEqual(2.0, s.Tick);
            Assert.AreEqual(10.0, s.Damage);
            Assert.AreEqual(10.0, s.DamagePerM);
            Assert.AreEqual(250.0, s.DamageMax);
            Assert.AreEqual(8, s.Wisps);
            Assert.AreEqual(-58.0, s.StartZ, 1e-9);
            Assert.AreEqual(0.06, s.Rate, 1e-9);
            Assert.AreEqual(2.0, s.DamageScale, 1e-9);
        }

        [TestMethod]
        public void MatronSettings_SubmergedTier_IsPinnedAndEmittedInFillArgs()
        {
            var d = MatronSettings.Defaults;

            Assert.AreEqual(139.0, d.SubmergedDamage);
            Assert.AreEqual(25.0, d.SubmergedPerM);
            Assert.AreEqual(500.0, d.SubmergedMax);
            Assert.AreEqual(2.0, d.HeadHeight);

            var s = FillSettings.From(d.FillArgs(-58.0, 0.06, 1.0), HazardRegionPresets.Matron(0));

            Assert.AreEqual(139.0, s.SubmergedDamage);
            Assert.AreEqual(25.0, s.SubmergedPerM);
            Assert.AreEqual(500.0, s.SubmergedMax);
            Assert.AreEqual(2.0, s.HeadHeight);

            // the mild curve under the head is unchanged
            Assert.AreEqual(10 + 10 * 1.5, FillRules.FlatDamage(1.5, s, 1.0), 1e-9);
        }

        [TestMethod]
        public void MatronSettings_SubmergedTier_Calibration_Level400With024ResistIs100PerTick()
        {
            var s = FillSettings.From(MatronSettings.Defaults.FillArgs(-58.0, 0.06, 1.0), HazardRegionPresets.Matron(0));

            var scale400 = MatronSettings.LevelScale(400, 0.25, 0.75);
            Assert.AreEqual(100.0, Math.Round(FillRules.FlatDamage(2.0, s, scale400) * 0.24), "139 x 3.0 x 0.24 = 100.08");
            Assert.AreEqual(100.08, FillRules.FlatDamage(2.0, s, scale400) * 0.24, 1e-9);
            Assert.AreEqual(67.0, Math.Round(FillRules.FlatDamage(2.0, s, MatronSettings.LevelScale(300, 0.25, 0.75)) * 0.24));
            Assert.AreEqual(33.0, Math.Round(FillRules.FlatDamage(2.0, s, MatronSettings.LevelScale(200, 0.25, 0.75)) * 0.24));
            Assert.AreEqual(18.0, (FillRules.FlatDamage(3.0, s, scale400) - FillRules.FlatDamage(2.0, s, scale400)) * 0.24, 1e-9, "+18 per extra metre after resist");
        }

        [TestMethod]
        public void MatronSettings_LevelScale_IsTheBenchmarkOverItsAnchor()
        {
            Assert.AreEqual(1.0, MatronSettings.LevelScale(200, 0.25, 0.75), 1e-9);
            Assert.AreEqual(3.0, MatronSettings.LevelScale(400, 0.25, 0.75), 1e-9);
            Assert.AreEqual(1.0, MatronSettings.LevelScale(1, 0.25, 0.75), 1e-9, "clamped at the minimum");
        }

        [TestMethod]
        public void MotionConstants_AreThePinnedEnumValues()
        {
            Assert.AreEqual(0x100000cdu, (uint)MotionCommand.SpecialAttack1);
            Assert.AreEqual(0x40000008u, (uint)MotionCommand.Fallen);
            Assert.AreEqual(0x8000003cu, (uint)MotionCommand.HandCombat);
        }
    }
}
