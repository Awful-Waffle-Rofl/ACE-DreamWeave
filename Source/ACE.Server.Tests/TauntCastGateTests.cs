using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Taunt halves a held monster's spell-cast chance: the pure gate (including that an untaunted monster
    /// invokes neither the multiplier reader nor the roll), the IsTauntHeld liveness checks, and the instance
    /// wiring from IsTauntHeld into the gate. Monsters are built as in PetEngagementTests; the landblock is an
    /// uninitialized stand-in because only a null test is made on it.
    /// </summary>
    [TestClass]
    public class TauntCastGateTests
    {
        private static uint nextGuid = 0x7F5C0000;
        private static uint nextWcid = 995700;

        private static readonly Landblock SpawnedLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
            PropertyManager.ModifyDouble("class_ability_taunt_duration",
                DefaultPropertyManager.DefaultDoubleProperties["class_ability_taunt_duration"].Item);
        }

        private static Creature NewCreature(string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 },
                },
            };

            var c = new Creature(weenie, new ObjectGuid(nextGuid++));
            c.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            c.CurrentLandblock = SpawnedLandblock;
            c.MonsterState = Creature.State.Awake;
            c.IsAwake = true;
            return c;
        }

        // Already targeting the taunter, so ApplyTaunt skips the new-enemy emote path (no EmoteManager in the harness).
        private static void Hold(Creature monster, Creature taunter)
        {
            monster.AttackTarget = taunter;
            Assert.IsTrue(monster.ApplyTaunt(taunter, 10.0));
        }

        // ---- the pure gate ---------------------------------------------------------------------------------

        [TestMethod]
        public void NotHeld_Passes_AndInvokesNeitherTheReaderNorTheRoll()
        {
            var multCalls = 0;
            var rollCalls = 0;

            Assert.IsTrue(Creature.PassesTauntCastGate(false, () => { multCalls++; return 0.0; }, () => { rollCalls++; return 0.99; }));
            Assert.AreEqual(0, multCalls, "an untaunted monster must not read the tunable");
            Assert.AreEqual(0, rollCalls, "an untaunted monster must not consume a random draw");
        }

        [TestMethod]
        public void Held_HalfMult_BoundaryAtHalf()
        {
            Assert.IsTrue(Creature.PassesTauntCastGate(true, () => 0.5, () => 0.49));
            Assert.IsFalse(Creature.PassesTauntCastGate(true, () => 0.5, () => 0.5));
            Assert.IsFalse(Creature.PassesTauntCastGate(true, () => 0.5, () => 0.99));
        }

        [TestMethod]
        public void Held_MultAboveOne_ClampsToOne()
        {
            Assert.IsTrue(Creature.PassesTauntCastGate(true, () => 1.5, () => 0.999));
        }

        [TestMethod]
        public void Held_NegativeMult_ClampsToZero()
        {
            Assert.IsFalse(Creature.PassesTauntCastGate(true, () => -1.0, () => 0.0));
        }

        // ---- IsTauntHeld ------------------------------------------------------------------------------------

        [TestMethod]
        public void IsTauntHeld_FalseWithNoTaunt()
        {
            Assert.IsFalse(NewCreature("Monster").IsTauntHeld);
        }

        [TestMethod]
        public void IsTauntHeld_TrueForAnUnexpiredHoldFromALiveTaunter()
        {
            var monster = NewCreature("Monster");
            Hold(monster, NewCreature("Taunter"));

            Assert.IsTrue(monster.IsTauntHeld);
        }

        [TestMethod]
        public void IsTauntHeld_FalseOnceTheHoldExpires()
        {
            var monster = NewCreature("Monster");
            Hold(monster, NewCreature("Taunter"));
            monster.TauntExpiry = Timers.RunningTime - 1.0;

            Assert.IsFalse(monster.IsTauntHeld);
        }

        [TestMethod]
        public void IsTauntHeld_FalseWhenTheTaunterIsDead()
        {
            var monster = NewCreature("Monster");
            var taunter = NewCreature("Taunter");
            Hold(monster, taunter);
            taunter.Health.Current = 0;

            Assert.IsFalse(monster.IsTauntHeld);
        }

        [TestMethod]
        public void IsTauntHeld_FalseWhenTheTaunterLeftItsLandblock()
        {
            var monster = NewCreature("Monster");
            var taunter = NewCreature("Taunter");
            Hold(monster, taunter);
            taunter.CurrentLandblock = null;

            Assert.IsFalse(monster.IsTauntHeld);
        }

        // ---- the instance wiring and the reader seam ------------------------------------------------------

        [TestMethod]
        public void InstanceGate_UsesIsTauntHeld_AndTheReaderSeam()
        {
            var original = Creature.TauntCastChanceMultReader;
            try
            {
                var reads = 0;
                Creature.TauntCastChanceMultReader = () => { reads++; return 0.0; };

                var free = NewCreature("Free");
                Assert.IsTrue(free.PassesTauntCastGate(), "untaunted: always passes");
                Assert.AreEqual(0, reads, "untaunted: tunable not read");

                var held = NewCreature("Held");
                Hold(held, NewCreature("Taunter"));
                Assert.IsFalse(held.PassesTauntCastGate(), "taunted with mult 0: never passes");
                Assert.AreEqual(1, reads);

                Creature.TauntCastChanceMultReader = () => 1.0;
                Assert.IsTrue(held.PassesTauntCastGate(), "taunted with mult 1.0 (off): always passes");
            }
            finally
            {
                Creature.TauntCastChanceMultReader = original;
            }
        }

        [TestMethod]
        public void DefaultReader_ReturnsHalf_WhenNothingIsConfigured()
        {
            // Either the code default (0.5) is cached, or the read misses and throws in the test host and the
            // catch supplies 0.5. Both paths must yield 0.5, so this is deterministic in any class order.
            Assert.AreEqual(0.5, Creature.TauntCastChanceMultReader(), 1e-12);
        }
    }
}
