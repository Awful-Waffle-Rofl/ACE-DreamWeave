using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the palette half of Creature.ApplyWornModelOverrides: on the paths where no ClothingTable effect ran for an item
    /// carrying PropertyString.WornModelOverrides (no ClothingBase, or none for the wearer's setup), the item's own palette rows
    /// are appended to the wearer's ObjDesc. Before this, those rows only ever reached the item's own ground/pack model.
    ///
    /// ApplyWornModelOverrides is exercised directly with a DB-free, dat-free item: it reads no dat itself (the caller has
    /// already reduced the overrides to the applicable pairs, which here come from WornModelOverrides.Parse, the pure half).
    /// Creature.CalculateObjDesc itself reads client_portal.dat and a live EquippedObjects collection and is not reachable here.
    /// </summary>
    [TestClass]
    public class WornOverridePaletteTests
    {
        /// <summary>Asheron's Regalia (1006363) override string, verbatim from Content/sql/weenies/1006363 Asheron's Regalia.sql (the 9020 row).</summary>
        private const string RegaliaOverrides = "0:0x01003F99,1:0x01003F9A,2:0x01003F9B,3:0x010001EC,4:0x010001EC,5:0x01003F9C,6:0x01003F9D,7:0x010001EC,8:0x010001EC,9:0x01003F9E,10:0x01003F9F,11:0x01003FA0,13:0x01003FA2,14:0x01003FA3";

        private static PropertiesPalette Palette(uint id, ushort offset, ushort length) =>
            new PropertiesPalette { SubPaletteId = id, Offset = offset, Length = length };

        private static Creature Item(string overrides, params PropertiesPalette[] rows)
        {
            var item = TestCreatures.CreateQuestBearer("override item");
            item.SetProperty(PropertyString.WornModelOverrides, overrides);
            item.Biota.PropertiesPalette = rows.Length == 0 ? null : rows.ToList();
            return item;
        }

        private static ACE.Entity.ObjDesc WearerDescWithOnePalette()
        {
            var objDesc = new ACE.Entity.ObjDesc();
            objDesc.SubPalettes.Add(Palette(0x04000001, 0, 8)); // the wearer's own skin row, already in place
            return objDesc;
        }

        [TestMethod]
        public void NoClothingTablePath_AppendsTheItemsPaletteRows_AfterTheWearersOwn()
        {
            var item = Item("0:0x01001FD7,16:0x01001FE5",
                Palette(0x04001072, 0, 40), Palette(0x04001071, 40, 16), Palette(0x04001072, 56, 72), Palette(0x04001072, 128, 128));
            var objDesc = WearerDescWithOnePalette();

            Creature.ApplyWornModelOverrides(objDesc, item, WornModelOverrides.Parse(item.GetProperty(PropertyString.WornModelOverrides)), null, new List<uint>(), mergeTextureRowsForOverrideParts: true);

            Assert.AreEqual(5, objDesc.SubPalettes.Count, "the wearer's row plus the item's four");
            Assert.AreEqual(0x04000001u, objDesc.SubPalettes[0].SubPaletteId, "the wearer's own row stays first; the item's rows come after it, so they win");

            var appended = objDesc.SubPalettes.Skip(1).Select(p => (p.SubPaletteId, p.Offset, p.Length)).ToList();
            CollectionAssert.AreEqual(
                new[] { (0x04001072u, (ushort)0, (ushort)40), (0x04001071u, (ushort)40, (ushort)16), (0x04001072u, (ushort)56, (ushort)72), (0x04001072u, (ushort)128, (ushort)128) },
                appended);

            Assert.AreEqual(2, objDesc.AnimPartChanges.Count, "the model half is unchanged: one swap per override pair");
        }

        [TestMethod]
        public void AppendedRows_AreCopies_NotTheItemsOwnInstances()
        {
            var item = Item("9:0x01001FE2", Palette(0x04001072, 56, 72));
            var objDesc = new ACE.Entity.ObjDesc();

            Creature.ApplyWornModelOverrides(objDesc, item, WornModelOverrides.Parse("9:0x01001FE2"), null, new List<uint>(), mergeTextureRowsForOverrideParts: true);

            Assert.AreEqual(1, objDesc.SubPalettes.Count, "positive control: the row was appended");
            item.Biota.PropertiesPalette[0].SubPaletteId = 0x0400FFFF;
            Assert.AreEqual(0x04001072u, objDesc.SubPalettes[0].SubPaletteId, "editing the item's row must not move the wearer's ObjDesc");
        }

        [TestMethod]
        public void AsheronsRegalia_HasNoPaletteRows_WearerPalettesUnchanged()
        {
            var regalia = Item(RegaliaOverrides);
            var objDesc = WearerDescWithOnePalette();
            var coverage = new List<uint>();

            Creature.ApplyWornModelOverrides(objDesc, regalia, WornModelOverrides.Parse(RegaliaOverrides), null, coverage, mergeTextureRowsForOverrideParts: true);

            Assert.AreEqual(1, objDesc.SubPalettes.Count, "the Regalia carries no palette rows, so nothing is appended");
            Assert.AreEqual(0x04000001u, objDesc.SubPalettes[0].SubPaletteId);
            Assert.AreEqual(14, objDesc.AnimPartChanges.Count, "positive control: the Regalia's 14 model swaps still applied");
            Assert.AreEqual(14, coverage.Count);

            // Positive control for the discriminator: the same item WITH a palette row does append it.
            regalia.Biota.PropertiesPalette = new List<PropertiesPalette> { Palette(0x04001072, 56, 72) };
            var withRow = WearerDescWithOnePalette();
            Creature.ApplyWornModelOverrides(withRow, regalia, WornModelOverrides.Parse(RegaliaOverrides), null, new List<uint>(), mergeTextureRowsForOverrideParts: true);
            Assert.AreEqual(2, withRow.SubPalettes.Count);
        }

        [TestMethod]
        public void ClothingTablePath_DoesNotAppendHere_BecauseThatPathEmitsTheItemRowsItself()
        {
            var item = Item("16:0x01001FE5", Palette(0x04001072, 56, 72));
            var objDesc = new ACE.Entity.ObjDesc();

            Creature.ApplyWornModelOverrides(objDesc, item, WornModelOverrides.Parse("16:0x01001FE5"), null, new List<uint>(), mergeTextureRowsForOverrideParts: false);

            Assert.AreEqual(0, objDesc.SubPalettes.Count, "on the ClothingTable path the general item-row merge already appends these; appending here too would double them");

            // Positive control: the same call on the no-ClothingTable path appends the row.
            var control = new ACE.Entity.ObjDesc();
            Creature.ApplyWornModelOverrides(control, item, WornModelOverrides.Parse("16:0x01001FE5"), null, new List<uint>(), mergeTextureRowsForOverrideParts: true);
            Assert.AreEqual(1, control.SubPalettes.Count);
        }

        [TestMethod]
        public void WithExclusions_RowsAreClippedOutOfTheBaseAppearanceRegion()
        {
            var item = Item("9:0x01001FE2", Palette(0x04001072, 0, 40), Palette(0x04001071, 32, 24), Palette(0x04001072, 56, 72));
            var exclusions = new HashSet<byte> { 16 };
            var objDesc = new ACE.Entity.ObjDesc();

            Creature.ApplyWornModelOverrides(objDesc, item, WornModelOverrides.Parse("9:0x01001FE2"), exclusions, new List<uint>(), mergeTextureRowsForOverrideParts: true);

            var rows = objDesc.SubPalettes.Select(p => (p.SubPaletteId, p.Offset, p.Length)).ToList();
            CollectionAssert.AreEqual(
                new[] { (0x04001071u, ClothingPartExclusions.BaseAppearancePaletteEnd, (ushort)16), (0x04001072u, (ushort)56, (ushort)72) },
                rows,
                "0-39 lies wholly in the base-appearance region and is dropped; 32-55 is clipped to 40-55; 56-127 is untouched");

            // Positive control: without exclusions nothing is clipped.
            var unclipped = new ACE.Entity.ObjDesc();
            Creature.ApplyWornModelOverrides(unclipped, item, WornModelOverrides.Parse("9:0x01001FE2"), null, new List<uint>(), mergeTextureRowsForOverrideParts: true);
            Assert.AreEqual(3, unclipped.SubPalettes.Count);
            Assert.AreEqual((ushort)0, unclipped.SubPalettes[0].Offset);
        }

        [TestMethod]
        public void RowsTheWireCannotCarry_AreDropped_NotTruncated()
        {
            // ObjDesc writes offset and length as single bytes (WorldObject_Networking.cs, the SubPalettes loop).
            var subPalettes = new List<PropertiesPalette>();
            var rows = new[] { Palette(0x04000010, 0, 256), Palette(0x04000011, 256, 8), Palette(0x04000012, 8, 0), Palette(0x04000013, 255, 1), Palette(0x04000014, 0, 255) };

            Creature.AppendWornOverridePaletteRows(subPalettes, rows, null);

            CollectionAssert.AreEqual(new[] { 0x04000013u, 0x04000014u }, subPalettes.Select(p => p.SubPaletteId).ToList(),
                "length 256 and offset 256 would wrap to 0 on the wire, and a zero-length row says nothing; the two in-range rows survive");
        }

        [TestMethod]
        public void RowsThatRunPastTheEndOfThePalette_AreDropped()
        {
            // Each value fits a byte, but the range ends past unit 256 (2048 colours / 8).
            var subPalettes = new List<PropertiesPalette>();
            var rows = new[] { Palette(0x04000020, 128, 129), Palette(0x04000021, 200, 100), Palette(0x04000022, 255, 2), Palette(0x04000023, 128, 128), Palette(0x04000024, 255, 1) };

            Creature.AppendWornOverridePaletteRows(subPalettes, rows, null);

            CollectionAssert.AreEqual(new[] { 0x04000023u, 0x04000024u }, subPalettes.Select(p => p.SubPaletteId).ToList(),
                "128+129, 200+100 and 255+2 run past unit 256; 128+128 and 255+1 end exactly at it and survive");
            Assert.AreEqual(256, Creature.PaletteUnitCount);
        }

        // ---- the three call sites in Creature.CalculateObjDesc ----

        private static string CalculateObjDescBody()
        {
            var native = Path.Combine("Source", "ACE.Server", "WorldObjects", "Creature_Networking.cs");
            string code = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && code == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    code = AccountVaultPurgeTests.StripComments(File.ReadAllText(candidate));
            }

            Assert.IsNotNull(code, $"Could not find {native} by walking up from {AppContext.BaseDirectory}.");

            var start = code.IndexOf("public override ACE.Entity.ObjDesc CalculateObjDesc()", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "could not find Creature.CalculateObjDesc");
            var open = code.IndexOf('{', start);
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail("unbalanced braces in CalculateObjDesc");
            return null;
        }

        [TestMethod]
        public void CalculateObjDesc_PassesTheMergeFlag_TrueOnlyWhereNoClothingTableRan()
        {
            var body = CalculateObjDescBody();

            var calls = Regex.Matches(body, @"ApplyWornModelOverrides\(objDesc, w, modelOverrides, exclusions, coverage, mergeTextureRowsForOverrideParts: (true|false)\)")
                .Cast<Match>().Select(m => (Index: m.Index, Flag: m.Groups[1].Value)).ToList();

            Assert.AreEqual(3, calls.Count, "expected exactly three ApplyWornModelOverrides call sites in CalculateObjDesc");

            var noClothingBase = body.IndexOf("if (!w.ClothingBase.HasValue && hasModelOverrides)", StringComparison.Ordinal);
            var readTable = body.IndexOf("item = DatManager.PortalDat.ReadFromDat<ClothingTable>((uint)w.ClothingBase)", StringComparison.Ordinal);
            var tableLoop = body.IndexOf("foreach (CloObjectEffect t in clothingBaseEffect.CloObjectEffects)", StringComparison.Ordinal);
            var noEntry = body.IndexOf("else if (hasModelOverrides)", StringComparison.Ordinal);
            Assert.IsTrue(noClothingBase >= 0 && readTable > noClothingBase && tableLoop > readTable && noEntry > tableLoop, "the three branches are no longer in the expected shape");

            // 1. no ClothingBase: no table ran, so the item's own rows (textures AND palettes) must be merged here.
            Assert.IsTrue(calls[0].Index > noClothingBase && calls[0].Index < readTable, "first call is the no-ClothingBase branch");
            Assert.AreEqual("true", calls[0].Flag, "no-ClothingBase branch must merge the item's own rows");

            // 2. ClothingTable path: the general item-row merge already emits them, so this call must not.
            Assert.IsTrue(calls[1].Index > tableLoop && calls[1].Index < noEntry, "second call is the ClothingTable branch");
            Assert.AreEqual("false", calls[1].Flag, "ClothingTable branch must not merge again, or every palette row doubles");

            // 3. ClothingBase with no entry for this setup: no table ran here either.
            Assert.IsTrue(calls[2].Index > noEntry, "third call is the no-entry-for-this-setup branch");
            Assert.AreEqual("true", calls[2].Flag, "no-entry branch must merge the item's own rows");
        }

        [TestMethod]
        public void AppendWornOverridePaletteRows_NullInputs_AreNoOps()
        {
            var subPalettes = new List<PropertiesPalette>();
            Creature.AppendWornOverridePaletteRows(subPalettes, null, null);
            Creature.AppendWornOverridePaletteRows(null, new[] { Palette(1, 0, 8) }, null);
            Assert.AreEqual(0, subPalettes.Count);
        }
    }
}
