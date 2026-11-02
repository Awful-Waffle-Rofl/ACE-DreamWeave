using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpDailyCatalog, the single source of truth the quest-content PR reads byte for byte: the names, the targets and each task's delta
    /// for crafted facts. Pure; touches no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PvpDailyCatalogTests
    {
        private const uint Opp = 0x00001234;

        private static PvpMatchFacts Facts(PvpDailyMode mode, PvpBloodResult result = PvpBloodResult.Loss, int team = 0, long kills = 0, long hill = 0,
            long crystal = 0, long nearCrystal = 0, long onHill = 0, uint? opponent = null)
            => new PvpMatchFacts(Guid.Empty, 0x50000001, mode, team, result, kills, hill, crystal, nearCrystal, onHill, opponent, 0);

        /// <summary>
        /// The owner's table, task by task: target, daily or weekly, and the delta for one crafted facts record per relevant case. A mode the
        /// task does not read always gives 0. Changing any delta function, target or mode test fails a row.
        /// </summary>
        [TestMethod]
        public void Table_EveryTaskDeltaAndTarget()
        {
            var koth = Facts(PvpDailyMode.Koth, PvpBloodResult.Win, hill: 42, onHill: 3, nearCrystal: 9, crystal: 77, kills: 6);
            var kothLoss = Facts(PvpDailyMode.Koth, PvpBloodResult.Loss);
            var attackerWin = Facts(PvpDailyMode.AttackDefend, PvpBloodResult.Win, team: 0, crystal: 2500, nearCrystal: 4, onHill: 2, hill: 5);
            var defenderWin = Facts(PvpDailyMode.AttackDefend, PvpBloodResult.Win, team: 1, crystal: 0, nearCrystal: 4);
            var defenderLoss = Facts(PvpDailyMode.AttackDefend, PvpBloodResult.Loss, team: 1, nearCrystal: 2);
            var duelWin = Facts(PvpDailyMode.OneVOne, PvpBloodResult.Win, kills: 1, opponent: Opp);
            var duelLoss = Facts(PvpDailyMode.OneVOne, PvpBloodResult.Loss, opponent: Opp);
            var duelDraw = Facts(PvpDailyMode.OneVOne, PvpBloodResult.Draw, opponent: Opp);
            var pitWin3 = Facts(PvpDailyMode.Tugak, PvpBloodResult.Win, kills: 3);
            var pitLoss2 = Facts(PvpDailyMode.Tugak, PvpBloodResult.Loss, kills: 2);
            var twoVTwoWin = Facts(PvpDailyMode.TwoVTwo, PvpBloodResult.Win, kills: 2);

            var rows = new (PvpDailyTask Task, int Target, bool Weekly, (PvpMatchFacts Facts, long Delta)[] Cases)[]
            {
                (PvpDailyTask.WarmongersDue, 5, false, new[] { (koth, 1L), (kothLoss, 1L), (attackerWin, 1L), (defenderLoss, 1L), (duelWin, 0L), (pitWin3, 0L) }),
                (PvpDailyTask.WeeklyBattlegrounds, 20, true, new[] { (koth, 1L), (defenderLoss, 1L), (duelWin, 0L), (twoVTwoWin, 0L) }),
                (PvpDailyTask.BloodInTheSand, 3, false, new[] { (duelWin, 1L), (duelLoss, 1L), (duelDraw, 1L), (twoVTwoWin, 0L), (koth, 0L) }),
                (PvpDailyTask.FirstBlood, 1, false, new[] { (duelWin, 1L), (duelLoss, 0L), (duelDraw, 0L), (twoVTwoWin, 0L) }),
                (PvpDailyTask.ProvingGround, 3, false, new[] { (duelWin, 1L), (duelLoss, 0L), (Facts(PvpDailyMode.OneVOne, PvpBloodResult.Win), 0L), (twoVTwoWin, 0L) }),
                (PvpDailyTask.GladiatorsStreak, 2, false, new[] { (duelWin, 1L), (duelLoss, 0L), (duelDraw, 0L), (pitWin3, 0L) }),
                (PvpDailyTask.IntoThePit, 3, false, new[] { (pitWin3, 1L), (pitLoss2, 1L), (duelWin, 0L) }),
                (PvpDailyTask.PitVictor, 1, false, new[] { (pitWin3, 1L), (pitLoss2, 0L), (duelWin, 0L) }),
                (PvpDailyTask.Carnage, 5, false, new[] { (pitWin3, 3L), (pitLoss2, 2L), (koth, 0L), (twoVTwoWin, 0L) }),
                (PvpDailyTask.Bloodbath, 1, false, new[] { (pitWin3, 1L), (pitLoss2, 0L), (Facts(PvpDailyMode.Tugak, kills: 9), 1L), (koth, 0L) }),
                (PvpDailyTask.HoldTheCrown, 2, false, new[] { (koth, 1L), (kothLoss, 1L), (attackerWin, 0L) }),
                (PvpDailyTask.KingsGround, 180, false, new[] { (koth, 42L), (kothLoss, 0L), (attackerWin, 0L) }),
                (PvpDailyTask.Usurper, 5, false, new[] { (koth, 3L), (attackerWin, 0L), (kothLoss, 0L) }),
                (PvpDailyTask.LastOneStanding, 1, false, new[] { (koth, 1L), (kothLoss, 0L), (attackerWin, 0L) }),
                (PvpDailyTask.SiegeDuty, 2, false, new[] { (attackerWin, 1L), (defenderLoss, 1L), (koth, 0L) }),
                (PvpDailyTask.CrystalBreaker, 5000, false, new[] { (attackerWin, 2500L), (defenderWin, 0L), (koth, 0L) }),
                (PvpDailyTask.Bulwark, 5, false, new[] { (defenderWin, 4L), (defenderLoss, 2L), (attackerWin, 0L), (koth, 0L) }),
                (PvpDailyTask.Shatterpoint, 1, false, new[] { (attackerWin, 1L), (defenderWin, 0L), (koth, 0L) }),
                (PvpDailyTask.Holdfast, 1, false, new[] { (defenderWin, 1L), (defenderLoss, 0L), (attackerWin, 0L), (koth, 0L) }),
            };

            CollectionAssert.AreEquivalent(Enum.GetValues(typeof(PvpDailyTask)).Cast<PvpDailyTask>().ToArray(), rows.Select(r => r.Task).ToArray(), "every task has a row");
            Assert.AreEqual(19, PvpDailyCatalog.Tasks.Count);

            foreach (var row in rows)
            {
                var def = PvpDailyCatalog.Get(row.Task);

                Assert.AreEqual(row.Target, def.Target, $"{row.Task} target");
                Assert.AreEqual(row.Weekly, def.Weekly, $"{row.Task} weekly");

                for (var i = 0; i < row.Cases.Length; i++)
                    Assert.AreEqual(row.Cases[i].Delta, def.Delta(row.Cases[i].Facts), $"{row.Task} case {i} ({row.Cases[i].Facts.Mode} {row.Cases[i].Facts.Result} team {row.Cases[i].Facts.TeamIndex})");
            }
        }

        /// <summary>
        /// The names are the contract with the content PR: PvpDaily&lt;Task&gt;Count and PvpDaily&lt;Task&gt;Done, exactly, unique across the
        /// catalog (counters and done flags together), each fits a quest_Name column, and the stateful tasks are the only non-additive ones.
        /// </summary>
        [TestMethod]
        public void Names_AreUnique_AndFollowThePattern()
        {
            var counter = new Regex("^PvpDaily([A-Z][A-Za-z]+)Count$");
            var done = new Regex("^PvpDaily([A-Z][A-Za-z]+)Done$");
            var all = new List<string>();

            foreach (var def in PvpDailyCatalog.Tasks)
            {
                var c = counter.Match(def.CounterName);
                var d = done.Match(def.DoneName);

                Assert.IsTrue(c.Success, def.CounterName);
                Assert.IsTrue(d.Success, def.DoneName);
                Assert.AreEqual(def.Task.ToString(), c.Groups[1].Value, "the counter carries the task name");
                Assert.AreEqual(def.Task.ToString(), d.Groups[1].Value, "the done flag carries the task name");
                Assert.IsTrue(def.CounterName.Length <= 64 && def.DoneName.Length <= 64);

                all.Add(def.CounterName);
                all.Add(def.DoneName);
            }

            Assert.AreEqual(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique, ignoring case as the quest registry does");

            // Spot-check literal bytes: what the content PR will type.
            Assert.AreEqual("PvpDailyWarmongersDueCount", PvpDailyCatalog.Get(PvpDailyTask.WarmongersDue).CounterName);
            Assert.AreEqual("PvpDailyWeeklyBattlegroundsCount", PvpDailyCatalog.Get(PvpDailyTask.WeeklyBattlegrounds).CounterName);
            Assert.AreEqual("PvpDailyHoldfastDone", PvpDailyCatalog.Get(PvpDailyTask.Holdfast).DoneName);

            CollectionAssert.AreEquivalent(new[] { PvpDailyTask.ProvingGround, PvpDailyTask.GladiatorsStreak },
                PvpDailyCatalog.Tasks.Where(t => t.Kind != PvpDailyProgressKind.Additive).Select(t => t.Task).ToArray());
        }

        /// <summary>The player-facing table: every task's display name, giver and unit, exactly as the owner gave them.</summary>
        [TestMethod]
        public void DisplayNames_Givers_AndUnits()
        {
            const string V = "Dark Master Vidorian", F = "Black Ferah", L = "Ler Rhan";

            var expected = new (PvpDailyTask Task, string Display, string Giver, string Unit)[]
            {
                (PvpDailyTask.WarmongersDue, "Warmonger's Due", V, ""),
                (PvpDailyTask.WeeklyBattlegrounds, "The Long War", V, ""),
                (PvpDailyTask.SiegeDuty, "Siege Duty", V, ""),
                (PvpDailyTask.CrystalBreaker, "Crystal Breaker", V, " damage"),
                (PvpDailyTask.Bulwark, "Bulwark", V, ""),
                (PvpDailyTask.Shatterpoint, "Shatterpoint", V, ""),
                (PvpDailyTask.Holdfast, "Holdfast", V, ""),
                (PvpDailyTask.BloodInTheSand, "Blood in the Sand", F, ""),
                (PvpDailyTask.FirstBlood, "First Blood", F, ""),
                (PvpDailyTask.ProvingGround, "Proving Ground", F, ""),
                (PvpDailyTask.GladiatorsStreak, "Gladiator's Streak", F, ""),
                (PvpDailyTask.IntoThePit, "Into the Pit", F, ""),
                (PvpDailyTask.PitVictor, "Pit Victor", F, ""),
                (PvpDailyTask.Carnage, "Carnage", F, ""),
                (PvpDailyTask.Bloodbath, "Bloodbath", F, ""),
                (PvpDailyTask.HoldTheCrown, "Hold the Crown", L, ""),
                (PvpDailyTask.KingsGround, "King's Ground", L, " seconds"),
                (PvpDailyTask.Usurper, "Usurper", L, ""),
                (PvpDailyTask.LastOneStanding, "Last One Standing", L, ""),
            };

            Assert.AreEqual(PvpDailyCatalog.Tasks.Count, expected.Length, "every task is listed");

            foreach (var e in expected)
            {
                var def = PvpDailyCatalog.Get(e.Task);
                Assert.AreEqual(e.Display, def.DisplayName, $"{e.Task} display name");
                Assert.AreEqual(e.Giver, def.GiverName, $"{e.Task} giver");
                Assert.AreEqual(e.Unit, def.Unit, $"{e.Task} unit");
            }
        }

        /// <summary>Every mode key maps to its daily mode; anything else is Other.</summary>
        [TestMethod]
        public void ModeOf_MapsEveryModeKey()
        {
            Assert.AreEqual(PvpDailyMode.OneVOne, PvpMatchFacts.ModeOf("1v1"));
            Assert.AreEqual(PvpDailyMode.TwoVTwo, PvpMatchFacts.ModeOf("2v2"));
            Assert.AreEqual(PvpDailyMode.Tugak, PvpMatchFacts.ModeOf("ffa"));
            Assert.AreEqual(PvpDailyMode.Koth, PvpMatchFacts.ModeOf("bg_koth"));
            Assert.AreEqual(PvpDailyMode.AttackDefend, PvpMatchFacts.ModeOf("bg_ad"));
            Assert.AreEqual(PvpDailyMode.Other, PvpMatchFacts.ModeOf("bg"));
            Assert.AreEqual(PvpDailyMode.Other, PvpMatchFacts.ModeOf(null));
        }
    }
}
