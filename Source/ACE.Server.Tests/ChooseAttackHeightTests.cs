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
    /// Creature.ChooseAttackHeight's null guard (2026-11-10). Monster_Missile.LaunchMissile calls it on every shot, and
    /// AttackHeights is null until GetCombatTable has loaded the combat table - which only GetNextAttackType and
    /// GamePiece do. The Hunter's Echo sets CurrentAttack itself and never runs GetNextAttackType, so on stage every
    /// Echo LaunchMissile threw a NullReferenceException at Monster_Combat.cs:102 and the Echo never fired.
    ///
    /// The full Echo shot path cannot run here: past ChooseAttackHeight it needs a physics object, the portal dat
    /// (motion tables) and DatabaseManager.World (GetProjectileRadius), none of which this host has. These tests pin
    /// the guard on the pure pick and on a real, unloaded CombatPet built the way the Echo's weenie is.
    /// </summary>
    [TestClass]
    public class ChooseAttackHeightTests
    {
        [TestMethod]
        public void Pick_NullList_IsMedium()
        {
            Assert.AreEqual(AttackHeight.Medium, Creature.ChooseAttackHeight((List<AttackHeight>)null));
        }

        [TestMethod]
        public void Pick_EmptyList_IsMedium()
        {
            Assert.AreEqual(AttackHeight.Medium, Creature.ChooseAttackHeight(new List<AttackHeight>()));
        }

        [TestMethod]
        public void Pick_OneHeight_IsThatHeight()
        {
            for (var i = 0; i < 20; i++)
                Assert.AreEqual(AttackHeight.High, Creature.ChooseAttackHeight(new List<AttackHeight> { AttackHeight.High }));
        }

        [TestMethod]
        public void Pick_SeveralHeights_StaysInTheList_AndReachesEveryEntry()
        {
            var heights = new List<AttackHeight> { AttackHeight.High, AttackHeight.Low };
            var seen = new HashSet<AttackHeight>();

            for (var i = 0; i < 500; i++)
            {
                var pick = Creature.ChooseAttackHeight(heights);
                CollectionAssert.Contains(heights, pick);
                seen.Add(pick);
            }

            // ThreadSafeRandom.Next(int, int) is inclusive of its max, so the last entry is reachable
            Assert.AreEqual(2, seen.Count, "every listed height must be reachable");
        }

        /// <summary>
        /// The live state: a CombatPet whose weenie names a combat table (the Echo's 1008050 carries 0x30000000) that
        /// was never loaded, because nothing on the Echo path calls GetNextAttackType. It used to throw.
        /// </summary>
        [TestMethod]
        public void CombatPet_WithAnUnloadedCombatTable_ChoosesMedium_AndDoesNotThrow()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999875u,
                ClassName = "attackheighttestecho",
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Attack Height Test Echo" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, 0x02000001u },
                    { PropertyDataId.MotionTable, 0x09000001u },
                    { PropertyDataId.CombatTable, 0x30000000u },
                },
            };

            TestGameTables.EnsureInitialized();

            var pet = new CombatPet(weenie, new ObjectGuid(0x8FFFF0A1));

            Assert.IsNull(pet.CombatTable, "precondition: the table is only loaded by GetCombatTable");
            Assert.IsNull(pet.AttackHeights, "precondition: no table, no heights");

            Assert.AreEqual(AttackHeight.Medium, pet.ChooseAttackHeight());
        }
    }
}
