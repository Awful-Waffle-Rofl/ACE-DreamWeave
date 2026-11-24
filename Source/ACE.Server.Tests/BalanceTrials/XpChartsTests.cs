using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.BalanceTrials;
using ACE.Server.Entity;

namespace ACE.Server.Tests.BalanceTrials
{
    [TestClass]
    public class XpChartsTests
    {
        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        [TestMethod]
        public void FromBase_totals_equal_the_live_Extend_output()
        {
            // retail-shaped: last two deltas 200 and 400, so the tail doubles each level: 700 + 800 = 1500, + 1600 = 3100
            var baseTotals = new ulong[] { 0, 100, 300, 700 };

            var charts = XpCharts.FromBase(baseTotals, new uint[] { 0, 1, 2, 3 }, new uint[] { 1 }, new uint[] { 2 }, new uint[] { 3 }, new uint[] { 4 },
                new Dictionary<Skill, int> { [Skill.Sword] = 6 });

            CollectionAssert.AreEqual(EnlightenmentXpCurve.Extend(baseTotals, EnlightenmentXpCurve.TotalCap), charts.LevelTotals.ToList());

            // golden tail values worked by hand from the doubling rule, so this is not a function compared with itself
            CollectionAssert.AreEqual(new ulong[] { 0, 100, 300, 700, 1500, 3100, 6300 }, charts.LevelTotals.Take(7).ToList());
            Assert.IsTrue(charts.LevelTotals.Count > 7, "the tail runs on to the overflow cap");
            Assert.IsTrue(charts.LevelTotals[charts.LevelTotals.Count - 1] <= EnlightenmentXpCurve.TotalCap);

            CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3 }, charts.LevelCredits.ToList());
            Assert.AreEqual(6, charts.TrainedCost[Skill.Sword]);
            Assert.AreEqual(1u, charts.TrainedSkillXp[0]);
            Assert.AreEqual(2u, charts.SpecializedSkillXp[0]);
            Assert.AreEqual(3u, charts.AttributeXp[0]);
            Assert.AreEqual(4u, charts.VitalXp[0]);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Live_charts_match_the_dat()
        {
            RequireDats();

            var table = DatManager.PortalDat.XpTable;
            var charts = XpCharts.Live();

            // identity, not equality: Live() must hand over each XpTable list for its own field, so a swap fails
            Assert.AreSame(table.CharacterLevelSkillCreditList, charts.LevelCredits);
            Assert.AreSame(table.TrainedSkillXpList, charts.TrainedSkillXp);
            Assert.AreSame(table.SpecializedSkillXpList, charts.SpecializedSkillXp);
            Assert.AreSame(table.AttributeXpList, charts.AttributeXp);
            Assert.AreSame(table.VitalXpList, charts.VitalXp);

            // the level chart is the retail one verbatim up to its end, then the extension of the dat's base chart
            CollectionAssert.AreEqual(table.CharacterLevelXPList, charts.LevelTotals.Take(table.CharacterLevelXPList.Count).ToList());
            CollectionAssert.AreEqual(EnlightenmentXpCurve.Extend(table.CharacterLevelXPList, EnlightenmentXpCurve.TotalCap), charts.LevelTotals.ToList());
            Assert.IsTrue(charts.LevelTotals.Count > table.CharacterLevelXPList.Count);

            foreach (var (id, skillBase) in DatManager.PortalDat.SkillTable.SkillBaseHash)
                Assert.AreEqual(skillBase.TrainedCost, charts.TrainedCost[(Skill)id], $"trained cost of skill {id}");
            Assert.AreEqual(DatManager.PortalDat.SkillTable.SkillBaseHash.Count, charts.TrainedCost.Count);
        }

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
                return null;

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
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
    }
}
