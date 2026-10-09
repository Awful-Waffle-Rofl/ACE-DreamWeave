using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The pvp_bg_* settings seam: BattlegroundTunables.Defaults must equal the PropertyManager registered
    /// defaults for every key (Docs/Pvp/BATTLEGROUNDS.md "Settings"), and the premade-size clamp must hold.
    /// Reads only the static Default*Properties dictionaries; no live PropertyManager value is touched.
    /// </summary>
    [TestClass]
    public class BattlegroundTunablesTests
    {
        private static IEnumerable<(string Key, bool Value)> BoolKeys()
        {
            var d = BattlegroundTunables.Defaults;
            yield return ("pvp_bg_enabled", d.Enabled);
            yield return ("pvp_bg_koth_enabled", d.KothEnabled);
            yield return ("pvp_bg_split_clanmates", d.SplitClanmates);
            yield return ("pvp_bg_koth_dedupe_ip", d.KothDedupeIp);
            yield return ("pvp_bg_koth_drain_lethal", d.KothDrainLethal);
            yield return ("pvp_bg_team_fellowship", d.TeamFellowship);
            yield return ("pvp_bg_team_fellowship_restore", d.TeamFellowshipRestore);
            yield return ("pvp_bg_koth_markers_enabled", d.KothMarkersEnabled);
            yield return ("pvp_bg_ad_enabled", d.AdEnabled);
            yield return ("pvp_bg_ad_sequential_crystals", d.AdSequentialCrystals);
            yield return ("pvp_bg_snake_draft", d.SnakeDraft);
        }

        private static IEnumerable<(string Key, long Value)> LongKeys()
        {
            var d = BattlegroundTunables.Defaults;
            yield return ("pvp_bg_min_players", d.MinPlayers);
            yield return ("pvp_bg_max_players", d.MaxPlayers);
            yield return ("pvp_bg_fill_window_seconds", d.FillWindowSeconds);
            yield return ("pvp_bg_max_premade_size", d.MaxPremadeSize);
            yield return ("pvp_bg_premade_imbalance_tolerance", d.PremadeImbalanceTolerance);
            yield return ("pvp_bg_premade_vs_solo_after_seconds", d.PremadeVsSoloAfterSeconds);
            yield return ("pvp_bg_max_concurrent_matches", d.MaxConcurrentMatches);
            yield return ("pvp_bg_respawn_seconds", d.RespawnSeconds);
            yield return ("pvp_bg_time_limit_seconds_koth", d.TimeLimitSecondsKoth);
            yield return ("pvp_bg_koth_score_target", d.KothScoreTarget);
            yield return ("pvp_bg_koth_tick_seconds", d.KothTickSeconds);
            yield return ("pvp_bg_koth_hold_points", d.KothHoldPoints);
            yield return ("pvp_bg_koth_decay_points", d.KothDecayPoints);
            yield return ("pvp_bg_koth_min_holders", d.KothMinHolders);
            yield return ("pvp_bg_koth_drain_health", d.KothDrainHealth);
            yield return ("pvp_bg_koth_drain_stamina", d.KothDrainStamina);
            yield return ("pvp_bg_koth_drain_mana", d.KothDrainMana);
            yield return ("pvp_bg_koth_score_announce_seconds", d.KothScoreAnnounceSeconds);
            yield return ("pvp_bg_koth_hill_moves", d.KothHillMoves);
            yield return ("pvp_bg_time_limit_seconds_ad", d.TimeLimitSecondsAd);
            yield return ("pvp_bg_ad_crystal_count", d.AdCrystalCount);
            yield return ("pvp_bg_ad_crystal_health_per_attacker", d.AdCrystalHealthPerAttacker);
            yield return ("pvp_bg_ad_respawn_seconds_attackers", d.AdRespawnSecondsAttackers);
            yield return ("pvp_bg_ad_respawn_seconds_defenders", d.AdRespawnSecondsDefenders);
            yield return ("pvp_bg_ad_alert_step_percent", d.AdAlertStepPercent);
            yield return ("pvp_bg_ad_alert_min_interval_seconds", d.AdAlertMinIntervalSeconds);
            yield return ("pvp_bg_ad_status_announce_seconds", d.AdStatusAnnounceSeconds);
            yield return ("pvp_bg_spawn_protection_seconds", d.SpawnProtectionSeconds);
            yield return ("pvp_bg_marks_win", d.MarksWin);
            yield return ("pvp_bg_marks_loss", d.MarksLoss);
            yield return ("pvp_bg_marks_draw", d.MarksDraw);
            yield return ("pvp_bg_marks_daily_cap", d.MarksDailyCap);
        }

        private static IEnumerable<(string Key, double Value)> DoubleKeys()
        {
            var d = BattlegroundTunables.Defaults;
            yield return ("pvp_bg_koth_zone_radius", d.KothZoneRadius);
            yield return ("pvp_bg_koth_zone_height", d.KothZoneHeight);
            yield return ("pvp_bg_koth_marker_spacing", d.KothMarkerSpacing);
            yield return ("pvp_bg_koth_marker_z_offset", d.KothMarkerZOffset);
            yield return ("pvp_bg_dmg_mod", d.DmgMod);
            yield return ("pvp_bg_ad_kill_chip_pct", d.AdKillChipPercent);
            yield return ("pvp_bg_ad_kill_heal_pct", d.AdKillHealPercent);
            yield return ("pvp_bg_ad_kill_range", d.AdKillRange);
            yield return ("pvp_bg_ad_defender_dr_per", d.AdDefenderDrPer);
            yield return ("pvp_bg_ad_defender_dr_cap", d.AdDefenderDrCap);
            yield return ("pvp_bg_ad_defender_dr_radius", d.AdDefenderDrRadius);
            yield return ("pvp_bg_ad_defender_dr_window_s", d.AdDefenderDrWindowSeconds);
            yield return ("pvp_bg_marks_scale", d.MarksScale);
        }

        /// <summary>The marker z offset clamps to the placeable range; NaN falls back to the default (a NaN would otherwise poison every marker z).</summary>
        [TestMethod]
        public void ClampMarkerZOffset_HoldsThePlaceableRange()
        {
            Assert.AreEqual(BattlegroundTunables.MinMarkerZOffset, BattlegroundTunables.ClampMarkerZOffset(-3.0), 0.0);
            Assert.AreEqual(BattlegroundTunables.MaxMarkerZOffset, BattlegroundTunables.ClampMarkerZOffset(50.0), 0.0);
            Assert.AreEqual(-0.1, BattlegroundTunables.ClampMarkerZOffset(-0.1), 0.0);
            Assert.AreEqual(BattlegroundTunables.Defaults.KothMarkerZOffset, BattlegroundTunables.ClampMarkerZOffset(double.NaN), 0.0);
            Assert.IsTrue(BattlegroundTunables.Defaults.KothMarkerZOffset >= BattlegroundTunables.MinMarkerZOffset, "the shipped default is placeable");
        }

        [TestMethod]
        public void Clamp_BoundsTheHillMovesToZeroThroughThree_AndDefaultsToTwo()
        {
            Assert.AreEqual(2, BattlegroundTunables.Defaults.KothHillMoves);
            Assert.AreEqual(0, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothHillMoves = -4 }).KothHillMoves);
            Assert.AreEqual(3, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothHillMoves = 5 }).KothHillMoves, "clamped to 0..3");
            Assert.AreEqual(3, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothHillMoves = 3 }).KothHillMoves);
        }

        [TestMethod]
        public void Clamp_AppliesTheMarkerZOffsetRange()
        {
            var d = BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerZOffset = -9.0 });

            Assert.AreEqual(BattlegroundTunables.MinMarkerZOffset, d.KothMarkerZOffset, 0.0);
        }

        [TestMethod]
        public void Defaults_MatchTheRegisteredBoolDefaults()
        {
            foreach (var (key, value) in BoolKeys())
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(key), $"{key} is not registered as a bool");
                Assert.AreEqual(DefaultPropertyManager.DefaultBooleanProperties[key].Item, value, $"{key}: compiled default and registered default disagree");
            }
        }

        [TestMethod]
        public void Defaults_MatchTheRegisteredLongDefaults()
        {
            foreach (var (key, value) in LongKeys())
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey(key), $"{key} is not registered as a long");
                Assert.AreEqual(DefaultPropertyManager.DefaultLongProperties[key].Item, value, $"{key}: compiled default and registered default disagree");
            }
        }

        [TestMethod]
        public void Defaults_MatchTheRegisteredDoubleDefaults()
        {
            foreach (var (key, value) in DoubleKeys())
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key), $"{key} is not registered as a double");
                Assert.AreEqual(DefaultPropertyManager.DefaultDoubleProperties[key].Item, value, $"{key}: compiled default and registered default disagree");
            }
        }

        [TestMethod]
        public void EveryRegisteredBgKey_IsCoveredByTheDefaultsCheck()
        {
            var covered = new HashSet<string>();
            foreach (var (k, _) in BoolKeys()) covered.Add(k);
            foreach (var (k, _) in LongKeys()) covered.Add(k);
            foreach (var (k, _) in DoubleKeys()) covered.Add(k);

            var registered = new List<string>();
            foreach (var k in DefaultPropertyManager.DefaultBooleanProperties.Keys) if (k.StartsWith("pvp_bg_") && !ACE.Server.Pvp.Rules.PvpContextTuning.IsContextKey(k)) registered.Add(k);
            foreach (var k in DefaultPropertyManager.DefaultLongProperties.Keys) if (k.StartsWith("pvp_bg_") && !ACE.Server.Pvp.Rules.PvpContextTuning.IsContextKey(k)) registered.Add(k);
            foreach (var k in DefaultPropertyManager.DefaultDoubleProperties.Keys) if (k.StartsWith("pvp_bg_") && !ACE.Server.Pvp.Rules.PvpContextTuning.IsContextKey(k)) registered.Add(k);

            Assert.AreEqual(56, registered.Count, "the snake draft switch (pvp_bg_snake_draft) plus 31 keys (25, plus the three zone-marker settings, plus the two team fellowship switches, plus the hill-moves count) plus the seventeen Attack/Defend settings (Docs/Pvp/ATTACK-DEFEND.md 'Settings', the tenth being sequential crystals, then the three kill chip and heal settings, then the four engaged-defender settings) plus the spawn protection window (Docs/Pvp/BATTLEGROUNDS.md 'Spawn protection') plus the battleground damage mod (pvp_bg_dmg_mod) plus the five Mark of the Hopeslayer payout settings (pvp_bg_marks_*, Docs/Pvp/BATTLEGROUNDS.md 'Rewards')");
            foreach (var k in registered)
                Assert.IsTrue(covered.Contains(k), $"{k} is registered but not asserted against BattlegroundTunables.Defaults");
        }

        /// <summary>
        /// Owner ruling 2026-10-04: the KOTH score is ANNOUNCED every 10 s (was 30); the scoring and drain tick stays at 5 s.
        /// Defaults_MatchTheRegisteredLongDefaults ties the compiled default to the registered one; this pins the value.
        /// </summary>
        [TestMethod]
        public void KothScoreAnnounce_Is10Seconds_TickStays5()
        {
            Assert.AreEqual(10, BattlegroundTunables.Defaults.KothScoreAnnounceSeconds);
            Assert.AreEqual(10L, DefaultPropertyManager.DefaultLongProperties["pvp_bg_koth_score_announce_seconds"].Item);
            Assert.AreEqual(5, BattlegroundTunables.Defaults.KothTickSeconds, "the scoring tick is unchanged");
        }

        [TestMethod]
        public void DialSource_InTestEnvironment_FallsBackToDefaults()
        {
            Assert.AreEqual(BattlegroundTunables.Defaults, BattlegroundTunables.DialSource());
        }

        [TestMethod]
        public void ClampMaxPremadeSize_ClampsToSixAndToTheFellowshipCap()
        {
            Assert.AreEqual(6, BattlegroundTunables.ClampMaxPremadeSize(6, 100));
            Assert.AreEqual(6, BattlegroundTunables.ClampMaxPremadeSize(50, 100));
            Assert.AreEqual(4, BattlegroundTunables.ClampMaxPremadeSize(6, 4));
            Assert.AreEqual(3, BattlegroundTunables.ClampMaxPremadeSize(3, 100));
            Assert.AreEqual(1, BattlegroundTunables.ClampMaxPremadeSize(0, 100));
            Assert.AreEqual(1, BattlegroundTunables.ClampMaxPremadeSize(-5, 100));
            Assert.AreEqual(1, BattlegroundTunables.ClampMaxPremadeSize(6, 0));
        }

        [TestMethod]
        public void Clamp_LeavesTheDefaultsUnchanged()
        {
            Assert.AreEqual(BattlegroundTunables.Defaults, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults));
        }

        [TestMethod]
        public void Clamp_RaisesEveryFloor()
        {
            var bad = BattlegroundTunables.Defaults with
            {
                KothScoreTarget = 0, KothTickSeconds = -3, TimeLimitSecondsKoth = 0, RespawnSeconds = -1,
                KothMinHolders = 0, KothHoldPoints = -2, KothDecayPoints = -2, MinPlayers = 1, MaxPlayers = 2, FillWindowSeconds = -10
            };

            var c = BattlegroundTunables.Clamp(bad);

            Assert.AreEqual(1, c.KothScoreTarget, "score target");
            Assert.AreEqual(1, c.KothTickSeconds, "tick");
            Assert.AreEqual(1, c.TimeLimitSecondsKoth, "time limit");
            Assert.AreEqual(0, c.RespawnSeconds, "respawn");
            Assert.AreEqual(1, c.KothMinHolders, "min holders");
            Assert.AreEqual(0, c.KothHoldPoints, "hold");
            Assert.AreEqual(0, c.KothDecayPoints, "decay");
            Assert.AreEqual(4, c.MinPlayers, "min players");
            Assert.AreEqual(4, c.MaxPlayers, "max players floored at min players");
            Assert.AreEqual(0, c.FillWindowSeconds, "fill window");
        }

        /// <summary>The Attack/Defend defaults are the design doc's table, pinned by value (the registered-default tests tie them to PropertyManager).</summary>
        [TestMethod]
        public void AttackDefendDefaults_AreTheDesignTable()
        {
            var d = BattlegroundTunables.Defaults;

            Assert.IsTrue(d.AdEnabled);
            Assert.AreEqual(600, d.TimeLimitSecondsAd);
            Assert.AreEqual(0, d.AdCrystalCount);
            Assert.AreEqual(5000, d.AdCrystalHealthPerAttacker);
            Assert.AreEqual(30, d.AdRespawnSecondsAttackers);
            Assert.AreEqual(30, d.AdRespawnSecondsDefenders);
            Assert.AreEqual(25, d.AdAlertStepPercent);
            Assert.AreEqual(10, d.AdAlertMinIntervalSeconds);
            Assert.AreEqual(60, d.AdStatusAnnounceSeconds);
        }

        [TestMethod]
        public void Clamp_AppliesTheAttackDefendRanges()
        {
            var low = BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with
            {
                TimeLimitSecondsAd = 5, AdCrystalCount = -2, AdCrystalHealthPerAttacker = 0, AdRespawnSecondsAttackers = -1, AdRespawnSecondsDefenders = -1,
                AdAlertStepPercent = 1, AdAlertMinIntervalSeconds = 0, AdStatusAnnounceSeconds = 1
            });

            Assert.AreEqual(60, low.TimeLimitSecondsAd, "time limit floor");
            Assert.AreEqual(0, low.AdCrystalCount, "crystal count floor (0 = map default)");
            Assert.AreEqual(1, low.AdCrystalHealthPerAttacker, "health per attacker floor");
            Assert.AreEqual(0, low.AdRespawnSecondsAttackers);
            Assert.AreEqual(0, low.AdRespawnSecondsDefenders);
            Assert.AreEqual(5, low.AdAlertStepPercent, "alert step floor");
            Assert.AreEqual(1, low.AdAlertMinIntervalSeconds);
            Assert.AreEqual(5, low.AdStatusAnnounceSeconds);

            var high = BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { AdCrystalCount = 9, AdAlertStepPercent = 500 });

            Assert.AreEqual(4, high.AdCrystalCount, "crystal count ceiling");
            Assert.AreEqual(100, high.AdAlertStepPercent, "alert step ceiling");
        }

        [TestMethod]
        public void Clamp_RaisesMarkerSpacingToHalfAMetre_AndKeepsLargerValues()
        {
            Assert.AreEqual(0.5, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerSpacing = 0.1 }).KothMarkerSpacing);
            Assert.AreEqual(0.5, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerSpacing = 0 }).KothMarkerSpacing);
            Assert.AreEqual(0.5, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerSpacing = -3 }).KothMarkerSpacing);
            Assert.AreEqual(0.5, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerSpacing = double.NaN }).KothMarkerSpacing);
            Assert.AreEqual(0.5, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerSpacing = 0.5 }).KothMarkerSpacing);
            Assert.AreEqual(4.0, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { KothMarkerSpacing = 4.0 }).KothMarkerSpacing);
        }

        [TestMethod]
        public void Clamp_CapsMaxPlayersAtTwelve_AndKeepsInRangeValues()
        {
            var c = BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { MinPlayers = 6, MaxPlayers = 40, RespawnSeconds = 0, KothHoldPoints = 0 });

            Assert.AreEqual(12, c.MaxPlayers);
            Assert.AreEqual(6, c.MinPlayers);
            Assert.AreEqual(0, c.RespawnSeconds, "0 is a valid respawn");
            Assert.AreEqual(0, c.KothHoldPoints, "0 is a valid hold");

            var big = BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { MinPlayers = 20, MaxPlayers = 30 });
            Assert.AreEqual(12, big.MinPlayers, "min players cannot exceed the 12 a match holds");
            Assert.AreEqual(12, big.MaxPlayers);
        }
    }
}