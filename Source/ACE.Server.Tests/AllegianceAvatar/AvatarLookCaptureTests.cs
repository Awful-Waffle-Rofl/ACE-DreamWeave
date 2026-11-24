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
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>Capturing a look off bare Creatures and WorldObjects. No Player, no world, no database, no PropertyManager reads.</summary>
    [TestClass]
    public class AvatarLookCaptureTests
    {
        private const uint Elite = 0x330011BE;

        private static uint nextGuid = 0x7EC90000;

        [ClassInitialize]
        public static void InitTables(TestContext _) => TestGameTables.EnsureInitialized();

        private static WorldObject MakeItem(Action<Dictionary<PropertyInt, int>> ints = null, Action<Dictionary<PropertyDataId, uint>> dids = null, string name = "Test Piece")
        {
            var i = new Dictionary<PropertyInt, int>();
            var d = new Dictionary<PropertyDataId, uint>();
            ints?.Invoke(i);
            dids?.Invoke(d);

            var weenie = new Weenie
            {
                WeenieClassId = 4242,
                WeenieType = WeenieType.Generic,
                PropertiesInt = i,
                PropertiesDID = d,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };
            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        [TestMethod]
        public void Capture_piece_copies_tailoring_look_fields()
        {
            var item = MakeItem(
                i => i[PropertyInt.PaletteTemplate] = 85,
                d => { d[PropertyDataId.ClothingBase] = 0x10000AAAu; d[PropertyDataId.Setup] = 0x02000BBBu; });
            item.Shade = 0.8;
            item.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { new PropertiesTextureMap { PartIndex = 9, OldTexture = 0x05002C23, NewTexture = 0x05002D52 } };

            var piece = AvatarLookCapture.CapturePiece(item);

            Assert.AreEqual(85, piece.Ints[PropertyInt.PaletteTemplate]);
            Assert.AreEqual(0.8, piece.Floats[PropertyFloat.Shade]);
            Assert.AreEqual(0x10000AAAu, piece.DataIds[PropertyDataId.ClothingBase]);
            Assert.AreEqual(0x02000BBBu, piece.DataIds[PropertyDataId.Setup]);
            Assert.AreEqual(1, piece.ObjDescRows.Count);
            Assert.AreEqual(new AvatarLookObjDescRow(AvatarLookRowKind.Texture, 9, 0x05002C23, 0x05002D52), piece.ObjDescRows[0]);
            Assert.AreEqual("Test Piece", piece.Name);
            Assert.AreEqual(4242u, piece.Wcid);
            // only what is present is captured: no invented keys
            Assert.IsFalse(piece.Floats.ContainsKey(PropertyFloat.Shade2));
            Assert.IsFalse(piece.DataIds.ContainsKey(PropertyDataId.VisualEffectScript));
        }

        [TestMethod]
        public void Capture_piece_copies_worn_model_strings_and_palette_rows()
        {
            var item = MakeItem();
            item.SetProperty(PropertyString.WornModelOverrides, "ovr");
            item.SetProperty(PropertyString.ClothingPartExclusions, "excl");
            item.Biota.PropertiesPalette = new List<PropertiesPalette> { new PropertiesPalette { SubPaletteId = 0x04001234, Offset = 8, Length = 16 } };

            var piece = AvatarLookCapture.CapturePiece(item);

            Assert.AreEqual("ovr", piece.Strings[PropertyString.WornModelOverrides]);
            Assert.AreEqual("excl", piece.Strings[PropertyString.ClothingPartExclusions]);
            Assert.AreEqual(new AvatarLookObjDescRow(AvatarLookRowKind.Palette, 0x04001234, 8, 16), piece.ObjDescRows.Single());
        }

        [TestMethod]
        public void Capture_piece_drops_weapon_quality_aura()
        {
            var elite = MakeItem(dids: d => d[PropertyDataId.VisualEffectScript] = Elite);
            Assert.IsNull(Tailoring.IntermediateScript(Elite));
            Assert.IsFalse(AvatarLookCapture.CapturePiece(elite).DataIds.ContainsKey(PropertyDataId.VisualEffectScript));

            const uint look = 0x33001999;
            var kept = MakeItem(dids: d => d[PropertyDataId.VisualEffectScript] = look);
            Assert.AreEqual(0x33001999u, AvatarLookCapture.CapturePiece(kept).DataIds[PropertyDataId.VisualEffectScript]);
        }

        [TestMethod]
        public void IsLookPiece_rules()
        {
            var armor = MakeItem(i => i[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.HeadWear, d => d[PropertyDataId.ClothingBase] = 0x10000001u);
            var sword = MakeItem(i => i[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.MeleeWeapon);
            var ammo = MakeItem(i => i[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.MissileAmmo);
            var ring = MakeItem(i => i[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.FingerWearLeft);

            Assert.IsTrue(AvatarLookCapture.IsLookPiece(armor));
            Assert.IsTrue(AvatarLookCapture.IsLookPiece(sword));
            Assert.IsFalse(AvatarLookCapture.IsLookPiece(ammo));
            Assert.IsFalse(AvatarLookCapture.IsLookPiece(ring));
            // not equipped: even with a ClothingBase, a pack item does not show
            Assert.IsFalse(AvatarLookCapture.IsLookPiece(MakeItem(dids: d => d[PropertyDataId.ClothingBase] = 0x10000001u)));
        }

        private static Creature MakeBody(Action<Dictionary<PropertyInt, int>> ints = null, bool motionTable = false)
        {
            var i = new Dictionary<PropertyInt, int>();
            ints?.Invoke(i);
            var weenie = new Weenie
            {
                WeenieClassId = 4243,
                WeenieType = WeenieType.Creature,
                PropertiesInt = i,
                PropertiesDID = new Dictionary<PropertyDataId, uint>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Body" } },
            };
            if (motionTable)
                weenie.PropertiesDID[PropertyDataId.MotionTable] = 0x09000001u;
            return new Creature(weenie, new ObjectGuid(++nextGuid));
        }

        [TestMethod]
        public void Capture_gearknight_body_has_no_hairstyle()
        {
            var gk = MakeBody(i => i[PropertyInt.HeritageGroup] = (int)HeritageGroup.Gearknight);

            var body = AvatarLookCapture.CaptureBody(gk, null, null);

            Assert.AreEqual((int)HeritageGroup.Gearknight, body.Ints[PropertyInt.HeritageGroup]);
            Assert.IsFalse(body.Ints.ContainsKey(PropertyInt.Hairstyle));
        }

        [TestMethod]
        public void Capture_body_with_no_pieces_is_valid()
        {
            var body = AvatarLookCapture.CaptureBody(MakeBody(i => i[PropertyInt.Gender] = 1), 7u, 8u);
            var look = new AvatarLook(AvatarLookJson.CurrentVersion, DateTime.UtcNow, body, new List<AvatarLookPiece>());

            Assert.AreEqual(7u, look.Body.DefaultHairTexture);
            Assert.AreEqual(8u, look.Body.HairTexture);
            Assert.AreEqual(look, AvatarLookJson.Parse(AvatarLookJson.Serialize(look)));
        }

        [TestMethod]
        public void Capture_records_caller_stance()
        {
            var source = MakeBody(motionTable: true);
            source.CurrentMotionState = new Motion(MotionStance.SwordCombat);
            Assert.AreEqual(MotionStance.SwordCombat, AvatarLookCapture.CaptureBody(source, null, null).Stance);

            var none = MakeBody();
            none.CurrentMotionState = null;
            Assert.AreEqual(MotionStance.NonCombat, AvatarLookCapture.CaptureBody(none, null, null).Stance);

            // a motion table not yet driven reads Invalid, which also means NonCombat
            var fresh = MakeBody(motionTable: true);
            Assert.AreEqual(MotionStance.NonCombat, AvatarLookCapture.CaptureBody(fresh, null, null).Stance);
        }

        [TestMethod]
        public void Body_property_list_is_pinned()
        {
            CollectionAssert.AreEqual(new[] { PropertyInt.HeritageGroup, PropertyInt.Gender, PropertyInt.Hairstyle }, AvatarLookCapture.BodyInts);
            CollectionAssert.AreEqual(new[] { PropertyFloat.DefaultScale }, AvatarLookCapture.BodyFloats);
            CollectionAssert.AreEqual(new[] { PropertyString.Sex, PropertyString.HeritageGroup }, AvatarLookCapture.BodyStrings);
            CollectionAssert.AreEqual(new[]
            {
                PropertyDataId.HeadObject, PropertyDataId.HairPalette, PropertyDataId.SkinPalette,
                PropertyDataId.EyesPalette, PropertyDataId.EyesTexture, PropertyDataId.DefaultEyesTexture,
                PropertyDataId.NoseTexture, PropertyDataId.DefaultNoseTexture,
                PropertyDataId.MouthTexture, PropertyDataId.DefaultMouthTexture,
                PropertyDataId.PaletteBase, PropertyDataId.Setup, PropertyDataId.MotionTable, PropertyDataId.SoundTable,
            }, AvatarLookCapture.BodyDataIds);
            Assert.AreEqual(188, (int)PropertyInt.HeritageGroup);
            Assert.AreEqual(113, (int)PropertyInt.Gender);
        }

        [TestMethod]
        public void Capture_records_held_pose()
        {
            var source = MakeBody(motionTable: true);

            source.CurrentMotionState = new Motion(MotionStance.NonCombat, MotionCommand.ATOYOT);
            Assert.AreEqual(MotionCommand.ATOYOT, AvatarLookCapture.CaptureBody(source, null, null).Pose);

            source.CurrentMotionState = new Motion(MotionStance.NonCombat, MotionCommand.Ready);
            Assert.IsNull(AvatarLookCapture.CaptureBody(source, null, null).Pose);

            // a one-shot emote carries no SubState bit
            Assert.AreEqual(0u, (uint)MotionCommand.Cheer & (uint)CommandMask.SubState);
            source.CurrentMotionState = new Motion(MotionStance.NonCombat, MotionCommand.Cheer);
            Assert.IsNull(AvatarLookCapture.CaptureBody(source, null, null).Pose);

            var none = MakeBody();
            none.CurrentMotionState = null;
            Assert.IsNull(AvatarLookCapture.CaptureBody(none, null, null).Pose);
        }

        [TestMethod]
        public void Capture_pose_keeps_only_pose_commands()
        {
            var source = MakeBody(motionTable: true);

            // movement carries the SubState bit too (0x45 / 0x44), and the Aim motions are class 0x40: none is a pose
            foreach (var movement in new[] { MotionCommand.WalkForward, MotionCommand.WalkBackwards, MotionCommand.RunForward, MotionCommand.AimLevel })
            {
                source.CurrentMotionState = new Motion(MotionStance.NonCombat, movement);
                Assert.IsNull(AvatarLookCapture.CaptureBody(source, null, null).Pose, $"{movement} is not a pose");
            }

            foreach (var pose in new[] { MotionCommand.ATOYOT, MotionCommand.KneelState, MotionCommand.Sitting, MotionCommand.Crouch, MotionCommand.Sleeping })
            {
                source.CurrentMotionState = new Motion(MotionStance.NonCombat, pose);
                Assert.AreEqual(pose, AvatarLookCapture.CaptureBody(source, null, null).Pose, $"{pose} is a pose");
            }
        }

        [TestMethod]
        public void Capture_piece_copies_coverage_priority_and_top_layer()
        {
            var item = MakeItem(i => i[PropertyInt.ClothingPriority] = (int)CoverageMask.OuterwearChest);
            item.SetProperty(PropertyBool.TopLayerPriority, true);

            var piece = AvatarLookCapture.CapturePiece(item);

            Assert.AreEqual((int)CoverageMask.OuterwearChest, piece.Ints[PropertyInt.ClothingPriority], "armor reduction narrows this");
            Assert.AreEqual(true, piece.Bools[PropertyBool.TopLayerPriority], "armor layering sets this");

            var plain = AvatarLookCapture.CapturePiece(MakeItem());
            Assert.IsFalse(plain.Bools.ContainsKey(PropertyBool.TopLayerPriority));
            Assert.IsFalse(plain.Ints.ContainsKey(PropertyInt.ClothingPriority));
        }

        [TestMethod]
        public void Capture_keeps_unscaled_body_scale()
        {
            var source = MakeBody();
            source.ObjScale = 1.2f;

            var body = AvatarLookCapture.CaptureBody(source, null, null);

            Assert.AreEqual(1.2, body.Floats[PropertyFloat.DefaultScale], 1e-6, "the officer's real scale; the Avatar multiplier is applied at apply time only");
        }

        [TestMethod]
        public void SameFigure_ignores_stance_and_pose()
        {
            var a = AvatarLookTests.FullLook();
            var b = AvatarLookTests.FullLook();
            b = b with { Body = b.Body with { Stance = MotionStance.NonCombat, Pose = null } };
            Assert.AreNotEqual(a.Body, b.Body);
            Assert.IsTrue(AvatarLookCapture.SameFigure(a, b));

            var c = b with { Body = b.Body with { Pose = MotionCommand.Cheer } };
            Assert.IsTrue(AvatarLookCapture.SameFigure(b, c));

            var d = b with { Body = b.Body with { Ints = new Dictionary<PropertyInt, int>() } };
            Assert.IsFalse(AvatarLookCapture.SameFigure(a, d));
        }

        [TestMethod]
        public void Describe_names_the_pose()
        {
            var look = AvatarLookTests.FullLook();
            look = look with { Body = look.Body with { Pose = MotionCommand.ATOYOT } };
            StringAssert.Contains(AvatarLookCapture.Describe(look), "as a female Viamontian, posed atoyot, in: Koujia Helm?");
            Assert.AreEqual("atoyot", AvatarLookCapture.PoseName(MotionCommand.ATOYOT));
        }

        [TestMethod]
        public void SameFigure_ignores_time_only()
        {
            var a = AvatarLookTests.FullLook(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var b = AvatarLookTests.FullLook(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.IsTrue(AvatarLookCapture.SameFigure(a, b));
            Assert.AreNotEqual(a, b);

            var c = AvatarLookTests.FullLook();
            c.Pieces[0].Ints[PropertyInt.PaletteTemplate] = 1;
            Assert.IsFalse(AvatarLookCapture.SameFigure(a, c));
        }

        [TestMethod]
        public void Describe_names_pieces()
        {
            var look = AvatarLookTests.FullLook();
            look = look with { Body = look.Body with { Pose = null } };
            look.Pieces[0].Strings.Clear();
            var p2 = look.Pieces[0] with { Name = "Koujia Robe" };
            var p3 = look.Pieces[0] with { Name = "Silifi of Crimson Stars" };
            var three = look with { Pieces = new List<AvatarLookPiece> { look.Pieces[0] with { Name = "Olthoi Koujia Helm" }, p2, p3 } };

            Assert.AreEqual("Dress the Avatar as a female Viamontian in: Olthoi Koujia Helm, Koujia Robe and Silifi of Crimson Stars? This replaces its current look.", AvatarLookCapture.Describe(three));

            var none = look with { Pieces = new List<AvatarLookPiece>(), Body = look.Body with { Strings = new Dictionary<PropertyString, string> { { PropertyString.Sex, "Male" }, { PropertyString.HeritageGroup, "Olthoi" } } } };
            var bare = look with { Body = look.Body with { Strings = new Dictionary<PropertyString, string>() } };
            Assert.AreEqual("Dress the Avatar as this figure? This replaces its current look.", AvatarLookCapture.Describe(bare with { Pieces = new List<AvatarLookPiece>() }));
            var reset = look with { Body = null, Pieces = new List<AvatarLookPiece>() };
            Assert.AreEqual("Return the Avatar to its default look? This replaces its current look.", AvatarLookCapture.Describe(reset));

            Assert.AreEqual("Dress the Avatar as a male Olthoi? This replaces its current look.", AvatarLookCapture.Describe(none));
        }
    }
}
