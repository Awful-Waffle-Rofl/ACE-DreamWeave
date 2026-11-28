using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Recipe = ACE.Database.Models.World.Recipe;
using Weenie = ACE.Entity.Models.Weenie;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Shared harness for the Perennial dye tests that need real dats and real WorldObjects: loads client_portal.dat once (Inconclusive
    /// without ACE_DAT_PATH), makes GuidManager constructible, and seeds weenies and recipes into the world cache by reflection.
    /// Same techniques as VaultCollapseTests; kept local so the two classes do not depend on each other.
    /// </summary>
    internal static class PerennialDyeHarness
    {
        private static readonly object datInitLock = new object();
        private static bool datInitialized;
        private static string datSkipReason;
        private static bool guidManagerSeeded;

        public static void RequireDats()
        {
            lock (datInitLock)
            {
                if (!datInitialized)
                {
                    datInitialized = true;
                    datSkipReason = InitializeDats();
                }
            }

            if (datSkipReason != null)
                Assert.Inconclusive(datSkipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();
            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);
            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));
            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }

        public static void EnsureGuidManagerConstructible()
        {
            if (guidManagerSeeded)
                return;

            var allocatorType = typeof(GuidManager).GetNestedType("DynamicGuidAllocator", BindingFlags.NonPublic);
            Assert.IsNotNull(allocatorType, "GuidManager.DynamicGuidAllocator was not found by reflection - has it been renamed?");

            var existing = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(existing, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");

            if (existing.GetValue(null) == null)
            {
                var allocator = FormatterServices.GetUninitializedObject(allocatorType);

                void SetField(string name, object value) =>
                    allocatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(allocator, value);

                SetField("min", ObjectGuid.DynamicMin);
                SetField("max", ObjectGuid.DynamicMax);
                SetField("current", 0x7D000000u);
                SetField("name", "test-dynamic");
                SetField("recycledGuids", Activator.CreateInstance(typeof(Queue<Tuple<DateTime, uint>>)));
                SetField("availableIDs", Activator.CreateInstance(allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType));
                SetField("useSequenceGapExhaustedMessageDisplayed", false);

                existing.SetValue(null, allocator);
            }

            guidManagerSeeded = true;
        }

        private static ConcurrentDictionary<uint, Weenie> WeenieCache()
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            return (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
        }

        private static Dictionary<uint, Recipe> RecipeCache()
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("recipeCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.recipeCache was not found by reflection - has it been renamed?");
            return (Dictionary<uint, Recipe>)field.GetValue(DatabaseManager.World);
        }

        public static void SeedWeenie(Weenie weenie) => WeenieCache()[weenie.WeenieClassId] = weenie;

        public static void UnseedWeenie(uint wcid) => WeenieCache().TryRemove(wcid, out _);

        public static void SeedRecipe(uint id, Recipe recipe)
        {
            var cache = RecipeCache();
            lock (cache)
                cache[id] = recipe;
        }

        public static void UnseedRecipe(uint id)
        {
            var cache = RecipeCache();
            lock (cache)
                cache.Remove(id);
        }

        /// <summary>A recipe whose success mod copies the given int and float stats from the source, like the fork dye recipes.</summary>
        public static Recipe CopyRecipe(uint id, int[] intStats, int[] floatStats)
        {
            var mod = new RecipeMod { ExecutesOnSuccess = true };
            foreach (var s in intStats)
                mod.RecipeModsInt.Add(new RecipeModsInt { Stat = s, Enum = (int)ModificationOperation.CopyFromSourceToTarget, Source = (int)RecipeSourceType.Source });
            foreach (var s in floatStats)
                mod.RecipeModsFloat.Add(new RecipeModsFloat { Stat = s, Enum = (int)ModificationOperation.CopyFromSourceToTarget, Source = (int)RecipeSourceType.Source });

            var recipe = new Recipe { Id = id, Skill = 39 };
            recipe.RecipeMod.Add(mod);
            return recipe;
        }

        /// <summary>A Clothing weenie on <paramref name="clothingBase"/> (setup = the table's first), carrying the given template, shade and shell shade.</summary>
        public static Weenie ClothingWeenie(uint wcid, uint clothingBase, int? template, double? shade, double? shellShade, bool dyable = true)
        {
            var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(clothingBase);
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "perennialdyeprobe" + wcid,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.EncumbranceVal, 10 } },
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Dyable, dyable } },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, table.ClothingBaseEffects.Keys.First() },
                    { PropertyDataId.ClothingBase, clothingBase },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Dye Probe " + wcid } },
            };

            if (template.HasValue)
                weenie.PropertiesInt[PropertyInt.PaletteTemplate] = template.Value;
            if (shade.HasValue)
                weenie.PropertiesFloat[PropertyFloat.Shade] = shade.Value;
            if (shellShade.HasValue)
                weenie.PropertiesFloat[PropertyFloat.DyeShellShade] = shellShade.Value;

            return weenie;
        }

        /// <summary>A dye CraftTool weenie carrying a template, shade and optionally a kind and shell shade.</summary>
        public static Weenie DyeWeenie(uint wcid, int? kind, int template, double? shade, double? shellShade)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "perennialdyesource" + wcid,
                WeenieType = WeenieType.CraftTool,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PaletteTemplate, template }, { PropertyInt.EncumbranceVal, 5 } },
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Dye Source " + wcid } },
            };

            if (kind.HasValue)
                weenie.PropertiesInt[PropertyInt.PerennialDyeKind] = kind.Value;
            if (shade.HasValue)
                weenie.PropertiesFloat[PropertyFloat.Shade] = shade.Value;
            if (shellShade.HasValue)
                weenie.PropertiesFloat[PropertyFloat.DyeShellShade] = shellShade.Value;

            return weenie;
        }
    }

    /// <summary>
    /// What the client receives for a piece carrying DyeShellShade: the item's own CalculateObjDesc (the only render path a test can reach;
    /// the worn path needs a live Player and is pinned by a source scan instead), plus the guard and routing driven with real WorldObjects.
    /// RequiresDatFiles tests are Inconclusive without ACE_DAT_PATH.
    /// </summary>
    [TestClass]
    public class PerennialDyeWorldTests
    {
        private const uint OlthoiTable = 0x10000733;
        private const uint WithShade = 990201;
        private const uint WithoutShade = 990202;
        private const uint SourceWcid = 990203;
        private const uint TargetWcid = 990204;

        private static readonly Func<uint, IReadOnlyDictionary<uint, uint[]>> ProductionReader = PerennialDyes.TableReader;
        private readonly List<uint> seeded = new List<uint>();

        [TestCleanup]
        public void Cleanup()
        {
            PerennialDyes.TableReader = ProductionReader;
            PerennialDyes.ClearCache();
            foreach (var w in seeded)
                PerennialDyeHarness.UnseedWeenie(w);
            PerennialDyeHarness.UnseedRecipe(PerennialDyes.PrimaryRecipeId);
            PerennialDyeHarness.UnseedRecipe(PerennialDyes.ShellRecipeId);
            seeded.Clear();
        }

        private WorldObject Make(Weenie weenie)
        {
            PerennialDyeHarness.SeedWeenie(weenie);
            seeded.Add(weenie.WeenieClassId);
            var wo = WorldObjectFactory.CreateNewWorldObject(weenie.WeenieClassId);
            Assert.IsNotNull(wo, "the probe weenie must materialize, or nothing below is being tested");
            return wo;
        }

        private static List<(ushort Pal, ushort Offset, ushort Length)> Expected(ClothingTable table, uint template, Func<int, float> shadeFor)
        {
            var result = new List<(ushort, ushort, ushort)>();
            var sub = table.ClothingSubPalEffects[template];
            for (int i = 0; i < sub.CloSubPalettes.Count; i++)
            {
                var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(sub.CloSubPalettes[i].PaletteSet);
                var pal = (ushort)set.GetPaletteID(shadeFor(i));
                foreach (var r in sub.CloSubPalettes[i].Ranges)
                    result.Add((pal, (ushort)(r.Offset / 8), (ushort)(r.NumColors / 8)));
            }
            return result;
        }

        private static List<(ushort, ushort, ushort)> Actual(ACE.Entity.ObjDesc d) => d.SubPalettes.Select(p => ((ushort)p.SubPaletteId, (ushort)p.Offset, (ushort)p.Length)).ToList();

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Render_Region1UsesDyeShellShade_EveryOtherRegionKeepsShade()
        {
            PerennialDyeHarness.RequireDats();
            PerennialDyeHarness.EnsureGuidManagerConstructible();

            var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(OlthoiTable);
            var sub = table.ClothingSubPalEffects[21];
            Assert.IsTrue(sub.CloSubPalettes.Count >= 2 && sub.CloSubPalettes[1].PaletteSet == PerennialDyes.ShellPaletteSet, "precondition: template 21 on 0x10000733 has a shell region");

            var set0 = DatManager.PortalDat.ReadFromDat<PaletteSet>(sub.CloSubPalettes[0].PaletteSet);
            var shell = DatManager.PortalDat.ReadFromDat<PaletteSet>(PerennialDyes.ShellPaletteSet);
            Assert.AreNotEqual((ushort)shell.GetPaletteID(1.0f), (ushort)shell.GetPaletteID(0.2f), "precondition: shade 1.0 and 0.2 pick different shell palettes, or this test cannot fail");

            var item = Make(PerennialDyeHarness.ClothingWeenie(WithShade, OlthoiTable, 21, 1.0, 0.2));
            var actual = Actual(item.CalculateObjDesc());
            var expected = Expected(table, 21, i => i == 1 ? 0.2f : 1.0f);

            CollectionAssert.AreEqual(expected, actual, "region 0 must use Shade 1.0 and region 1 (the shell) DyeShellShade 0.2");
            Assert.IsTrue(actual.Any(a => a.Item1 == (ushort)set0.GetPaletteID(1.0f)), "region 0's palette is set(region0).GetPaletteID(1.0)");
            Assert.IsTrue(actual.Any(a => a.Item1 == (ushort)shell.GetPaletteID(0.2f)), "region 1's palette is 0x0F000A40.GetPaletteID(0.2)");
            Assert.IsFalse(actual.Any(a => a.Item1 == (ushort)shell.GetPaletteID(1.0f) && sub.CloSubPalettes[1].Ranges.Any(r => (ushort)(r.Offset / 8) == a.Item2)), "the shell must NOT also render at Shade");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Render_WithoutDyeShellShade_Region1FollowsShade()
        {
            PerennialDyeHarness.RequireDats();
            PerennialDyeHarness.EnsureGuidManagerConstructible();

            var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(OlthoiTable);
            var item = Make(PerennialDyeHarness.ClothingWeenie(WithoutShade, OlthoiTable, 21, 1.0, null));

            CollectionAssert.AreEqual(Expected(table, 21, i => 1.0f), Actual(item.CalculateObjDesc()), "with no DyeShellShade every region uses Shade, exactly as before");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Render_ShellShadeZero_IsAValueNotAbsent()
        {
            PerennialDyeHarness.RequireDats();
            PerennialDyeHarness.EnsureGuidManagerConstructible();

            var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(OlthoiTable);
            var item = Make(PerennialDyeHarness.ClothingWeenie(WithShade, OlthoiTable, 21, 1.0, 0.0));

            CollectionAssert.AreEqual(Expected(table, 21, i => i == 1 ? 0.0f : 1.0f), Actual(item.CalculateObjDesc()), "Bone (0.0) must colour the shell at 0.0, not fall back to Shade");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Render_ShellDyeOnAPieceWithNeitherTemplateNorShade_StillRendersTheShell()
        {
            PerennialDyeHarness.RequireDats();
            PerennialDyeHarness.EnsureGuidManagerConstructible();

            // 22 dyeable retail weenies carry neither PaletteTemplate nor Shade. The own-object path used to skip all palette work for them, so a shell dye
            // showed worn (that path has no such gate) but not in the pack, on the ground or in a trade.
            var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(OlthoiTable);
            var firstKey = table.ClothingSubPalEffects.Keys.First();
            var sub = table.ClothingSubPalEffects[firstKey];
            Assert.IsTrue(sub.CloSubPalettes.Count >= 2 && sub.CloSubPalettes[1].PaletteSet == PerennialDyes.ShellPaletteSet, $"precondition: the first template ({firstKey}) of 0x10000733 has a shell region");

            var weenie = PerennialDyeHarness.ClothingWeenie(WithShade, OlthoiTable, null, null, 0.2);
            const uint KnownIcon = 0x06001234;
            Assert.IsTrue(sub.Icon == 0 || sub.Icon != KnownIcon, "precondition: the table icon differs from the known icon, or the icon assertion cannot fail");
            weenie.PropertiesDID[PropertyDataId.Icon] = KnownIcon;
            var item = Make(weenie);
            Assert.IsNull(item.PaletteTemplate, "precondition: no PaletteTemplate");
            Assert.IsNull(item.Shade, "precondition: no Shade");
            Assert.AreEqual(KnownIcon, item.IconId, "precondition: the known icon is set");

            CollectionAssert.AreEqual(Expected(table, firstKey, i => i == 1 ? 0.2f : 0f), Actual(item.CalculateObjDesc()),
                "first-key template, region 0 at the default shade 0, region 1 at DyeShellShade 0.2");

            Assert.AreEqual(KnownIcon, item.IconId, "a shell-dye-only piece must never get the table's first-key icon persisted");
            Assert.IsTrue(sub.Icon > 0, "precondition: the first template carries an icon, or the assertion above proves nothing");

            var plain = Make(PerennialDyeHarness.ClothingWeenie(WithoutShade, OlthoiTable, null, null, null));
            Assert.AreEqual(0, plain.CalculateObjDesc().SubPalettes.Count, "positive control: with none of the three, no palette work, exactly as before");
        }

        [TestMethod]
        public void WornPath_ReadsTheSameShadeForRegionHelper_SourcePin()
        {
            // Creature.CalculateObjDesc needs a live Player and EquippedObjects, so no harness reaches the worn path; pin the call instead.
            var worn = File.ReadAllText(RepoPath("Source/ACE.Server/WorldObjects/Creature_Networking.cs"));
            StringAssert.Contains(worn, "var shellShade = w.DyeShellShade;");
            StringAssert.Contains(worn, "PerennialDyes.ShadeForRegion(i, shade, shellShade)");

            var own = File.ReadAllText(RepoPath("Source/ACE.Server/WorldObjects/WorldObject_Networking.cs"));
            StringAssert.Contains(own, "PerennialDyes.ShadeForRegion(i, shade, shellShade)");
        }

        // ------------------------------------------------------------------
        // Guard, driven with real WorldObjects and the seam
        // ------------------------------------------------------------------

        private static readonly uint[] ShellSetB = { 0x0F000001, PerennialDyes.ShellPaletteSet };

        private static IReadOnlyDictionary<uint, uint[]> FakeTable() => new Dictionary<uint, uint[]>
        {
            { 14, new uint[] { 0x0F000001, 0x0F000002 } },
            { 21, new uint[] { 0x0F000001, PerennialDyes.ShellPaletteSet } },
            { 17, new uint[] { 0x0F000001 } },
        };

        [TestMethod]
        public void TryRefuse_TemplateDye_RefusedWhenTheTableLacksTheTemplate_AcceptedWhenItHasIt()
        {
            PerennialDyeHarness.EnsureGuidManagerConstructible();
            PerennialDyes.TableReader = id => FakeTable();
            PerennialDyes.ClearCache();
            var recipe = PerennialDyeHarness.CopyRecipe(PerennialDyes.PrimaryRecipeId, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            var target = MakeFake(TargetWcid, 0x10000001, 14, null);

            Assert.IsFalse(PerennialDyes.TryRefuse(recipe, MakeFake(SourceWcid, null, 21, null, dye: true), target, out var msg), "template 21 is in the table");
            Assert.IsNull(msg);

            var refused = PerennialDyes.TryRefuse(recipe, MakeFake(SourceWcid + 10, null, 93, null, dye: true), target, out msg);
            Assert.IsTrue(refused, "template 93 is not in the table");
            Assert.AreEqual("The Dye Source 990213 will not take on your Dye Probe 990204. Nothing has changed, and you keep the dye.", msg);
        }

        [TestMethod]
        public void TryRefuse_ShellDye_RefusedOnOneRegionAndNonShellTemplates_AcceptedOnAShellTemplate()
        {
            PerennialDyeHarness.EnsureGuidManagerConstructible();
            PerennialDyes.TableReader = id => FakeTable();
            PerennialDyes.ClearCache();
            var recipe = PerennialDyeHarness.CopyRecipe(PerennialDyes.ShellRecipeId, new int[0], new[] { (int)PropertyFloat.DyeShellShade });
            var source = MakeFake(SourceWcid, null, 14, 0.5, dye: true);

            Assert.IsFalse(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid, 0x10000001, 21, null), out _), "template 21: region 1 is 0F000A40");
            Assert.IsTrue(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid + 1, 0x10000001, 17, null), out var msg), "template 17 has one region");
            Assert.AreEqual("Your Dye Probe 990205 has no shell this dye can colour. Nothing has changed, and you keep the dye.", msg);
            Assert.IsTrue(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid + 2, 0x10000001, 14, null), out _), "template 14: region 1 is not 0F000A40");
            Assert.IsTrue(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid + 3, 0x10000001, 99, null), out _), "an unknown template falls back to the table's FIRST entry (14, no shell), as both renderers do: refused");
        }

        [TestMethod]
        public void TryRefuse_NoClothingBase_AndOwnPaletteRows_AreRefused_UnreadableTableIsAllowed()
        {
            PerennialDyeHarness.EnsureGuidManagerConstructible();
            PerennialDyes.TableReader = id => FakeTable();
            PerennialDyes.ClearCache();
            var recipe = PerennialDyeHarness.CopyRecipe(PerennialDyes.PrimaryRecipeId, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            var source = MakeFake(SourceWcid, null, 21, null, dye: true);

            Assert.IsTrue(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid, null, 21, null), out _), "no ClothingBase");

            var withRows = MakeFake(TargetWcid + 1, 0x10000001, 21, null);
            Assert.IsFalse(PerennialDyes.TryRefuse(recipe, source, withRows, out _), "positive control: the same piece without palette rows is accepted");
            withRows.Biota.PropertiesPalette = new List<PropertiesPalette> { new PropertiesPalette { SubPaletteId = 0x04000001, Offset = 0, Length = 8 } };
            Assert.IsTrue(PerennialDyes.TryRefuse(recipe, source, withRows, out _), "a piece with its own palette rows");

            PerennialDyes.TableReader = id => null;
            PerennialDyes.ClearCache();
            Assert.IsFalse(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid + 2, 0x10000002, 93, null), out _), "an unreadable table is ALLOWED");

            PerennialDyes.TableReader = id => throw new InvalidOperationException("dat failure");
            PerennialDyes.ClearCache();
            Assert.IsFalse(PerennialDyes.TryRefuse(recipe, source, MakeFake(TargetWcid + 3, 0x10000003, 93, null), out _), "a throwing reader is ALLOWED too");
        }

        [TestMethod]
        public void TryRefuse_ANonDyeRecipe_IsNeverRefused()
        {
            PerennialDyeHarness.EnsureGuidManagerConstructible();
            PerennialDyes.TableReader = id => FakeTable();
            PerennialDyes.ClearCache();
            var recipe = PerennialDyeHarness.CopyRecipe(5, new[] { (int)PropertyInt.Value }, new[] { (int)PropertyFloat.Shade });

            Assert.IsFalse(PerennialDyes.TryRefuse(recipe, MakeFake(SourceWcid, null, 93, 1.0, dye: true), MakeFake(TargetWcid, null, 21, null), out _), "copies neither PaletteTemplate nor DyeShellShade");
        }

        private WorldObject MakeFake(uint wcid, uint? clothingBase, int template, double? shade, bool dye = false)
        {
            var weenie = dye
                ? PerennialDyeHarness.DyeWeenie(wcid, null, template, shade, null)
                : new Weenie
                {
                    WeenieClassId = wcid,
                    ClassName = "perennialdyefake" + wcid,
                    WeenieType = WeenieType.Clothing,
                    PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PaletteTemplate, template }, { PropertyInt.EncumbranceVal, 10 } },
                    PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Dyable, true } },
                    PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Dye Probe " + wcid } },
                };

            if (clothingBase.HasValue)
            {
                weenie.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, clothingBase.Value } };
            }

            return Make(weenie);
        }

        // ------------------------------------------------------------------
        // Dat-backed guard cases
        // ------------------------------------------------------------------

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DatBacked_TemplateDye_RefusedWhereTheTableLacksTheTemplate()
        {
            PerennialDyeHarness.RequireDats();
            PerennialDyes.ClearCache();

            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 93, 0x10000533, 14, false), "retail dye template 93 on Envoy's Heaume (0x10000533) is refused");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 91, 0x100002D5, 14, false), "template 91 on PvP Fletcher's Cap (0x100002D5) is refused");
            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 84, 0x100002D5, 14, false), "template 84 on 0x100002D5 is accepted");
            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Template, 93, OlthoiTable, 14, false), "positive control: 93 on an Olthoi table is accepted");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DatBacked_ShellDye_AcceptedOnOlthoiShell_RefusedOnOneRegionAndOtherArmor()
        {
            PerennialDyeHarness.RequireDats();
            PerennialDyes.ClearCache();

            Assert.IsTrue(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, OlthoiTable, 21, false), "shell dye on 0x10000733 template 21");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, OlthoiTable, 17, false), "shell dye on 0x10000733 template 17 (one region covering both ranges)");
            Assert.IsFalse(PerennialDyes.Accepts(PerennialDyes.DyeCheck.Shell, null, 0x100000A4, 14, false), "shell dye on 0x100000A4 (no shell layer)");
        }

        // ------------------------------------------------------------------
        // Routing
        // ------------------------------------------------------------------

        [TestMethod]
        public void Routing_ForkKindsRouteToTheirOwnRecipe_ForAClothingDyableTarget_OtherwiseNull()
        {
            PerennialDyeHarness.EnsureGuidManagerConstructible();
            var primary = PerennialDyeHarness.CopyRecipe(PerennialDyes.PrimaryRecipeId, new[] { (int)PropertyInt.PaletteTemplate }, new int[0]);
            var shell = PerennialDyeHarness.CopyRecipe(PerennialDyes.ShellRecipeId, new int[0], new[] { (int)PropertyFloat.DyeShellShade });
            PerennialDyeHarness.SeedRecipe(PerennialDyes.PrimaryRecipeId, primary);
            PerennialDyeHarness.SeedRecipe(PerennialDyes.ShellRecipeId, shell);

            var armor = Make(new Weenie
            {
                WeenieClassId = TargetWcid, ClassName = "perennialdyetarget", WeenieType = WeenieType.Clothing,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Dyable, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Probe Armor" } },
            });
            var notDyable = Make(new Weenie
            {
                WeenieClassId = TargetWcid + 1, ClassName = "perennialdyenotdyable", WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Probe Plain" } },
            });

            var primarySource = Make(PerennialDyeHarness.DyeWeenie(SourceWcid, PerennialDyes.KindPrimary, 14, 1.0, null));
            var shellSource = Make(PerennialDyeHarness.DyeWeenie(SourceWcid + 1, PerennialDyes.KindShell, 14, 1.0, 0.3));

            Assert.AreSame(primary, RecipeManager.GetNewRecipe(null, primarySource, armor), "kind 1 routes to the primary recipe");
            Assert.AreSame(shell, RecipeManager.GetNewRecipe(null, shellSource, armor), "kind 2 routes to the shell recipe");
            Assert.IsNull(RecipeManager.GetNewRecipe(null, primarySource, notDyable), "a non-Dyable target gets no recipe");
            Assert.IsNull(RecipeManager.GetNewRecipe(null, shellSource, notDyable), "a non-Dyable target gets no recipe");

            seeded.Add(SourceWcid + 1);
        }

        [TestMethod]
        public void Routing_KindOf_OnlyOneAndTwoRoute()
        {
            PerennialDyeHarness.EnsureGuidManagerConstructible();

            Assert.IsNull(PerennialDyes.KindOf(Make(PerennialDyeHarness.DyeWeenie(SourceWcid, null, 14, 1.0, null))));
            Assert.IsNull(PerennialDyes.KindOf(Make(PerennialDyeHarness.DyeWeenie(SourceWcid + 1, 3, 14, 1.0, null))));
            Assert.AreEqual(1, PerennialDyes.KindOf(Make(PerennialDyeHarness.DyeWeenie(SourceWcid + 2, 1, 14, 1.0, null))));
            Assert.AreEqual(2, PerennialDyes.KindOf(Make(PerennialDyeHarness.DyeWeenie(SourceWcid + 3, 2, 14, 1.0, 0.3))));
        }

        // ------------------------------------------------------------------

        private static string RepoPath(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
        }
    }
}
