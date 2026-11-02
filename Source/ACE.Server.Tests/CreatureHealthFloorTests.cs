using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Creature.P_HealthFloor (the speed-course boss guard): an overkill stops at the floor and does not kill, heals are
    /// unaffected, and floor 0 is the upstream clamp exactly. Driven through UpdateVitalDelta, the write every
    /// creature damage sink makes before its death test (Creature.TakeDamage, SpellProjectile.DamageTarget, life-magic
    /// harm and drain, Hotspot, heal-over-time, regeneration).
    /// </summary>
    [TestClass]
    public class CreatureHealthFloorTests
    {
        private static uint nextGuid = 0x7F0002A1;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static Creature NewCreature()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 0x7F0002A1,
                ClassName = "healthfloortest",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Floor Test" },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
                },
            };

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.Health.Current = creature.Health.MaxValue;
            return creature;
        }

        [TestMethod]
        public void Floor_zero_is_the_upstream_clamp_and_an_overkill_kills()
        {
            var c = NewCreature();
            var max = c.Health.MaxValue;
            Assert.IsTrue(max > 100, "positive control: the fixture has health to lose");

            var delta = c.UpdateVitalDelta(c.Health, -(int)(max + 500));

            Assert.AreEqual(0u, c.Health.Current);
            Assert.AreEqual(-(int)max, delta, "the returned change is what was actually removed");
            Assert.IsTrue(c.IsDead);
        }

        [TestMethod]
        public void An_overkill_stops_at_the_floor_and_does_not_kill()
        {
            var c = NewCreature();
            var max = c.Health.MaxValue;
            c.P_HealthFloor = max * 3 / 4;

            var delta = c.UpdateVitalDelta(c.Health, -(int)(max * 10));

            Assert.AreEqual(max * 3 / 4, c.Health.Current, "pinned at the floor");
            Assert.AreEqual(-(int)(max - max * 3 / 4), delta, "the returned change is the damage actually applied");
            Assert.IsFalse(c.IsDead);

            // Pinned: more damage changes nothing while the floor stands.
            Assert.AreEqual(0, c.UpdateVitalDelta(c.Health, -50));
            Assert.AreEqual(max * 3 / 4, c.Health.Current);

            // A non-overkill hit above the floor lands in full.
            c.P_HealthFloor = max / 4;
            Assert.AreEqual(-10, c.UpdateVitalDelta(c.Health, -10));
            Assert.AreEqual(max * 3 / 4 - 10, c.Health.Current);

            // Floor cleared: the next overkill kills.
            c.P_HealthFloor = 0;
            c.UpdateVitalDelta(c.Health, -(int)(max * 10));
            Assert.IsTrue(c.IsDead);
        }

        [TestMethod]
        public void Heals_are_unaffected_by_the_floor()
        {
            var c = NewCreature();
            var max = c.Health.MaxValue;
            c.P_HealthFloor = max / 2;

            c.UpdateVitalDelta(c.Health, -(int)max);
            Assert.AreEqual(max / 2, c.Health.Current);

            Assert.AreEqual(50, c.UpdateVitalDelta(c.Health, 50));
            Assert.AreEqual(max / 2 + 50, c.Health.Current);

            c.UpdateVital(c.Health, (int)max);
            Assert.AreEqual(max, c.Health.Current, "a heal to full is not clamped");
        }

        [TestMethod]
        public void Below_the_floor_already_it_takes_no_more_damage_but_still_heals()
        {
            var c = NewCreature();
            var max = c.Health.MaxValue;

            c.UpdateVital(c.Health, 100);
            c.P_HealthFloor = max / 2;

            Assert.AreEqual(0, c.UpdateVitalDelta(c.Health, -60), "min(current, floor) is current");
            Assert.AreEqual(100u, c.Health.Current);
            Assert.AreEqual(20, c.UpdateVitalDelta(c.Health, 20));
        }

        [TestMethod]
        public void Only_health_is_floored()
        {
            var c = NewCreature();
            c.P_HealthFloor = c.Health.MaxValue;
            c.Stamina.Current = c.Stamina.MaxValue;

            c.UpdateVitalDelta(c.Stamina, -(int)(c.Stamina.MaxValue + 10));

            Assert.AreEqual(0u, c.Stamina.Current);
        }

        [TestMethod]
        public void TakeDamage_overkill_is_pinned_at_the_floor_and_does_not_kill()
        {
            var c = NewCreature();
            var max = c.Health.MaxValue;
            c.P_HealthFloor = max / 2;

            var dealt = c.TakeDamage(null, DamageType.Slash, max * 3f);

            Assert.IsFalse(c.IsDead, "the death check after the vital write sees the floor");
            Assert.AreEqual(max / 2, c.Health.Current);
            Assert.AreEqual(max - max / 2, dealt, "TakeDamage reports what actually landed");

            // Control: the same overkill with no floor kills.
            var control = NewCreature();
            control.UpdateVitalDelta(control.Health, -(int)(control.Health.MaxValue * 3));
            Assert.IsTrue(control.IsDead);
        }

        [TestMethod]
        public void ClampToHealthFloor_is_the_rule()
        {
            Assert.AreEqual(0u, Creature.ClampToHealthFloor(100, 0, 0), "floor 0: no-op");
            Assert.AreEqual(50u, Creature.ClampToHealthFloor(100, 0, 50), "overkill stops at the floor");
            Assert.AreEqual(70u, Creature.ClampToHealthFloor(100, 70, 50), "a hit above the floor lands");
            Assert.AreEqual(150u, Creature.ClampToHealthFloor(100, 150, 50), "a heal lands");
            Assert.AreEqual(40u, Creature.ClampToHealthFloor(40, 0, 50), "below the floor: min(current, floor)");
            Assert.AreEqual(45u, Creature.ClampToHealthFloor(40, 45, 50), "below the floor a heal still lands");
        }
    }
}
