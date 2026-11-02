using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Knots;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// KnotItems: the predicates read PropertyInt.KnotItemKind and nothing else, the text formats are the ones
    /// the design names, and the five shipped weenie rows carry the kind and bind flags the predicates and the
    /// trade rules depend on.
    /// </summary>
    [TestClass]
    public class KnotItemsTests
    {
        private static uint nextGuid = 0x7E100000;

        private static WorldObject Item(uint wcid, int? kind)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Gem } },
            };
            if (kind.HasValue)
                weenie.PropertiesInt[PropertyInt.KnotItemKind] = kind.Value;

            return WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void KindValues_AreTheDocumentedOnes()
        {
            Assert.AreEqual(1, KnotItemKind.Fiber);
            Assert.AreEqual(2, KnotItemKind.Knot);
            Assert.AreEqual(3, KnotItemKind.Pattern);
            Assert.AreEqual(4, KnotItemKind.Pouch);
        }

        [TestMethod]
        public void Predicates_ReadTheKindProperty_EachMatchesExactlyOneKind()
        {
            var cases = new (int kind, Func<WorldObject, bool> pred, string name)[]
            {
                (KnotItemKind.Fiber, KnotItems.IsFiber, "IsFiber"),
                (KnotItemKind.Knot, KnotItems.IsKnot, "IsKnot"),
                (KnotItemKind.Pattern, KnotItems.IsPattern, "IsPattern"),
                (KnotItemKind.Pouch, KnotItems.IsPouch, "IsPouch"),
            };

            foreach (var holder in cases)
            {
                var item = Item(900000, holder.kind);
                foreach (var probe in cases)
                    Assert.AreEqual(probe.kind == holder.kind, probe.pred(item), $"{probe.name} on a kind-{holder.kind} item");
            }
        }

        [TestMethod]
        public void Predicates_NeverCompareWcids()
        {
            // The real Fiber wcid with no kind property is not a Fiber; an unrelated wcid with the property is.
            Assert.IsFalse(KnotItems.IsFiber(Item(KnotItems.FiberWcid, null)), "Fiber wcid without the kind property");
            Assert.IsFalse(KnotItems.IsKnot(Item(KnotItems.KnotWcid, null)));
            Assert.IsFalse(KnotItems.IsPattern(Item(KnotItems.PatternWcid, null)));
            Assert.IsFalse(KnotItems.IsPouch(Item(KnotItems.PouchWcid, null)));

            Assert.IsTrue(KnotItems.IsFiber(Item(12345, KnotItemKind.Fiber)), "any wcid carrying kind=Fiber");
            Assert.IsTrue(KnotItems.IsPouch(Item(KnotItems.FiberWcid, KnotItemKind.Pouch)), "kind wins over a mismatched wcid");
            Assert.IsFalse(KnotItems.IsFiber(Item(KnotItems.FiberWcid, KnotItemKind.Pouch)));
        }

        [TestMethod]
        public void Predicates_NullAndUnrelatedItems_AreFalse()
        {
            Assert.IsFalse(KnotItems.IsFiber((WorldObject)null));
            Assert.IsFalse(KnotItems.IsKnotDrop((WorldObject)null));
            Assert.AreEqual(KnotItemKind.None, KnotItems.KindOf(null));
            Assert.AreEqual(KnotItemKind.None, KnotItems.KindOf(Item(900001, null)));
            Assert.IsFalse(KnotItems.IsKnotDrop(Item(900001, null)));
        }

        [TestMethod]
        public void IsKnotDrop_IsFiberOrPattern_Only()
        {
            Assert.IsTrue(KnotItems.IsKnotDrop(Item(900002, KnotItemKind.Fiber)));
            Assert.IsTrue(KnotItems.IsKnotDrop(Item(900002, KnotItemKind.Pattern)));
            Assert.IsFalse(KnotItems.IsKnotDrop(Item(900002, KnotItemKind.Knot)));
            Assert.IsFalse(KnotItems.IsKnotDrop(Item(900002, KnotItemKind.Pouch)));
        }

        [TestMethod]
        public void IntOverloads_AgreeWithTheWorldObjectOnes()
        {
            Assert.IsTrue(KnotItems.IsFiber(1) && KnotItems.IsKnot(2) && KnotItems.IsPattern(3) && KnotItems.IsPouch(4));
            Assert.IsFalse(KnotItems.IsFiber(0) || KnotItems.IsKnot(1) || KnotItems.IsPattern(4) || KnotItems.IsPouch(3));
            Assert.IsTrue(KnotItems.IsKnotDrop(1) && KnotItems.IsKnotDrop(3));
            Assert.IsFalse(KnotItems.IsKnotDrop(0) || KnotItems.IsKnotDrop(2) || KnotItems.IsKnotDrop(4));
        }

        [TestMethod]
        public void Names_FollowTheDesignFormats()
        {
            Assert.AreEqual("Fiber (Level 342)", KnotItems.FiberName(342));
            Assert.AreEqual("Knot (Level 280)", KnotItems.KnotName(280));
            Assert.AreEqual("Knot of the Ember Tyrant (Level 280)", KnotItems.BoundKnotName("Ember Tyrant", 280));
            Assert.AreEqual("Pattern: Ember Tyrant", KnotItems.PatternName("Ember Tyrant"));
        }

        [TestMethod]
        public void ChatLines_MatchThePlaceholderText()
        {
            Assert.AreEqual("A Fiber has caught in your Thread Cache. (Fiber 7 of 10)", KnotItems.FiberLanded(7));
            Assert.AreEqual("A Pattern has caught in your Thread Cache: Ember Tyrant.", KnotItems.PatternLanded("Ember Tyrant"));
        }

        [TestMethod]
        public void PatternFillLines_MatchTheOwnerCopy()
        {
            Assert.AreEqual("Use a Pattern on this Fiber to weave it in.", KnotItems.FiberNeedsPattern);
            Assert.AreEqual("Use this Pattern on a Fiber.", KnotItems.PatternNeedsTarget);
            Assert.AreEqual("A Pattern can only take a Fiber from your pack.", KnotItems.PatternNeedsFiber);
            Assert.AreEqual("The Fiber weaves into the Pattern. (3 of 10, Level 120)", KnotItems.PatternFiberAdded(3, 120));
            Assert.AreEqual("The Pattern is complete, but you have no room for the Knot. Make room, then use the Pattern.", KnotItems.PatternNoRoom);
            Assert.AreEqual("The Pattern draws tight. You now hold a Knot of the Ember Tyrant (Level 280).", KnotItems.PatternCompleted("Ember Tyrant", 280));
        }

        [TestMethod]
        public void PatternName_ShowsTheFillOnlyOnceAFiberIsIn()
        {
            Assert.AreEqual("Pattern: Ember Tyrant", KnotItems.PatternName("Ember Tyrant", 0, 0));
            Assert.AreEqual("Pattern: Ember Tyrant (4/10, Level 120)", KnotItems.PatternName("Ember Tyrant", 4, 120));
            Assert.AreEqual("Pattern: Ember Tyrant (10/10, Level 300)", KnotItems.PatternName("Ember Tyrant", 10, 300));
        }

        [TestMethod]
        public void FiberLanded_CapsTheCountAtTen_AndNeverShowsLessThanOne()
        {
            StringAssert.Contains(KnotItems.FiberLanded(10), "(Fiber 10 of 10)");
            StringAssert.Contains(KnotItems.FiberLanded(14), "(Fiber 10 of 10)");
            StringAssert.Contains(KnotItems.FiberLanded(0), "(Fiber 1 of 10)");
        }

        [TestMethod]
        public void RefusalLines_ArePresentAndDistinct()
        {
            var lines = new[]
            {
                KnotItems.PouchAcceptsFibersOnly, KnotItems.FiberNeedsPattern, KnotItems.PatternNeedsTarget, KnotItems.PatternNeedsFiber,
                KnotItems.PatternNoRoom, KnotItems.NoPinnacleAvailable, KnotItems.PinnacleDisabled,
            };
            Assert.IsTrue(lines.All(l => !string.IsNullOrWhiteSpace(l)));
            Assert.AreEqual(lines.Length, lines.Distinct().Count());
        }

        [TestMethod]
        public void Text_IsAsciiOnly()
        {
            var all = new[]
            {
                KnotItems.FiberName(1), KnotItems.KnotName(1), KnotItems.BoundKnotName("x", 1), KnotItems.PatternName("x"),
                KnotItems.FiberLanded(3), KnotItems.PatternLanded("x"), KnotItems.PatternName("x", 3, 1), KnotItems.PatternFiberAdded(3, 1),
                KnotItems.PatternCompleted("x", 1), KnotItems.FiberNeedsPattern, KnotItems.PatternNeedsTarget, KnotItems.PatternNeedsFiber,
                KnotItems.PatternNoRoom, KnotItems.PouchAcceptsFibersOnly, KnotItems.NoPinnacleAvailable, KnotItems.PinnacleDisabled,
            };
            Assert.IsTrue(all.All(s => s.All(c => c >= 0x20 && c < 0x7F)));
        }

        // ---- the shipped weenie rows ----

        private static string WeenieSql(string wcidAndName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", wcidAndName + ".sql");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            Assert.Fail($"Content/sql/weenies/{wcidAndName}.sql not found by walking up from the test assembly");
            return null;
        }

        private static bool HasInt(string sql, uint wcid, int type, int value)
            => Regex.IsMatch(sql, @"\(\s*" + wcid + @",\s*" + type + @",\s*" + value + @"\s*\)");

        [TestMethod]
        public void ShippedWeenies_CarryTheirKind()
        {
            Assert.IsTrue(HasInt(WeenieSql("1007000 Fiber"), KnotItems.FiberWcid, (int)PropertyInt.KnotItemKind, KnotItemKind.Fiber));
            Assert.IsTrue(HasInt(WeenieSql("1007001 Knot"), KnotItems.KnotWcid, (int)PropertyInt.KnotItemKind, KnotItemKind.Knot));
            Assert.IsTrue(HasInt(WeenieSql("1007002 Knotting Pouch"), KnotItems.PouchWcid, (int)PropertyInt.KnotItemKind, KnotItemKind.Pouch));
            Assert.IsTrue(HasInt(WeenieSql("1007003 Pattern"), KnotItems.PatternWcid, (int)PropertyInt.KnotItemKind, KnotItemKind.Pattern));
        }

        [TestMethod]
        public void ShippedWeenies_FiberIsTradeableAndBonded_KnotAndPouchAreAttunedAndBonded_PatternIsNeither()
        {
            // owner ruling 2026-10-29: Fibers and Patterns are tradeable, Knots are not. Bonded stays on the Fiber (death drop only; it blocks no give or trade).
            var fiber = WeenieSql("1007000 Fiber");
            Assert.IsFalse(Regex.IsMatch(fiber, @"\(\s*1007000,\s*114,"), "Fiber carries no Attuned (114) row");
            Assert.IsTrue(HasInt(fiber, KnotItems.FiberWcid, (int)PropertyInt.Bonded, (int)BondedStatus.Bonded), "Fiber Bonded (33) = 1 on the weenie row");

            foreach (var (file, wcid) in new[] { ("1007001 Knot", KnotItems.KnotWcid), ("1007002 Knotting Pouch", KnotItems.PouchWcid) })
            {
                var sql = WeenieSql(file);
                Assert.IsTrue(HasInt(sql, wcid, (int)PropertyInt.Attuned, (int)AttunedStatus.Attuned), $"{file} Attuned (114) = 1 on the weenie row");
                Assert.IsTrue(HasInt(sql, wcid, (int)PropertyInt.Bonded, (int)BondedStatus.Bonded), $"{file} Bonded (33) = 1 on the weenie row");
            }
            var pattern = WeenieSql("1007003 Pattern");
            Assert.IsFalse(Regex.IsMatch(pattern, @"\(\s*1007003,\s*(114|33),"), "Pattern is tradeable: no Attuned or Bonded row");
        }

        [TestMethod]
        public void ShardMigration_ClearsAttunedFromExistingFibersOnly()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string sql = null;
            while (dir != null && sql == null)
            {
                var candidate = Path.Combine(dir.FullName, "Database", "Updates", "Shard", "2026-10-29-01-Knots-Fiber-Tradeable-Existing-Items.sql");
                if (File.Exists(candidate)) sql = File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.IsNotNull(sql, "the Fiber shard migration was not found by walking up from the test assembly");

            var statements = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline).Split(';')
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            Assert.AreEqual(1, statements.Count, "one statement");
            StringAssert.StartsWith(statements[0], "DELETE bi FROM `biota_properties_int`");
            StringAssert.Contains(statements[0], "bi.`type` = 114");
            StringAssert.Contains(statements[0], "`weenie_Class_Id` = 1007000");
            Assert.IsFalse(statements[0].Contains("1007001") || statements[0].Contains("1007002"), "the Knot and the Pouch stay Attuned");
        }

        [TestMethod]
        public void ShardMigration_IconAndText_MatchTheWeenieTemplates()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string sql = null;
            while (dir != null && sql == null)
            {
                var candidate = Path.Combine(dir.FullName, "Database", "Updates", "Shard", "2026-10-30-01-Knots-Existing-Items-Icon-And-Text.sql");
                if (File.Exists(candidate)) sql = File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.IsNotNull(sql, "the icon and text shard migration was not found by walking up from the test assembly");

            // Every appraisal string the migration writes onto existing items must be the template's current text, so an
            // edit to one side without the other fails here instead of leaving old and new items disagreeing.
            foreach (var (file, wcid, type) in new[] {
                ("1007000 Fiber", 1007000u, 16), ("1007001 Knot", 1007001u, 16), ("1007002 Knotting Pouch", 1007002u, 16),
                ("1007003 Pattern", 1007003u, 14), ("1007003 Pattern", 1007003u, 16) })
            {
                var m = Regex.Match(WeenieSql(file), $@"\(\s*{wcid},\s*{type},\s*('(?:[^']|'')*')\)");
                Assert.IsTrue(m.Success, $"{file} has a string {type} row");
                StringAssert.Contains(sql, m.Groups[1].Value, $"{file} string {type} matches the migration");
            }

            var pattern = WeenieSql("1007003 Pattern");
            StringAssert.Matches(pattern, new Regex(@"\(\s*1007003,\s*8,\s*0x06006582\)"), "Pattern Icon is the Falatacot Trinket");
            StringAssert.Matches(pattern, new Regex(@"\(\s*1007003,\s*52,\s*0x06005B0C\)"), "Pattern IconUnderlay is the rare purple");
            StringAssert.Contains(sql, "0x06006582");
            StringAssert.Contains(sql, "0x06005B0C");
            Assert.IsFalse(Regex.IsMatch(sql, @"100700[4-9]"), "scoped to wcids 1007000..1007003");
        }
        [TestMethod]
        public void ShippedWeenies_FiberKnotPatternAreNotStackable_PouchHasTenSlots()
        {
            foreach (var (file, wcid) in new[] { ("1007000 Fiber", KnotItems.FiberWcid), ("1007001 Knot", KnotItems.KnotWcid), ("1007003 Pattern", KnotItems.PatternWcid) })
                Assert.IsTrue(HasInt(WeenieSql(file), wcid, (int)PropertyInt.MaxStackSize, 1), $"{file} MaxStackSize 1");

            var pouch = WeenieSql("1007002 Knotting Pouch");
            Assert.IsTrue(HasInt(pouch, KnotItems.PouchWcid, (int)PropertyInt.ItemsCapacity, KnotItems.FibersPerKnot));
            Assert.IsTrue(HasInt(pouch, KnotItems.PouchWcid, (int)PropertyInt.ContainersCapacity, 0));
            StringAssert.Matches(pouch, new Regex(@"VALUES \(1007002, '[^']+', 21,"));
        }

        [TestMethod]
        public void ShippedPattern_TargetsAGem_FromThePacks()
        {
            var sql = WeenieSql("1007003 Pattern");
            Assert.IsTrue(HasInt(sql, KnotItems.PatternWcid, (int)PropertyInt.ItemUseable, 0x00080008));
            Assert.IsTrue(HasInt(sql, KnotItems.PatternWcid, (int)PropertyInt.TargetType, (int)ItemType.Gem));
        }

        [TestMethod]
        public void KnottedThreadCache_IsAChestWithADifferentSetupThanTheThreadCache()
        {
            var plain = WeenieSql("1003603 Thread Cache");
            var knotted = WeenieSql("1007004 Knotted Thread Cache");
            StringAssert.Matches(knotted, new Regex(@"VALUES \(1007004, '[^']+', 20,"));

            var plainSetup = Regex.Match(plain, @"\(1003603,\s+1,\s+(0x[0-9A-Fa-f]{8})\)");
            var knottedSetup = Regex.Match(knotted, @"\(1007004,\s+1,\s+(0x[0-9A-Fa-f]{8})\)");
            Assert.IsTrue(plainSetup.Success && knottedSetup.Success);
            Assert.AreNotEqual(plainSetup.Groups[1].Value.ToUpperInvariant(), knottedSetup.Groups[1].Value.ToUpperInvariant());
        }

        [TestMethod]
        public void PinnacleCache_IsAChestWithTheKnottedCachesSetup()
        {
            var knotted = WeenieSql("1007004 Knotted Thread Cache");
            var pinnacle = WeenieSql("1007005 Pinnacle Cache");
            StringAssert.Matches(pinnacle, new Regex(@"VALUES \(1007005, '[^']+', 20,"));

            var knottedSetup = Regex.Match(knotted, @"\(1007004,\s+1,\s+(0x[0-9A-Fa-f]{8})\)");
            var pinnacleSetup = Regex.Match(pinnacle, @"\(1007005,\s+1,\s+(0x[0-9A-Fa-f]{8})\)");
            Assert.IsTrue(knottedSetup.Success && pinnacleSetup.Success);
            Assert.AreEqual(knottedSetup.Groups[1].Value.ToUpperInvariant(), pinnacleSetup.Groups[1].Value.ToUpperInvariant());
        }
    }
}
