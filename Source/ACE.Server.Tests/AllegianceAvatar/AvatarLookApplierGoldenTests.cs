using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The vendor's dressed figure, pinned to golden fingerprints taken from origin/master's AvatarLookApplier (bc4b97dfc)
    /// BEFORE the applier was generalised for the Avatar fighter. Each fingerprint is the whole result of one Apply: the body
    /// properties, the hair override, the scale, the face rolls, the motion, and every worn piece with its slot, scale and
    /// cosmetic strip. A behaviour change for the vendor changes a fingerprint here.
    /// </summary>
    [TestClass]
    public class AvatarLookApplierGoldenTests
    {
        private const uint Jerkin = 124, Breeches = 117, Sword = 5555, Shield = 5556, Missing = 7777;
        private const EquipMask Chest = EquipMask.ChestArmor, Legs = EquipMask.LowerLegWear;

        private static uint nextGuid = 0x7EC97000;
        private static int faceRolls;
        private static double? defaultWeenieScale;

        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        [TestInitialize]
        public void Setup()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();

            faceRolls = 0;
            defaultWeenieScale = null;

            AvatarLookApplier.ResetSeams();
            AvatarLookApplier.PieceBuilder = BuildPiece;
            AvatarLookApplier.HeritageGenderValid = (h, g) => h == 3 || h == 1;
            AvatarLookApplier.DefaultWeenie = DefaultWeenie;
            AvatarLookApplier.FaceGenerator = _ => faceRolls++;
            AvatarLookApplier.ScaleFactor = () => 2.0;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
            AvatarLookApplier.ResetSeams();
        }

        // ---- fixtures ------------------------------------------------------------------------------------------------------

        internal static WorldObject BuildPiece(uint wcid)
        {
            if (wcid == Missing)
                return null;

            var slot = wcid == Sword ? EquipMask.MeleeWeapon : wcid == Shield ? EquipMask.Shield : wcid == Breeches ? Legs : Chest;

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Damage, 30 }, { PropertyInt.ArmorLevel, 200 }, { PropertyInt.ImbuedEffect, 1 },
                    { PropertyInt.WieldRequirements, 2 }, { PropertyInt.ItemMaxMana, 500 }, { PropertyInt.GearDamage, 5 },
                    { PropertyInt.ValidLocations, (int)slot }, { PropertyInt.PaletteTemplate, 3 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 }, { PropertyFloat.ArmorModVsSlash, 1.2 } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.ProcSpell, 99 }, { PropertyDataId.ClothingBase, 0x10000001 } },
                PropertiesSpellBook = new Dictionary<int, float> { { 1, 2.0f } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Piece " + wcid } },
            };

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        private static Weenie DefaultWeenie() => new Weenie
        {
            PropertiesFloat = defaultWeenieScale.HasValue ? new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, defaultWeenieScale.Value } } : null,
            WeenieClassId = AvatarVendor.AvatarWcid,
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Gender, 1 }, { PropertyInt.HeritageGroup, 3 }, { PropertyInt.Hairstyle, 4 } },
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Sex, "Male" }, { PropertyString.HeritageGroup, "Sho" } },
            PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001 }, { PropertyDataId.MotionTable, 0x09000001 } },
            PropertiesCreateList = new List<PropertiesCreateList>
            {
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Jerkin, Palette = 9, Shade = 0.25f },
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Breeches, Palette = 17, Shade = 1 },
                new PropertiesCreateList { DestinationType = DestinationType.Shop, WeenieClassId = 8888 },
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = Sword },
            },
        };

        internal static AvatarVendor MakeVendor()
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

        internal static AvatarLookPiece Piece(uint wcid, EquipMask slot, int palette = 85) =>
            new AvatarLookPiece(wcid, slot,
                new Dictionary<PropertyInt, int> { { PropertyInt.PaletteTemplate, palette } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.Shade, 0.5 } },
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, 0x10000ABC }, { PropertyDataId.VisualEffectScript, 0x33000001 } },
                new Dictionary<PropertyBool, bool>(),
                new List<AvatarLookObjDescRow>
                {
                    new AvatarLookObjDescRow(AvatarLookRowKind.Texture, 9, 0x05002C23, 0x05002D52),
                    new AvatarLookObjDescRow(AvatarLookRowKind.Palette, 0x04001234, 8, 16),
                    new AvatarLookObjDescRow(AvatarLookRowKind.Palette, 0x04001235, 24, 8),
                },
                "Piece " + wcid,
                new Dictionary<PropertyString, string>());

        internal static AvatarLookBody Body(MotionStance? stance = MotionStance.NonCombat, uint? defHair = null, uint? hair = null,
            MotionCommand? pose = null, double? scale = 1.1, int heritage = 1) =>
            new AvatarLookBody(
                new Dictionary<PropertyInt, int> { { PropertyInt.Gender, 2 }, { PropertyInt.HeritageGroup, heritage }, { PropertyInt.Hairstyle, 7 } },
                scale.HasValue ? new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale.Value } } : new Dictionary<PropertyFloat, double>(),
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000002 }, { PropertyDataId.HeadObject, 0x01000A00 }, { PropertyDataId.SkinPalette, 0x04000111 } },
                new Dictionary<PropertyString, string> { { PropertyString.Sex, "Female" }, { PropertyString.HeritageGroup, "Aluvian" } },
                defHair, hair, stance, pose);

        internal static AvatarLook Look(AvatarLookBody body, params AvatarLookPiece[] pieces) =>
            new AvatarLook(AvatarLookJson.CurrentVersion, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), body, pieces);

        // ---- the fingerprint -------------------------------------------------------------------------------------------------

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0.####", CultureInfo.InvariantCulture) : "-";

        /// <summary>Everything one Apply writes onto the figure, in a fixed order. Piece guids are left out (they vary per run).</summary>
        internal static string Fingerprint(Creature npc, int faces, (uint, uint)? hair)
        {
            var sb = new StringBuilder();

            foreach (var k in AvatarLookCapture.BodyInts) sb.Append($"i{(int)k}={npc.GetProperty(k)?.ToString() ?? "-"};");
            foreach (var k in AvatarLookCapture.BodyFloats) sb.Append($"f{(int)k}={F(npc.GetProperty(k))};");
            foreach (var k in AvatarLookCapture.BodyDataIds) sb.Append($"d{(int)k}={(npc.GetProperty(k).HasValue ? npc.GetProperty(k).Value.ToString("X8") : "-")};");
            foreach (var k in AvatarLookCapture.BodyStrings) sb.Append($"s{(int)k}={npc.GetProperty(k) ?? "-"};");

            sb.Append($"hair={(hair.HasValue ? $"{hair.Value.Item1:X8}/{hair.Value.Item2:X8}" : "-")};");
            sb.Append($"scale={F(npc.ObjScale)};faces={faces};");
            sb.Append($"motion={npc.CurrentMotionState?.Stance}/{npc.CurrentMotionState?.MotionState?.ForwardCommand};");

            foreach (var item in npc.EquippedObjects.Values.OrderBy(i => (uint)(i.CurrentWieldedLocation ?? 0)).ThenBy(i => i.WeenieClassId))
            {
                sb.Append($"[{item.WeenieClassId}@{item.CurrentWieldedLocation} scale={F(item.ObjScale)} pal={item.GetProperty(PropertyInt.PaletteTemplate)?.ToString() ?? "-"}");
                sb.Append($" shade={F(item.GetProperty(PropertyFloat.Shade))} cb={item.GetProperty(PropertyDataId.ClothingBase)?.ToString("X8") ?? "-"}");
                sb.Append($" vfx={item.GetProperty(PropertyDataId.VisualEffectScript)?.ToString("X8") ?? "-"}");
                sb.Append($" cos={item.GetProperty(PropertyBool.AvatarCosmetic)?.ToString() ?? "-"} give={item.GetProperty(PropertyBool.AllowGive)?.ToString() ?? "-"}");
                sb.Append($" dmg={item.GetProperty(PropertyInt.Damage)?.ToString() ?? "-"} al={item.GetProperty(PropertyInt.ArmorLevel)?.ToString() ?? "-"}");
                sb.Append($" proc={item.GetProperty(PropertyDataId.ProcSpell)?.ToString() ?? "-"} book={(item.Biota.PropertiesSpellBook == null ? "null" : item.Biota.PropertiesSpellBook.Count.ToString())}");
                sb.Append($" tex={item.Biota.PropertiesTextureMap?.Count.ToString() ?? "-"} pals={item.Biota.PropertiesPalette?.Count.ToString() ?? "-"}]");
            }

            return sb.ToString();
        }

        private static string Dress(AvatarLook look, Action<AvatarVendor> before = null)
        {
            var npc = MakeVendor();
            before?.Invoke(npc);
            AvatarLookApplier.Apply(npc, look);
            return Fingerprint(npc, faceRolls, npc.HairTextureOverride);
        }

        // ---- golden values (computed by origin/master bc4b97dfc's AvatarLookApplier) ------------------------------------------

        [TestMethod]
        public void Golden_DressedLook_WithAWeaponAndAHairOverride()
        {
            var actual = Dress(Look(Body(MotionStance.SwordCombat, 0x05001111, 0x05002222, MotionCommand.Ready),
                Piece(Jerkin, Chest), Piece(Breeches, Legs, 12), Piece(Sword, EquipMask.MeleeWeapon), Piece(Shield, EquipMask.Shield)));

            Assert.AreEqual(GoldenDressed, actual);
        }

        [TestMethod]
        public void Golden_NullLook_TheDefaultBodyAndCreateList()
        {
            defaultWeenieScale = 1.25;
            Assert.AreEqual(GoldenDefault, Dress(null));
        }

        [TestMethod]
        public void Golden_AnInvalidHeritage_KeepsTheDefaultBody_ButEquipsTheLooksPieces()
        {
            Assert.AreEqual(GoldenInvalidBody, Dress(Look(Body(MotionStance.Magic, heritage: 9), Piece(Jerkin, Chest), Piece(Missing, Legs))));
        }

        [TestMethod]
        public void Golden_RedressOverAWornFigure_ClearsEverythingFirst()
        {
            Assert.AreEqual(GoldenRedress, Dress(Look(Body(scale: null), Piece(Breeches, Legs)), npc => AvatarLookApplier.Apply(npc, Look(Body(), Piece(Jerkin, Chest), Piece(Sword, EquipMask.MeleeWeapon)))));
        }

        internal const string GoldenDressed =
            "i188=1;i113=2;i9012=7;f39=2.2;d18=01000A00;d15=-;d17=04000111;d16=-;d9=-;d12=-;d10=-;d13=-;d11=-;d14=-;d6=-;d1=02000002;d2=-;d3=-;s3=Female;s4=Aluvian;hair=05001111/05002222;scale=2.2;faces=0;motion=SwordCombat/Ready;[117@LowerLegWear scale=- pal=12 shade=0.5 cb=10000ABC vfx=33000001 cos=True give=False dmg=- al=- proc=- book=null tex=1 pals=2][124@ChestArmor scale=- pal=85 shade=0.5 cb=10000ABC vfx=33000001 cos=True give=False dmg=- al=- proc=- book=null tex=1 pals=2][5555@MeleeWeapon scale=2 pal=85 shade=0.5 cb=10000ABC vfx=33000001 cos=True give=False dmg=- al=- proc=- book=null tex=1 pals=2][5556@Shield scale=2 pal=85 shade=0.5 cb=10000ABC vfx=33000001 cos=True give=False dmg=- al=- proc=- book=null tex=1 pals=2]";
        internal const string GoldenDefault =
            "i188=3;i113=1;i9012=4;f39=2.5;d18=-;d15=-;d17=-;d16=-;d9=-;d12=-;d10=-;d13=-;d11=-;d14=-;d6=-;d1=02000001;d2=09000001;d3=-;s3=Male;s4=Sho;hair=-;scale=2.5;faces=1;motion=NonCombat/Ready;[117@LowerLegWear scale=- pal=17 shade=1 cb=10000001 vfx=- cos=True give=False dmg=- al=- proc=- book=null tex=- pals=-][124@ChestArmor scale=- pal=9 shade=0.25 cb=10000001 vfx=- cos=True give=False dmg=- al=- proc=- book=null tex=- pals=-][5555@MeleeWeapon scale=2 pal=3 shade=0 cb=10000001 vfx=- cos=True give=False dmg=- al=- proc=- book=null tex=- pals=-]";
        internal const string GoldenInvalidBody =
            "i188=3;i113=1;i9012=4;f39=2;d18=-;d15=-;d17=-;d16=-;d9=-;d12=-;d10=-;d13=-;d11=-;d14=-;d6=-;d1=02000001;d2=09000001;d3=-;s3=Male;s4=Sho;hair=-;scale=2;faces=1;motion=NonCombat/Ready;[124@ChestArmor scale=- pal=85 shade=0.5 cb=10000ABC vfx=33000001 cos=True give=False dmg=- al=- proc=- book=null tex=1 pals=2]";
        internal const string GoldenRedress =
            "i188=1;i113=2;i9012=7;f39=2;d18=01000A00;d15=-;d17=04000111;d16=-;d9=-;d12=-;d10=-;d13=-;d11=-;d14=-;d6=-;d1=02000002;d2=-;d3=-;s3=Female;s4=Aluvian;hair=-;scale=2;faces=0;motion=NonCombat/Ready;[117@LowerLegWear scale=- pal=85 shade=0.5 cb=10000ABC vfx=33000001 cos=True give=False dmg=- al=- proc=- book=null tex=1 pals=2]";
    }
}
