using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HazardContext.HitPlayers and PlayersInRegion across two fake landblocks (HAZARDS-PLAN A1), through the
    /// players-provider seam (HazardContext.PlayerSource) and the damage seam (DamageForTest): a spanning arena hits
    /// players on the anchor AND on a usable neighbour in one anchor frame, never one on a neighbour that is not on
    /// the anchor's tick thread, requires the player inside the arena (HitsRequireInside, absolute band), and a
    /// single-landblock region still reads only its own landblock's list. No PropertyManager, no world.
    /// </summary>
    [TestClass]
    public class HazardSpanHitPlayersTests
    {
        private const ushort Anchor = 0x7F7F;
        private const ushort East = 0x807F;

        private sealed class FakePlayers : HazardContext.IPlayerSource
        {
            public readonly Dictionary<Landblock, List<HazardContext.PlayerSighting?>> Lists =
                new Dictionary<Landblock, List<HazardContext.PlayerSighting?>>(ReferenceEqualityComparer.Instance);

            public readonly List<Landblock> Asked = new List<Landblock>();

            public int Count(Landblock landblock)
            {
                if (!Asked.Contains(landblock))
                    Asked.Add(landblock);

                return Lists.TryGetValue(landblock, out var l) ? l.Count : 0;
            }

            public bool TryGet(Landblock landblock, int index, out HazardContext.PlayerSighting sighting)
            {
                var s = Lists[landblock][index];
                sighting = s ?? default;
                return s.HasValue;
            }

            public void Put(Landblock lb, uint guid, ushort onLandblock, float x, float y, float z, bool dead = false, uint instance = 0)
            {
                if (!Lists.TryGetValue(lb, out var l))
                    Lists[lb] = l = new List<HazardContext.PlayerSighting?>();

                var pos = new Position(((uint)onLandblock << 16) | 0x0001, x, y, z, 0f, 0f, 0f, 1f, instance);
                l.Add(new HazardContext.PlayerSighting(null, guid, pos, dead, false));
            }
        }

        private sealed class Fixture
        {
            public HazardSpanTests.FakeWorld World = new HazardSpanTests.FakeWorld();
            public object Group = new object();
            public Landblock AnchorLb;
            public Landblock EastLb;
            public FakePlayers Players = new FakePlayers();
            public HazardContext Ctx;
            public HazardSpan Span;
            public HazardHandle Source;
            public readonly List<uint> Hit = new List<uint>();

            public Fixture(HazardRegion region)
            {
                AnchorLb = World.Add(Anchor, Group);
                EastLb = World.Add(East, Group);

                var run = new HazardRun(1, "test", null, region, HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow);
                Ctx = run.Context;
                Span = HazardSpan.For(region, AnchorLb, World);
                Span.Refresh();
                Ctx.Span = Span;
                Ctx.PlayerSource = Players;
                Ctx.DamageForTest = (src, seen, fraction, type, text) => { Hit.Add(seen.Guid); return 7; };
                Ctx.SetNow(1.0);

                var wo = (GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject));
                Source = new HazardHandle(1, wo, new HazardSpawnSpec { Wcid = 1006951, Tag = "ball" }, Vector3.Zero);
            }
        }

        /// <summary>An arena around (190, 96) r 20 on 0x7F7F: x 170..210 reaches 18 m into 0x807F. Band z 0..20.</summary>
        private static HazardRegion Arena() => HazardRegion.ArenaCircle("arena", Anchor, 0, 190f, 96f, 20f, 1.5f, 0f, 20f);

        [TestMethod]
        public void HitPlayers_SpanningArena_HitsPlayersOnBothLandblocksInOneFrame()
        {
            var f = new Fixture(Arena());

            f.Players.Put(f.AnchorLb, 1, Anchor, 190f, 96f, 5f);   // anchor frame (190, 96): 3 m from the hazard
            f.Players.Put(f.EastLb, 2, East, 3f, 96f, 5f);         // anchor frame (195, 96): 2 m
            f.Players.Put(f.EastLb, 3, East, 100f, 96f, 5f);       // anchor frame (292, 96): far
            f.Players.Put(f.AnchorLb, 4, Anchor, 3f, 96f, 5f);     // local (3, 96) on the ANCHOR is 190 m away, not the east player's spot

            var hits = f.Ctx.HitPlayers(f.Source, new Vector3(193f, 96f, 5f), 4f);

            Assert.AreEqual(2, hits);
            CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, f.Hit);
            CollectionAssert.AreEquivalent(new[] { f.AnchorLb, f.EastLb }, f.Players.Asked, "both landblocks' lists were read");

            // the ledger is per (hazard, player) across landblocks: the same hazard does not hit them again
            Assert.AreEqual(0, f.Ctx.HitPlayers(f.Source, new Vector3(193f, 96f, 5f), 4f));
            Assert.AreEqual(2, f.Hit.Count);
        }

        [TestMethod]
        public void HitPlayers_ANeighbourOnAnotherTickThread_IsNeverRead()
        {
            var f = new Fixture(Arena());

            f.Players.Put(f.AnchorLb, 1, Anchor, 190f, 96f, 5f);
            f.Players.Put(f.EastLb, 2, East, 3f, 96f, 5f);

            f.World.Groups[f.EastLb] = new object();
            f.Span.Refresh();

            Assert.AreEqual(1, f.Ctx.HitPlayers(f.Source, new Vector3(193f, 96f, 5f), 4f));
            CollectionAssert.AreEqual(new uint[] { 1 }, f.Hit);
            CollectionAssert.AreEqual(new[] { f.AnchorLb }, f.Players.Asked, "the other thread's player list is not even counted");

            // unloaded: the same
            f.World.Loaded.Remove(East);
            f.Span.Refresh();
            f.Players.Asked.Clear();

            var second = new HazardHandle(2, f.Source.Object, new HazardSpawnSpec { Wcid = 1006951 }, Vector3.Zero);
            Assert.AreEqual(1, f.Ctx.HitPlayers(second, new Vector3(193f, 96f, 5f), 4f));
            CollectionAssert.AreEqual(new[] { f.AnchorLb }, f.Players.Asked);
        }

        [TestMethod]
        public void HitPlayers_AnArenaHitNeedsThePlayerInside()
        {
            var f = new Fixture(Arena());

            f.Players.Put(f.AnchorLb, 1, Anchor, 190f, 117f, 5f);  // 1 m from the hazard, 21 m from the arena centre: outside
            f.Players.Put(f.AnchorLb, 2, Anchor, 190f, 114f, 5f);  // inside, in reach
            f.Players.Put(f.AnchorLb, 3, Anchor, 190f, 115f, 25f); // inside in 2D, above the absolute band
            f.Players.Put(f.EastLb, 4, East, 0.5f, 110f, 5f);      // anchor (192.5, 110): 18.2 m from centre, inside, in reach? no (5.5 m): not hit
            f.Players.Put(f.AnchorLb, 5, Anchor, 189f, 116f, 5f, dead: true);
            f.Players.Put(f.AnchorLb, 6, Anchor, 190f, 115f, 5f, instance: 7); // another instance

            Assert.AreEqual(1, f.Ctx.HitPlayers(f.Source, new Vector3(190f, 116f, 5f), 2f));
            CollectionAssert.AreEqual(new uint[] { 2 }, f.Hit);
        }

        [TestMethod]
        public void HitPlayers_APlayerListedUnderTheWrongLandblock_IsNotCountedTwice()
        {
            var f = new Fixture(Arena());

            // listed by the east landblock but its Location names the anchor (mid-transfer): skipped there, and it
            // is not in the anchor's list, so it is simply not hit this tick (today's single-landblock rule)
            f.Players.Put(f.EastLb, 9, Anchor, 191f, 96f, 5f);

            Assert.AreEqual(0, f.Ctx.HitPlayers(f.Source, new Vector3(191f, 96f, 5f), 4f));
        }

        [TestMethod]
        public void PlayersInRegion_SpanningArena_ReportsAnchorFramePositions()
        {
            var f = new Fixture(Arena());

            f.Players.Put(f.AnchorLb, 1, Anchor, 180f, 96f, 5f);
            f.Players.Put(f.EastLb, 2, East, 10f, 96f, 5f);   // anchor (202, 96): inside
            f.Players.Put(f.EastLb, 3, East, 40f, 96f, 5f);   // anchor (232, 96): outside

            var inside = f.Ctx.PlayersInRegion();

            Assert.AreEqual(2, inside.Count);
            CollectionAssert.AreEquivalent(new[] { new Vector3(180f, 96f, 5f), new Vector3(202f, 96f, 5f) }, inside.Select(p => p.Position).ToArray());

            // the neighbour on another thread drops out
            f.World.Groups[f.EastLb] = new object();
            f.Span.Refresh();
            CollectionAssert.AreEqual(new[] { new Vector3(180f, 96f, 5f) }, f.Ctx.PlayersInRegion().Select(p => p.Position).ToArray());
        }

        [TestMethod]
        public void RegistryRun_WithNoLandblockAndNoSpan_HitsAndListsNobody()
        {
            var run = new HazardRun(1, "test", null, Arena(), HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow);
            var wo = (GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject));
            var source = new HazardHandle(1, wo, new HazardSpawnSpec { Wcid = 1006951 }, Vector3.Zero);

            Assert.IsNull(run.Context.Span, "no landblock: no span");
            Assert.AreEqual(0, run.Context.HitPlayers(source, Vector3.Zero, 5f));
            Assert.AreEqual(0, run.Context.PlayersInRegion().Count);
        }

        [TestMethod]
        public void HitPlayers_SingleLandblockRegion_ReadsOnlyItsOwnLandblock()
        {
            // a plane circle on the anchor; a span built for it is the identity, so the east list is never read
            var circle = HazardRegion.Circle("t", Anchor, 0, true, 100f, 96f, 30f, HazardGroundMode.Plane, 0f, null, 1.5f, null, 2f, 6f);
            var f = new Fixture(circle);

            Assert.IsFalse(f.Span.Spans);

            f.Players.Put(f.AnchorLb, 1, Anchor, 100f, 96f, 1f);
            f.Players.Put(f.AnchorLb, 2, Anchor, 100f, 96f, 7f);   // above the ground band (0 + 6)
            f.Players.Put(f.EastLb, 3, East, 100f, 96f, 1f);
            f.Players.Put(f.AnchorLb, 4, East, 100f, 96f, 1f);     // on the anchor's list, Location on another landblock

            Assert.AreEqual(1, f.Ctx.HitPlayers(f.Source, new Vector3(100f, 96f, 1f), 3f));
            CollectionAssert.AreEqual(new uint[] { 1 }, f.Hit);
            CollectionAssert.AreEqual(new[] { f.AnchorLb }, f.Players.Asked);
        }
    }
}
