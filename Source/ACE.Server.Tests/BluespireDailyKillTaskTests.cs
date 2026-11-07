using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bluespire daily kill tasks (OpenSpec change bluespire-daily-kill-tasks, Drift Marae Lassel).
    ///
    /// The feature is content-driven - ten Hea NPCs whose emotes assign a random task, credit kills through the
    /// stock KillQuest path and pay on turn-in - so the engine's only new rule is cooldown isolation: the
    /// BluespireDailyKill* quest family ignores quest_mindelta_rate, so a category's Wait row is an exact
    /// 72,000 seconds from the claim stamp. These tests pin that rule, prove nothing else moved (ColoArena and
    /// ordinary quests still scale), and walk the content's quest-row lifecycle - assign, credit, complete,
    /// claim, cooldown, re-eligible - on the real QuestManager the emotes drive, so a later engine change that
    /// breaks the content contract fails here rather than on Stage.
    ///
    /// Like BluespireLadderKillCooldownTests, everything runs against a bare Creature's QuestManager with world
    /// quest rows injected into the cache; no live Player, database, or content is needed.
    /// </summary>
    [TestClass]
    public class BluespireDailyKillTaskTests
    {
        private const uint Cooldown = BluespireDailyKillTasks.CooldownSeconds;

        // the content contract's three row shapes, for two independent categories
        private const string OlthoiWait = "BluespireDailyKillOlthoiWait";
        private const string OlthoiTask = "BluespireDailyKillOlthoiTask";
        private const string OlthoiSoldier = "BluespireDailyKillOlthoiOlthoiSoldier";
        private const string OlthoiWarrior = "BluespireDailyKillOlthoiOlthoiWarrior";
        private const string GolemsWait = "BluespireDailyKillGolemsWait";

        // test-only kill count: the real per-task counts are an open gameplay decision, not set here
        private const int TestKillCount = 3;

        // unrelated quests that must keep their existing behaviour
        private const string UnrelatedDaily = "TestUnrelatedDailyQuest";
        private const string ColoQuest = "ColoArenaTestTimer";
        private const string LadderQuestLike = "BluespireLadderTestLikeQuest";

        [TestInitialize]
        public void SeedWorldQuests()
        {
            DatabaseManager.World.AddCachedQuest(new Quest { Name = OlthoiWait, MaxSolves = -1, MinDelta = Cooldown });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = GolemsWait, MaxSolves = -1, MinDelta = Cooldown });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = OlthoiTask, MaxSolves = -1, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = OlthoiSoldier, MaxSolves = TestKillCount, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = OlthoiWarrior, MaxSolves = TestKillCount, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = UnrelatedDaily, MaxSolves = -1, MinDelta = Cooldown });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = ColoQuest, MaxSolves = -1, MinDelta = Cooldown });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = LadderQuestLike, MaxSolves = -1, MinDelta = Cooldown });
        }

        [TestCleanup]
        public void ClearWorldQuests()
        {
            foreach (var name in new[] { OlthoiWait, GolemsWait, OlthoiTask, OlthoiSoldier, OlthoiWarrior, UnrelatedDaily, ColoQuest, LadderQuestLike })
                DatabaseManager.World.ClearCachedQuest(name);
        }

        private static QuestManager NewQuestManager() => TestCreatures.CreateQuestBearer().QuestManager;

        /// <summary>
        /// Seeds quest_mindelta_rate into PropertyManager's static cache for the body and always restores the
        /// shipped default (1.0) - per test, in a try/finally, as BluespireLadderKillCooldownTests does, because
        /// that cache is shared by every test class in the process.
        /// </summary>
        private static void WithMinDeltaRate(double rate, Action body)
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("quest_mindelta_rate", rate),
                "quest_mindelta_rate is missing from DefaultDoubleProperties");

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyDouble("quest_mindelta_rate", 1.0);
            }
        }

        /// <summary>Back-dates a quest row so its last completion was <paramref name="secondsAgo"/> seconds ago.</summary>
        private static void SetCompletedSecondsAgo(QuestManager qm, string quest, uint secondsAgo)
        {
            qm.GetQuest(quest).LastTimeCompleted = (uint)Time.GetUnixTime() - secondsAgo;
        }

        // =====================================================================================================
        // Family membership
        // =====================================================================================================

        [TestMethod]
        public void IsQuestFamily_MatchesOnlyThePrefix()
        {
            Assert.IsTrue(BluespireDailyKillTasks.IsQuestFamily(OlthoiWait));
            Assert.IsTrue(BluespireDailyKillTasks.IsQuestFamily(OlthoiTask));
            Assert.IsTrue(BluespireDailyKillTasks.IsQuestFamily(OlthoiSoldier));
            Assert.IsTrue(BluespireDailyKillTasks.IsQuestFamily("bluespiredailykillaunwait"), "hand-authored case variants stay in the family");
            Assert.IsTrue(BluespireDailyKillTasks.IsQuestFamily(OlthoiSoldier + "@Done"), "an emote @comment is stripped first");

            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily(null));
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily(""));
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily(UnrelatedDaily));
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily(ColoQuest));
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily(LadderQuestLike), "the Bluespire ladder is a different family");
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily("BluespireLadderD1Cleared"));
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily("XBluespireDailyKillOlthoiWait"), "prefix, not substring");
            Assert.IsFalse(BluespireDailyKillTasks.IsQuestFamily("Note@BluespireDailyKillOlthoiWait"), "only the name before @ counts");
        }

        [TestMethod]
        public void CanScaleQuestMinDelta_ExemptsOnlyTheFamily_AndKeepsColoArena()
        {
            Assert.IsFalse(QuestManager.CanScaleQuestMinDelta(new Quest { Name = OlthoiWait }));
            Assert.IsFalse(QuestManager.CanScaleQuestMinDelta(new Quest { Name = GolemsWait }));

            // ColoArena exemption is unchanged
            Assert.IsFalse(QuestManager.CanScaleQuestMinDelta(new Quest { Name = ColoQuest }));

            // everything else still scales
            Assert.IsTrue(QuestManager.CanScaleQuestMinDelta(new Quest { Name = UnrelatedDaily }));
            Assert.IsTrue(QuestManager.CanScaleQuestMinDelta(new Quest { Name = LadderQuestLike }));
            Assert.IsTrue(QuestManager.CanScaleQuestMinDelta(new Quest { Name = "BluespireLadderD1Cleared" }));
        }

        // =====================================================================================================
        // Exact 72,000 s cooldown under a modified multiplier, and isolation of everything else
        // =====================================================================================================

        [DataTestMethod]
        [DataRow(1.0)]
        [DataRow(0.5)]
        [DataRow(2.0)]
        [DataRow(0.0)]
        public void WaitRow_IsExactly72000Seconds_WhateverTheRate(double rate)
        {
            WithMinDeltaRate(rate, () =>
            {
                var qm = NewQuestManager();
                qm.Stamp(OlthoiWait);

                Assert.IsFalse(qm.CanSolve(OlthoiWait), "a just-claimed category is on cooldown");

                var remaining = qm.GetNextSolveTime(OlthoiWait).TotalSeconds;
                Assert.IsTrue(remaining > Cooldown - 5 && remaining <= Cooldown,
                    $"rate {rate}: remaining should be ~72000 s, was {remaining}");

                // 60 s short of the window: still on cooldown
                SetCompletedSecondsAgo(qm, OlthoiWait, Cooldown - 60);
                Assert.IsFalse(qm.CanSolve(OlthoiWait), $"rate {rate}: still on cooldown 60 s before 72000");

                // exactly at the window: eligible again
                SetCompletedSecondsAgo(qm, OlthoiWait, Cooldown);
                Assert.IsTrue(qm.CanSolve(OlthoiWait), $"rate {rate}: eligible at exactly 72000 s");
            });
        }

        [TestMethod]
        public void UnrelatedQuests_KeepTheirScaling()
        {
            // halved rate: an ordinary 72000 s quest is solvable after 36000 s, Bluespire is not
            WithMinDeltaRate(0.5, () =>
            {
                var qm = NewQuestManager();
                qm.Stamp(UnrelatedDaily);
                qm.Stamp(LadderQuestLike);
                qm.Stamp(OlthoiWait);

                SetCompletedSecondsAgo(qm, UnrelatedDaily, Cooldown / 2);
                SetCompletedSecondsAgo(qm, LadderQuestLike, Cooldown / 2);
                SetCompletedSecondsAgo(qm, OlthoiWait, Cooldown / 2);

                Assert.IsTrue(qm.CanSolve(UnrelatedDaily), "unrelated quest still honours a 0.5 rate");
                Assert.IsTrue(qm.CanSolve(LadderQuestLike), "a non-family Bluespire quest still honours a 0.5 rate");
                Assert.IsFalse(qm.CanSolve(OlthoiWait), "the daily kill-task cooldown ignores the 0.5 rate");
            });

            // doubled rate: an ordinary quest is still blocked at 72000 s, Bluespire is free, ColoArena exact
            WithMinDeltaRate(2.0, () =>
            {
                var qm = NewQuestManager();
                qm.Stamp(UnrelatedDaily);
                qm.Stamp(ColoQuest);
                qm.Stamp(OlthoiWait);

                SetCompletedSecondsAgo(qm, UnrelatedDaily, Cooldown);
                SetCompletedSecondsAgo(qm, ColoQuest, Cooldown);
                SetCompletedSecondsAgo(qm, OlthoiWait, Cooldown);

                Assert.IsFalse(qm.CanSolve(UnrelatedDaily), "unrelated quest still honours a 2.0 rate");
                Assert.IsTrue(qm.CanSolve(ColoQuest), "ColoArena keeps its existing unscaled behaviour");
                Assert.IsTrue(qm.CanSolve(OlthoiWait), "the daily kill-task cooldown ignores the 2.0 rate");
            });
        }

        [TestMethod]
        public void CategoryCooldowns_AreIndependent()
        {
            WithMinDeltaRate(1.0, () =>
            {
                var qm = NewQuestManager();

                qm.Stamp(OlthoiWait);

                Assert.IsFalse(qm.CanSolve(OlthoiWait));
                Assert.IsFalse(qm.HasQuest(GolemsWait), "claiming Olthoi must not create a Golems timer");
                Assert.IsTrue(qm.CanSolve(GolemsWait), "Golems stays claimable while Olthoi cools down");
                Assert.IsTrue(qm.CanSolve(UnrelatedDaily), "unrelated quests are untouched");
            });
        }

        // =====================================================================================================
        // Content lifecycle on the real QuestManager primitives the NPC emotes drive
        // =====================================================================================================

        /// <summary>
        /// assign (SetQuestCompletions Task=index, counter=0) -> kills (ApplyKillStamp, the stock KillQuest path)
        /// -> completion -> claim (Stamp Wait, Erase counter and Task) -> cooldown -> eligible again. Asserts the
        /// spec's timing rules: acceptance and the final kill start no cooldown; only the claim stamp does.
        /// </summary>
        [TestMethod]
        public void Lifecycle_CooldownStartsOnlyAtClaim()
        {
            WithMinDeltaRate(1.0, () =>
            {
                var qm = NewQuestManager();

                // before assignment the stock kill path gives no credit (the HasQuest gate)
                Assert.AreEqual(QuestManager.KillStampOutcome.NotHeld, qm.ApplyKillStamp(OlthoiSoldier, bootstrapRow: false));
                Assert.IsFalse(qm.HasQuest(OlthoiSoldier));

                // assignment: task index 1 = Olthoi Soldier, counter armed at 0
                qm.SetQuestCompletions(OlthoiTask, 1);
                qm.SetQuestCompletions(OlthoiSoldier, 0);

                Assert.IsTrue(qm.HasQuestSolves(OlthoiTask, 1, 1), "the Task row records which target was assigned");
                Assert.IsTrue(qm.HasQuest(OlthoiSoldier));
                Assert.AreEqual(0, qm.GetCurrentSolves(OlthoiSoldier), "the counter starts at 0, not 1");
                Assert.IsFalse(qm.HasQuest(OlthoiWait), "acceptance must not start the cooldown");

                // kills of an unassigned target in the same category credit nothing
                Assert.AreEqual(QuestManager.KillStampOutcome.NotHeld, qm.ApplyKillStamp(OlthoiWarrior, bootstrapRow: false));

                for (var i = 1; i <= TestKillCount; i++)
                {
                    Assert.AreEqual(QuestManager.KillStampOutcome.Restamped, qm.ApplyKillStamp(OlthoiSoldier, bootstrapRow: false));
                    Assert.AreEqual(i, qm.GetCurrentSolves(OlthoiSoldier));
                }

                Assert.IsTrue(qm.IsMaxSolves(OlthoiSoldier), "kill count reached");
                Assert.IsTrue(qm.HasQuestSolves(OlthoiSoldier, TestKillCount, null), "the turn-in test the NPC uses");
                Assert.IsFalse(qm.HasQuest(OlthoiWait), "the final kill must not start the cooldown");

                // kills beyond the count are refused, not over-counted
                Assert.AreEqual(QuestManager.KillStampOutcome.Refused, qm.ApplyKillStamp(OlthoiSoldier, bootstrapRow: false));
                Assert.AreEqual(TestKillCount, qm.GetCurrentSolves(OlthoiSoldier));

                // claim: the reward emotes run in this same set; the quest-side effects are these three
                qm.Stamp(OlthoiWait);
                qm.Erase(OlthoiSoldier);
                qm.Erase(OlthoiTask);

                Assert.IsFalse(qm.CanSolve(OlthoiWait), "the claim starts the 72000 s cooldown");
                Assert.IsFalse(qm.HasQuestSolves(OlthoiSoldier, TestKillCount, null), "a repeat turn-in finds nothing complete");
                Assert.IsFalse(qm.HasQuestSolves(OlthoiTask, 1, 1), "no active assignment remains after the claim");
                Assert.AreEqual(QuestManager.KillStampOutcome.NotHeld, qm.ApplyKillStamp(OlthoiSoldier, bootstrapRow: false),
                    "kills after the claim credit nothing until a new task is assigned");

                // cooldown elapses -> eligible for a new assignment
                SetCompletedSecondsAgo(qm, OlthoiWait, Cooldown);
                Assert.IsTrue(qm.CanSolve(OlthoiWait));

                // a second claim later restamps the same Wait row, resetting its 72000 s window
                var completionsBefore = qm.GetCurrentSolves(OlthoiWait);
                qm.Stamp(OlthoiWait);
                Assert.AreEqual(completionsBefore + 1, qm.GetCurrentSolves(OlthoiWait));
                Assert.IsFalse(qm.CanSolve(OlthoiWait));
            });
        }

        /// <summary>
        /// A completed-but-unclaimed task keeps its progress and starts no timer however long it sits - the
        /// "completed but not turned in" scenario.
        /// </summary>
        [TestMethod]
        public void CompletedButUnclaimed_KeepsProgress_AndNoCooldown()
        {
            WithMinDeltaRate(1.0, () =>
            {
                var qm = NewQuestManager();
                qm.SetQuestCompletions(OlthoiTask, 2);
                qm.SetQuestCompletions(OlthoiWarrior, 0);

                for (var i = 0; i < TestKillCount; i++)
                    qm.ApplyKillStamp(OlthoiWarrior, bootstrapRow: false);

                // age the rows well past a day
                SetCompletedSecondsAgo(qm, OlthoiWarrior, Cooldown * 3);
                SetCompletedSecondsAgo(qm, OlthoiTask, Cooldown * 3);

                Assert.IsTrue(qm.HasQuestSolves(OlthoiWarrior, TestKillCount, null), "still claimable");
                Assert.IsTrue(qm.HasQuestSolves(OlthoiTask, 2, 2), "still assigned to the Warrior");
                Assert.IsFalse(qm.HasQuest(OlthoiWait), "no cooldown without a claim");
            });
        }

        /// <summary>
        /// The NPC's on-cooldown test is EmoteType.InqQuest, which succeeds when HasQuest &amp;&amp; !CanSolve.
        /// Pins that predicate's truth table for the Wait row: never claimed, just claimed, cooled down.
        /// </summary>
        [TestMethod]
        public void InqQuestPredicate_OnWaitRow()
        {
            WithMinDeltaRate(1.0, () =>
            {
                var qm = NewQuestManager();

                bool OnCooldown() => qm.HasQuest(OlthoiWait) && !qm.CanSolve(OlthoiWait);

                Assert.IsFalse(OnCooldown(), "never claimed: offer a task");

                qm.Stamp(OlthoiWait);
                Assert.IsTrue(OnCooldown(), "just claimed: refuse with the remaining time");

                SetCompletedSecondsAgo(qm, OlthoiWait, Cooldown);
                Assert.IsFalse(OnCooldown(), "cooled down: offer a task");
            });
        }
    }
}
