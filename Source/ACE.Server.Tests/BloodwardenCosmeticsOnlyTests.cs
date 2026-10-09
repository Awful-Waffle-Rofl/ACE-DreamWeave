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
    /// The cosmetics-only guard for the PvP arena's Bloodwarden vendor (wcid 1004130, restyled from Tegao
    /// Wavecounter) and its Blood currency (wcid 1006650).
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
        private const uint VendorWcid = 1004130;
        private const uint BloodWcid = 1006650;
        private const int ExpectedPrice = 5;

        // The three armor reduction tools (Main/Middle/Lower) were repriced to 1 Blood in #1590.
        private const int ReductionToolPrice = 1;
        private static readonly HashSet<uint> ReductionToolWcids = new() { 1004316, 1004317, 1004318 };

        // Every shop clone must be one of the generated/hand-curated cosmetic ranges; a count far below this
        // means the parse found nothing (a regex that matches zero rows would otherwise pass vacuously).
        private const int MinimumShopLines = 800;

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

        private static ParsedWeenie Parse(string sql, uint wcid)
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

        private static List<uint> ShopWcids()
        {
            var sql = Normalise(WeenieSql(VendorWcid));
            var shop = new List<uint>();
            var statements = Regex.Matches(sql, @"INSERT INTO `weenie_properties_create_list`(.*?);", RegexOptions.Singleline);
            Assert.IsTrue(statements.Count > 0, $"weenie {VendorWcid} has no weenie_properties_create_list statement");

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

                    if (owner != VendorWcid || (destination != 2 /* Wield */ && destination != 4 /* Shop */))
                        badRows.Add($"object {owner}, destination_Type {destination}, wcid {m.Groups[3].Value}");

                    if (owner == VendorWcid && destination == 4)
                    {
                        shopRows++;
                        shop.Add(uint.Parse(m.Groups[3].Value));
                    }
                }
            }

            Assert.AreEqual(rowsInFile, rowsMatched, $"weenie {VendorWcid}'s create_list has {rowsInFile} row(s) but only {rowsMatched} were parsed - an unreadable row is a shop item the guard never checked");
            Assert.AreEqual(0, badRows.Count, "create_list rows that are not this vendor's Wield (2) or Shop (4) rows:\n" + string.Join("\n", badRows.Take(10)));
            Assert.AreEqual(shopRows, shop.Count, "shop wcid count does not equal the number of destination_Type 4 rows");

            Assert.IsTrue(shop.Count >= MinimumShopLines, $"parsed only {shop.Count} shop lines from weenie {VendorWcid}; expected at least {MinimumShopLines} - the guard would pass vacuously");
            Assert.AreEqual(shop.Count, shop.Distinct().Count(), "duplicate shop wcids in the Bloodwarden's create_list");
            return shop;
        }

        /// <summary>Every shop clone, parsed; a file that is missing or not wholly accounted for is reported, never skipped.</summary>
        private static List<(uint Wcid, ParsedWeenie Parsed)> ParsedShop(List<string> violations)
        {
            var result = new List<(uint, ParsedWeenie)>();
            foreach (var wcid in ShopWcids())
            {
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
            var shopCount = ShopWcids().Count;
            var parsed = ParsedShop(violations);
            Assert.AreEqual(shopCount, parsed.Count, "some shop files were skipped instead of parsed:\n" + string.Join("\n", violations.Take(25)));
            Assert.AreEqual(0, violations.Count, "shop files the guard could not fully account for:\n" + string.Join("\n", violations.Take(25)));
        }

        [TestMethod]
        public void Bloodwarden_EveryShopItem_IsPricedFiveBlood()
        {
            var violations = new List<string>();
            foreach (var (wcid, w) in ParsedShop(violations))
            {
                var value = IntValue(w, (int)PropertyInt.Value);
                var unit = IntValue(w, (int)PropertyInt.StackUnitValue);
                var expected = ReductionToolWcids.Contains(wcid) ? ReductionToolPrice : ExpectedPrice;
                if (value != expected || unit != expected)
                    violations.Add($"{wcid}: Value={value?.ToString() ?? "absent"} StackUnitValue={unit?.ToString() ?? "absent"} (both must be {expected})");
            }
            Assert.AreEqual(0, violations.Count, "Bloodwarden shop items not at their expected Blood price:\n" + string.Join("\n", violations.Take(25)));
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
        }

        [TestMethod]
        public void Bloodwarden_UsesBloodAsItsCurrency()
        {
            var vendor = Parse(WeenieSql(VendorWcid), VendorWcid);
            var currency = vendor.Table("weenie_properties_d_i_d").Where(r => r.Type == (int)PropertyDataId.AlternateCurrency).ToList();
            Assert.AreEqual(1, currency.Count, "the Bloodwarden must carry exactly one AlternateCurrency (DID 57) row");
            Assert.AreEqual(BloodWcid.ToString(), currency[0].Value, "the Bloodwarden's AlternateCurrency must be Blood (1006650)");
        }

        /// <summary>
        /// Creature.GenerateNewFace (Creature.cs:207, for every non-Player creature) rolls a random head from the
        /// heritage's CharGen list unless HeadObject (DID 18) is already set (Creature.cs:359), so an Undead NPC
        /// without the pin spawns with a different skull each time. The Bloodwarden's look is the pinned one.
        /// </summary>
        [TestMethod]
        public void Bloodwarden_PinsItsUndeadHead()
        {
            var vendor = Parse(WeenieSql(VendorWcid), VendorWcid);
            var heads = vendor.Table("weenie_properties_d_i_d").Where(r => r.Type == (int)PropertyDataId.HeadObject).ToList();
            Assert.AreEqual(1, heads.Count, "the Bloodwarden must pin exactly one HeadObject (DID 18) or its head is randomised on every spawn");
            Assert.AreEqual(0x01004619u.ToString(), heads[0].Value, "the Bloodwarden's HeadObject must be the Undead-male CharGen head 0x01004619");
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
