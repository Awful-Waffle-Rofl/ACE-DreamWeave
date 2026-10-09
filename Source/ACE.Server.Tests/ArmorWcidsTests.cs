using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables.Wcids;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Structural regression coverage for the Ancient Heaume (wcid 8396, heaumeold) loot slot added
    /// alongside heaumenew (wcid 8489) in the plate-family armor tables (PlatemailWcids,
    /// ScalemailWcids, YoroiWcids, DiforsaWcids).
    ///
    /// WHY THE ARITHMETIC TESTS EXIST: a ChanceTable that does not sum to 1.0 fails SILENTLY.
    /// ChanceTable.Roll walks a cumulative sum against a uniform [0,1) draw and returns the LAST
    /// non-zero entry for any draw the sum never reaches, and ChanceTable.VerifyTable only
    /// log.Errors a mismatch rather than throwing (Source/ACE.Server/Factories/Entity/ChanceTable.cs).
    /// So an arithmetic slip while editing these tables does not crash, does not fail an existing
    /// test, and does not stop a server - it quietly redirects the missing probability mass onto
    /// whichever entry happens to be written last. These asserts cover exactly the four tables this
    /// Ancient Heaume change edited, using the same decimal accumulation and 1e-7 threshold VerifyTable
    /// uses, so a table that passes here is a table that will not log an error at runtime.
    /// </summary>
    [TestClass]
    public class ArmorWcidsTests
    {
        private const decimal Threshold = 0.0000001M;

        private static readonly string[] EditedTableFieldNames =
        {
            "PlatemailWcids",
            "ScalemailWcids",
            "YoroiWcids",
            "DiforsaWcids",
        };

        private static List<(string Name, ChanceTable<WeenieClassName> Table)> GetEditedTables()
        {
            var tables = new List<(string, ChanceTable<WeenieClassName>)>();

            foreach (var name in EditedTableFieldNames)
            {
                var field = typeof(ArmorWcids).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(field, $"ArmorWcids.{name} not found - the field was renamed or removed.");

                tables.Add((name, (ChanceTable<WeenieClassName>)field.GetValue(null)));
            }

            return tables;
        }

        [TestMethod]
        public void HeaumeOldAndHeaumeNew_BothResolveToPlatemail()
        {
            var resolvedOld = ArmorWcids.TryGetValue(WeenieClassName.heaumeold, out var oldArmorType);
            var resolvedNew = ArmorWcids.TryGetValue(WeenieClassName.heaumenew, out var newArmorType);

            Assert.IsTrue(resolvedOld, "heaumeold (wcid 8396) is not in ArmorWcids._combined at all.");
            Assert.IsTrue(resolvedNew, "heaumenew (wcid 8489) is not in ArmorWcids._combined at all.");

            Assert.AreEqual(TreasureArmorType.Platemail, oldArmorType,
                "heaumeold resolves to a different TreasureArmorType than expected - _combined is " +
                "built by first-table-wins (ArmorWcids.BuildCombined), and PlatemailWcids is built " +
                "before ScalemailWcids/YoroiWcids/DiforsaWcids, so both heaume wcids should land on " +
                "Platemail today.");
            Assert.AreEqual(TreasureArmorType.Platemail, newArmorType,
                "heaumenew resolves to a different TreasureArmorType than expected.");
        }

        [TestMethod]
        public void EditedPlatemailFamilyTables_SumToOne()
        {
            var tables = GetEditedTables();

            foreach (var (name, table) in tables)
            {
                var total = table.Aggregate(0.0M, (acc, entry) => acc + (decimal)entry.chance);

                Assert.IsTrue(Math.Abs(1.0M - total) <= Threshold,
                    $"{name} sums to {total}, expected 1.0. ChanceTable.Roll would silently hand the " +
                    $"{1.0M - total} shortfall to its last entry.");
            }
        }

        [TestMethod]
        public void EditedPlatemailFamilyTables_HaveNoDuplicateWcids()
        {
            var tables = GetEditedTables();

            foreach (var (name, table) in tables)
            {
                var duplicates = table.GroupBy(entry => entry.result)
                                       .Where(group => group.Count() > 1)
                                       .Select(group => group.Key.ToString())
                                       .ToList();

                Assert.AreEqual(0, duplicates.Count,
                    $"{name} lists {string.Join(", ", duplicates)} more than once - a copy/paste slip " +
                    "that inflates that wcid's real weight without changing the sum.");
            }
        }
    }
}
