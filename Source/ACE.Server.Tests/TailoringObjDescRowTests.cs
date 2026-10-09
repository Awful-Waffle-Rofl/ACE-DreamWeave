using System.Collections.Generic;

using ACE.Entity.Models;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers Tailoring.CopyObjDescRows, which carries a source item's instance-level texture_map and
    /// palette rows onto the tailoring target.
    ///
    /// The helper is exercised directly rather than through SetCommonProperties / UpdateCommonProps.
    /// Those two are not reachable from a unit test: SetCommonProperties calls
    /// LootGenerationFactory.GetLongDesc and PropertyManager, and UpdateCommonProps needs a live
    /// Player with a Session. The VisualEffectScript copy therefore has NO unit cover here - it is a
    /// single assignment inside those methods, and its behaviour is queued for live verification
    /// instead (Docs/VERIFY-QUEUE.md, the tailoring row).
    ///
    /// TestCreatures supplies bare WorldObjects with no database and no dat files.
    /// </summary>
    [TestClass]
    public class TailoringObjDescRowTests
    {
        private static PropertiesTextureMap Texture(byte part, uint oldTex, uint newTex)
        {
            return new PropertiesTextureMap { PartIndex = part, OldTexture = oldTex, NewTexture = newTex };
        }

        private static PropertiesPalette Palette(uint subPaletteId, ushort offset, ushort length)
        {
            return new PropertiesPalette { SubPaletteId = subPaletteId, Offset = offset, Length = length };
        }

        [TestMethod]
        public void CopyObjDescRows_CopiesRowsAsIndependentInstances()
        {
            var source = TestCreatures.CreateQuestBearer("tailoring source");
            var target = TestCreatures.CreateQuestBearer("tailoring target");

            source.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { Texture(9, 0x05002C23, 0x05002D52) };
            source.Biota.PropertiesPalette = new List<PropertiesPalette> { Palette(0x04001234, 8, 16) };

            Tailoring.CopyObjDescRows(source, target);

            Assert.AreEqual(1, target.Biota.PropertiesTextureMap.Count);
            Assert.AreEqual((byte)9, target.Biota.PropertiesTextureMap[0].PartIndex);
            Assert.AreEqual(0x05002C23u, target.Biota.PropertiesTextureMap[0].OldTexture);
            Assert.AreEqual(0x05002D52u, target.Biota.PropertiesTextureMap[0].NewTexture);

            Assert.AreEqual(1, target.Biota.PropertiesPalette.Count);
            Assert.AreEqual(0x04001234u, target.Biota.PropertiesPalette[0].SubPaletteId);
            Assert.AreEqual((ushort)8, target.Biota.PropertiesPalette[0].Offset);
            Assert.AreEqual((ushort)16, target.Biota.PropertiesPalette[0].Length);

            // The source is consumed by tailoring, but a shared row instance would still be a live
            // aliasing bug: both biotas would save the same object and one edit would move both.
            source.Biota.PropertiesTextureMap[0].NewTexture = 0x0500FFFF;
            source.Biota.PropertiesPalette[0].SubPaletteId = 0x0400FFFF;

            Assert.AreEqual(0x05002D52u, target.Biota.PropertiesTextureMap[0].NewTexture);
            Assert.AreEqual(0x04001234u, target.Biota.PropertiesPalette[0].SubPaletteId);
        }

        [TestMethod]
        public void CopyObjDescRows_SourceWithNoRows_ClearsTarget()
        {
            var source = TestCreatures.CreateQuestBearer("plain source");
            var target = TestCreatures.CreateQuestBearer("decorated target");

            source.Biota.PropertiesTextureMap = null;
            source.Biota.PropertiesPalette = null;

            target.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { Texture(16, 0x050021EA, 0x05002CF4) };
            target.Biota.PropertiesPalette = new List<PropertiesPalette> { Palette(0x04005678, 0, 8) };

            Tailoring.CopyObjDescRows(source, target);

            // Null, not empty: BiotaUpdater treats a null source collection as "remove every child
            // row", which is what makes a plain source actually strip a decorated target.
            Assert.IsNull(target.Biota.PropertiesTextureMap);
            Assert.IsNull(target.Biota.PropertiesPalette);
        }

        [TestMethod]
        public void CopyObjDescRows_EmptySource_ClearsTarget()
        {
            var source = TestCreatures.CreateQuestBearer("empty source");
            var target = TestCreatures.CreateQuestBearer("decorated target");

            source.Biota.PropertiesTextureMap = new List<PropertiesTextureMap>();
            source.Biota.PropertiesPalette = new List<PropertiesPalette>();

            target.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { Texture(16, 0x050021EA, 0x05002CF4) };
            target.Biota.PropertiesPalette = new List<PropertiesPalette> { Palette(0x04005678, 0, 8) };

            Tailoring.CopyObjDescRows(source, target);

            Assert.AreEqual(0, target.Biota.PropertiesTextureMap.Count);
            Assert.AreEqual(0, target.Biota.PropertiesPalette.Count);
        }

        [TestMethod]
        public void CopyObjDescRows_FlagsTargetForSave()
        {
            var source = TestCreatures.CreateQuestBearer("source");
            var target = TestCreatures.CreateQuestBearer("target");

            source.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { Texture(0, 0x05002EBD, 0x05002DCE) };
            target.ChangesDetected = false;

            Tailoring.CopyObjDescRows(source, target);

            Assert.IsTrue(target.ChangesDetected, "rows are not properties, so nothing else marks the biota dirty");
        }
    }
}
