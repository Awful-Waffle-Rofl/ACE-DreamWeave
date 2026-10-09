using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>The crystal plan, the health scaler, the sided respawn policy and the mode picker (the Attack/Defend pure cores).</summary>
    [TestClass]
    public class AttackDefendPlanTests
    {
        private static BattlegroundLayout Mines => BattlegroundMapCatalog.Bg003c;

        private static BattlegroundDials Dials(int count = 0, int perAttacker = 5000) =>
            BattlegroundTunables.Defaults with { AdCrystalCount = count, AdCrystalHealthPerAttacker = perAttacker };

        // ---------------- AttackDefendPlan ----------------

        [TestMethod]
        public void CountZero_UsesTheMapDefault_AllThreeSitesInOrder()
        {
            var plan = AttackDefendPlan.Build(Mines, Dials(count: 0), attackerCount: 4);

            Assert.AreEqual(3, plan.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, plan.Crystals.Select(c => c.Index).ToArray(), "the index is the position in the plan");
            CollectionAssert.AreEqual(Mines.CrystalSites.ToArray(), plan.Crystals.Select(c => c.Site).ToArray());
        }

        [TestMethod]
        public void CountAboveSites_ClampsToTheSiteCount()
        {
            Assert.AreEqual(3, AttackDefendPlan.Build(Mines, Dials(count: 4), 4).Count);
            Assert.AreEqual(3, AttackDefendPlan.Build(Mines, Dials(count: 99), 4).Count);
        }

        [TestMethod]
        public void SmallerCounts_TakeTheFirstSitesInListOrder()
        {
            var one = AttackDefendPlan.Build(Mines, Dials(count: 1), 4);
            var two = AttackDefendPlan.Build(Mines, Dials(count: 2), 4);

            CollectionAssert.AreEqual(new[] { "Great Hall" }, one.Crystals.Select(c => c.Site.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "Great Hall", "West Cavern" }, two.Crystals.Select(c => c.Site.Name).ToArray());
        }

        [TestMethod]
        public void NegativeCount_ClampsToOne_NotToTheDefault()
        {
            Assert.AreEqual(1, AttackDefendPlan.Build(Mines, Dials(count: -3), 4).Count);
        }

        [TestMethod]
        public void HealthPerCrystal_IsPerAttackerTimesAttackers()
        {
            Assert.AreEqual(5000 * 2, AttackDefendPlan.Build(Mines, Dials(), 2).HealthPerCrystal);
            Assert.AreEqual(5000 * 6, AttackDefendPlan.Build(Mines, Dials(), 6).HealthPerCrystal);
            Assert.AreEqual(1234 * 5, AttackDefendPlan.Build(Mines, Dials(perAttacker: 1234), 5).HealthPerCrystal);
            Assert.IsTrue(AttackDefendPlan.Build(Mines, Dials(), 6).Crystals.All(c => c.MaxHealth == 30000), "every crystal gets the same health");
        }

        [TestMethod]
        public void HealthPerCrystal_ChangesTheNumber_NotJustTheShape()
        {
            // Three attackers vs four attackers must differ by exactly one attacker's share.
            var three = AttackDefendPlan.Build(Mines, Dials(), 3).HealthPerCrystal;
            var four = AttackDefendPlan.Build(Mines, Dials(), 4).HealthPerCrystal;

            Assert.AreEqual(5000, four - three);
        }

        [TestMethod]
        public void HealthSaturates_AndAnEmptyTeamStillGivesALivingCrystal()
        {
            Assert.AreEqual(int.MaxValue, AttackDefendPlan.Build(Mines, Dials(perAttacker: int.MaxValue), 6).HealthPerCrystal);
            Assert.AreEqual(5000, AttackDefendPlan.Build(Mines, Dials(), 0).HealthPerCrystal, "zero attackers counts as one, never a dead crystal");
        }

        [TestMethod]
        public void CompassWordsAreDistinct_AndMatchTheSiteBearings()
        {
            var plan = AttackDefendPlan.Build(Mines, Dials(), 4);

            CollectionAssert.AreEqual(new[] { "north-east", "north-west", "south" }, plan.Crystals.Select(c => c.Compass).ToArray());
        }

        [TestMethod]
        public void ALayoutWithNoSites_PlansNothing()
        {
            Assert.AreEqual(0, AttackDefendPlan.Build(BattlegroundMapCatalog.Bg016c, Dials(), 4).Count);
            Assert.AreEqual(0, AttackDefendPlan.Build(null, Dials(), 4).Count);
        }

        [TestMethod]
        public void EffectiveCount_UsesTheLayoutsOwnDefault()
        {
            var twoDefault = Mines with { DefaultCrystalCount = 2 };

            Assert.AreEqual(2, AttackDefendPlan.EffectiveCount(twoDefault, Dials(count: 0)));
            Assert.AreEqual(3, AttackDefendPlan.EffectiveCount(twoDefault, Dials(count: 3)), "a set count wins over the map default");
        }

        // ---------------- CrystalHealthScaler ----------------

        [TestMethod]
        public void Scaler_ScalesCurrentAndMaxByTheAttackerRatio()
        {
            var scaled = CrystalHealthScaler.Scale(new[] { new CrystalHealth(0, 12000, 24000), new CrystalHealth(1, 24000, 24000) }, sizedForAttackers: 4, currentAttackers: 3);

            Assert.AreEqual(new CrystalHealth(0, 9000, 18000), scaled[0], "half-damaged stays half-damaged");
            Assert.AreEqual(new CrystalHealth(1, 18000, 18000), scaled[1], "untouched stays full");
        }

        [TestMethod]
        public void Scaler_RepeatedCalls_ComposeInsteadOfCompounding()
        {
            var start = new[] { new CrystalHealth(0, 24000, 24000) };

            // 4 -> 3 -> 2 in two steps lands exactly where 4 -> 2 in one step does, because each call is sized for the previous count.
            var step1 = CrystalHealthScaler.Scale(start, 4, 3);
            var step2 = CrystalHealthScaler.Scale(step1, 3, 2);
            var direct = CrystalHealthScaler.Scale(start, 4, 2);

            Assert.AreEqual(direct[0], step2[0]);
            Assert.AreEqual(new CrystalHealth(0, 12000, 12000), direct[0]);
        }

        [TestMethod]
        public void Scaler_NeverKillsAStandingCrystal_AndNeverRaisesOne()
        {
            var tiny = CrystalHealthScaler.Scale(new[] { new CrystalHealth(0, 1, 100) }, 6, 1);

            Assert.AreEqual(1, tiny[0].Current, "a standing crystal keeps at least 1 health");
            Assert.AreEqual(17, tiny[0].Max);

            var none = CrystalHealthScaler.Scale(new[] { new CrystalHealth(0, 50, 100) }, 6, 0);
            Assert.AreEqual(1, none[0].Current);
            Assert.AreEqual(1, none[0].Max);

            var more = CrystalHealthScaler.Scale(new[] { new CrystalHealth(0, 50, 100) }, 4, 6);
            Assert.AreEqual(new CrystalHealth(0, 50, 100), more[0], "the ratio is capped at 1: a larger count does not grow a crystal");
        }

        [TestMethod]
        public void Scaler_CurrentNeverExceedsTheScaledMax()
        {
            foreach (var cur in new[] { 1, 7, 99, 100 })
            {
                var s = CrystalHealthScaler.Scale(new[] { new CrystalHealth(0, cur, 100) }, 6, 5)[0];

                Assert.IsTrue(s.Current <= s.Max, $"current {cur}");
                Assert.IsTrue(s.Current >= 1);
            }
        }

        [TestMethod]
        public void Scaler_EmptyAndNull_AreSafe()
        {
            Assert.AreEqual(0, CrystalHealthScaler.Scale(null, 4, 3).Count);
            Assert.AreEqual(0, CrystalHealthScaler.Scale(Array.Empty<CrystalHealth>(), 4, 3).Count);
        }

        // ---------------- SidedRespawnPolicy ----------------

        private static (PvpMatch Match, PvpParticipant Attacker, PvpParticipant Defender) TwoSidedMatch()
        {
            var a = new PvpParticipant(1, 1500);
            var d = new PvpParticipant(2, 1500);
            var teams = new List<PvpTeam> { new PvpTeam(0, new List<PvpParticipant> { a }), new PvpTeam(1, new List<PvpParticipant> { d }) };

            return (new PvpMatch(Guid.NewGuid(), BattlegroundModes.AttackDefendModeKey, teams, DateTime.UnixEpoch), a, d);
        }

        [TestMethod]
        public void SidedRespawn_AttackersAndDefendersWaitTheirOwnDelay()
        {
            var (m, a, d) = TwoSidedMatch();
            var policy = new SidedRespawnPolicy(attackerSeconds: 20, defenderSeconds: 45);

            var attacker = (DeathDisposition.RespawnDisposition)policy.OnDeath(m, a);
            var defender = (DeathDisposition.RespawnDisposition)policy.OnDeath(m, d);

            Assert.AreEqual(TimeSpan.FromSeconds(20), attacker.Delay);
            Assert.AreEqual(TimeSpan.FromSeconds(45), defender.Delay);
            Assert.AreEqual(BattlegroundRespawnPolicy.CoordinatorChoosesSpawn, attacker.SpawnPointIndex);
            Assert.AreEqual(BattlegroundRespawnPolicy.CoordinatorChoosesSpawn, defender.SpawnPointIndex);
        }

        [TestMethod]
        public void SidedRespawn_EqualDefaults_AreThirtySecondsEach()
        {
            var (m, a, d) = TwoSidedMatch();
            var dials = BattlegroundTunables.Defaults;
            var policy = new SidedRespawnPolicy(dials.AdRespawnSecondsAttackers, dials.AdRespawnSecondsDefenders);

            Assert.AreEqual(TimeSpan.FromSeconds(30), ((DeathDisposition.RespawnDisposition)policy.OnDeath(m, a)).Delay);
            Assert.AreEqual(TimeSpan.FromSeconds(30), ((DeathDisposition.RespawnDisposition)policy.OnDeath(m, d)).Delay);
        }

        [TestMethod]
        public void SidedRespawn_ZeroIsValid_AndNegativeFloorsAtZero()
        {
            var (m, a, d) = TwoSidedMatch();
            var policy = new SidedRespawnPolicy(0, -5);

            Assert.AreEqual(TimeSpan.Zero, ((DeathDisposition.RespawnDisposition)policy.OnDeath(m, a)).Delay);
            Assert.AreEqual(TimeSpan.Zero, ((DeathDisposition.RespawnDisposition)policy.OnDeath(m, d)).Delay);
        }

        // ---------------- BattlegroundModePicker ----------------

        private static IReadOnlyList<PvpModeDefinition> Modes() => BattlegroundModes.All(BattlegroundTunables.Defaults);

        [TestMethod]
        public void Picker_RotatesThroughTheEnabledModes()
        {
            var picker = new BattlegroundModePicker();
            var modes = Modes();
            var keys = Enumerable.Range(0, 5).Select(_ => picker.Pick(modes).ModeKey).ToArray();

            CollectionAssert.AreEqual(new[] { "bg_koth", "bg_ad", "bg_koth", "bg_ad", "bg_koth" }, keys);
        }

        [TestMethod]
        public void Picker_SkipsAModeWhoseTemplatesAreNotOfferedToEverySeat()
        {
            var picker = new BattlegroundModePicker();
            var modes = Modes();

            for (var i = 0; i < 4; i++)
                Assert.AreEqual("bg_ad", picker.Pick(modes, m => m.ModeKey == "bg_ad").ModeKey, $"pick {i}");
        }

        [TestMethod]
        public void Picker_ASkippedModeIsNotStarved_WhenItQualifiesAgain()
        {
            var picker = new BattlegroundModePicker();
            var modes = Modes();

            Assert.AreEqual("bg_ad", picker.Pick(modes, m => m.ModeKey == "bg_ad").ModeKey);
            Assert.AreEqual("bg_koth", picker.Pick(modes).ModeKey, "the cursor moved past ad, so koth is next");
            Assert.AreEqual("bg_ad", picker.Pick(modes).ModeKey);
        }

        [TestMethod]
        public void Picker_NoneQualifies_FallsBackToTheFirstEnabledMode()
        {
            var picker = new BattlegroundModePicker();
            var modes = Modes();

            Assert.AreEqual("bg_koth", picker.Pick(modes, m => false).ModeKey);
            Assert.AreEqual("bg_koth", picker.Pick(modes, m => false).ModeKey, "a fallback is repeatable");
            Assert.AreEqual("bg_koth", picker.Pick(modes).ModeKey, "and it did not move the cursor");
        }

        [TestMethod]
        public void Picker_OnlyOneEnabledMode_IsAlwaysThatMode_AndNoModesIsNull()
        {
            var picker = new BattlegroundModePicker();
            var onlyAd = Modes().Where(m => m.ModeKey == "bg_ad").ToList();

            for (var i = 0; i < 3; i++)
                Assert.AreEqual("bg_ad", picker.Pick(onlyAd).ModeKey);

            Assert.IsNull(picker.Pick(new List<PvpModeDefinition>()));
            Assert.IsNull(picker.Pick(null));
        }

        /// <summary>
        /// With a seat count and no mode offered to every seat, the mode offered to the most seats wins; a tie goes to round-robin order
        /// from the cursor; a negative count (a mode that cannot be played) is never picked; and none of it moves the cursor.
        /// </summary>
        [TestMethod]
        public void Picker_NoneQualifies_PicksTheModeOfferedToTheMostSeats()
        {
            var picker = new BattlegroundModePicker();
            var modes = Modes();

            Assert.AreEqual("bg_ad", picker.Pick(modes, m => false, m => m.ModeKey == "bg_ad" ? 3 : 1).ModeKey, "three seats beat one");
            Assert.AreEqual("bg_koth", picker.Pick(modes, m => false, m => m.ModeKey == "bg_ad" ? 1 : 3).ModeKey);
            Assert.AreEqual("bg_koth", picker.Pick(modes, m => false, m => 2).ModeKey, "a tie goes to the cursor's order (koth first here)");
            Assert.AreEqual("bg_koth", picker.Pick(modes, m => false, m => m.ModeKey == "bg_ad" ? -1 : 0).ModeKey, "an unplayable mode is never the fallback");
            Assert.AreEqual("bg_koth", picker.Pick(modes, m => false, m => m.ModeKey == "bg_koth" ? -1 : -5).ModeKey, "nothing playable: the first enabled mode");
            Assert.AreEqual("bg_koth", picker.Pick(modes).ModeKey, "no fallback moved the cursor: koth, and the cursor is now on ad");

            Assert.AreEqual("bg_ad", picker.Pick(modes, m => false, m => 2).ModeKey, "the tie follows the cursor: ad is first from it");
            Assert.AreEqual("bg_ad", picker.Pick(modes).ModeKey, "the tie did not move the cursor");
        }
    }
}
