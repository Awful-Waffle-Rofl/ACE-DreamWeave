using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Every Hammer the forge can create must have a weenie to create: SalvageForge.MaterialTable names a HammerWcid per row,
    /// and a row with no Content/sql/weenies/&lt;wcid&gt; *.sql file would forge a missing item.
    /// </summary>
    [TestClass]
    public class SalvageForgeHammerWeenieTests
    {
        [TestMethod]
        public void EveryMaterialTableHammerWcid_HasAWeenieSqlFile()
        {
            var weenies = FindWeenieDirectory();

            var missing = SalvageForge.MaterialTable.Values
                .Where(m => !Directory.EnumerateFiles(weenies, $"{m.HammerWcid} *.sql").Any())
                .Select(m => $"{m.DisplayName} ({m.HammerWcid})")
                .ToList();

            Assert.AreEqual(0, missing.Count, "MaterialTable Hammer wcids with no Content/sql/weenies/<wcid> *.sql file: " + string.Join(", ", missing));
        }

        private static string FindWeenieDirectory()
        {
            for (var dir = new DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find Content/sql/weenies by walking up from {System.AppContext.BaseDirectory}");
            return null;
        }
    }
}