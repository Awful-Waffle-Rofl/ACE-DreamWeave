using System.Collections.Generic;
using System.Reflection;

using ACE.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-06 objectives (TECH-DESIGN 2.5) and the WorldEventObjectiveFactory body.
    /// Every objective is driven through its testable *Core method (D6) - no live Creature/Player/Session
    /// is constructed anywhere in this file. Factory tests build a WorldEvent the same way
    /// WorldEventStateMachineTests does (a hand-built composition, no landblock bridge).
    /// </summary>
    [TestClass]
    public class WorldEventObjectiveTests
    {
        private const double T0 = 1_700_000_000d;

        // ---- shared builders ---------------------------------------------------------------------------

        private static GoalDef KillCountGoal(MvpRule rule = MvpRule.MostKills, int @base = 5, double perParticipant = 0, int cap = 150)
        {
            return new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = rule.ToString(),
                RuleKind = rule,
                Count = new ScaledCount { Base = @base, PerParticipant = perParticipant, Cap = cap },
                ProgressTemplate = "{killed} of {target} slain."
            };
        }

        private static GoalDef DestroySourceGoal()
        {
            return new GoalDef
            {
                Id = "destroy_source",
                DisplayName = "Destroy the Rifts",
                Type = "DestroySource",
                TypeKind = GoalType.DestroySource,
                MvpRule = "killingBlowAndTopDamage",
                RuleKind = MvpRule.KillingBlowAndTopDamage,
                ProgressTemplate = "{remaining} of {total} rifts remain."
            };
        }

        private static GoalDef KillBossGoal()
        {
            return new GoalDef
            {
                Id = "kill_boss",
                DisplayName = "Kill the Champion",
                Type = "KillBoss",
                TypeKind = GoalType.KillBoss,
                MvpRule = "killingBlowAndTopDamage",
                RuleKind = MvpRule.KillingBlowAndTopDamage
            };
        }

        /// <summary>
        /// Populates a real WorldEventParticipation's private records dictionary via reflection, so
        /// KillCountObjective's MostKills MVP path can be exercised against ledger.TopKiller() without
        /// constructing a live Creature/DamageHistoryInfo (which CreditDamage/CreditKill require).
        /// </summary>
        private static WorldEventParticipation LedgerWith(params ParticipantRecord[] records)
        {
            var ledger = new WorldEventParticipation(() => T0);

            var field = typeof(WorldEventParticipation).GetField("records", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (Dictionary<uint, ParticipantRecord>)field.GetValue(ledger);

            foreach (var rec in records)
                dict[rec.CharacterGuid] = rec;

            return ledger;
        }

        private static ParticipantRecord Record(uint guid, string name, int kills, double firstCredit = T0)
        {
            return new ParticipantRecord { CharacterGuid = guid, Name = name, Kills = kills, FirstCredit = firstCredit };
        }

        // ================================================================================================
        // KillCountObjective
        // ================================================================================================

        [TestMethod]
        public void KillCount_CompletesAtExactlyTargetAndNotOneShort()
        {
            var goal = KillCountGoal(@base: 3);
            var objective = new KillCountObjective(goal, () => 0, () => new List<uint>(), LedgerWith());

            objective.OnDeathCore(1, 100, true, "Alice", "Alice");
            objective.OnDeathCore(2, 100, true, "Alice", "Alice");

            Assert.IsFalse(objective.IsComplete, "two of three kills must not complete the objective");

            objective.OnDeathCore(3, 100, true, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete, "the third kill must complete a target-3 objective");
        }

        [TestMethod]
        public void KillCount_IgnoresNonEventDeathsAndSourceGuids()
        {
            var goal = KillCountGoal(@base: 2);
            var sources = new List<uint> { 500 };
            var objective = new KillCountObjective(goal, () => 0, () => sources, LedgerWith());

            objective.OnDeathCore(1, 100, false, "Alice", "Alice"); // not an event creature
            objective.OnDeathCore(500, 100, true, "Alice", "Alice"); // a source (rift) spawn, not trash

            Assert.IsFalse(objective.IsComplete, "neither a non-event death nor a source death should count");

            objective.OnDeathCore(2, 100, true, "Alice", "Alice");
            objective.OnDeathCore(3, 100, true, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete, "two genuine trash kills must complete a target-2 objective");
        }

        [TestMethod]
        public void KillCount_TargetIsFrozenFromTheFirstEvaluation()
        {
            var goal = KillCountGoal(@base: 10, perParticipant: 0);
            var participants = 0;

            var objective = new KillCountObjective(goal, () => participants, () => new List<uint>(), LedgerWith());

            // First evaluation - Tick - reads participantCount() == 0 and freezes the target at base (10).
            objective.Tick(T0);

            // Audience is sampled AFTER the objective is built in production; simulate that here by
            // changing what the Func now returns. The frozen target must not move.
            participants = 100;

            for (var i = 1; i <= 9; i++)
                objective.OnDeathCore((uint)i, 1, true, "Alice", "Alice");

            Assert.IsFalse(objective.IsComplete, "9 of a frozen target of 10 must not complete");

            objective.OnDeathCore(10, 1, true, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete, "the 10th kill must complete the target frozen at construction-time participant count");
        }

        [TestMethod]
        public void KillCount_ProgressText_SubstitutesKilledAndTarget()
        {
            var goal = KillCountGoal(@base: 4);
            var objective = new KillCountObjective(goal, () => 0, () => new List<uint>(), LedgerWith());

            objective.OnDeathCore(1, 1, true, "Alice", "Alice");

            Assert.AreEqual("1 of 4 slain.", objective.ProgressText);
        }

        [TestMethod]
        public void KillCount_Mvp_MostKills_ReadsTheLedgersTopKiller()
        {
            var goal = KillCountGoal(MvpRule.MostKills, @base: 1);
            var ledger = LedgerWith(Record(1, "Alice", 5), Record(2, "Bob", 2));

            var objective = new KillCountObjective(goal, () => 0, () => new List<uint>(), ledger);
            objective.OnDeathCore(1, 1, true, "Alice", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Alice", mvp.Name);
            Assert.AreEqual("most kills", mvp.Reason);
            Assert.AreEqual(5, mvp.Kills);
        }

        [TestMethod]
        public void KillCount_Mvp_KillingBlow_UsesTheLastRecordedKillingBlowName()
        {
            var goal = KillCountGoal(MvpRule.KillingBlow, @base: 1);
            var objective = new KillCountObjective(goal, () => 0, () => new List<uint>(), LedgerWith());

            objective.OnDeathCore(1, 1, true, "Bob", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Bob", mvp.Name);
            Assert.AreEqual("killing blow", mvp.Reason);
        }

        [TestMethod]
        public void KillCount_Mvp_KillingBlowAndTopDamage_NamesBothWhenDifferent()
        {
            var goal = KillCountGoal(MvpRule.KillingBlowAndTopDamage, @base: 1);
            var objective = new KillCountObjective(goal, () => 0, () => new List<uint>(), LedgerWith());

            objective.OnDeathCore(1, 1, true, "Bob", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Bob", mvp.Name);
            Assert.AreEqual("struck the killing blow, top damage: Alice", mvp.Reason);
        }

        [TestMethod]
        public void KillCount_Mvp_KillingBlowAndTopDamage_OneNameWhenEqual()
        {
            var goal = KillCountGoal(MvpRule.KillingBlowAndTopDamage, @base: 1);
            var objective = new KillCountObjective(goal, () => 0, () => new List<uint>(), LedgerWith());

            objective.OnDeathCore(1, 1, true, "Alice", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Alice", mvp.Name);
            Assert.AreEqual("killing blow", mvp.Reason);
        }

        // ================================================================================================
        // DestroySourceObjective
        // ================================================================================================

        [TestMethod]
        public void DestroySource_CompletesOnlyOnTheLastRegisteredGuid()
        {
            var sources = new List<uint> { 10, 11, 12 };
            var objective = new DestroySourceObjective(DestroySourceGoal(), () => sources);

            objective.OnDeathCore(10, "Alice", "Alice");
            objective.OnDeathCore(11, "Alice", "Alice");

            Assert.IsFalse(objective.IsComplete, "two of three rifts down must not complete the objective");

            objective.OnDeathCore(12, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete, "the last rift's death must complete the objective");
        }

        [TestMethod]
        public void DestroySource_UnregisteredGuidIsIgnored()
        {
            var sources = new List<uint> { 10 };
            var objective = new DestroySourceObjective(DestroySourceGoal(), () => sources);

            objective.OnDeathCore(999, "Alice", "Alice"); // not a registered source

            Assert.IsFalse(objective.IsComplete);

            objective.OnDeathCore(10, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete);
        }

        [TestMethod]
        public void DestroySource_EmptySourcesNeverCompletes()
        {
            var objective = new DestroySourceObjective(DestroySourceGoal(), () => new List<uint>());

            Assert.IsFalse(objective.IsComplete);
            Assert.AreEqual("no rifts were placed", objective.ProgressText);
        }

        [TestMethod]
        public void DestroySource_Mvp_NamesLastRiftKillerAndTopDamagerWhenDifferent()
        {
            var sources = new List<uint> { 10 };
            var objective = new DestroySourceObjective(DestroySourceGoal(), () => sources);

            objective.OnDeathCore(10, "Bob", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Bob", mvp.Name);
            Assert.AreEqual("destroyed the last rift, top damage: Alice", mvp.Reason);
        }

        [TestMethod]
        public void DestroySource_Mvp_OnlyOneNameWhenEqual()
        {
            var sources = new List<uint> { 10 };
            var objective = new DestroySourceObjective(DestroySourceGoal(), () => sources);

            objective.OnDeathCore(10, "Alice", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Alice", mvp.Name);
            Assert.AreEqual("destroyed the last rift", mvp.Reason);
        }

        // ================================================================================================
        // KillBossObjective
        // ================================================================================================

        [TestMethod]
        public void KillBoss_CompletesOnlyOnTheBossGuid()
        {
            var objective = new KillBossObjective(KillBossGoal(), () => 777);

            objective.OnDeathCore(1, "Alice", "Alice");

            Assert.IsFalse(objective.IsComplete, "a non-boss death must not complete the objective");

            objective.OnDeathCore(777, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete);
        }

        [TestMethod]
        public void KillBoss_ZeroBossGuidNeverCompletes()
        {
            var objective = new KillBossObjective(KillBossGoal(), () => 0);

            objective.OnDeathCore(0, "Alice", "Alice");

            Assert.IsFalse(objective.IsComplete);
            Assert.AreEqual("the champion has not yet appeared", objective.ProgressText);
        }

        [TestMethod]
        public void KillBoss_Mvp_Shape()
        {
            var objective = new KillBossObjective(KillBossGoal(), () => 777);

            objective.OnDeathCore(777, "Bob", "Alice");

            var mvp = objective.Mvp();

            Assert.AreEqual("Bob", mvp.Name);
            Assert.AreEqual("struck the killing blow, top damage: Alice", mvp.Reason);

            Assert.AreEqual("The champion has fallen.", objective.ProgressText);
        }

        /// <summary>
        /// Code review finding on #602: the combo Reason text must be a full verb phrase, not just
        /// "killing blow, top damage: X", because WorldEventAnnouncer.MvpSentence only special-cases the
        /// exact string "killing blow" - anything else (including the old combo text) fell into the
        /// raw-text fallback and broadcast as "Alice killing blow, top damage: Bob." with no verb.
        /// WorldEventAnnouncer.MvpSentence itself is private static, so its composed sentence is not
        /// directly testable here - this only pins the Reason text objectives produce.
        /// </summary>
        [TestMethod]
        public void KillBoss_Mvp_ComboReason_ReadsAsAFullVerbPhrase()
        {
            var objective = new KillBossObjective(KillBossGoal(), () => 777);

            objective.OnDeathCore(777, "Bob", "Alice");

            var mvp = objective.Mvp();

            StringAssert.StartsWith(mvp.Reason, "struck the killing blow");
        }

        // ================================================================================================
        // WorldEventObjectiveFactory
        // ================================================================================================

        private static WorldEventComposition BuildComposition(GoalDef goal, BossDef boss)
        {
            var source = new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "edges",
                GeometryKind = SourceGeometry.Edges,
                GeometryRadius = 45f,
                GeometryPoints = 3,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                RewardRadius = 60f,
                CompatibleGoals = new List<string> { goal.Id }
            };

            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string>(),
                Members = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 }
                }
            };

            var reward = new RewardDef
            {
                Id = "standard",
                DisplayName = "Hammer Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = 300,
                GateByCharacter = true,
                GateByAccount = true,
                GateByIp = true
            };

            var anchor = new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() };

            return new WorldEventComposition(source, new[] { family }, boss, goal, reward, anchor,
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);
        }

        private static WorldEvent BuildEvent(GoalDef goal, BossDef boss)
        {
            var request = new WorldEventRequest { SourceId = "ambush", FamilyId = "emberwrought", GoalId = goal.Id, Invoker = "test" };

            return new WorldEvent(1, BuildComposition(goal, boss), request, null,
                () => new AudienceEstimate(3, 100, 150), () => T0);
        }

        [TestMethod]
        public void Factory_Hold_ReturnsAnError()
        {
            var goal = new GoalDef { Id = "hold", DisplayName = "Hold", Type = "Hold", TypeKind = GoalType.Hold, HoldSeconds = 60 };
            var evt = BuildEvent(goal, BossDef.None);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNull(objective);
            StringAssert.Contains(error, "Hold");
        }

        [TestMethod]
        public void Factory_KillBossWithBossNone_ReturnsAnError()
        {
            var evt = BuildEvent(KillBossGoal(), BossDef.None);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNull(objective);
            StringAssert.Contains(error, "not 'none'");
        }

        [TestMethod]
        public void Factory_KillBossWithFamilyChampion_Succeeds()
        {
            var evt = BuildEvent(KillBossGoal(), BossDef.FamilyChampion);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNotNull(objective);
            Assert.IsNull(error);
            Assert.IsInstanceOfType(objective, typeof(KillBossObjective));
        }

        [TestMethod]
        public void Factory_KillCount_Succeeds()
        {
            var evt = BuildEvent(KillCountGoal(), BossDef.None);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNotNull(objective);
            Assert.IsNull(error);
            Assert.IsInstanceOfType(objective, typeof(KillCountObjective));
        }

        [TestMethod]
        public void Factory_DestroySource_Succeeds()
        {
            var evt = BuildEvent(DestroySourceGoal(), BossDef.None);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNotNull(objective);
            Assert.IsNull(error);
            Assert.IsInstanceOfType(objective, typeof(DestroySourceObjective));
        }

        [TestMethod]
        public void Factory_UnknownGoalType_ReturnsAnError()
        {
            var goal = new GoalDef { Id = "mystery", DisplayName = "Mystery", Type = "Mystery", TypeKind = (GoalType)99 };
            var evt = BuildEvent(goal, BossDef.None);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNull(objective);
            StringAssert.Contains(error, "unknown goal type");
        }
    }
}
