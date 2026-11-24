using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.WorldObjects;

using G = ACE.Server.Tests.Avatars.AvatarLookApplierGoldenTests;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The fighter wears the Avatar's look (AvatarLookApplier.ApplyFighter through AvatarFighterFactory.Dress): the body, the
    /// 2x scale and the worn pieces, while its own weapon and shield stay on and grow with it. And the Gauntlet dresses every
    /// fighter before it places it. No world, no database, no dats; no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class AvatarFighterLookTests
    {
        private const uint Jerkin = 124, Breeches = 117, Sword = 5555, Shield = 5556;
        private const uint FighterBlade = 1007728, FighterAegis = 1007753, ScholarShoes = 132;

        private static uint nextGuid = 0x7EC98000;
        private static int faceRolls;

        [TestInitialize]
        public void Setup()
        {
            GauntletFighterFixtures.EnsureInitialized();
            faceRolls = 0;

            AvatarLookApplier.ResetSeams();
            AvatarLookApplier.PieceBuilder = G.BuildPiece;
            AvatarLookApplier.HeritageGenderValid = (h, g) => h == 3 || h == 1;
            AvatarLookApplier.DefaultWeenie = DefaultWeenie;
            AvatarLookApplier.FaceGenerator = _ => faceRolls++;
            AvatarLookApplier.ScaleFactor = () => 2.0;
            AvatarFighterFactory.ResetSeams();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarLookApplier.ResetSeams();
            AvatarFighterFactory.ResetSeams();
        }

        private static Weenie DefaultWeenie() => new Weenie
        {
            WeenieClassId = AvatarVendor.AvatarWcid,
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Gender, 1 }, { PropertyInt.HeritageGroup, 3 } },
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Sex, "Male" }, { PropertyString.HeritageGroup, "Sho" } },
            PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001 } },
            PropertiesCreateList = new List<PropertiesCreateList>
            {
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Jerkin, Palette = 9 },
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Sword },
            },
        };

        private static WorldObject Item(uint wcid, EquipMask slot, int damage = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ValidLocations, (int)slot } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Item " + wcid } },
            };

            if (damage > 0)
                weenie.PropertiesInt[PropertyInt.Damage] = damage;

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        /// <summary>A fighter wearing what its weenie wields: the scholar's shoes, the Warden blade and the Aegis.</summary>
        private static GauntletAvatarFighter ArmedFighter()
        {
            var fighter = GauntletFighterFixtures.Fighter();

            Assert.IsTrue(fighter.TryEquipObject(Item(ScholarShoes, EquipMask.FootWear), EquipMask.FootWear), "fixture: shoes");
            Assert.IsTrue(fighter.TryEquipObject(Item(FighterBlade, EquipMask.MeleeWeapon, 300), EquipMask.MeleeWeapon), "fixture: blade");
            Assert.IsTrue(fighter.TryEquipObject(Item(FighterAegis, EquipMask.Shield), EquipMask.Shield), "fixture: shield");

            return fighter;
        }

        private static WorldObject Worn(Creature c, uint wcid) => c.EquippedObjects.Values.SingleOrDefault(i => i.WeenieClassId == wcid);

        [TestMethod]
        public void DressedLook_BodyScaleAndWornPieces_ArmsKeptAndScaled()
        {
            var fighter = ArmedFighter();
            var motionBefore = fighter.CurrentMotionState;

            AvatarLookApplier.ApplyFighter(fighter, G.Look(G.Body(MotionStance.NonCombat, 0x05001111, 0x05002222),
                G.Piece(Jerkin, EquipMask.ChestArmor), G.Piece(Breeches, EquipMask.LowerLegWear), G.Piece(Sword, EquipMask.MeleeWeapon), G.Piece(Shield, EquipMask.Shield)));

            // the body and the 2x scale (the look's 1.1 x the factor)
            Assert.AreEqual(1, fighter.Heritage);
            Assert.AreEqual(2, fighter.Gender);
            Assert.AreEqual(0x02000002u, fighter.GetProperty(PropertyDataId.Setup));
            Assert.AreEqual(2.2f, fighter.ObjScale.Value, 1e-5f);
            Assert.AreEqual(((uint)0x05001111, (uint)0x05002222), fighter.HairTextureOverride, "the hair pair rides the fighter, as on the vendor");
            Assert.AreEqual(0, faceRolls, "a look carries its own face");

            // the scholar outfit is gone; the look's worn pieces are on, cosmetic
            Assert.IsNull(Worn(fighter, ScholarShoes), "the weenie's clothes come off");
            Assert.AreEqual(EquipMask.ChestArmor, Worn(fighter, Jerkin).CurrentWieldedLocation);
            Assert.AreEqual(EquipMask.LowerLegWear, Worn(fighter, Breeches).CurrentWieldedLocation);
            Assert.AreEqual(true, Worn(fighter, Jerkin).GetProperty(PropertyBool.AvatarCosmetic));

            // its own arms stay, with their damage, and grow with the body; the look's held pieces are not put on
            var blade = Worn(fighter, FighterBlade);
            var aegis = Worn(fighter, FighterAegis);
            Assert.IsNotNull(blade, "the Warden blade survives the look");
            Assert.IsNotNull(aegis, "the Aegis survives the look");
            Assert.AreEqual(300, blade.GetProperty(PropertyInt.Damage), "the blade is not stripped to cosmetic");
            Assert.AreEqual(2.0f, blade.ObjScale.Value, 1e-5f);
            Assert.AreEqual(2.0f, aegis.ObjScale.Value, 1e-5f);
            Assert.IsNull(Worn(fighter, Sword), "the look's sword is not put on");
            Assert.IsNull(Worn(fighter, Shield), "the look's shield is not put on");
            Assert.AreEqual(4, fighter.EquippedObjects.Count);

            Assert.AreSame(motionBefore, fighter.CurrentMotionState, "the fighter's motion is left for placement");
        }

        [TestMethod]
        public void NullLook_DefaultBodyAndWornDefaults_ArmsKept()
        {
            var fighter = ArmedFighter();

            AvatarLookApplier.ApplyFighter(fighter, null);

            Assert.AreEqual(3, fighter.Heritage);
            Assert.AreEqual(1, faceRolls, "the default body rolls a face");
            Assert.AreEqual(2.0f, fighter.ObjScale.Value, 1e-5f, "1.0 x the factor");
            Assert.IsNotNull(Worn(fighter, Jerkin), "the default create list's worn piece");
            Assert.IsNull(Worn(fighter, Sword), "the default create list's weapon is not put on");
            Assert.IsNotNull(Worn(fighter, FighterBlade));
            Assert.IsNotNull(Worn(fighter, FighterAegis));
            Assert.IsNull(Worn(fighter, ScholarShoes));
        }

        [TestMethod]
        public void Factory_Dress_ReadsTheAvatarsLook_AndBindsTheAvatar()
        {
            var asked = new List<uint>();
            AvatarFighterFactory.LookOf = id => { asked.Add(id); return G.Look(G.Body(), G.Piece(Jerkin, EquipMask.ChestArmor)); };
            var fighter = ArmedFighter();

            Assert.IsTrue(AvatarFighterFactory.Dress(fighter, 4321));

            CollectionAssert.AreEqual(new[] { 4321u }, asked);
            Assert.AreEqual(4321u, fighter.AvatarId);
            Assert.IsNotNull(Worn(fighter, Jerkin));
            Assert.AreEqual(2.2f, fighter.ObjScale.Value, 1e-5f);
        }

        [TestMethod]
        public void Factory_Dress_AThrowIsContained_TheFighterKeepsItsArms()
        {
            AvatarFighterFactory.LookOf = _ => throw new InvalidOperationException("no Avatar manager");
            var fighter = ArmedFighter();

            Assert.IsFalse(AvatarFighterFactory.Dress(fighter, 1));
            Assert.IsNotNull(Worn(fighter, FighterBlade));
        }

        [TestMethod]
        public void Gauntlet_DressesEveryFighter_BeforePlacingIt_SpawnAndRespawn()
        {
            var h = new FighterRunHarness();
            h.StartActive();

            var first = h.Placed.Single();
            first.OnDeath(null, DamageType.Slash);
            h.Advance(h.Tuning.FighterRespawnSeconds + 1);

            Assert.AreEqual(2, h.Placed.Count, "fixture: a respawn happened");
            Assert.IsTrue(h.DressedBeforePlace.All(b => b), "every placement was preceded by a dress of that fighter");
            Assert.AreEqual(2, h.DressedBeforePlace.Count);
            Assert.IsTrue(h.Dressed.All(d => d.Item2 == FighterRunHarness.AvatarId), "dressed in the run's Avatar's look");
        }
    }
}
