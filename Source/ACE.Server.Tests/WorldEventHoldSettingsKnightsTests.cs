using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The knights group of the Hold settings, and the general rules (unset vs 0, clamping, source, live) through it.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        private static GoalDef EnvoysGoal()
        {
            for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = System.IO.Path.Combine(dir.FullName, "dreamweave-content", "events", "defend_envoys.goal.json");

                if (System.IO.File.Exists(path))
                    return WorldEventAxisStore.Parse(null, null, System.IO.File.ReadAllText(path), null, null).Goals["defend_envoys"];
            }

            Assert.Inconclusive("defend_envoys.goal.json not found next to the repo");
            return null;
        }

        private static HoldSettingRow Row(string key, GoalDef goal)
        {
            Assert.IsTrue(HoldSettings.TryFind(key, out var s), key);
            return HoldSettings.Describe(s, goal);
        }

        // ---- the general rules, on knight settings -------------------------------------------------------------

        [TestMethod]
        public void Resolve_PropertyThenGoalsJsonThenDefault_AndTheSourceSaysWhich()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            Assert.AreEqual(200, goal.Protect.UndefendedSeconds, "not set: goals.json");
            Assert.AreEqual(HoldSettingSource.GoalsJson, Row(HoldKeys.UndefendedSeconds, goal).Source);

            Assert.AreEqual(ProtectDef.TimeScaledMaxHealth, goal.Protect.KnightMaxHealth, "not set, not in goals.json: the code default");
            Assert.AreEqual(HoldSettingSource.Default, Row(HoldKeys.KnightMaxHealth, goal).Source);

            // minSurvivors is 1 in goals.json, the same as the code default: still reported as goals.json.
            Assert.AreEqual(HoldSettingSource.GoalsJson, Row(HoldKeys.MinSurvivors, goal).Source);

            scope.Set(HoldKeys.UndefendedSeconds, "300");
            Assert.AreEqual(300, goal.Protect.UndefendedSeconds, "the property wins");
            var row = Row(HoldKeys.UndefendedSeconds, goal);
            Assert.AreEqual(HoldSettingSource.Property, row.Source);
            Assert.IsTrue(row.Overridden);
            Assert.AreEqual("300", row.Value);

            scope.Set(HoldKeys.UndefendedSeconds, "");
            Assert.AreEqual(200, goal.Protect.UndefendedSeconds, "cleared: back to goals.json");
        }

        [TestMethod]
        public void Unset_IsNotZero_ZeroIsARealValue()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            Assert.AreEqual(60, goal.Protect.MinDeathSeconds);

            scope.Set(HoldKeys.MinDeathSeconds, "0");
            Assert.AreEqual(0, goal.Protect.MinDeathSeconds, "0 is set on purpose: no floor");
            Assert.IsTrue(HoldSettings.IsSet(HoldKeys.MinDeathSeconds));

            scope.Set(HoldKeys.KnightDamageTakenMult, "0");
            Assert.AreEqual(0, goal.Protect.KnightDamageTakenMult, "a 0 multiplier is allowed");

            scope.Set(HoldKeys.MinDeathSeconds, "  ");
            Assert.AreEqual(60, goal.Protect.MinDeathSeconds, "blank is not set");
        }

        [TestMethod]
        public void Clamp_OutOfRangeIsClampedAndWarned_GarbageIsIgnoredAndWarned()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.UndefendedSeconds, out var s));

            scope.Set(HoldKeys.UndefendedSeconds, "5");
            Assert.AreEqual(20, goal.Protect.UndefendedSeconds, "clamped to the minimum");
            Assert.IsTrue(Row(HoldKeys.UndefendedSeconds, goal).Clamped);
            StringAssert.Contains(HoldSettings.Check(s, "5"), "out of range");

            scope.Set(HoldKeys.UndefendedSeconds, "99999");
            Assert.AreEqual(3600, goal.Protect.UndefendedSeconds, "clamped to the maximum");

            scope.Set(HoldKeys.UndefendedSeconds, "abc");
            Assert.AreEqual(200, goal.Protect.UndefendedSeconds, "not a number: ignored, goals.json stands");
            Assert.IsFalse(HoldSettings.IsSet(HoldKeys.UndefendedSeconds));
            StringAssert.Contains(HoldSettings.Check(s, "abc"), "not a number");
            StringAssert.Contains(Row(HoldKeys.UndefendedSeconds, goal).Warning, "not a number");

            Assert.IsNull(HoldSettings.Check(s, "250"), "in range: no warning");
            Assert.IsNull(HoldSettings.Check(s, ""), "not set: no warning");

            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.KnightCount, out var count));
            StringAssert.Contains(HoldSettings.Check(count, "2.5"), "whole number");
        }

        [TestMethod]
        public void Live_AChangeIsSeenByTheSameDefWithoutAReload()
        {
            // A run keeps a reference to its GoalDef; the next read sees the new value.
            var goal = EnvoysGoal();
            var protect = goal.Protect;

            using var scope = new HoldSettingsScope();

            var before = protect.EffectiveUndefendedSeconds(1);
            scope.Set(HoldKeys.UndefendedSeconds, "400");
            Assert.AreEqual(400, protect.EffectiveUndefendedSeconds(1));
            Assert.AreNotEqual(before, protect.EffectiveUndefendedSeconds(1));

            scope.Set(HoldKeys.UndefendedSecondsFloor, "350");
            Assert.AreEqual(350, protect.EffectiveUndefendedSeconds(10), "the floor too");
        }

        // ---- knights ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void FailCondition_AllAndAny()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            Assert.IsTrue(HoldSettings.TryFind("fail_condition", out _) == false, "no parallel field: the fail condition is min_survivors");
            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.MinSurvivors, out var s));

            Assert.AreEqual("3", HoldSettings.Normalize(s, "any"));
            Assert.AreEqual("1", HoldSettings.Normalize(s, "ALL"));

            scope.Set(HoldKeys.MinSurvivors, "any");
            Assert.AreEqual(3, goal.Protect.MinSurvivors, "the word works stored as is, too");
            Assert.AreEqual(2, HoldObjective.RequiredFor(goal.Protect.MinSurvivors, 2), "clamped to the knights placed");

            scope.Set(HoldKeys.MinSurvivors, "all");
            Assert.AreEqual(1, goal.Protect.MinSurvivors);
        }

        [TestMethod]
        public void KnightCount_OverridesTheGoalsCount()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            Assert.AreEqual(3, goal.Protect.ResolveLayout(1).Count);

            scope.Set(HoldKeys.KnightCount, "2");
            Assert.AreEqual(2, goal.Protect.ResolveCount(1));
            Assert.AreEqual(2, goal.Protect.ResolveLayout(1).Count);

            scope.Set(HoldKeys.KnightCount, "9");
            Assert.AreEqual(ProtectDef.MaxProtectedTargets, goal.Protect.ResolveCount(1), "clamped to the knight models there are");
        }

        [TestMethod]
        public void KnightHealth_GlobalAndPerKnight()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            Assert.AreEqual(10000, goal.Protect.EffectiveMaxHealthFor(2000005, 1));

            scope.Set(HoldKeys.KnightMaxHealth, "15000");
            Assert.AreEqual(15000, goal.Protect.EffectiveMaxHealthFor(2000005, 1));

            scope.Set(HoldKeys.KnightMaxHealthByWcid, "2000005=12000, 2000007=500000, junk");
            Assert.AreEqual(12000, goal.Protect.EffectiveMaxHealthFor(2000005, 1), "named knight");
            Assert.AreEqual(15000, goal.Protect.EffectiveMaxHealthFor(2000006, 1), "not listed: the global value");
            Assert.AreEqual(HoldSettings.KnightHealthMax, goal.Protect.EffectiveMaxHealthFor(2000007, 1), "clamped");

            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.KnightMaxHealthByWcid, out var s));
            var warning = HoldSettings.Check(s, "2000005=12000, 2000007=500000, junk");
            StringAssert.Contains(warning, "out of range");
            StringAssert.Contains(warning, "junk");
        }

        [TestMethod]
        public void KnightHealth_RescaleKeepsThePercentage()
        {
            Assert.AreEqual(7500u, WorldEventHoldRules.RescaledHealth(5000, 10000, 15000));
            Assert.AreEqual(1u, WorldEventHoldRules.RescaledHealth(1, 10000, 1000), "a living knight never reaches 0");
            Assert.AreEqual(1000u, WorldEventHoldRules.RescaledHealth(10000, 10000, 1000));
            Assert.AreEqual(0u, WorldEventHoldRules.RescaledHealth(0, 10000, 15000));
        }

        [TestMethod]
        public void DefendPose_ToggleAndSwingInterval()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            Assert.IsNotNull(goal.Protect.DefendPose);

            scope.Set(HoldKeys.DefendPose, "off");
            Assert.IsNull(goal.Protect.DefendPose, "off hides the pose live");

            scope.Set(HoldKeys.DefendPose, "on");
            Assert.IsNotNull(goal.Protect.DefendPose);

            goal.Protect.DefendPose = null;
            Assert.IsNotNull(goal.Protect.DefendPose, "on with no goals.json block: the defaults");

            scope.Set(HoldKeys.DefendPose, "");
            Assert.IsNull(goal.Protect.DefendPose, "not set: goals.json (none now)");

            goal.Protect.DefendPose = new DefendPoseDef();
            scope.Set(HoldKeys.DefendSwingSeconds, "5");
            Assert.AreEqual(5, goal.Protect.DefendPose.SwingSeconds);
            Assert.AreEqual(5, goal.Protect.DefendPose.SwingMaxSeconds, "the max never falls below the min");
            Assert.IsNull(WorldEventHoldDefense.Validate(goal.Protect.DefendPose), "the file's own values are what the loader checks");
        }

        [TestMethod]
        public void HealthMilestones_Toggle()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            Assert.IsTrue(goal.Protect.HealthMilestones);

            scope.Set(HoldKeys.HealthMilestones, "off");
            Assert.IsFalse(goal.Protect.HealthMilestones);
        }

        [TestMethod]
        public void Knights_WithNothingSet_ReadExactlyTheGoalsJson()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupKnights).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);

            Assert.AreEqual(1.0, goal.Protect.KnightDamageTakenMult);
            Assert.IsNull(HoldSettings.KnightHealthByWcid());
        }
    }

    public partial class WorldEventHoldTests
    {
        [TestMethod]
        public void KnightDamageTakenMult_ScalesEveryHit_AndTheCapStillHolds()
        {
            var (evt, protect) = BuildTimeScaledRun();
            protect.MinDeathSeconds = 0;

            var attacker = BuildHoldCreature();
            attacker.P_WorldEvent = evt;

            var plain = BuildFreshTarget(evt).ScaleWorldEventProtectedHit(attacker, 30f, T0);

            using var scope = new HoldSettingsScope();
            scope.Set(HoldKeys.KnightDamageTakenMult, "2");

            var doubled = BuildFreshTarget(evt).ScaleWorldEventProtectedHit(attacker, 30f, T0);
            Assert.AreEqual(plain * 2, doubled, 1.0, "x2 on the same hit");

            scope.Set(HoldKeys.KnightDamageTakenMult, "0");
            Assert.AreEqual(0f, BuildFreshTarget(evt).ScaleWorldEventProtectedHit(attacker, 30f, T0), "x0: knights take nothing");

            // The floor still holds with the multiplier up.
            scope.Set(HoldKeys.KnightDamageTakenMult, "5");
            protect.MinDeathSeconds = 60;
            var down = SecondsUntilDown(BuildFreshTarget(evt), 6, 2.0, protect, out _);
            Assert.IsTrue(down >= 58, $"x5 damage still needs the 60 s floor: {down} s");
        }
    }
}
