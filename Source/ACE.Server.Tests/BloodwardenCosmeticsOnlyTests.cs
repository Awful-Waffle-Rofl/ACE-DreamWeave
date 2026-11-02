using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The cosmetics-only and pricing guard for the PvP arena's FOUR Hopeslayer vendors (named Bloodwarden until 2026-10-15;
    /// 1004130 Armsmaster, restyled from
    /// Tegao Wavecounter; 1006651 Quartermaster; 1006652 Armorer; 1006653 Tailor - the one 895-row shop was split
    /// on 2026-10-12 because it lagged the client on open) and their Blood currency (wcid 1006650). Every check
    /// below runs over the shop rows of ALL FOUR vendors; which vendor stocks which clone is pinned separately by
    /// ACE.Content.Tools.Tests VendorRoutingTests.
    ///
    /// WHY THIS EXISTS: the owner allows PvP rewards at all ONLY because the one thing they buy is cosmetic.
    /// A single shop clone that carries damage, armor, a gear rating, a spell or a proc would turn Blood into
    /// a power currency, and nothing else in the build would notice - the shop list is ~895 generated clones
    /// owned by two different tools. This test reads the committed SQL and fails loudly instead.
    ///
    /// ALLOWLIST, NOT DENYLIST. An earlier version named the power properties it knew about and passed a shop
    /// file that had armor mods, a gear mod and a cloak proc appended, because none of those ids were on the
    /// list. Now every shop file may contain ONLY the tables and property ids below (the exact set the 895
    /// committed clones use, each id checked by enum name to be cosmetic), and anything else - an unknown
    /// id, an unknown table, an emote or spell-book row, any statement that is not an INSERT into an allowed
    /// table or the file's own `DELETE FROM weenie` - fails. A file the parser cannot account for in full also
    /// fails: a skipped file must never count as a pass.
    ///
    /// It reads Content/sql/weenies directly (no database), locating the repo by walking up from the test
    /// assembly like ClassAbilityTokenTests does, so it must run in-tree (never with --artifacts-path).
    /// </summary>
    [TestClass]
    public class BloodwardenCosmeticsOnlyTests
    {
        private static readonly uint[] VendorWcids = { 1004130, 1006651, 1006652, 1006653 };
        private const uint BloodWcid = 1006650;

        // PRICE TABLE, in Marks of the Hopeslayer (owner ranges, 2026-10-15), per vendor: weapons and shields 500
        // (Armsmaster, Quartermaster), armor 800 each with no bundle (Armorer), clothing, robes and cloaks 500
        // (Tailor). Every clone's price is written by ACE.Content.Tools cosmeticimport (VendorRouting.Price, in
        // CosmeticAllowlist.Strip); this is the independent pin on the committed result. It was 5 on every line.
        private static readonly Dictionary<uint, int> ExpectedPricePerVendor = new()
        {
            { 1004130, 500 }, { 1006651, 500 }, { 1006652, 800 }, { 1006653, 500 },
        };

        // The seven tailoring tools (1004312-1004318) cost 1 Mark each:
        // the three reduction tools since #1590, the rest since 2026-10-15.
        private const int TailoringToolPrice = 1;
        private static readonly HashSet<uint> TailoringToolWcids = new() { 1004312, 1004313, 1004314, 1004315, 1004316, 1004317, 1004318 };

        // The nine RETAIL Perennial dyes the Tailor stocks as-is (not clones: a dye works through cook_book rows keyed
        // on its own wcid). They have no unit under Content/sql/weenies, so the clone checks skip them; their price
        // (3500, set on the retail weenies by Content/sql/patches/hopeslayer_perennial_dye_price.sql) is pinned by
        // Hopeslayer_Tailor_StocksTheNinePerennialDyes_PricedByTheirPatch. 30084 Perennial Botched Dye is deliberately
        // absent (owner ruling 2026-10-15: 0 cook_book rows in retail data, so it does nothing).
        private const int PerennialDyePrice = 3500;
        private static readonly HashSet<uint> RetailDyeWcids = new() { 30082, 30083, 30085, 30086, 30087, 30088, 30089, 30090, 30091 };
        private const uint BotchedDyeWcid = 30084;

        // PvP PRESTIGE items: hand-authored, NOT clones, priced on their own and gated by PropertyBool 9076 PvpPrestigeGated
        // (a peak rating or battleground wins, Vendor.BuyItems_ValidateTransaction). Each is stocked by exactly one named
        // vendor, skipped by the clone checks, and pinned instead by Hopeslayer_PrestigeItems_AreGatedPricedAndCarryNoPower,
        // which still forbids every power id and allows only the named extras below on top of the cosmetic allowlist.
        // 1006654 Guise of the Hopeslayer: the Bael'Zharon body suit (PropertyString 9020 WornModelOverrides), 9000 Marks.
        private static readonly Dictionary<uint, (uint Vendor, int Price)> PrestigeItems = new()
        {
            { 1006654, (1006652, 9000) },
        };

        // What a prestige item may carry beyond the cosmetic allowlist: int 33 Bonded, 114 Attuned (it cannot be handed to
        // someone who has not earned it); bool 123 TopLayerPriority, 9076 PvpPrestigeGated; string 9020 WornModelOverrides;
        // and weenie_properties_palette rows (its colour). None of these is a power property.
        private static readonly HashSet<int> PrestigeExtraInts = new() { (int)PropertyInt.Bonded, (int)PropertyInt.Attuned };
        private static readonly HashSet<int> PrestigeExtraBools = new() { (int)PropertyBool.TopLayerPriority, (int)PropertyBool.PvpPrestigeGated };
        private static readonly HashSet<int> PrestigeExtraStrings = new() { (int)PropertyString.WornModelOverrides };
        private const string PaletteTable = "weenie_properties_palette";

        // (object_Id, sub_Palette_Id, offset, length)
        private static readonly Regex PaletteRow = new(@"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);

        // Every shop clone must be one of the generated/hand-curated cosmetic ranges; a count far below this
        // means the parse found nothing (a regex that matches zero rows would otherwise pass vacuously).
        // MinimumShopLines is the total across the four vendors; the per-vendor floor makes an emptied vendor fail
        // on its own (the smallest vendor stocks ~170 rows, so 100 leaves room without letting one go empty).
        private const int MinimumShopLines = 800;
        private const int MinimumShopLinesPerVendor = 100;

        // Exact stock, beside the floors: the floors only catch a collapse, not a few dropped rows. These are the 895
        // rows the single Bloodwarden carried before the 2026-10-12 split, divided 225/208/289/173 by the routing rule;
        // they move only deliberately (same numbers as ACE.Content.Tools.Tests VendorRoutingTests).
        // 2026-10-15: plus the nine working retail Perennial dyes on the Tailor (173 + 9, 895 + 9).
        // 2026-10-15: plus the prestige suit 1006654 on the Armorer (289 + 1, 904 + 1).
        private const int ExpectedTotalShopLines = 905;
        private static readonly Dictionary<uint, int> ExpectedShopLinesPerVendor = new()
        {
            { 1004130, 225 }, { 1006651, 208 }, { 1006652, 290 }, { 1006653, 182 },
        };

        private static readonly HashSet<string> AllowedTables = new()
        {
            "weenie",
            "weenie_properties_int",
            "weenie_properties_bool",
            "weenie_properties_float",
            "weenie_properties_d_i_d",
            "weenie_properties_string",
        };

        // 1 ItemType, 3 PaletteTemplate, 4 ClothingPriority, 5 EncumbranceVal, 8 Mass, 9 ValidLocations,
        // 11 MaxStackSize, 12 StackSize, 13 StackUnitEncumbrance, 15 StackUnitValue, 16 ItemUseable,
        // 18 UiEffects, 19 Value, 45 DamageType, 46 DefaultCombatStyle, 47 AttackType, 50 AmmoType,
        // 51 CombatUse, 52 ParentLocation, 53 PlacementPosition, 93 PhysicsState, 94 TargetType,
        // 131 MaterialType, 150 HookPlacement, 151 HookType, 353 WeaponType.
        // DamageType and friends say what KIND of weapon a model is; with no Damage (44) they do nothing.
        private static readonly HashSet<int> AllowedInts = new() { 1, 3, 4, 5, 8, 9, 11, 12, 13, 15, 16, 18, 19, 45, 46, 47, 50, 51, 52, 53, 93, 94, 131, 150, 151, 353 };

        // 12 Shade, 39 DefaultScale, 76 Translucency, 77 PhysicsScriptIntensity, 84 Shade2.
        private static readonly HashSet<int> AllowedFloats = new() { 12, 39, 76, 77, 84 };

        // 1 Setup, 3 SoundTable, 6 PaletteBase, 7 ClothingBase, 8 Icon, 22 PhysicsEffectTable, 30 PhysicsScript,
        // 50 IconOverlay, 52 IconUnderlay.
        private static readonly HashSet<int> AllowedDids = new() { 1, 3, 6, 7, 8, 22, 30, 50, 52 };

        // 1 Stuck, 11 IgnoreCollisions, 13 Ethereal, 14 GravityStatus, 15 LightsStatus, 19 Attackable,
        // 22 Inscribable, 69 IsSellable, 84 IgnoreCloIcons, 100 Dyable.
        private static readonly HashSet<int> AllowedBools = new() { 1, 11, 13, 14, 15, 19, 22, 69, 84, 100 };

        // 1 Name, 14 Use, 16 LongDesc.
        private static readonly HashSet<int> AllowedStrings = new() { 1, 14, 16 };

        // Named explicitly so the failure says WHY, and so the allowlist can never quietly grow one of them.
        private static readonly Dictionary<int, string> NeverInts = new()
        {
            { (int)PropertyInt.Damage, "Damage" },
            { (int)PropertyInt.ArmorLevel, "ArmorLevel" },
            { (int)PropertyInt.ItemWorkmanship, "ItemWorkmanship" },
        };

        // Every gear/luminance rating int, found by NAME so a rating added to the enum later is covered
        // without anyone remembering to touch this test: anything called ...Rating or Gear...
        private static readonly HashSet<int> RatingInts = Enum.GetValues(typeof(PropertyInt)).Cast<PropertyInt>()
            .Where(p => p.ToString().EndsWith("Rating", StringComparison.Ordinal) || p.ToString().StartsWith("Gear", StringComparison.Ordinal))
            .Select(p => (int)p)
            .ToHashSet();

        private static readonly int[] NeverDids =
        {
            (int)PropertyDataId.Spell,
            (int)PropertyDataId.DeathSpell,
            (int)PropertyDataId.ProcSpell,
            (int)PropertyDataId.BlueSurgeSpell,
            (int)PropertyDataId.YellowSurgeSpell,
            (int)PropertyDataId.RedSurgeSpell,
        };

        // One left-to-right pass so each token is judged by what it really is: a comment may hold an unbalanced
        // apostrophe (1004227's header quotes its own file name, "o' Lantern"), and a string may hold "/*" or "--".
        private static readonly Regex CommentOrLiteral = new(@"/\*.*?\*/|--[^\n]*|'(?:[^']|'')*'", RegexOptions.Singleline | RegexOptions.Compiled);

        // (object_Id, type, value) once string literals have been blanked to '' - the int, float, d_i_d, bool
        // and string tables all share this three-column shape.
        private static readonly Regex Triple = new(@"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?[0-9A-Za-z.]+|'')\s*\)", RegexOptions.Compiled);

        // (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
        private static readonly Regex CreateListRow = new(@"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?\d+)\s*,\s*[^,)]+,\s*[^,)]+,\s*[^,)]+\)", RegexOptions.Compiled);

        /// <summary>One weenie file reduced to its statements. Problems is everything the parser could not account for.</summary>
        private sealed class ParsedWeenie
        {
            public readonly Dictionary<string, List<(int Type, string Value)>> Rows = new();
            public readonly List<string> Problems = new();
            public int InsertCount;

            public List<(int Type, string Value)> Table(string name)
                => Rows.TryGetValue(name, out var rows) ? rows : new List<(int, string)>();
        }

        private static DirectoryInfo WeenieDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "Could not find Content/sql/weenies by walking up from " + AppContext.BaseDirectory + " (run in-tree, never with --artifacts-path).");
            return new DirectoryInfo(Path.Combine(dir.FullName, "Content", "sql", "weenies"));
        }

        private static string WeenieSql(uint wcid)
        {
            var matches = WeenieDir().GetFiles(wcid + " *.sql");
            Assert.AreEqual(1, matches.Length, $"expected exactly one Content/sql/weenies/{wcid} *.sql file, found {matches.Length}");
            return File.ReadAllText(matches[0].FullName);
        }

        /// <summary>Drop comments and blank string literals to '' (their content is never a property id).</summary>
        private static string Normalise(string sql)
            => CommentOrLiteral.Replace(sql, m => m.Value.StartsWith("'") ? "''" : "");

        private static ParsedWeenie Parse(string sql, uint wcid, bool allowPaletteRows = false)
        {
            var parsed = new ParsedWeenie();
            var deleteSeen = 0;

            foreach (var raw in Normalise(sql).Split(';'))
            {
                var stmt = raw.Trim();
                if (stmt.Length == 0)
                    continue;

                var insert = Regex.Match(stmt, @"^INSERT\s+INTO\s+`(\w+)`(.*)$", RegexOptions.Singleline);
                if (insert.Success)
                {
                    var table = insert.Groups[1].Value;
                    var body = insert.Groups[2].Value;
                    parsed.InsertCount++;

                    // Prestige items only: their palette rows are four-column, so they are counted here rather than by Triple.
                    if (allowPaletteRows && table == PaletteTable)
                    {
                        var paletteRows = PaletteRow.Matches(body).Cast<Match>().ToList();
                        if (body.Count(c => c == '(') != paletteRows.Count + 1)
                            parsed.Problems.Add($"table `{table}`: {body.Count(c => c == '(') - 1} row(s) in the file but only {paletteRows.Count} parsed");
                        if (!parsed.Rows.TryGetValue(table, out var paletteList))
                            parsed.Rows[table] = paletteList = new List<(int, string)>();
                        paletteList.AddRange(paletteRows.Select(m => (int.Parse(m.Groups[2].Value), m.Groups[3].Value + ":" + m.Groups[4].Value)));
                        continue;
                    }

                    if (!AllowedTables.Contains(table))
                    {
                        parsed.Problems.Add($"table `{table}` is not allowed on a shop item");
                        continue;
                    }

                    if (table == "weenie")
                        continue;

                    // Every "(" after the column list must be a row the triple regex accounted for, or a row
                    // went unread (a comma-first layout, a hex id, a four-column row, ...) and its ids were never checked.
                    var rows = Triple.Matches(body).Cast<Match>().Select(m => (int.Parse(m.Groups[2].Value), m.Groups[3].Value)).ToList();
                    var opens = body.Count(c => c == '(');
                    if (opens != rows.Count + 1)
                        parsed.Problems.Add($"table `{table}`: {opens - 1} row(s) in the file but only {rows.Count} parsed");

                    if (!parsed.Rows.TryGetValue(table, out var list))
                        parsed.Rows[table] = list = new List<(int, string)>();
                    list.AddRange(rows);
                    continue;
                }

                if (Regex.IsMatch(stmt, @"^DELETE\s+FROM\s+`weenie`\s+WHERE\s+`class_Id`\s*=\s*" + wcid + @"$"))
                {
                    deleteSeen++;
                    continue;
                }

                parsed.Problems.Add("unrecognised statement: " + stmt.Substring(0, Math.Min(70, stmt.Length)).Replace("\n", " ").Replace("\r", ""));
            }

            if (deleteSeen != 1)
                parsed.Problems.Add($"expected exactly one `DELETE FROM weenie WHERE class_Id = {wcid}`, found {deleteSeen}");
            if (!parsed.Rows.ContainsKey("weenie_properties_int"))
                parsed.Problems.Add("no weenie_properties_int statement parsed");

            return parsed;
        }

        private static int? IntValue(ParsedWeenie w, int type)
        {
            var row = w.Table("weenie_properties_int").Where(r => r.Type == type).ToList();
            return row.Count == 0 ? null : int.Parse(row[0].Value);
        }

        /// <summary>One vendor's Shop wcids; the guard fails if the vendor's create_list holds anything but its own Wield/Shop rows or too few Shop rows.</summary>
        private static List<uint> ShopWcids(uint vendorWcid)
        {
            var sql = Normalise(WeenieSql(vendorWcid));
            var shop = new List<uint>();
            var statements = Regex.Matches(sql, @"INSERT INTO `weenie_properties_create_list`(.*?);", RegexOptions.Singleline);
            Assert.IsTrue(statements.Count > 0, $"weenie {vendorWcid} has no weenie_properties_create_list statement");

            var rowsInFile = 0;
            var rowsMatched = 0;
            var shopRows = 0;
            var badRows = new List<string>();
            foreach (Match stmt in statements)
            {
                // Every "(" after the column list is one value tuple; a row the regex cannot read (a 6-column form,
                // a hex wcid, ...) would otherwise be dropped from the shop list and its file never checked.
                rowsInFile += stmt.Groups[1].Value.Count(c => c == '(') - 1;

                foreach (Match m in CreateListRow.Matches(stmt.Groups[1].Value))
                {
                    rowsMatched++;
                    var owner = uint.Parse(m.Groups[1].Value);
                    var destination = int.Parse(m.Groups[2].Value);

                    if (owner != vendorWcid || (destination != 2 /* Wield */ && destination != 4 /* Shop */))
                        badRows.Add($"object {owner}, destination_Type {destination}, wcid {m.Groups[3].Value}");

                    if (owner == vendorWcid && destination == 4)
                    {
                        shopRows++;
                        shop.Add(uint.Parse(m.Groups[3].Value));
                    }
                }
            }

            Assert.AreEqual(rowsInFile, rowsMatched, $"weenie {vendorWcid}'s create_list has {rowsInFile} row(s) but only {rowsMatched} were parsed - an unreadable row is a shop item the guard never checked");
            Assert.AreEqual(0, badRows.Count, "create_list rows that are not this vendor's Wield (2) or Shop (4) rows (vendor " + vendorWcid + "):\n" + string.Join("\n", badRows.Take(10)));
            Assert.AreEqual(shopRows, shop.Count, "shop wcid count does not equal the number of destination_Type 4 rows");

            Assert.IsTrue(shop.Count >= MinimumShopLinesPerVendor, $"parsed only {shop.Count} shop lines from weenie {vendorWcid}; expected at least {MinimumShopLinesPerVendor} per vendor - an empty vendor, or a guard that passes vacuously");
            Assert.AreEqual(shop.Count, shop.Distinct().Count(), $"duplicate shop wcids in weenie {vendorWcid}'s create_list");
            return shop;
        }

        /// <summary>Every Shop wcid of all four vendors, each clone on exactly one vendor.</summary>
        private static List<uint> ShopWcids()
        {
            var all = new List<uint>();
            foreach (var vendor in VendorWcids)
                all.AddRange(ShopWcids(vendor));

            Assert.IsTrue(all.Count >= MinimumShopLines, $"parsed only {all.Count} shop lines across the four vendors; expected at least {MinimumShopLines} - the guard would pass vacuously");
            var twice = all.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.AreEqual(0, twice.Count, "clones stocked by more than one Bloodwarden vendor (or twice by one): " + string.Join(", ", twice.Take(25)));
            return all;
        }

        /// <summary>
        /// Every shop CLONE, parsed; a file that is missing or not wholly accounted for is reported, never skipped. The
        /// nine retail Perennial dyes are the only shop rows that are not clones, and only on the Tailor; they are
        /// excluded here and checked by their own test.
        /// </summary>
        private static List<(uint Wcid, ParsedWeenie Parsed)> ParsedShop(List<string> violations)
        {
            var result = new List<(uint, ParsedWeenie)>();
            foreach (var wcid in ShopWcids())
            {
                if (RetailDyeWcids.Contains(wcid) || PrestigeItems.ContainsKey(wcid))
                    continue;
                var files = WeenieDir().GetFiles(wcid + " *.sql");
                if (files.Length != 1)
                {
                    violations.Add($"{wcid}: expected exactly one weenie file, found {files.Length}");
                    continue;
                }

                var parsed = Parse(File.ReadAllText(files[0].FullName), wcid);
                foreach (var problem in parsed.Problems)
                    violations.Add($"{wcid}: UNPARSEABLE/UNACCOUNTED - {problem}");
                result.Add((wcid, parsed));
            }
            return result;
        }

        [TestMethod]
        public void Bloodwarden_EveryShopFile_IsFullyParsed()
        {
            var violations = new List<string>();
            var shopCount = ShopWcids().Count(w => !RetailDyeWcids.Contains(w) && !PrestigeItems.ContainsKey(w)); // the retail dyes and the prestige items have their own tests
            var parsed = ParsedShop(violations);
            Assert.AreEqual(shopCount, parsed.Count, "some shop files were skipped instead of parsed:\n" + string.Join("\n", violations.Take(25)));
            Assert.AreEqual(0, violations.Count, "shop files the guard could not fully account for:\n" + string.Join("\n", violations.Take(25)));
        }

        /// <summary>
        /// The vendor panel lists stock in create_list row order (Vendor iterates PropertiesCreateList; the rows are
        /// inserted in file order), so the Armorer's first Shop row is the first item a player sees. Owner request:
        /// Guise of the Hopeslayer (1006654) leads the Armorer's list.
        /// </summary>
        [TestMethod]
        public void Armorer_FirstShopRow_IsTheGuiseOfTheHopeslayer()
        {
            var shop = ShopWcids(1006652);
            Assert.AreEqual(1006654u, shop[0], "Hopeslayer's Armorer (1006652) must list Guise of the Hopeslayer (1006654) first; first shop row is " + shop[0]);
        }

        /// <summary>
        /// The price table, per vendor ROUTE: every clone a vendor stocks costs that vendor's price (Value and
        /// StackUnitValue both, since the shelf and the till read different ones on a stackable), except the seven tailoring tools (1004312-1004318)
        /// at 1 Mark each. Checked per vendor so a clone priced for the wrong shop fails by name.
        /// </summary>
        [TestMethod]
        public void Hopeslayer_EveryShopClone_IsPricedPerItsVendorsRoute()
        {
            var violations = new List<string>();
            var checkedPerVendor = new Dictionary<uint, int>();
            foreach (var vendor in VendorWcids)
            {
                var vendorViolations = new List<string>();
                foreach (var wcid in ShopWcids(vendor))
                {
                    if (RetailDyeWcids.Contains(wcid) || PrestigeItems.ContainsKey(wcid))
                        continue;
                    var files = WeenieDir().GetFiles(wcid + " *.sql");
                    if (files.Length != 1)
                    {
                        violations.Add($"{wcid}: expected exactly one weenie file, found {files.Length}");
                        continue;
                    }
                    var w = Parse(File.ReadAllText(files[0].FullName), wcid);
                    var value = IntValue(w, (int)PropertyInt.Value);
                    var unit = IntValue(w, (int)PropertyInt.StackUnitValue);
                    var expected = TailoringToolWcids.Contains(wcid) ? TailoringToolPrice : ExpectedPricePerVendor[vendor];
                    if (value != expected || unit != expected)
                        violations.Add($"vendor {vendor}, {wcid}: Value={value?.ToString() ?? "absent"} StackUnitValue={unit?.ToString() ?? "absent"} (both must be {expected})");
                    checkedPerVendor[vendor] = checkedPerVendor.GetValueOrDefault(vendor) + 1;
                }
            }
            foreach (var vendor in VendorWcids)
                Assert.IsTrue(checkedPerVendor.GetValueOrDefault(vendor) >= MinimumShopLinesPerVendor - 10, $"only {checkedPerVendor.GetValueOrDefault(vendor)} clones price-checked on vendor {vendor} - the check would pass vacuously");
            Assert.AreEqual(0, violations.Count, "Hopeslayer shop clones not at their vendor's price:\n" + string.Join("\n", violations.Take(25)));
        }

        /// <summary>Every stocked clone carries IsSellable (bool 69) = false, so it cannot sell back to an NPC for pyreals.</summary>
        [TestMethod]
        public void Hopeslayer_EveryShopClone_IsNotSellable()
        {
            var violations = new List<string>();
            var parsed = ParsedShop(violations);
            foreach (var (wcid, w) in parsed)
            {
                var rows = w.Table("weenie_properties_bool").Where(r => r.Type == (int)PropertyBool.IsSellable).ToList();
                if (rows.Count != 1 || !(rows[0].Value == "False" || rows[0].Value == "0"))
                    violations.Add($"{wcid}: IsSellable rows = [{string.Join(", ", rows.Select(r => r.Value))}] (must be exactly one, False)");
            }
            Assert.IsTrue(parsed.Count >= MinimumShopLines, $"only {parsed.Count} clones checked - the check would pass vacuously");
            Assert.AreEqual(0, violations.Count, "Hopeslayer shop clones that can sell back for pyreals:\n" + string.Join("\n", violations.Take(25)));
        }

        /// <summary>
        /// The Tailor stocks exactly the nine working retail Perennial dyes as its only non-clone rows, and the patch unit
        /// that prices them sets Value (19) and StackUnitValue (15) to 3500 for exactly those nine. 30084 Perennial Botched
        /// Dye is neither stocked by any vendor nor priced. Without the patch a dye
        /// ships at Value 0 and sells for the max(1, ...) floor of one Mark.
        /// </summary>
        [TestMethod]
        public void Hopeslayer_Tailor_StocksTheNinePerennialDyes_PricedByTheirPatch()
        {
            var tailorDyes = ShopWcids(1006653).Where(RetailDyeWcids.Contains).ToList();
            CollectionAssert.AreEquivalent(RetailDyeWcids.ToList(), tailorDyes, "the Tailor must stock the nine Perennial dyes 30082-30091 except 30084");
            foreach (var vendor in VendorWcids.Where(v => v != 1006653))
                Assert.IsFalse(ShopWcids(vendor).Any(RetailDyeWcids.Contains), $"vendor {vendor} stocks a Perennial dye; only the Tailor does");
            foreach (var vendor in VendorWcids)
                Assert.IsFalse(ShopWcids(vendor).Contains(BotchedDyeWcid), $"vendor {vendor} stocks Perennial Botched Dye {BotchedDyeWcid}, which does nothing");

            var patch = Path.Combine(WeenieDir().Parent.FullName, "patches", "hopeslayer_perennial_dye_price.sql");
            Assert.IsTrue(File.Exists(patch), $"missing {patch}");
            var sql = Normalise(File.ReadAllText(patch));
            var priced = Regex.Matches(sql, @"\(\s*(\d+)\s*,\s*(15|19)\s*,\s*(\d+)\s*\)").Cast<Match>()
                .Select(m => (Wcid: uint.Parse(m.Groups[1].Value), Type: int.Parse(m.Groups[2].Value), Value: int.Parse(m.Groups[3].Value))).ToList();
            foreach (var dye in RetailDyeWcids)
                foreach (var type in new[] { 15, 19 })
                {
                    var rows = priced.Where(r => r.Wcid == dye && r.Type == type).ToList();
                    Assert.AreEqual(1, rows.Count, $"patch must set int {type} on {dye} exactly once");
                    Assert.AreEqual(PerennialDyePrice, rows[0].Value, $"patch int {type} on {dye}");
                }
            Assert.IsFalse(priced.Any(r => r.Wcid == BotchedDyeWcid), $"the patch must not price Perennial Botched Dye {BotchedDyeWcid}");
            Assert.AreEqual(18, priced.Count, "the patch must price exactly the nine dyes (18 rows), nothing else");
        }

        [TestMethod]
        public void Bloodwarden_ShopItems_CarryOnlyAllowlistedCosmeticProperties()
        {
            var violations = new List<string>();
            foreach (var (wcid, w) in ParsedShop(violations))
            {
                foreach (var (type, value) in w.Table("weenie_properties_int"))
                {
                    if (NeverInts.TryGetValue(type, out var never))
                        violations.Add($"{wcid}: {never} (int {type}) = {value} - a shop item must never carry it");
                    else if (RatingInts.Contains(type))
                        violations.Add($"{wcid}: gear/luminance rating int {type} ({(PropertyInt)type}) = {value}");
                    else if (!AllowedInts.Contains(type))
                        violations.Add($"{wcid}: int {type} ({(PropertyInt)type}) = {value} is not on the cosmetic allowlist");
                }

                foreach (var (type, value) in w.Table("weenie_properties_float"))
                    if (!AllowedFloats.Contains(type))
                        violations.Add($"{wcid}: float {type} ({(PropertyFloat)type}) = {value} is not on the cosmetic allowlist");

                foreach (var (type, value) in w.Table("weenie_properties_d_i_d"))
                {
                    if (NeverDids.Contains(type))
                        violations.Add($"{wcid}: spell/proc data id {type} ({(PropertyDataId)type}) = {value}");
                    else if (!AllowedDids.Contains(type))
                        violations.Add($"{wcid}: data id {type} ({(PropertyDataId)type}) = {value} is not on the cosmetic allowlist");
                }

                foreach (var (type, value) in w.Table("weenie_properties_bool"))
                    if (!AllowedBools.Contains(type))
                        violations.Add($"{wcid}: bool {type} ({(PropertyBool)type}) = {value} is not on the cosmetic allowlist");

                foreach (var (type, _) in w.Table("weenie_properties_string"))
                    if (!AllowedStrings.Contains(type))
                        violations.Add($"{wcid}: string {type} ({(PropertyString)type}) is not on the cosmetic allowlist");
            }
            Assert.AreEqual(0, violations.Count, "Bloodwarden shop items carrying anything outside the cosmetic allowlist (Blood must buy cosmetics only):\n" + string.Join("\n", violations.Take(25)));
        }

        /// <summary>The allowlist itself must never drift into a power property.</summary>
        [TestMethod]
        public void Bloodwarden_Allowlist_ContainsNoKnownPowerId()
        {
            Assert.IsFalse(AllowedInts.Overlaps(NeverInts.Keys), "the int allowlist grew Damage, ArmorLevel or ItemWorkmanship");
            Assert.IsFalse(AllowedInts.Overlaps(RatingInts), "the int allowlist grew a gear or luminance rating");
            Assert.IsFalse(AllowedDids.Overlaps(NeverDids), "the data-id allowlist grew a spell or proc");
            Assert.IsFalse(PrestigeExtraInts.Overlaps(NeverInts.Keys) || PrestigeExtraInts.Overlaps(RatingInts), "the prestige extras grew a power int");
        }

        /// <summary>Every property a prestige item carries outside the cosmetic allowlist plus the named prestige extras.</summary>
        private static List<string> PrestigeViolations(uint wcid, ParsedWeenie w, bool allowExtras)
        {
            var violations = new List<string>();

            foreach (var (type, value) in w.Table("weenie_properties_int"))
            {
                if (NeverInts.TryGetValue(type, out var never))
                    violations.Add($"{wcid}: {never} (int {type}) = {value} - a shop item must never carry it");
                else if (RatingInts.Contains(type))
                    violations.Add($"{wcid}: gear/luminance rating int {type} ({(PropertyInt)type}) = {value}");
                else if (!AllowedInts.Contains(type) && !(allowExtras && PrestigeExtraInts.Contains(type)))
                    violations.Add($"{wcid}: int {type} ({(PropertyInt)type}) = {value}");
            }

            foreach (var (type, value) in w.Table("weenie_properties_float"))
                if (!AllowedFloats.Contains(type))
                    violations.Add($"{wcid}: float {type} ({(PropertyFloat)type}) = {value}");

            foreach (var (type, value) in w.Table("weenie_properties_d_i_d"))
                if (NeverDids.Contains(type) || !AllowedDids.Contains(type))
                    violations.Add($"{wcid}: data id {type} ({(PropertyDataId)type}) = {value}");

            foreach (var (type, value) in w.Table("weenie_properties_bool"))
                if (!AllowedBools.Contains(type) && !(allowExtras && PrestigeExtraBools.Contains(type)))
                    violations.Add($"{wcid}: bool {type} ({(PropertyBool)type}) = {value}");

            foreach (var (type, _) in w.Table("weenie_properties_string"))
                if (!AllowedStrings.Contains(type) && !(allowExtras && PrestigeExtraStrings.Contains(type)))
                    violations.Add($"{wcid}: string {type} ({(PropertyString)type})");

            if (!allowExtras && w.Table(PaletteTable).Count > 0)
                violations.Add($"{wcid}: {w.Table(PaletteTable).Count} palette row(s)");

            return violations;
        }

        /// <summary>
        /// The prestige items (PrestigeItems): each is stocked by exactly its named vendor and no other, is fully parsed, costs
        /// its own price on shelf and till, is gated (PvpPrestigeGated = True), Attuned and not sellable, and carries nothing
        /// beyond the cosmetic allowlist plus the named prestige extras - never a power id.
        /// </summary>
        [TestMethod]
        public void Hopeslayer_PrestigeItems_AreGatedPricedAndCarryNoPower()
        {
            Assert.IsTrue(PrestigeItems.Count > 0, "the check would pass vacuously");

            foreach (var (wcid, (vendor, price)) in PrestigeItems)
            {
                CollectionAssert.Contains(ShopWcids(vendor), wcid, $"prestige item {wcid} must be a Shop row of vendor {vendor}");
                foreach (var other in VendorWcids.Where(v => v != vendor))
                    Assert.IsFalse(ShopWcids(other).Contains(wcid), $"prestige item {wcid} is also stocked by vendor {other}");

                var w = Parse(WeenieSql(wcid), wcid, allowPaletteRows: true);
                Assert.AreEqual(0, w.Problems.Count, $"prestige item {wcid} not fully parsed:\n" + string.Join("\n", w.Problems));

                Assert.AreEqual(price, IntValue(w, (int)PropertyInt.Value), $"{wcid} Value");
                Assert.AreEqual(price, IntValue(w, (int)PropertyInt.StackUnitValue), $"{wcid} StackUnitValue");
                Assert.AreEqual(1, IntValue(w, (int)PropertyInt.Attuned), $"{wcid} must be Attuned");
                Assert.AreEqual(1, IntValue(w, (int)PropertyInt.Bonded), $"{wcid} must be Bonded");

                // Palette rows: ObjDesc writes offset and length as ONE byte each, and a palette has 256 units of 8 colours, so
                // every row must fit both, and the rows must tile units 0-255 exactly with no overlap (a gap would leave
                // the wearer's own colours showing through; an overlap means two rows fight over the same units).
                var palette = w.Table(PaletteTable)
                    .Select(r => r.Value.Split(':'))
                    .Select(v => (Offset: int.Parse(v[0]), Length: int.Parse(v[1])))
                    .OrderBy(r => r.Offset)
                    .ToList();
                Assert.IsTrue(palette.Count > 0, $"{wcid} has no palette rows to check");
                var next = 0;
                foreach (var (offset, length) in palette)
                {
                    Assert.IsTrue(offset <= 255, $"{wcid}: palette offset {offset} does not fit a byte");
                    Assert.IsTrue(length >= 1 && length <= 255, $"{wcid}: palette length {length} at offset {offset} is not 1-255");
                    Assert.IsTrue(offset + length <= 256, $"{wcid}: palette row {offset}+{length} runs past unit 256");
                    Assert.AreEqual(next, offset, $"{wcid}: palette rows leave a gap or overlap at unit {next} (next row starts at {offset})");
                    next = offset + length;
                }
                Assert.AreEqual(256, next, $"{wcid}: palette rows end at unit {next}, not 256");

                string Bool(PropertyBool b) => w.Table("weenie_properties_bool").Where(r => r.Type == (int)b).Select(r => r.Value).SingleOrDefault();
                Assert.AreEqual("True", Bool(PropertyBool.PvpPrestigeGated), $"{wcid} must carry PvpPrestigeGated = True, or anyone with the Marks can buy it");
                Assert.AreEqual("False", Bool(PropertyBool.IsSellable), $"{wcid} must not sell back for pyreals");

                var violations = PrestigeViolations(wcid, w, allowExtras: true);
                Assert.AreEqual(0, violations.Count, $"prestige item {wcid} carries something outside the cosmetic allowlist and the prestige extras:\n" + string.Join("\n", violations));

                // Positive control: the exemption is load-bearing and narrow. Without the extras the same file fails, and only on
                // the named extras it actually carries.
                var strict = PrestigeViolations(wcid, w, allowExtras: false);
                Assert.IsTrue(strict.Count > 0, $"{wcid} needs no prestige extras; it should be an ordinary clone instead");
                Assert.IsTrue(strict.Any(v => v.Contains("bool 9076")), $"{wcid}: the strict check must flag PvpPrestigeGated:\n" + string.Join("\n", strict));
            }
        }

        [TestMethod]
        public void Bloodwarden_UsesBloodAsItsCurrency()
        {
            foreach (var wcid in VendorWcids)
            {
                var vendor = Parse(WeenieSql(wcid), wcid);
                var currency = vendor.Table("weenie_properties_d_i_d").Where(r => r.Type == (int)PropertyDataId.AlternateCurrency).ToList();
                Assert.AreEqual(1, currency.Count, $"vendor {wcid} must carry exactly one AlternateCurrency (DID 57) row");
                Assert.AreEqual(BloodWcid.ToString(), currency[0].Value, $"vendor {wcid}'s AlternateCurrency must be Blood (1006650)");
            }
        }

        /// <summary>
        /// Every Mark price is the item's own Value times the vendor's SellPrice (Vendor.GetSellCost), so the prices
        /// pinned above hold only while each vendor's SellPrice (float 38) is exactly 1.
        /// </summary>
        [TestMethod]
        public void Hopeslayer_EachVendor_HasSellPriceExactlyOne()
        {
            foreach (var wcid in VendorWcids)
            {
                var vendor = Parse(WeenieSql(wcid), wcid);
                var sellPrice = vendor.Table("weenie_properties_float").Where(r => r.Type == (int)PropertyFloat.SellPrice).ToList();
                Assert.AreEqual(1, sellPrice.Count, $"vendor {wcid} must carry exactly one SellPrice (float 38) row");
                Assert.AreEqual(1.0, double.Parse(sellPrice[0].Value, System.Globalization.CultureInfo.InvariantCulture), $"vendor {wcid}'s SellPrice must be 1, or every Mark price drifts");
            }
        }

        /// <summary>The split must leave four distinct vendors: the four wcids are all in the shop-vendor list the other checks use.</summary>
        [TestMethod]
        public void Bloodwarden_ShopStock_IsExactlyTheKnownSet_PerVendorAndInTotal()
        {
            var total = 0;
            foreach (var wcid in VendorWcids)
            {
                var n = ShopWcids(wcid).Count;
                total += n;
                Assert.AreEqual(ExpectedShopLinesPerVendor[wcid], n, $"vendor {wcid} has {n} shop lines; expected exactly {ExpectedShopLinesPerVendor[wcid]} (update deliberately if stock changed on purpose)");
            }
            Assert.AreEqual(ExpectedTotalShopLines, total, "total shop lines across the four vendors changed; update the expected counts deliberately if this was intended");
        }

        [TestMethod]
        public void Bloodwarden_IsFourVendors_WithTheirOwnShops()
        {
            CollectionAssert.AreEqual(new uint[] { 1004130, 1006651, 1006652, 1006653 }, VendorWcids);
            foreach (var wcid in VendorWcids)
                Assert.IsTrue(ShopWcids(wcid).Count >= MinimumShopLinesPerVendor, $"vendor {wcid} stocks too few lines");
        }

        /// <summary>
        /// Each vendor wears its own retail look (owner spec 2026-10-15): Setup from the look source, and no
        /// HeritageGroup int (188), so Creature.GenerateNewFace (Creature.cs:207) never randomises a head - the old
        /// Undead look needed a HeadObject pin for that, which is gone with it.
        /// </summary>
        [TestMethod]
        public void Hopeslayer_EachVendorWearsItsRetailLook_WithNoRandomHead()
        {
            var expectedSetup = new Dictionary<uint, uint>
            {
                { 1004130, 0x020013F1 }, // Shonossoar the Song-Keeper, retail 33009
                { 1006651, 0x020015FF }, // Count Dardante, retail 87595
                { 1006652, 0x02000001 }, // Faurset, retail 87516
                { 1006653, 0x02001BFB }, // Tormented Shadow, retail 51876
            };
            foreach (var wcid in VendorWcids)
            {
                var vendor = Parse(WeenieSql(wcid), wcid);
                var setup = vendor.Table("weenie_properties_d_i_d").Where(r => r.Type == (int)PropertyDataId.Setup).ToList();
                Assert.AreEqual(1, setup.Count, $"vendor {wcid} must carry exactly one Setup");
                Assert.AreEqual(expectedSetup[wcid], Convert.ToUInt32(setup[0].Value, 16), $"vendor {wcid}'s Setup");
                Assert.IsNull(IntValue(vendor, (int)PropertyInt.HeritageGroup), $"vendor {wcid} carries a HeritageGroup int, so GenerateNewFace would randomise its head");
            }
        }

        [TestMethod]
        public void Blood_IsBondedAttunedAndStacksTo1000()
        {
            var blood = Parse(WeenieSql(BloodWcid), BloodWcid);
            Assert.AreEqual(1, IntValue(blood, (int)PropertyInt.Bonded), "Blood must be Bonded (int 33) = 1");
            Assert.AreEqual(1, IntValue(blood, (int)PropertyInt.Attuned), "Blood must be Attuned (int 114) = 1");
            Assert.AreEqual(1000, IntValue(blood, (int)PropertyInt.MaxStackSize), "Blood must stack to 1000 (int 11)");
        }
    }
}
