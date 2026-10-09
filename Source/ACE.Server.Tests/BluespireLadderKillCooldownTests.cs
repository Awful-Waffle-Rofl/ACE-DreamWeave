using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bluespire ladder daily kill-task cooldown and repeat payout (WaffleACE round 15): a kill-task
    /// clear of a rung boss is daily (a rolling MinDelta, set in world data by a companion content
    /// branch), and a re-clear after the cooldown window pays the ladder currency again at the same
    /// amount - retail/fork kill-task counters that are not the ladder must be completely unaffected.
    ///
    /// Exercised directly against QuestManager.ApplyKillStamp and BluespireLadderRewards.ShouldPayOnKillStamp
    /// - both internal, both reachable via the ACE.Server -> ACE.Server.Tests InternalsVisibleTo
    /// (ACE.Server.csproj:15) - so none of this needs a live Player, a database, or a real currency
    /// weenie. Quests are stamped on a bare Creature, exactly as QuestManagerTests does.
    /// </summary>
    [TestClass]
    public class BluespireLadderKillCooldownTests
    {
        // world quest fixtures injected into the cache in TestInitialize
        //
        // LadderQuest MUST be a real rung-1 clear name (BluespireLadderGate.ClearedQuestName(1)), not an
        // arbitrary "ladder-shaped" fixture: ApplyKillStamp's OnCooldown branch (WaffleACE round 17) is
        // gated on BluespireLadderRewards.RungForQuestName(questName) != 0, a compiled table built ONLY
        // from the six real BluespireLadderD{1-6}Cleared names, so a fixture with any other name (this
        // used to be "TestBluespireLadderD1Cleared") would silently miss that table and this test would
        // stop proving what it claims to.
        private const string LadderQuest = "BluespireLadderD1Cleared";   // bootstrap-shaped: MaxSolves=-1, MinDelta=72000
        private const string RetailQuest = "TestRetailKillTask";             // retail-shaped: MaxSolves=20, MinDelta=0
        private const string DelayedQuest = "TestDelayedMinDeltaKillTask";   // MaxSolves=-1, MinDelta>0, always bootstrapRow=false
        private const string BlockedRestampQuest = "TestBlockedRestampKillTask"; // MaxSolves=1, MinDelta=0 - maxed on the first stamp

        private const uint LadderMinDelta = 72000;   // the ladder's 20h daily cooldown, in seconds
        private const uint DelayedMinDelta = 1000;

        [TestInitialize]
        public void SeedWorldQuests()
        {
            DatabaseManager.World.AddCachedQuest(new Quest { Name = LadderQuest, MaxSolves = -1, MinDelta = LadderMinDelta });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = RetailQuest, MaxSolves = 20, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = DelayedQuest, MaxSolves = -1, MinDelta = DelayedMinDelta });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = BlockedRestampQuest, MaxSolves = 1, MinDelta = 0 });
        }

        [TestCleanup]
        public void ClearWorldQuests()
        {
            DatabaseManager.World.ClearCachedQuest(LadderQuest);
            DatabaseManager.World.ClearCachedQuest(RetailQuest);
            DatabaseManager.World.ClearCachedQuest(DelayedQuest);
            DatabaseManager.World.ClearCachedQuest(BlockedRestampQuest);
        }

        private static QuestManager NewQuestManager() => TestCreatures.CreateQuestBearer().QuestManager;

        /// <summary>
        /// Seeds quest_mindelta_rate = 1 (the shipped default) directly into PropertyManager's cache, so
        /// GetNextSolveTime/CanSolve never fall through to a live ShardConfig database, and restores it
        /// afterward. PropertyManager's caches are static and shared across every test class in the
        /// process (see CLAUDE.md's ACE.Server.Tests warning), so this must run per-test in a try/finally
        /// rather than once in TestInitialize/TestCleanup - mirrors AccountVaultBarrelTests.Setup's seed
        /// pattern (AccountVaultBarrelTests.cs:49).
        /// </summary>
        private static void WithSeededMinDeltaRate(Action body)
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("quest_mindelta_rate", 1.0),
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

        [TestMethod]
        public void ApplyKillStamp_LadderRow_CooldownsThenRestampsAfterMinDelta()
        {
            WithSeededMinDeltaRate(() =>
            {
                var qm = NewQuestManager();

                // (a) first-ever clear: no row exists yet, so bootstrapRow creates it
                var first = qm.ApplyKillStamp(LadderQuest, bootstrapRow: true);
                Assert.AreEqual(QuestManager.KillStampOutcome.Created, first);
                Assert.AreEqual(1, qm.GetCurrentSolves(LadderQuest));

                var lastTimeAfterFirst = qm.GetQuest(LadderQuest).LastTimeCompleted;

                // a kill inside the daily window must neither re-stamp nor reset the timer
                var second = qm.ApplyKillStamp(LadderQuest, bootstrapRow: true);
                Assert.AreEqual(QuestManager.KillStampOutcome.OnCooldown, second);
                Assert.AreEqual(1, qm.GetCurrentSolves(LadderQuest), "a kill on cooldown must not increment the count");
                Assert.AreEqual(lastTimeAfterFirst, qm.GetQuest(LadderQuest).LastTimeCompleted,
                    "a kill on cooldown must not reset LastTimeCompleted");

                // back-date the clear past the cooldown window (GetQuest returns the live registry entry)
                qm.GetQuest(LadderQuest).LastTimeCompleted -= LadderMinDelta + 1;

                var third = qm.ApplyKillStamp(LadderQuest, bootstrapRow: true);
                Assert.AreEqual(QuestManager.KillStampOutcome.Restamped, third);
                Assert.AreEqual(2, qm.GetCurrentSolves(LadderQuest));
            });
        }

        [TestMethod]
        public void ApplyKillStamp_RetailShapeCounter_AcceptsUpToMaxSolves()
        {
            var qm = NewQuestManager();

            // pre-armed: every real kill task other than the ladder's bootstrap rungs is armed by the
            // NPC that hands it out, so bootstrapRow is false and the row must already exist
            qm.SetQuestCompletions(RetailQuest, 0);

            for (var i = 1; i <= 20; i++)
            {
                var outcome = qm.ApplyKillStamp(RetailQuest, bootstrapRow: false);
                Assert.AreEqual(QuestManager.KillStampOutcome.Restamped, outcome);
                Assert.AreEqual(i, qm.GetCurrentSolves(RetailQuest));
            }

            // the 21st kill must not push the counter past MaxSolves - unchanged, pre-existing behaviour -
            // and must be classified Refused (the row exists but its state did not change), not Restamped
            var capped = qm.ApplyKillStamp(RetailQuest, bootstrapRow: false);
            Assert.AreEqual(QuestManager.KillStampOutcome.Refused, capped, "a maxed existing row is a blocked restamp, not a fresh one");
            Assert.AreEqual(20, qm.GetCurrentSolves(RetailQuest), "MaxSolves must still cap a non-ladder kill task");
        }

        /// <summary>
        /// Regression test for the accepted code-review finding on this branch: ApplyKillStamp must
        /// classify an existing row by whether its COMPLETION STATE changed, not by whether the row still
        /// exists. Update() has two silent no-op paths on an existing row - the mule guard
        /// (QuestManager.cs:179, MuleBlocked(AdvanceQuest)) and IsMaxSolves (:216-220) - and both leave the
        /// row in place untouched. A bare test Creature cannot be muled (MuleBlocked only applies to a
        /// Player), so this drives the IDENTICAL silent-return shape through IsMaxSolves instead - a row
        /// already at its cap, restamped again. Before the fix this returned Restamped (existence-only
        /// classification saw a row before and a row after, and called that a restamp); after the fix it
        /// must return Refused, and ShouldPayOnKillStamp must therefore route no payout for it.
        /// </summary>
        [TestMethod]
        public void ApplyKillStamp_BlockedExistingRowUpdate_ReturnsRefused_NotRestamped()
        {
            var qm = NewQuestManager();

            // pre-armed at the cap: the row exists and is already maxed, so the very next Stamp hits
            // Update's IsMaxSolves guard and silently no-ops
            qm.SetQuestCompletions(BlockedRestampQuest, 1);

            var lastTimeBefore = qm.GetQuest(BlockedRestampQuest).LastTimeCompleted;

            var outcome = qm.ApplyKillStamp(BlockedRestampQuest, bootstrapRow: false);

            Assert.AreEqual(QuestManager.KillStampOutcome.Refused, outcome,
                "a blocked restamp (state unchanged) must be Refused, never Restamped");
            Assert.AreEqual(1, qm.GetCurrentSolves(BlockedRestampQuest), "a blocked restamp must not increment the count");
            Assert.AreEqual(lastTimeBefore, qm.GetQuest(BlockedRestampQuest).LastTimeCompleted,
                "a blocked restamp must not touch LastTimeCompleted");

            Assert.IsFalse(BluespireLadderRewards.ShouldPayOnKillStamp(outcome),
                "a blocked restamp must never route the ladder's repeat payout");
        }

        [TestMethod]
        public void ApplyKillStamp_NonBootstrapMinDelta_NeverGatesOnCooldown()
        {
            var qm = NewQuestManager();

            // pre-armed, same reasoning as the retail-shape test above
            qm.SetQuestCompletions(DelayedQuest, 0);

            // every one of these kills lands well inside DelayedMinDelta, but bootstrapRow is false, so
            // ApplyKillStamp must never consult CanSolve/GetNextSolveTime for it - the cooldown gate is
            // wired to bootstrapRow, not to whether the world quest row happens to carry a MinDelta
            for (var i = 1; i <= 5; i++)
            {
                var outcome = qm.ApplyKillStamp(DelayedQuest, bootstrapRow: false);
                Assert.AreEqual(QuestManager.KillStampOutcome.Restamped, outcome);
                Assert.AreEqual(i, qm.GetCurrentSolves(DelayedQuest));
            }
        }

        [TestMethod]
        public void ShouldPayOnKillStamp_TruthTable()
        {
            Assert.IsFalse(BluespireLadderRewards.ShouldPayOnKillStamp(QuestManager.KillStampOutcome.NotHeld));
            Assert.IsFalse(BluespireLadderRewards.ShouldPayOnKillStamp(QuestManager.KillStampOutcome.OnCooldown));
            Assert.IsFalse(BluespireLadderRewards.ShouldPayOnKillStamp(QuestManager.KillStampOutcome.Created),
                "Created already paid inside Update's created-row branch; paying again here would double-pay the first clear");
            Assert.IsTrue(BluespireLadderRewards.ShouldPayOnKillStamp(QuestManager.KillStampOutcome.Restamped));
            Assert.IsFalse(BluespireLadderRewards.ShouldPayOnKillStamp(QuestManager.KillStampOutcome.Refused));
        }

        /// <summary>
        /// A source-text pin, not a behavioural test: HandleKillTask must decide everything from
        /// ApplyKillStamp's returned outcome, and the repeat-payout check must physically sit AFTER that
        /// call in source order, never before it (which would mean deciding to pay before the cooldown/
        /// creation outcome for this kill is even known). Modelled on AccountCapacityUpgradeDaoShapeTests'
        /// source-text pins.
        /// </summary>
        [TestMethod]
        public void HandleKillTask_CallsApplyKillStamp_AndGatesRepeatPayoutBehindOutcome()
        {
            var src = QuestManagerSource();

            var handleStart = src.IndexOf("public void HandleKillTask(", StringComparison.Ordinal);
            Assert.IsTrue(handleStart >= 0, "HandleKillTask is missing");

            var nextMemberStart = src.IndexOf("public void OnDeath(WorldObject killer)", handleStart, StringComparison.Ordinal);
            Assert.IsTrue(nextMemberStart > handleStart, "OnDeath no longer follows HandleKillTask");

            var body = src.Substring(handleStart, nextMemberStart - handleStart);

            var applyIdx = body.IndexOf("ApplyKillStamp(questName, bootstrapRow)", StringComparison.Ordinal);
            Assert.IsTrue(applyIdx >= 0, "HandleKillTask must call ApplyKillStamp");

            var payoutIdx = body.IndexOf("ShouldPayOnKillStamp(outcome)", StringComparison.Ordinal);
            Assert.IsTrue(payoutIdx >= 0, "HandleKillTask must gate the repeat payout on ShouldPayOnKillStamp");

            Assert.IsTrue(payoutIdx > applyIdx,
                "the repeat payout must be decided from ApplyKillStamp's outcome, not before it runs");
        }

        /// <summary>
        /// A second source-text pin, for the same accepted finding as
        /// ApplyKillStamp_BlockedExistingRowUpdate_ReturnsRefused_NotRestamped: ApplyKillStamp's classification
        /// must be a STATE comparison (NumTimesCompleted / LastTimeCompleted captured before Stamp, compared
        /// after it) and not merely a before/after null check on the row. Pins the specific comparison so a
        /// future edit that quietly reverts to existence-only classification fails a source check even if it
        /// does not happen to fail the behavioural regression test above.
        /// </summary>
        [TestMethod]
        public void ApplyKillStamp_ClassifiesExistingRowByCompletionState()
        {
            var body = ApplyKillStampBody();

            var numBeforeIdx = body.IndexOf("var numBefore = rowBefore?.NumTimesCompleted;", StringComparison.Ordinal);
            Assert.IsTrue(numBeforeIdx >= 0, "ApplyKillStamp must capture NumTimesCompleted before Stamp");

            var lastBeforeIdx = body.IndexOf("var lastBefore = rowBefore?.LastTimeCompleted;", StringComparison.Ordinal);
            Assert.IsTrue(lastBeforeIdx >= 0, "ApplyKillStamp must capture LastTimeCompleted before Stamp");

            var stampIdx = body.IndexOf("Stamp(questName);", StringComparison.Ordinal);
            Assert.IsTrue(stampIdx >= 0, "ApplyKillStamp must call Stamp");

            var compareIdx = body.IndexOf(
                "if (rowAfter.NumTimesCompleted == numBefore && rowAfter.LastTimeCompleted == lastBefore)",
                StringComparison.Ordinal);
            Assert.IsTrue(compareIdx >= 0, "ApplyKillStamp must compare completion state after Stamp, not just row existence");

            Assert.IsTrue(numBeforeIdx < stampIdx && lastBeforeIdx < stampIdx,
                "state must be captured BEFORE Stamp runs");
            Assert.IsTrue(compareIdx > stampIdx,
                "the state comparison must run AFTER Stamp, against the state captured before it");
        }

        private static string ApplyKillStampBody()
        {
            var src = QuestManagerSource();

            var start = src.IndexOf("internal KillStampOutcome ApplyKillStamp(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "ApplyKillStamp is missing");

            var end = src.IndexOf("public void HandleKillTask(", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "HandleKillTask no longer follows ApplyKillStamp");

            return src.Substring(start, end - start);
        }

        private static string QuestManagerSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "Managers", "QuestManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find QuestManager.cs by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "Managers", "QuestManager.cs")).Replace("\r\n", "\n");
        }
    }
}
