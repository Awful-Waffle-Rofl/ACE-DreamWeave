using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Models;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Dat-backed cover for ClothingVisiblePaletteUnits, the set of /8 palette units a worn
    /// exclusion item's visible textures index. Skipped (Inconclusive) when client_portal.dat is not
    /// available, following BadSetupDidTests.
    /// </summary>
    [TestClass]
    public class ClothingVisiblePaletteUnitsTests
    {
        /// <summary>DreamWeave Strathelar Suit (wcid 1002713): ClothingBase, male setup, exclusions.</summary>
        private const uint SuitClothingBase = 0x10000405;
        private const uint HumanMaleSetup = 0x02000001;

        private static readonly object initLock = new object();

        private static bool initialized;

        private static string skipReason;

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;    // already loaded by something else in this test host

            var candidates = new List<string>();

            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            var portalDat = Path.Combine(datDir, "client_portal.dat");

            try
            {
                using (new FileStream(portalDat, FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException ex)
            {
                return $"Skipped: {portalDat} could not be opened for reading ({ex.Message}). Close any running client that holds the .dat files.";
            }

            try
            {
                // the dat readers use Encoding.Default (cp1252), which is not present on .NET without this
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                // loadCell: false - these tests need only client_portal.dat (and client_highres.dat if present)
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            if (DatManager.PortalDat == null)
                return $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat.";

            return null;
        }

        /// <summary>The suit's 22 weenie texture_map rows, as in Content/sql/weenies/1002713 DreamWeave Strathelar Suit.sql.</summary>
        private static List<PropertiesTextureMap> SuitTextureRows()
        {
            var rows = new (byte Part, uint Old, uint New)[]
            {
                (0, 0x05001F9B, 0x05002585), (0, 0x05001F9A, 0x05002585),
                (1, 0x05001FA6, 0x05002588), (5, 0x05001FA6, 0x05002588),
                (2, 0x05001FA6, 0x05002589), (6, 0x05001FA6, 0x05002589),
                (3, 0x05001FA8, 0x05002582), (4, 0x05001FA8, 0x05002582), (7, 0x05001FA8, 0x05002582), (8, 0x05001FA8, 0x05002582),
                (9, 0x05001FA0, 0x05002584), (9, 0x05001FA1, 0x05002584), (9, 0x05001FA2, 0x05002584),
                (10, 0x05001F9E, 0x05002586), (13, 0x05001F9E, 0x05002586), (13, 0x05001F9D, 0x05002586), (13, 0x05001F9F, 0x05002586),
                (11, 0x05001F9C, 0x05002587), (14, 0x05001F9C, 0x05002587), (14, 0x05001FA9, 0x05002587),
                (12, 0x05001FA3, 0x05002581), (15, 0x05001FA3, 0x05002581),
            };

            return rows.Select(r => new PropertiesTextureMap { PartIndex = r.Part, OldTexture = r.Old, NewTexture = r.New }).ToList();
        }

        private static HashSet<int> Units(params (int Start, int EndInclusive)[] ranges)
        {
            var set = new HashSet<int>();
            foreach (var (start, endInclusive) in ranges)
                for (int u = start; u <= endInclusive; u++)
                    set.Add(u);
            return set;
        }

        private static ClothingBaseEffect SuitMaleEffect()
        {
            var clothingTable = DatManager.PortalDat.ReadFromDat<ClothingTable>(SuitClothingBase);
            Assert.IsTrue(clothingTable.ClothingBaseEffects.ContainsKey(HumanMaleSetup), "precondition: the suit's ClothingTable dresses the human male setup");
            return clothingTable.ClothingBaseEffects[HumanMaleSetup];
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Suit_VisibleUnits_ExcludeHeadOnlyRange_IncludeBodyRanges()
        {
            RequireDats();

            var units = ClothingVisiblePaletteUnits.Get(SuitClothingBase, HumanMaleSetup, SuitMaleEffect(), new HashSet<byte> { 16 }, SuitTextureRows());

            Assert.IsNotNull(units, "the suit's visible textures are all readable, so the set must be computed");

            // 240-248 is indexed only by the suit's excluded head (part 16); it must not survive.
            foreach (var u in Units((240, 248)))
                Assert.IsFalse(units.Contains(u), $"unit {u} is used only by the excluded head");

            // What the visible parts 0-15 actually index, as measured from the dats.
            var expected = Units((40, 63), (72, 95), (136, 174), (209, 239));
            CollectionAssert.AreEquivalent(expected.OrderBy(u => u).ToList(), units.OrderBy(u => u).ToList());
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void BogusModelId_ReturnsNull_FailOpen()
        {
            RequireDats();

            const uint bogusModelId = 0x01FFFFF0;
            Assert.IsFalse(DatManager.PortalDat.AllFiles.ContainsKey(bogusModelId), "precondition: the bogus GfxObj id must not exist");

            // One CloObjectEffect { Index = 0, ModelId = bogus, no texture effects }, built through the
            // dat Unpack path because the entity's setters are private.
            var effect = new ClothingBaseEffect();
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(1u);               // CloObjectEffects count
                    writer.Write(0u);               // Index
                    writer.Write(bogusModelId);     // ModelId
                    writer.Write(0u);               // CloTextureEffects count
                }

                stream.Position = 0;
                using (var reader = new BinaryReader(stream))
                    effect.Unpack(reader);
            }

            Assert.AreEqual(1, effect.CloObjectEffects.Count);
            Assert.AreEqual(bogusModelId, effect.CloObjectEffects[0].ModelId);

            // A clothingBase no real item uses keeps this cache entry apart from the other tests.
            var units = ClothingVisiblePaletteUnits.Get(0xDEAD0001, HumanMaleSetup, effect, new HashSet<byte> { 16 }, null);

            Assert.IsNull(units, "a GfxObj missing from the dat must fail open (null), never yield an empty set that drops every range");
        }

        /// <summary>Asheron's baked setup and its parts 0-15, index for index.</summary>
        private const uint AsheronSetup = 0x020016CB;

        private static readonly uint[] AsheronParts0To15 =
        {
            0x01003F99, 0x01003F9A, 0x01003F9B, 0x010001EC, 0x010001EC, 0x01003F9C, 0x01003F9D, 0x010001EC,
            0x010001EC, 0x01003F9E, 0x01003F9F, 0x01003FA0, 0x01003FA1, 0x01003FA2, 0x01003FA3, 0x01003FA4,
        };

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void AsheronSetup_PartList_MatchesDocumentedOverrideModels()
        {
            RequireDats();

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(AsheronSetup);

            Assert.AreEqual(17, setup.Parts.Count, "Asheron's setup has the 17 human body parts 0-16");
            for (var i = 0; i < AsheronParts0To15.Length; i++)
                Assert.AreEqual(AsheronParts0To15[i], setup.Parts[i], $"part {i}");
            Assert.AreEqual(0x01003FA5u, setup.Parts[16], "part 16 is his head, which a worn suit excludes");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ModelOverrides_ReplaceTableModels_ForPaletteUnits()
        {
            RequireDats();

            // The suit's ClothingTable with every visible part overridden by Asheron's meshes. His body
            // meshes carry non-palette (R8G8B8) textures, except the placeholder 0x010001EC (unit 0) and
            // his left hand 0x01003FA1, which uses the human hand texture 0x050003D3 (skin units 3-23).
            // If the overrides were ignored this would be the suit's own 40-63, 72-95, ... set.
            var overrides = new SortedDictionary<byte, uint>();
            for (byte i = 0; i < AsheronParts0To15.Length; i++)
                overrides[i] = AsheronParts0To15[i];

            var units = ClothingVisiblePaletteUnits.Get(SuitClothingBase, HumanMaleSetup, SuitMaleEffect(), new HashSet<byte> { 16 }, SuitTextureRows(), overrides);

            Assert.IsNotNull(units);
            var expected = Units((0, 0), (3, 23));
            CollectionAssert.AreEquivalent(expected.OrderBy(u => u).ToList(), units.OrderBy(u => u).ToList());
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WornModelOverrides_ParseApplicable_DropsIdMissingFromDat_KeepsRealOne()
        {
            RequireDats();

            const uint bogusModelId = 0x01FFFFF0;
            Assert.IsFalse(DatManager.PortalDat.AllFiles.ContainsKey(bogusModelId), "precondition: the bogus GfxObj id must not exist");
            Assert.IsTrue(DatManager.PortalDat.AllFiles.ContainsKey(0x01003F99), "precondition: Asheron's part 0 exists");

            var result = WornModelOverrides.ParseApplicable("0:0x01003F99,1:0x01FFFFF0", new HashSet<byte>(), 999999);

            CollectionAssert.AreEqual(new byte[] { 0 }, result.Keys.ToArray());
            Assert.AreEqual(0x01003F99u, result[0]);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void SameInputs_ReturnCachedResult()
        {
            RequireDats();

            var effect = SuitMaleEffect();

            var first = ClothingVisiblePaletteUnits.Get(SuitClothingBase, HumanMaleSetup, effect, new HashSet<byte> { 16 }, SuitTextureRows());
            var second = ClothingVisiblePaletteUnits.Get(SuitClothingBase, HumanMaleSetup, effect, new HashSet<byte> { 16 }, SuitTextureRows());

            Assert.IsNotNull(first);
            Assert.AreSame(first, second, "equal inputs (fresh collections) must hit the per-item cache");
        }
    }
}
