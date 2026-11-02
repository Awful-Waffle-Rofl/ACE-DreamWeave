using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HazardSpan (HAZARDS-PLAN A1): the cover set of a spanning region, the anchor-frame split at the 0 and 192
    /// edges, the world-edge refusal, the anchor-frame round trip, per-tick usability (loaded AND in the anchor's own
    /// tick group), and the single-landblock identity. Landblocks are opaque uninitialised tokens behind a fake world:
    /// HazardSpan only ever compares them by reference. No PropertyManager.
    /// </summary>
    [TestClass]
    public class HazardSpanTests
    {
        internal static Landblock Token() => (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        /// <summary>A world of loaded tokens by id, each with a tick group object (null = not grouped yet).</summary>
        internal sealed class FakeWorld : HazardSpan.IWorld
        {
            public readonly Dictionary<ushort, Landblock> Loaded = new Dictionary<ushort, Landblock>();
            public readonly Dictionary<Landblock, object> Groups = new Dictionary<Landblock, object>(ReferenceEqualityComparer.Instance);
            public int Finds;

            public Landblock Find(ushort landblockId, uint instance)
            {
                Finds++;
                return Loaded.TryGetValue(landblockId, out var lb) ? lb : null;
            }

            public object GroupOf(Landblock landblock) => landblock != null && Groups.TryGetValue(landblock, out var g) ? g : null;

            public Landblock Add(ushort id, object group)
            {
                var lb = Token();
                Loaded[id] = lb;
                Groups[lb] = group;
                return lb;
            }
        }

        private const ushort Anchor = 0x7F7F;

        private static HazardRegion Arena(float cx, float cy, float r, ushort anchor = Anchor)
            => HazardRegion.ArenaCircle("arena", anchor, 0, cx, cy, r, 1.5f, 0f, 40f);

        private static List<(ushort Id, int Dx, int Dy)> Cover(HazardRegion region)
        {
            Assert.IsNull(region.Validate(), "fixture: the region itself validates");
            Assert.IsTrue(HazardSpan.TryCover(region, out var cover, out var error), error);
            return cover;
        }

        // ---- cover ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Cover_InsideTheAnchor_IsTheAnchorAlone()
        {
            var cover = Cover(Arena(96f, 96f, 60f));

            Assert.AreEqual(1, cover.Count);
            Assert.AreEqual((Anchor, 0, 0), cover[0]);
        }

        [TestMethod]
        public void Cover_NegativeCoordinates_ReachWestAndSouth()
        {
            var cover = Cover(Arena(10f, 10f, 30f));

            CollectionAssert.AreEquivalent(new[] { (ushort)0x7F7F, (ushort)0x7E7F, (ushort)0x7F7E, (ushort)0x7E7E }, cover.Select(c => c.Id).ToArray());
            Assert.AreEqual(((ushort)0x7F7F, 0, 0), cover[0], "the anchor comes first");
            Assert.IsTrue(cover.Contains(((ushort)0x7E7E, -1, -1)), "the south-west corner is x-1, y-1");
            Assert.IsTrue(cover.Contains(((ushort)0x7E7F, -1, 0)), "x is the HIGH byte: west is 0x7E7F");
        }

        [TestMethod]
        public void Cover_PastOneNinetyTwo_ReachesEastAndNorth()
        {
            var cover = Cover(Arena(180f, 185f, 30f));

            Assert.AreEqual(4, cover.Count, "a 2 x 2 cover");
            Assert.IsTrue(cover.Contains(((ushort)0x807F, 1, 0)));
            Assert.IsTrue(cover.Contains(((ushort)0x7F80, 0, 1)));
            Assert.IsTrue(cover.Contains(((ushort)0x8080, 1, 1)));
        }

        [TestMethod]
        public void Cover_ExactEdges_ZeroIsTheAnchor_OneNinetyTwoIsTheNeighbour()
        {
            // x 0..191.5: max under 192 stays on the anchor
            Assert.AreEqual(1, Cover(Arena(95.75f, 96f, 95.75f)).Count);

            // x 1..192: a box reaching exactly 192 covers the east neighbour (local x 0 is its first metre)
            var east = Cover(Arena(96.5f, 96f, 95.5f));
            Assert.AreEqual(2, east.Count);
            Assert.IsTrue(east.Contains(((ushort)0x807F, 1, 0)));

            // x 0..190 with min exactly 0: no west neighbour
            var west = Cover(Arena(95f, 96f, 95f));
            Assert.AreEqual(1, west.Count, "x = 0 is the anchor's, not the west neighbour's");
        }

        [TestMethod]
        public void Cover_RefusedAtTheWorldEdge()
        {
            AssertRefused(Arena(10f, 96f, 30f, anchor: 0x007F), "west of column 0x00");
            AssertRefused(Arena(180f, 96f, 30f, anchor: 0xFE7F), "east of column 0xFE");
            AssertRefused(Arena(96f, 10f, 30f, anchor: 0x7F00), "south of row 0x00");
            AssertRefused(Arena(96f, 180f, 30f, anchor: 0x7FFE), "north of row 0xFE");

            // the same anchors are fine when the region stays off that edge
            Assert.IsTrue(HazardSpan.TryCover(Arena(180f, 96f, 30f, anchor: 0x007F), out _, out _), "column 0x00 may reach east");
            Assert.IsTrue(HazardSpan.TryCover(Arena(10f, 96f, 30f, anchor: 0xFE7F), out _, out _), "column 0xFE may reach west");
        }

        private static void AssertRefused(HazardRegion region, string why)
        {
            Assert.IsNull(region.Validate(), "fixture: the frame rules pass; only the world edge refuses");
            Assert.IsFalse(HazardSpan.TryCover(region, out var cover, out var error), why);
            StringAssert.Contains(error, "world edge", why);
            Assert.AreEqual(0, cover.Count, why);
        }

        [TestMethod]
        public void Cover_ARegionThatMissesItsAnchor_IsRefused()
        {
            // inside the (-192, 384) frame and under 192 m, but wholly on the east neighbour
            var region = Arena(290f, 96f, 40f);
            Assert.IsNull(region.Validate());

            Assert.IsFalse(HazardSpan.TryCover(region, out _, out var error));
            StringAssert.Contains(error, "does not cover its anchor");
        }

        [TestMethod]
        public void Cover_ASingleLandblockRegion_IsItsOwnLandblock()
        {
            var circle = HazardRegion.Circle("t", 0x0069, 0, false, 10f, 10f, 5f, HazardGroundMode.Plane, 0f, null, 1f, null, 1f, 1f);

            Assert.IsTrue(HazardSpan.TryCover(circle, out var cover, out _));
            Assert.AreEqual(1, cover.Count);
            Assert.AreEqual(((ushort)0x0069, 0, 0), cover[0]);
        }

        // ---- the split and the round trip ------------------------------------------------------------------------

        [TestMethod]
        public void Split_EdgesAndNegatives()
        {
            Assert.AreEqual((0, 0, new Vector3(0f, 0f, 5f)), HazardSpan.Split(new Vector3(0f, 0f, 5f)));
            Assert.AreEqual((1, 0, new Vector3(0f, 10f, 5f)), HazardSpan.Split(new Vector3(192f, 10f, 5f)), "x = 192 is the east neighbour's local 0");
            Assert.AreEqual((0, 0, new Vector3(191.5f, 10f, 5f)), HazardSpan.Split(new Vector3(191.5f, 10f, 5f)));

            var (dx, dy, local) = HazardSpan.Split(new Vector3(-0.5f, 383.5f, -2f));
            Assert.AreEqual(-1, dx);
            Assert.AreEqual(1, dy);
            Assert.AreEqual(191.5f, local.X, 1e-4f);
            Assert.AreEqual(191.5f, local.Y, 1e-4f);
            Assert.AreEqual(-2f, local.Z, "z is never touched");
        }

        [TestMethod]
        public void ToAnchorFrame_RoundTripsEveryCoveredLandblock()
        {
            var spanning = HazardSpan.For(Arena(10f, 10f, 30f), Token(), new FakeWorld());

            var points = new[]
            {
                new Vector3(-150.25f, -3f, 12f), new Vector3(0f, 0f, 0f), new Vector3(191.75f, 191.75f, 1f),
                new Vector3(192f, 100f, 2f), new Vector3(250.5f, -191.5f, 3f), new Vector3(-0.25f, 383f, 4f),
            };

            foreach (var p in points)
            {
                var (dx, dy, local) = HazardSpan.Split(p);
                var id = (ushort)(((Anchor >> 8) + dx) << 8 | ((Anchor & 0xFF) + dy));

                Assert.IsTrue(local.X >= 0f && local.X < 192f && local.Y >= 0f && local.Y < 192f, $"{p}: local {local} inside the block");

                var back = HazardSpan.ToAnchorFrame(Anchor, id, local);
                Assert.AreEqual(p.X, back.X, 1e-3f, $"{p} x");
                Assert.AreEqual(p.Y, back.Y, 1e-3f, $"{p} y");
                Assert.AreEqual(p.Z, back.Z, $"{p} z");

                // and through a Position on that landblock, as a player's Location arrives
                var pos = new Position(((uint)id << 16) | 0x0001, local.X, local.Y, local.Z, 0f, 0f, 0f, 1f, 0);

                var viaPosition = spanning.ToAnchorFrame(pos);
                Assert.AreEqual(p.X, viaPosition.X, 1e-3f, $"{p} via Position");
                Assert.AreEqual(p.Y, viaPosition.Y, 1e-3f, $"{p} via Position");
                Assert.AreEqual(p.Z, viaPosition.Z, $"{p} via Position");
            }
        }

        // ---- usable --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Refresh_UsableOnlyWhenLoadedAndInTheAnchorsOwnGroup()
        {
            var world = new FakeWorld();
            var group = new object();
            var anchor = world.Add(Anchor, group);
            var west = world.Add(0x7E7F, group);           // same group: usable
            var southWest = world.Add(0x7E7E, new object()); // another group: another thread
            world.Add(0x7F7E, null);                        // loaded, no group yet (just loaded)

            var span = HazardSpan.For(Arena(10f, 10f, 30f), anchor, world);
            Assert.IsTrue(span.Spans);
            Assert.IsTrue(span.Usable(anchor), "the anchor is usable before any refresh");
            Assert.IsFalse(span.Usable(west), "a neighbour is not usable until the first refresh");

            var logged = new List<(ushort, string)>();
            span.Refresh((slot, why) => logged.Add((slot.Id, why)));

            Assert.IsTrue(span.Usable(anchor));
            Assert.IsTrue(span.Usable(west));
            Assert.IsFalse(span.Usable(southWest));
            Assert.IsFalse(span.SlotOf(0x7F7E).Usable, "no group yet");
            Assert.IsFalse(span.Usable(Token()), "a landblock the span does not cover");
            Assert.IsFalse(span.Usable(null));

            CollectionAssert.AreEquivalent(new ushort[] { 0x7E7E, 0x7F7E }, logged.Select(l => l.Item1).ToArray());
            StringAssert.Contains(logged.Single(l => l.Item1 == 0x7E7E).Item2, "tick thread");

            // the just-loaded one gets its group; the other is unloaded. Logged at most once per landblock per run.
            world.Groups[world.Loaded[0x7F7E]] = group;
            world.Loaded.Remove(0x7E7E);
            span.Refresh((slot, why) => logged.Add((slot.Id, why)));

            Assert.IsTrue(span.SlotOf(0x7F7E).Usable, "grouped now");
            Assert.IsFalse(span.SlotOf(0x7E7E).Usable, "unloaded");
            Assert.IsNull(span.SlotOf(0x7E7E).Current);
            Assert.AreEqual(2, logged.Count, "0x7E7E was already logged; nothing new");
        }

        [TestMethod]
        public void Refresh_AReloadedNeighbourIsTheNewCopy()
        {
            var world = new FakeWorld();
            var group = new object();
            var anchor = world.Add(Anchor, group);
            var oldEast = world.Add(0x807F, group);

            var span = HazardSpan.For(Arena(180f, 96f, 30f), anchor, world);
            span.Refresh();
            Assert.IsTrue(span.Usable(oldEast));

            var newEast = world.Add(0x807F, group);
            span.Refresh();

            Assert.IsFalse(span.Usable(oldEast), "the unloaded copy is never touched again");
            Assert.IsTrue(span.Usable(newEast));
            Assert.IsTrue(span.TryLocate(new Vector3(200f, 96f, 1f), out var owner, out var local));
            Assert.AreSame(newEast, owner);
            Assert.AreEqual(new Vector3(8f, 96f, 1f), local);
        }

        [TestMethod]
        public void Refresh_AnAnchorWithNoGroup_MakesNoNeighbourUsable()
        {
            var world = new FakeWorld();
            var anchor = world.Add(Anchor, null);
            var east = world.Add(0x807F, null);

            var span = HazardSpan.For(Arena(180f, 96f, 30f), anchor, world);
            span.Refresh();

            Assert.IsTrue(span.Usable(anchor), "the anchor itself (the driver's own landblock)");
            Assert.IsFalse(span.Usable(east), "two null groups are not the same group");
        }

        [TestMethod]
        public void TryLocate_SpanningRegion_RoutesToTheOwningUsableLandblock()
        {
            var world = new FakeWorld();
            var group = new object();
            var anchor = world.Add(Anchor, group);
            var east = world.Add(0x807F, group);

            var span = HazardSpan.For(Arena(180f, 96f, 30f), anchor, world);
            span.Refresh();

            Assert.IsTrue(span.TryLocate(new Vector3(100f, 50f, 3f), out var o1, out var l1));
            Assert.AreSame(anchor, o1);
            Assert.AreEqual(new Vector3(100f, 50f, 3f), l1);

            Assert.IsTrue(span.TryLocate(new Vector3(192f, 50f, 3f), out var o2, out var l2));
            Assert.AreSame(east, o2);
            Assert.AreEqual(new Vector3(0f, 50f, 3f), l2);

            Assert.IsFalse(span.TryLocate(new Vector3(-1f, 50f, 3f), out var o3, out _), "west is not covered");
            Assert.IsNull(o3);

            world.Groups[east] = new object();
            span.Refresh();
            Assert.IsFalse(span.TryLocate(new Vector3(200f, 50f, 3f), out var o4, out _), "east moved to another thread");
            Assert.AreSame(east, o4, "the owner is still reported, but not usable");
        }

        // ---- the single-landblock identity ---------------------------------------------------------------------

        [TestMethod]
        public void SingleLandblockRegion_IsTheIdentity_AndNeverAsksTheWorld()
        {
            var world = new FakeWorld();
            var anchor = Token();
            var circle = HazardRegion.Circle("t", 0x0069, 0, false, 10f, 10f, 5f, HazardGroundMode.Plane, 0f, null, 1f, null, 1f, 1f);

            var span = HazardSpan.For(circle, anchor, world);
            span.Refresh();

            Assert.IsFalse(span.Spans);
            Assert.AreEqual(1, span.Slots.Count);
            Assert.AreSame(anchor, span.Slots[0].Current);
            Assert.IsTrue(span.Slots[0].Usable);
            Assert.AreEqual((ushort)0x0069, span.Slots[0].Id);
            Assert.AreEqual(0, world.Finds, "the identity span never looks anything up");

            // every point - even one outside 0..192, which the cell lookup then refuses as today - is the run's own landblock, unchanged
            foreach (var p in new[] { new Vector3(10f, 10f, -54f), new Vector3(-5f, 300f, 1f), new Vector3(192f, 0f, 0f) })
            {
                Assert.IsTrue(span.TryLocate(p, out var owner, out var local), p.ToString());
                Assert.AreSame(anchor, owner, p.ToString());
                Assert.AreEqual(p, local, p.ToString());
            }

            // and a Position's local coordinates are returned as they are, whatever landblock it names
            var elsewhere = new Position(0x00700100, 12.5f, 7.25f, -3f, 0f, 0f, 0f, 1f, 0);
            Assert.AreEqual(new Vector3(12.5f, 7.25f, -3f), span.ToAnchorFrame(elsewhere));
        }

        [TestMethod]
        public void SpanRefusal_SingleLandblock_IsNull_Spanning_NeedsEveryNeighbourLoaded()
        {
            var circle = HazardRegion.Circle("t", 0x0069, 0, false, 10f, 10f, 5f, HazardGroundMode.Plane, 0f, null, 1f, null, 1f, 1f);
            Assert.IsNull(HazardManager.SpanRefusal(circle, (_, _) => throw new AssertFailedException("a single-landblock region never looks up a neighbour")));

            var region = Arena(180f, 96f, 30f);
            var asked = new List<ushort>();

            var notLoaded = HazardManager.SpanRefusal(region, (id, _) => { asked.Add(id); return null; });
            StringAssert.Contains(notLoaded, "0x807F");
            StringAssert.Contains(notLoaded, "never loads a landblock");
            CollectionAssert.AreEqual(new ushort[] { 0x807F }, asked, "only the neighbour is looked up; the anchor is Start's own check");

            var east = Token();
            SetIsDungeon(east, false);
            Assert.IsNull(HazardManager.SpanRefusal(region, (_, _) => east));

            SetIsDungeon(east, true);
            StringAssert.Contains(HazardManager.SpanRefusal(region, (_, _) => east), "dungeon");

            StringAssert.Contains(HazardManager.SpanRefusal(Arena(180f, 96f, 30f, anchor: 0xFE7F), (_, _) => east), "world edge");
        }

        /// <summary>Landblock.IsDungeon caches into a private bool?; setting it keeps the getter off the (absent) dat.</summary>
        internal static void SetIsDungeon(Landblock lb, bool value)
        {
            var field = typeof(Landblock).GetField("isDungeon", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Landblock.isDungeon (the IsDungeon cache) was renamed; update this fixture");
            field.SetValue(lb, (bool?)value);
        }
    }
}
