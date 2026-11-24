using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>Dressing an AvatarVendor from a stored look. No world, no database, no dats: every outside read goes through a seam.</summary>
    [TestClass]
    public class AvatarLookApplierTests
    {
        private const uint Jerkin = 124, Breeches = 117, Sword = 5555, Wand = 6666, Missing = 7777;
        private const EquipMask Chest = EquipMask.ChestArmor, Legs = EquipMask.LowerLegWear;

        private static uint nextGuid = 0x7EC95000;
        private static readonly List<WorldObject> built = new List<WorldObject>();
        private static int faceRolls;

        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        [TestInitialize]
        public void Setup()
        {
            // Vendor's constructor chain reads PropertyManager; seed the key it reads and put it back in Cleanup
            SalvageRollerVendorTests.SeedVendorConstructible();

            built.Clear();
            dynamicPieceGuids = false;
            defaultWeenieScale = null;
            defaultWithSword = false;
            defaultWeenieLookups = 0;
            faceRolls = 0;

            AvatarLookApplier.ResetSeams();
            AvatarLookApplier.PieceBuilder = BuildPiece;
            AvatarLookApplier.HeritageGenderValid = (h, g) => h == 3 || h == 1;
            AvatarLookApplier.DefaultWeenie = () => { defaultWeenieLookups++; return DefaultWeenie(); };
            AvatarLookApplier.FaceGenerator = _ => faceRolls++;
            AvatarLookApplier.ScaleFactor = () => 2.0;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
            AvatarLookApplier.ResetSeams();
        }

        private static WorldObject BuildPiece(uint wcid)
        {
            if (wcid == Missing)
                return null;

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Damage, 30 }, { PropertyInt.ArmorLevel, 200 }, { PropertyInt.ImbuedEffect, 1 },
                    { PropertyInt.WieldRequirements, 2 }, { PropertyInt.WieldSkillType, 6 }, { PropertyInt.WieldDifficulty, 400 },
                    { PropertyInt.ItemMaxMana, 500 }, { PropertyInt.GearDamage, 5 }, { PropertyInt.GearCritResist, 7 }, { PropertyInt.GearPKDamageResistRating, 3 }, { PropertyInt.ValidLocations, wcid == Sword ? (int)EquipMask.MeleeWeapon : (int)Chest },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 }, { PropertyFloat.ArmorModVsSlash, 1.2 } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.ProcSpell, 99 }, { PropertyDataId.ClothingBase, 0x10000001 } },
                PropertiesSpellBook = new Dictionary<int, float> { { 1, 2.0f } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Piece " + wcid } },
            };
            // IsDynamicThatShouldPersistToShard returns false for any static guid, so a persistence test needs a dynamic one
            var guid = dynamicPieceGuids ? 0x80000000u + (++nextDynamicGuid) : ++nextGuid;
            var wo = new GenericObject(weenie, new ObjectGuid(guid));
            built.Add(wo);
            return wo;
        }

        private static bool dynamicPieceGuids;
        private static uint nextDynamicGuid = 0x00C95000;

        private static double? defaultWeenieScale;
        private static bool defaultWithSword;
        private static int defaultWeenieLookups;

        private static Weenie DefaultWeenie() => new Weenie
        {
            PropertiesFloat = defaultWeenieScale.HasValue ? new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, defaultWeenieScale.Value } } : null,
            WeenieClassId = AvatarVendor.AvatarWcid,
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Gender, 1 }, { PropertyInt.HeritageGroup, 3 } },
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Sex, "Male" }, { PropertyString.HeritageGroup, "Sho" } },
            PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001 } },
            PropertiesCreateList = new List<PropertiesCreateList>
            {
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Jerkin, Palette = 9, Shade = 0 },
                new PropertiesCreateList { DestinationType = DestinationType.Shop, WeenieClassId = 8888 },
            }.Concat(defaultWithSword ? new[] { new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Sword } } : new PropertiesCreateList[0]).ToList(),
        };

        private static AvatarVendor MakeVendor()
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.AvatarWcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceAvatarVendor, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 0 }, { PropertyInt.MerchandiseMinValue, 0 }, { PropertyInt.MerchandiseMaxValue, 0 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.BuyPrice, 0 }, { PropertyFloat.SellPrice, 1 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Avatar" } },
            };
            return (AvatarVendor)WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(++nextGuid));
        }

        private static AvatarLookPiece Piece(uint wcid, EquipMask slot, Action<AvatarLookPiece> _ = null) =>
            new AvatarLookPiece(wcid, slot,
                new Dictionary<PropertyInt, int> { { PropertyInt.PaletteTemplate, 85 } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.Shade, 0.5 } },
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, 0x10000ABC } },
                new Dictionary<PropertyBool, bool>(),
                new List<AvatarLookObjDescRow>
                {
                    new AvatarLookObjDescRow(AvatarLookRowKind.Texture, 9, 0x05002C23, 0x05002D52),
                    new AvatarLookObjDescRow(AvatarLookRowKind.Palette, 0x04001234, 8, 16),
                },
                "Piece " + wcid,
                new Dictionary<PropertyString, string>());

        private static AvatarLookBody Body(MotionStance? stance = MotionStance.NonCombat, uint? defHair = null, uint? hair = null, MotionCommand? pose = null, double? scale = 1.1) =>
            new AvatarLookBody(
                new Dictionary<PropertyInt, int> { { PropertyInt.Gender, 2 }, { PropertyInt.HeritageGroup, 1 } },
                scale.HasValue ? new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale.Value } } : new Dictionary<PropertyFloat, double>(),
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000002 }, { PropertyDataId.HeadObject, 0x01000A00 } },
                new Dictionary<PropertyString, string> { { PropertyString.Sex, "Female" }, { PropertyString.HeritageGroup, "Aluvian" } },
                defHair, hair, stance, pose);

        private static AvatarLook Look(AvatarLookBody body, params AvatarLookPiece[] pieces) =>
            new AvatarLook(AvatarLookJson.CurrentVersion, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), body, pieces);

        [TestMethod]
        public void Apply_sets_body_and_equips_pieces()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Jerkin, Chest), Piece(Breeches, Legs)));

            Assert.AreEqual(2, npc.Gender);
            Assert.AreEqual(1, npc.Heritage);
            Assert.AreEqual(0, faceRolls, "a look carries its own face");
            Assert.AreEqual("Female", npc.GetProperty(PropertyString.Sex));
            Assert.AreEqual(0x02000002u, npc.GetProperty(PropertyDataId.Setup));
            Assert.AreEqual(2, npc.EquippedObjects.Count);
            Assert.IsTrue(npc.EquippedObjects.Values.Any(i => i.WeenieClassId == Jerkin && i.CurrentWieldedLocation == Chest));
            Assert.IsTrue(npc.EquippedObjects.Values.Any(i => i.WeenieClassId == Breeches && i.CurrentWieldedLocation == Legs));

            var jerkin = npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Jerkin);
            Assert.AreEqual(85, jerkin.GetProperty(PropertyInt.PaletteTemplate));
            Assert.AreEqual(0.5, jerkin.GetProperty(PropertyFloat.Shade));
            Assert.AreEqual(0x10000ABCu, jerkin.GetProperty(PropertyDataId.ClothingBase));
            Assert.AreEqual(1, jerkin.Biota.PropertiesTextureMap.Count);
            Assert.AreEqual(1, jerkin.Biota.PropertiesPalette.Count);
        }

        [TestMethod]
        public void Apply_null_restores_default()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Breeches, Legs), Piece(Sword, EquipMask.MeleeWeapon)));

            AvatarLookApplier.Apply(npc, null);

            Assert.AreEqual(1, npc.Gender);
            Assert.AreEqual(3, npc.Heritage);
            Assert.AreEqual("Sho", npc.GetProperty(PropertyString.HeritageGroup));
            Assert.AreEqual(0x02000001u, npc.GetProperty(PropertyDataId.Setup));
            Assert.IsNull(npc.GetProperty(PropertyDataId.HeadObject));
            Assert.AreEqual(2.0, npc.GetProperty(PropertyFloat.DefaultScale), "the default weenie has no scale (1.0) times the factor");
            // only the default weenie's Wield create_list row comes back; the Shop row does not
            Assert.AreEqual(1, npc.EquippedObjects.Count);
            var item = npc.EquippedObjects.Values.Single();
            Assert.AreEqual(Jerkin, item.WeenieClassId);
            Assert.AreEqual(9, item.GetProperty(PropertyInt.PaletteTemplate));
            Assert.AreEqual(MotionStance.NonCombat, npc.CurrentMotionState.Stance);
            Assert.AreEqual(1, faceRolls, "the default body gets a fresh face, as the constructor gave it");
        }

        [TestMethod]
        public void Apply_null_body_is_the_reset_look()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Breeches, Legs)));

            AvatarLookApplier.Apply(npc, Look(null));

            Assert.AreEqual(1, npc.Gender);
            Assert.AreEqual(Jerkin, npc.EquippedObjects.Values.Single().WeenieClassId);
        }

        [TestMethod]
        public void Apply_skips_missing_wcid_and_equips_rest()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Missing, Chest), Piece(Breeches, Legs)));

            Assert.AreEqual(1, npc.EquippedObjects.Count);
            Assert.AreEqual(Breeches, npc.EquippedObjects.Values.Single().WeenieClassId);
        }

        [TestMethod]
        public void Apply_skips_clashing_piece()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Jerkin, Chest), Piece(Breeches, Chest), Piece(Sword, EquipMask.MeleeWeapon)));

            Assert.AreEqual(2, npc.EquippedObjects.Count);
            Assert.AreEqual(Jerkin, npc.EquippedObjects.Values.Single(i => i.CurrentWieldedLocation == Chest).WeenieClassId);
            var loser = built.Single(b => b.WeenieClassId == Breeches);
            Assert.IsTrue(loser.IsDestroyed, "the piece that would not equip is destroyed");
        }

        [TestMethod]
        public void Apply_invalid_heritage_keeps_default_body()
        {
            var npc = MakeVendor();
            AvatarLookApplier.HeritageGenderValid = (h, g) => false;

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Breeches, Legs)));

            Assert.AreEqual(1, npc.Gender, "the default body, not the look's");
            Assert.AreEqual(3, npc.Heritage);
            Assert.AreEqual(0x02000001u, npc.GetProperty(PropertyDataId.Setup));
            Assert.AreEqual(2.0, npc.GetProperty(PropertyFloat.DefaultScale));
            Assert.IsTrue(npc.EquippedObjects.Values.Any(i => i.WeenieClassId == Breeches), "the pieces still go on");
            Assert.AreEqual(1, faceRolls);
        }

        [TestMethod]
        public void Pieces_are_cosmetic_and_statless()
        {
            var npc = MakeVendor();
            dynamicPieceGuids = true;

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Sword, EquipMask.MeleeWeapon)));

            var w = npc.EquippedObjects.Values.Single();
            Assert.IsTrue(w.Guid.IsDynamic(), "precondition: the persistence check below is meaningless on a static guid");
            Assert.AreEqual(true, w.GetProperty(PropertyBool.AvatarCosmetic));
            Assert.IsNull(w.GetProperty(PropertyInt.Damage));
            Assert.IsNull(w.GetProperty(PropertyInt.ArmorLevel));
            Assert.IsNull(w.GetProperty(PropertyInt.ImbuedEffect));
            Assert.IsNull(w.GetProperty(PropertyInt.WieldRequirements));
            Assert.IsNull(w.GetProperty(PropertyInt.WieldSkillType));
            Assert.IsNull(w.GetProperty(PropertyInt.WieldDifficulty));
            Assert.IsNull(w.GetProperty(PropertyInt.ItemMaxMana));
            Assert.IsNull(w.GetProperty(PropertyInt.GearDamage));
            Assert.IsNull(w.GetProperty(PropertyInt.GearCritResist));
            Assert.IsNull(w.GetProperty(PropertyInt.GearPKDamageResistRating));
            Assert.IsNull(w.GetProperty(PropertyFloat.DamageVariance));
            Assert.IsNull(w.GetProperty(PropertyFloat.ArmorModVsSlash));
            Assert.IsNull(w.GetProperty(PropertyDataId.ProcSpell));
            Assert.AreEqual(0, w.Biota.PropertiesSpellBook?.Count ?? 0);
            Assert.AreEqual(false, w.GetProperty(PropertyBool.AllowGive), "a dressed piece cannot be given");
            Assert.IsFalse(w.IsDynamicThatShouldPersistToShard(), "a dressed piece is never saved to the shard");
        }

        [TestMethod]
        public void Redress_destroys_old_pieces()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Jerkin, Chest), Piece(Breeches, Legs)));
            var first = npc.EquippedObjects.Values.ToList();

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Sword, EquipMask.MeleeWeapon)));

            Assert.AreEqual(2, first.Count);
            Assert.IsTrue(first.All(i => i.IsDestroyed));
            Assert.AreEqual(1, npc.EquippedObjects.Count);
            Assert.AreEqual(Sword, npc.EquippedObjects.Values.Single().WeenieClassId);
            Assert.IsFalse(npc.EquippedObjects.Values.Single().IsDestroyed);
        }

        [TestMethod]
        public void Apply_sets_stored_stance()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.SwordCombat), Piece(Sword, EquipMask.MeleeWeapon)));

            Assert.AreEqual(MotionStance.SwordCombat, npc.CurrentMotionState.Stance);
            Assert.AreEqual(MotionCommand.Ready, npc.CurrentMotionState.MotionState.ForwardCommand);
        }

        [TestMethod]
        public void Apply_stance_falls_back_when_weapon_skipped()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.SwordCombat), Piece(Missing, EquipMask.MeleeWeapon), Piece(Jerkin, Chest)));

            Assert.AreEqual(MotionStance.NonCombat, npc.CurrentMotionState.Stance);
        }

        [TestMethod]
        public void Apply_magic_stance_needs_a_held_caster()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.Magic), Piece(Wand, EquipMask.Held)));
            Assert.AreEqual(MotionStance.Magic, npc.CurrentMotionState.Stance);

            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.Magic), Piece(Jerkin, Chest)));
            Assert.AreEqual(MotionStance.NonCombat, npc.CurrentMotionState.Stance);
        }

        [TestMethod]
        public void Apply_sets_stored_pose()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.NonCombat, pose: MotionCommand.ATOYOT), Piece(Jerkin, Chest)));

            Assert.AreEqual(MotionCommand.ATOYOT, npc.CurrentMotionState.MotionState.ForwardCommand);
            Assert.AreEqual(MotionStance.NonCombat, npc.CurrentMotionState.Stance);

            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.NonCombat), Piece(Jerkin, Chest)));
            Assert.AreEqual(MotionCommand.Ready, npc.CurrentMotionState.MotionState.ForwardCommand, "no pose means Ready");
        }

        [TestMethod]
        public void Apply_keeps_pose_when_stance_falls_back()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(MotionStance.SwordCombat, pose: MotionCommand.ATOYOT), Piece(Jerkin, Chest)));

            Assert.AreEqual(MotionStance.NonCombat, npc.CurrentMotionState.Stance);
            Assert.AreEqual(MotionCommand.ATOYOT, npc.CurrentMotionState.MotionState.ForwardCommand);
        }

        [TestMethod]
        public void Apply_scales_body_by_factor()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.0)));
            Assert.AreEqual(2.0, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-6);

            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.2)));
            Assert.AreEqual(2.4, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-5, "re-applying does not compound");

            AvatarLookApplier.ScaleFactor = () => 1.0;
            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.2)));
            Assert.AreEqual(1.2, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-5);

            AvatarLookApplier.ScaleFactor = () => 0.0;
            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.2)));
            Assert.AreEqual(1.2, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-5, "a non-positive factor means 1.0");
        }

        [TestMethod]
        public void Apply_scales_wielded_pieces_by_factor()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.5), Piece(Jerkin, Chest), Piece(Sword, EquipMask.MeleeWeapon)));

            var sword = npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Sword);
            var jerkin = npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Jerkin);
            Assert.AreEqual(2.0f, sword.ObjScale, "the factor, not the body scale (1.5 * 2 = 3)");
            Assert.AreEqual(3.0f, npc.ObjScale);
            Assert.IsNull(jerkin.ObjScale, "clothing is unchanged");
        }

        [TestMethod]
        public void Apply_scales_wielded_default_pieces_by_factor()
        {
            var npc = MakeVendor();
            defaultWithSword = true;

            AvatarLookApplier.Apply(npc, null);

            Assert.AreEqual(2.0f, npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Sword).ObjScale);
            Assert.IsNull(npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Jerkin).ObjScale);
        }

        [TestMethod]
        public void Apply_scale_falls_back_to_the_default_weenie_then_one()
        {
            var npc = MakeVendor();
            defaultWeenieScale = 1.5;
            AvatarLookApplier.Apply(npc, Look(Body(scale: null)));
            Assert.AreEqual(3.0f, npc.ObjScale, "the weenie's 1.5 times the factor");

            defaultWeenieScale = null;
            AvatarLookApplier.Apply(npc, Look(Body(scale: null)));
            Assert.AreEqual(2.0f, npc.ObjScale, "no scale anywhere means 1.0 times the factor");
        }

        [TestMethod]
        public void Default_weenie_is_looked_up_only_when_needed()
        {
            var npc = MakeVendor();

            AvatarLookApplier.Apply(npc, Look(Body(), Piece(Jerkin, Chest)));
            Assert.AreEqual(0, defaultWeenieLookups, "a valid look with its own scale never reads the default weenie");

            AvatarLookApplier.Apply(npc, null);
            Assert.AreEqual(1, defaultWeenieLookups, "and the null look reads it once");
        }

        [TestMethod]
        public void Default_scale_factor_reads_the_tunable()
        {
            const string key = "allegiance_avatar_scale";
            var original = DefaultPropertyManager.DefaultDoubleProperties[key].Item;
            AvatarLookApplier.ResetSeams();
            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(key, 3.0, init: true));
                Assert.AreEqual(3.0, AvatarLookApplier.ScaleFactor());
            }
            finally
            {
                PropertyManager.ModifyDouble(key, original, init: true);
            }
        }

        [TestMethod]
        public void Apply_does_not_change_the_look()
        {
            var npc = MakeVendor();
            var look = Look(Body(scale: 1.2), Piece(Jerkin, Chest));

            AvatarLookApplier.Apply(npc, look);

            Assert.AreEqual(1.2, look.Body.Floats[PropertyFloat.DefaultScale], "the stored body keeps the real scale");
        }

        [TestMethod]
        public void Apply_default_look_is_scaled()
        {
            var npc = MakeVendor();
            defaultWeenieScale = 1.5;

            AvatarLookApplier.Apply(npc, null);

            Assert.AreEqual(3.0, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-6);
        }

        [TestMethod]
        public void Apply_null_stance_is_non_combat()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(stance: null), Piece(Jerkin, Chest)));
            Assert.AreEqual(MotionStance.NonCombat, npc.CurrentMotionState.Stance);
        }

        [TestMethod]
        public void Hair_override_reaches_objdesc()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(defHair: 0x05001111, hair: 0x05002222)));

            npc.RemoveProperty(PropertyDataId.Setup); // the naked-parts loop reads the portal dat, which a unit test has not loaded
            var objDesc = npc.CalculateObjDesc();

            var hair = objDesc.TextureChanges.Where(t => t.PartIndex == 0x10).ToList();
            Assert.AreEqual(1, hair.Count);
            Assert.AreEqual(0x05001111u, hair[0].OldTexture);
            Assert.AreEqual(0x05002222u, hair[0].NewTexture);
        }

        [TestMethod]
        public void Hair_override_is_cleared_by_a_look_without_one()
        {
            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(defHair: 0x05001111, hair: 0x05002222)));
            Assert.IsNotNull(npc.HairTextureOverride);

            AvatarLookApplier.Apply(npc, Look(Body()));
            Assert.IsNull(npc.HairTextureOverride);

            AvatarLookApplier.Apply(npc, Look(Body(defHair: 0x05001111, hair: 0x05002222)));
            AvatarLookApplier.Apply(npc, null);
            Assert.IsNull(npc.HairTextureOverride);
        }

        [TestMethod]
        public void Reduced_coat_and_its_sleeves_both_equip()
        {
            // A coat whose weenie covers chest and arms, captured after armor reduction as chest-only, plus a sleeves piece:
            // the captured ClothingPriority must be on the coat BEFORE it is equipped, or the sleeves clash with it and are skipped.
            const uint Coat = 9101, Sleeves = 9102;
            AvatarLookApplier.PieceBuilder = wcid =>
            {
                var priority = wcid == Coat
                    ? CoverageMask.OuterwearChest | CoverageMask.OuterwearUpperArms
                    : CoverageMask.OuterwearUpperArms;
                var weenie = new Weenie
                {
                    WeenieClassId = wcid,
                    WeenieType = WeenieType.Clothing,
                    PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ClothingPriority, (int)priority } },
                    PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, 0x10000001 } },
                    PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Piece " + wcid } },
                };
                var wo = new Clothing(weenie, new ObjectGuid(++nextGuid));
                built.Add(wo);
                return wo;
            };

            AvatarLookPiece WithPriority(uint wcid, EquipMask slot, CoverageMask priority) =>
                Piece(wcid, slot) with { Ints = new Dictionary<PropertyInt, int> { { PropertyInt.ClothingPriority, (int)priority } } };

            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, Look(Body(),
                WithPriority(Coat, EquipMask.ChestArmor, CoverageMask.OuterwearChest),
                WithPriority(Sleeves, EquipMask.UpperArmArmor, CoverageMask.OuterwearUpperArms)));

            CollectionAssert.AreEquivalent(new[] { Coat, Sleeves }, npc.EquippedObjects.Values.Select(i => i.WeenieClassId).ToList());
            Assert.AreEqual(CoverageMask.OuterwearChest, npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Coat).ClothingPriority);
        }

        [TestMethod]
        public void Apply_clamps_the_scale_factor()
        {
            var npc = MakeVendor();

            AvatarLookApplier.ScaleFactor = () => 50.0;
            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.0), Piece(Sword, EquipMask.MeleeWeapon)));
            Assert.AreEqual(5.0, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-6, "above 5 becomes 5");
            Assert.AreEqual(5.0f, npc.EquippedObjects.Values.Single().ObjScale, "the held item takes the clamped factor too");

            AvatarLookApplier.ScaleFactor = () => 5.0;
            AvatarLookApplier.Apply(npc, Look(Body(scale: 1.0)));
            Assert.AreEqual(5.0, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-6, "5 itself is allowed");

            foreach (var bad in new[] { 0.0, -3.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                AvatarLookApplier.ScaleFactor = () => bad;
                AvatarLookApplier.Apply(npc, Look(Body(scale: 1.2)));
                Assert.AreEqual(1.2, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-5, $"factor {bad} keeps the 1.0 fallback");
            }
        }

        [TestMethod]
        public void Cosmetic_flag_alone_keeps_a_dynamic_item_out_of_the_shard()
        {
            dynamicPieceGuids = true;
            var control = BuildPiece(Jerkin);
            Assert.IsTrue(control.Guid.IsDynamic());
            Assert.IsTrue(control.IsDynamicThatShouldPersistToShard(), "positive control: the same item without the flag persists");

            var item = BuildPiece(Jerkin);
            item.SetProperty(PropertyBool.AvatarCosmetic, true);

            Assert.IsFalse(item.IsDynamicThatShouldPersistToShard());
        }

        [TestMethod]
        public void Golden_officer_to_avatar_round_trip()
        {
            var officer = SalvageRollerVendorTests.MakePlayer(0x50000777);
            officer.SetProperty(PropertyInt.HeritageGroup, 1);
            officer.SetProperty(PropertyInt.Gender, 2);
            officer.SetProperty(PropertyInt.Hairstyle, 3);
            officer.SetProperty(PropertyFloat.DefaultScale, 1.1);
            officer.SetProperty(PropertyDataId.Setup, 0x02000002u);
            officer.SetProperty(PropertyDataId.HeadObject, 0x01000A00u);
            officer.SetProperty(PropertyDataId.HairPalette, 0x04000B00u);
            officer.SetProperty(PropertyString.Sex, "Female");
            officer.SetProperty(PropertyString.HeritageGroup, "Aluvian");
            officer.CurrentMotionState = new Motion(MotionStance.SwordCombat, MotionCommand.ATOYOT);

            // a tailored coat: new model, palette template, shade, a texture swap and a palette range
            var coat = BuildPiece(Jerkin);
            coat.SetProperty(PropertyDataId.ClothingBase, 0x10000ABCu);
            coat.SetProperty(PropertyInt.PaletteTemplate, 85);
            coat.SetProperty(PropertyFloat.Shade, 0.5);
            coat.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { new PropertiesTextureMap { PartIndex = 9, OldTexture = 0x05002C23, NewTexture = 0x05002D52 } };
            coat.Biota.PropertiesPalette = new List<PropertiesPalette> { new PropertiesPalette { SubPaletteId = 0x04001234, Offset = 8, Length = 16 } };
            coat.CurrentWieldedLocation = Chest;
            officer.EquippedObjects[coat.Guid] = coat;

            // a sword with a NON-tier aura, which the capture must keep
            var sword = BuildPiece(Sword);
            sword.SetProperty(PropertyDataId.VisualEffectScript, 0x33001999u);
            sword.CurrentWieldedLocation = EquipMask.MeleeWeapon;
            officer.EquippedObjects[sword.Guid] = sword;

            var look = AvatarLookCapture.FromPlayer(officer, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc));
            var json = AvatarLookJson.Serialize(look);

            Assert.AreEqual(GoldenJson, json);

            var npc = MakeVendor();
            AvatarLookApplier.Apply(npc, AvatarLookJson.Parse(json));

            // body, field by field
            Assert.AreEqual(1, npc.GetProperty(PropertyInt.HeritageGroup));
            Assert.AreEqual(2, npc.GetProperty(PropertyInt.Gender));
            Assert.AreEqual(3, npc.GetProperty(PropertyInt.Hairstyle));
            Assert.AreEqual(2.2, npc.GetProperty(PropertyFloat.DefaultScale).Value, 1e-5, "1.1 body times the 2.0 factor");
            Assert.AreEqual(2.2f, npc.ObjScale);
            Assert.AreEqual(0x02000002u, npc.GetProperty(PropertyDataId.Setup));
            Assert.AreEqual(0x01000A00u, npc.GetProperty(PropertyDataId.HeadObject));
            Assert.AreEqual(0x04000B00u, npc.GetProperty(PropertyDataId.HairPalette));
            Assert.AreEqual("Female", npc.GetProperty(PropertyString.Sex));
            Assert.AreEqual("Aluvian", npc.GetProperty(PropertyString.HeritageGroup));

            // pieces
            Assert.AreEqual(2, npc.EquippedObjects.Count);
            var npcCoat = npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Jerkin);
            Assert.AreEqual(Chest, npcCoat.CurrentWieldedLocation);
            Assert.AreEqual(85, npcCoat.GetProperty(PropertyInt.PaletteTemplate));
            Assert.AreEqual(0.5, npcCoat.GetProperty(PropertyFloat.Shade));
            Assert.AreEqual(0x10000ABCu, npcCoat.GetProperty(PropertyDataId.ClothingBase));
            var tex = npcCoat.Biota.PropertiesTextureMap.Single();
            Assert.AreEqual((9, 0x05002C23u, 0x05002D52u), ((int)tex.PartIndex, tex.OldTexture, tex.NewTexture));
            var pal = npcCoat.Biota.PropertiesPalette.Single();
            Assert.AreEqual((0x04001234u, 8, 16), (pal.SubPaletteId, (int)pal.Offset, (int)pal.Length));
            Assert.AreEqual(true, npcCoat.GetProperty(PropertyBool.AvatarCosmetic));
            Assert.IsNull(npcCoat.GetProperty(PropertyInt.ArmorLevel));
            Assert.IsNull(npcCoat.ObjScale, "clothing is unchanged");

            var npcSword = npc.EquippedObjects.Values.Single(i => i.WeenieClassId == Sword);
            Assert.AreEqual(EquipMask.MeleeWeapon, npcSword.CurrentWieldedLocation);
            Assert.AreEqual(0x33001999u, npcSword.GetProperty(PropertyDataId.VisualEffectScript), "the non-tier aura reaches the equipped weapon");
            Assert.AreEqual(2.0f, npcSword.ObjScale, "the weapon takes the 2x factor, not the body scale");
            Assert.IsNull(npcSword.GetProperty(PropertyInt.Damage));

            // stance and pose
            Assert.AreEqual(MotionStance.SwordCombat, npc.CurrentMotionState.Stance);
            Assert.AreEqual(MotionCommand.ATOYOT, npc.CurrentMotionState.MotionState.ForwardCommand);
        }

        // Reviewed by hand against the officer built in the test: Gender 113, HeritageGroup 188, Hairstyle 9012; DataIds 1 Setup,
        // 15 HairPalette, 18 HeadObject; Stance 0x8000003E, Pose 0x420000F9 (ATOYOT); the coat (wcid 124, ChestArmor 512) carries the
        // tailored ClothingBase 0x10000ABC, palette template 85, shade 0.5 and one texture and one palette row; the sword (wcid 5555,
        // MeleeWeapon 1048576) keeps the non-tier aura 0x33001999 under DataIds 9000.
        private const string GoldenJson = @"{""Version"":1,""DressedAtUtc"":""2026-11-21T00:00:00Z"",""Body"":{""Ints"":{""113"":2,""188"":1,""9012"":3},""Floats"":{""39"":1.1},""DataIds"":{""1"":33554434,""15"":67111680,""18"":16779776},""Strings"":{""3"":""Female"",""4"":""Aluvian""},""DefaultHairTexture"":null,""HairTexture"":null,""Stance"":2147483710,""Pose"":1107296505},""Pieces"":[{""Wcid"":124,""Slot"":512,""Ints"":{""3"":85},""Floats"":{""12"":0.5},""DataIds"":{""7"":268438204},""Bools"":{},""ObjDescRows"":[{""Kind"":0,""A"":9,""B"":83897379,""C"":83897682},{""Kind"":1,""A"":67113524,""B"":8,""C"":16}],""Name"":""Piece 124"",""Strings"":{}},{""Wcid"":5555,""Slot"":1048576,""Ints"":{},""Floats"":{},""DataIds"":{""7"":268435457,""9000"":855644569},""Bools"":{},""ObjDescRows"":[],""Name"":""Piece 5555"",""Strings"":{}}]}";

        private const string NetworkingFile = "Source/ACE.Server/WorldObjects/WorldObject_Networking.cs";

        [TestMethod]
        public void Player_hair_branch_unchanged()
        {
            var text = PooledLootSourceText.Read(NetworkingFile);

            // the two Player lines, byte for byte, in order and adjacent; the Avatar branch follows as an else-if
            const string playerIf = "            if (this is Player player)\n";
            const string playerAdd = "                objDesc.TextureChanges.Add(new PropertiesTextureMap { PartIndex = 0x10, OldTexture = player.Character.DefaultHairTexture, NewTexture = player.Character.HairTexture });\n";
            var at = text.IndexOf(playerIf + playerAdd, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, "the Player hair branch changed");
            Assert.AreEqual(at, text.LastIndexOf(playerIf + playerAdd, StringComparison.Ordinal), "exactly one Player hair branch");

            var after = text.Substring(at + (playerIf + playerAdd).Length);
            Assert.IsTrue(after.StartsWith("            else if (this is ACE.Server.Avatars.IAvatarFigure", StringComparison.Ordinal), "the Avatar hair branch must follow the Player branch as an else-if");
        }
    }
}
