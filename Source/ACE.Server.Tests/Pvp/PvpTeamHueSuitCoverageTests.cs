using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Server.Managers;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Client-data cover for the team hue defaults (Docs/Pvp/TEMPLATES.md "Team hue"): every PvP suit piece
    /// (Content/sql/weenies/10072*.sql, Docs/Pvp/SUIT-LOOKS.md) must be dressable by the SHIPPED red, blue and neutral palettes and by
    /// every shipped override, because a palette its ClothingBase does not list is skipped at match time and that piece would fight
    /// in its own colours. Skipped (Inconclusive) when client_portal.dat is not available, following ClothingVisiblePaletteUnitsTests.
    /// The defaults are read from the registered PropertyManager defaults, not from constants, so changing a default in
    /// PropertyManager.cs is what this guards.
    /// </summary>
    [TestClass]
    public class PvpTeamHueSuitCoverageTests
    {
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

                // loadCell: false - these tests need only client_portal.dat
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

        private static string FindWeenieDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static readonly Regex ClothingBaseRow = new Regex(@"\(\s*\d+\s*,\s*7\s*,\s*0x(?<cb>[0-9A-Fa-f]{1,8})\s*\)\s*/\*\s*ClothingBase\s*\*/", RegexOptions.Compiled);

        private static readonly Regex RawRows = new Regex(@"INSERT\s+INTO\s+`?weenie_properties_(palette|texture_map|anim_part)`?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The ClothingBase of every PvP suit piece (file name to ClothingBase), and the files that carry raw rows.</summary>
        private static (Dictionary<string, uint> ClothingBases, List<string> WithRawRows, List<string> WithoutClothingBase) ReadSuit()
        {
            var clothingBases = new Dictionary<string, uint>();
            var withRaw = new List<string>();
            var without = new List<string>();

            foreach (var file in Directory.GetFiles(FindWeenieDirectory(), "10072*.sql").OrderBy(f => f, StringComparer.Ordinal))
            {
                // The Aegis colour variants (1007256-1007259) are "@wcid" look sources, not kit pieces dressed by palette: their
                // ClothingBases deliberately do not list the team palettes (the swap is how a team gets their colour). They have their own tests.
                if (AegisVariantWcids.Any(w => Path.GetFileName(file).StartsWith(w + " ", StringComparison.Ordinal)))
                    continue;

                var text = File.ReadAllText(file);
                var matches = ClothingBaseRow.Matches(text);
                var name = Path.GetFileName(file);

                if (matches.Count != 1)
                    without.Add(name);
                else
                    clothingBases[name] = Convert.ToUInt32(matches[0].Groups["cb"].Value, 16);

                if (RawRows.IsMatch(text))
                    withRaw.Add(name);
            }

            return (clothingBases, withRaw, without);
        }

        /// <summary>The team hue defaults as PropertyManager registers them (what a fresh shard runs).</summary>
        private static PvpTeamHueDials ShippedDials(List<string> warnings)
        {
            var longs = DefaultPropertyManager.DefaultLongProperties;
            var doubles = DefaultPropertyManager.DefaultDoubleProperties;

            return new PvpTeamHueDials(
                DefaultPropertyManager.DefaultBooleanProperties["pvp_team_hue_enabled"].Item,
                PvpTeamHueSettings.Look(longs["pvp_team_hue_red_palette"].Item, doubles["pvp_team_hue_red_shade"].Item),
                PvpTeamHueSettings.Look(longs["pvp_team_hue_blue_palette"].Item, doubles["pvp_team_hue_blue_shade"].Item),
                PvpTeamHueSettings.Look(longs["pvp_team_hue_neutral_palette"].Item, doubles["pvp_team_hue_neutral_shade"].Item),
                PvpTeamHue.ParseOverrides(DefaultPropertyManager.DefaultStringProperties["pvp_team_hue_overrides"].Item, warnings.Add),
                PvpTeamHue.ParseFfaPalettes(DefaultPropertyManager.DefaultStringProperties["pvp_team_hue_ffa_palettes"].Item, warnings.Add),
                PvpTeamHueSettings.Look(1, doubles["pvp_team_hue_ffa_shade"].Item).Shade);
        }

        [TestMethod]
        public void ShippedFfaPool_IsLoadedIntoTheDials_AndIsTheNinePaletteDefault()
        {
            var warnings = new List<string>();
            var dials = ShippedDials(warnings);

            Assert.AreEqual(0, warnings.Count, "the shipped FFA pool must parse without a warning: " + string.Join("|", warnings));
            CollectionAssert.AreEqual(new[] { 2, 13, 14, 84, 85, 86, 88, 89, 90 }, dials.FfaPalettes.ToArray());
            Assert.AreEqual(PvpTeamHueSettings.DefaultFfaShade, dials.FfaShade);
        }

        /// <summary>
        /// Every palette of the shipped Tugak pool is listed by every non-Aegis suit ClothingBase, so no seat's draw leaves a piece in
        /// its own colours. (The Aegis variants are look sources, excluded by ReadSuit.) Positive control: the check does fail for a pool
        /// holding palette 20, which two suit ClothingBases do not list.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ShippedFfaPool_IsValidOnEverySuitClothingBase()
        {
            RequireDats();

            var dials = ShippedDials(new List<string>());
            var distinct = ReadSuit().ClothingBases.Values.Distinct().OrderBy(v => v).ToList();

            Assert.IsTrue(distinct.Count > 0);
            Assert.IsTrue(dials.FfaPalettes.Count > 0);

            List<string> Problems(IEnumerable<int> pool)
            {
                var problems = new List<string>();

                foreach (var cb in distinct)
                {
                    var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb);
                    var listed = table?.ClothingSubPalEffects.Keys.ToHashSet() ?? new HashSet<uint>();

                    foreach (var palette in pool.Where(p => !listed.Contains((uint)p)))
                        problems.Add($"0x{cb:X8} does not list the FFA pool palette {palette}");
                }

                return problems;
            }

            var shipped = Problems(dials.FfaPalettes);
            Assert.AreEqual(0, shipped.Count, $"{shipped.Count} problem(s) over {distinct.Count} distinct suit ClothingBases:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, shipped)}");
            Assert.IsTrue(Problems(dials.FfaPalettes.Concat(new[] { 20 })).Count > 0, "control: a pool holding 20 is reported");
        }

        /// <summary>Not dat-backed: the suit files themselves are what the next test checks the palettes against.</summary>
        [TestMethod]
        public void SuitFiles_EachHaveOneClothingBase_AndNoRawRows()
        {
            var (clothingBases, withRaw, without) = ReadSuit();

            Assert.AreEqual(0, without.Count, "suit files without exactly one ClothingBase row: " + string.Join(", ", without));
            Assert.AreEqual(0, withRaw.Count, "raw palette/texture/anim rows disable the ClothingBase path, so team hue would not show: " + string.Join(", ", withRaw));
            Assert.IsTrue(clothingBases.Count >= 56, $"expected the 56 suit pieces, found {clothingBases.Count}");
        }

        [TestMethod]
        public void ShippedDefaults_ParseCleanly_AndMatchTheCompiledConstants()
        {
            var warnings = new List<string>();
            var dials = ShippedDials(warnings);

            Assert.AreEqual(0, warnings.Count, "the shipped overrides default must parse without a warning: " + string.Join("|", warnings));
            Assert.IsTrue(dials.Enabled, "new settings default ON");
            Assert.AreEqual(PvpTeamHueSettings.DefaultEnabled, dials.Enabled);
            Assert.AreEqual(PvpTeamHueSettings.DefaultRedPalette, dials.Red.Palette);
            Assert.AreEqual(PvpTeamHueSettings.DefaultBluePalette, dials.Blue.Palette);
            Assert.AreEqual(PvpTeamHueSettings.DefaultNeutralPalette, dials.Neutral.Palette);
            Assert.AreEqual(PvpTeamHueSettings.DefaultShade, dials.Red.Shade);
            Assert.AreEqual(PvpTeamHueSettings.DefaultShade, dials.Blue.Shade);
            Assert.AreEqual(PvpTeamHueSettings.DefaultShade, dials.Neutral.Shade);
            Assert.AreEqual(PvpTeamHueSettings.DefaultOverrides, DefaultPropertyManager.DefaultStringProperties["pvp_team_hue_overrides"].Item);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ShippedPalettes_AndOverrides_AreValidOnEverySuitClothingBase()
        {
            RequireDats();

            var warnings = new List<string>();
            var dials = ShippedDials(warnings);
            var suit = ReadSuit().ClothingBases;
            var problems = new List<string>();
            var distinct = suit.Values.Distinct().OrderBy(v => v).ToList();

            Assert.IsTrue(distinct.Count > 0);

            foreach (var cb in distinct)
            {
                var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb);

                if (table == null || table.ClothingSubPalEffects.Count == 0)
                {
                    problems.Add($"0x{cb:X8} has no readable sub-palettes");
                    continue;
                }

                var listed = table.ClothingSubPalEffects.Keys.ToHashSet();

                foreach (var hue in new[] { KitHue.Red, KitHue.Blue, KitHue.Neutral })
                {
                    var look = dials.Global(hue);

                    if (look.Palette != 0 && !listed.Contains((uint)look.Palette))
                        problems.Add($"0x{cb:X8} does not list the {PvpTeamHue.Token(hue)} palette {look.Palette}");
                }

                foreach (var o in dials.Overrides.Where(o => o.Key.ClothingBase == cb))
                {
                    if (o.Value.Palette != 0 && !listed.Contains((uint)o.Value.Palette))
                        problems.Add($"0x{cb:X8} does not list its {PvpTeamHue.Token(o.Key.Hue)} override palette {o.Value.Palette}");
                }
            }

            Assert.AreEqual(0, problems.Count, $"{problems.Count} problem(s) over {distinct.Count} distinct suit ClothingBases:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, problems)}");
        }

        /// <summary>
        /// Positive control for the test above: the dat check CAN fail. Palette 20 (Silver) is known to be missing from two suit
        /// ClothingBases, so a dial of 20 must be reported as a problem there while 85, 88 and 90 are not.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Control_Palette20_IsNotListedOnTwoSuitClothingBases_SoTheCoverageCheckCanFail()
        {
            RequireDats();

            foreach (var cb in new uint[] { 0x100002D5, 0x1000018E })
            {
                var listed = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb).ClothingSubPalEffects.Keys.ToHashSet();

                Assert.IsFalse(listed.Contains(20), $"0x{cb:X8} unexpectedly lists 20");
                Assert.IsTrue(listed.Contains(85) && listed.Contains(88) && listed.Contains(90), $"0x{cb:X8} lists the shipped defaults");
            }
        }

        private static readonly Regex PaletteTemplateRow = new Regex(@"\(\s*\d+\s*,\s*3\s*,\s*(?<pt>\d+)\s*\)\s*/\*\s*PaletteTemplate\s*\*/", RegexOptions.Compiled);

        /// <summary>The four PvP Arena Aegis colour variants (Docs/Pvp/SUIT-LOOKS.md "Aegis colour variants"), by wcid.</summary>
        private static readonly uint[] AegisVariantWcids = { 1007256, 1007257, 1007258, 1007259 };

        private static (uint ClothingBase, int Palette) ReadAegisVariant(uint wcid)
        {
            var files = Directory.GetFiles(FindWeenieDirectory(), $"{wcid} PvP Arena Aegis*.sql");

            Assert.AreEqual(1, files.Length, $"expected exactly one weenie file for {wcid}");

            var text = File.ReadAllText(files[0]);
            var cb = ClothingBaseRow.Matches(text);
            var pt = PaletteTemplateRow.Matches(text);

            Assert.AreEqual(1, cb.Count, $"{wcid}: exactly one ClothingBase row");
            Assert.AreEqual(1, pt.Count, $"{wcid}: exactly one PaletteTemplate row");
            Assert.IsFalse(RawRows.IsMatch(text), $"{wcid}: raw palette/texture/anim rows would disable the ClothingBase path");

            return (Convert.ToUInt32(cb[0].Groups["cb"].Value, 16), int.Parse(pt[0].Groups["pt"].Value));
        }

        /// <summary>Not dat-backed: each variant is a valid "@wcid" look source (a ClothingBase, a palette, no raw rows) and the four are distinct looks.</summary>
        [TestMethod]
        public void AegisVariants_EachHaveOneDistinctClothingBaseAndPalette_AndNoRawRows()
        {
            var looks = AegisVariantWcids.Select(ReadAegisVariant).ToList();

            Assert.AreEqual(4, looks.Select(l => l.ClothingBase).Distinct().Count(), "four different ClothingBases");
            Assert.AreEqual(4, looks.Select(l => l.Palette).Distinct().Count(), "four different palettes");
        }

        /// <summary>Each variant's ClothingBase must list its own PaletteTemplate in the client dat, or the swapped aegis would render the first palette.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void AegisVariants_EachClothingBaseListsItsOwnPalette()
        {
            RequireDats();

            foreach (var wcid in AegisVariantWcids)
            {
                var (cb, palette) = ReadAegisVariant(wcid);
                var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb);

                Assert.IsNotNull(table, $"{wcid}: ClothingTable 0x{cb:X8} is not in the portal dat");
                Assert.IsTrue(table.ClothingSubPalEffects.ContainsKey((uint)palette), $"{wcid}: 0x{cb:X8} does not list its own palette {palette}");
            }
        }

        /// <summary>Whatever the shipped palettes are, ResolveItem itself must apply (not skip) every suit piece, through the real dat lookup.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ResolveItem_AppliesEveryHue_ToEverySuitPiece_ThroughTheRealDatLookup()
        {
            RequireDats();

            var dials = ShippedDials(new List<string>());
            var suit = ReadSuit().ClothingBases;

            Func<uint, IReadOnlyCollection<uint>> lookup = cb => DatManager.PortalDat.ReadFromDat<ClothingTable>(cb)?.ClothingSubPalEffects.Keys.ToList();

            foreach (var (file, cb) in suit)
            {
                var item = new PvpTemplateKitItem { WeenieClassId = 1, WieldLocation = 0x200 };
                item.DataIds[(int)ACE.Entity.Enum.Properties.PropertyDataId.ClothingBase] = cb;

                foreach (var hue in new[] { KitHue.Red, KitHue.Blue, KitHue.Neutral })
                {
                    var r = PvpTeamHue.ResolveItem(item, hue, dials, lookup);

                    Assert.IsTrue(r.Applied || r.Skip == HueSkipKind.KeepOwn, $"{file} (0x{cb:X8}) {hue}: {r.Skip} {r.Detail}");
                }
            }
        }
    }
}
