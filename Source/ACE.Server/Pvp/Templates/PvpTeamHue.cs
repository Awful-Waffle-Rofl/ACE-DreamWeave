using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>The look a kit is dressed in for a match: a team colour, or the neutral look for a mode with no two sides.</summary>
    public enum KitHue
    {
        Neutral,
        Red,
        Blue,
    }

    /// <summary>
    /// One palette id and shade. A Palette of 0 means "keep the item's own look". A non-zero SourceWcid (an override written
    /// "@&lt;wcid&gt;") instead means "copy the whole look of that world weenie", and then Palette and Shade are unused.
    /// </summary>
    public readonly record struct PvpTeamHueLook(int Palette, double Shade, uint SourceWcid = 0);

    /// <summary>
    /// The team hue tunables read once per match dispatch (Docs/Pvp/TEMPLATES.md "Team hue"): the master switch, the global
    /// look for each hue, and the per-ClothingBase overrides. Immutable, so one snapshot serves a whole match.
    /// </summary>
    /// <remarks>
    /// FfaPalettes and FfaShade are the Tugak Brawl pool (pvp_team_hue_ffa_palettes / _ffa_shade): each seat is dressed in ONE palette
    /// drawn from the pool, see <see cref="PvpTeamHue.FfaPaletteDeck"/>. A null or empty pool means Tugak falls back to the match's
    /// one coin-flipped Red or Blue.
    /// </remarks>
    public sealed record PvpTeamHueDials(
        bool Enabled,
        PvpTeamHueLook Red,
        PvpTeamHueLook Blue,
        PvpTeamHueLook Neutral,
        IReadOnlyDictionary<(uint ClothingBase, KitHue Hue), PvpTeamHueLook> Overrides,
        IReadOnlyList<int> FfaPalettes = null,
        double FfaShade = 0.0)
    {
        /// <summary>The global look for <paramref name="hue"/>.</summary>
        public PvpTeamHueLook Global(KitHue hue) => hue switch
        {
            KitHue.Red => Red,
            KitHue.Blue => Blue,
            _ => Neutral,
        };
    }

    /// <summary>Where an applied look came from.</summary>
    public enum HueSource
    {
        None,
        Override,
        Global,

        /// <summary>The whole look was copied from a world weenie (an "@wcid" override).</summary>
        Swap,
    }

    /// <summary>Why <see cref="PvpTeamHue.ResolveItem"/> left an item's own look alone.</summary>
    public enum HueSkipKind
    {
        None,

        /// <summary>The item is not worn (a pack item): nothing to dress.</summary>
        NotWorn,

        NoClothingBase,

        /// <summary>The item carries raw palette, texture or anim rows, which disable the ClothingBase path.</summary>
        RawRows,

        /// <summary>The setting says keep the item's own look (palette 0).</summary>
        KeepOwn,

        /// <summary>No palette to try is valid for the item's ClothingBase (or the ClothingBase could not be read).</summary>
        NoValidPalette,

        /// <summary>An "@wcid" override whose source weenie is missing, is not a valid look source, or does not cover the kit item's slot.</summary>
        BadLookSource,
    }

    /// <summary>
    /// The outcome for one kit item. <see cref="Applied"/> carries the palette, shade and source; otherwise <see cref="Skip"/> says why.
    /// For a <see cref="HueSource.Swap"/>, <see cref="SwapLook"/> is the whole look to copy (Palette and Shade mirror its PaletteTemplate and Shade).
    /// </summary>
    public sealed record HueResolution(bool Applied, int Palette, double Shade, HueSource Source, HueSkipKind Skip, string Detail,
        LookFields SwapLook = null, uint SwapWcid = 0)
    {
        public static HueResolution Use(int palette, double shade, HueSource source) => new HueResolution(true, palette, shade, source, HueSkipKind.None, null);

        public static HueResolution Keep(HueSkipKind kind, string detail) => new HueResolution(false, 0, 0, HueSource.None, kind, detail);

        public static HueResolution UseSwap(uint wcid, LookFields look) =>
            new HueResolution(true, look.PaletteTemplate ?? 0, look.Shade ?? 0.0, HueSource.Swap, HueSkipKind.None, null, look, wcid);
    }

    /// <summary>One kit item the hue left alone, for the log and the audit.</summary>
    public sealed record HueSkipped(uint WeenieClassId, string Name, HueSkipKind Kind, string Detail);

    /// <summary>The result of <see cref="PvpTeamHue.Apply"/>: the dressed definition and every worn item that kept its own look.</summary>
    public sealed class HueApplyResult
    {
        public PvpTemplateDefinition Definition { get; init; }

        public List<HueSkipped> Skipped { get; init; } = new List<HueSkipped>();

        /// <summary>How many kit items were given a team look.</summary>
        public int AppliedCount { get; init; }
    }

    /// <summary>
    /// Team hue (Docs/Pvp/TEMPLATES.md "Team hue"): dresses a frozen template kit in a team palette and shade. Pure: no DAT, no
    /// PropertyManager. The caller supplies the dials and the DAT lookups, so the production path and the /pvphue audit and
    /// preview run the SAME <see cref="ResolveItem"/>.
    ///
    /// The only things it ever writes on a kit item are PaletteTemplate (Int 3), Shade (Float 12) and, where the clothing table
    /// carries one and the item does not opt out, the Icon (DataId 8). A palette id the item's ClothingBase does not list is
    /// never written, because the appearance code silently renders an unknown id as the first palette.
    ///
    /// The one exception is an "@wcid" override ("red=@1007256"): it copies a world weenie's whole look onto the item (Setup,
    /// PaletteBase, ClothingBase, Icon, PaletteTemplate, Shade and IgnoreCloIcons, the fields /pvphue look copies, planned by the same
    /// <see cref="PvpHueProbe.PlanLook"/>), after the same validation /pvphue look applies. It exists for a model that carries its own
    /// colour in its Setup, such as the Sanguinary Aegis glow, which a palette change cannot recolour.
    /// </summary>
    public static class PvpTeamHue
    {
        private static readonly int ClothingBaseKey = (int)PropertyDataId.ClothingBase;
        private static readonly int IconKey = (int)PropertyDataId.Icon;
        private static readonly int PaletteTemplateKey = (int)PropertyInt.PaletteTemplate;
        private static readonly int ShadeKey = (int)PropertyFloat.Shade;
        private static readonly int IgnoreCloIconsKey = (int)PropertyBool.IgnoreCloIcons;
        private static readonly int NameKey = (int)PropertyString.Name;

        /// <summary>
        /// The hue a seat's kit is dressed in. Mirrors <see cref="PvpTeamColours"/>: Red maps to Red and Blue to Blue in a two-team
        /// match, and any other team count is Neutral. Tugak Brawl returns the match's coin-flipped Red or Blue here, but that is
        /// only its FALLBACK: while the FFA pool (pvp_team_hue_ffa_palettes) has a valid entry, the coordinator dresses each Tugak
        /// seat in its own palette from the pool instead (owner ruling 2026-10-16, <see cref="FfaPaletteDeck"/>) and never calls this.
        /// </summary>
        public static KitHue KitHueFor(PvpColourRule rule, int teamCount, PvpTeamColour colour)
        {
            if (rule == PvpColourRule.Tugak)
                return colour == PvpTeamColour.Red ? KitHue.Red : KitHue.Blue;

            if (teamCount != 2)
                return KitHue.Neutral;

            return colour == PvpTeamColour.Red ? KitHue.Red : KitHue.Blue;
        }

        /// <summary>The lowercase token of a hue ("red", "blue", "neutral"), as the override setting and the admin commands spell it.</summary>
        public static string Token(KitHue hue) => hue switch
        {
            KitHue.Red => "red",
            KitHue.Blue => "blue",
            _ => "neutral",
        };

        /// <summary>Parses "red", "blue" or "neutral", any case.</summary>
        public static bool TryParseHue(string text, out KitHue hue)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "red": hue = KitHue.Red; return true;
                case "blue": hue = KitHue.Blue; return true;
                case "neutral": hue = KitHue.Neutral; return true;
                default: hue = KitHue.Neutral; return false;
            }
        }

        // ======================================================================================
        // Tugak Brawl: one random palette per seat
        // ======================================================================================

        /// <summary>
        /// Parses the pvp_team_hue_ffa_palettes setting: palette ids separated by ',' (for example "2,13,14,84"). An entry that is not
        /// a positive integer (0 would mean "keep own look", which is not a colour to hand out) is reported to <paramref name="warn"/> and
        /// skipped, and so is a repeat of an earlier id (a duplicate would let two seats share a colour). Never throws. An empty
        /// result means the pool is unusable and Tugak falls back to its coin-flipped Red or Blue.
        /// </summary>
        public static IReadOnlyList<int> ParseFfaPalettes(string text, Action<string> warn = null)
        {
            var result = new List<int>();

            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (var raw in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim();

                if (entry.Length == 0)
                    continue;

                if (!int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var palette) || palette <= 0)
                {
                    warn?.Invoke($"pvp_team_hue_ffa_palettes entry '{entry}' is ignored: not a positive palette id");
                    continue;
                }

                if (result.Contains(palette))
                {
                    warn?.Invoke($"pvp_team_hue_ffa_palettes lists {palette} more than once; the repeat is ignored");
                    continue;
                }

                result.Add(palette);
            }

            return result;
        }

        /// <summary>
        /// The dials one Tugak seat's kit is dressed with: no overrides (they are per team) and the seat's palette and shade as the
        /// look of <see cref="KitHue.Neutral"/>, so <see cref="Apply"/> and <see cref="ResolveItem"/> run unchanged and never write a
        /// palette the piece's ClothingBase does not list. Used by the match path and by /pvphue suit, so both run the same resolver.
        /// </summary>
        public static PvpTeamHueDials FfaSeatDials(PvpTeamHueDials dials, int palette, double shade)
        {
            var s = double.IsNaN(shade) ? 0.0 : Math.Max(0.0, Math.Min(1.0, shade));

            return dials with
            {
                Neutral = new PvpTeamHueLook(Math.Max(0, palette), s),
                Overrides = new Dictionary<(uint ClothingBase, KitHue Hue), PvpTeamHueLook>(),
            };
        }

        /// <summary>
        /// Draws one palette per Tugak seat from the pool WITHOUT repeats: the pool is shuffled (Fisher-Yates) and dealt one by one, and
        /// only when it runs out, which needs more seats than palettes, is it reshuffled and dealt again. <c>nextBelow(n)</c> returns an
        /// integer in [0, n); production passes ThreadSafeRandom, tests pass a deterministic source. A source that throws or returns
        /// out of range is clamped, so the deal is always a permutation of the pool.
        /// </summary>
        public sealed class FfaPaletteDeck
        {
            private readonly int[] pool;
            private readonly Func<int, int> nextBelow;
            private readonly List<int> hand = new List<int>();

            public FfaPaletteDeck(IReadOnlyList<int> pool, Func<int, int> nextBelow)
            {
                this.pool = (pool ?? Array.Empty<int>()).ToArray();
                this.nextBelow = nextBelow;

                if (this.pool.Length == 0)
                    throw new ArgumentException("the FFA palette pool is empty", nameof(pool));
            }

            /// <summary>The next palette: never a repeat within one pass over the pool.</summary>
            public int Next()
            {
                if (hand.Count == 0)
                    Reshuffle();

                var last = hand.Count - 1;
                var palette = hand[last];
                hand.RemoveAt(last);

                return palette;
            }

            private void Reshuffle()
            {
                hand.AddRange(pool);

                for (var i = hand.Count - 1; i > 0; i--)
                {
                    var j = 0;

                    try
                    {
                        j = nextBelow == null ? 0 : nextBelow(i + 1);
                    }
                    catch (Exception)
                    {
                        j = 0;
                    }

                    j = Math.Max(0, Math.Min(i, j));

                    (hand[i], hand[j]) = (hand[j], hand[i]);
                }
            }
        }

        // ======================================================================================
        // the override setting
        // ======================================================================================

        /// <summary>
        /// Parses the pvp_team_hue_overrides setting: entries separated by ';', each "&lt;ClothingBase hex&gt;:&lt;team&gt;=&lt;palette&gt;[/&lt;shade&gt;]"
        /// with the team pairs separated by ',' (for example "1000018E:red=85/0.4,blue=88/0.6;10000200:neutral=90"). The
        /// ClothingBase is hexadecimal with or without "0x"; the team is red, blue or neutral; the palette a non-negative integer
        /// (0 keeps the item's own look for that team); the shade a number in [0,1], default 0. Instead of "&lt;palette&gt;[/&lt;shade&gt;]" a
        /// team's value can be "@&lt;wcid&gt;" (a positive world weenie class id, no shade): copy that weenie's whole look onto the
        /// item (for example "10000156:red=@1007256,blue=2/0"). The two forms mix freely within one entry.
        /// A bad entry is reported to <paramref name="warn"/> and skipped as a whole; this never throws. A later pair for the same
        /// ClothingBase and team replaces an earlier one, with a warning.
        /// </summary>
        public static IReadOnlyDictionary<(uint ClothingBase, KitHue Hue), PvpTeamHueLook> ParseOverrides(string text, Action<string> warn = null)
        {
            var result = new Dictionary<(uint, KitHue), PvpTeamHueLook>();

            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (var rawEntry in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = rawEntry.Trim();

                if (entry.Length == 0)
                    continue;

                try
                {
                    if (!TryParseEntry(entry, out var clothingBase, out var pairs, out var problem))
                    {
                        warn?.Invoke($"pvp_team_hue_overrides entry '{entry}' is ignored: {problem}");
                        continue;
                    }

                    foreach (var pair in pairs)
                    {
                        if (result.ContainsKey((clothingBase, pair.Hue)))
                            warn?.Invoke($"pvp_team_hue_overrides sets {Token(pair.Hue)} for 0x{clothingBase:X8} more than once; the later value wins");

                        result[(clothingBase, pair.Hue)] = pair.Look;
                    }
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"pvp_team_hue_overrides entry '{entry}' is ignored: {ex.Message}");
                }
            }

            return result;
        }

        private static bool TryParseEntry(string entry, out uint clothingBase, out List<(KitHue Hue, PvpTeamHueLook Look)> pairs, out string problem)
        {
            clothingBase = 0;
            pairs = new List<(KitHue, PvpTeamHueLook)>();
            problem = null;

            var colon = entry.IndexOf(':');

            if (colon <= 0 || colon == entry.Length - 1)
            {
                problem = "expected <ClothingBase hex>:<team>=<palette>[/<shade>] or <team>=@<wcid>";
                return false;
            }

            var cbText = entry.Substring(0, colon).Trim();

            if (cbText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                cbText = cbText.Substring(2);

            if (!uint.TryParse(cbText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out clothingBase) || clothingBase == 0)
            {
                problem = $"'{entry.Substring(0, colon).Trim()}' is not a hexadecimal ClothingBase id";
                return false;
            }

            foreach (var rawPair in entry.Substring(colon + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = rawPair.Trim();
                var eq = pair.IndexOf('=');

                if (eq <= 0 || eq == pair.Length - 1)
                {
                    problem = $"'{pair}' is not <team>=<palette>[/<shade>]";
                    return false;
                }

                if (!TryParseHue(pair.Substring(0, eq), out var hue))
                {
                    problem = $"'{pair.Substring(0, eq).Trim()}' is not red, blue or neutral";
                    return false;
                }

                var value = pair.Substring(eq + 1).Trim();

                if (value.StartsWith("@", StringComparison.Ordinal))
                {
                    if (!uint.TryParse(value.Substring(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var sourceWcid) || sourceWcid == 0)
                    {
                        problem = $"'{value}' is not @<wcid> (a positive weenie class id, with no shade)";
                        return false;
                    }

                    pairs.Add((hue, new PvpTeamHueLook(0, 0.0, sourceWcid)));
                    continue;
                }

                var slash = value.IndexOf('/');
                var paletteText = slash < 0 ? value : value.Substring(0, slash).Trim();
                var shadeText = slash < 0 ? null : value.Substring(slash + 1).Trim();

                if (!int.TryParse(paletteText, NumberStyles.None, CultureInfo.InvariantCulture, out var palette))
                {
                    problem = $"'{paletteText}' is not a palette id";
                    return false;
                }

                var shade = 0.0;

                if (shadeText != null
                    && (!double.TryParse(shadeText, NumberStyles.Float, CultureInfo.InvariantCulture, out shade) || double.IsNaN(shade) || double.IsInfinity(shade) || shade < 0.0 || shade > 1.0))
                {
                    problem = $"'{shadeText}' is not a shade between 0 and 1";
                    return false;
                }

                pairs.Add((hue, new PvpTeamHueLook(palette, shade)));
            }

            if (pairs.Count == 0)
            {
                problem = "no <team>=<palette> pair";
                return false;
            }

            return true;
        }

        // ======================================================================================
        // resolving one item
        // ======================================================================================

        /// <summary>
        /// What one kit item should wear for <paramref name="hue"/>. Rules, in order:
        ///   1. A pack item, an item with no ClothingBase (DataId 7), or one with raw palette / texture / anim rows is left alone.
        ///   2. The override for (its ClothingBase, hue), when it names a palette the ClothingBase lists. An override whose palette
        ///      is 0 is an explicit "keep this item's own look for this hue".
        ///   3. Otherwise the global palette for the hue, when it is 0 (keep own look) or one the ClothingBase lists.
        ///   4. Otherwise the item keeps its own look and is reported.
        /// An id the ClothingBase does not list is never returned as an applied look.
        /// An "@wcid" override (step 2) copies that world weenie's whole look instead of a palette, when <paramref name="lookSource"/> finds
        /// the weenie and it passes the /pvphue look validation (a ClothingBase, no raw palette/texture/anim rows, slot coverage that
        /// includes the kit item's slot). When it does not, the item keeps its own look and the reason is reported; the global palette is
        /// NOT tried, because the override asked for a specific model. A null <paramref name="lookSource"/> finds no weenie.
        /// </summary>
        public static HueResolution ResolveItem(PvpTemplateKitItem item, KitHue hue, PvpTeamHueDials dials, Func<uint, IReadOnlyCollection<uint>> validPalettes,
            Func<uint, PvpLookSource> lookSource = null)
        {
            if (item == null)
                return HueResolution.Keep(HueSkipKind.NotWorn, "no item");

            if (!item.WieldLocation.HasValue || item.WieldLocation.Value == 0)
                return HueResolution.Keep(HueSkipKind.NotWorn, "not worn");

            if (!item.DataIds.TryGetValue(ClothingBaseKey, out var clothingBase) || clothingBase == 0)
                return HueResolution.Keep(HueSkipKind.NoClothingBase, "no ClothingBase");

            if (item.Palettes.Count > 0 || item.TextureMaps.Count > 0 || item.AnimParts.Count > 0)
                return HueResolution.Keep(HueSkipKind.RawRows, "raw palette/texture/anim rows disable the ClothingBase path");

            IReadOnlyCollection<uint> valid = null;

            try
            {
                valid = validPalettes?.Invoke(clothingBase);
            }
            catch (Exception)
            {
                valid = null;
            }

            string overrideNote = null;

            if (dials.Overrides != null && dials.Overrides.TryGetValue((clothingBase, hue), out var over))
            {
                if (over.SourceWcid != 0)
                    return ResolveSwap(item, over.SourceWcid, lookSource);

                if (over.Palette == 0)
                    return HueResolution.Keep(HueSkipKind.KeepOwn, "the override for this ClothingBase keeps its own look");

                if (IsValid(valid, clothingBase, over.Palette))
                    return HueResolution.Use(over.Palette, over.Shade, HueSource.Override);

                overrideNote = $"override palette {over.Palette} is not valid for 0x{clothingBase:X8}";
            }

            var global = dials.Global(hue);

            if (global.Palette == 0)
                return HueResolution.Keep(HueSkipKind.KeepOwn, $"the {Token(hue)} palette setting is 0 (keep own look)");

            if (IsValid(valid, clothingBase, global.Palette))
                return HueResolution.Use(global.Palette, global.Shade, HueSource.Global);

            var why = valid == null || valid.Count == 0
                ? $"0x{clothingBase:X8} has no readable sub-palettes"
                : $"{Token(hue)} palette {global.Palette} is not valid for 0x{clothingBase:X8}";

            return HueResolution.Keep(HueSkipKind.NoValidPalette, overrideNote == null ? why : overrideNote + "; " + why);
        }

        /// <summary>The "@wcid" override for one worn kit item: the source's look when it is a valid source for the item, else a reported skip. Never throws.</summary>
        private static HueResolution ResolveSwap(PvpTemplateKitItem item, uint sourceWcid, Func<uint, PvpLookSource> lookSource)
        {
            PvpLookSource source;

            try
            {
                source = lookSource?.Invoke(sourceWcid);
            }
            catch (Exception)
            {
                source = null;
            }

            if (source == null || source.Look == null)
                return HueResolution.Keep(HueSkipKind.BadLookSource, $"the @{sourceWcid} override names a world weenie that could not be read");

            var problem = source.Problem();

            if (problem != null)
                return HueResolution.Keep(HueSkipKind.BadLookSource, $"the @{sourceWcid} override cannot use {source.Name ?? "that weenie"} as a look source: {problem}");

            var fits = PvpHueProbe.SelectLookTargets(new[] { new HueWorn(0, ItemName(item), item.WieldLocation, false) }, source.ValidLocations, null);

            if (fits.Chosen.Count == 0)
                return HueResolution.Keep(HueSkipKind.BadLookSource,
                    $"the @{sourceWcid} override's {source.Name ?? "weenie"} covers slots 0x{source.ValidLocations:X}, which do not include this piece's slot 0x{item.WieldLocation.GetValueOrDefault():X}");

            return HueResolution.UseSwap(sourceWcid, source.Look);
        }

        private static bool IsValid(IReadOnlyCollection<uint> valid, uint clothingBase, int palette) =>
            PvpHueProbe.Classify(new HueItem(0, null, clothingBase, valid), palette) == HueSkipReason.None;

        // ======================================================================================
        // dressing a definition
        // ======================================================================================

        /// <summary>
        /// A copy of <paramref name="definition"/> with every resolvable worn kit item dressed in <paramref name="hue"/>, plus the worn items that
        /// kept their own look. Changes ONLY Ints[3], Floats[12] and the Icon (where <see cref="PvpHueProbe.ShouldWriteIcon"/> allows
        /// and <paramref name="tableIcon"/> knows one) on a resolved item; never ClothingBase or Setup, EXCEPT an item resolved by an
        /// "@wcid" override, which takes the source weenie's whole look (<see cref="WriteSwap"/>); never mutates the input.
        /// Disabled dials return the input itself, unchanged.
        /// </summary>
        /// <param name="tableIcon">(ClothingBase, palette) to the clothing table's icon for that palette, 0 or null for none.</param>
        /// <param name="lookSource">wcid to the world weenie read as a look source, for "@wcid" overrides; null finds none.</param>
        public static HueApplyResult Apply(PvpTemplateDefinition definition, KitHue hue, PvpTeamHueDials dials,
            Func<uint, IReadOnlyCollection<uint>> validPalettes, Func<uint, uint, uint> tableIcon = null,
            Func<uint, PvpLookSource> lookSource = null)
        {
            if (definition == null)
                return new HueApplyResult { Definition = null };

            if (dials == null || !dials.Enabled)
                return new HueApplyResult { Definition = definition };

            var json = PvpTemplateJson.SerializeDefinition(definition);

            if (!PvpTemplateJson.TryDeserializeDefinition(json, out var clone, out var error))
            {
                return new HueApplyResult
                {
                    Definition = definition,
                    Skipped = { new HueSkipped(0, definition.Key, HueSkipKind.NoValidPalette, $"the definition could not be copied ({error}), so its kit keeps its own look") },
                };
            }

            var skipped = new List<HueSkipped>();
            var applied = 0;

            foreach (var item in clone.Kit)
            {
                if (item == null)
                    continue;

                var resolution = ResolveItem(item, hue, dials, validPalettes, lookSource);

                if (!resolution.Applied)
                {
                    if (resolution.Skip != HueSkipKind.NotWorn)
                        skipped.Add(new HueSkipped(item.WeenieClassId, ItemName(item), resolution.Skip, resolution.Detail));

                    continue;
                }

                if (resolution.SwapLook != null)
                {
                    WriteSwap(item, resolution.SwapLook, tableIcon);
                    applied++;
                    continue;
                }

                item.Ints[PaletteTemplateKey] = resolution.Palette;
                item.Floats[ShadeKey] = resolution.Shade;

                var clothingBase = item.DataIds[ClothingBaseKey];
                var icon = tableIcon?.Invoke(clothingBase, (uint)resolution.Palette) ?? 0;
                bool? ignoreCloIcons = item.Bools.TryGetValue(IgnoreCloIconsKey, out var ignore) ? ignore : (bool?)null;

                if (PvpHueProbe.ShouldWriteIcon(icon, ignoreCloIcons))
                    item.DataIds[IconKey] = icon;

                applied++;
            }

            return new HueApplyResult { Definition = clone, Skipped = skipped, AppliedCount = applied };
        }

        /// <summary>
        /// Copies a source weenie's look onto a kit item, exactly as /pvphue look does: <see cref="PvpHueProbe.PlanLook"/> decides the
        /// fields (a field the source lacks is removed from the item; Setup and Icon are kept when the source offers none).
        /// </summary>
        private static void WriteSwap(PvpTemplateKitItem item, LookFields source, Func<uint, uint, uint> tableIcon)
        {
            var icon = source.ClothingBase.HasValue ? tableIcon?.Invoke(source.ClothingBase.Value, (uint)(source.PaletteTemplate ?? 0)) ?? 0 : 0;
            var plan = PvpHueProbe.PlanLook(source, icon);
            var t = plan.Target;

            if (!plan.KeepSetup) Put(item.DataIds, (int)PropertyDataId.Setup, t.Setup);
            Put(item.DataIds, ClothingBaseKey, t.ClothingBase);
            Put(item.DataIds, (int)PropertyDataId.PaletteBase, t.PaletteBase);
            Put(item.Ints, PaletteTemplateKey, t.PaletteTemplate);
            Put(item.Floats, ShadeKey, t.Shade);
            Put(item.Floats, (int)PropertyFloat.Shade2, t.Shade2);
            Put(item.Floats, (int)PropertyFloat.Shade3, t.Shade3);
            Put(item.Floats, (int)PropertyFloat.Shade4, t.Shade4);
            Put(item.Bools, IgnoreCloIconsKey, t.IgnoreCloIcons);
            if (!plan.KeepIcon) Put(item.DataIds, IconKey, t.Icon);
        }

        private static void Put<T>(Dictionary<int, T> map, int key, T? value) where T : struct
        {
            if (value.HasValue)
                map[key] = value.Value;
            else
                map.Remove(key);
        }

        // ======================================================================================
        // the /pvphue audit and suit preview
        // ======================================================================================

        /// <summary>
        /// The /pvphue audit rows for one template: one line per worn kit item showing what red, blue and neutral each resolve to
        /// (palette/shade and source, or skipped with the reason). Runs <see cref="ResolveItem"/>, the production resolver. Pack items
        /// are left out. Pure.
        /// </summary>
        public static List<string> AuditLines(PvpTemplateDefinition definition, PvpTeamHueDials dials, Func<uint, IReadOnlyCollection<uint>> validPalettes,
            Func<uint, PvpLookSource> lookSource = null)
        {
            var lines = new List<string>();
            var ffaPool = dials?.FfaPalettes;

            foreach (var item in definition?.Kit ?? new List<PvpTemplateKitItem>())
            {
                if (item == null || !item.WieldLocation.HasValue || item.WieldLocation.Value == 0)
                    continue;

                var cb = item.DataIds.TryGetValue(ClothingBaseKey, out var id) ? $"0x{id:X8}" : "none";
                var cells = new[] { KitHue.Red, KitHue.Blue, KitHue.Neutral }.Select(h => $"{Token(h)} {AuditCell(ResolveItem(item, h, dials, validPalettes, lookSource))}").ToList();

                // Tugak: which palettes of the FFA pool this piece can take, by the same resolver the match runs per seat.
                if (ffaPool != null && ffaPool.Count > 0)
                {
                    var ok = new List<int>();
                    var skipped = new List<int>();

                    foreach (var palette in ffaPool)
                        (ResolveItem(item, KitHue.Neutral, FfaSeatDials(dials, palette, dials.FfaShade), validPalettes).Applied ? ok : skipped).Add(palette);

                    cells.Add($"ffa pool ok [{string.Join(",", ok)}] skipped [{string.Join(",", skipped)}]");
                }

                lines.Add($"  {ItemName(item)} loc=0x{item.WieldLocation.Value:X} cb={cb}: {string.Join(" | ", cells)}");
            }

            return lines;
        }

        private static string AuditCell(HueResolution r) => !r.Applied
            ? $"skipped ({r.Detail})"
            : r.Source == HueSource.Swap
                ? $"swap @{r.SwapWcid} (cb 0x{r.SwapLook.ClothingBase.GetValueOrDefault():X8} setup 0x{r.SwapLook.Setup.GetValueOrDefault():X8} palette {r.Palette}/{r.Shade.ToString("0.###", CultureInfo.InvariantCulture)})"
                : $"{r.Palette}/{r.Shade.ToString("0.###", CultureInfo.InvariantCulture)} {(r.Source == HueSource.Override ? "override" : "global")}";

        /// <summary>One kit item's look to copy onto one worn piece (the /pvphue suit preview).</summary>
        public sealed record SuitStep(PvpTemplateKitItem Kit, LookFields Source, uint TargetGuid, string TargetName);

        public sealed class SuitPlan
        {
            public List<SuitStep> Steps { get; } = new List<SuitStep>();

            /// <summary>Kit items that found no worn piece in their slot, and worn pieces no kit item reached, with why.</summary>
            public List<string> Left { get; } = new List<string>();
        }

        /// <summary>The look-bearing fields of a kit item, as <see cref="PvpHueProbe.PlanLook"/> reads a look source.</summary>
        public static LookFields LookOf(PvpTemplateKitItem item)
        {
            uint? D(PropertyDataId p) => item.DataIds.TryGetValue((int)p, out var v) ? v : (uint?)null;
            double? F(PropertyFloat p) => item.Floats.TryGetValue((int)p, out var v) ? v : (double?)null;

            return new LookFields
            {
                Setup = D(PropertyDataId.Setup),
                ClothingBase = D(PropertyDataId.ClothingBase),
                PaletteBase = D(PropertyDataId.PaletteBase),
                Icon = D(PropertyDataId.Icon),
                PaletteTemplate = item.Ints.TryGetValue(PaletteTemplateKey, out var pt) ? pt : (int?)null,
                Shade = F(PropertyFloat.Shade),
                Shade2 = F(PropertyFloat.Shade2),
                Shade3 = F(PropertyFloat.Shade3),
                Shade4 = F(PropertyFloat.Shade4),
                IgnoreCloIcons = item.Bools.TryGetValue(IgnoreCloIconsKey, out var ignore) ? ignore : (bool?)null,
            };
        }

        /// <summary>
        /// Plans /pvphue suit: for each worn kit item of <paramref name="dressed"/> (the template already dressed by <see cref="Apply"/>, or
        /// the catalog definition for "own"), the worn piece in the same slot that takes its look. Reuses
        /// <see cref="PvpHueProbe.SelectLookTargets"/> with the kit item's wield location as the allowed slots, so a worn piece is chosen
        /// only when its whole location lies inside the kit item's, one with its own palette/texture/anim rows is never chosen, and a
        /// worn piece takes at most one kit item's look. Pure.
        /// </summary>
        public static SuitPlan PlanSuit(PvpTemplateDefinition dressed, IReadOnlyCollection<HueWorn> worn)
        {
            var plan = new SuitPlan();
            var assigned = new HashSet<uint>();

            foreach (var item in dressed?.Kit ?? new List<PvpTemplateKitItem>())
            {
                if (item == null || !item.WieldLocation.HasValue || item.WieldLocation.Value == 0)
                    continue;

                // Production never dresses these (ResolveItem leaves them alone), so the preview must not copy their look either: a kit
                // piece with no ClothingBase would null the worn piece's ClothingBase and swap its Setup.
                if (!item.DataIds.TryGetValue(ClothingBaseKey, out var kitCb) || kitCb == 0)
                {
                    plan.Left.Add($"kit piece {ItemName(item)} (loc 0x{item.WieldLocation.Value:X}): no ClothingBase, so a match never dresses it and the preview does not copy it");
                    continue;
                }

                if (item.Palettes.Count > 0 || item.TextureMaps.Count > 0 || item.AnimParts.Count > 0)
                {
                    plan.Left.Add($"kit piece {ItemName(item)} (loc 0x{item.WieldLocation.Value:X}): raw palette/texture/anim rows, which the preview cannot copy");
                    continue;
                }

                var chosen = PvpHueProbe.SelectLookTargets(worn.Where(w => !assigned.Contains(w.Guid)), item.WieldLocation.Value, null).Chosen;

                if (chosen.Count == 0)
                {
                    plan.Left.Add($"kit piece {ItemName(item)} (loc 0x{item.WieldLocation.Value:X}): no worn piece in that slot can take a look");
                    continue;
                }

                var source = LookOf(item);

                foreach (var guid in chosen)
                {
                    assigned.Add(guid);
                    plan.Steps.Add(new SuitStep(item, source, guid, worn.First(w => w.Guid == guid).Name));
                }
            }

            foreach (var w in worn.Where(w => !assigned.Contains(w.Guid)))
                plan.Left.Add($"worn piece {w.Name}: {(w.HasOwnRows ? "has its own palette/texture/anim rows" : "no kit piece in its slot")}");

            return plan;
        }

        /// <summary>The kit item's name when it carries one, else "wcid N".</summary>
        public static string ItemName(PvpTemplateKitItem item) =>
            item.Strings != null && item.Strings.TryGetValue(NameKey, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"wcid {item.WeenieClassId}";
    }
}
