using System;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Boss-phase trash cut (world_events_boss_phase_trash_multiplier): once the boss stands, wave sizing
    /// is scaled so players can focus fire on it. Pure statics only - SizeWave / PickWave /
    /// ResolveBossPhaseTrashMultiplier; the WorldEvent wiring (BossAlive, the pace hold) needs a live
    /// Spawner and is not reachable from a pure test.
    /// </summary>
    [TestClass]
    public class WorldEventBossPhaseTrashTests
    {
        /// <summary>The shipped "ambush" numbers: maxAlive 24, base 4 + 1.5/participant, cap 18.</summary>
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

        private static WaveSizing Size(SourceThemeDef theme, int participants, int alive, int bonus, int champions, double scale)
        {
            return WorldEventRosterSelector.SizeWave(theme, participants, alive, bonus, champions, scale);
        }

        // ---- scale 1.0 is today's arithmetic -------------------------------------------------------------

        [TestMethod]
        public void SizeWave_ScaleOne_IsIdenticalToTheFiveArgumentOverloadAcrossAGrid()
        {
            foreach (var participants in new[] { 0, 1, 4, 12, 40, 200 })
            foreach (var alive in new[] { 0, 5, 23, 24, 60 })
            foreach (var bonus in new[] { 0, 3, 20 })
            foreach (var champions in new[] { 0, 2, 9 })
            {
                var theme = Theme();

                var today = WorldEventRosterSelector.SizeWave(theme, participants, alive, bonus, champions);
                var scaled = Size(theme, participants, alive, bonus, champions, 1.0);

                Assert.AreEqual(today.ToString(), scaled.ToString(),
                    $"participants={participants} alive={alive} bonus={bonus} champions={champions}");
            }
        }

        [TestMethod]
        public void SizeWave_NonFiniteScale_IsUnscaled()
        {
            var today = WorldEventRosterSelector.SizeWave(Theme(), 40, 0, 0, 0);

            Assert.AreEqual(today.ToString(), Size(Theme(), 40, 0, 0, 0, double.NaN).ToString());
            Assert.AreEqual(today.ToString(), Size(Theme(), 40, 0, 0, 0, double.PositiveInfinity).ToString());
        }

        // ---- 0.33 scales demand and room -----------------------------------------------------------------

        [TestMethod]
        public void SizeWave_Third_ScalesDemand()
        {
            // 4 participants: raw 10 -> round(3.3) = 3; scaled maxAlive round(7.92) = 8 is not binding.
            var sizing = Size(Theme(), participants: 4, alive: 0, bonus: 0, champions: 0, scale: 0.33);

            Assert.AreEqual(3, sizing.Raw);
            Assert.AreEqual(8, sizing.Ceiling);
            Assert.AreEqual(3, sizing.TrashSize);
            Assert.AreEqual(0, sizing.Overflow);

            Assert.AreEqual(10, WorldEventRosterSelector.SizeWave(Theme(), 4, 0, 0, 0).TrashSize, "unscaled control");
        }

        [TestMethod]
        public void SizeWave_Third_ScalesTheMaxAliveRoomAndAlreadyAliveTrashUsesItUp()
        {
            // maxAlive 24 -> 8. Six already standing leaves a room of 2 (unscaled: 18, so 10 would land).
            var sizing = Size(Theme(), participants: 4, alive: 6, bonus: 0, champions: 0, scale: 0.33);

            Assert.AreEqual(2, sizing.Ceiling);
            Assert.AreEqual(2, sizing.TrashSize);
            Assert.AreEqual(1, sizing.Overflow, "demand 3, room 2");

            Assert.AreEqual(10, WorldEventRosterSelector.SizeWave(Theme(), 4, 6, 0, 0).TrashSize, "unscaled control");
        }

        [TestMethod]
        public void SizeWave_Third_ABossPhaseFullFieldIsEmptyAndNothingIsCulled()
        {
            // 20 alive against a scaled allowance of 8: no room, an empty wave (an unscaled field with 20
            // alive still has room for 4). Nothing in SizeWave can cull - it only ever reports what to add.
            var sizing = Size(Theme(), participants: 4, alive: 20, bonus: 0, champions: 0, scale: 0.33);

            Assert.AreEqual(0, sizing.Ceiling);
            Assert.AreEqual(0, sizing.TrashSize);

            Assert.AreEqual(4, WorldEventRosterSelector.SizeWave(Theme(), 4, 20, 0, 0).TrashSize, "unscaled control");
        }

        [TestMethod]
        public void SizeWave_Third_PaceBonusIsScaledToo()
        {
            // raw 10 + 20 bonus = 30 -> round(9.9) = 10, against a scaled ceiling of 8.
            var sizing = Size(Theme(), participants: 4, alive: 0, bonus: 20, champions: 0, scale: 0.33);

            Assert.AreEqual(10, sizing.Raw);
            Assert.AreEqual(8, sizing.TrashSize);
            Assert.AreEqual(2, sizing.Overflow);
        }

        // ---- overflow champions shrink -------------------------------------------------------------------

        [TestMethod]
        public void SizeWave_Third_OverflowChampionsShrinkWithTheScale()
        {
            var theme = Theme(overflowChampionMaxAlive: 20);

            var full = WorldEventRosterSelector.SizeWave(theme, 40, 0, 0, 0);
            var third = Size(theme, 40, 0, 0, 0, 0.33);

            // Unscaled: raw 64, trash 18, overflow 46, 46/4 = 11 champions.
            Assert.AreEqual(46, full.Overflow);
            Assert.AreEqual(11, full.ChampionCount);

            // Scaled: raw round(21.12) = 21, room 8, trash 8, overflow 13, 13/4 = 3 champions.
            Assert.AreEqual(21, third.Raw);
            Assert.AreEqual(8, third.TrashSize);
            Assert.AreEqual(13, third.Overflow);
            Assert.AreEqual(3, third.ChampionCount);
        }

        // ---- floors --------------------------------------------------------------------------------------

        [TestMethod]
        public void SizeWave_TinyScale_KeepsPositiveDemandAndCeilingAtOne()
        {
            // Scale clamps to 0.05: raw 10 -> round(0.5) = 1; maxAlive 24 -> round(1.2) = 1.
            var theme = Theme(maxAlive: 24, waveBase: 10, perParticipant: 0, cap: 18);

            var sizing = Size(theme, participants: 0, alive: 0, bonus: 0, champions: 0, scale: 0.05);

            Assert.AreEqual(1, sizing.Raw);
            Assert.AreEqual(1, sizing.Ceiling);
            Assert.AreEqual(1, sizing.TrashSize);

            // A maxAlive of 3 would round to 0 at 0.05; the positive ceiling floors at 1 (unscaled: 3).
            var small = Size(Theme(maxAlive: 3, waveBase: 10, perParticipant: 0, cap: 18), 0, 0, 0, 0, 0.05);

            Assert.AreEqual(1, small.Ceiling);
            Assert.AreEqual(1, small.TrashSize);
        }

        [TestMethod]
        public void SizeWave_Scaled_NeverGoesNegative()
        {
            foreach (var alive in new[] { 0, 8, 9, 100, int.MaxValue })
            {
                var sizing = Size(Theme(), 40, alive, 0, 0, 0.33);

                Assert.IsTrue(sizing.Ceiling >= 0 && sizing.TrashSize >= 0 && sizing.Overflow >= 0 && sizing.ChampionCount >= 0,
                    $"alive={alive}: {sizing}");
            }
        }

        [TestMethod]
        public void SizeWave_Scaled_KeepsTheOneCreatureBackstopWhileThereIsAnyRoom()
        {
            // maxAlive 24 -> 8; 7 alive leaves room 1, and a base-1 theme still lands one.
            var sizing = Size(Theme(maxAlive: 24, waveBase: 1, perParticipant: 0, cap: 1), 0, 7, 0, 0, 0.33);

            Assert.AreEqual(1, sizing.TrashSize);
        }

        // ---- clamp ---------------------------------------------------------------------------------------

        [TestMethod]
        public void ResolveBossPhaseTrashMultiplier_ClampsToTheDocumentedRange()
        {
            Assert.AreEqual(0.33, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(0.33), 1e-12);
            Assert.AreEqual(0.5, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(0.5), 1e-12);
            Assert.AreEqual(1.0, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(1.0), 1e-12);
            Assert.AreEqual(1.0, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(7.5), 1e-12, "above 1 is off");
            Assert.AreEqual(0.05, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(0.0), 1e-12);
            Assert.AreEqual(0.05, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(-3.0), 1e-12);
            Assert.AreEqual(0.05, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(0.01), 1e-12);
            Assert.AreEqual(0.33, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(double.NaN), 1e-12);
            Assert.AreEqual(0.33, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(double.PositiveInfinity), 1e-12);
            Assert.AreEqual(0.33, WorldEventRosterSelector.ResolveBossPhaseTrashMultiplier(double.NegativeInfinity), 1e-12);
        }

        [TestMethod]
        public void SizeWave_OutOfRangeScale_IsClampedNotTrusted()
        {
            var atFloor = Size(Theme(), 40, 0, 0, 0, 0.05);

            Assert.AreEqual(atFloor.ToString(), Size(Theme(), 40, 0, 0, 0, 0.0).ToString(), "0 clamps to 0.05");
            Assert.AreEqual(atFloor.ToString(), Size(Theme(), 40, 0, 0, 0, -4.0).ToString(), "negative clamps to 0.05");

            var unscaled = WorldEventRosterSelector.SizeWave(Theme(), 40, 0, 0, 0);

            Assert.AreEqual(unscaled.ToString(), Size(Theme(), 40, 0, 0, 0, 3.0).ToString(), "above 1 clamps to off");
        }

        [TestMethod]
        public void ReadBossPhaseTrashMultiplier_ResolvesTheInjectedRead()
        {
            Assert.AreEqual(0.33, WorldEventRosterSelector.ReadBossPhaseTrashMultiplier(() => throw new InvalidOperationException("no config")), 1e-12);
            Assert.AreEqual(0.33, WorldEventRosterSelector.ReadBossPhaseTrashMultiplier(() => double.NaN), 1e-12);
            Assert.AreEqual(1.0, WorldEventRosterSelector.ReadBossPhaseTrashMultiplier(() => 7.5), 1e-12);
            Assert.AreEqual(0.05, WorldEventRosterSelector.ReadBossPhaseTrashMultiplier(() => 0.01), 1e-12);
            Assert.AreEqual(0.5, WorldEventRosterSelector.ReadBossPhaseTrashMultiplier(() => 0.5), 1e-12);
        }

        // ---- when the cut applies ------------------------------------------------------------------------

        [TestMethod]
        public void BossPhaseApplies_OnlyForAKillBossRunWhileTheBossStands()
        {
            foreach (var goal in new GoalType?[] { null, GoalType.KillCount, GoalType.DestroySource, GoalType.KillBoss, GoalType.Hold })
            {
                foreach (var alive in new[] { false, true })
                {
                    var expected = alive && goal == GoalType.KillBoss;

                    Assert.AreEqual(expected, WorldEventRosterSelector.BossPhaseApplies(goal, alive), $"goal={goal} bossAlive={alive}");
                }
            }
        }

        [TestMethod]
        public void ShouldHoldPace_OnlyWhenTheCutApplies_AndActuallyCuts()
        {
            Assert.IsFalse(WorldEventRosterSelector.ShouldHoldPace(true, 1.0), "multiplier 1.0 is off: pace must still run");
            Assert.IsTrue(WorldEventRosterSelector.ShouldHoldPace(true, 0.33));
            Assert.IsFalse(WorldEventRosterSelector.ShouldHoldPace(false, 0.33));
            Assert.IsFalse(WorldEventRosterSelector.ShouldHoldPace(false, 1.0));
        }

        [TestMethod]
        public void ScaledMaxAlive_IsTheCeilingTheSizingBudgetsAgainst()
        {
            Assert.AreEqual(24, WorldEventRosterSelector.ScaledMaxAlive(Theme(), 1.0));
            Assert.AreEqual(8, WorldEventRosterSelector.ScaledMaxAlive(Theme(), 0.33));
            Assert.AreEqual(24, WorldEventRosterSelector.ScaledMaxAlive(Theme(), double.NaN));
            Assert.AreEqual(0, WorldEventRosterSelector.ScaledMaxAlive(null, 0.33));
        }
        // ---- the pick follows the sizing -----------------------------------------------------------------

        [TestMethod]
        public void PickWave_BossPhaseScale_ShrinksTheWaveAndScaleOneMatchesToday()
        {
            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                Members = new System.Collections.Generic.List<FamilyMember>
                {
                    new FamilyMember { Wcid = 1, Name = "m1", Level = 100, Role = 0 }
                }
            };

            var est = new AudienceEstimate(4, 100, 150);

            var today = WorldEventRosterSelector.PickWave(family, est, Theme(), 0, 0, 0, 0, new Random(1));
            var one = WorldEventRosterSelector.PickWave(family, est, Theme(), 0, 0, 0, 0, new Random(1), 1.0);
            var third = WorldEventRosterSelector.PickWave(family, est, Theme(), 0, 0, 0, 0, new Random(1), 0.33);

            Assert.AreEqual(10, today.Trash.Count);
            Assert.AreEqual(today.Trash.Count, one.Trash.Count);
            Assert.AreEqual(3, third.Trash.Count);
            Assert.IsTrue(third.Trash.All(w => w == 1));
        }
    }
}
