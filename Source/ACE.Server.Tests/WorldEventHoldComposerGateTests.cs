using System;
using System.Collections.Generic;
using System.IO;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The World Events Hold composer gate (H6): the random roll never lands on a Hold goal (one constant decides that), and a
    /// pinned Hold goal is refused with a line naming world_events_hold_enabled while the switch is off. Pure: the switch goes
    /// through WorldEventComposer.HoldEnabledReader, restored in a finally.
    /// </summary>
    [TestClass]
    public class WorldEventHoldComposerGateTests
    {
        private const string GoalId = "defend_envoys";

        internal static WorldEventCatalog CatalogFor(string family)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1002604,
                ClassName = "testcreature1002604",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    [PropertyBool.WorldEventCreature] = true,
                    [PropertyBool.Attackable] = true
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.Level] = 120,
                    [PropertyInt.WorldEventRole] = 0
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.WorldEventFamily] = family,
                    [PropertyString.Name] = "Test Creature"
                }
            };

            return WorldEventCatalogBuilder.Build(new List<Weenie> { weenie }, out _);
        }

        internal static WorldEventRequest Request(string sourceId, string goalId, string familyId, bool random)
        {
            return new WorldEventRequest
            {
                Random = random,
                SourceId = sourceId,
                GoalId = goalId,
                FamilyId = familyId,
                AnchorPosition = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test"
            };
        }

        private static WorldEventAxisStore RealStore()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "events", "axes", "goals.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/events/axes/goals.json by walking up from {AppContext.BaseDirectory}");

            return WorldEventAxisStore.Load(Path.Combine(dir.FullName, "Content", "events", "axes"));
        }

        private static string FirstFamily(WorldEventAxisStore store)
        {
            foreach (var key in new SortedSet<string>(store.Families.Keys, StringComparer.Ordinal))
                return key;

            Assert.Fail("the real axes store has no families");
            return null;
        }

        [TestMethod]
        public void RandomRoll_ShipsPinnedOnly()
        {
            Assert.IsFalse(WorldEventComposer.RandomRollIncludesHoldGoals,
                "the owner has not ruled that a random roll may land on a Hold goal; H6 ships pinned-only");
        }

        [TestMethod]
        public void RandomRollMayPick_NeverHold_WhateverTheSwitch_AndAlwaysOtherGoals()
        {
            var hold = new GoalDef { Id = GoalId, TypeKind = GoalType.Hold };
            var kill = new GoalDef { Id = "kill_count", TypeKind = GoalType.KillCount };

            Assert.AreEqual(WorldEventComposer.RandomRollIncludesHoldGoals, WorldEventComposer.RandomRollMayPick(hold, holdEnabled: true));
            Assert.IsFalse(WorldEventComposer.RandomRollMayPick(hold, holdEnabled: false));
            Assert.IsTrue(WorldEventComposer.RandomRollMayPick(kill, holdEnabled: false));
            Assert.IsTrue(WorldEventComposer.RandomRollMayPick(kill, holdEnabled: true));
            Assert.IsTrue(WorldEventComposer.RandomRollMayPick(null, holdEnabled: false));
        }

        [TestMethod]
        public void PinnedHoldGoal_SwitchOff_IsRefused_NamingTheSwitch()
        {
            var store = RealStore();
            var family = FirstFamily(store);
            var saved = WorldEventComposer.HoldEnabledReader;

            try
            {
                WorldEventComposer.HoldEnabledReader = _ => false;

                Assert.IsFalse(WorldEventComposer.TryCompose(store, CatalogFor(family), Request("ambush", GoalId, family, random: false),
                    out var composition, out var error));
                Assert.IsNull(composition);
                StringAssert.Contains(error, WorldEventObjectiveFactory.HoldEnabledProperty);

                // The same goal pinned on a random roll (--goal with start random) is refused the same way.
                Assert.IsFalse(WorldEventComposer.TryCompose(store, CatalogFor(family), Request(null, GoalId, family, random: true),
                    out _, out var randomError, new Random(1)));
                StringAssert.Contains(randomError, WorldEventObjectiveFactory.HoldEnabledProperty);
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }

        [TestMethod]
        public void PinnedHoldGoal_SwitchOn_PassesTheGate()
        {
            var store = RealStore();
            var family = FirstFamily(store);
            var saved = WorldEventComposer.HoldEnabledReader;

            try
            {
                WorldEventComposer.HoldEnabledReader = _ => true;

                var ok = WorldEventComposer.TryCompose(store, CatalogFor(family), Request("ambush", GoalId, family, random: false),
                    out var composition, out var error);

                Assert.IsTrue(ok, error);
                Assert.AreEqual(GoalId, composition.Goal.Id);
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }

        [TestMethod]
        public void PinnedHoldGoal_ReaderThrows_ReadsAsOff()
        {
            var store = RealStore();
            var family = FirstFamily(store);
            var saved = WorldEventComposer.HoldEnabledReader;

            try
            {
                WorldEventComposer.HoldEnabledReader = _ => throw new InvalidOperationException("no shard");

                Assert.IsFalse(WorldEventComposer.TryCompose(store, CatalogFor(family), Request("ambush", GoalId, family, random: false),
                    out _, out var error));
                StringAssert.Contains(error, WorldEventObjectiveFactory.HoldEnabledProperty);
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }

        [TestMethod]
        public void NonHoldGoal_NeverReadsTheSwitch()
        {
            var store = RealStore();
            var family = FirstFamily(store);
            var saved = WorldEventComposer.HoldEnabledReader;
            var reads = 0;

            try
            {
                WorldEventComposer.HoldEnabledReader = _ => { reads++; return false; };

                var ok = WorldEventComposer.TryCompose(store, CatalogFor(family), Request("ambush", "kill_count", family, random: false),
                    out _, out var error);

                Assert.IsTrue(ok, error);
                Assert.AreEqual(0, reads, "a non-Hold composition read world_events_hold_enabled");
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }
    }
}
