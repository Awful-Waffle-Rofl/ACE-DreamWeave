using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ThreadDungeonSpawner.StripGenerator: a run creature must never generate children (owner ruling
    /// 2026-10-05). PropertiesGenerator is one of the collections a WorldObject shares BY REFERENCE with the
    /// cached weenie (WorldObject.cs:141 passes referenceWeenieCollectionsForCommonProperties: true), so the
    /// strip has to REPLACE it; clearing it in place would remove the generator from every instance of the
    /// wcid server-wide.
    ///
    /// A DB-free GenericObject stands in for the creature: the strip is written against WorldObject, and a
    /// live Creature cannot be constructed under this harness (see ThreadDungeonSpawnerLootStripTests).
    /// </summary>
    [TestClass]
    public class ThreadDungeonSpawnerGeneratorStripTests
    {
        private const uint GeneratorWcid = 36031;

        private static Weenie CachedGeneratorWeenie(int profiles)
        {
            var weenie = new Weenie
            {
                WeenieClassId = GeneratorWcid,
                WeenieType = WeenieType.Generic,
                PropertiesGenerator = new List<PropertiesGenerator>(),
                // A regeneration interval, so InitializeHeartbeats leaves NextGeneratorRegenerationTime armed
                // (WorldObject_Tick.cs:77-78 sets MaxValue when it is 0) and the strip has something to disable.
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.RegenerationInterval, 30 } }
            };

            for (var i = 0; i < profiles; i++)
            {
                weenie.PropertiesGenerator.Add(new PropertiesGenerator
                {
                    Probability = -1,
                    WeenieClassId = 36032,
                    Delay = 1f,
                    InitCreate = 1,
                    MaxCreate = 1,
                    WhenCreate = RegenerationType.Destruction,
                    WhereCreate = RegenLocationType.Scatter
                });
            }

            return weenie;
        }

        private static WorldObject NewInstanceOf(Weenie weenie, uint guid) =>
            new GenericObject(weenie, new ObjectGuid(guid));

        [TestMethod]
        public void A_new_instance_is_a_generator_that_shares_its_collection_with_the_cached_weenie()
        {
            // The premise: without it the strip is vacuous and the tests below measure nothing.
            var weenie = CachedGeneratorWeenie(2);
            var wo = NewInstanceOf(weenie, 0x70000001);

            Assert.IsTrue(wo.IsGenerator, "the fixture is not a generator, so nothing here is being stripped");
            Assert.AreEqual(2, wo.GeneratorProfiles.Count);
            Assert.AreSame(weenie.PropertiesGenerator, wo.Biota.PropertiesGenerator,
                "WeenieConverter no longer reference-shares PropertiesGenerator; the strip's hazard has changed.");
        }

        [TestMethod]
        public void Strip_removes_every_profile_and_the_biota_rows_and_reports_the_count()
        {
            var wo = NewInstanceOf(CachedGeneratorWeenie(3), 0x70000001);

            var stripped = ThreadDungeonSpawner.StripGenerator(wo);

            Assert.AreEqual(3, stripped);
            Assert.AreEqual(0, wo.GeneratorProfiles.Count);
            Assert.IsFalse(wo.IsGenerator);
            Assert.IsTrue(wo.Biota.PropertiesGenerator == null || wo.Biota.PropertiesGenerator.Count == 0);
        }

        [TestMethod]
        public void Strip_disables_the_generator_heartbeats()
        {
            // InitializeHeartbeats armed NextGeneratorUpdateTime at construction because IsGenerator was true
            // then (WorldObject_Tick.cs:72-74); Landblock only indexes a generator whose time is not MaxValue.
            var wo = NewInstanceOf(CachedGeneratorWeenie(1), 0x70000001);

            Assert.AreNotEqual(double.MaxValue, wo.NextGeneratorUpdateTime, "premise: the update heartbeat was armed");
            Assert.AreNotEqual(double.MaxValue, wo.NextGeneratorRegenerationTime, "premise: the regeneration time was armed");

            ThreadDungeonSpawner.StripGenerator(wo);

            Assert.AreEqual(double.MaxValue, wo.NextGeneratorUpdateTime);
            Assert.AreEqual(double.MaxValue, wo.NextGeneratorRegenerationTime);
        }

        [TestMethod]
        public void Strip_leaves_the_cached_weenie_intact()
        {
            var weenie = CachedGeneratorWeenie(2);
            var wo = NewInstanceOf(weenie, 0x70000001);

            ThreadDungeonSpawner.StripGenerator(wo);

            Assert.AreEqual(2, weenie.PropertiesGenerator.Count,
                "the CACHED weenie lost generator rows; every instance of this wcid server-wide is now affected");
        }

        [TestMethod]
        public void Strip_leaves_a_sibling_instance_a_working_generator()
        {
            var weenie = CachedGeneratorWeenie(2);
            var runCreature = NewInstanceOf(weenie, 0x70000001);
            var retailCreature = NewInstanceOf(weenie, 0x70000002);

            ThreadDungeonSpawner.StripGenerator(runCreature);

            Assert.IsFalse(runCreature.IsGenerator);
            Assert.AreEqual(2, retailCreature.Biota.PropertiesGenerator.Count,
                "a sibling instance lost its generator rows, so the strip mutated the shared collection");

            // A fresh instance built AFTER the strip also still sees the generator.
            var later = NewInstanceOf(weenie, 0x70000003);
            Assert.IsTrue(later.IsGenerator);
            Assert.AreEqual(2, later.GeneratorProfiles.Count);
        }

        [TestMethod]
        public void Strip_of_a_non_generator_returns_zero_and_changes_nothing()
        {
            var weenie = new Weenie { WeenieClassId = 30000, WeenieType = WeenieType.Generic };
            var wo = NewInstanceOf(weenie, 0x70000001);

            Assert.AreEqual(0, ThreadDungeonSpawner.StripGenerator(wo));
            Assert.IsFalse(wo.IsGenerator);
        }

        [TestMethod]
        public void Strip_death_spawn_removes_the_trigger_from_the_instance_only()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 30000,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.DeathSpawnWcid, 36032 },
                    { PropertyInt.DeathSpawnCount, 3 }
                }
            };
            var wo = NewInstanceOf(weenie, 0x70000001);
            var sibling = NewInstanceOf(weenie, 0x70000002);

            Assert.AreEqual(36032, wo.GetProperty(PropertyInt.DeathSpawnWcid), "premise: the fixture carries a death spawn");

            Assert.IsTrue(ThreadDungeonSpawner.StripDeathSpawn(wo));

            Assert.IsNull(wo.GetProperty(PropertyInt.DeathSpawnWcid));
            Assert.AreEqual(36032, weenie.PropertiesInt[PropertyInt.DeathSpawnWcid],
                "the CACHED weenie lost its death spawn; every instance of this wcid server-wide is affected");
            Assert.AreEqual(36032, sibling.GetProperty(PropertyInt.DeathSpawnWcid), "a sibling instance lost its death spawn");
        }

        [TestMethod]
        public void Strip_death_spawn_without_one_returns_false_and_null_is_tolerated()
        {
            var wo = NewInstanceOf(new Weenie { WeenieClassId = 30000, WeenieType = WeenieType.Generic }, 0x70000001);

            Assert.IsFalse(ThreadDungeonSpawner.StripDeathSpawn(wo));
            Assert.IsFalse(ThreadDungeonSpawner.StripDeathSpawn(null));
        }

        [TestMethod]
        public void TryPlaceOnce_strips_generator_and_death_spawn_before_EnterWorld()
        {
            // Source-text pin of the production call site: the helpers are unit-tested above, but nothing else
            // would notice the calls being dropped from the spawner.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static PlaceOutcome TryPlaceOnce(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,");

            var enterWorld = body.IndexOf("creature.EnterWorld()", System.StringComparison.Ordinal);
            Assert.IsTrue(enterWorld >= 0, "creature.EnterWorld() not found in TryPlaceOnce");

            foreach (var call in new[] { "StripGenerator(creature)", "StripDeathSpawn(creature)" })
            {
                var at = body.IndexOf(call, System.StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, $"TryPlaceOnce no longer calls {call}");
                Assert.IsTrue(at < enterWorld, $"{call} must run before creature.EnterWorld()");
            }
        }
        [TestMethod]
        public void A_null_creature_is_tolerated()
        {
            Assert.AreEqual(0, ThreadDungeonSpawner.StripGenerator(null));
        }
    }
}
