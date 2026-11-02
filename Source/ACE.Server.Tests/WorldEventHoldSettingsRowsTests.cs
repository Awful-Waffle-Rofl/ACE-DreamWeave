using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Hold goal's settings rows in the shipped registry and the typed properties generated from them. Reads only the static
    /// registry and DefaultPropertyManager's default tables, never a live PropertyManager value (those reads throw in a unit test).
    /// </summary>
    [TestClass]
    public class WorldEventHoldSettingsRowsTests
    {
        private static IReadOnlyList<WorldEventSettingRow> HoldRows =>
            WorldEventSettingsRegistry.Default.Rows.Where(r => !r.Scope.IsGlobal).ToList();

        private static IReadOnlyList<WorldEventSettingRow> GlobalRows =>
            WorldEventSettingsRegistry.Default.Rows.Where(r => r.Scope.IsGlobal).ToList();

        // ---- scope ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void EveryHoldRow_IsScopedToTheHoldGoal()
        {
            Assert.IsTrue(HoldRows.Count > 100, $"expected the Hold rows to be registered, found {HoldRows.Count}");

            foreach (var row in HoldRows)
            {
                Assert.AreEqual(SettingScope.Goal("hold"), row.Scope, row.Key);
                StringAssert.StartsWith(row.PropertyName, "world_events_hold_", row.Key);
                Assert.AreEqual("world_events_hold_" + row.Key, row.PropertyName, row.Key);
            }
        }

        [TestMethod]
        public void NoHoldRow_SharesAPropertyOrKeyWithAGlobalRow()
        {
            var globalProps = new HashSet<string>(GlobalRows.Select(r => r.PropertyName), StringComparer.OrdinalIgnoreCase);
            var globalKeys = new HashSet<string>(GlobalRows.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);

            foreach (var row in HoldRows)
            {
                Assert.IsFalse(globalProps.Contains(row.PropertyName), $"{row.PropertyName} is a global row's property");
                Assert.IsFalse(globalKeys.Contains(row.Key), $"{row.Key} is a global row's key");
            }
        }

        /// <summary>The 13 reused global rows, pinned field by field: the Hold slice must not have touched them.</summary>
        private static readonly (string Key, SettingKind Kind, string Default, double Min, double Max, SettingUnsetRule Unset, SettingApplies Applies)[] PinnedGlobals =
        {
            ("max_alive", SettingKind.Long, "0", 0, 200, SettingUnsetRule.NonPositiveIsUnset, SettingApplies.Live),
            ("teaser_lead_seconds", SettingKind.Long, "60", 0, 3600, SettingUnsetRule.AlwaysSet, SettingApplies.NextRun),
            ("progress_interval_seconds", SettingKind.Long, "120", 0, 3600, SettingUnsetRule.AlwaysSet, SettingApplies.Live),
            ("announce_chat_type", SettingKind.Long, "5", 0, 100, SettingUnsetRule.AlwaysSet, SettingApplies.Live),
            ("boss_scale_multiplier", SettingKind.Double, "0.67", 0.1, 5, SettingUnsetRule.AlwaysSet, SettingApplies.NextRun),
            ("boss_hits_to_kill", SettingKind.Double, "3", 1, 50, SettingUnsetRule.NonPositiveIsUnset, SettingApplies.Live),
            ("boss_damage_rating_min", SettingKind.Long, "-80", -200, 0, SettingUnsetRule.AlwaysSet, SettingApplies.Live),
            ("boss_damage_rating_max", SettingKind.Long, "300", 0, 1000, SettingUnsetRule.AlwaysSet, SettingApplies.Live),
            ("boss_per_player", SettingKind.Double, "0", 0, 10, SettingUnsetRule.NonPositiveIsUnset, SettingApplies.NextRun),
            ("boss_health_cap", SettingKind.Double, "0", 0, 1000, SettingUnsetRule.NonPositiveIsUnset, SettingApplies.NextRun),
            ("boss_throughput_floor_health", SettingKind.Long, "0", 0, 100000000, SettingUnsetRule.NonPositiveIsUnset, SettingApplies.NextRun),
            ("boss_throughput_cap_health", SettingKind.Long, "0", 0, 100000000, SettingUnsetRule.NonPositiveIsUnset, SettingApplies.NextRun),
            ("participation_payout_enabled", SettingKind.Bool, "True", 0, 1, SettingUnsetRule.AlwaysSet, SettingApplies.Live)
        };

        [TestMethod]
        public void TheThirteenGlobalRows_AreUnchanged()
        {
            Assert.AreEqual(PinnedGlobals.Length, GlobalRows.Count);

            foreach (var p in PinnedGlobals)
            {
                Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind(p.Key, out var row), p.Key);
                Assert.IsTrue(row.Scope.IsGlobal, p.Key);
                Assert.AreEqual("world_events_" + p.Key, row.PropertyName, p.Key);
                Assert.AreEqual(p.Kind, row.Kind, p.Key);
                Assert.AreEqual(p.Default, row.CodeDefault, p.Key);
                Assert.AreEqual(p.Min, row.Min, p.Key);
                Assert.AreEqual(p.Max, row.Max, p.Key);
                Assert.AreEqual(p.Unset, row.Unset, p.Key);
                Assert.AreEqual(p.Applies, row.Applies, p.Key);
            }
        }

        // ---- golden table: an independent pin on every Hold row ------------------------------------------------------

        /// <summary>
        /// One literal tuple per Hold row, transcribed from the contributor's HoldSettings.*.cs (key, min, max, goals.json path and
        /// applies as Roz wrote them; constants resolved to their values) plus this fork's mapping (kind, shipped default, unset
        /// rule). NOT generated from the rows: a change to a row's range, path or rule must change this table too.
        /// Applies is the contributor's: NextEvent maps to NextRun; NextWave, NextSpawn and StageEnd map to Live with a note.
        /// </summary>
        private static readonly (string Key, SettingKind Kind, string Default, double Min, double Max, SettingUnsetRule Unset, string Json, string Applies)[] Golden =
        {
            ("enabled", SettingKind.Bool, "False", 0, 1, SettingUnsetRule.AlwaysSet, null, "NextEvent"),
            ("knight_count", SettingKind.Long, "0", 1, 3, SettingUnsetRule.NonPositiveIsUnset, "protect.count.base", "NextEvent"),
            ("knight_max_health", SettingKind.Long, "10000", 1000, 100000, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("knight_max_health_by_wcid", SettingKind.Text, "", 0, 0, SettingUnsetRule.EmptyIsUnset, null, "Live"),
            ("knight_damage_taken_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("undefended_seconds", SettingKind.Double, "0", 20, 3600, SettingUnsetRule.NonPositiveIsUnset, "protect.undefendedSeconds", "Live"),
            ("undefended_seconds_per_participant", SettingKind.Double, "-1", 0, 600, SettingUnsetRule.NegativeIsUnset, "protect.undefendedSecondsPerParticipant", "Live"),
            ("undefended_seconds_floor", SettingKind.Double, "-1", 0, 3600, SettingUnsetRule.NegativeIsUnset, "protect.undefendedSecondsFloor", "Live"),
            ("min_death_seconds", SettingKind.Double, "-1", 0, 600, SettingUnsetRule.NegativeIsUnset, "protect.minDeathSeconds", "Live"),
            ("min_survivors", SettingKind.Long, "0", 1, 3, SettingUnsetRule.NonPositiveIsUnset, "protect.minSurvivors", "Live"),
            ("defend_pose", SettingKind.Choice, "on", 0, 0, SettingUnsetRule.EmptyIsUnset, "protect.defendPose", "Live"),
            ("defend_swing_seconds", SettingKind.Double, "0", 1, 10, SettingUnsetRule.NonPositiveIsUnset, "protect.defendPose.swingSeconds", "Live"),
            ("defend_swing_max_seconds", SettingKind.Double, "0", 1, 10, SettingUnsetRule.NonPositiveIsUnset, "protect.defendPose.swingMaxSeconds", "Live"),
            ("health_milestones", SettingKind.Choice, "on", 0, 0, SettingUnsetRule.EmptyIsUnset, "protect.healthMilestones", "Live"),
            ("shrine", SettingKind.Choice, "on", 0, 0, SettingUnsetRule.EmptyIsUnset, "protect.shrine", "Live"),
            ("shrine_heal", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.shrineHeal", "Live"),
            ("shrine_cooldown_seconds", SettingKind.Double, "-1", 0, 600, SettingUnsetRule.NegativeIsUnset, "protect.shrineCooldownSeconds", "Live"),
            ("shrine_count", SettingKind.Long, "3", 0, 3, SettingUnsetRule.AlwaysSet, null, "NextEvent"),
            ("shrine_use_radius", SettingKind.Double, "0", 1, 10, SettingUnsetRule.NonPositiveIsUnset, "protect.shrine.useRadius", "NextEvent"),
            ("shrine_heals_all", SettingKind.Bool, "False", 0, 1, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("shrine_player_cooldown_seconds", SettingKind.Double, "0", 0, 600, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("shrine_bind_range", SettingKind.Double, "8", 2, 20, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("corrupted_kill_heal", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.corruptedKillHeal", "Live"),
            ("rescue_heal", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.rescueHeal", "Live"),
            ("rescue_cap_per_wave", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.rescueCapPerWave", "Live"),
            ("regen_per_second", SettingKind.Double, "-1", 0, 0.1, SettingUnsetRule.NegativeIsUnset, "protect.clearRegenPerSecond", "Live"),
            ("regen_clear_radius", SettingKind.Double, "-1", 0, 50, SettingUnsetRule.NegativeIsUnset, "protect.clearRadius", "Live"),
            ("regen_interval_seconds", SettingKind.Double, "1", 1, 30, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("player_heal", SettingKind.Bool, "True", 0, 1, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("player_heal_cap", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.maxHealPerTenSeconds", "Live"),
            ("player_heal_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("heal_reduction_after_hit", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.healReductionAfterHit", "Live"),
            ("heal_reduction_seconds", SettingKind.Double, "-1", 0, 60, SettingUnsetRule.NegativeIsUnset, "protect.healReductionSeconds", "Live"),
            ("heal_between_waves", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.healBetweenWaves", "Live"),
            ("heal_budget_per_wave", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.healBudgetPerWave", "Live"),
            ("share_breach", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.breach", "NextWave"),
            ("share_runners", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.runners", "NextWave"),
            ("share_ranged", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.ranged", "NextWave"),
            ("share_hunters", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.hunters", "NextWave"),
            ("share_seekers", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.seekers", "NextWave"),
            ("share_corrupted", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.corrupted", "NextWave"),
            ("min_breach", SettingKind.Long, "-1", 0, 10, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.minBreach", "NextWave"),
            ("min_ranged", SettingKind.Long, "-1", 0, 20, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.minRanged", "NextWave"),
            ("min_corrupted", SettingKind.Long, "-1", 0, 5, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.minCorrupted", "NextWave"),
            ("max_corrupted", SettingKind.Long, "-1", 0, 10, SettingUnsetRule.NegativeIsUnset, "protect.waveMix.maxCorrupted", "NextWave"),
            ("runner_health_mult", SettingKind.Double, "0", 0.1, 1, SettingUnsetRule.NonPositiveIsUnset, "protect.specialWaves[type=runners].healthMult", "NextWave"),
            ("runner_speed_mult", SettingKind.Double, "0", 1, 3, SettingUnsetRule.NonPositiveIsUnset, "protect.specialWaves[type=runners].speedMult", "NextWave"),
            ("runner_attack_speed_mult", SettingKind.Double, "0", 1, 3, SettingUnsetRule.NonPositiveIsUnset, "protect.specialWaves[type=runners].attackSpeedMult", "NextWave"),
            ("ranged_keep_distance", SettingKind.Double, "0", 5, 40, SettingUnsetRule.NonPositiveIsUnset, "protect.specialWaves[type=ranged].holdDistance", "NextWave"),
            ("corrupted_health_mult", SettingKind.Double, "0", 1, 5, SettingUnsetRule.NonPositiveIsUnset, "protect.corrupted.healthMult", "NextSpawn"),
            ("corrupted_self_mult", SettingKind.Double, "0", 1, 10, SettingUnsetRule.NonPositiveIsUnset, "protect.corrupted.selfMult", "Live"),
            ("corrupted_empower_mult", SettingKind.Double, "0", 1, 5, SettingUnsetRule.NonPositiveIsUnset, "protect.corrupted.empowerMult", "Live"),
            ("corrupted_assault_every_waves", SettingKind.Long, "-1", 0, 10, SettingUnsetRule.NegativeIsUnset, "protect.corrupted.assaultEveryWaves", "NextWave"),
            ("corrupted_assault_from_wave", SettingKind.Long, "0", 1, 10, SettingUnsetRule.NonPositiveIsUnset, "protect.corrupted.assaultFromWave", "NextWave"),
            ("corrupted_assault_count", SettingKind.Long, "0", 1, 5, SettingUnsetRule.NonPositiveIsUnset, "protect.corrupted.assaultCount", "NextWave"),
            ("corrupted_tint", SettingKind.Text, "none", 0, 0, SettingUnsetRule.EmptyIsUnset, "protect.corrupted.tintPalette", "NextSpawn"),
            ("melee_health_mult", SettingKind.Double, "-1", 0.1, 10, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("ranged_health_mult", SettingKind.Double, "-1", 0.1, 10, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("caster_health_mult", SettingKind.Double, "-1", 0.1, 10, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("melee_player_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("runner_player_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("ranged_player_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("caster_player_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("corrupted_player_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("melee_npc_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("runner_npc_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("ranged_npc_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("caster_npc_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("corrupted_npc_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("melee_attack_speed_mult", SettingKind.Double, "-1", 1, 3, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("ranged_attack_speed_mult", SettingKind.Double, "-1", 1, 3, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("caster_attack_speed_mult", SettingKind.Double, "-1", 1, 3, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("corrupted_attack_speed_mult", SettingKind.Double, "-1", 1, 3, SettingUnsetRule.NegativeIsUnset, null, "NextSpawn"),
            ("melee_level_offset", SettingKind.Long, "0", -50, 50, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("runner_level_offset", SettingKind.Long, "0", -50, 50, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("ranged_level_offset", SettingKind.Long, "0", -50, 50, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("caster_level_offset", SettingKind.Long, "0", -50, 50, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("corrupted_level_offset", SettingKind.Long, "0", -50, 50, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("caster_share_of_ranged", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, null, "NextWave"),
            ("wave_count_base", SettingKind.Long, "0", 1, 20, SettingUnsetRule.NonPositiveIsUnset, "protect.waves.base", "NextEvent"),
            ("wave_count_per_player", SettingKind.Double, "-1", 0, 5, SettingUnsetRule.NegativeIsUnset, "protect.waves.perParticipant", "NextEvent"),
            ("wave_count_cap", SettingKind.Long, "0", 1, 20, SettingUnsetRule.NonPositiveIsUnset, "protect.waves.cap", "NextEvent"),
            ("max_waves", SettingKind.Long, "10", 1, 20, SettingUnsetRule.AlwaysSet, null, "NextEvent"),
            ("wave_size_base", SettingKind.Long, "0", 1, 50, SettingUnsetRule.NonPositiveIsUnset, "protect.waveSize.base", "NextWave"),
            ("wave_size_per_player", SettingKind.Double, "-1", 0, 20, SettingUnsetRule.NegativeIsUnset, "protect.waveSize.perParticipant", "NextWave"),
            ("wave_size_cap", SettingKind.Long, "0", 1, 200, SettingUnsetRule.NonPositiveIsUnset, "protect.waveSize.cap", "NextWave"),
            ("wave_size_base_players", SettingKind.Long, "0", 0, 35, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("wave_size_max_players", SettingKind.Long, "0", 0, 100, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("final_wave_bonus", SettingKind.Double, "0.25", 0, 2, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("first_wave_delay_seconds", SettingKind.Double, "5", 0, 120, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("wave_interval_seconds", SettingKind.Double, "-1", 0, 600, SettingUnsetRule.NegativeIsUnset, "protect.waveIntervalSeconds", "Live"),
            ("breather_seconds", SettingKind.Double, "-1", 0, 60, SettingUnsetRule.NegativeIsUnset, "protect.minWaveGapSeconds", "Live"),
            ("max_wave_interval_seconds", SettingKind.Double, "-1", 0, 600, SettingUnsetRule.NegativeIsUnset, "protect.maxWaveIntervalSeconds", "Live"),
            ("next_wave_alive_share", SettingKind.Double, "-1", 0, 1, SettingUnsetRule.NegativeIsUnset, "protect.nextWaveAliveShare", "Live"),
            ("spawn_radius", SettingKind.Double, "-1", 0, 80, SettingUnsetRule.NegativeIsUnset, "protect.spawnRadius", "NextWave"),
            ("portal_count", SettingKind.Long, "0", 1, 8, SettingUnsetRule.NonPositiveIsUnset, "protect.portals.count", "NextEvent"),
            ("portal_radius", SettingKind.Double, "0", 5, 80, SettingUnsetRule.NonPositiveIsUnset, "protect.portals.radius", "NextEvent"),
            ("hold_seconds", SettingKind.Long, "0", 60, 3600, SettingUnsetRule.NonPositiveIsUnset, "holdSeconds", "Live"),
            ("max_seconds", SettingKind.Long, "0", 360, 3600, SettingUnsetRule.NonPositiveIsUnset, null, "NextEvent"),
            ("abandon_seconds", SettingKind.Long, "-1", 0, 3600, SettingUnsetRule.NegativeIsUnset, null, "NextEvent"),
            ("finale_health_mult", SettingKind.Double, "0", 1, 10, SettingUnsetRule.NonPositiveIsUnset, "protect.finaleBossHealthMult", "NextSpawn"),
            ("finale_health", SettingKind.Long, "0", 1000, 10000000, SettingUnsetRule.NonPositiveIsUnset, null, "NextSpawn"),
            ("finale_scale", SettingKind.Double, "-1", 0, 2.5, SettingUnsetRule.NegativeIsUnset, "protect.finaleBossScale", "NextSpawn"),
            ("finale_min_kill_seconds", SettingKind.Double, "-1", 0, 300, SettingUnsetRule.NegativeIsUnset, "protect.bossMinKillSeconds", "Live"),
            ("finale_lead_seconds", SettingKind.Double, "-1", 0, 30, SettingUnsetRule.NegativeIsUnset, "protect.finaleBossLeadSeconds", "NextWave"),
            ("finale_target", SettingKind.Choice, "", 0, 0, SettingUnsetRule.EmptyIsUnset, "protect.bossTargetsProtected", "NextSpawn"),
            ("finale_swap_seconds", SettingKind.Double, "0", 5, 120, SettingUnsetRule.NonPositiveIsUnset, "protect.bossSwapSeconds", "Live"),
            ("finale_swap_max_seconds", SettingKind.Double, "0", 5, 120, SettingUnsetRule.NonPositiveIsUnset, "protect.bossSwapMaxSeconds", "Live"),
            ("finale_pull_share", SettingKind.Double, "-1", 0, 0.5, SettingUnsetRule.NegativeIsUnset, "protect.bossPullShare", "Live"),
            ("finale_pull_window_seconds", SettingKind.Double, "0", 2, 30, SettingUnsetRule.NonPositiveIsUnset, "protect.bossPullWindowSeconds", "Live"),
            ("finale_pull_seconds", SettingKind.Double, "0", 3, 60, SettingUnsetRule.NonPositiveIsUnset, "protect.bossPullSeconds", "Live"),
            ("finale_player_damage_mult", SettingKind.Double, "1", 0, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("finale_level_offset", SettingKind.Long, "0", -50, 50, SettingUnsetRule.AlwaysSet, null, "NextSpawn"),
            ("finale_add_count", SettingKind.Long, "0", 0, 10, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("finale_add_interval_seconds", SettingKind.Double, "30", 5, 300, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("finale_enrage_seconds", SettingKind.Double, "0", 0, 1800, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("finale_enrage_mult", SettingKind.Double, "1.5", 1, 5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("finale_boss_wcid", SettingKind.Long, "0", 0, 2147483647, SettingUnsetRule.NonPositiveIsUnset, null, "NextSpawn"),
            ("finale_fallback_scale", SettingKind.Double, "3.08", 0.5, 12, SettingUnsetRule.AlwaysSet, null, "NextSpawn"),
            ("finale_height_scale_min", SettingKind.Double, "0.5", 0.1, 5, SettingUnsetRule.AlwaysSet, null, "NextSpawn"),
            ("finale_height_scale_max", SettingKind.Double, "12", 1, 20, SettingUnsetRule.AlwaysSet, null, "NextSpawn"),
            ("boss_damage_keep_seconds", SettingKind.Double, "30", 10, 120, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("participants_per_cache", SettingKind.Long, "0", 1, 100, SettingUnsetRule.NonPositiveIsUnset, null, "StageEnd"),
            ("min_contribution", SettingKind.Double, "0", 0, 0.5, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("failure_reward", SettingKind.Choice, "consolation", 0, 0, SettingUnsetRule.EmptyIsUnset, null, "Live"),
            ("participant_radius", SettingKind.Double, "0", 10, 300, SettingUnsetRule.NonPositiveIsUnset, null, "Live"),
            ("min_presence_seconds", SettingKind.Double, "0", 0, 1800, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("afk_seconds", SettingKind.Double, "0", 0, 1800, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("admins_count", SettingKind.Bool, "False", 0, 1, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("broadcast", SettingKind.Choice, "world", 0, 0, SettingUnsetRule.EmptyIsUnset, null, "Live"),
            ("milestone_percents", SettingKind.Text, "75,50,25", 0, 0, SettingUnsetRule.EmptyIsUnset, null, "Live"),
            ("hit_interval_seconds", SettingKind.Double, "2", 0.5, 10, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("hit_interval_max_seconds", SettingKind.Double, "4", 1, 20, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("cap_burst_seconds", SettingKind.Double, "2", 0, 10, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("spawn_clearance", SettingKind.Double, "10", 0, 50, SettingUnsetRule.AlwaysSet, null, "NextWave"),
            ("ranged_slack", SettingKind.Double, "1.5", 0, 10, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("healer_memory_seconds", SettingKind.Double, "20", 0, 120, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("wave_resolve_grace_seconds", SettingKind.Double, "15", 5, 120, SettingUnsetRule.AlwaysSet, null, "Live"),
            ("seek_share", SettingKind.Double, "0", 0, 1, SettingUnsetRule.NonPositiveIsUnset, "protect.seekShare", "NextWave")
        };

        [TestMethod]
        public void EveryHoldRow_MatchesTheGoldenTable_BothWays()
        {
            var problems = new List<string>();
            var golden = Golden.ToDictionary(g => g.Key, StringComparer.Ordinal);

            foreach (var row in HoldRows)
                if (!golden.ContainsKey(row.Key))
                    problems.Add($"{row.Key}: registered but not in the golden table");

            foreach (var g in Golden)
            {
                if (!WorldEventSettingsRegistry.Default.TryFind(g.Key, out var row) || row.Scope.IsGlobal || row.Key != g.Key)
                {
                    problems.Add($"{g.Key}: in the golden table but not a Hold row");
                    continue;
                }

                void Check(string field, object expected, object actual)
                {
                    if (!Equals(expected, actual))
                        problems.Add($"{g.Key}.{field}: expected {expected ?? "null"}, got {actual ?? "null"}");
                }

                Check("Kind", g.Kind, row.Kind);
                Check("CodeDefault", g.Default, row.CodeDefault);
                Check("Min", g.Min, row.Min);
                Check("Max", g.Max, row.Max);
                Check("Unset", g.Unset, row.Unset);
                Check("JsonPath", g.Json, row.JsonPath);
                Check("Applies", g.Applies == "NextEvent" ? SettingApplies.NextRun : SettingApplies.Live, row.Applies);

                var note = g.Applies == "NextWave" ? "(next wave)" : g.Applies == "NextSpawn" ? "(next spawn)" : g.Applies == "StageEnd" ? "(at stage end)" : null;

                if (note != null && !row.Description.EndsWith(note, StringComparison.Ordinal))
                    problems.Add($"{g.Key}.Description: should end with {note}");
            }

            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.AreEqual(Golden.Length, HoldRows.Count);
        }
        // ---- the master switch -----------------------------------------------------------------------------------------

        [TestMethod]
        public void HoldEnabled_ShipsOff()
        {
            Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind("world_events_hold_enabled", out var row));
            Assert.AreEqual(SettingKind.Bool, row.Kind);
            Assert.AreEqual("False", row.CodeDefault);
            Assert.IsFalse(DefaultPropertyManager.DefaultBooleanProperties["world_events_hold_enabled"].Item, "owner ruling: Hold is off until switched on");
        }

        // ---- the generated typed properties ----------------------------------------------------------------------------

        [TestMethod]
        public void EveryHoldRow_HasItsTypedPropertyWithTheShippedDefault()
        {
            foreach (var row in HoldRows)
            {
                Assert.IsTrue(PropertyAdminService.TryResolveType(row.PropertyName, out var type), $"{row.PropertyName} is not a registered property");
                Assert.AreEqual(WorldEventSettingsService.TypeFor(row), type, row.PropertyName);

                switch (row.Kind)
                {
                    case SettingKind.Long:
                        Assert.AreEqual(long.Parse(row.CodeDefault, CultureInfo.InvariantCulture), DefaultPropertyManager.DefaultLongProperties[row.PropertyName].Item, row.PropertyName);
                        break;
                    case SettingKind.Double:
                        Assert.AreEqual(double.Parse(row.CodeDefault, CultureInfo.InvariantCulture), DefaultPropertyManager.DefaultDoubleProperties[row.PropertyName].Item, row.PropertyName);
                        break;
                    case SettingKind.Bool:
                        Assert.AreEqual(bool.Parse(row.CodeDefault), DefaultPropertyManager.DefaultBooleanProperties[row.PropertyName].Item, row.PropertyName);
                        break;
                    default:
                        Assert.AreEqual("", DefaultPropertyManager.DefaultStringProperties[row.PropertyName].Item, $"{row.PropertyName}: an empty string is not set");
                        break;
                }

                StringAssert.StartsWith(PropertyDescriptionOf(row), "(non-retail function) World Events hold goal", row.PropertyName);
            }
        }

        private static string PropertyDescriptionOf(WorldEventSettingRow row)
        {
            switch (row.Kind)
            {
                case SettingKind.Long: return DefaultPropertyManager.DefaultLongProperties[row.PropertyName].Description;
                case SettingKind.Double: return DefaultPropertyManager.DefaultDoubleProperties[row.PropertyName].Description;
                case SettingKind.Bool: return DefaultPropertyManager.DefaultBooleanProperties[row.PropertyName].Description;
                default: return DefaultPropertyManager.DefaultStringProperties[row.PropertyName].Description;
            }
        }

        [TestMethod]
        public void UnsetRules_ShipTheirNotSetMarker()
        {
            foreach (var row in HoldRows)
            {
                if (row.Unset == SettingUnsetRule.NegativeIsUnset)
                    Assert.AreEqual("-1", row.CodeDefault, $"{row.Key}: a NegativeIsUnset row ships not set");
                else if (row.Unset == SettingUnsetRule.NonPositiveIsUnset)
                {
                    Assert.AreEqual("0", row.CodeDefault, $"{row.Key}: a NonPositiveIsUnset row ships not set");
                    // seek_share keeps the contributor's "set when above 0" rule (it was a reused NonPositive property there).
                    if (row.Key != "seek_share")
                        Assert.IsTrue(row.Min > 0 || row.JsonPath == null, $"{row.Key}: 0 would be a wanted value; it needs NegativeIsUnset");
                }

                if (row.Unset == SettingUnsetRule.NegativeIsUnset || row.Unset == SettingUnsetRule.NonPositiveIsUnset)
                    Assert.IsTrue(row.IsNumber, row.Key);
            }
        }

        // ---- the slice's scope -----------------------------------------------------------------------------------------

        [TestMethod]
        public void TheChainRowsAreNotInThisSlice()
        {
            foreach (var row in HoldRows)
                Assert.IsFalse(row.Key.StartsWith("chain", StringComparison.Ordinal), row.Key);

            foreach (var skipped in new[] { "keep_stage_rewards_on_fail", "story", "chain_full_bonus", "chain_reward_stage1", "chain_champion_health_mult" })
                Assert.IsFalse(WorldEventSettingsRegistry.Default.TryFind(skipped, out _), skipped);
        }

        [TestMethod]
        public void KnightHealthByWcid_NamesTheForkKnightWcids()
        {
            Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind("knight_max_health_by_wcid", out var row));
            StringAssert.Contains(row.Description, "1007650");

            // Built, so the retired id block never appears as a literal in this tree.
            var retiredBlock = "20000" + "0";
            Assert.IsFalse(row.Description.Contains(retiredBlock), "the description must name this fork's knight wcids, not the contributor's");
        }

        [TestMethod]
        public void TheAfkAndThresholdRules_AreHoldOnly()
        {
            foreach (var key in new[] { "afk_seconds", "min_presence_seconds", "min_contribution" })
            {
                Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind(key, out var row), key);
                Assert.AreEqual(WorldEventSettingsRegistry.HoldScope, row.Scope, key);
            }
        }

        [TestMethod]
        public void EveryJsonPathRow_HasAGoalReader_AndNoOtherRowDoes()
        {
            var withJson = HoldRows.Where(r => r.JsonPath != null).Select(r => r.Key).ToList();

            CollectionAssert.AreEquivalent(withJson, WorldEventHoldSettingsResolver.GoalValues.Keys.ToList());
        }

        // ---- the goal-scoped writer ------------------------------------------------------------------------------------

        [TestMethod]
        public void HoldScopedWriter_RefusesEveryGlobalKey_AndAGlobalWriterRefusesHoldKeys()
        {
            var writes = new List<string>();
            var service = new WorldEventSettingsService(WorldEventSettingsRegistry.Default, new WorldEventSettingsIo
            {
                ReadDouble = _ => 0.5,
                ReadLong = _ => 5,
                ReadBool = _ => true,
                ReadString = _ => "x",
                DefaultText = p => WorldEventSettingsRegistry.Default.TryFind(p, out var r) ? WorldEventSettingsRegistry.PropertyDefaultText(r) : null,
                Write = (type, property, text, session) => { writes.Add(property); return null; }
            });

            var hold = WorldEventSettingsRegistry.HoldScope;

            foreach (var g in GlobalRows)
            {
                var o = service.Set(hold, g.Key, g.Kind == SettingKind.Bool ? "on" : "1", null);
                Assert.AreEqual(1, o.Refused.Count, g.Key);
            }

            Assert.AreEqual(0, writes.Count, "a Hold caller wrote a global property");

            service.Reset(hold, "all", null);
            Assert.IsTrue(writes.Count > 0, "the control: a Hold reset does write the Hold rows");
            Assert.IsTrue(writes.All(p => p.StartsWith("world_events_hold_", StringComparison.Ordinal)), string.Join(", ", writes));

            writes.Clear();
            var refused = service.Set(SettingScope.Global, "shrine_heal", "0.2", null);
            Assert.AreEqual(1, refused.Refused.Count);
            Assert.AreEqual(0, writes.Count, "a global caller wrote a Hold property");

            var ok = service.Set(hold, "shrine_heal", "0", null);
            CollectionAssert.Contains(ok.Written, "shrine_heal");
            CollectionAssert.AreEqual(new[] { "world_events_hold_shrine_heal" }, writes);
        }
    }
}
