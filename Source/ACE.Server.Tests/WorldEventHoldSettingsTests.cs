using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Sets Hold settings for one test through HoldSettings' property seam, and puts everything back when disposed. A value
    /// of null or "" leaves that setting unset.
    /// </summary>
    internal sealed class HoldSettingsScope : IDisposable
    {
        private readonly Func<string, string> previousSource;
        private readonly Func<bool> previousEnabled;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public HoldSettingsScope(params (string Key, string Value)[] settings)
        {
            previousSource = HoldSettings.StringSource;
            previousEnabled = HoldSettings.EnabledSource;

            foreach (var (key, value) in settings)
                Set(key, value);

            HoldSettings.StringSource = property => values.TryGetValue(property, out var v) ? v : null;
            HoldSettings.ClearParseCache();
        }

        /// <summary>Sets a setting by short name or property; the change is seen by the very next read (live).</summary>
        public void Set(string key, string value)
        {
            Assert.IsTrue(HoldSettings.TryFind(key, out var s), $"unknown Hold setting {key}");
            values[s.PropertyName] = value ?? "";
        }

        public void Enabled(bool on) => HoldSettings.EnabledSource = () => on;

        public void Dispose()
        {
            HoldSettings.StringSource = previousSource;
            HoldSettings.EnabledSource = previousEnabled;
            HoldSettings.ClearParseCache();
        }
    }

    /// <summary>
    /// The Hold settings registry and the master toggle (world_events_hold_enabled). The per-group settings have their
    /// own tests next to this file's later sections.
    /// </summary>
    [TestClass]
    public partial class WorldEventHoldSettingsTests
    {
        // ---- the registry --------------------------------------------------------------------------------------

        [TestMethod]
        public void Registry_EveryNewSettingIsAStringPropertyDefaultingToNotSet()
        {
            foreach (var s in HoldSettings.All.Where(s => !s.IsReused))
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultStringProperties.TryGetValue(s.PropertyName, out var def), $"{s.PropertyName} is registered");
                Assert.AreEqual("", def.Item, $"{s.PropertyName} defaults to not set");
                StringAssert.StartsWith(s.PropertyName, "world_events_", s.Key);
            }
        }

        [TestMethod]
        public void Registry_ReusedRowsPointAtTheirExistingTypedProperty()
        {
            foreach (var s in HoldSettings.All.Where(s => s.IsReused))
            {
                var found = s.ExistingType switch
                {
                    "long" => DefaultPropertyManager.DefaultLongProperties.ContainsKey(s.PropertyName),
                    "double" => DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(s.PropertyName),
                    "bool" => DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(s.PropertyName),
                    _ => false
                };

                Assert.IsTrue(found, $"{s.Key} reuses {s.ExistingType} {s.PropertyName}");
                Assert.IsFalse(DefaultPropertyManager.DefaultStringProperties.ContainsKey(s.PropertyName), $"{s.Key} gets no second, string, property");
            }
        }

        [TestMethod]
        public void Registry_ShortNamesAndPropertiesResolve_AndHoldDoesNotReuseTheGlobalWaveDials()
        {
            foreach (var s in HoldSettings.All)
            {
                Assert.IsTrue(HoldSettings.TryFind(s.Key, out var byKey) && ReferenceEquals(byKey, s), s.Key);
                Assert.IsTrue(HoldSettings.TryFind(s.PropertyName, out var byProperty) && ReferenceEquals(byProperty, s), s.PropertyName);
                Assert.IsTrue(HoldSettings.IsGroup(s.Group), s.Group);
            }

            // Owner ruling 2026-10-19: Hold never starts reading the global wave dials other events may have set.
            Assert.IsFalse(HoldSettings.All.Any(s => s.PropertyName == "world_events_wave_interval_seconds"));
            Assert.IsFalse(HoldSettings.All.Any(s => s.PropertyName.StartsWith("world_events_wave_count_", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void MasterToggle_ShipsOff()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(HoldSettings.EnabledProperty, out var def));
            Assert.IsFalse(def.Item, "Hold goals ship off");
        }

        // ---- the master toggle ---------------------------------------------------------------------------------

        private const uint Cell = 0x8090000C;

        private const string Sources = @"{ ""sources"": [ {
            ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""edges"", ""geometryRadius"": 45.0, ""geometryPoints"": 4,
            ""waveIntervalSeconds"": 40.0, ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
            ""rewardRadius"": 60.0, ""compatibleGoals"": [ ""hold_wards"", ""kill_count"" ],
            ""startFlavour"": ""x near {anchor}"", ""waveFlavour"": ""y"" } ] }";

        private const string Families = @"{ ""families"": [ { ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"" } ] }";

        private const string Goals = @"{ ""goals"": [
            { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
              ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
              ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" },
            { ""id"": ""hold_wards"", ""displayName"": ""Defend the Wards"", ""type"": ""Hold"", ""holdSeconds"": 420, ""mvpRule"": ""mostKills"",
              ""progressTemplate"": ""{alive} of {total} stand."",
              ""protect"": { ""wcid"": 2000001, ""count"": { ""base"": 1, ""perParticipant"": 0.2, ""cap"": 3 },
                ""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0, ""minSurvivors"": 1 } } ] }";

        private const string Rewards = @"{ ""rewards"": [ {
            ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
            ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
            ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

        private static WorldEventAxisStore Store() => WorldEventAxisStore.Parse(Sources, Families, Goals, Rewards, null, null);

        private static WorldEventCatalog Catalog() => WorldEventCatalogBuilder.Build(new List<Weenie>
        {
            new Weenie
            {
                WeenieClassId = 1002604,
                ClassName = "testcreature1002604",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.WorldEventCreature] = true, [PropertyBool.Attackable] = true },
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 20, [PropertyInt.WorldEventRole] = 0 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.WorldEventFamily] = "emberwrought", [PropertyString.Name] = "Test Creature" }
            }
        }, out _);

        private static WorldEventRequest Request(bool random, string goal) => new WorldEventRequest
        {
            Random = random,
            SourceId = "ambush",
            FamilyIds = random ? new List<string>() : new List<string> { "emberwrought" },
            GoalId = goal,
            RewardId = "standard",
            AnchorPosition = new Position(Cell, 96, 96, 124, 0, 0, 0, 1, 0),
            PositionAnchorId = WorldEventComposer.WebAnchorId
        };

        [TestMethod]
        public void MasterToggle_Off_RefusesAPinnedHoldGoal_On_ComposesIt()
        {
            using var scope = new HoldSettingsScope();

            scope.Enabled(false);
            Assert.IsFalse(WorldEventComposer.TryCompose(Store(), Catalog(), Request(false, "hold_wards"), out _, out var error));
            StringAssert.Contains(error, HoldSettings.EnabledProperty);

            Assert.IsTrue(WorldEventComposer.TryCompose(Store(), Catalog(), Request(false, "kill_count"), out _, out error), "other goals are untouched: " + error);

            scope.Enabled(true);
            Assert.IsTrue(WorldEventComposer.TryCompose(Store(), Catalog(), Request(false, "hold_wards"), out var composition, out error), error);
            Assert.AreEqual("hold_wards", composition.Goal.Id);
        }

        [TestMethod]
        public void MasterToggle_Off_RandomRollsNeverLandOnAHoldGoal()
        {
            using var scope = new HoldSettingsScope();
            scope.Enabled(false);

            for (var seed = 0; seed < 40; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(Store(), Catalog(), Request(true, null), out var composition, out var error, new Random(seed)), error);
                Assert.AreNotEqual(GoalType.Hold, composition.Goal.TypeKind, $"seed {seed}");
            }

            Assert.IsFalse(WorldEventComposer.TryCompose(Store(), Catalog(), Request(true, "hold_wards"), out _, out var refused, new Random(1)));
            StringAssert.Contains(refused, HoldSettings.EnabledProperty);

            scope.Enabled(true);
            var sawHold = Enumerable.Range(0, 40).Any(seed =>
                WorldEventComposer.TryCompose(Store(), Catalog(), Request(true, null), out var c, out _, new Random(seed)) && c.Goal.TypeKind == GoalType.Hold);
            Assert.IsTrue(sawHold, "with the toggle on, random rolls reach the Hold goal again");
        }
    }
}
