using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WorldEventHoldSettingsResolver over an in-memory property lookup (PropertyManager reads throw in a unit test): a set value
    /// wins clamped, a value that is not set falls to the goal def where the row has a goals.json path, else to the code default,
    /// else to null (no preference).
    /// </summary>
    [TestClass]
    public class WorldEventHoldSettingsResolverTests
    {
        private readonly Dictionary<string, string> props = new Dictionary<string, string>();

        private WorldEventHoldSettings Resolve(GoalDef goal) =>
            WorldEventHoldSettingsResolver.Resolve(goal, p => props.TryGetValue(p, out var v) ? v : null);

        private void Set(string key, string value) => props[K.PropertyPrefix + key] = value;

        private static GoalDef Goal(Action<ProtectDef> edit = null)
        {
            var p = new ProtectDef { Count = new ScaledCount { Base = 2 }, ShrineHeal = 0.3, ShrineCooldownSeconds = 30 };
            edit?.Invoke(p);
            return new GoalDef { HoldSeconds = 480, Protect = p };
        }

        // ---- NegativeIsUnset rows with a goals.json path ---------------------------------------------------------------

        [TestMethod]
        public void NegativeRow_SetZero_IsHonoured()
        {
            Set(K.ShrineHeal, "0");
            var s = Resolve(Goal());

            Assert.AreEqual(0.0, s.Number(K.ShrineHeal));
            Assert.AreEqual(SettingSource.Property, s.SourceOf(K.ShrineHeal));
        }

        [TestMethod]
        public void NegativeRow_NotSet_TakesTheGoalValue()
        {
            foreach (var stored in new[] { null, "-1", "-0.5" })
            {
                props.Clear();

                if (stored != null)
                    Set(K.ShrineHeal, stored);

                var s = Resolve(Goal());
                Assert.AreEqual(0.3, s.Number(K.ShrineHeal), $"stored {stored ?? "(none)"}");
                Assert.AreEqual(SettingSource.Json, s.SourceOf(K.ShrineHeal), $"stored {stored ?? "(none)"}");
            }
        }

        [TestMethod]
        public void NegativeRow_SetAboveMax_Clamps()
        {
            Set(K.ShrineHeal, "2.5");
            var s = Resolve(Goal());

            Assert.AreEqual(1.0, s.Number(K.ShrineHeal));
            Assert.IsTrue(s[K.ShrineHeal].Clamped);
        }

        [TestMethod]
        public void GoalValue_IsNotReclampedToTheSettingRange()
        {
            // shrine_cooldown_seconds allows 0..600 as a setting; a goal value is used exactly as the def holds it.
            var s = Resolve(Goal(p => p.ShrineCooldownSeconds = 900));
            Assert.AreEqual(900.0, s.Number(K.ShrineCooldownSeconds));

            Set(K.ShrineCooldownSeconds, "0");
            Assert.AreEqual(0.0, Resolve(Goal()).Number(K.ShrineCooldownSeconds), "the setting may be 0 though goals.json may not");
        }

        [TestMethod]
        public void NegativeRow_NoGoal_IsNull()
        {
            var s = Resolve(null);
            Assert.IsNull(s.Number(K.ShrineHeal), "-1 is not a code default: with no goal there is no value");
            Assert.AreEqual(SettingSource.Default, s.SourceOf(K.ShrineHeal));
        }

        // ---- NegativeIsUnset rows with no goals.json path: "no preference" -------------------------------------------

        [TestMethod]
        public void NoPreferenceRows_NotSet_AreNull_SetZeroOrValueIsHonoured()
        {
            var s = Resolve(Goal());

            foreach (var key in new[] { K.CasterShareOfRanged, K.AbandonSeconds, K.Role("melee", K.RoleHealthMult), K.Role("corrupted", K.RoleAttackSpeedMult) })
                Assert.IsNull(s.Number(key), key);

            Set(K.CasterShareOfRanged, "0");
            Set(K.AbandonSeconds, "0");
            Set(K.Role("caster", K.RoleAttackSpeedMult), "0");

            s = Resolve(Goal());
            Assert.AreEqual(0.0, s.Number(K.CasterShareOfRanged), "0 = all archers, a real value");
            Assert.AreEqual(0L, s.Whole(K.AbandonSeconds), "0 = never abandon, a real value");
            Assert.AreEqual(1.0, s.Number(K.Role("caster", K.RoleAttackSpeedMult)), "0 is set, and below the minimum 1, so it clamps");
        }

        // ---- NonPositiveIsUnset rows (range starts above 0) -------------------------------------------------------------

        [TestMethod]
        public void PositiveRow_ZeroFallsToGoal_AboveMaxClamps()
        {
            Set(K.KnightCount, "0");
            Assert.AreEqual(2L, Resolve(Goal()).Whole(K.KnightCount));

            Set(K.KnightCount, "7");
            var s = Resolve(Goal());
            Assert.AreEqual(3L, s.Whole(K.KnightCount));
            Assert.IsTrue(s[K.KnightCount].Clamped);

            Set(K.KnightCount, "2");
            Assert.AreEqual(SettingSource.Property, Resolve(Goal()).SourceOf(K.KnightCount));
        }

        [TestMethod]
        public void PositiveRow_NoJsonPath_NotSetIsNull()
        {
            Assert.IsNull(Resolve(Goal()).Number(K.FinaleHealth));

            Set(K.FinaleHealth, "500");
            Assert.AreEqual(1000.0, Resolve(Goal()).Number(K.FinaleHealth), "set below its 1000 minimum: clamped up");
        }

        // ---- AlwaysSet rows -------------------------------------------------------------------------------------------

        [TestMethod]
        public void AlwaysSetRow_DefaultsToTheCodeValue_AndClampsBothWays()
        {
            var s = Resolve(null);
            Assert.AreEqual(10000L, s.Whole(K.KnightMaxHealth));
            Assert.AreEqual(1.0, s.Number(K.KnightDamageTakenMult));

            Set(K.KnightDamageTakenMult, "9");
            Set(K.FinaleLevelOffset, "-80");
            Set(K.Role("ranged", K.RoleLevelOffset), "-3");
            s = Resolve(null);

            Assert.AreEqual(5.0, s.Number(K.KnightDamageTakenMult));
            Assert.AreEqual(-50L, s.Whole(K.FinaleLevelOffset), "a negative value is real here and clamps to -50");
            Assert.AreEqual(-3L, s.Whole(K.Role("ranged", K.RoleLevelOffset)));
        }

        [TestMethod]
        public void Enabled_DefaultsOff_AndFollowsTheProperty()
        {
            Assert.IsFalse(Resolve(Goal()).Enabled);

            Set(K.Enabled, "True");
            Assert.IsTrue(Resolve(Goal()).Enabled);
        }

        // ---- on/off and other Choice rows -----------------------------------------------------------------------------

        [TestMethod]
        public void OnOffChoice_NotSet_FollowsTheGoal_SetWins_JunkIsIgnored()
        {
            Assert.AreEqual(false, Resolve(Goal()).OnOff(K.Shrine), "no shrine block in goals.json: off");
            Assert.AreEqual(true, Resolve(Goal(p => p.Shrine = new ShrineDef())).OnOff(K.Shrine));
            Assert.AreEqual(true, Resolve(Goal()).OnOff(K.HealthMilestones), "ProtectDef default");
            Assert.AreEqual(false, Resolve(Goal()).OnOff(K.DefendPose));

            Set(K.Shrine, "off");
            Assert.AreEqual(false, Resolve(Goal(p => p.Shrine = new ShrineDef())).OnOff(K.Shrine));
            Assert.AreEqual(SettingSource.Property, Resolve(Goal()).SourceOf(K.Shrine));

            Set(K.Shrine, "maybe");
            Assert.AreEqual(true, Resolve(Goal(p => p.Shrine = new ShrineDef())).OnOff(K.Shrine), "a value that is not a choice is not set");

            props.Clear();
            Assert.AreEqual(true, Resolve(null).OnOff(K.Shrine), "no goal: the code default (on)");
        }

        [TestMethod]
        public void FinaleTarget_GoalBoolMapsToNpcs_OrNoPreference()
        {
            Assert.IsNull(Resolve(Goal()).Text(K.FinaleTarget), "bossTargetsProtected false: the boss's own seeking, no choice");
            Assert.AreEqual("npcs", Resolve(Goal(p => p.BossTargetsProtected = true)).Text(K.FinaleTarget));

            Set(K.FinaleTarget, "Players");
            Assert.AreEqual("players", Resolve(Goal(p => p.BossTargetsProtected = true)).Text(K.FinaleTarget));
        }

        [TestMethod]
        public void TextAndPlainChoice_NotSet_UseTheCodeDefault()
        {
            var s = Resolve(Goal());
            Assert.AreEqual("75,50,25", s.Text(K.MilestonePercents));
            Assert.AreEqual("consolation", s.Text(K.FailureReward));
            Assert.AreEqual("none", s.Text(K.CorruptedTint), "CorruptedDef default tint, from the goal");
            Assert.IsNull(s.Text(K.KnightMaxHealthByWcid));

            Set(K.KnightMaxHealthByWcid, "1007650=12000,1007652=8000");
            Assert.AreEqual("1007650=12000,1007652=8000", Resolve(Goal()).Text(K.KnightMaxHealthByWcid));
        }

        // ---- every goal reader reads its own field ----------------------------------------------------------------------

        /// <summary>A goal with a distinct value in every field a GoalValues reader looks at, and the value each key must resolve to.</summary>
        private static (GoalDef Goal, Dictionary<string, string> Expected) DistinctGoal()
        {
            var p = new ProtectDef
            {
                Count = new ScaledCount { Base = 11 },
                UndefendedSeconds = 101.5, UndefendedSecondsPerParticipant = 102.5, UndefendedSecondsFloor = 103.5, MinDeathSeconds = 104.5, MinSurvivors = 12,
                DefendPose = new DefendPoseDef { SwingSeconds = 1.11, SwingMaxSeconds = 1.12 }, HealthMilestones = false,
                Shrine = new ShrineDef { UseRadius = 1.13 }, ShrineHeal = 0.201, ShrineCooldownSeconds = 105.5,
                CorruptedKillHeal = 0.202, RescueHeal = 0.203, RescueCapPerWave = 0.204, ClearRegenPerSecond = 0.0205, ClearRadius = 20.6,
                MaxHealPerTenSeconds = 0.207, HealReductionAfterHit = 0.208, HealReductionSeconds = 20.9, HealBetweenWaves = 0.211, HealBudgetPerWave = 0.212,
                SeekShare = 0.301,
                WaveMix = new WaveMixDef { Breach = 0.302, Runners = 0.303, Ranged = 0.304, Hunters = 0.305, Seekers = 0.306, Corrupted = 0.307, MinBreach = 13, MinRanged = 14, MinCorrupted = 15, MaxCorrupted = 16 },
                SpecialWaves = new List<SpecialWaveDef>
                {
                    new SpecialWaveDef { TypeKind = HoldWaveType.Runners, HealthMult = 0.401, SpeedMult = 1.402, AttackSpeedMult = 1.403 },
                    new SpecialWaveDef { TypeKind = HoldWaveType.Ranged, HoldDistance = 30.4 }
                },
                Corrupted = new CorruptedDef { HealthMult = 1.501, SelfMult = 1.502, EmpowerMult = 1.503, AssaultEveryWaves = 17, AssaultFromWave = 18, AssaultCount = 19, TintPalette = "0x0400137D" },
                Waves = new ScaledCount { Base = 21, PerParticipant = 0.601, Cap = 22 },
                WaveSize = new ScaledCount { Base = 23, PerParticipant = 0.602, Cap = 24 },
                WaveIntervalSeconds = 60.3, MinWaveGapSeconds = 6.04, MaxWaveIntervalSeconds = 60.5, NextWaveAliveShare = 0.606, SpawnRadius = 60.7,
                Portals = new PortalsDef { Count = 25, Radius = 60.8 },
                FinaleBossHealthMult = 7.01, FinaleBossScale = 1.702, BossMinKillSeconds = 70.3, FinaleBossLeadSeconds = 7.04, BossTargetsProtected = true,
                BossSwapSeconds = 70.5, BossSwapMaxSeconds = 70.6, BossPullShare = 0.0707, BossPullWindowSeconds = 7.08, BossPullSeconds = 7.09
            };

            var expected = new Dictionary<string, string>
            {
                [K.KnightCount] = "11", [K.UndefendedSeconds] = "101.5", [K.UndefendedSecondsPerParticipant] = "102.5", [K.UndefendedSecondsFloor] = "103.5",
                [K.MinDeathSeconds] = "104.5", [K.MinSurvivors] = "12", [K.DefendPose] = "on", [K.DefendSwingSeconds] = "1.11", [K.DefendSwingMaxSeconds] = "1.12",
                [K.HealthMilestones] = "off", [K.Shrine] = "on", [K.ShrineHeal] = "0.201", [K.ShrineCooldownSeconds] = "105.5", [K.ShrineUseRadius] = "1.13",
                [K.CorruptedKillHeal] = "0.202", [K.RescueHeal] = "0.203", [K.RescueCapPerWave] = "0.204", [K.RegenPerSecond] = "0.0205", [K.RegenClearRadius] = "20.6",
                [K.PlayerHealCap] = "0.207", [K.HealReductionAfterHit] = "0.208", [K.HealReductionSeconds] = "20.9", [K.HealBetweenWaves] = "0.211", [K.HealBudgetPerWave] = "0.212",
                [K.SeekShare] = "0.301", [K.ShareBreach] = "0.302", [K.ShareRunners] = "0.303", [K.ShareRanged] = "0.304", [K.ShareHunters] = "0.305",
                [K.ShareSeekers] = "0.306", [K.ShareCorrupted] = "0.307", [K.MinBreach] = "13", [K.MinRanged] = "14", [K.MinCorrupted] = "15", [K.MaxCorrupted] = "16",
                [K.RunnerHealthMult] = "0.401", [K.RunnerSpeedMult] = "1.402", [K.RunnerAttackSpeedMult] = "1.403", [K.RangedKeepDistance] = "30.4",
                [K.CorruptedHealthMult] = "1.501", [K.CorruptedSelfMult] = "1.502", [K.CorruptedEmpowerMult] = "1.503", [K.CorruptedAssaultEveryWaves] = "17",
                [K.CorruptedAssaultFromWave] = "18", [K.CorruptedAssaultCount] = "19", [K.CorruptedTint] = "0x0400137D",
                [K.WaveCountBase] = "21", [K.WaveCountPerPlayer] = "0.601", [K.WaveCountCap] = "22", [K.WaveSizeBase] = "23", [K.WaveSizePerPlayer] = "0.602", [K.WaveSizeCap] = "24",
                [K.WaveIntervalSeconds] = "60.3", [K.BreatherSeconds] = "6.04", [K.MaxWaveIntervalSeconds] = "60.5", [K.NextWaveAliveShare] = "0.606", [K.SpawnRadius] = "60.7",
                [K.PortalCount] = "25", [K.PortalRadius] = "60.8", [K.HoldSeconds] = "26",
                [K.FinaleHealthMult] = "7.01", [K.FinaleScale] = "1.702", [K.FinaleMinKillSeconds] = "70.3", [K.FinaleLeadSeconds] = "7.04", [K.FinaleTarget] = "npcs",
                [K.FinaleSwapSeconds] = "70.5", [K.FinaleSwapMaxSeconds] = "70.6", [K.FinalePullShare] = "0.0707", [K.FinalePullWindowSeconds] = "7.08", [K.FinalePullSeconds] = "7.09"
            };

            return (new GoalDef { HoldSeconds = 26, Protect = p }, expected);
        }

        [TestMethod]
        public void EveryGoalReader_ResolvesItsOwnField()
        {
            var (goal, expected) = DistinctGoal();

            CollectionAssert.AreEquivalent(new List<string>(WorldEventHoldSettingsResolver.GoalValues.Keys), new List<string>(expected.Keys),
                "the expected table must name every goal reader");

            var s = Resolve(goal);
            var problems = new List<string>();

            foreach (var (key, want) in expected)
            {
                var r = s[key];
                var got = r.Number is double d ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : r.Text;

                if (got != want || r.Source != SettingSource.Json)
                    problems.Add($"{key}: expected {want} from json, got {got ?? "null"} from {r.Source}");
            }

            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        // ---- a block forced on with no goals.json block: the contributor's synthesized defaults -------------------------

        [TestMethod]
        public void ForcedOnWithoutABlock_UsesDefaultDefNumbers()
        {
            Set(K.Shrine, "on");
            Set(K.DefendPose, "on");
            var s = Resolve(Goal());

            Assert.AreEqual(true, s.OnOff(K.Shrine));
            Assert.AreEqual(4.0, s.Number(K.ShrineUseRadius), "a default ShrineDef's use radius");
            Assert.AreEqual(2.0, s.Number(K.DefendSwingSeconds), "a default DefendPoseDef's swing");
            Assert.AreEqual(3.0, s.Number(K.DefendSwingMaxSeconds));

            Assert.IsNull(Resolve(new GoalDef()).Number(K.ShrineUseRadius), "no protect block at all: nothing to synthesize from");
        }

        // ---- the single-row entry point --------------------------------------------------------------------------------

        [TestMethod]
        public void ResolveRow_AgreesWithResolve()
        {
            var (goal, _) = DistinctGoal();
            Set(K.ShrineHeal, "0");
            Set(K.KnightCount, "9");
            Set(K.FinaleTarget, "both");

            Func<string, string> lookup = p => props.TryGetValue(p, out var v) ? v : null;
            var all = WorldEventHoldSettingsResolver.Resolve(goal, lookup);

            foreach (var key in new[] { K.ShrineHeal, K.KnightCount, K.FinaleTarget, K.CasterShareOfRanged, K.Enabled, K.UndefendedSecondsFloor, K.MilestonePercents })
            {
                var one = WorldEventHoldSettingsResolver.ResolveRow(goal, key, lookup);
                var whole = all[key];

                Assert.AreEqual(whole.Source, one.Source, key);
                Assert.AreEqual(whole.Number, one.Number, key);
                Assert.AreEqual(whole.Flag, one.Flag, key);
                Assert.AreEqual(whole.Text, one.Text, key);
                Assert.AreEqual(whole.Clamped, one.Clamped, key);
            }

            Assert.ThrowsExactly<ArgumentException>(() => WorldEventHoldSettingsResolver.ResolveRow(goal, "max_alive", lookup), "a global key is not a Hold setting");
            Assert.ThrowsExactly<ArgumentException>(() => WorldEventHoldSettingsResolver.ResolveRow(goal, "world_events_hold_shrine_heal", lookup), "the short key only");
        }
        // ---- the snapshot ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Snapshot_HoldsOnlyHoldRows()
        {
            var s = Resolve(Goal());

            Assert.ThrowsExactly<ArgumentException>(() => s.Number("max_alive"));
            Assert.ThrowsExactly<ArgumentException>(() => s.Number("no_such_key"));
            Assert.AreEqual(480L, s.Whole(K.HoldSeconds));
        }
    }
}
