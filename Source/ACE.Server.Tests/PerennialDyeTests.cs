using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure half of the Perennial dye feature (Docs/Pvp/DYES.md): recipe detection, the decision both the guard and the appraisal share,
    /// the refusal text, the appraisal lines against a stub examiner, and where the guard sits in the use path. Dat-free: the ClothingTable
    /// reader is a seam, restored in TestCleanup.
    /// </summary>
    [TestClass]
    public class PerennialDyeLogicTests
    {
        private static readonly Func<uint, IReadOnlyDictionary<uint, uint[]>> ProductionReader = PerennialDyes.TableReader;

        [TestInitialize]
        public void Setup()
        {
            PerennialDyes.TableReader = ProductionReader;
            PerennialDyes.ClearCache();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PerennialDyes.TableReader = ProductionReader;
            PerennialDyes.ClearCache();
        }

        // Fake tables keyed by ClothingBase: 1 = armor with templates 14 (no shell), 21 (shell) and 17 (one region); 2 = cloth with only 14.
        private static IReadOnlyDictionary<uint, uint[]> Fake(uint clothingBase)
        {
            switch (clothingBase)
            {
                case 1:
                    return new Dictionary<uint, uint[]>
                    {
                        { 14, new uint[] { 0x0F000001, 0x0F000002 } },
                        { 21, new uint[] { 0x0F000001, PerennialDyes.ShellPaletteSet } },
                        { 17, new uint[] { 0x0F000001 } },
                    };
                case 2:
                    return new Dictionary<uint, uint[]> { { 14, new uint[] { 0x0F000010 } } };
                default:
                    return null;
            }
        }

        [TestMethod]
        public void CheckFor_FindsTemplateAndShellRecipesByWhatTheyWrite_NotById()
        {
            var template = PerennialDyeHarness.CopyRecipe(7, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            var primary = PerennialDyeHarness.CopyRecipe(8, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            var shell = PerennialDyeHarness.CopyRecipe(9, new int[0], new[] { (int)PropertyFloat.DyeShellShade });
            var other = PerennialDyeHarness.CopyRecipe(10, new[] { (int)PropertyInt.Value }, new[] { (int)PropertyFloat.Shade });

            Assert.AreEqual(PerennialDyes.DyeCheck.Template, PerennialDyes.CheckFor(template));
            Assert.AreEqual(PerennialDyes.DyeCheck.Template, PerennialDyes.CheckFor(primary));
            Assert.AreEqual(PerennialDyes.DyeCheck.Shell, PerennialDyes.CheckFor(shell));
            Assert.IsNull(PerennialDyes.CheckFor(other), "copying Value or Shade alone is no dye");
            Assert.IsNull(PerennialDyes.CheckFor(null));

            // a FAILURE mod that copies the template does not make the recipe a dye
            var failOnly = PerennialDyeHarness.CopyRecipe(11, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            failOnly.RecipeMod.First().ExecutesOnSuccess = false;
            Assert.IsNull(PerennialDyes.CheckFor(failOnly));

            // SetValue (not a copy) of the template is not a dye either
            var setValue = PerennialDyeHarness.CopyRecipe(12, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            setValue.RecipeMod.First().RecipeModsInt.First().Enum = (int)ModificationOperation.SetValue;
            Assert.IsNull(PerennialDyes.CheckFor(setValue));
        }

        [TestMethod]
        public void Accepts_Template_ChecksTheTableForTheDyesTemplate()
        {
            PerennialDyes.TableReader = Fake;

            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 21, 1, 14, false));
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 93, 1, 14, false));
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 21, 2, 14, false), "cloth table has no template 21");
            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, null, 2, 14, false), "a dye with no template has nothing to refuse on");
        }

        [TestMethod]
        public void Accepts_Shell_NeedsTwoRegionsWithRegion1TheShellSet_OnTheCurrentTemplate()
        {
            PerennialDyes.TableReader = Fake;

            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 1, 21, false));
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 1, 14, false), "two regions but region 1 is not 0F000A40");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 1, 17, false), "one region");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 2, 14, false), "one region on cloth");
        }

        [TestMethod]
        public void Accepts_NoClothingBase_OwnPaletteRows_Refused_UnreadableAllowed()
        {
            PerennialDyes.TableReader = Fake;

            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 21, null, 21, false), "no ClothingBase");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, null, 21, false), "no ClothingBase");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 21, 1, 14, true), "own palette rows");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 1, 21, true), "own palette rows");

            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 21, 99, 14, false), "unknown table (reader returns null) is allowed");
            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 99, 14, false), "unknown table is allowed for a shell dye too");

            PerennialDyes.TableReader = id => throw new InvalidOperationException("dat failure");
            PerennialDyes.ClearCache();
            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 21, 1, 14, false), "a reader that throws is allowed");
        }

        [TestMethod]
        public void Accepts_CachesOnlySuccessfulReads()
        {
            var calls = 0;
            PerennialDyes.TableReader = id => { calls++; return calls == 1 ? null : Fake(1); };
            PerennialDyes.ClearCache();

            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 93, 1, 14, false), "first read fails: allowed, not cached");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 93, 1, 14, false), "retried and now refused");
            PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 93, 1, 14, false);
            Assert.AreEqual(2, calls, "the successful read is cached");
        }

        [TestMethod]
        public void RefusalMessage_TemplateAndShell_AreTheOwnerTexts()
        {
            Assert.AreEqual("The Perennial Hive Full Dye will not take on your Leather Gloves. Nothing has changed, and you keep the dye.",
                PerennialDyes.RefusalMessage(PerennialDyes.DyeCheck.Template, "Perennial Hive Full Dye", "Leather Gloves"));
            Assert.AreEqual("Your Leather Gloves has no shell this dye can colour. Nothing has changed, and you keep the dye.",
                PerennialDyes.RefusalMessage(PerennialDyes.DyeCheck.Shell, "Perennial Bone Olthoi Shell Dye", "Leather Gloves"));
        }

        [TestMethod]
        public void Appraisal_Template_CountsPiecesAndNamesTheOnesThatCannot()
        {
            PerennialDyes.TableReader = Fake;
            var worn = new List<PerennialDyes.WornPiece>
            {
                new PerennialDyes.WornPiece("Amuli Helm", 1, 14),
                new PerennialDyes.WornPiece("Cloth Robe", 2, 14),
                new PerennialDyes.WornPiece("Painted Cuirass", 1, 14, hasOwnPalette: true),
            };

            var lines = PerennialDyes.BuildAppraisalLines(PerennialDyes.DyeCheck.Template, 21, worn);

            CollectionAssert.AreEqual(new[] { "Your worn armor: 1 of 3 dyeable pieces can take this colour.", "Cannot: Cloth Robe, Painted Cuirass." }, lines);
        }

        [TestMethod]
        public void Appraisal_Shell_SaysShellColour_AndOmitsTheCannotLineWhenAllTake()
        {
            PerennialDyes.TableReader = Fake;

            var mixed = PerennialDyes.BuildAppraisalLines(PerennialDyes.DyeCheck.Shell, null,
                new List<PerennialDyes.WornPiece> { new PerennialDyes.WornPiece("Amuli Helm", 1, 21), new PerennialDyes.WornPiece("Amuli Gloves", 1, 14) });
            CollectionAssert.AreEqual(new[] { "Your worn armor: 1 of 2 dyeable pieces can take this shell colour.", "Cannot: Amuli Gloves." }, mixed);

            var all = PerennialDyes.BuildAppraisalLines(PerennialDyes.DyeCheck.Shell, null, new List<PerennialDyes.WornPiece> { new PerennialDyes.WornPiece("Amuli Helm", 1, 21) });
            CollectionAssert.AreEqual(new[] { "Your worn armor: 1 of 1 dyeable pieces can take this shell colour." }, all);
        }

        [TestMethod]
        public void Appraisal_NothingWorn_SaysToWearArmor()
        {
            var lines = PerennialDyes.BuildAppraisalLines(PerennialDyes.DyeCheck.Template, 21, new List<PerennialDyes.WornPiece>());

            CollectionAssert.AreEqual(new[] { "Wear your armor and appraise this again to see which pieces it will take." }, lines);
        }

        [TestMethod]
        public void Appraisal_AndGuard_AgreePieceForPiece()
        {
            // The appraisal must never promise what the guard refuses: for every (template, piece) combination the two answers are the same call.
            PerennialDyes.TableReader = Fake;
            foreach (var check in new[] { PerennialDyes.DyeCheck.Template, PerennialDyes.DyeCheck.Shell })
                foreach (var template in new int?[] { 14, 21, 17, 93 })
                    foreach (var clothingBase in new uint?[] { 1, 2, 99, null })
                        foreach (var ownPalette in new[] { false, true })
                        {
                            var guard = PerennialDyes.Accepts(check, 21, clothingBase, template, ownPalette);
                            var lines = PerennialDyes.BuildAppraisalLines(check, 21, new List<PerennialDyes.WornPiece> { new PerennialDyes.WornPiece("P", clothingBase, template, ownPalette) });

                            Assert.AreEqual(guard, !lines.Any(l => l.StartsWith("Cannot")), $"{check} template {template} base {clothingBase} palette {ownPalette}");
                        }
        }

        // ------------------------------------------------------------------
        // Source pins
        // ------------------------------------------------------------------

        private static string RepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n");
            }

            throw new FileNotFoundException($"Could not find {relative} by walking up from {AppContext.BaseDirectory} (run in-tree, never with --artifacts-path)");
        }

        [TestMethod]
        public void GuardPlacement_AfterGetRecipe_BeforeVerifyRequirements_InUseObjectOnTarget()
        {
            var src = RepoFile("Source/ACE.Server/Managers/RecipeManager.cs");
            var start = src.IndexOf("public static void UseObjectOnTarget(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "UseObjectOnTarget not found - re-aim this pin");
            var body = src.Substring(start);

            var getRecipe = body.IndexOf("var recipe = GetRecipe(player, source, target);", StringComparison.Ordinal);
            // The call must BE the whole condition of its `if`, on its own line: not nested in a constant-false (`false && ...`) or otherwise combined condition.
            var guardLine = Regex.Match(body, @"^[ \t]*if \(PerennialDyes\.TryRefuse\(recipe, source, target, out var dyeRefusal\)\)[ \t]*$", RegexOptions.Multiline);
            Assert.IsTrue(guardLine.Success, "the refusal must be exactly `if (PerennialDyes.TryRefuse(recipe, source, target, out var dyeRefusal))` with nothing else in the condition");
            var guard = guardLine.Success ? guardLine.Index : -1;
            var verify = body.IndexOf("if (!VerifyRequirements(recipe, player, source, target))", StringComparison.Ordinal);

            Assert.IsTrue(getRecipe >= 0 && guard >= 0 && verify >= 0, $"anchors missing (GetRecipe {getRecipe}, guard {guard}, VerifyRequirements {verify})");
            Assert.IsTrue(getRecipe < guard && guard < verify, "the refusal guard must run after GetRecipe and before VerifyRequirements");

            var after = body.Substring(guard, verify - guard);
            Assert.IsFalse(Regex.IsMatch(after, @"\b(false|true)\b"), "no constant condition may sit between the guard and VerifyRequirements");
            StringAssert.Contains(after, "SendUseDoneEvent();", "a refusal ends the use event");
            StringAssert.Contains(after, "return;", "a refusal returns before HandleRecipe, so nothing changes and nothing is consumed");
            StringAssert.Contains(after, "ChatMessageType.Craft", "the refusal is a Craft line");
        }

        [TestMethod]
        public void Routing_GetNewRecipe_RoutesForkKindsAheadOfTheWeenieClassNameSwitch()
        {
            var src = RepoFile("Source/ACE.Server/Managers/RecipeManager_New.cs");
            var route = src.IndexOf("PerennialDyes.RecipeIdFor(perennialKind.Value)", StringComparison.Ordinal);
            var sw = src.IndexOf("switch ((WeenieClassName)source.WeenieClassId)", StringComparison.Ordinal);

            Assert.IsTrue(route >= 0 && sw >= 0 && route < sw, "fork dyes are fork wcids and cannot be WeenieClassName labels: they must route before the switch");
        }

        [TestMethod]
        public void Tailoring_CopiesDyeShellShade_NextToShade_InBothPlaces()
        {
            var src = RepoFile("Source/ACE.Server/Entity/Tailoring.cs");

            StringAssert.Contains(src, "target.Shade = source.Shade;\n            target.DyeShellShade = source.DyeShellShade;");
            StringAssert.Contains(src, "player.UpdateProperty(target, PropertyFloat.Shade, source.Shade);\n            player.UpdateProperty(target, PropertyFloat.DyeShellShade, source.DyeShellShade);");
        }

        [TestMethod]
        public void LookCopySites_CarryDyeShellShadeLikeShade2To4()
        {
            // /pvphue: capture (reset memory), look apply, reset restore; the world-look source; the team-hue kit swap and its LookOf.
            var cmds = RepoFile("Source/ACE.Server/Command/Handlers/PvpHueCommands.cs");
            StringAssert.Contains(cmds, "item.GetProperty(PropertyFloat.DyeShellShade));");
            StringAssert.Contains(cmds, "Restore(item, PropertyFloat.DyeShellShade, plan.Target.DyeShellShade);");
            StringAssert.Contains(cmds, "Restore(item, PropertyFloat.DyeShellShade, o.DyeShellShade);");
            StringAssert.Contains(RepoFile("Source/ACE.Server/Pvp/PvpWorldLooks.cs"), "DyeShellShade = weenie.GetProperty(PropertyFloat.DyeShellShade),");
            var hue = RepoFile("Source/ACE.Server/Pvp/Templates/PvpTeamHue.cs");
            StringAssert.Contains(hue, "Put(item.Floats, (int)PropertyFloat.DyeShellShade, t.DyeShellShade);");
            StringAssert.Contains(hue, "DyeShellShade = F(PropertyFloat.DyeShellShade),");
        }

        [TestMethod]
        public void Appraisal_IsWiredIntoAppraiseInfo()
        {
            StringAssert.Contains(RepoFile("Source/ACE.Server/Network/Structure/AppraiseInfo.cs"), "PerennialDyes.GetAppraisalLines(wo, examiner)");
        }
    }

    /// <summary>
    /// The content half: the 16 fork dye weenies, the two recipes, the Tailor rows, the price patch, the prod un-hold and the registry fragments, pinned
    /// against each other and against the code's kind and recipe ids. Read from Content/ by walking up from the test output (run in-tree).
    /// </summary>
    [TestClass]
    public class PerennialDyeContentTests
    {
        private const uint FirstWcid = 1008650;
        private const int Count = 16;

        private static readonly (string Name, int Kind, int Template, double? Shell)[] Expected =
        {
            ("Perennial Crimson Dye", 1, 14, null), ("Perennial Violet Dye", 1, 13, null), ("Perennial Verdant Dye", 1, 8, null), ("Perennial Gilded Dye", 1, 21, null),
            ("Perennial Burnished Dye", 1, 19, null), ("Perennial Tidewater Dye", 1, 22, null), ("Perennial Sulfur Full Dye", 1, 17, null), ("Perennial Hive Full Dye", 1, 27, null),
            ("Perennial Bone Olthoi Shell Dye", 2, 14, 0.0), ("Perennial Orchid Olthoi Shell Dye", 2, 14, 0.1), ("Perennial Lime Olthoi Shell Dye", 2, 14, 0.2), ("Perennial Saffron Olthoi Shell Dye", 2, 14, 0.3),
            ("Perennial Ember Olthoi Shell Dye", 2, 14, 0.4), ("Perennial Azure Olthoi Shell Dye", 2, 14, 0.5), ("Perennial Ice Olthoi Shell Dye", 2, 14, 0.7), ("Perennial Blood Olthoi Shell Dye", 2, 14, 0.8),
        };

        private static DirectoryInfo ContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "Could not find Content/sql/weenies by walking up from " + AppContext.BaseDirectory + " (run in-tree, never with --artifacts-path).");
            return new DirectoryInfo(Path.Combine(dir.FullName, "Content"));
        }

        private static string Read(string relative) => File.ReadAllText(Path.Combine(ContentDir().FullName, relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

        private static string DyeSql(uint wcid)
        {
            var files = new DirectoryInfo(Path.Combine(ContentDir().FullName, "sql", "weenies")).GetFiles(wcid + " *.sql");
            Assert.AreEqual(1, files.Length, $"expected exactly one weenie file for {wcid}, found {files.Length}");
            return File.ReadAllText(files[0].FullName).Replace("\r\n", "\n");
        }

        private static string StripComments(string sql) => Regex.Replace(sql, @"/\*.*?\*/|--[^\n]*", "", RegexOptions.Singleline);

        /// <summary>The (type, value) pairs of every row of `table` for the wcid, string literals blanked to their text.</summary>
        private static List<(int Type, string Value)> Rows(string sql, string table, uint wcid)
        {
            var body = StripComments(sql);
            var rows = new List<(int, string)>();

            // EVERY INSERT for the table, not just the first: a second INSERT carrying a forbidden row must not slip past a one-row-per-type check.
            foreach (Match m in Regex.Matches(body, @"INSERT INTO `" + table + @"`[^;]*?VALUES(?<v>(?:'(?:[^']|'')*'|[^;'])*);", RegexOptions.Singleline))
                foreach (Match r in Regex.Matches(m.Groups["v"].Value, @"\(\s*" + wcid + @"\s*,\s*(\d+)\s*,\s*('(?:[^']|'')*'|-?[0-9.]+)\s*\)"))
                    rows.Add((int.Parse(r.Groups[1].Value), r.Groups[2].Value.Trim('\'').Replace("''", "'")));
            return rows;
        }

        private static string One(List<(int Type, string Value)> rows, int type)
        {
            var hits = rows.Where(r => r.Type == type).ToList();
            Assert.AreEqual(1, hits.Count, $"expected exactly one row of type {type}, found {hits.Count}");
            return hits[0].Value;
        }

        [TestMethod]
        public void EveryForkDyeSql_CarriesItsKindTemplateShellPriceUnderlayAndIsNotSellable()
        {
            for (var i = 0; i < Count; i++)
            {
                var wcid = FirstWcid + (uint)i;
                var e = Expected[i];
                var sql = DyeSql(wcid);
                var ints = Rows(sql, "weenie_properties_int", wcid);
                var floats = Rows(sql, "weenie_properties_float", wcid);
                var bools = Rows(sql, "weenie_properties_bool", wcid);
                var strings = Rows(sql, "weenie_properties_string", wcid);
                var label = $"{wcid} {e.Name}";

                Assert.AreEqual(e.Name, One(strings, (int)PropertyString.Name), label);
                Assert.AreEqual(e.Kind.ToString(), One(ints, (int)PropertyInt.PerennialDyeKind), $"{label}: PerennialDyeKind");
                Assert.AreEqual(e.Template.ToString(), One(ints, (int)PropertyInt.PaletteTemplate), $"{label}: PaletteTemplate");
                Assert.AreEqual("250", One(ints, (int)PropertyInt.Value), $"{label}: Value");
                Assert.AreEqual("250", One(ints, (int)PropertyInt.StackUnitValue), $"{label}: StackUnitValue");
                Assert.AreEqual("0", One(bools, (int)PropertyBool.IsSellable), $"{label}: IsSellable");
                Assert.AreEqual(0, floats.Count(f => f.Type == (int)PropertyFloat.Shade), $"{label}: no Shade row: no recipe copies one (owner ruling 2026-11-26), a primary dye leaves the piece's own tone alone");

                // Icon underlay (PropertyDataId 52): primary dyes keep the retail dye underlay 0x06005B0C, the two full dyes (Sulfur, Hive) use rose 0x060011CB, shell dyes use 0x06006E89.
                var underlay = One(Rows(sql, "weenie_properties_d_i_d", wcid), (int)PropertyDataId.IconUnderlay);
                var fullDye = wcid == 1008656 || wcid == 1008657;   // Sulfur Full and Hive Full: the third group
                var wantUnderlay = e.Kind == PerennialDyes.KindShell ? 0x06006E89u : fullDye ? 0x060011CBu : 0x06005B0Cu;
                Assert.AreEqual(wantUnderlay, uint.Parse(underlay), $"{label}: icon underlay");
                if (fullDye)
                {
                    StringAssert.Contains(One(strings, (int)PropertyString.LongDesc), "A full dye replaces both colour layers, so a shell dye cannot be used on top of it; dyeing the piece again with any other colour brings back its own shell. A piece that cannot hold this colour refuses it:");
                    StringAssert.Contains(e.Name, "Full Dye");
                }
                else
                    Assert.IsFalse(One(strings, (int)PropertyString.LongDesc).Contains("A full dye"), $"{label}: only the two full dyes carry the full-dye sentence");

                if (e.Kind == PerennialDyes.KindShell)
                {
                    Assert.IsTrue(e.Name.EndsWith(" Olthoi Shell Dye"), $"{label}: shell dyes are named Perennial <Colour> Olthoi Shell Dye");
                    StringAssert.Contains(One(strings, (int)PropertyString.LongDesc), " Olthoi shell dye from the Hopeslayer's tailor.");
                }
                Assert.AreEqual("Use this on a piece of armor to dye it. It never runs out.", One(strings, (int)PropertyString.Use), label);
                Assert.IsTrue(One(strings, (int)PropertyString.LongDesc).Length > 40, $"{label}: LongDesc");
                Assert.IsTrue(Regex.IsMatch(StripComments(sql), @"VALUES \(" + wcid + @", '[^']+', 44,"), $"{label}: WeenieType 44 CraftTool");
                Assert.IsTrue(sql.Contains("-- @owner: pvp-arena-rewards") && sql.Contains("-- @requires-restart: no") && sql.Contains($"-- @invalidate: weenie:{wcid}"), $"{label}: unit header");

                var shell = floats.Where(f => f.Type == (int)PropertyFloat.DyeShellShade).ToList();
                if (e.Shell.HasValue)
                {
                    Assert.AreEqual(1, shell.Count, $"{label}: a shell dye carries DyeShellShade");
                    Assert.AreEqual(e.Shell.Value, double.Parse(shell[0].Value, System.Globalization.CultureInfo.InvariantCulture), 1e-9, $"{label}: DyeShellShade");
                }
                else
                {
                    Assert.AreEqual(0, shell.Count, $"{label}: a primary dye must not carry DyeShellShade");
                }
            }
        }

        [TestMethod]
        public void KindSet_InTheCode_TheSqlFiles_AndTheTailorRows_MatchExactly()
        {
            // SQL side: every weenie file under Content/sql/weenies that carries int 9111, by kind.
            var byKind = new Dictionary<uint, int>();
            foreach (var f in new DirectoryInfo(Path.Combine(ContentDir().FullName, "sql", "weenies")).GetFiles("*.sql"))
            {
                var text = File.ReadAllText(f.FullName);
                if (!Regex.IsMatch(text, @"\(\d+, 9111, \d+\)"))
                    continue;
                var wcid = uint.Parse(f.Name.Split(' ')[0]);
                byKind[wcid] = int.Parse(Regex.Match(text, @"\(" + wcid + @", 9111, (\d+)\)").Groups[1].Value);
            }

            var expected = Enumerable.Range(0, Count).ToDictionary(i => FirstWcid + (uint)i, i => Expected[i].Kind);
            CollectionAssert.AreEquivalent(expected.Keys.ToList(), byKind.Keys.ToList(), "the wcids carrying PerennialDyeKind in Content/ must be exactly the sixteen");
            foreach (var kv in expected)
                Assert.AreEqual(kv.Value, byKind[kv.Key], $"kind of {kv.Key}");
            CollectionAssert.IsSubsetOf(byKind.Values.Distinct().ToList(), new[] { PerennialDyes.KindPrimary, PerennialDyes.KindShell }, "every kind in the SQL is a kind the code routes");
            Assert.AreEqual(8, byKind.Values.Count(k => k == PerennialDyes.KindPrimary));
            Assert.AreEqual(8, byKind.Values.Count(k => k == PerennialDyes.KindShell));

            // Tailor side: the Tailor unit itself stocks all sixteen (destination 4, stack -1), once each, and the former stocking patch is gone.
            var tailor = StripComments(Read("sql/weenies/1006653 Hopeslayer's Tailor.sql"));
            var stockedInTailor = Regex.Matches(tailor, @"\(1006653, 4, (\d+), -1, 0, 0, False\)").Cast<Match>().Select(m => uint.Parse(m.Groups[1].Value)).Where(expected.ContainsKey).ToList();
            CollectionAssert.AreEquivalent(expected.Keys.ToList(), stockedInTailor, "un-held: the Tailor unit itself stocks the sixteen, once each");
            Assert.IsFalse(File.Exists(Path.Combine(ContentDir().FullName, "sql", "patches", "hopeslayer_tailor_fork_dyes.sql")), "the Tailor stocking patch was folded into the Tailor unit and deleted");

            // Prod: un-held (2026-11-26, owner ruling). No fork dye unit, the recipe unit or the old patch may sit in the prod manifest exclude list.
            var exclude = Read("prod-manifest-exclude.txt").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToHashSet();
            foreach (var w in expected.Keys)
                Assert.IsFalse(exclude.Any(l => l.StartsWith($"Content/sql/weenies/{w} ")), $"{w} must ship to prod (not in the exclude list)");
            Assert.IsFalse(exclude.Contains("Content/sql/recipes/PerennialDyeRecipes.sql"), "the recipe unit ships to prod too");
            Assert.IsFalse(exclude.Contains("Content/sql/patches/hopeslayer_tailor_fork_dyes.sql"), "the folded patch must not be re-held");

            Assert.IsFalse(exclude.Contains("Content/sql/weenies/1006653 Hopeslayer's Tailor.sql"), "the Tailor unit itself must stay shippable");
            Assert.IsFalse(exclude.Contains("Content/sql/patches/hopeslayer_perennial_dye_price.sql"), "the 250 price patch must stay shippable");
        }

        [TestMethod]
        public void Recipes_PrimaryCopiesTemplateOnly_ShellCopiesDyeShellShadeOnly_NoFailModNoDestroy()
        {
            var sql = Read("sql/recipes/PerennialDyeRecipes.sql");
            var body = StripComments(sql);

            Assert.AreEqual(PerennialDyes.PrimaryRecipeId, 1005510u);
            Assert.AreEqual(PerennialDyes.ShellRecipeId, 1005511u);

            // one success recipe_mod per recipe, nothing else
            var mods = Regex.Matches(body, @"INSERT INTO `recipe_mod` [^;]*?VALUES \((\d+), (\d+), 0, 0, 0, 0, 0, 0, 0\);");
            CollectionAssert.AreEqual(new[] { "1005510:1", "1005511:1" }, mods.Cast<Match>().Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value).ToList(), "exactly one recipe_mod per recipe, both executes_On_Success, no fail mod, no mutation script");

            var ints = Regex.Matches(body, @"INSERT INTO `recipe_mods_int`[^;]*?VALUES \(@parent_id, (\d+), (\d+), (-?\d+), (\d+), (\d+)\);").Cast<Match>().Select(m => (m.Groups[2].Value, m.Groups[4].Value, m.Groups[5].Value)).ToList();
            CollectionAssert.AreEqual(new[] { ("3", "3", "1") }, ints, "only PaletteTemplate, CopyFromSourceToTarget (3), from the dye (1), and only in the primary recipe");

            var floats = Regex.Matches(body, @"INSERT INTO `recipe_mods_float`[^;]*?VALUES \(@parent_id, (\d+), (\d+), ([0-9.]+), (\d+), (\d+)\);").Cast<Match>().Select(m => (m.Groups[2].Value, m.Groups[4].Value, m.Groups[5].Value)).ToList();
            CollectionAssert.AreEqual(new[] { ("9025", "3", "1") }, floats, "the primary recipe copies NO float (the piece keeps its Shade and DyeShellShade); only the shell recipe copies DyeShellShade from the dye");

            // the order inside the file: the primary's int follows the first recipe_mod, the shell's float the second
            var firstMod = body.IndexOf("VALUES (1005510, 1", StringComparison.Ordinal);
            var secondMod = body.IndexOf("VALUES (1005511, 1", StringComparison.Ordinal);
            Assert.IsTrue(firstMod > 0 && secondMod > firstMod);
            Assert.IsTrue(body.IndexOf("(@parent_id, 0, 3, 0, 3, 1)", StringComparison.Ordinal) > firstMod && body.IndexOf("(@parent_id, 0, 3, 0, 3, 1)", StringComparison.Ordinal) < secondMod);
            Assert.AreEqual(-1, body.IndexOf("(@parent_id, 0, 12,", StringComparison.Ordinal), "no Shade (float 12) copy anywhere");
            Assert.IsTrue(body.IndexOf("(@parent_id, 0, 9025, 0, 3, 1)", StringComparison.Ordinal) > secondMod);

            // the recipe rows: skill 39, difficulty 0, salvage 0, every destroy chance 0
            foreach (var id in new[] { "1005510", "1005511" })
            {
                var m = Regex.Match(body, @"\(" + id + @", 0, 39, 0, 0,\s*0, 0, '[^']*',\s*0, 0, NULL,\s*0, 0, NULL,\s*0, 0, NULL,\s*0, 0, NULL,\s*0, 0, NULL,\s*0, '[^']*'\)");
                Assert.IsTrue(m.Success, $"recipe {id}: skill 39, difficulty 0, no create/destroy on success or failure");
            }
        }

        [TestMethod]
        public void Unheld_TheTailorHoldsTheSixteenRowsAndThePatchIsGone()
        {
            var patchExists = File.Exists(Path.Combine(ContentDir().FullName, "sql", "patches", "hopeslayer_tailor_fork_dyes.sql"));
            Assert.IsFalse(patchExists, "the patch was folded into the Tailor unit and deleted");
            var tailor = StripComments(Read("sql/weenies/1006653 Hopeslayer's Tailor.sql"));
            for (var i = 0; i < Count; i++)
                Assert.IsTrue(Regex.IsMatch(tailor, @"\(1006653, 4, " + (FirstWcid + (uint)i) + @", -1, 0, 0, False\)"), $"the Tailor unit must contain the row for {FirstWcid + (uint)i}");
            var exclude = Read("prod-manifest-exclude.txt").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            Assert.IsFalse(exclude.Any(l => l.Contains("Perennial") || l.Contains("hopeslayer_tailor_fork_dyes")), "no Perennial dye unit or the old patch in the prod exclude list");
        }

        [TestMethod]
        public void TailorHeader_NoLongerPointsAtTheRetiredPatch()
        {
            var text = Read("sql/weenies/1006653 Hopeslayer's Tailor.sql");
            Assert.IsFalse(text.Contains("Re-apply patches/hopeslayer_tailor_fork_dyes"), "the patch is gone; the header must not tell anyone to re-apply it");
            StringAssert.Contains(text, "stocked by this unit itself");
        }

        [TestMethod]
        public void PricePatch_IsFiftyPerRetailDye_AndTheTailorHeaderNoLongerSaysSevenHundred()
        {
            var patch = StripComments(Read("sql/patches/hopeslayer_perennial_dye_price.sql"));
            Assert.AreEqual(0, Regex.Matches(patch, @"\b700\b").Count);
            Assert.AreEqual(18, Regex.Matches(patch, @", (15|19), 250\)").Count, "nine dyes x (StackUnitValue, Value) at 250");

            var tailor = Read("sql/weenies/1006653 Hopeslayer's Tailor.sql");
            Assert.IsFalse(Regex.IsMatch(tailor, @"Perennial dyes 700|at 700 Marks|\(700, a RETAIL"), "the Tailor header text must not still quote the old 700 price");
        }

        [TestMethod]
        public void RegistryFragments_ReserveTheWcidBlockAndTheRecipeIds()
        {
            var wcid = Read("wcid-registry.d/wcid-1008650.tsv").Split('\t');
            Assert.AreEqual("wcid", wcid[0]);
            Assert.AreEqual("1008650", wcid[1]);
            Assert.AreEqual("1008699", wcid[2]);
            Assert.AreEqual("pvp-arena-rewards", wcid[4]);

            var recipe = Read("wcid-registry.d/recipe-1005510.tsv").Split('\t');
            Assert.AreEqual("recipe", recipe[0]);
            Assert.AreEqual("1005510", recipe[1]);
            Assert.AreEqual("1005519", recipe[2]);
        }
    }
}
