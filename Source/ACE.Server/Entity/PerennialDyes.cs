using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Perennial dyes (Docs/Pvp/DYES.md): the two fork dye kinds, the refusal guard every template-copying dye recipe runs through,
    /// the shell-layer shade the renderers read, and the appraisal lines that say what a dye will take on the examiner's worn armor.
    ///
    /// COLOUR MODEL. An armor piece stores PaletteTemplate (int 3) and Shade (float 12). Its ClothingTable maps a template to 1..6
    /// regions (CloSubPalettes), each with a PaletteSet, and ONE Shade picks the palette in every region. DyeShellShade (float 9025)
    /// replaces Shade for region index 1 only (<see cref="ShadeForRegion"/>), which on Olthoi Amuli, Koujia and Celdon tables is the
    /// shared set 0F000A40 (the shell, or the Celdon inlay).
    ///
    /// REFUSAL. A dye used on armor that cannot take it is refused with a message: nothing changes and the dye is kept. A template the
    /// piece's table lacks would otherwise be written onto the piece and silently render as the table's FIRST template.
    /// </summary>
    public static class PerennialDyes
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PerennialDyes));

        /// <summary>The palette set region index 1 holds on the Olthoi Amuli, Koujia and Celdon shell/inlay templates.</summary>
        public const uint ShellPaletteSet = 0x0F000A40;

        /// <summary>Region index the shell shade applies to (the second CloSubPalette).</summary>
        public const int ShellRegionIndex = 1;

        /// <summary>PerennialDyeKind value: sets only the colour template (PaletteTemplate); the piece keeps its own Shade.</summary>
        public const int KindPrimary = 1;

        /// <summary>PerennialDyeKind value: sets only the second colour layer.</summary>
        public const int KindShell = 2;

        /// <summary>Recipe copying ONLY PaletteTemplate (int 3) from the dye to the target, like retail 9068: the piece keeps its own Shade (tone). Content/sql/recipes/PerennialDyeRecipes.sql.</summary>
        public const uint PrimaryRecipeId = 1005510;

        /// <summary>Recipe copying only DyeShellShade (float 9025) from the dye to the target. Content/sql/recipes/PerennialDyeRecipes.sql.</summary>
        public const uint ShellRecipeId = 1005511;

        /// <summary>The nine working RETAIL Perennial dyes (30084 Botched has no recipes and is excluded), which route through retail recipe 9068.</summary>
        public static readonly IReadOnlyCollection<uint> RetailPerennialWcids = new uint[] { 30082, 30083, 30085, 30086, 30087, 30088, 30089, 30090, 30091 };

        /// <summary>Which template/shell question a dye asks of a target.</summary>
        public enum DyeCheck
        {
            /// <summary>The recipe copies the dye's PaletteTemplate: the target's table must list that template.</summary>
            Template,

            /// <summary>The recipe copies DyeShellShade: the target's current template must have a shell layer (region 1 = 0F000A40).</summary>
            Shell,
        }

        // ------------------------------------------------------------------
        // Render
        // ------------------------------------------------------------------

        /// <summary>
        /// The shade one ClothingTable region is rendered with: the piece's DyeShellShade for region index 1 when it has one, Shade for every
        /// other region (and for region 1 without a DyeShellShade). Both render paths (the worn ObjDesc and the item's own CalculateObjDesc) call this.
        /// </summary>
        public static float ShadeForRegion(int regionIndex, float shade, double? shellShade)
        {
            if (regionIndex == ShellRegionIndex && shellShade.HasValue)
                return (float)shellShade.Value;

            return shade;
        }

        // ------------------------------------------------------------------
        // ClothingTable region reader
        // ------------------------------------------------------------------

        private static readonly ConcurrentDictionary<uint, IReadOnlyDictionary<uint, uint[]>> cache = new ConcurrentDictionary<uint, IReadOnlyDictionary<uint, uint[]>>();

        /// <summary>
        /// Reads one ClothingTable as template to the PaletteSet id of each region in order, or null when it cannot be read. Test seam:
        /// production reads the portal dat. A throw is treated like null.
        /// </summary>
        internal static Func<uint, IReadOnlyDictionary<uint, uint[]>> TableReader = id =>
        {
            var table = DatManager.PortalDat?.ReadFromDat<ClothingTable>(id);

            return table?.ClothingSubPalEffects.ToDictionary(kv => kv.Key, kv => kv.Value.CloSubPalettes.Select(p => p.PaletteSet).ToArray());
        };

        /// <summary>Forgets every cached table. Test use.</summary>
        internal static void ClearCache() => cache.Clear();

        /// <summary>The table's templates and per-region palette sets, or null when unreadable. Only a successful read is cached.</summary>
        private static IReadOnlyDictionary<uint, uint[]> ReadRegions(uint clothingBase)
        {
            if (cache.TryGetValue(clothingBase, out var cached))
                return cached;

            try
            {
                var table = TableReader(clothingBase);

                if (table != null)
                    cache[clothingBase] = table;

                return table;
            }
            catch (Exception ex)
            {
                log.Warn($"[PERENNIALDYE] ClothingTable 0x{clothingBase:X8} could not be read (the dye is allowed): {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Decision
        // ------------------------------------------------------------------

        /// <summary>
        /// Whether a target can take a dye. THE one rule: the use guard (<see cref="TryRefuse"/>) and the appraisal lines both call it.
        /// Refused: no ClothingBase; the piece carries its own palette rows (they win over the table, so the dye would do nothing visible).
        /// An unreadable table is ALLOWED - a dat failure never refuses a dye. Template dyes: the table must list <paramref name="sourceTemplate"/>
        /// (a dye with no template has nothing to refuse on). Shell dyes: the target's CURRENT template entry (the table's first when the piece's
        /// template is missing, as both renderers fall back) needs at least two regions with region 1 = <see cref="ShellPaletteSet"/>.
        /// </summary>
        internal static bool Accepts(DyeCheck check, int? sourceTemplate, uint? targetClothingBase, int? targetTemplate, bool targetHasOwnPalette)
        {
            if (targetClothingBase == null || targetHasOwnPalette)
                return false;

            var table = ReadRegions(targetClothingBase.Value);

            if (table == null)
                return true;

            if (check == DyeCheck.Template)
                return sourceTemplate == null || table.ContainsKey((uint)sourceTemplate.Value);

            if (table.Count == 0)
                return false;

            var key = (uint)(targetTemplate ?? 0);
            var entry = table.TryGetValue(key, out var own) ? own : table.Values.First();

            return entry.Length > ShellRegionIndex && entry[ShellRegionIndex] == ShellPaletteSet;
        }

        /// <summary>
        /// Which check a recipe's success mods ask for, or null for a recipe that is no dye. Detected by what the recipe WRITES, never by id:
        /// a success mod that copies PropertyFloat DyeShellShade from the source is the shell dye; one that copies PropertyInt PaletteTemplate
        /// is a template dye (retail 3654, 3844, 3845, 6966, 9068 and the fork primary recipe).
        /// </summary>
        internal static DyeCheck? CheckFor(Recipe recipe)
        {
            if (recipe?.RecipeMod == null)
                return null;

            var success = recipe.RecipeMod.Where(m => m.ExecutesOnSuccess).ToList();

            if (success.Any(m => m.RecipeModsFloat.Any(f => f.Stat == (int)PropertyFloat.DyeShellShade && f.Enum == (int)ModificationOperation.CopyFromSourceToTarget)))
                return DyeCheck.Shell;

            if (success.Any(m => m.RecipeModsInt.Any(i => i.Stat == (int)PropertyInt.PaletteTemplate && i.Enum == (int)ModificationOperation.CopyFromSourceToTarget)))
                return DyeCheck.Template;

            return null;
        }

        /// <summary>The Craft line a refused dye sends.</summary>
        internal static string RefusalMessage(DyeCheck check, string dyeName, string itemName)
        {
            return check == DyeCheck.Shell
                ? $"Your {itemName} has no shell this dye can colour. Nothing has changed, and you keep the dye."
                : $"The {dyeName} will not take on your {itemName}. Nothing has changed, and you keep the dye.";
        }

        /// <summary>
        /// The use guard. For a recipe that copies PaletteTemplate or DyeShellShade, returns true with the message to send when the target
        /// cannot take it. RecipeManager.UseObjectOnTarget runs it after the recipe is found and before VerifyRequirements; on true it sends the line,
        /// ends the use and returns before HandleRecipe, so nothing is changed and nothing is consumed.
        /// </summary>
        public static bool TryRefuse(Recipe recipe, WorldObject source, WorldObject target, out string message)
        {
            message = null;

            var check = CheckFor(recipe);

            if (check == null)
                return false;

            var hasOwnPalette = target.Biota.PropertiesPalette.GetCount(target.BiotaDatabaseLock) > 0;

            if (Accepts(check.Value, source.PaletteTemplate, target.ClothingBase, target.PaletteTemplate, hasOwnPalette))
                return false;

            message = RefusalMessage(check.Value, source.NameWithMaterial, target.NameWithMaterial);
            return true;
        }

        // ------------------------------------------------------------------
        // Routing
        // ------------------------------------------------------------------

        /// <summary>The fork dye kind (1 primary, 2 shell) a source carries, or null for any other item.</summary>
        public static int? KindOf(WorldObject source)
        {
            var kind = source?.GetProperty(PropertyInt.PerennialDyeKind);

            return kind == KindPrimary || kind == KindShell ? kind : null;
        }

        /// <summary>The recipe id a fork dye kind routes to.</summary>
        public static uint RecipeIdFor(int kind) => kind == KindShell ? ShellRecipeId : PrimaryRecipeId;

        // ------------------------------------------------------------------
        // Appraisal
        // ------------------------------------------------------------------

        /// <summary>One piece the examiner is wearing, as the appraisal needs it.</summary>
        internal readonly struct WornPiece
        {
            public WornPiece(string name, uint? clothingBase, int? paletteTemplate, bool hasOwnPalette = false)
            {
                Name = name;
                ClothingBase = clothingBase;
                PaletteTemplate = paletteTemplate;
                HasOwnPalette = hasOwnPalette;
            }

            public string Name { get; }
            public uint? ClothingBase { get; }
            public int? PaletteTemplate { get; }
            public bool HasOwnPalette { get; }
        }

        /// <summary>The check a dye asks (Template for a retail Perennial or a primary fork dye, Shell for a shell dye), or null for any other item.</summary>
        internal static DyeCheck? CheckForDye(WorldObject wo)
        {
            if (wo == null)
                return null;

            var kind = KindOf(wo);

            if (kind == KindShell)
                return DyeCheck.Shell;

            if (kind == KindPrimary || RetailPerennialWcids.Contains(wo.WeenieClassId))
                return DyeCheck.Template;

            return null;
        }

        /// <summary>
        /// The appraisal lines for <paramref name="check"/> against the examiner's worn dyeable pieces, with the SAME decision as the use guard
        /// (<see cref="Accepts"/>). No dyeable piece worn: one line saying to wear armor and appraise again.
        /// </summary>
        internal static List<string> BuildAppraisalLines(DyeCheck check, int? sourceTemplate, IReadOnlyList<WornPiece> worn)
        {
            var lines = new List<string>();

            if (worn == null || worn.Count == 0)
            {
                lines.Add("Wear your armor and appraise this again to see which pieces it will take.");
                return lines;
            }

            var cannot = worn.Where(p => !Accepts(check, sourceTemplate, p.ClothingBase, p.PaletteTemplate, p.HasOwnPalette)).Select(p => p.Name).ToList();
            var colour = check == DyeCheck.Shell ? "this shell colour" : "this colour";

            lines.Add($"Your worn armor: {worn.Count - cannot.Count} of {worn.Count} dyeable pieces can take {colour}.");

            if (cannot.Count > 0)
                lines.Add($"Cannot: {string.Join(", ", cannot)}.");

            return lines;
        }

        /// <summary>
        /// Appraisal lines for a Perennial dye (the nine retail ones and every fork kind), computed from what the examiner is wearing; empty for
        /// any other item or with no examiner.
        /// </summary>
        public static List<string> GetAppraisalLines(WorldObject wo, Player examiner)
        {
            var check = CheckForDye(wo);

            if (check == null || examiner == null)
                return new List<string>();

            var worn = examiner.EquippedObjects.Values
                .Where(w => w.WeenieType == WeenieType.Clothing && (w.Dyable ?? false))
                .Select(w => new WornPiece(w.NameWithMaterial, w.ClothingBase, w.PaletteTemplate, w.Biota.PropertiesPalette.GetCount(w.BiotaDatabaseLock) > 0))
                .ToList();

            return BuildAppraisalLines(check.Value, wo.PaletteTemplate, worn);
        }
    }
}
