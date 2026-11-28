using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Knots;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure seams of WorldEventRewardCrate.Open: the draws arithmetic and the itemMaxLevel stamp.
    /// The stamp test builds a WorldObject from an in-memory weenie (no database, no dats).
    /// </summary>
    [TestClass]
    public class WorldEventRewardCrateRollTests
    {
        private static uint nextGuid = 0x7D100000;

        private static WorldObject CreateObject()
        {
            var weenie = new Weenie { WeenieClassId = 9101, WeenieType = WeenieType.Stackable };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void EffectiveRolls_NullDraws_LeavesMultiplierUnchanged()
        {
            Assert.AreEqual(1.0, WorldEventRewardCrate.EffectiveRolls(null, 1.0), 1e-12);
            Assert.AreEqual(0.37, WorldEventRewardCrate.EffectiveRolls(null, 0.37), 1e-12);
            Assert.AreEqual(2.0, WorldEventRewardCrate.EffectiveRolls(null, 2.0), 1e-12);
        }

        [TestMethod]
        public void EffectiveRolls_DrawsAreAdditive()
        {
            Assert.AreEqual(3.0, WorldEventRewardCrate.EffectiveRolls(3, 1.0), 1e-12);
            Assert.AreEqual(3.9, WorldEventRewardCrate.EffectiveRolls(3, 1.9), 1e-12);
            Assert.AreEqual(4.0, WorldEventRewardCrate.EffectiveRolls(4, 1.0), 1e-12);
        }

        [TestMethod]
        public void EffectiveRolls_NeverFallsPastATierBoundary()
        {
            for (var draws = 1; draws < 10; draws++)
            {
                var topOfThis = WorldEventRewardCrate.EffectiveRolls(draws, 2.0);
                var bottomOfNext = WorldEventRewardCrate.EffectiveRolls(draws + 1, 1.0);

                Assert.IsTrue(bottomOfNext >= topOfThis - 1.0 + 1e-12, $"draws {draws} -> {draws + 1}");
                Assert.IsTrue(WorldEventRewardCrate.EffectiveRolls(draws + 1, 1.0) >= WorldEventRewardCrate.EffectiveRolls(draws, 1.0));
            }
        }

        [TestMethod]
        public void ApplyRollStamps_SetsLevelAndMatchingOverlay()
        {
            for (var level = 1; level <= 5; level++)
            {
                var wo = CreateObject();

                WorldEventRewardCrate.ApplyRollStamps(wo, new RolledReward(9101, 1, level));

                Assert.AreEqual(level, wo.ItemMaxLevel);
                Assert.AreEqual(LootGenerationFactory.IconOverlay_ItemMaxLevel[level - 1], wo.IconOverlayId);
            }
        }

        private static readonly PinnacleDef[] Pinnacles =
        {
            new PinnacleDef { Key = "p1", Name = "P One" },
            new PinnacleDef { Key = "p2", Name = "P Two" },
            new PinnacleDef { Key = "p3", Name = "P Three" },
        };

        [TestMethod]
        public void ApplyRollStamps_Pattern_GetsAKeyFromTheAvailableListAndTheMatchingName()
        {
            var seen = new HashSet<string>();

            for (var seed = 0; seed < 60; seed++)
            {
                var wo = CreateObject();
                var ok = WorldEventRewardCrate.ApplyRollStamps(wo, new RolledReward(KnotItems.PatternWcid, 1, null, "pattern"), Pinnacles, 100, new Random(seed));
                var key = wo.GetProperty(PropertyString.PinnacleKey);
                var def = Pinnacles.Single(p => p.Key == key);

                Assert.IsTrue(ok);
                Assert.AreEqual(KnotItems.PatternName(def.Name), wo.GetProperty(PropertyString.Name));
                seen.Add(key);
            }

            Assert.AreEqual(3, seen.Count, "the pick is spread over every available pinnacle");
        }

        [TestMethod]
        public void ApplyRollStamps_Fiber_GetsThePassedLevelAndName()
        {
            var wo = CreateObject();

            Assert.IsTrue(WorldEventRewardCrate.ApplyRollStamps(wo, new RolledReward(KnotItems.FiberWcid, 1, null, "fiber"), Pinnacles, 137, new Random(1)));
            Assert.AreEqual(137, wo.GetProperty(PropertyInt.KnotLevel));
            Assert.AreEqual(KnotItems.FiberName(137), wo.GetProperty(PropertyString.Name));
            Assert.IsNull(wo.GetProperty(PropertyString.PinnacleKey));
        }

        [TestMethod]
        public void ApplyRollStamps_PatternWithNoAvailablePinnacle_IsRefused_AndOthersStillStamp()
        {
            var pattern = CreateObject();
            var other = CreateObject();

            Assert.IsFalse(WorldEventRewardCrate.ApplyRollStamps(pattern, new RolledReward(KnotItems.PatternWcid, 1, null, "pattern"), new List<PinnacleDef>(), 100, new Random(1)));
            Assert.IsFalse(WorldEventRewardCrate.ApplyRollStamps(pattern, new RolledReward(KnotItems.PatternWcid, 1, null, "pattern"), null, 100, new Random(1)));
            Assert.IsNull(pattern.GetProperty(PropertyString.PinnacleKey));

            // positive control: a sibling row in the same roll is unaffected by the refusal
            Assert.IsTrue(WorldEventRewardCrate.ApplyRollStamps(other, new RolledReward(9101, 1, 4), new List<PinnacleDef>(), 100, new Random(1)));
            Assert.AreEqual(4, other.ItemMaxLevel);
        }

        [TestMethod]
        public void ApplyRollStamps_FiberWithNoPlayerLevel_IsRefused()
        {
            var wo = CreateObject();

            Assert.IsFalse(WorldEventRewardCrate.ApplyRollStamps(wo, new RolledReward(KnotItems.FiberWcid, 1, null, "fiber"), Pinnacles, null, new Random(1)));
            Assert.IsNull(wo.GetProperty(PropertyInt.KnotLevel));
        }

        /// <summary>
        /// The consumer pin: Open cannot run without a live world, so this reads its source and requires that the
        /// object creation path calls ApplyRollStamps with the available pinnacles, the player's level and an rng,
        /// and destroys the object when the stamp is refused. Removing the call makes this fail.
        /// </summary>
        [TestMethod]
        public void CrateOpen_CallsTheStampSeam_WithPinnaclesAndPlayerLevel_AndSkipsRefusedObjects()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;

            while (dir != null && path == null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "WorldEventRewardCrate.cs");
                path = File.Exists(candidate) ? candidate : null;
                dir = dir.Parent;
            }

            Assert.IsNotNull(path, "could not locate WorldEventRewardCrate.cs by walking up from the test assembly");

            var src = File.ReadAllText(path).Replace("\r\n", "\n");
            var open = src.Substring(src.IndexOf("private static void Open(", StringComparison.Ordinal));

            StringAssert.Contains(open, "BuildRowObjects(row, WorldObjectFactory.CreateNewWorldObject, pinnacles, player.Level, stampRng, items.Count)");
            StringAssert.Contains(open, "KnotPinnacles.Available(ThreadDungeonManager.Store)");

            // An all-skipped crate refuses and is kept: the empty check sits after the build and before any give or consume.
            var empty = open.IndexOf("if (items.Count == 0)", StringComparison.Ordinal);
            Assert.IsTrue(empty > open.IndexOf("BuildRowObjects(", StringComparison.Ordinal), "the empty check follows the build");
            Assert.IsTrue(empty < open.IndexOf("CanAddToInventory", StringComparison.Ordinal), "and precedes the give");
            Assert.IsTrue(empty < open.IndexOf("TryConsumeFromInventoryWithNetworking", StringComparison.Ordinal), "and precedes the consume");
            StringAssert.Contains(open.Substring(empty, 400), "The Event Crate will not open.");
            StringAssert.Contains(open, "DescribeRow"); // the info log names each row's stamp
        }

        // ---- BuildRowObjects: the per-row loop Open runs ----

        private static WorldObject CreateStackable(uint wcid, int maxStack = 10)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.StackSize, 1 }, { PropertyInt.MaxStackSize, maxStack } },
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void BuildRowObjects_LevelRowOverOneStack_EveryObjectIsStamped()
        {
            var made = new List<WorldObject>();
            var row = new RolledReward(9201, 25, 5);

            var built = WorldEventRewardCrate.BuildRowObjects(row, w => { var o = CreateStackable(w); made.Add(o); return o; }, null, 100, new Random(1));

            Assert.IsNull(built.Failure);
            Assert.AreEqual(3, built.Objects.Count, "25 over a stack size of 10 is 10 + 10 + 5");
            CollectionAssert.AreEqual(new[] { 10, 10, 5 }, built.Objects.Select(o => o.StackSize ?? 1).ToArray());

            foreach (var o in built.Objects)
            {
                Assert.AreEqual(5, o.ItemMaxLevel);
                Assert.AreEqual(LootGenerationFactory.IconOverlay_ItemMaxLevel[4], o.IconOverlayId);
            }
        }

        [TestMethod]
        public void BuildRowObjects_PatternRowWithNoPinnacles_ReturnsNothing_AndDestroysTheObject()
        {
            var made = new List<WorldObject>();
            var row = new RolledReward(KnotItems.PatternWcid, 1, null, "pattern");

            var built = WorldEventRewardCrate.BuildRowObjects(row, w => { var o = CreateObject(); made.Add(o); return o; }, new List<PinnacleDef>(), 100, new Random(1));

            Assert.IsNull(built.Failure);
            Assert.AreEqual(0, built.Objects.Count);
            Assert.AreEqual(1, built.Skipped);
            Assert.AreEqual(1, made.Count);
            Assert.IsTrue(made[0].IsDestroyed, "the created object is destroyed, not leaked");
        }

        [TestMethod]
        public void BuildRowObjects_FiberRowWithNullLevel_ReturnsNothing()
        {
            var made = new List<WorldObject>();

            var built = WorldEventRewardCrate.BuildRowObjects(new RolledReward(KnotItems.FiberWcid, 1, null, "fiber"), w => { var o = CreateObject(); made.Add(o); return o; }, Pinnacles, null, new Random(1));

            Assert.AreEqual(0, built.Objects.Count);
            Assert.IsTrue(made.All(o => o.IsDestroyed));
        }

        [TestMethod]
        public void BuildRowObjects_MixedRoll_SkippedRowDoesNotCostItsSiblings()
        {
            var rolled = new List<RolledReward>
            {
                new RolledReward(9201, 15, 4),
                new RolledReward(KnotItems.PatternWcid, 1, null, "pattern"),
                new RolledReward(KnotItems.FiberWcid, 1, null, "fiber"),
                new RolledReward(9202, 3),
            };

            var items = new List<WorldObject>();
            var skipped = 0;

            foreach (var row in rolled)
            {
                var built = WorldEventRewardCrate.BuildRowObjects(row, w => CreateStackable(w), new List<PinnacleDef>(), 88, new Random(1), items.Count);

                Assert.IsNull(built.Failure);
                skipped += built.Skipped;
                items.AddRange(built.Objects);
            }

            Assert.AreEqual(1, skipped, "only the pattern is skipped");
            Assert.AreEqual(4, items.Count, "two L4 stacks, the fiber and the plain stack");
            Assert.AreEqual(88, items[2].GetProperty(PropertyInt.KnotLevel));
            Assert.IsNull(items[3].ItemMaxLevel);
        }

        [TestMethod]
        public void BuildRowObjects_UncreatableWcid_AndObjectCap_AreFailures()
        {
            Assert.IsNotNull(WorldEventRewardCrate.BuildRowObjects(new RolledReward(1, 1), w => null, null, 1, new Random(1)).Failure);

            var capped = WorldEventRewardCrate.BuildRowObjects(new RolledReward(9201, 1), w => CreateStackable(w), null, 1, new Random(1), WorldEventRewardCrate.MaxObjects);
            Assert.IsNotNull(capped.Failure);
            Assert.AreEqual(0, capped.Objects.Count);
        }

        [TestMethod]
        public void DescribeRow_NamesTheStamp()
        {
            Assert.AreEqual("42635:1(L5)", WorldEventRewardCrate.DescribeRow(new RolledReward(42635, 1, 5)));
            Assert.AreEqual("1007003:1(pattern)", WorldEventRewardCrate.DescribeRow(new RolledReward(1007003, 1, null, "pattern")));
            Assert.AreEqual("1007000:1(fiber)", WorldEventRewardCrate.DescribeRow(new RolledReward(1007000, 1, null, "fiber")));
            Assert.AreEqual("6331:2", WorldEventRewardCrate.DescribeRow(new RolledReward(6331, 2)));
        }

        [TestMethod]
        public void ApplyRollStamps_UnstampedRow_LeavesObjectUntouched()
        {
            var wo = CreateObject();

            WorldEventRewardCrate.ApplyRollStamps(wo, new RolledReward(9101, 1, null));

            Assert.IsNull(wo.ItemMaxLevel);
            Assert.IsNull(wo.IconOverlayId);
        }
    }
}
