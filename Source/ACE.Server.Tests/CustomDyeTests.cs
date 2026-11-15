using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Dye;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom dye (ACE.Server.Dye): the colour list, and the real render path (WorldObject.CalculateObjDesc) of a
    /// retail Scalemail Hauberk (73) and Long Sword (351) built DB-free, against client_portal.dat.
    /// </summary>
    [TestClass]
    public class CustomDyeTests
    {
        private static bool? datsReady;

        private static void RequireDats()
        {
            if (datsReady == null)
            {
                datsReady = DatManager.PortalDat != null;
                if (!datsReady.Value && File.Exists(@"C:\ACE\Dats\client_portal.dat"))
                {
                    try
                    {
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        DatManager.Initialize(@"C:\ACE\Dats\", true, false);
                        datsReady = DatManager.PortalDat != null;
                    }
                    catch { datsReady = false; }
                }
            }
            if (datsReady != true)
                Assert.Inconclusive("client_portal.dat not available at C:\\ACE\\Dats\\.");
        }

        private static uint nextGuid = 0x7F00D000;

        private static WorldObject Hauberk() => Make(WeenieType.Clothing, ItemType.Armor, 0x020000D4, 0x0400007E, 0x100000A6, 20, 0.66);
        private static WorldObject LongSword() => Make(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, 0x02000065, 0x04000BEF, 0x10000139, 20, null);

        private static WorldObject Make(WeenieType type, ItemType itemType, uint setup, uint paletteBase, uint clothingBase, int template, double? shade)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999998,
                ClassName = "dyetestitem",
                WeenieType = type,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Dye Test Item" } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)itemType }, { PropertyInt.PaletteTemplate, template } },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, setup }, { PropertyDataId.PaletteBase, paletteBase }, { PropertyDataId.ClothingBase, clothingBase },
                },
                PropertiesFloat = shade.HasValue ? new Dictionary<PropertyFloat, double> { { PropertyFloat.Shade, shade.Value } } : new Dictionary<PropertyFloat, double>(),
            };
            var guid = new ObjectGuid(nextGuid++);
            return type == WeenieType.MeleeWeapon ? new MeleeWeapon(weenie, guid) : new Clothing(weenie, guid);
        }

        private static List<(uint id, ushort off, ushort len)> Pals(WorldObject wo) =>
            wo.CalculateObjDesc().SubPalettes.Select(p => (p.SubPaletteId, p.Offset, p.Length)).ToList();

        [TestMethod]
        public void ColourList_SizeKeysAndText()
        {
            Assert.IsTrue(DyeColours.All.Count >= 40 && DyeColours.All.Count <= 100, $"{DyeColours.All.Count} colours");
            Assert.AreEqual(DyeColours.All.Count, DyeColours.All.Select(c => c.Key).Distinct().Count(), "keys are unique");
            Assert.IsFalse(DyeColours.All.Any(c => c.Name.Contains('\u2013') || c.Name.Contains('\u2014')), "no en or em dashes");
            Assert.AreEqual("forestgreen", DyeColours.Find("Forest-Green")?.Key);
            Assert.IsNull(DyeColours.Find("original"), "original is not a colour key");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Hauberk_DyeAll_ThenOneSlot_ThenOriginal()
        {
            RequireDats();
            var item = Hauberk();
            Assert.IsNull(CustomDye.WhyNot(item));
            Assert.AreEqual(2, CustomDye.SlotCount(item));

            var undyed = Pals(item);
            Assert.IsTrue(undyed.Count > 0);

            CustomDye.Apply(item, "crimson", -1);
            Assert.AreEqual("crimson,crimson", item.GetProperty(PropertyString.DyeColours));
            var crimson = Pals(item);
            Assert.AreEqual(undyed.Sum(p => p.len), crimson.Sum(p => p.len), "the same units are painted (long ranges in chunks)");
            Assert.IsTrue(crimson.Count > undyed.Count, "the 66-unit range is matched in chunks");
            Assert.IsFalse(crimson.Any(p => undyed.Contains(p)), "every entry is a dye palette");

            // Every picked palette really is red in its range.
            foreach (var (id, off, len) in crimson)
            {
                var pal = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Palette>(id | 0x04000000);
                long r = 0, g = 0, b = 0;
                for (int i = off * 8; i < (off + len) * 8; i++) { var c = pal.Colors[i]; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; }
                Assert.IsTrue(r > g * 1.5 && r > b * 1.5, $"0x{id:X8} at {off}+{len} is red (r {r} g {g} b {b})");
            }

            // Slot 2 only: the trim changes, the body stays crimson.
            CustomDye.Apply(item, "navy", 1);
            Assert.AreEqual("crimson,navy", item.GetProperty(PropertyString.DyeColours));

            // Back to its own colour: the property goes and the look is the retail one again.
            CustomDye.Apply(item, CustomDye.OriginalKey, -1);
            Assert.IsNull(item.GetProperty(PropertyString.DyeColours));
            CollectionAssert.AreEqual(undyed, Pals(item));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Refuses_WeaponsAndShields()
        {
            RequireDats();
            Assert.IsNotNull(CustomDye.WhyNot(LongSword()), "weapons are not dyeable");
            var shield = Hauberk();
            shield.ValidLocations = EquipMask.Shield;
            Assert.IsNotNull(CustomDye.WhyNot(shield), "shields are not dyeable");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WornSegments_KeepSkinHairEyeBlock()
        {
            RequireDats();
            var item = Hauberk();
            CustomDye.Apply(item, "crimson", -1);
            var segs = CustomDye.Segments(item, 0, 0x0400007E, 0, 64, protectBaseAppearance: true).ToList();
            Assert.AreEqual((0x0400007Eu, (ushort)0, (ushort)0x28), segs[0], "units below 0x28 keep the item's own palette");
            Assert.AreEqual(64, segs.Sum(s => s.length));
        }

        [TestMethod]
        public void Refuses_Jewellery()
        {
            var ring = Make(WeenieType.Generic, ItemType.Jewelry, 0x02000065, 0x04000BEF, 0x10000139, 20, null);
            Assert.IsNotNull(CustomDye.WhyNot(ring));
        }

        [TestMethod]
        public void Pot_LocksToItsFirstColour_OriginalStaysFree()
        {
            // 15 uses, locked to the colour of the first use.
            Assert.AreEqual("crimson:2@crimson", CustomDye.Stored("crimson", 1, "crimson"));
            Assert.AreEqual("navy", CustomDye.Stored("navy", -1, null));
            Assert.AreEqual(("crimson", 1), CustomDye.ParseLoaded("crimson:2@crimson"));
            Assert.AreEqual(("navy", -1), CustomDye.ParseLoaded("navy"));
            Assert.AreEqual((null, -1), CustomDye.ParseLoaded("@crimson"));
            Assert.AreEqual("crimson", CustomDye.ParseLock("original@crimson"));
            Assert.IsNull(CustomDye.ParseLock("navy"));

            Assert.IsTrue(CustomDye.CanLoad(null, "navy"), "a fresh pot takes any colour");
            Assert.IsTrue(CustomDye.CanLoad("crimson", "crimson"));
            Assert.IsFalse(CustomDye.CanLoad("crimson", "navy"));
            Assert.IsTrue(CustomDye.CanLoad("crimson", CustomDye.OriginalKey), "removing dye is always allowed");
        }
    }
}
