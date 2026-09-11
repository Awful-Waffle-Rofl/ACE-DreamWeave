using System.Collections.Generic;

using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WP-20: never spawn a wave nobody needs to kill.
    ///
    /// The defect this covers, from a live run: the cadence fired one more wave while the objective needed
    /// fewer kills than the field already held, the player killed the one creature that completed it, and
    /// Finish destroyed the rest of that wave in front of them. The fix is a single extra refusal in the
    /// pure cadence predicate, fed by a new objective member.
    ///
    /// Pure members only (TECH-DESIGN D6) - no Creature, Player, Session or Landblock is constructed here.
    /// </summary>
    [TestClass]
    public class WorldEventWp20Tests
    {
        private const double Interval = 40d;
        private const double MinGap = WorldEvent.MinWaveGapSeconds;

        // ================================================================================================
        // ShouldSpawnNextWave - the new remainingKills term
        // ================================================================================================

        /// <summary>
        /// The defect itself. The cadence timer HAS elapsed - pre-WP-20 that alone spawned a wave - but the
        /// 5 creatures already standing can finish a 5-kill objective by themselves, so nothing is added.
        /// </summary>
        [TestMethod]
        public void ShouldSpawnNextWave_IntervalElapsed_AliveExactlyMeetsRemaining_IsFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 5,
                now: 140d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 5),
                "5 alive can supply the last 5 kills; a sixth wave would only be destroyed at Finish");
        }

        [TestMethod]
        public void ShouldSpawnNextWave_IntervalElapsed_AliveExceedsRemaining_IsFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 9,
                now: 140d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 3));
        }

        /// <summary>The term must not become a general brake: one kill short of enough still spawns.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_IntervalElapsed_AliveOneShortOfRemaining_IsTrue()
        {
            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 4,
                now: 140d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 5),
                "4 alive cannot supply 5 kills, so the run still needs pressure");
        }

        /// <summary>The suppression must reach the cleared-field trigger too, not only the interval one.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_ClearedField_RemainingZero_IsFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 100d + MinGap, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 0),
                "an objective needing no further kills must not be handed a fresh wave by the clear-field rule");
        }

        [TestMethod]
        public void ShouldSpawnNextWave_ClearedField_RemainingPositive_StillFires()
        {
            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 100d + MinGap, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 4),
                "WP-18's wave-on-clear must survive WP-20 untouched while kills are still owed");
        }

        /// <summary>
        /// Null is "no opinion" and must leave every pre-WP-20 term exactly as it was - including the one
        /// case the new term would otherwise swallow, a full field with the cadence elapsed.
        /// </summary>
        [TestMethod]
        public void ShouldSpawnNextWave_NullRemaining_BehavesExactlyAsBeforeWp20()
        {
            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 9,
                now: 140d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null),
                "interval elapsed with creatures alive is the pre-WP-18 behaviour and must be preserved");

            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 110d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null),
                "wave-on-clear must be preserved");

            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 101d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null),
                "the 3 s floor must be preserved");
        }

        /// <summary>
        /// A zero remaining count must not resurrect a wave that some OTHER term already refused. The
        /// refusals are independent, and the new one is only ever additional.
        /// </summary>
        [TestMethod]
        public void ShouldSpawnNextWave_TheNewTermNeverOverridesTheOldRefusals()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(active: false, objectiveComplete: false, 0, 0,
                now: 999d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 50));

            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, waveIndexSpawned: -1, liveCount: 0,
                now: 999d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: 50),
                "a huge remaining count must not pre-empt wave 0");

            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 999d, lastWaveAt: 100d, interval: 0d, minGap: MinGap, remainingKills: 50),
                "an interval of 0 still disables the cadence entirely");
        }

        // ================================================================================================
        // KillCountObjective.RemainingKills
        // ================================================================================================

        private static GoalDef KillCountGoal(int @base)
        {
            return new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = MvpRule.MostKills,
                Count = new ScaledCount { Base = @base, PerParticipant = 0, Cap = 150 },
                ProgressTemplate = "{killed} of {target} slain."
            };
        }

        private static KillCountObjective KillCount(int @base, int participants = 0)
        {
            return new KillCountObjective(KillCountGoal(@base), () => participants,
                () => new List<uint>(), new WorldEventParticipation(() => 0d));
        }

        [TestMethod]
        public void KillCount_RemainingKills_CountsDownWithKills()
        {
            var objective = KillCount(@base: 5);

            objective.Tick(0d); // freezes the target at 5

            Assert.AreEqual(5, objective.RemainingKills);

            objective.OnDeathCore(1, 100, true, "Alice", "Alice");
            objective.OnDeathCore(2, 100, true, "Alice", "Alice");

            Assert.AreEqual(3, objective.RemainingKills);
        }

        /// <summary>
        /// Feeding a NEGATIVE remaining into the cadence would be harmless today (liveCount >= a negative is
        /// always true) but reads as nonsense in the skip log, so it is floored.
        /// </summary>
        [TestMethod]
        public void KillCount_RemainingKills_NeverGoesNegativePastTheTarget()
        {
            var objective = KillCount(@base: 2);

            for (uint guid = 1; guid <= 5; guid++)
                objective.OnDeathCore(guid, 100, true, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete);
            Assert.AreEqual(0, objective.RemainingKills, "five kills against a target of two must report 0, not -3");
        }

        /// <summary>
        /// The one trap in this member: <c>Target</c> is lazily resolved and FROZEN on first read, against
        /// whatever participant count is current at that instant. The cadence asking "how many more?" must
        /// never be what decides how big the run is, so RemainingKills reads the backing field and answers
        /// null until something else has frozen it.
        /// </summary>
        [TestMethod]
        public void KillCount_RemainingKills_IsNullBeforeTheFreeze_AndDoesNotCauseIt()
        {
            var participants = 0;

            var objective = new KillCountObjective(KillCountGoal(@base: 10), () => participants,
                () => new List<uint>(), new WorldEventParticipation(() => 0d));

            Assert.IsNull(objective.RemainingKills, "nothing has frozen the target yet, so there is no opinion to give");

            // If the read above had frozen the target, it would have frozen at 0 participants. Stage samples
            // the audience after the objective is built, so a later evaluation must still see the real count.
            participants = 40;

            objective.Tick(0d);

            Assert.AreEqual(10, objective.RemainingKills,
                "reading RemainingKills must not have frozen the target ahead of the first real evaluation");
        }

        // ================================================================================================
        // The objectives with no opinion
        // ================================================================================================

        /// <summary>
        /// Rift deaths, not trash deaths, complete this objective, so it must return null rather than 0.
        /// Zero would read as "no kills needed" and suppress every remaining wave of the run.
        /// </summary>
        [TestMethod]
        public void DestroySource_RemainingKills_IsNullNotZero()
        {
            var objective = new DestroySourceObjective(new GoalDef
            {
                Id = "destroy_source",
                Type = "DestroySource",
                TypeKind = GoalType.DestroySource,
                MvpRule = "killingBlowAndTopDamage",
                RuleKind = MvpRule.KillingBlowAndTopDamage,
                ProgressTemplate = "{remaining} of {total} rifts remain."
            }, () => new List<uint> { 1, 2, 3 });

            Assert.IsNull(objective.RemainingKills);
        }

        [TestMethod]
        public void KillBoss_RemainingKills_IsNullNotZero()
        {
            var objective = new KillBossObjective(new GoalDef
            {
                Id = "kill_boss",
                Type = "KillBoss",
                TypeKind = GoalType.KillBoss,
                MvpRule = "killingBlowAndTopDamage",
                RuleKind = MvpRule.KillingBlowAndTopDamage
            }, () => 777);

            Assert.IsNull(objective.RemainingKills);
        }

        /// <summary>
        /// The interface default. An implementation written before WP-20 keeps compiling and reports "no
        /// opinion", so the cadence it sees is byte-for-byte the pre-WP-20 one.
        /// </summary>
        private sealed class OpinionlessObjective : IWorldEventObjective
        {
            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager) { }

            public void Tick(double now) { }

            public bool IsComplete => false;

            public string ProgressText => "";

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        [TestMethod]
        public void IWorldEventObjective_RemainingKills_DefaultsToNull()
        {
            IWorldEventObjective objective = new OpinionlessObjective();

            Assert.IsNull(objective.RemainingKills,
                "an objective that never heard of WP-20 must read as 'no opinion', never as 'zero kills needed'");
        }
    }
}
