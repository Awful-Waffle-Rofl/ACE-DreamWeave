using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Server-side ground-truth tests for the quest-logic oracle (<see cref="QuestManager"/>),
    /// driven entirely in-process: no MySQL, no dat files, no network. This is the cheap,
    /// deterministic base of the synthetic-testing pyramid (Approach A) -- it asserts on the
    /// server's own quest registry rather than anything a client can see.
    ///
    /// Quests are stamped on a bare Creature (the non-player <c>runtimeQuests</c> registry),
    /// and the world-side quest definitions (MaxSolves / MinDelta) are injected via
    /// <see cref="WorldDatabaseWithEntityCache.AddCachedQuest"/> so gating is fully controlled.
    ///
    /// One exception: the time-scaled cooldown path (CanSolve *within* the MinDelta window)
    /// additionally reads <c>quest_mindelta_rate</c> from shard-config, which requires a live
    /// database. That single test is gated on <see cref="TestEnvironment.RequireDatabases"/>
    /// and skips (Assert.Inconclusive) when no MySQL is reachable, like <c>StartupTests</c>.
    /// </summary>
    [TestClass]
    public class QuestManagerTests
    {
        // world quest fixtures injected into the cache in TestInitialize
        private const string UnlimitedQuest = "TestUnlimitedQuest";   // MaxSolves=-1, no cooldown
        private const string OnceQuest = "TestOnceOnlyQuest";         // MaxSolves=1
        private const string BitsQuest = "TestBitsQuest";            // used as a bitfield
        private const string RepeatableQuest = "TestRepeatable20hQuest"; // the repeatable-every-20h default

        // the repeatable-quest default cadence codified in doctrine: unlimited solves, 20h (72000s) apart
        private const uint TwentyHoursSeconds = 72000;

        [TestInitialize]
        public void SeedWorldQuests()
        {
            DatabaseManager.World.AddCachedQuest(new Quest { Name = UnlimitedQuest, MaxSolves = -1, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = OnceQuest, MaxSolves = 1, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = BitsQuest, MaxSolves = -1, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = RepeatableQuest, MaxSolves = -1, MinDelta = TwentyHoursSeconds });
        }

        [TestCleanup]
        public void ClearWorldQuests()
        {
            DatabaseManager.World.ClearCachedQuest(UnlimitedQuest);
            DatabaseManager.World.ClearCachedQuest(OnceQuest);
            DatabaseManager.World.ClearCachedQuest(BitsQuest);
            DatabaseManager.World.ClearCachedQuest(RepeatableQuest);
        }

        private static QuestManager NewQuestManager() => TestCreatures.CreateQuestBearer().QuestManager;

        [TestMethod]
        public void Stamp_RecordsQuest_AndCounts()
        {
            var qm = NewQuestManager();
            Assert.IsFalse(qm.HasQuest(UnlimitedQuest), "quest should be absent before stamping");

            qm.Stamp(UnlimitedQuest);

            Assert.IsTrue(qm.HasQuest(UnlimitedQuest));
            Assert.AreEqual(1, qm.GetCurrentSolves(UnlimitedQuest));
        }

        [TestMethod]
        public void Increment_And_Decrement_AdjustSolveCount()
        {
            var qm = NewQuestManager();

            qm.Increment(UnlimitedQuest, 3);
            Assert.AreEqual(3, qm.GetCurrentSolves(UnlimitedQuest));

            qm.Decrement(UnlimitedQuest);
            Assert.AreEqual(2, qm.GetCurrentSolves(UnlimitedQuest));
        }

        [TestMethod]
        public void Erase_RemovesQuest()
        {
            var qm = NewQuestManager();
            qm.Stamp(UnlimitedQuest);
            Assert.IsTrue(qm.HasQuest(UnlimitedQuest));

            qm.Erase(UnlimitedQuest);
            Assert.IsFalse(qm.HasQuest(UnlimitedQuest));
        }

        [TestMethod]
        public void EraseAll_RemovesEveryQuest()
        {
            var qm = NewQuestManager();
            qm.Stamp(UnlimitedQuest);
            qm.Stamp(BitsQuest);

            qm.EraseAll();

            Assert.IsFalse(qm.HasQuest(UnlimitedQuest));
            Assert.IsFalse(qm.HasQuest(BitsQuest));
        }

        [TestMethod]
        public void MaxSolves_BlocksFurtherSolves()
        {
            var qm = NewQuestManager();

            qm.Stamp(OnceQuest);
            Assert.AreEqual(1, qm.GetCurrentSolves(OnceQuest));
            Assert.IsTrue(qm.IsMaxSolves(OnceQuest), "a 1-solve quest is maxed after one solve");
            Assert.IsFalse(qm.CanSolve(OnceQuest), "a maxed quest can never be solved again");

            // a stamp past the cap must be a no-op, not an increment
            qm.Stamp(OnceQuest);
            Assert.AreEqual(1, qm.GetCurrentSolves(OnceQuest), "solve count must not exceed MaxSolves");
        }

        [TestMethod]
        public void CanSolve_IsTrue_ForUnstartedQuest()
        {
            var qm = NewQuestManager();
            Assert.IsTrue(qm.CanSolve(RepeatableQuest), "an unstarted quest is immediately solvable");
        }

        [TestMethod]
        public void QuestBits_SetClearAndTest_RoundTrip()
        {
            var qm = NewQuestManager();

            qm.SetQuestBits(BitsQuest, 0x2);
            qm.SetQuestBits(BitsQuest, 0x8);

            Assert.IsTrue(qm.HasQuestBits(BitsQuest, 0x2));
            Assert.IsTrue(qm.HasQuestBits(BitsQuest, 0x8));
            Assert.IsTrue(qm.HasQuestBits(BitsQuest, 0xA), "both bits set");
            Assert.IsTrue(qm.HasNoQuestBits(BitsQuest, 0x4), "unset bit reads as absent");

            qm.SetQuestBits(BitsQuest, 0x2, on: false);
            Assert.IsTrue(qm.HasNoQuestBits(BitsQuest, 0x2), "cleared bit is gone");
            Assert.IsTrue(qm.HasQuestBits(BitsQuest, 0x8), "other bit remains");
        }

        [TestMethod]
        public void HasQuestSolves_RespectsRange()
        {
            var qm = NewQuestManager();
            qm.Increment(UnlimitedQuest, 5);

            Assert.IsTrue(qm.HasQuestSolves(UnlimitedQuest, 1, 10));
            Assert.IsTrue(qm.HasQuestSolves(UnlimitedQuest, 5, 5));
            Assert.IsFalse(qm.HasQuestSolves(UnlimitedQuest, 6, null), "5 solves is below a min of 6");
        }

        /// <summary>
        /// The repeatable-every-20h doctrine end to end: after one solve the quest enters its
        /// ~20h cooldown window (CanSolve false), then becomes solvable again once MinDelta has
        /// elapsed. GetNextSolveTime consults quest_mindelta_rate from shard-config, so this one
        /// is gated on a reachable database.
        /// </summary>
        [TestMethod]
        public void RepeatableQuest_EntersCooldown_ThenSolvableAfterMinDelta()
        {
            TestEnvironment.RequireDatabases();

            var qm = NewQuestManager();
            qm.Stamp(RepeatableQuest);

            Assert.IsFalse(qm.CanSolve(RepeatableQuest), "a just-solved repeatable quest is on cooldown");

            var remaining = qm.GetNextSolveTime(RepeatableQuest);
            Assert.IsTrue(remaining.TotalSeconds > 0 && remaining.TotalSeconds <= TwentyHoursSeconds + 5,
                $"cooldown should sit within the 20h window, was {remaining}");

            // simulate the cooldown elapsing by back-dating the last completion (GetQuest returns
            // the live non-player registry entry, not a copy)
            var quest = qm.GetQuest(RepeatableQuest);
            quest.LastTimeCompleted -= TwentyHoursSeconds + 1;

            Assert.IsTrue(qm.CanSolve(RepeatableQuest), "quest is solvable again once MinDelta elapses");
        }
    }
}
