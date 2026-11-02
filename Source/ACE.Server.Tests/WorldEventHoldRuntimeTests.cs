using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H6 (runtime) and H7 (claim policy): the pure cadence and line rules, the runtime tuning rows, the
    /// objective's run-shaping hooks, the shrine's participant gate and the claim policy a Hold run hands the claim pipeline.
    /// No WorldEvent, no landblock, no PropertyManager: settings come through the resolver seams, heals through the
    /// WorldEventHoldHeals.Enqueue seam (restored in a finally).
    /// </summary>
    [TestClass]
    public class WorldEventHoldRuntimeTests
    {
        private const double T0 = 1_000_000.0;

        private static Func<GoalDef, WorldEventHoldSettings> Resolver(IDictionary<string, string> props)
        {
            var full = (props ?? new Dictionary<string, string>()).ToDictionary(kv => K.PropertyPrefix + kv.Key, kv => kv.Value);

            return g => WorldEventHoldSettingsResolver.Resolve(g, p => full.TryGetValue(p, out var v) ? v : null);
        }

        private static ProtectDef Protect(ScaledCount waves = null) => new ProtectDef
        {
            Wcid = 2000001,
            Count = new ScaledCount { Base = 1, PerParticipant = 0, Cap = 3 },
            Health = new ScaledCount { Base = 20000, PerParticipant = 4000, Cap = 200000 },
            Radius = 6.0,
            MinSurvivors = 1,
            Noun = "envoy",
            NounPlural = "envoys",
            Waves = waves
        };

        private static GoalDef Goal(ScaledCount waves = null) => new GoalDef
        {
            Id = "hold_test",
            DisplayName = "Hold Test",
            Type = "Hold",
            TypeKind = GoalType.Hold,
            HoldSeconds = 600,
            MvpRule = MvpRule.MostKills.ToString(),
            RuleKind = MvpRule.MostKills,
            Protect = Protect(waves)
        };

        private static HoldObjective Objective(GoalDef goal, IDictionary<string, string> props, IReadOnlyCollection<uint> placed = null,
            IReadOnlyCollection<uint> live = null)
        {
            var run = new WorldEventHoldRun(7, Resolver(props));

            return new HoldObjective(goal, () => placed ?? Array.Empty<uint>(), () => live ?? Array.Empty<uint>(), () => T0, null,
                () => "the Drudges", null, null, run);
        }

        // ---- the cadence rules -----------------------------------------------------------------------------------------

        [TestMethod]
        public void HoldNextWaveDue_IntervalOrMostDead_NeverInsideTheBreather()
        {
            // Either rule: the interval elapsed, or no more than 30% of a wave of 10 (3) still alive.
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 4, T0, 45, 5, 0, 10, 0.3, false, 0), "inside the breather");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 5, T0, 45, 5, 3, 10, 0.3, false, 0), "most of the wave is dead");
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 20, T0, 45, 5, 4, 10, 0.3, false, 0), "too many still alive, interval not up");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 45, T0, 45, 5, 9, 10, 0.3, false, 0), "the interval elapsed");

            // Both rules: the interval AND most dead, with the max interval as the stall breaker.
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 20, T0, 45, 5, 0, 10, 0.3, true, 0), "dead but the interval is not up");
            Assert.IsFalse(WorldEventHoldRules.HoldNextWaveDue(T0 + 60, T0, 45, 5, 9, 10, 0.3, true, 0), "interval up but the field stands");
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 60, T0, 45, 5, 2, 10, 0.3, true, 0));
            Assert.IsTrue(WorldEventHoldRules.HoldNextWaveDue(T0 + 120, T0, 45, 5, 9, 10, 0.3, true, 120), "the max interval sends it anyway");
        }

        [TestMethod]
        public void ResolvedWaves_SpawnerOrGrace_AndThePendingEscortHoldsTheFinaleBack()
        {
            Assert.AreEqual(3, WorldEventHoldRules.ResolvedWaves(3, 3, T0, T0 - 1, 15, false));
            Assert.AreEqual(2, WorldEventHoldRules.ResolvedWaves(3, 2, T0, T0 - 1, 15, false), "the last wave is still being placed");
            Assert.AreEqual(3, WorldEventHoldRules.ResolvedWaves(3, 2, T0, T0 - 15, 15, false), "the grace resolves a lost batch");
            Assert.AreEqual(2, WorldEventHoldRules.ResolvedWaves(3, 3, T0, T0 - 1, 15, true), "the boss landed, the escort has not");
        }

        [TestMethod]
        public void FirstWave_WantsAnotherWave_FinalWaveBonus()
        {
            Assert.IsTrue(WorldEventHoldRules.FirstWaveMayStart(true, 0, 10));
            Assert.IsFalse(WorldEventHoldRules.FirstWaveMayStart(false, 9.9, 10));
            Assert.IsTrue(WorldEventHoldRules.FirstWaveMayStart(false, 10, 10), "the grace sends it without the knights");

            Assert.IsTrue(WorldEventHoldRules.WantsAnotherWave(4, 5));
            Assert.IsFalse(WorldEventHoldRules.WantsAnotherWave(5, 5), "the finale has started");
            Assert.IsFalse(WorldEventHoldRules.WantsAnotherWave(0, 0), "a time-only hold has no counted waves");

            Assert.AreEqual(2, WorldEventHoldRules.FinalWaveBonus(6, 0.25), "ceil(6 * 0.25)");
            Assert.AreEqual(1, WorldEventHoldRules.FinalWaveBonus(0, 0.25), "at least one");
            Assert.AreEqual(0, WorldEventHoldRules.FinalWaveBonus(6, 0), "0 is off");
            Assert.AreEqual(0, WorldEventHoldRules.FinalWaveBonus(6, double.NaN));
        }

        [TestMethod]
        public void RescaledHealth_KeepsThePercentage_NeverKillsALivingKnight()
        {
            Assert.AreEqual(500u, WorldEventHoldRules.RescaledHealth(250, 500, 1000), "50% stays 50%");
            Assert.IsTrue(WorldEventHoldRules.RescaledHealth(1, 100000, 10) >= 1, "a knight that was alive stays alive");
            Assert.IsTrue(WorldEventHoldRules.RescaledHealth(1000, 1000, 200) <= 200, "never above the new max");
        }

        [TestMethod]
        public void DueMilestones_EachTierOnce_InOrder()
        {
            var fired = new HashSet<int>();
            var tiers = new[] { 75, 50, 25 };

            CollectionAssert.AreEqual(new int[0], WorldEventHoldRules.DueMilestones(800, 1000, fired, tiers));
            CollectionAssert.AreEqual(new[] { 75 }, WorldEventHoldRules.DueMilestones(750, 1000, fired, tiers));
            CollectionAssert.AreEqual(new int[0], WorldEventHoldRules.DueMilestones(740, 1000, fired, tiers), "75 already fired");
            CollectionAssert.AreEqual(new[] { 50, 25 }, WorldEventHoldRules.DueMilestones(200, 1000, fired, tiers), "a big hit crosses two");
        }

        // ---- the lines -------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Lines_AreFilled_AndAscii()
        {
            Assert.AreEqual("A {monster} marches on {target}!".Replace("{monster}", "Corrupted Gorger").Replace("{target}", "Envoy Adarl"),
                WorldEventHoldRules.AssaultLine(null, "Corrupted Gorger", "Envoy Adarl"));
            Assert.AreEqual("Final wave", WorldEventHoldRules.PhaseLabel(5, 5, true));
            StringAssert.EndsWith(WorldEventHoldRules.PhaseLine("Wave 2 of 5", "They charge!"), "Wave 2 of 5: They charge!");
            StringAssert.EndsWith(WorldEventHoldRules.KnightHealthMilestoneLine("envoy", "Holtburg", 50), "An envoy at Holtburg is at 50% health!");
            StringAssert.EndsWith(WorldEventHoldRules.EnrageLine("Warlord Grub"), "Warlord Grub flies into a rage!");
            Assert.AreEqual("2 of 3 envoys survived, the Drudges routed.",
                WorldEventHoldRules.OutcomeTemplate("{alive} of {total} envoys survived, {family} routed.", true, 2, 3, "the Drudges"));
            Assert.IsNull(WorldEventHoldRules.OutcomeTemplate(null, false, 0, 3, null), "a failure with no template keeps the announcer's line");
            StringAssert.Contains(WorldEventHoldRules.OutcomeTemplate(null, true, 3, 3, null), "3 of 3");

            foreach (var line in new[] { WorldEventHoldRules.ShrineNotParticipantLine, WorldEventHoldRules.DefaultSuccessTemplate })
                Assert.IsTrue(line.All(c => c < 128), "ASCII only: " + line);
        }

        // ---- the runtime tuning rows ---------------------------------------------------------------------------------------

        [TestMethod]
        public void RuntimeTuning_RowsOverTheGoal_AndFallBackToIt()
        {
            var goal = Goal(new ScaledCount { Base = 4, PerParticipant = 0.5, Cap = 8 });

            var none = HoldFixtures.Tuning(new Dictionary<string, string>(), goal).Runtime;
            var waves = none.WaveCount(goal.Protect.Waves);
            Assert.AreEqual(4, waves.Base, "no row: protect.waves");
            Assert.AreEqual(8, waves.Cap);
            Assert.IsNull(none.WaveSize(null), "no row and no protect.waveSize: the theme's own sizing");

            var rows = HoldFixtures.Tuning(new Dictionary<string, string>
            {
                [K.WaveCountBase] = "6",
                [K.FinalWaveBonus] = "0.5",
                [K.FirstWaveDelaySeconds] = "12",
                [K.FailureReward] = "none",
                [K.MinPresenceSeconds] = "90",
                [K.MinContribution] = "0.05",
                [K.ParticipantsPerCache] = "2",
                [K.AdminsCount] = WorldEventSettingsRegistry.On,
            }, goal).Runtime;

            Assert.AreEqual(6, rows.WaveCount(goal.Protect.Waves).Base, "wave_count_base wins");
            Assert.AreEqual(0.5, rows.FinalWaveBonus, 1e-9);
            Assert.AreEqual(12.0, rows.FirstWaveDelaySeconds, 1e-9);
            Assert.AreEqual(FailureRewardMode.None, rows.FailureReward);
            Assert.AreEqual(90.0, rows.MinPresenceSeconds, 1e-9);
            Assert.AreEqual(0.05, rows.MinContribution, 1e-9);
            Assert.AreEqual(2, rows.ParticipantsPerCache);
            Assert.IsTrue(rows.AdminsCount);
        }

        [TestMethod]
        public void RuntimeTuning_WaveSize_OwnsEveryDial()
        {
            var size = WorldEventHoldRuntimeTuning.Defaults.WaveSize(new ScaledCount { Base = 5, PerParticipant = 1, Cap = 12 });

            Assert.AreEqual(5, size.Base);
            Assert.IsTrue(size.OwnsBase && size.OwnsPerParticipant && size.OwnsCap,
                "the global world_events_wave_count_* overrides must never replace a Hold goal's wave size");
        }

        // ---- the objective's hooks -------------------------------------------------------------------------------------

        [TestMethod]
        public void Objective_WaveBased_RunsItsOwnCadence_TimeOnly_KeepsTheCore()
        {
            var waves = Objective(Goal(new ScaledCount { Base = 5, PerParticipant = 0, Cap = 5 }), null);
            IWorldEventObjective w = waves;

            Assert.IsTrue(w.ReplacesWaveCadence);
            Assert.IsFalse(w.FirstWaveReady(T0 + 100), "the core never sends a plain wave 0 for a wave-based hold");
            Assert.AreEqual(5, waves.TargetWaves);
            Assert.IsTrue(waves.IsFinalWave(5));
            Assert.IsFalse(waves.IsFinalWave(4));

            IWorldEventObjective t = Objective(Goal(), null);
            Assert.IsFalse(t.ReplacesWaveCadence, "a time-only hold keeps the core cadence");
            Assert.IsTrue(t.FirstWaveReady(T0 + 1), "no run attached: no knights to wait for, the core's wave 0 goes as before");
        }

        [TestMethod]
        public void Objective_WaveTarget_ComesFromTheWaveCountRows()
        {
            var goal = Goal(new ScaledCount { Base = 4, PerParticipant = 0, Cap = 4 });
            var objective = Objective(goal, new Dictionary<string, string> { [K.WaveCountBase] = "7", ["wave_count_cap"] = "7" });

            Assert.AreEqual(7, objective.TargetWaves, "wave_count_base over protect.waves");
        }

        [TestMethod]
        public void Objective_RunShapingHooks()
        {
            var objective = Objective(Goal(), new Dictionary<string, string> { [K.AdminsCount] = WorldEventSettingsRegistry.On },
                placed: new uint[] { 1, 2, 3 }, live: new uint[] { 1, 3 });
            IWorldEventObjective o = objective;

            Assert.IsTrue(o.HoldsAdjacentLandblocks);
            Assert.IsTrue(o.FreezesProgressAtFinish);
            Assert.IsTrue(o.CountsStaffTowardAudience, "admins_count");
            Assert.AreEqual("2 of 3 lost 1, the Drudges", o.OutcomeTemplate("{alive} of {total} lost {lost}, {family}", false));
        }

        [TestMethod]
        public void Objective_ClaimPolicy_IsTheHoldRows()
        {
            var objective = Objective(Goal(), new Dictionary<string, string>
            {
                [K.FailureReward] = "partial",
                [K.MinPresenceSeconds] = "120",
                [K.MinContribution] = "0.1",
                [K.ParticipantsPerCache] = "3",
            });

            var policy = ((IWorldEventObjective)objective).ClaimPolicy;

            Assert.AreEqual(FailureRewardMode.Partial, policy.OnFailure);
            Assert.AreEqual(120.0, policy.Thresholds.MinPresenceSeconds, 1e-9);
            Assert.AreEqual(0.1, policy.Thresholds.MinContributionShare, 1e-9);
            Assert.AreEqual(3, policy.ParticipantsPerCache);
            Assert.AreEqual(3, policy.EffectiveParticipantsPerCache(8));
        }

        [TestMethod]
        public void ClaimPolicy_Defaults_AreTodaysClaim_AndTheAxisCacheRule()
        {
            var policy = HoldObjective.ClaimPolicyFrom(WorldEventHoldRuntimeTuning.Defaults);

            Assert.AreEqual(FailureRewardMode.Consolation, policy.OnFailure);
            Assert.IsFalse(policy.Thresholds.PresenceOn);
            Assert.IsFalse(policy.Thresholds.ContributionOn);
            Assert.IsNull(policy.ParticipantsPerCache);
            Assert.AreEqual(8, policy.EffectiveParticipantsPerCache(8), "no participants_per_cache: the reward axis's value");
            Assert.AreEqual(8, (WorldEventClaimPolicy.Default with { ParticipantsPerCache = 0 }).EffectiveParticipantsPerCache(8), "0 never wins");
        }

        // ---- under a threshold pays coal (owner ruling 2026-11-01) ----------------------------------------------------------

        private static readonly RewardDef Reward = new RewardDef
        {
            Id = "test",
            SuccessCrateWcid = 100,
            ConsolationCrateWcid = 200,
            CoalWcid = 300
        };

        private static ClaimPayout Claim(WorldEventClaimPolicy policy, WorldEventOutcome outcome, double seconds, double share)
        {
            var inputs = new ClaimInputs
            {
                PresenceEnabled = true,
                PresenceKnown = true,
                PresenceFraction = 0.8,
                PresentSecondsKnown = true,
                PresentSeconds = seconds,
                DamageShareKnown = true,
                DamageShare = share,
                PaysSuccess = WorldEvent.PaysSuccessCrate(outcome),
                CoalWcid = 300,
                Policy = policy
            };

            return WorldEventClaimRules.ResolvePayout(WorldEventClaimRules.ResolveEligibility(inputs), Reward, outcome, policy);
        }

        [TestMethod]
        public void BelowThreshold_PaysCoal_OnSuccessAndFailure()
        {
            var policy = HoldObjective.ClaimPolicyFrom(WorldEventHoldRuntimeTuning.Defaults with { MinPresenceSeconds = 60, MinContribution = 0.05 });

            foreach (var outcome in new[] { WorldEventOutcome.Success, WorldEventOutcome.FailedObjectiveLost })
            {
                var shortStay = Claim(policy, outcome, 30, 0.5);
                Assert.IsTrue(shortStay.PayCoal, $"{outcome}: under min_presence_seconds pays coal");
                Assert.AreEqual(ClaimCoalReason.BelowPresenceThreshold, shortStay.CoalReason);
                Assert.AreEqual(300u, shortStay.PayoutWcid);

                var idle = Claim(policy, outcome, 600, 0.01);
                Assert.IsTrue(idle.PayCoal, $"{outcome}: under min_contribution pays coal");
                Assert.AreEqual(ClaimCoalReason.BelowContributionThreshold, idle.CoalReason);

                var earned = Claim(policy, outcome, 600, 0.5);
                Assert.IsFalse(earned.PayCoal, $"{outcome}: over both thresholds is paid the outcome crate");
                Assert.AreEqual(WorldEvent.PaysSuccessCrate(outcome) ? 100u : 200u, earned.PayoutWcid);
            }
        }

        [TestMethod]
        public void FailureReward_None_RefusesAFailedHold()
        {
            var policy = HoldObjective.ClaimPolicyFrom(WorldEventHoldRuntimeTuning.Defaults with { FailureReward = FailureRewardMode.None });

            Assert.AreEqual(ClaimDenial.NoRewardForOutcome, Claim(policy, WorldEventOutcome.FailedObjectiveLost, 600, 0.5).Denial);
            Assert.AreEqual(ClaimDenial.None, Claim(policy, WorldEventOutcome.Success, 600, 0.5).Denial, "a success still pays");
        }

        // ---- the shrine is the defenders' own -----------------------------------------------------------------------------

        private static readonly Dictionary<string, string> ShrineOn = new Dictionary<string, string>
        {
            [K.Shrine] = WorldEventSettingsRegistry.On,
            [K.ShrineHeal] = "0.1",
            [K.ShrineCooldownSeconds] = "30",
            [K.ShrineBindRange] = "20",
            [K.HealBudgetPerWave] = "0",
        };

        [TestMethod]
        public void Shrine_RefusesANonParticipant_AndHealsForAParticipant()
        {
            var saved = WorldEventHoldHeals.Enqueue;
            var queued = new List<Action>();

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var run = new WorldEventHoldRun(42, Resolver(ShrineOn));
                run.Stage(null, 1, T0);
                run.SetWaveNumber(1);
                run.MarkActive();

                var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight, name: "Envoy Donovan");
                knight.Health.Current = knight.Health.MaxValue / 2;
                knight.Location = HoldFixtures.At(10, 10);
                run.PublishKnights(new[] { knight });

                var shrine = HoldFixtures.MakeCreature("Restoration Shrine");
                shrine.Location = HoldFixtures.At(12, 10);
                run.RegisterShrines(new[] { shrine });

                run.SetParticipantTest(guid => guid == 1);

                Assert.IsTrue(run.TryUseShrineCore(shrine, 2, "Stranger", null, T0, out var reply), "the shrine answers (refusing)");
                Assert.AreEqual(WorldEventHoldRules.ShrineNotParticipantLine, reply);
                Assert.AreEqual(0, queued.Count, "a non-participant heals nothing");

                Assert.IsTrue(run.TryUseShrineCore(shrine, 1, "Roz", null, T0, out reply));
                Assert.IsNull(reply);
                Assert.AreEqual(1, queued.Count, "a participant's use heals");

                Assert.IsFalse(run.IsParticipant(0), "guid 0 is nobody");

                run.SetParticipantTest(_ => throw new InvalidOperationException("ledger gone"));
                Assert.IsFalse(run.IsParticipant(1), "a throwing ledger refuses rather than heals for free");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void IsParticipant_WithoutATest_EveryoneCounts()
        {
            var run = new WorldEventHoldRun(1, Resolver(null));

            Assert.IsTrue(run.IsParticipant(5), "no ledger attached (a test run): the view's default");
            Assert.IsTrue(((IWorldEventHoldRunView)new FakeHoldRunView(3)).IsParticipant(5), "the interface default");
        }

        // ---- the audience ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Audience_StaffCountOnlyWhenAsked()
        {
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Admin, false), "the sampler's staff rule");
            Assert.IsTrue(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Admin, true), "admins_count");
            Assert.IsTrue(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Player, false));
        }

        [TestMethod]
        public void ShiftAudience_MovesEveryLevel_ZeroIsUnchanged()
        {
            var audience = new AudienceEstimate(3, 100, 150, new List<int> { 90, 100, 150 }, 1.0);

            var same = HoldObjective.ShiftAudience(audience, 0);
            Assert.AreEqual(100, same.MedianLevel);

            var up = HoldObjective.ShiftAudience(audience, 20);
            Assert.AreEqual(3, up.Count);
            Assert.AreEqual(120, up.MedianLevel);
            Assert.AreEqual(170, up.P90Level);
            CollectionAssert.AreEqual(new[] { 110, 120, 170 }, up.Levels.ToArray());

            Assert.AreEqual(1, HoldObjective.ShiftAudience(audience, -500).MedianLevel, "never below level 1");
        }
    }
}
