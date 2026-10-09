using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Dynamic difficulty (TECH-DESIGN 2.15): the kill-count ratchet, the minimum run duration, overflow
    /// champions, the crowd health multiplier and the real-time pace controller.
    ///
    /// Pure members only (D6) - no Creature, Player, Session or Landblock is constructed here, which is why
    /// every one of the five rules was written as a static or as a class with no engine dependency.
    /// </summary>
    [TestClass]
    public class WorldEventDifficultyTests
    {
        private const double Epsilon = 1e-9;

        // ================================================================================================
        // helpers
        // ================================================================================================

        private static FamilyMember Member(uint wcid, int level, int role)
        {
            return new FamilyMember { Wcid = wcid, Name = $"member{wcid}", Level = level, Role = role };
        }

        private static FamilyDef Family(params FamilyMember[] members)
        {
            return new FamilyDef { Id = "emberwrought", DisplayName = "the Emberwrought", Members = members.ToList() };
        }

        /// <summary>The shipped "ambush" numbers, so the arithmetic below is against a real theme.</summary>
        private static SourceThemeDef Theme(int maxAlive = 24, int waveBase = 4, double perParticipant = 1.5,
            int cap = 18, int overflowPerChampion = 4, int overflowChampionMaxAlive = 3)
        {
            return new SourceThemeDef
            {
                Id = "ambush",
                MaxAlive = maxAlive,
                WaveCount = new ScaledCount { Base = waveBase, PerParticipant = perParticipant, Cap = cap },
                OverflowPerChampion = overflowPerChampion,
                OverflowChampionMaxAlive = overflowChampionMaxAlive
            };
        }

        /// <summary>Median 100, p90 150: trash band [80, 115], elite band [150, 210].</summary>
        private static AudienceEstimate Estimate(int count = 4, int median = 100, int p90 = 150)
        {
            return new AudienceEstimate(count, median, p90);
        }

        private static GoalDef KillCountGoal(int @base, double perParticipant = 0, int cap = 150)
        {
            return new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = MvpRule.MostKills,
                Count = new ScaledCount { Base = @base, PerParticipant = perParticipant, Cap = cap },
                ProgressTemplate = "{killed} of {target} slain."
            };
        }

        private static KillCountObjective KillCount(GoalDef goal, Func<int> participants = null)
        {
            return new KillCountObjective(goal, participants ?? (() => 0),
                () => new List<uint>(), new WorldEventParticipation(() => 0d));
        }

        // ================================================================================================
        // item 1 - the kill-count target ratchets UP
        // ================================================================================================

        [TestMethod]
        public void Ratchet_RaisesTheTargetAsTheCrowdGrows()
        {
            var objective = KillCount(KillCountGoal(@base: 20, perParticipant: 6));

            objective.OnAudienceResampled(2);
            Assert.AreEqual(32, objective.TargetKills, "20 + ceil(6 * 2)");

            objective.OnAudienceResampled(10);
            Assert.AreEqual(80, objective.TargetKills, "20 + ceil(6 * 10)");
        }

        [TestMethod]
        public void Ratchet_NeverLowersTheTargetWhenPlayersLeave()
        {
            var objective = KillCount(KillCountGoal(@base: 20, perParticipant: 6));

            objective.OnAudienceResampled(10);
            Assert.AreEqual(80, objective.TargetKills);

            objective.OnAudienceResampled(1);

            Assert.AreEqual(80, objective.TargetKills,
                "a target that fell would hand a half-killed run an instant completion and count the progress line backwards");
        }

        [TestMethod]
        public void Ratchet_IsWhatFirstSizesAnObjectiveThatWasNeverEvaluated()
        {
            var objective = KillCount(KillCountGoal(@base: 20, perParticipant: 6));

            Assert.IsNull(objective.TargetKills, "nothing has sized it yet");

            objective.OnAudienceResampled(4);

            Assert.AreEqual(44, objective.TargetKills);
            Assert.AreEqual(44, objective.RemainingKills);
        }

        [TestMethod]
        public void Ratchet_ProgressTextReportsTheCurrentTarget()
        {
            var objective = KillCount(KillCountGoal(@base: 10, perParticipant: 1));

            objective.OnAudienceResampled(2);
            objective.OnDeathCore(1, 100, true, "Alice", "Alice");

            Assert.AreEqual("1 of 12 slain.", objective.ProgressText);

            objective.OnAudienceResampled(20);

            Assert.AreEqual("1 of 30 slain.", objective.ProgressText,
                "the player-facing line must follow the ratchet, not the target it was first sized at");
        }

        /// <summary>
        /// A run that was already complete against the old target goes back to incomplete when the ratchet
        /// raises it. That is the intended behaviour and the whole point: the objective is re-sized for the
        /// crowd that is actually there.
        /// </summary>
        [TestMethod]
        public void Ratchet_ReopensAnObjectiveThatTheOldTargetHadAlreadySatisfied()
        {
            var objective = KillCount(KillCountGoal(@base: 2, perParticipant: 1));

            objective.OnAudienceResampled(0);

            objective.OnDeathCore(1, 100, true, "Alice", "Alice");
            objective.OnDeathCore(2, 100, true, "Alice", "Alice");

            Assert.IsTrue(objective.IsComplete);

            objective.OnAudienceResampled(10);

            Assert.IsFalse(objective.IsComplete, "target 12 against 2 kills");
            Assert.AreEqual(10, objective.RemainingKills);
        }

        /// <summary>
        /// The status line must not be what sizes a run, exactly as WP-20 established for RemainingKills.
        /// </summary>
        [TestMethod]
        public void TargetKills_DoesNotSizeTheObjectiveByBeingRead()
        {
            var participants = 0;

            var objective = KillCount(KillCountGoal(@base: 10, perParticipant: 1), () => participants);

            Assert.IsNull(objective.TargetKills);

            participants = 30;

            objective.Tick(0d);

            Assert.AreEqual(40, objective.TargetKills,
                "reading TargetKills must not have frozen the target against the empty audience");
        }

        [TestMethod]
        public void IWorldEventObjective_TargetKills_DefaultsToNull()
        {
            IWorldEventObjective objective = new DestroySourceObjective(new GoalDef
            {
                Id = "destroy_source",
                Type = "DestroySource",
                TypeKind = GoalType.DestroySource,
                MvpRule = "killingBlowAndTopDamage",
                RuleKind = MvpRule.KillingBlowAndTopDamage,
                ProgressTemplate = "{remaining} of {total} rifts remain."
            }, () => new List<uint> { 1 });

            Assert.IsNull(objective.TargetKills, "a goal with no kill target reports none rather than 0");

            // The default no-op must also be safe to call on it.
            objective.OnAudienceResampled(50);
        }

        // ================================================================================================
        // item 2 - the minimum run duration
        // ================================================================================================

        [TestMethod]
        public void MinDurationElapsed_IsFalseUntilTheMinimumPasses()
        {
            Assert.IsFalse(WorldEvent.MinDurationElapsed(now: 1100d, activeAt: 1000d, minDurationSeconds: 300));
            Assert.IsFalse(WorldEvent.MinDurationElapsed(now: 1299.9d, activeAt: 1000d, minDurationSeconds: 300));
            Assert.IsTrue(WorldEvent.MinDurationElapsed(now: 1300d, activeAt: 1000d, minDurationSeconds: 300),
                "the boundary itself counts as elapsed");
            Assert.IsTrue(WorldEvent.MinDurationElapsed(now: 9999d, activeAt: 1000d, minDurationSeconds: 300));
        }

        [TestMethod]
        public void MinDurationElapsed_ZeroDisablesTheMinimumEntirely()
        {
            Assert.IsTrue(WorldEvent.MinDurationElapsed(now: 0d, activeAt: 0d, minDurationSeconds: 0));
            Assert.IsTrue(WorldEvent.MinDurationElapsed(now: 1000d, activeAt: 1000d, minDurationSeconds: 0));
        }

        [TestMethod]
        public void MinDurationElapsed_ARunThatIsNotActiveYetHasNotElapsed()
        {
            Assert.IsFalse(WorldEvent.MinDurationElapsed(now: 5000d, activeAt: 0d, minDurationSeconds: 300),
                "ActiveAt 0 means the run has not started counting, not that it started at the epoch");
        }

        /// <summary>
        /// Owner decision 2026-08-16: BOTH kill_count and destroy_source are held by the minimum. The
        /// remaining time is the window in which players chase the high score and the MVP announcement.
        /// </summary>
        [TestMethod]
        public void CompletionAllowed_HoldsKillCountAndDestroySourceOpen()
        {
            Assert.IsFalse(WorldEvent.CompletionAllowed(GoalType.KillCount, minDurationElapsed: false));
            Assert.IsTrue(WorldEvent.CompletionAllowed(GoalType.KillCount, minDurationElapsed: true));

            Assert.IsFalse(WorldEvent.CompletionAllowed(GoalType.DestroySource, minDurationElapsed: false),
                "a rift run that is won early keeps running until the minimum, like a kill_count one");
            Assert.IsTrue(WorldEvent.CompletionAllowed(GoalType.DestroySource, minDurationElapsed: true));

            Assert.IsTrue(WorldEvent.CompletionAllowed(GoalType.KillBoss, minDurationElapsed: false),
                "kill_boss defers naturally - its champion does not exist until the minimum passes");
            Assert.IsTrue(WorldEvent.CompletionAllowed(GoalType.Hold, minDurationElapsed: false));
        }

        /// <summary>
        /// The obligation that comes with holding destroy_source open: while the minimum is still running,
        /// waves must keep arriving even though every rift is already down, or the held-open window would
        /// be an empty field. After the minimum the original early-out applies again.
        /// </summary>
        [TestMethod]
        public void ShouldStopWavesForDeadSources_IsHeldUntilTheMinimumPasses()
        {
            Assert.IsFalse(WorldEvent.ShouldStopWavesForDeadSources(
                sourcesSpawned: true, allSourcesDead: true, minDurationElapsed: false),
                "the rifts are down but the run is still serving its minimum, so the field must not go quiet");

            Assert.IsTrue(WorldEvent.ShouldStopWavesForDeadSources(
                sourcesSpawned: true, allSourcesDead: true, minDurationElapsed: true),
                "past the minimum the pre-2.15 early-out is restored exactly");
        }

        [TestMethod]
        public void ShouldStopWavesForDeadSources_NeverFiresWhileARiftIsStanding()
        {
            Assert.IsFalse(WorldEvent.ShouldStopWavesForDeadSources(true, allSourcesDead: false, minDurationElapsed: true));

            Assert.IsFalse(WorldEvent.ShouldStopWavesForDeadSources(
                sourcesSpawned: false, allSourcesDead: true, minDurationElapsed: true),
                "a theme that never placed an objective spawn is unaffected either way");
        }

        [TestMethod]
        public void ValidateDurations_RefusesAMinimumAtOrAboveTheMaximum()
        {
            Assert.IsTrue(WorldEvent.ValidateDurations(300, 600, out _));
            Assert.IsTrue(WorldEvent.ValidateDurations(0, 600, out _), "0 is a legitimate 'no minimum'");
            Assert.IsTrue(WorldEvent.ValidateDurations(599, 600, out _));

            Assert.IsFalse(WorldEvent.ValidateDurations(600, 600, out var equalError));
            Assert.IsTrue(equalError.Contains("min-duration"), equalError);

            Assert.IsFalse(WorldEvent.ValidateDurations(900, 600, out _));
            Assert.IsFalse(WorldEvent.ValidateDurations(-1, 600, out _));
            Assert.IsFalse(WorldEvent.ValidateDurations(0, 0, out _));
        }

        /// <summary>
        /// The wave cadence before the minimum: WorldEvent passes remainingKills as NULL, which is the
        /// documented "no opinion" value, so the WP-20 suppression cannot fire. The same inputs WITH the
        /// count would refuse the wave - that contrast is the whole point.
        /// </summary>
        [TestMethod]
        public void BeforeTheMinimum_SuppressionByRemainingKillsIsOff()
        {
            const double interval = 40d;
            const double minGap = WorldEvent.MinWaveGapSeconds;

            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 5,
                now: 140d, lastWaveAt: 100d, interval: interval, minGap: minGap, remainingKills: 5),
                "past the minimum, 5 alive against 5 remaining suppresses the wave (WP-20)");

            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 5,
                now: 140d, lastWaveAt: 100d, interval: interval, minGap: minGap, remainingKills: null),
                "before the minimum the same field keeps getting waves, and the overshoot is accepted");
        }

        [TestMethod]
        public void MinDurationSeconds_DefaultsToFiveMinutesAndIsOverridableByTheRequest()
        {
            Assert.AreEqual(300, WorldEvent.DefaultMinDurationSeconds);
            Assert.AreEqual(300, new WorldEventRequest().MinDurationSeconds);

            var evt = new WorldEvent(1, null, new WorldEventRequest { MinDurationSeconds = 45 }, null,
                () => WorldEventRosterSelector.FloorEstimate, () => 0d);

            Assert.AreEqual(45, evt.MinDurationSeconds);
        }

        [TestMethod]
        public void MinDurationRemaining_CountsDownFromActiveAt()
        {
            var now = 1000d;

            var evt = new WorldEvent(1, null, new WorldEventRequest { MinDurationSeconds = 300 }, null,
                () => WorldEventRosterSelector.FloorEstimate, () => now);

            Assert.AreEqual(300d, evt.MinDurationRemaining(now), Epsilon,
                "a run that has not gone Active still owes the whole minimum");
        }

        // ================================================================================================
        // item 3 - overflow champions
        // ================================================================================================

        [TestMethod]
        public void ResolveRaw_IsTheUncappedWaveDemand()
        {
            var count = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 };

            Assert.AreEqual(4, count.ResolveRaw(0));
            Assert.AreEqual(10, count.ResolveRaw(4));
            Assert.AreEqual(64, count.ResolveRaw(40));
            Assert.AreEqual(18, count.Resolve(40), "Resolve still caps; ResolveRaw is what the cap threw away");
        }

        [TestMethod]
        public void SizeWave_SmallTurnout_PlacesEverythingAndOverflowsNothing()
        {
            var sizing = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 4, currentAlive: 0,
                quantityBonus: 0, championsAlive: 0);

            Assert.AreEqual(10, sizing.Raw);
            Assert.AreEqual(18, sizing.Ceiling);
            Assert.AreEqual(10, sizing.TrashSize);
            Assert.AreEqual(0, sizing.Overflow);
            Assert.AreEqual(0, sizing.ChampionCount);
            Assert.IsFalse(sizing.QuantityMaxed);
        }

        [TestMethod]
        public void SizeWave_BigTurnout_ConvertsTheCappedRemainderIntoChampions()
        {
            var sizing = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 40, currentAlive: 0,
                quantityBonus: 0, championsAlive: 0);

            Assert.AreEqual(64, sizing.Raw);
            Assert.AreEqual(18, sizing.TrashSize, "the theme cap holds");
            Assert.AreEqual(46, sizing.Overflow);
            Assert.AreEqual(3, sizing.ChampionCount, "floor(46 / 4) is 11, capped at overflowChampionMaxAlive 3");
            Assert.IsTrue(sizing.QuantityMaxed);
        }

        [TestMethod]
        public void SizeWave_ChampionsAlreadyStanding_ReduceTheAllowance()
        {
            var sizing = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 40, currentAlive: 0,
                quantityBonus: 0, championsAlive: 2);

            Assert.AreEqual(1, sizing.ChampionCount);

            var full = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 40, currentAlive: 0,
                quantityBonus: 0, championsAlive: 3);

            Assert.AreEqual(0, full.ChampionCount);

            var over = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 40, currentAlive: 0,
                quantityBonus: 0, championsAlive: 9);

            Assert.AreEqual(0, over.ChampionCount, "more standing than the cap is 0, never negative");
        }

        /// <summary>
        /// Overflow champions are additive ABOVE MaxAlive by design, so a field with no room still gets
        /// them - and a full field is precisely the state that generates the most overflow.
        /// </summary>
        [TestMethod]
        public void SizeWave_AFullFieldStillProducesChampions()
        {
            var sizing = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 40, currentAlive: 24,
                quantityBonus: 0, championsAlive: 0);

            Assert.AreEqual(0, sizing.Ceiling);
            Assert.AreEqual(0, sizing.TrashSize, "no room under MaxAlive");
            Assert.AreEqual(64, sizing.Overflow);
            Assert.AreEqual(3, sizing.ChampionCount);
        }

        [TestMethod]
        public void SizeWave_QuantityBonusFeedsTheRawSizeBeforeTheClamp()
        {
            var modest = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 4, currentAlive: 0,
                quantityBonus: 6, championsAlive: 0);

            Assert.AreEqual(16, modest.Raw);
            Assert.AreEqual(16, modest.TrashSize, "still under the cap, so the bonus lands as trash");
            Assert.AreEqual(0, modest.ChampionCount);

            var large = WorldEventRosterSelector.SizeWave(Theme(), participantCount: 4, currentAlive: 0,
                quantityBonus: 20, championsAlive: 0);

            Assert.AreEqual(30, large.Raw);
            Assert.AreEqual(18, large.TrashSize);
            Assert.AreEqual(12, large.Overflow);
            Assert.AreEqual(3, large.ChampionCount, "past the cap the bonus turns into champions instead");
        }

        [TestMethod]
        public void SizeWave_OverflowPerChampionZeroDisablesChampionsEntirely()
        {
            var sizing = WorldEventRosterSelector.SizeWave(Theme(overflowPerChampion: 0), participantCount: 40,
                currentAlive: 0, quantityBonus: 0, championsAlive: 0);

            Assert.AreEqual(46, sizing.Overflow);
            Assert.AreEqual(0, sizing.ChampionCount);
        }

        [TestMethod]
        public void SizeWave_KeepsTheOneCreatureBackstopWhileThereIsAnyRoom()
        {
            var sizing = WorldEventRosterSelector.SizeWave(Theme(maxAlive: 24, waveBase: 1, perParticipant: 0, cap: 1),
                participantCount: 0, currentAlive: 23, quantityBonus: 0, championsAlive: 0);

            Assert.AreEqual(1, sizing.TrashSize);
        }

        [TestMethod]
        public void PickWave_OverflowChampionsAreRoleTwoMembersFromTheEliteBand()
        {
            // The champion band is now EliteBand(a SAMPLED participant level) (#632), and this estimate
            // carries no level list, so every sample is the median 100 and the band is [125, 175]. wcid 7 is
            // inside it; the in-band draw is strict (#629), so an out-of-band role-2 is never reached for.
            var family = Family(Member(1, 100, 0), Member(7, 150, 2), Member(8, 400, 2));

            var pick = WorldEventRosterSelector.PickWave(family, Estimate(count: 40), Theme(maxAlive: 100),
                0, 0, 0, 0, new Random(11));

            Assert.AreEqual(18, pick.Trash.Count);
            Assert.AreEqual(3, pick.Champions.Count);
            Assert.IsTrue(pick.Champions.All(w => w == 7),
                "wcid 8 is level 400, far outside the elite band [125, 175], so the in-band champion wins");
            Assert.IsTrue(pick.ChampionsSynthetic.All(s => !s), "a real in-band champion is never synthetic");
        }

        [TestMethod]
        public void PickWave_WithNoRoleTwoMember_FallsBackToTheRoleOneElites()
        {
            // wcid 5 sits inside EliteBand(100) = [125, 175] - see the test above for why the band anchors
            // on the sampled (here median) level rather than on p90.
            var family = Family(Member(1, 100, 0), Member(5, 150, 1));

            var pick = WorldEventRosterSelector.PickWave(family, Estimate(count: 40), Theme(maxAlive: 100),
                0, 0, 0, 0, new Random(11));

            Assert.AreEqual(3, pick.Champions.Count);
            Assert.IsTrue(pick.Champions.All(w => w == 5));
            Assert.IsTrue(pick.ChampionsSynthetic.All(s => !s), "a real in-band role-1 champion is never synthetic");
        }

        /// <summary>
        /// WP-24 (owner ruling 2026-08-16): a family with neither a role-2 nor a role-1 member no longer
        /// places no champions - the arithmetic's demand is filled by promoting a role-0 member from the
        /// same band instead (synthetic = true), renamed and health-buffed rather than left as plain trash.
        /// </summary>
        [TestMethod]
        public void PickWave_WithNeitherRoleTwoNorRoleOne_PromotesRoleZeroChampions()
        {
            var family = Family(Member(1, 100, 0));

            var pick = WorldEventRosterSelector.PickWave(family, Estimate(count: 40), Theme(maxAlive: 100),
                0, 0, 0, 0, new Random(11));

            Assert.AreEqual(3, pick.Sizing.ChampionCount, "the arithmetic still says three");
            Assert.AreEqual(3, pick.Champions.Count, "the roster has nobody real to draw them from, so a role-0 member is promoted instead");
            Assert.IsTrue(pick.Champions.All(w => w == 1));
            Assert.IsTrue(pick.ChampionsSynthetic.All(s => s), "every one of them must be marked synthetic");
        }

        [TestMethod]
        public void PickWave_CarriesTheSizingBackForThePaceController()
        {
            var family = Family(Member(1, 100, 0));

            var maxed = WorldEventRosterSelector.PickWave(family, Estimate(count: 40), Theme(maxAlive: 100),
                0, 0, 0, 0, new Random(1));

            Assert.IsTrue(maxed.Sizing.QuantityMaxed);

            var roomy = WorldEventRosterSelector.PickWave(family, Estimate(count: 2), Theme(maxAlive: 100),
                0, 0, 0, 0, new Random(1));

            Assert.IsFalse(roomy.Sizing.QuantityMaxed);
        }

        // ================================================================================================
        // item 4 - the crowd health multiplier
        // ================================================================================================

        [TestMethod]
        public void CrowdHealth_IsOneAtOrBelowTheStartThreshold()
        {
            var crowd = new CrowdHealthDef();

            Assert.AreEqual(8, crowd.StartAt);
            Assert.AreEqual(1.0, crowd.Resolve(0), Epsilon);
            Assert.AreEqual(1.0, crowd.Resolve(8), Epsilon);
        }

        [TestMethod]
        public void CrowdHealth_RisesLinearlyAboveTheThreshold()
        {
            var crowd = new CrowdHealthDef();

            Assert.AreEqual(1.1, crowd.Resolve(9), Epsilon);
            Assert.AreEqual(1.4, crowd.Resolve(12), Epsilon);
            Assert.AreEqual(2.0, crowd.Resolve(18), Epsilon);
        }

        [TestMethod]
        public void CrowdHealth_HoldsAtTheCap()
        {
            var crowd = new CrowdHealthDef();

            Assert.AreEqual(3.0, crowd.Resolve(28), Epsilon, "1 + 0.1 * 20 is exactly the cap");
            Assert.AreEqual(3.0, crowd.Resolve(500), Epsilon);
        }

        [TestMethod]
        public void CrowdHealth_ACapBelowOneCannotProduceAWeakerCreature()
        {
            var crowd = new CrowdHealthDef { StartAt = 0, PerParticipant = 0.5, Cap = 0.25 };

            Assert.AreEqual(1.0, crowd.Resolve(40), Epsilon,
                "a mis-authored cap turns the feature off; it never makes wave creatures weaker than the weenie");
        }

        [TestMethod]
        public void CrowdHealth_ThemeWithNoBlockGetsTheDefaults()
        {
            var theme = new SourceThemeDef();

            Assert.IsNotNull(theme.CrowdHealth);
            Assert.AreEqual(CrowdHealthDef.DefaultStartAt, theme.CrowdHealth.StartAt);
            Assert.AreEqual(CrowdHealthDef.DefaultPerParticipant, theme.CrowdHealth.PerParticipant, Epsilon);
            Assert.AreEqual(CrowdHealthDef.DefaultCap, theme.CrowdHealth.Cap, Epsilon);

            Assert.AreEqual(SourceThemeDef.DefaultOverflowPerChampion, theme.OverflowPerChampion);
            Assert.AreEqual(SourceThemeDef.DefaultOverflowChampionMaxAlive, theme.OverflowChampionMaxAlive);
        }

        // ---- the health hook itself --------------------------------------------------------------------

        /// <summary>
        /// CreatureVital.GetMaxValue computes total = StartingValue + Ranks + attr (CreatureVital.cs:147),
        /// and for a freshly constructed non-Player nothing else moves it, so MaxValue tracks StartingValue
        /// one for one. This is the arithmetic that relies on that - the same idiom CombatPet.cs:62-63 uses.
        /// </summary>
        [TestMethod]
        public void ScaledStartingValue_AddsExactlyTheExtraMaximum()
        {
            Assert.AreEqual(80u, WorldEventSpawner.ScaledStartingValue(startingValue: 30, baseMax: 100, mult: 1.5),
                "100 becomes 150, so InitLevel gains the 50");

            Assert.AreEqual(30u, WorldEventSpawner.ScaledStartingValue(30, 100, 1.0), "a multiplier of 1 is a no-op");
            Assert.AreEqual(30u, WorldEventSpawner.ScaledStartingValue(30, 100, 0.5), "never used to WEAKEN a creature");
            Assert.AreEqual(30u, WorldEventSpawner.ScaledStartingValue(30, 0, 4.0), "a creature with no maximum is left alone");
            Assert.AreEqual(30u, WorldEventSpawner.ScaledStartingValue(30, 100, double.NaN));
            Assert.AreEqual(30u, WorldEventSpawner.ScaledStartingValue(30, 100, double.PositiveInfinity));
        }

        [TestMethod]
        public void ScaledStartingValue_RoundsTheNewMaximumHalfAwayFromZero()
        {
            // 45 * 1.1 = 49.5 -> 50, so the delta is 5.
            Assert.AreEqual(15u, WorldEventSpawner.ScaledStartingValue(startingValue: 10, baseMax: 45, mult: 1.1));
        }

        [TestMethod]
        public void ScaledStartingValue_ComposesCrowdAndPaceMultiplicatively()
        {
            var crowd = new CrowdHealthDef().Resolve(18);   // x2.00
            var pace = 1.5;

            Assert.AreEqual(2.0, crowd, Epsilon);

            // 1000 -> 3000, so InitLevel gains 2000.
            Assert.AreEqual(2100u, WorldEventSpawner.ScaledStartingValue(100, 1000, crowd * pace));
        }

        // ================================================================================================
        // item 5 - the real-time pace controller
        // ================================================================================================

        private static PaceDef Pace()
        {
            return new PaceDef();   // 60 / 120 / +2 / +0.25 / cap 4.0
        }

        private static IReadOnlyList<PaceObservation> Cleared(double seconds, bool quantityWasMaxed = false)
        {
            return new List<PaceObservation> { new PaceObservation(seconds, quantityWasMaxed) };
        }

        [TestMethod]
        public void Pace_StartsNeutral()
        {
            var controller = new WorldEventPaceController(Pace());

            Assert.AreEqual(0, controller.QuantityBonus);
            Assert.AreEqual(1.0, controller.HealthMult, Epsilon);
            Assert.IsNull(controller.LastClearSeconds);
        }

        [TestMethod]
        public void Pace_AFastClearAddsQuantityFirst()
        {
            var controller = new WorldEventPaceController(Pace());

            controller.Evaluate(Cleared(20d), oldestAliveWaveIsStale: false);

            Assert.AreEqual(2, controller.QuantityBonus);
            Assert.AreEqual(1.0, controller.HealthMult, Epsilon, "health does not move while quantity has room");
            Assert.AreEqual(20d, controller.LastClearSeconds.Value, Epsilon);

            controller.Evaluate(Cleared(20d), false);

            Assert.AreEqual(4, controller.QuantityBonus);
        }

        [TestMethod]
        public void Pace_AFastClearOnAMaxedWaveRaisesHealthInstead()
        {
            var controller = new WorldEventPaceController(Pace());

            controller.Evaluate(Cleared(20d, quantityWasMaxed: true), false);

            Assert.AreEqual(0, controller.QuantityBonus, "quantity had nothing left to give");
            Assert.AreEqual(1.25, controller.HealthMult, Epsilon);
        }

        [TestMethod]
        public void Pace_HealthStopsAtTheCap()
        {
            var controller = new WorldEventPaceController(Pace());

            for (var i = 0; i < 40; i++)
                controller.Evaluate(Cleared(5d, quantityWasMaxed: true), false);

            Assert.AreEqual(4.0, controller.HealthMult, Epsilon);
        }

        [TestMethod]
        public void Pace_ASlowClearEasesHealthOffBeforeQuantity()
        {
            var controller = new WorldEventPaceController(Pace());

            controller.Evaluate(Cleared(10d), false);                          // quantity 2
            controller.Evaluate(Cleared(10d, quantityWasMaxed: true), false);  // health 1.25

            Assert.AreEqual(2, controller.QuantityBonus);
            Assert.AreEqual(1.25, controller.HealthMult, Epsilon);

            controller.Evaluate(Cleared(300d), false);

            Assert.AreEqual(1.0, controller.HealthMult, Epsilon, "health comes down first");
            Assert.AreEqual(2, controller.QuantityBonus, "quantity is untouched while health is still above 1");

            controller.Evaluate(Cleared(300d), false);

            Assert.AreEqual(0, controller.QuantityBonus, "only now does quantity give way");
        }

        [TestMethod]
        public void Pace_QuantityNeverGoesNegative()
        {
            var controller = new WorldEventPaceController(Pace());

            for (var i = 0; i < 5; i++)
                controller.Evaluate(Cleared(400d), false);

            Assert.AreEqual(0, controller.QuantityBonus);
            Assert.AreEqual(1.0, controller.HealthMult, Epsilon);
        }

        [TestMethod]
        public void Pace_AStaleFieldEasesOffWithoutAnyWaveHavingCleared()
        {
            var controller = new WorldEventPaceController(Pace());

            controller.Evaluate(Cleared(10d, quantityWasMaxed: true), false);

            Assert.AreEqual(1.25, controller.HealthMult, Epsilon);

            controller.Evaluate(new List<PaceObservation>(), oldestAliveWaveIsStale: true);

            Assert.AreEqual(1.0, controller.HealthMult, Epsilon,
                "a wave that is far past the window without ever clearing must still count as too hard");
        }

        /// <summary>
        /// Two slow signals arriving on the same pick - a slow clear AND a stale field - must cost one step,
        /// not two. Otherwise a single bad tick could collapse the whole difficulty at once.
        /// </summary>
        [TestMethod]
        public void Pace_TheSlowSideAppliesAtMostOncePerPick()
        {
            var controller = new WorldEventPaceController(Pace());

            for (var i = 0; i < 4; i++)
                controller.Evaluate(Cleared(10d, quantityWasMaxed: true), false);

            Assert.AreEqual(2.0, controller.HealthMult, Epsilon);

            controller.Evaluate(
                new List<PaceObservation> { new PaceObservation(300d, false), new PaceObservation(400d, false) },
                oldestAliveWaveIsStale: true);

            Assert.AreEqual(1.75, controller.HealthMult, Epsilon, "one step, not three");
        }

        /// <summary>
        /// A clear inside the window is not evidence in either direction. That band is most of the run.
        /// </summary>
        [TestMethod]
        public void Pace_AClearInsideTheWindowChangesNothing()
        {
            var controller = new WorldEventPaceController(Pace());

            controller.Evaluate(Cleared(90d), false);

            Assert.AreEqual(0, controller.QuantityBonus);
            Assert.AreEqual(1.0, controller.HealthMult, Epsilon);
            Assert.AreEqual(90d, controller.LastClearSeconds.Value, Epsilon, "it is still reported on the status line");
        }

        [TestMethod]
        public void Pace_MultipleFastClearsInOnePickEachCount()
        {
            var controller = new WorldEventPaceController(Pace());

            controller.Evaluate(
                new List<PaceObservation> { new PaceObservation(10d, false), new PaceObservation(12d, false) },
                false);

            Assert.AreEqual(4, controller.QuantityBonus, "the fast side is per completed wave, unlike the slow side");
        }

        [TestMethod]
        public void Pace_ANullTunablesBlockStillProducesAWorkingController()
        {
            var controller = new WorldEventPaceController(null);

            Assert.AreEqual(PaceDef.DefaultMinWaveSeconds, controller.Tunables.MinWaveSeconds, Epsilon);

            controller.Evaluate(Cleared(10d), false);

            Assert.AreEqual(PaceDef.DefaultQuantityStep, controller.QuantityBonus);
        }

        // ================================================================================================
        // the shipped axis file
        // ================================================================================================

        /// <summary>
        /// The same walk WorldEventAxisStoreTests uses. Null when the tree cannot be found, which only
        /// happens if the test assembly has been moved out of the repo.
        /// </summary>
        private static string FindAxesDir()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "Content", "events", "axes");

                if (System.IO.Directory.Exists(candidate) && System.IO.File.Exists(System.IO.Path.Combine(candidate, "sources.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        [TestMethod]
        public void ShippedThemes_AllCarryExplicitDifficultyValues()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            var store = WorldEventAxisStore.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "sources.json")),
                System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "families.json")),
                System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "goals.json")),
                System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "rewards.json")),
                System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "anchors.json")));

            // 4 base themes plus the five element_portal themes. The count is asserted so that a theme
            // added later cannot quietly ship without the difficulty block the loop below checks for.
            Assert.AreEqual(9, store.Sources.Count);

            foreach (var theme in store.Sources.Values)
            {
                Assert.AreEqual(4, theme.OverflowPerChampion, theme.Id);
                Assert.AreEqual(3, theme.OverflowChampionMaxAlive, theme.Id);

                Assert.AreEqual(2.0, theme.SyntheticEliteHealthMult, Epsilon, theme.Id);
                Assert.AreEqual(3.0, theme.SyntheticChampionHealthMult, Epsilon, theme.Id);

                Assert.AreEqual(8, theme.CrowdHealth.StartAt, theme.Id);
                Assert.AreEqual(0.1, theme.CrowdHealth.PerParticipant, Epsilon, theme.Id);
                Assert.AreEqual(3.0, theme.CrowdHealth.Cap, Epsilon, theme.Id);

                Assert.AreEqual(60.0, theme.Pace.MinWaveSeconds, Epsilon, theme.Id);
                Assert.AreEqual(120.0, theme.Pace.MaxWaveSeconds, Epsilon, theme.Id);
                Assert.AreEqual(2, theme.Pace.QuantityStep, theme.Id);
                Assert.AreEqual(0.25, theme.Pace.HealthStep, Epsilon, theme.Id);
                Assert.AreEqual(4.0, theme.Pace.HealthCap, Epsilon, theme.Id);
            }

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray(),
                "the shipped files must still validate clean with the new blocks");
        }

        /// <summary>
        /// A theme JSON with no difficulty keys at all - which is what every theme looked like before this
        /// change - must load with the documented defaults rather than with zeroes.
        /// </summary>
        [TestMethod]
        public void AThemeWithNoDifficultyKeys_LoadsWithTheDocumentedDefaults()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""bare"", ""displayName"": ""Bare"", ""geometry"": ""single"",
                ""geometryRadius"": 10.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 40.0,
                ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
                ""rewardRadius"": 60.0, ""compatibleGoals"": [] } ] }";

            var store = WorldEventAxisStore.Parse(sources, "{}", "{}", "{}", "{}");

            Assert.AreEqual(1, store.Sources.Count, string.Join("; ", store.Diagnostics));

            var theme = store.Sources["bare"];

            Assert.AreEqual(SourceThemeDef.DefaultOverflowPerChampion, theme.OverflowPerChampion);
            Assert.AreEqual(SourceThemeDef.DefaultOverflowChampionMaxAlive, theme.OverflowChampionMaxAlive);
            Assert.AreEqual(CrowdHealthDef.DefaultStartAt, theme.CrowdHealth.StartAt);
            Assert.AreEqual(PaceDef.DefaultMaxWaveSeconds, theme.Pace.MaxWaveSeconds, Epsilon);

            Assert.AreEqual(SourceThemeDef.DefaultSyntheticEliteHealthMult, theme.SyntheticEliteHealthMult, Epsilon);
            Assert.AreEqual(SourceThemeDef.DefaultSyntheticChampionHealthMult, theme.SyntheticChampionHealthMult, Epsilon);
        }

        /// <summary>
        /// WP-24: a syntheticEliteHealthMult/syntheticChampionHealthMult at or below 0 is repaired to the
        /// documented default with a diagnostic, exactly like every other difficulty dial - never dropping
        /// the theme.
        /// </summary>
        [TestMethod]
        public void ASyntheticHealthMultAtOrBelowZero_RepairsItselfWithDiagnostic()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""synthetic_bad"", ""displayName"": ""Synthetic Bad"", ""geometry"": ""single"",
                ""geometryRadius"": 10.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 40.0,
                ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
                ""rewardRadius"": 60.0, ""compatibleGoals"": [],
                ""syntheticEliteHealthMult"": 0,
                ""syntheticChampionHealthMult"": -3.0 } ] }";

            var store = WorldEventAxisStore.Parse(sources, "{}", "{}", "{}", "{}");

            Assert.AreEqual(1, store.Sources.Count, "the theme survives");

            var theme = store.Sources["synthetic_bad"];

            Assert.AreEqual(SourceThemeDef.DefaultSyntheticEliteHealthMult, theme.SyntheticEliteHealthMult, Epsilon);
            Assert.AreEqual(SourceThemeDef.DefaultSyntheticChampionHealthMult, theme.SyntheticChampionHealthMult, Epsilon);

            Assert.AreEqual(2, store.Diagnostics.Count, string.Join("; ", store.Diagnostics));
        }

        /// <summary>
        /// A mis-authored difficulty value repairs itself with a diagnostic. It must NEVER drop the theme -
        /// the worst a bad dial can do is turn itself off, which is not worth losing a whole source over.
        /// </summary>
        [TestMethod]
        public void ABadDifficultyValue_RepairsItselfAndKeepsTheTheme()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""bad"", ""displayName"": ""Bad"", ""geometry"": ""single"",
                ""geometryRadius"": 10.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 40.0,
                ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
                ""rewardRadius"": 60.0, ""compatibleGoals"": [],
                ""overflowPerChampion"": -1,
                ""crowdHealth"": { ""startAt"": 8, ""perParticipant"": 0.1, ""cap"": 0.5 },
                ""pace"": { ""minWaveSeconds"": 200, ""maxWaveSeconds"": 100, ""quantityStep"": 2,
                            ""healthStep"": 0.25, ""healthCap"": 4.0 } } ] }";

            var store = WorldEventAxisStore.Parse(sources, "{}", "{}", "{}", "{}");

            Assert.AreEqual(1, store.Sources.Count, "the theme survives");

            var theme = store.Sources["bad"];

            Assert.AreEqual(0, theme.OverflowPerChampion);
            Assert.AreEqual(1.0, theme.CrowdHealth.Cap, Epsilon);
            Assert.AreEqual(PaceDef.DefaultMinWaveSeconds, theme.Pace.MinWaveSeconds, Epsilon);
            Assert.AreEqual(PaceDef.DefaultMaxWaveSeconds, theme.Pace.MaxWaveSeconds, Epsilon);

            Assert.AreEqual(3, store.Diagnostics.Count, string.Join("; ", store.Diagnostics));
        }
    }
}
