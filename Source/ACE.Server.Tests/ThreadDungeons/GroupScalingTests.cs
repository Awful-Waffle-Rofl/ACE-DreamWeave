using System;

using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins the Group Threads formulas owned by GroupScaling (spec sections 5, 6.1, 6.2). The formula tests are pure:
    /// every tunable is passed in and Fellowship.GetMemberSharePercent takes its plateau as an argument. The two
    /// live readers, ThreadDungeonManager.ReadGroupScaling and GroupModeEnabled, are tested at the end; those
    /// tests seed every key they read and restore the shipped defaults in a finally.
    /// </summary>
    [TestClass]
    public class GroupScalingTests
    {
        private const double Eps = 1e-9;
        private const double RetailPlateau = 2.7;

        private static GroupScaling Defaults(int n, double plateau = RetailPlateau)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, plateau);

        [TestMethod]
        public void Compiled_defaults_match_the_tunable_table()
        {
            Assert.AreEqual(1.0, GroupScaling.DefaultEffortPerMember);
            Assert.AreEqual(0.5, GroupScaling.DefaultCountShare);
            Assert.AreEqual(180, GroupScaling.DefaultGroupMaxMonsters);
            Assert.AreEqual(15, GroupScaling.DefaultDamageRatingPerMember);
            Assert.AreEqual(300, GroupScaling.DefaultDamageRatingCap);
            Assert.AreEqual(0.05, GroupScaling.DefaultRewardBonusPerMember);
            Assert.AreEqual(1.5, GroupScaling.DefaultRewardBonusCap);
            Assert.AreEqual(100, GroupScaling.MaxRosterSize);
        }

        [TestMethod]
        public void Effort_is_one_plus_g_per_extra_member()
        {
            Assert.AreEqual(1.0, GroupScaling.EffortFor(1, 1.0), Eps);
            Assert.AreEqual(2.0, GroupScaling.EffortFor(2, 1.0), Eps);
            Assert.AreEqual(6.0, GroupScaling.EffortFor(6, 1.0), Eps);
            Assert.AreEqual(1.5, GroupScaling.EffortFor(2, 0.5), Eps);
            Assert.AreEqual(1.0, GroupScaling.EffortFor(5, 0.0), Eps, "g = 0 adds no effort");
        }

        [TestMethod]
        public void Count_target_is_one_plus_s_times_extra_effort()
        {
            Assert.AreEqual(1.5, GroupScaling.CountTargetFor(2.0, 0.5), Eps);
            Assert.AreEqual(3.5, GroupScaling.CountTargetFor(6.0, 0.5), Eps);
            Assert.AreEqual(5.5, GroupScaling.CountTargetFor(10.0, 0.5), Eps);
            Assert.AreEqual(1.0, GroupScaling.CountTargetFor(6.0, 0.0), Eps, "s = 0 keeps the solo count");
            Assert.AreEqual(6.0, GroupScaling.CountTargetFor(6.0, 1.0), Eps, "s = 1 scales count with effort");
        }

        [TestMethod]
        public void Health_mult_is_effort_over_actual_count()
        {
            Assert.AreEqual(2.0 / 1.5, GroupScaling.HealthMultFor(2.0, 1.5), Eps);
            Assert.AreEqual(6.0 / 3.5, GroupScaling.HealthMultFor(6.0, 3.5), Eps);
            Assert.AreEqual(6.0, GroupScaling.HealthMultFor(6.0, 0.0), Eps, "count 0 reads as E");
            Assert.AreEqual(6.0, GroupScaling.HealthMultFor(6.0, -1.0), Eps, "negative count reads as E");
            Assert.AreEqual(6.0, GroupScaling.HealthMultFor(6.0, double.NaN), Eps, "NaN count reads as E");
        }

        [TestMethod]
        public void Count_actual_is_group_over_solo_and_one_without_solo_slots()
        {
            Assert.AreEqual(1.5, GroupScaling.CountActualFor(40, 60), Eps);
            Assert.AreEqual(4.5, GroupScaling.CountActualFor(40, 180), Eps);
            Assert.AreEqual(1.0, GroupScaling.CountActualFor(0, 60), Eps);
            Assert.AreEqual(1.0, GroupScaling.CountActualFor(-3, 60), Eps);
        }

        [TestMethod]
        public void Boss_factors_use_effort_not_health()
        {
            var g = Defaults(6);
            Assert.AreEqual(6.0, g.Effort, Eps);
            Assert.AreEqual(1.25, g.RewardBonus, Eps);
            Assert.AreEqual(6.0 * 1.25, g.BossLootFactor(), Eps);
            Assert.AreEqual(6.0 * 1.25 / 2.7, g.BossRewardFactor(), Eps);

            var h = 6.0 / 3.5;
            Assert.AreEqual(h * 1.25, g.TrashLootFactor(h), Eps);
            Assert.AreEqual(h * 1.25 / 2.7, g.TrashRewardFactor(h), Eps);
        }

        [TestMethod]
        public void Damage_bonus_per_member_cap_binding_and_uncapped()
        {
            Assert.AreEqual(0, GroupScaling.DamageRatingBonusFor(1, 15, 300));
            Assert.AreEqual(15, GroupScaling.DamageRatingBonusFor(2, 15, 300));
            Assert.AreEqual(75, GroupScaling.DamageRatingBonusFor(6, 15, 300));
            Assert.AreEqual(300, GroupScaling.DamageRatingBonusFor(21, 15, 300), "20 extra * 15 = 300 lands exactly on the cap");
            Assert.AreEqual(300, GroupScaling.DamageRatingBonusFor(30, 15, 300), "cap binds");
            Assert.AreEqual(435, GroupScaling.DamageRatingBonusFor(30, 15, 0), "cap 0 is uncapped");
            Assert.AreEqual(0, GroupScaling.DamageRatingBonusFor(10, 0, 300), "d = 0 adds nothing");
        }

        [TestMethod]
        public void Damage_bonus_saturates_and_sanitises()
        {
            Assert.AreEqual(int.MaxValue, GroupScaling.DamageRatingBonusFor(100, long.MaxValue, 0), "no overflow");
            Assert.AreEqual(int.MaxValue, GroupScaling.DamageRatingBonusFor(3, (long)int.MaxValue + 5, 0), "saturates at int.MaxValue");
            Assert.AreEqual(75, GroupScaling.DamageRatingBonusFor(6, -1, 300), "negative d reads as default 15");
            Assert.AreEqual(300, GroupScaling.DamageRatingBonusFor(50, 15, -1), "negative cap reads as default 300");
        }

        [TestMethod]
        public void Reward_bonus_caps_and_b_zero()
        {
            Assert.AreEqual(1.0, GroupScaling.RewardBonusFor(1, 0.05, 1.5), Eps);
            Assert.AreEqual(1.05, GroupScaling.RewardBonusFor(2, 0.05, 1.5), Eps);
            Assert.AreEqual(1.45, GroupScaling.RewardBonusFor(10, 0.05, 1.5), Eps);
            Assert.AreEqual(1.5, GroupScaling.RewardBonusFor(11, 0.05, 1.5), Eps);
            Assert.AreEqual(1.5, GroupScaling.RewardBonusFor(40, 0.05, 1.5), Eps, "cap binds");
            Assert.AreEqual(1.0, GroupScaling.RewardBonusFor(10, 0.0, 1.5), Eps, "b = 0 is no bonus");
        }

        [TestMethod]
        public void Reward_bonus_sanitises_garbage()
        {
            Assert.AreEqual(1.45, GroupScaling.RewardBonusFor(10, double.NaN, 1.5), Eps, "NaN b reads as default");
            Assert.AreEqual(1.45, GroupScaling.RewardBonusFor(10, -0.2, 1.5), Eps, "negative b reads as default");
            Assert.AreEqual(1.45, GroupScaling.RewardBonusFor(10, double.PositiveInfinity, 1.5), Eps, "infinite b reads as default");
            Assert.AreEqual(1.5, GroupScaling.RewardBonusFor(40, 0.05, double.NaN), Eps, "NaN cap reads as default");
            Assert.AreEqual(1.0, GroupScaling.RewardBonusFor(40, 0.05, 0.5), Eps, "cap below 1 reads as 1");
            Assert.AreEqual(1.0, GroupScaling.RewardBonusFor(40, 0.05, double.NegativeInfinity), Eps, "never below 1");
        }

        [TestMethod]
        public void Share_total_matches_fellowship_for_one_to_twenty_at_retail_plateau()
        {
            double[] expected = { 1.0, 1.5, 1.8, 2.2, 2.5, 2.7, 2.8, 2.8, 2.7 };

            for (var n = 1; n <= 20; n++)
            {
                var want = n <= 9 ? expected[n - 1] : 2.7;
                Assert.AreEqual(want, GroupScaling.ShareTotalFor(n, RetailPlateau), 1e-9, $"T({n}) at plateau 2.7");
                Assert.AreEqual(n * Fellowship.GetMemberSharePercent(n, RetailPlateau), GroupScaling.ShareTotalFor(n, RetailPlateau), Eps, $"T({n}) must be the fellowship formula");
            }
        }

        [TestMethod]
        public void Share_total_follows_another_plateau_past_nine()
        {
            for (var n = 1; n <= 20; n++)
            {
                var want = n <= 9 ? n * Fellowship.GetMemberSharePercent(n, 2.7) : 4.0;
                Assert.AreEqual(want, GroupScaling.ShareTotalFor(n, 4.0), 1e-9, $"T({n}) at plateau 4.0");
            }

            Assert.AreEqual(2.7, GroupScaling.ShareTotalFor(12, double.NaN), 1e-9, "NaN plateau falls back to the fellowship's retail plateau");
        }

        [TestMethod]
        public void Group_slots_scale_by_count_target_uncapped()
        {
            Assert.AreEqual(60, GroupScaling.GroupSlotsFor(40, 1.0, 40, 1.5, 180, 40));
            Assert.AreEqual(140, GroupScaling.GroupSlotsFor(40, 1.0, 40, 3.5, 180, 40));
            Assert.AreEqual(90, GroupScaling.GroupSlotsFor(20, 1.0, 60, 1.5, 180, 60), "the min-monsters floor scales too");
        }

        [TestMethod]
        public void Group_slots_cap_and_r27_floor()
        {
            Assert.AreEqual(180, GroupScaling.GroupSlotsFor(40, 1.0, 40, 5.5, 180, 40), "group cap binds");
            Assert.AreEqual(50, GroupScaling.GroupSlotsFor(40, 1.0, 40, 3.5, 30, 50), "a cap below solo never lowers the count (R27)");
            Assert.AreEqual(180, GroupScaling.GroupSlotsFor(40, 1.0, 40, 5.5, 0, 40), "cap <= 0 reads as default 180");
        }

        [TestMethod]
        public void Group_slots_zero_without_points_and_sanitised()
        {
            Assert.AreEqual(0, GroupScaling.GroupSlotsFor(0, 1.0, 40, 3.5, 180, 0));
            Assert.AreEqual(0, GroupScaling.GroupSlotsFor(0, 1.0, 40, 3.5, 180, 40), "no curated points, no slots, even with a solo count");
            Assert.AreEqual(60, GroupScaling.GroupSlotsFor(40, double.NaN, 40, 1.5, 180, 40), "NaN count mult reads as 1");
            Assert.AreEqual(40, GroupScaling.GroupSlotsFor(40, 1.0, 40, double.NaN, 180, 40), "NaN count target reads as 1");
            Assert.AreEqual(180, GroupScaling.GroupSlotsFor(40, 1e300, 40, 5.5, 180, 40), "huge products do not overflow");
        }

        [TestMethod]
        public void Group_slots_round_like_the_solo_block()
        {
            // 5 * 1.5 = 7.5 and Math.Round is banker's rounding -> 8; 3 * 1.5 = 4.5 -> 4.
            Assert.AreEqual(Math.Max((int)Math.Round(5 * 1.5), 5), GroupScaling.GroupSlotsFor(5, 1.0, 0, 1.5, 180, 5));
            Assert.AreEqual(4, GroupScaling.GroupSlotsFor(3, 1.0, 0, 1.5, 180, 0));
        }

        [TestMethod]
        public void Worked_checks_at_defaults_on_a_forty_monster_base()
        {
            AssertWorked(2, effort: 2.0, countTarget: 1.5, slots: 60, health: 2.0 / 1.5);
            AssertWorked(6, effort: 6.0, countTarget: 3.5, slots: 140, health: 6.0 / 3.5);
            AssertWorked(10, effort: 10.0, countTarget: 5.5, slots: 180, health: 10.0 / 4.5);

            Assert.AreEqual(1.333, Math.Round(GroupScaling.HealthMultFor(2.0, 1.5), 3), 1e-12);
            Assert.AreEqual(1.714, Math.Round(GroupScaling.HealthMultFor(6.0, 3.5), 3), 1e-12);
            Assert.AreEqual(2.22, Math.Round(GroupScaling.HealthMultFor(10.0, 4.5), 2), 1e-12);
        }

        private static void AssertWorked(int n, double effort, double countTarget, int slots, double health)
        {
            var g = Defaults(n);
            Assert.AreEqual(n, g.RosterSize);
            Assert.IsTrue(g.IsGroup);
            Assert.AreEqual(effort, g.Effort, Eps, $"E at N={n}");
            Assert.AreEqual(countTarget, g.CountTarget, Eps, $"C at N={n}");

            var groupSlots = GroupScaling.GroupSlotsFor(40, 1.0, 40, g.CountTarget, g.GroupMaxMonsters, 40);
            Assert.AreEqual(slots, groupSlots, $"slots at N={n}");

            var actual = GroupScaling.CountActualFor(40, groupSlots);
            Assert.AreEqual(health, GroupScaling.HealthMultFor(g.Effort, actual), Eps, $"H at N={n}");
            Assert.IsTrue(GroupScaling.HealthMultFor(g.Effort, actual) <= g.Effort + Eps, "H never exceeds E");
        }

        [TestMethod]
        public void Roster_of_one_is_solo_with_exact_unit_factors()
        {
            var g = GroupScaling.Compute(1, 7.0, 0.9, 5, 99, 0, 3.0, 9.0, 2.7);

            Assert.AreSame(GroupScaling.Solo, g, "N = 1 returns Solo whatever the tunables say");
            Assert.IsFalse(g.IsGroup);
            Assert.AreEqual(1, g.RosterSize);
            Assert.AreEqual(1.0, g.Effort);
            Assert.AreEqual(1.0, g.CountTarget);
            Assert.AreEqual(0, g.DamageRatingBonus);
            Assert.AreEqual(1.0, g.RewardBonus);
            Assert.AreEqual(1.0, g.ShareTotal);
            Assert.AreEqual(1.0, g.TrashRewardFactor(3.0), "exactly 1.0, not approximately");
            Assert.AreEqual(1.0, g.BossRewardFactor());
            Assert.AreEqual(1.0, g.TrashLootFactor(3.0));
            Assert.AreEqual(1.0, g.BossLootFactor());
        }

        [TestMethod]
        public void Roster_size_clamps_to_one_and_the_maximum()
        {
            Assert.AreSame(GroupScaling.Solo, GroupScaling.Compute(0, 1.0, 0.5, 180, 15, 300, 0.05, 1.5, 2.7));
            Assert.AreSame(GroupScaling.Solo, GroupScaling.Compute(-5, 1.0, 0.5, 180, 15, 300, 0.05, 1.5, 2.7));
            Assert.AreEqual(GroupScaling.MaxRosterSize, Defaults(500).RosterSize);
            Assert.AreEqual(100.0, Defaults(500).Effort, Eps);
        }

        [TestMethod]
        public void Garbage_tunables_read_as_defaults()
        {
            var g = GroupScaling.Compute(6, double.NaN, double.PositiveInfinity, 0, -4, -4, -1.0, double.NaN, double.NaN);
            var d = Defaults(6);

            Assert.AreEqual(d.Effort, g.Effort, Eps);
            Assert.AreEqual(d.CountTarget, g.CountTarget, Eps);
            Assert.AreEqual(GroupScaling.DefaultGroupMaxMonsters, g.GroupMaxMonsters);
            Assert.AreEqual(d.DamageRatingBonus, g.DamageRatingBonus);
            Assert.AreEqual(d.RewardBonus, g.RewardBonus, Eps);
            Assert.AreEqual(d.ShareTotal, g.ShareTotal, Eps);

            Assert.AreEqual(GroupScaling.DefaultGroupMaxMonsters, GroupScaling.Compute(6, 1, 0.5, -10, 15, 300, 0.05, 1.5, 2.7).GroupMaxMonsters);
            Assert.AreEqual(int.MaxValue, GroupScaling.Compute(6, 1, 0.5, long.MaxValue, 15, 300, 0.05, 1.5, 2.7).GroupMaxMonsters);
            Assert.AreEqual(1.0, GroupScaling.CountTargetFor(double.NaN, 0.5), Eps, "NaN effort reads as 1");
        }

        [TestMethod]
        public void Loot_hold_is_snapshotted_sanitised_and_zero_for_solo()
        {
            Assert.AreEqual(0, GroupScaling.Solo.LootHoldMinutes, "a solo run is never held");
            Assert.AreEqual(TimeSpan.Zero, GroupScaling.Solo.LootHold);
            Assert.AreEqual(0, GroupScaling.Compute(1, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7, 45).LootHoldMinutes, "N = 1 is Solo whatever the hold");

            Assert.AreEqual(GroupScaling.DefaultLootHoldMinutes, GroupScaling.Compute(2, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7).LootHoldMinutes, "omitted reads as the default");
            Assert.AreEqual(30, GroupScaling.DefaultLootHoldMinutes);
            Assert.AreEqual(45, GroupScaling.Compute(2, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7, 45).LootHoldMinutes);
            Assert.AreEqual(0, GroupScaling.Compute(2, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7, 0).LootHoldMinutes, "0 is a valid explicit no-hold");
            Assert.AreEqual(GroupScaling.DefaultLootHoldMinutes, GroupScaling.Compute(2, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7, -5).LootHoldMinutes, "negative reads as the default");
            Assert.AreEqual(int.MaxValue, GroupScaling.Compute(2, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7, long.MaxValue).LootHoldMinutes, "saturates");
            Assert.AreEqual(TimeSpan.FromMinutes(int.MaxValue), GroupScaling.Compute(2, 1, 0.5, 180, 15, 300, 0.05, 1.5, 2.7, long.MaxValue).LootHold, "and still converts");
        }

        private static void SeedGroupTunables(double g, double s, long max, long d, long cap, double b, double bCap, double plateau, long hold = GroupScaling.DefaultLootHoldMinutes)
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("dynamic_dungeons_group_effort_per_member", g), "effort key not registered");
            Assert.IsTrue(PropertyManager.ModifyDouble("dynamic_dungeons_group_count_share", s), "count share key not registered");
            Assert.IsTrue(PropertyManager.ModifyLong("dynamic_dungeons_group_max_monsters_per_run", max), "max monsters key not registered");
            Assert.IsTrue(PropertyManager.ModifyLong("dynamic_dungeons_group_damage_rating_per_member", d), "damage per member key not registered");
            Assert.IsTrue(PropertyManager.ModifyLong("dynamic_dungeons_group_damage_rating_cap", cap), "damage cap key not registered");
            Assert.IsTrue(PropertyManager.ModifyDouble("dynamic_dungeons_group_reward_bonus_per_member", b), "bonus per member key not registered");
            Assert.IsTrue(PropertyManager.ModifyDouble("dynamic_dungeons_group_reward_bonus_cap", bCap), "bonus cap key not registered");
            Assert.IsTrue(PropertyManager.ModifyDouble("fellowship_share_group_plateau", plateau), "plateau key not registered");
            Assert.IsTrue(PropertyManager.ModifyLong("dynamic_dungeons_group_loot_hold_minutes", hold), "loot hold key not registered");
        }

        /// <summary>
        /// Restores the shipped defaults WITHOUT asserting, because it runs in a finally: a throwing restore would
        /// replace the test's own failure with its own and hide the assertion that actually failed.
        /// </summary>
        private static void RestoreGroupTunables()
        {
            PropertyManager.ModifyDouble("dynamic_dungeons_group_effort_per_member", GroupScaling.DefaultEffortPerMember);
            PropertyManager.ModifyDouble("dynamic_dungeons_group_count_share", GroupScaling.DefaultCountShare);
            PropertyManager.ModifyLong("dynamic_dungeons_group_max_monsters_per_run", GroupScaling.DefaultGroupMaxMonsters);
            PropertyManager.ModifyLong("dynamic_dungeons_group_damage_rating_per_member", GroupScaling.DefaultDamageRatingPerMember);
            PropertyManager.ModifyLong("dynamic_dungeons_group_damage_rating_cap", GroupScaling.DefaultDamageRatingCap);
            PropertyManager.ModifyDouble("dynamic_dungeons_group_reward_bonus_per_member", GroupScaling.DefaultRewardBonusPerMember);
            PropertyManager.ModifyDouble("dynamic_dungeons_group_reward_bonus_cap", GroupScaling.DefaultRewardBonusCap);
            PropertyManager.ModifyDouble("fellowship_share_group_plateau", RetailPlateau);
            PropertyManager.ModifyLong("dynamic_dungeons_group_loot_hold_minutes", GroupScaling.DefaultLootHoldMinutes);
        }

        [TestMethod]
        public void ReadGroupScaling_reads_every_group_key_and_the_plateau()
        {
            try
            {
                SeedGroupTunables(0.5, 0.25, 90, 20, 50, 0.1, 1.3, 4.0, hold: 45);

                var live = ThreadDungeonManager.ReadGroupScaling(12);
                var want = GroupScaling.Compute(12, 0.5, 0.25, 90, 20, 50, 0.1, 1.3, 4.0);

                Assert.AreEqual(12, live.RosterSize);
                Assert.AreEqual(want.Effort, live.Effort, Eps, "g read");
                Assert.AreEqual(6.5, live.Effort, Eps);
                Assert.AreEqual(want.CountTarget, live.CountTarget, Eps, "s read");
                Assert.AreEqual(90, live.GroupMaxMonsters, "max monsters read");
                Assert.AreEqual(50, live.DamageRatingBonus, "d and cap read: 20 * 11 = 220 capped at 50");
                Assert.AreEqual(1.3, live.RewardBonus, Eps, "b and bonus cap read: 1 + 0.1 * 11 = 2.1 capped at 1.3");
                Assert.AreEqual(4.0, live.ShareTotal, Eps, "plateau read: T(12) at plateau 4.0");
                Assert.AreEqual(45, live.LootHoldMinutes, "loot hold read (final review F1)");
                Assert.AreEqual(TimeSpan.FromMinutes(45), live.LootHold);

                SeedGroupTunables(0.5, 0.25, 90, 20, 0, 0.1, 5.0, 4.0);
                var uncapped = ThreadDungeonManager.ReadGroupScaling(12);
                Assert.AreEqual(220, uncapped.DamageRatingBonus, "cap 0 read as uncapped");
                Assert.AreEqual(2.1, uncapped.RewardBonus, Eps);
            }
            finally
            {
                RestoreGroupTunables();
            }
        }

        [TestMethod]
        public void ReadGroupScaling_at_shipped_defaults_matches_the_worked_check()
        {
            try
            {
                SeedGroupTunables(GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                    GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                    GroupScaling.DefaultRewardBonusCap, RetailPlateau);

                var six = ThreadDungeonManager.ReadGroupScaling(6);
                Assert.AreEqual(6.0, six.Effort, Eps);
                Assert.AreEqual(3.5, six.CountTarget, Eps);
                Assert.AreEqual(180, six.GroupMaxMonsters);
                Assert.AreEqual(75, six.DamageRatingBonus);
                Assert.AreEqual(1.25, six.RewardBonus, Eps);
                Assert.AreEqual(2.7, six.ShareTotal, Eps);
                Assert.AreEqual(30, six.LootHoldMinutes, "the shipped loot hold");

                Assert.AreSame(GroupScaling.Solo, ThreadDungeonManager.ReadGroupScaling(1));
                Assert.AreSame(GroupScaling.Solo, ThreadDungeonManager.ReadGroupScaling(0));
            }
            finally
            {
                RestoreGroupTunables();
            }
        }

        [TestMethod]
        public void GroupModeEnabled_needs_both_the_group_switch_and_pooled_loot()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_group_enabled", true), "group switch key not registered");
                Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_pooled_loot_enabled", true), "pooled loot key not registered");
                Assert.IsTrue(ThreadDungeonManager.GroupModeEnabled, "both on");

                PropertyManager.ModifyBool("dynamic_dungeons_group_enabled", false);
                Assert.IsFalse(ThreadDungeonManager.GroupModeEnabled, "group switch off");

                PropertyManager.ModifyBool("dynamic_dungeons_group_enabled", true);
                PropertyManager.ModifyBool("dynamic_dungeons_pooled_loot_enabled", false);
                Assert.IsFalse(ThreadDungeonManager.GroupModeEnabled, "pooled loot off (R13: group runs are always pooled)");

                PropertyManager.ModifyBool("dynamic_dungeons_group_enabled", false);
                Assert.IsFalse(ThreadDungeonManager.GroupModeEnabled, "both off");
            }
            finally
            {
                PropertyManager.ModifyBool("dynamic_dungeons_group_enabled", true);
                PropertyManager.ModifyBool("dynamic_dungeons_pooled_loot_enabled", true);
            }
        }
        [TestMethod]
        public void Group_factor_with_garbage_health_reads_health_as_one()
        {
            var g = Defaults(2);
            Assert.AreEqual(g.RewardBonus, g.TrashLootFactor(double.NaN), Eps);
            Assert.AreEqual(g.RewardBonus, g.TrashLootFactor(0.0), Eps);
            Assert.AreEqual(g.RewardBonus / g.ShareTotal, g.TrashRewardFactor(-2.0), Eps);
        }
    }
}
