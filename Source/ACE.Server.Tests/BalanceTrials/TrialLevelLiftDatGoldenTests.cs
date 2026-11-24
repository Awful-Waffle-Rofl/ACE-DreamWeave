using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.BalanceTrials;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The lift on the real client dat, pinned against golden values captured from origin/master ca2a8e2d1 on 2026-11-18
    /// (a throwaway worktree at that commit, ACE_DAT_PATH dats, a scratch test calling the live functions:
    /// EnlightenmentXpCurve.ExtendedTotals and GetSkillCreditsForLevel, the XpTable lists, Player.CalcSkillRank /
    /// CalcAttributeRank / CalcVitalRank, SkillBase.TrainedCost, Player.XpCostForClassAbilityPoints at the shipped
    /// defaults). Skips without dats; TrialLevelLiftTests carries the dat-free pins of the same logic.
    /// </summary>
    [TestClass]
    public class TrialLevelLiftDatGoldenTests
    {
        // Golden values, origin/master ca2a8e2d1, 2026-11-18.
        private const ulong Totals275 = 191_226_310_247;
        private const ulong Totals375 = 952_018_978_980;
        private const ulong Totals380 = 1_026_259_184_505;
        private const ulong Totals400 = 1_382_647_400_007;
        private const ulong Totals401 = 1_403_291_112_728;
        private const int TotalsCount = 1446;
        private const int Credits376To400 = 1;
        private const int Credits381To400 = 1;
        private const uint TrainedLast = 4_203_819_496; private const int TrainedCount = 209;
        private const uint SpecLast = 4_100_490_438; private const int SpecCount = 227;
        private const uint AttrLast = 4_019_438_644; private const int AttrCount = 191;
        private const uint VitalLast = 4_285_430_197; private const int VitalCount = 197;
        private const int CalcSkillRankSpec = 226, CalcSkillRankTrained = 208, CalcAttributeRank = 190, CalcVitalRank = 196;
        private const int WarMagicTrainedCost = 16, VoidMagicTrainedCost = 16;
        private const long XpCost0Plus1 = 200_000_000_000, XpCost3Plus2 = 1_317_120_000_000;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Live_charts_equal_the_ca2a8e2d1_goldens()
        {
            RequireDats();
            var c = XpCharts.Live();

            Assert.AreEqual(Totals275, c.LevelTotals[275]);
            Assert.AreEqual(Totals375, c.LevelTotals[375]);
            Assert.AreEqual(Totals380, c.LevelTotals[380]);
            Assert.AreEqual(Totals400, c.LevelTotals[400]);
            Assert.AreEqual(Totals401, c.LevelTotals[401]);
            Assert.AreEqual(TotalsCount, c.LevelTotals.Count);

            Assert.AreEqual(Credits376To400, TrialLevelLift.CreditsBetween(375, 400, c.LevelCredits));
            Assert.AreEqual(Credits381To400, TrialLevelLift.CreditsBetween(380, 400, c.LevelCredits));

            Assert.AreEqual(TrainedLast, c.TrainedSkillXp[^1]); Assert.AreEqual(TrainedCount, c.TrainedSkillXp.Count);
            Assert.AreEqual(SpecLast, c.SpecializedSkillXp[^1]); Assert.AreEqual(SpecCount, c.SpecializedSkillXp.Count);
            Assert.AreEqual(AttrLast, c.AttributeXp[^1]); Assert.AreEqual(AttrCount, c.AttributeXp.Count);
            Assert.AreEqual(VitalLast, c.VitalXp[^1]); Assert.AreEqual(VitalCount, c.VitalXp.Count);

            Assert.AreEqual(WarMagicTrainedCost, c.TrainedCost[Skill.WarMagic]);
            Assert.AreEqual(VoidMagicTrainedCost, c.TrainedCost[Skill.VoidMagic]);

            // The lift's price at the shipped defaults (the values Player.XpCostForClassAbilityPoints gave at ca2a8e2d1).
            var t = new XpLaneTunables(200_000_000_000, 1.4, 1.4, 1.4, 1, 1, 275);
            Assert.AreEqual(XpCost0Plus1, TrialLevelLift.XpCost(0, 1, t));
            Assert.AreEqual(XpCost3Plus2, TrialLevelLift.XpCost(3, 2, t));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Chart_top_ranks_equal_the_live_CalcRank_helpers()
        {
            RequireDats();
            var c = XpCharts.Live();

            // The lift writes ranks = chart count - 1 for a topped trait; the live helpers agree on the real charts.
            Assert.AreEqual(Player.CalcSkillRank(SkillAdvancementClass.Specialized, c.SpecializedSkillXp[^1]), c.SpecializedSkillXp.Count - 1);
            Assert.AreEqual(Player.CalcSkillRank(SkillAdvancementClass.Trained, c.TrainedSkillXp[^1]), c.TrainedSkillXp.Count - 1);
            Assert.AreEqual(Player.CalcAttributeRank(c.AttributeXp[^1]), c.AttributeXp.Count - 1);
            Assert.AreEqual(Player.CalcVitalRank(c.VitalXp[^1]), c.VitalXp.Count - 1);

            Assert.AreEqual(CalcSkillRankSpec, c.SpecializedSkillXp.Count - 1);
            Assert.AreEqual(CalcSkillRankTrained, c.TrainedSkillXp.Count - 1);
            Assert.AreEqual(CalcAttributeRank, c.AttributeXp.Count - 1);
            Assert.AreEqual(CalcVitalRank, c.VitalXp.Count - 1);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Lift_375_to_400_on_live_charts_equals_golden()
        {
            RequireDats();
            var c = XpCharts.Live();
            const long progress = 1_000_000;

            var i = new TrialLiftInput(375, (long)Totals375 + progress, 5, 2, 60, 12, 0, Array.Empty<TraitState>(), 400);
            var r = TrialLevelLift.Lift(i, c, new XpLaneTunables(200_000_000_000, 1.4, 1.4, 1.4, 1, 1, 275), new CohortTargets(null, null, Array.Empty<Skill>()), out var refusal);

            Assert.IsNotNull(r, refusal);
            Assert.AreEqual(400, r.Level);
            Assert.AreEqual((long)Totals400 + progress, r.TotalXp);
            Assert.AreEqual((long)(Totals400 - Totals375), r.XpGranted);
            Assert.AreEqual(5 + (long)(Totals400 - Totals375), r.UnspentXp);
            Assert.AreEqual(2 + Credits376To400, r.UnspentCredits);
            Assert.AreEqual(60 + Credits376To400, r.TotalCredits);
            Assert.AreEqual(0, r.CapAdded);
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
