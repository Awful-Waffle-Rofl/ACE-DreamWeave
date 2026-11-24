using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bluespire ladder repeat payout, owned by QuestManager.Update for EVERY clearing path.
    ///
    /// The bug this pins (live on prod, 2026-11-21): each rung BluespireLadderD{1-6}Cleared is meant to pay
    /// again on every re-clear after its 14-day MinDelta (PR #1371). The repeat payout used to live only in
    /// QuestManager.HandleKillTask, so D3-D6 (cleared by a boss KillQuest) paid, while D1 and D2 (cleared by a
    /// turn-in NPC's emote StampQuest, which reaches Update's existing-row branch directly) restamped their
    /// rows and paid nothing. The fix moves the repeat payout into Update's existing-row branch, which both
    /// paths reach, and removes the HandleKillTask call so a kill re-clear is not paid twice.
    ///
    /// ACE.Server.Tests cannot construct a Player, so payouts are observed through
    /// QuestManager.BluespirePayoutObserver, which fires at the exact point QuestManager hands a ladder
    /// payout to a Player (PayBluespireLadderReward) - on a bare Creature quest bearer it records the
    /// decision without paying anything.
    ///
    /// Fixtures: real rung names (BluespireLadderRewards.RungForQuestName only recognizes the six compiled
    /// names), seeded into the world quest cache with the shipped MinDelta (1209600, from
    /// Content/sql/quests/BluespireLadderD1Cleared.sql). Rung 2 is left alone (KillQuestCreatesRowTests owns
    /// it); rung 4 is seeded with an artificial MaxSolves 1 to drive the IsMaxSolves silent no-op.
    /// </summary>
    [TestClass]
    public class BluespireLadderRepeatPayoutTests
    {
        private const string EmoteRung = "BluespireLadderD1Cleared";     // cleared by NPC StampQuest in content
        private const string KillRung = "BluespireLadderD3Cleared";      // cleared by a boss KillQuest in content
        private const string MaxedRung = "BluespireLadderD4Cleared";     // fixture only: MaxSolves 1
        private const string NonLadderQuest = "TestBluespireRepeatPayoutNonLadder";

        private const uint RungMinDelta = 1209600;   // 14 days, the shipped min_Delta for every rung

        [TestInitialize]
        public void SeedWorldQuests()
        {
            DatabaseManager.World.AddCachedQuest(new Quest { Name = EmoteRung, MaxSolves = -1, MinDelta = RungMinDelta });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = KillRung, MaxSolves = -1, MinDelta = RungMinDelta });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = MaxedRung, MaxSolves = 1, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = NonLadderQuest, MaxSolves = -1, MinDelta = 0 });
        }

        [TestCleanup]
        public void ClearWorldQuests()
        {
            DatabaseManager.World.ClearCachedQuest(EmoteRung);
            DatabaseManager.World.ClearCachedQuest(KillRung);
            DatabaseManager.World.ClearCachedQuest(MaxedRung);
            DatabaseManager.World.ClearCachedQuest(NonLadderQuest);
        }

        /// <summary>
        /// Seeds quest_mindelta_rate (read by CanSolve, which Update now consults for a rung restamp) into
        /// PropertyManager's static cache and restores it, per test, because those caches are shared by every
        /// test class in the process.
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

        private static (QuestManager qm, List<(string quest, bool firstClear)> payouts) NewObservedQuestManager()
        {
            var qm = TestCreatures.CreateQuestBearer().QuestManager;
            var payouts = new List<(string quest, bool firstClear)>();
            qm.BluespirePayoutObserver = (quest, firstClear) => payouts.Add((quest, firstClear));
            return (qm, payouts);
        }

        private static void ExpireCooldown(QuestManager qm, string questName)
        {
            // GetQuest returns the live registry entry, so this back-dates the clear past its window
            qm.GetQuest(questName).LastTimeCompleted -= RungMinDelta + 1;
        }

        [TestMethod]
        public void EmoteStamp_FirstClear_PaysOnce_AsFirstClear()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                // EmoteType.StampQuest -> QuestManager.Stamp -> Update
                qm.Stamp(EmoteRung);

                Assert.AreEqual(1, qm.GetCurrentSolves(EmoteRung));
                CollectionAssert.AreEqual(new[] { (EmoteRung, true) }, payouts);
            });
        }

        /// <summary>
        /// The prod bug: an emote StampQuest re-clear of D1 after its cooldown advanced the row and paid
        /// nothing. Fails against the pre-fix code (the existing-row branch made no payout).
        /// </summary>
        [TestMethod]
        public void EmoteStamp_ReclearAfterCooldown_PaysRepeatExactlyOnce()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                qm.Stamp(EmoteRung);
                ExpireCooldown(qm, EmoteRung);
                qm.Stamp(EmoteRung);

                Assert.AreEqual(2, qm.GetCurrentSolves(EmoteRung), "the re-clear must advance the row");
                CollectionAssert.AreEqual(new[] { (EmoteRung, true), (EmoteRung, false) }, payouts,
                    "first clear pays firstClear:true, the post-cooldown re-clear pays firstClear:false, once each");
            });
        }

        /// <summary>
        /// Update enforces MaxSolves but not MinDelta, so a caller that restamps a rung inside its cooldown
        /// (an admin stamp, a racing second Use) still advances the row. It must not pay: the payout reads
        /// CanSolve before the stamp moves LastTimeCompleted.
        /// </summary>
        [TestMethod]
        public void EmoteStamp_RestampInsideCooldown_AdvancesRow_PaysNothing()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                qm.Stamp(EmoteRung);
                qm.Stamp(EmoteRung);

                Assert.AreEqual(2, qm.GetCurrentSolves(EmoteRung), "Update itself still advances the row inside the window");
                CollectionAssert.AreEqual(new[] { (EmoteRung, true) }, payouts, "an inside-cooldown restamp must not pay");
            });
        }

        /// <summary>
        /// IncrementQuest with an amount runs Update once per unit. Only the first unit lands after the
        /// cooldown; the rest land inside the window it just restarted, so the whole call pays once.
        /// </summary>
        [TestMethod]
        public void Increment_AfterCooldown_PaysOneRepeat_NotOnePerUnit()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                qm.Stamp(EmoteRung);
                ExpireCooldown(qm, EmoteRung);
                qm.Increment(EmoteRung, 3);

                Assert.AreEqual(4, qm.GetCurrentSolves(EmoteRung));
                CollectionAssert.AreEqual(new[] { (EmoteRung, true), (EmoteRung, false) }, payouts);
            });
        }

        /// <summary>
        /// The kill path (ApplyKillStamp -> Stamp -> Update): Created pays the first clear once, OnCooldown
        /// pays nothing, Restamped pays the repeat once - and only once, because HandleKillTask no longer
        /// adds a payout of its own (pinned by BluespireLadderKillCooldownTests.
        /// HandleKillTask_CallsApplyKillStamp_AndPaysNothingItself, since HandleKillTask needs a Player).
        /// </summary>
        [TestMethod]
        public void KillStamp_D3_FirstClear_Cooldown_Reclear_EachPaysExactlyOnce()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                Assert.AreEqual(QuestManager.KillStampOutcome.Created, qm.ApplyKillStamp(KillRung, bootstrapRow: true));
                CollectionAssert.AreEqual(new[] { (KillRung, true) }, payouts);

                Assert.AreEqual(QuestManager.KillStampOutcome.OnCooldown, qm.ApplyKillStamp(KillRung, bootstrapRow: true));
                CollectionAssert.AreEqual(new[] { (KillRung, true) }, payouts, "a kill on cooldown must not pay");

                ExpireCooldown(qm, KillRung);

                Assert.AreEqual(QuestManager.KillStampOutcome.Restamped, qm.ApplyKillStamp(KillRung, bootstrapRow: true));
                CollectionAssert.AreEqual(new[] { (KillRung, true), (KillRung, false) }, payouts,
                    "a post-cooldown kill re-clear pays the repeat exactly once");
            });
        }

        [TestMethod]
        public void NonLadderQuest_CreateAndRestamp_PaysNothing()
        {
            var (qm, payouts) = NewObservedQuestManager();

            qm.Stamp(NonLadderQuest);
            qm.Stamp(NonLadderQuest);
            qm.SetQuestCompletions(NonLadderQuest, 7);

            Assert.AreEqual(7, qm.GetCurrentSolves(NonLadderQuest));
            Assert.AreEqual(0, payouts.Count, "a quest outside the six rung names must never route a ladder payout");
        }

        /// <summary>
        /// The IsMaxSolves silent no-op on an existing rung row: the row does not advance, so nothing pays.
        /// (The mule and PvP template guards are the same shape - an early return in front of the branch -
        /// but only apply to a Player, so they are pinned by source order in
        /// Update_SilentNoOpGuards_ReturnBeforeTheRepeatPayout below.)
        /// </summary>
        [TestMethod]
        public void MaxSolvesNoOp_OnRungRow_PaysNothing()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                qm.Stamp(MaxedRung);
                qm.Stamp(MaxedRung);

                Assert.AreEqual(1, qm.GetCurrentSolves(MaxedRung), "IsMaxSolves must refuse the restamp");
                CollectionAssert.AreEqual(new[] { (MaxedRung, true) }, payouts, "a refused restamp must not pay");
            });
        }

        /// <summary>
        /// SetQuestCompletions' existing-row branch is an absolute set (admin repair, SetQuestBits), not a
        /// clear, and is deliberately not a repeat payer even when it raises the count after the cooldown.
        /// </summary>
        [TestMethod]
        public void SetQuestCompletions_ExistingRungRow_PaysNoRepeat()
        {
            WithSeededMinDeltaRate(() =>
            {
                var (qm, payouts) = NewObservedQuestManager();

                qm.Stamp(EmoteRung);
                ExpireCooldown(qm, EmoteRung);
                qm.SetQuestCompletions(EmoteRung, 5);

                Assert.AreEqual(5, qm.GetCurrentSolves(EmoteRung));
                CollectionAssert.AreEqual(new[] { (EmoteRung, true) }, payouts);
            });
        }

        /// <summary>
        /// One owner: QuestManager hands a ladder payout to the Player in exactly one place.
        /// </summary>
        [TestMethod]
        public void QuestManager_HandsLadderPayoutToPlayer_InExactlyOnePlace()
        {
            var src = QuestManagerSource();

            Assert.AreEqual(1, Regex.Matches(src, @"\.TryPayBluespireLadderReward\(").Count,
                "QuestManager must call Player.TryPayBluespireLadderReward from PayBluespireLadderReward only");
        }

        /// <summary>
        /// The mule guard and the PvP template guard (Player-only, so not drivable here) must return before
        /// Update's repeat payout, so a silent no-op on either can never pay.
        /// </summary>
        [TestMethod]
        public void Update_SilentNoOpGuards_ReturnBeforeTheRepeatPayout()
        {
            var src = QuestManagerSource();

            var start = src.IndexOf("public void Update(string questFormat)", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Update is missing");

            var end = src.IndexOf("public void SetQuestCompletions(", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "SetQuestCompletions no longer follows Update");

            var body = src.Substring(start, end - start);

            var mule = body.IndexOf("MuleBlocked(MuleAction.AdvanceQuest, notify: false))\n                return;", StringComparison.Ordinal);
            var template = body.IndexOf("PvpTemplateBlocked(PvpTemplateAction.QuestStamp) != null)\n                return;", StringComparison.Ordinal);
            var maxSolves = body.IndexOf("if (IsMaxSolves(questName))", StringComparison.Ordinal);
            var repeat = body.IndexOf("PayBluespireLadderReward(quest.QuestName, firstClear: false);", StringComparison.Ordinal);

            Assert.IsTrue(mule >= 0, "Update's mule guard must return silently");
            Assert.IsTrue(template >= 0, "Update's PvP template guard must return silently");
            Assert.IsTrue(maxSolves >= 0, "Update's IsMaxSolves guard is missing");
            Assert.IsTrue(repeat >= 0, "Update's existing-row branch must own the repeat payout");

            Assert.IsTrue(mule < repeat && template < repeat && maxSolves < repeat,
                "every silent no-op guard must run before the repeat payout");
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
