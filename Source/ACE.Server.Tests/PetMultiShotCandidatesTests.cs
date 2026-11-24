using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Revenant Marksman "does not seem to fire multishot" (2026-11-02). A combat pet's extra arrows pick their
    /// secondary targets in Creature.GetMultiShotTargets, which read ObjMaint's VisibleObjects. ObjectMaint keeps
    /// VisibleObjects for players only, so for a pet that list was always empty: GetMultiShotTargets found no
    /// secondary target, and every extra arrow overflowed onto the primary at a third of its damage, flying the
    /// same line as the real arrow. The pet's candidates now come from VisibleTargets, the list its own target
    /// selection (CombatPet.GetNearbyMonsters) already reads.
    ///
    /// The objects carry bare PhysicsObjs (no cell, no dats): VisibleTargets is filled through the real
    /// ObjectMaint.AddVisibleTargets, the same call PhysicsObj.handle_visible_cells_non_player makes. The rest of
    /// GetMultiShotTargets (line of sight) needs a landblock and dats, so it is not run here; the source pin
    /// below keeps it reading GetMultiShotCandidates. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class PetMultiShotCandidatesTests
    {
        private static uint nextGuid = 0x7F5C0000;
        private static uint nextWcid = 995800;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals() =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> { [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 } };

        private static ACE.Entity.Position At(float x) => new ACE.Entity.Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

        private static T WithPhysics<T>(T wo) where T : WorldObject
        {
            var physicsObj = new PhysicsObj
            {
                ID = wo.Guid.Full,
                Position = new ACE.Server.Physics.Common.Position(wo.Location),
            };

            physicsObj.WeenieObj = new WeenieObject(wo);

            var prop = typeof(WorldObject).GetProperty("PhysicsObj");
            Assert.IsNotNull(prop, "WorldObject.PhysicsObj was not found by reflection - has it been renamed?");
            prop.SetValue(wo, physicsObj);

            return wo;
        }

        private static CombatPet Pet()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Marksman" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.SummonNeverInitiates, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.Location = At(50.0f);
            return WithPhysics(pet);
        }

        private static Creature Monster(float x)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Monster" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            var monster = new Creature(weenie, new ObjectGuid(nextGuid++));
            monster.Location = At(x);
            return WithPhysics(monster);
        }

        [TestMethod]
        public void PetCandidates_AreTheMonstersItTracks_NotThePlayerOnlyVisibleObjects()
        {
            var pet = Pet();
            var near = Monster(55.0f);
            var far = Monster(60.0f);

            Assert.IsTrue(pet.PhysicsObj.WeenieObj.IsCombatPet && near.PhysicsObj.WeenieObj.IsMonster, "fixture: the physics flags ObjectMaint filters on");

            var added = pet.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { far.PhysicsObj, near.PhysicsObj });
            Assert.AreEqual(0, added.Count, "AddVisibleTargets returns only newly known PLAYERS");
            Assert.AreEqual(2, pet.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "precondition: the pet tracks both monsters, as its own targeting sees them");

            // the defect's mechanism: VisibleObjects is a player-only list, so the old candidate source was empty
            Assert.AreEqual(0, pet.PhysicsObj.ObjMaint.GetVisibleObjectsCount(), "a pet never has VisibleObjects");

            var candidates = pet.GetMultiShotCandidates();

            CollectionAssert.AreEquivalent(new[] { near.PhysicsObj, far.PhysicsObj }, candidates,
                "a pet's extra arrows must be able to find the monsters around its target");
        }

        [TestMethod]
        public void PlainCreatureCandidates_StayOnVisibleObjects()
        {
            // the base (player) source is unchanged: VisibleObjects. A plain creature has none, so it gets none,
            // even while it tracks targets - only CombatPet switches to VisibleTargets.
            var shooter = Monster(50.0f);
            var pet = Pet();

            shooter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { pet.PhysicsObj });
            Assert.AreEqual(1, shooter.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "precondition: the creature tracks a target");

            Assert.AreEqual(0, shooter.GetMultiShotCandidates().Count);
        }

        [TestMethod]
        public void Pin_MultiShotTargets_ReadTheShootersCandidates()
        {
            var body = ThreadDungeons.PooledLootSourceText.MethodBody(
                ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Missile.cs"),
                "public List<Creature> GetMultiShotTargets(");

            StringAssert.Contains(body, "var visible = GetMultiShotCandidates();",
                "GetMultiShotTargets must take its candidates from the per-shooter source, or a pet finds none");
            Assert.IsFalse(body.Contains("GetVisibleObjectsValuesWhere"),
                "a direct VisibleObjects read is empty for every shooter but a player");
        }
    }
}
