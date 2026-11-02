using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Hold goal's settings rows (port of DreamWeave #17's HoldSettings, without the chain). Every row is scoped to
    /// <see cref="HoldScope"/>, so a global caller can never write one and a Hold caller can never write a global row.
    ///
    /// "Not set" rules, per row:
    /// - NegativeIsUnset (property ships at -1): 0 is a real value; not set falls to the goals.json value at JsonPath, or, with
    ///   no JsonPath, to "no preference" (the consumer's own behaviour).
    /// - NonPositiveIsUnset (property ships at 0): for rows whose range starts above 0, so 0 can never be a wanted value.
    /// - AlwaysSet: a plain value whose shipped default is the code default.
    /// - EmptyIsUnset (string property ships empty): Choice and Text rows; on/off values that fall back to goals.json are
    ///   Choice rows {on, off} so that "not set" exists for them.
    ///
    /// Applies: the framework has Live and NextRun. A value read when the next wave, spawn or stage end happens is Live here
    /// (its consumer re-reads the resolver at that moment) and says so in its description: "(next wave)", "(next spawn)",
    /// "(at stage end)".
    ///
    /// The typed properties behind these rows are generated from them (<see cref="GoalScopedLongDefaults"/> and friends,
    /// wired in DefaultPropertyManager), so the key list and the shipped defaults have exactly one source.
    /// </summary>
    public sealed partial class WorldEventSettingsRegistry
    {
        /// <summary>The goal type the Hold rows are scoped to.</summary>
        public const string HoldGoalType = "hold";

        public static readonly SettingScope HoldScope = SettingScope.Goal(HoldGoalType);

        public const string GroupHold = "hold";
        public const string GroupKnights = "knights";
        public const string GroupHealing = "healing";
        public const string GroupMonsters = "monsters";
        public const string GroupFinale = "finale";
        public const string GroupInternals = "internals";
        public const string GroupParticipation = "participation";

        public const string On = "on", Off = "off";

        private static readonly string[] OnOff = { On, Off };

        /// <summary>When a Hold value is first read; mapped onto <see cref="SettingApplies"/> plus a description note.</summary>
        private enum When { Live, NextWave, NextSpawn, NextRun, StageEnd }

        private static WorldEventSettingRow H(string group, string key, SettingKind kind, string codeDefault, double min, double max,
            SettingUnsetRule unset, When when, string json, string description, string[] choices = null,
            System.Func<string, (string Normalized, string Error)> parse = null)
        {
            string note;

            switch (when)
            {
                case When.NextWave: note = " (next wave)"; break;
                case When.NextSpawn: note = " (next spawn)"; break;
                case When.StageEnd: note = " (at stage end)"; break;
                default: note = ""; break;
            }

            return new WorldEventSettingRow
            {
                Key = key, PropertyName = K.PropertyPrefix + key, Group = group, Kind = kind, Scope = HoldScope, CodeDefault = codeDefault,
                Min = min, Max = max, Unset = unset, JsonPath = json, Choices = choices, ParseValue = parse,
                Applies = when == When.NextRun ? SettingApplies.NextRun : SettingApplies.Live,
                Description = description + note
            };
        }

        // Shorthands: a goals.json-backed number where 0 is a real value; one whose range starts above 0; a plain value.
        private static WorldEventSettingRow Neg(string group, string key, SettingKind kind, double min, double max, When when, string json, string description) =>
            H(group, key, kind, "-1", min, max, SettingUnsetRule.NegativeIsUnset, when, json, description);

        private static WorldEventSettingRow Pos(string group, string key, SettingKind kind, double min, double max, When when, string json, string description,
            System.Func<string, (string Normalized, string Error)> parse = null) =>
            H(group, key, kind, "0", min, max, SettingUnsetRule.NonPositiveIsUnset, when, json, description, null, parse);

        private static WorldEventSettingRow Val(string group, string key, SettingKind kind, string def, double min, double max, When when, string description) =>
            H(group, key, kind, def, min, max, SettingUnsetRule.AlwaysSet, when, null, description);

        private static WorldEventSettingRow Flag(string group, string key, bool def, When when, string description) =>
            H(group, key, SettingKind.Bool, def ? "True" : "False", 0, 1, SettingUnsetRule.AlwaysSet, when, null, description);

        private static WorldEventSettingRow Pick(string group, string key, string def, string[] choices, When when, string json, string description) =>
            H(group, key, SettingKind.Choice, def, 0, 0, SettingUnsetRule.EmptyIsUnset, when, json, description, choices);

        private static WorldEventSettingRow Txt(string group, string key, string def, When when, string json, string description,
            System.Func<string, (string Normalized, string Error)> parse = null) =>
            H(group, key, SettingKind.Text, def, 0, 0, SettingUnsetRule.EmptyIsUnset, when, json, description, null, parse);

        private const SettingKind D = SettingKind.Double, L = SettingKind.Long;

        internal static IEnumerable<WorldEventSettingRow> HoldRows()
        {
            // ---- master switch: OFF by default (owner ruling; an exception to the new-settings-default-on rule) ----
            yield return Flag(GroupHold, K.Enabled, false, When.NextRun,
                "Hold goals may start. Off (the default) refuses Hold goals and keeps random rolls off them.");

            // ---- knights ----
            yield return Pos(GroupKnights, K.KnightCount, L, 1, 3, When.NextRun, "protect.count.base", "How many knights are placed (at most ProtectDef.MaxProtectedTargets, 3).");
            yield return Val(GroupKnights, K.KnightMaxHealth, L, "10000", 1000, 100000, When.Live,
                "Each knight's max health (time-based damage; default ProtectDef.TimeScaledMaxHealth). How long a knight lasts is set by undefended_seconds and min_death_seconds, not by this.");
            yield return Txt(GroupKnights, K.KnightMaxHealthByWcid, "", When.Live, null,
                "Max health per knight, by wcid: \"1007650=12000,1007652=8000\" (the knights are 1007650 to 1007652). A knight not listed uses knight_max_health. Each value 1000 to 100000.",
                WorldEventHoldRules.CheckKnightHealthByWcid);
            yield return Val(GroupKnights, K.KnightDamageTakenMult, D, "1", 0, 5, When.Live,
                "Multiplies every monster hit on a knight (the finale boss's included), before the min_death_seconds cap. 1 = unchanged.");
            yield return Pos(GroupKnights, K.UndefendedSeconds, D, 20, 3600, When.Live, "protect.undefendedSeconds", "Seconds one attacker alone takes to destroy a knight (solo).");
            yield return Neg(GroupKnights, K.UndefendedSecondsPerParticipant, D, 0, 600, When.Live, "protect.undefendedSecondsPerParticipant", "Seconds taken off undefended_seconds per player beyond the first.");
            yield return Neg(GroupKnights, K.UndefendedSecondsFloor, D, 0, 3600, When.Live, "protect.undefendedSecondsFloor", "The lowest undefended_seconds can go with more players.");
            yield return Neg(GroupKnights, K.MinDeathSeconds, D, 0, 600, When.Live, "protect.minDeathSeconds", "However many monsters pile on, a knight lasts at least this long from full health (0 = no floor).");
            yield return Pos(GroupKnights, K.MinSurvivors, L, 1, 3, When.Live, "protect.minSurvivors",
                "The fail condition: the run fails when fewer knights than this stand. 1 = fails when all are dead; 3 (clamped to the knights placed) = fails at the first death. Also accepts all (1) and any (3).",
                WorldEventHoldRules.CheckMinSurvivors);
            yield return Pick(GroupKnights, K.DefendPose, On, OnOff, When.Live, "protect.defendPose", "The knights draw weapons and swing at monsters in reach (cosmetic only). Not set: goals.json decides.");
            yield return Pos(GroupKnights, K.DefendSwingSeconds, D, 1, 10, When.Live, "protect.defendPose.swingSeconds", "Shortest time between two cosmetic swings.");
            yield return Pos(GroupKnights, K.DefendSwingMaxSeconds, D, 1, 10, When.Live, "protect.defendPose.swingMaxSeconds", "Longest time between two cosmetic swings (never below defend_swing_seconds).");
            yield return Pick(GroupKnights, K.HealthMilestones, On, OnOff, When.Live, "protect.healthMilestones", "A world line as each knight first drops below each milestone_percents step. Not set: goals.json decides.");

            // ---- healing ----
            yield return Pick(GroupHealing, K.Shrine, On, OnOff, When.Live, "protect.shrine", "The Shrines of Restoration. Off: using one does nothing; shrines are placed at the start. Not set: goals.json decides.");
            yield return Neg(GroupHealing, K.ShrineHeal, D, 0, 1, When.Live, "protect.shrineHeal", "Share of max health a shrine use restores.");
            yield return Neg(GroupHealing, K.ShrineCooldownSeconds, D, 0, 600, When.Live, "protect.shrineCooldownSeconds", "Seconds a shrine rests after a use (0 allowed here; goals.json keeps its own 1 to 600).");
            yield return Val(GroupHealing, K.ShrineCount, L, "3", 0, 3, When.NextRun, "How many knights get a shrine (the first N; 3 = every knight).");
            yield return Pos(GroupHealing, K.ShrineUseRadius, D, 1, 10, When.NextRun, "protect.shrine.useRadius", "How far away a player may use a shrine (set on the shrine when it is placed).");
            yield return Flag(GroupHealing, K.ShrineHealsAll, false, When.Live, "A shrine use heals every standing knight instead of only the one beside it.");
            yield return Val(GroupHealing, K.ShrinePlayerCooldownSeconds, D, "0", 0, 600, When.Live, "One player waits this long between any two shrine uses (0 = off).");
            yield return Val(GroupHealing, K.ShrineBindRange, D, "8", 2, 20, When.Live, "A shrine restores the nearest standing knight within this many metres (default ProtectDef.DefaultShrineBindRange).");
            yield return Neg(GroupHealing, K.CorruptedKillHeal, D, 0, 1, When.Live, "protect.corruptedKillHeal", "Share of max health every standing knight regains when a corrupted monster dies.");
            yield return Neg(GroupHealing, K.RescueHeal, D, 0, 1, When.Live, "protect.rescueHeal", "Share a knight regains when a monster attacking it is killed by a player or pet.");
            yield return Neg(GroupHealing, K.RescueCapPerWave, D, 0, 1, When.Live, "protect.rescueCapPerWave", "The most a knight regains from rescues per wave.");
            yield return Neg(GroupHealing, K.RegenPerSecond, D, 0, 0.1, When.Live, "protect.clearRegenPerSecond", "Share of max health a knight with no monster near regenerates per second (0 = off).");
            yield return Neg(GroupHealing, K.RegenClearRadius, D, 0, 50, When.Live, "protect.clearRadius", "A knight regenerates only with no wave monster within this many metres.");
            yield return Val(GroupHealing, K.RegenIntervalSeconds, D, "1", 1, 30, When.Live, "Regeneration is applied every this many seconds (the same amount per second overall).");
            yield return Flag(GroupHealing, K.PlayerHeal, true, When.Live, "Players may heal knights with heal spells and kits. Off: their heals restore nothing.");
            yield return Neg(GroupHealing, K.PlayerHealCap, D, 0, 1, When.Live, "protect.maxHealPerTenSeconds",
                "The most player heals restore on one knight per 10 s, as a share of max health (0 = off; goals.json default ProtectDef.DefaultMaxHealPerTenSeconds 0.1).");
            yield return Val(GroupHealing, K.PlayerHealMult, D, "1", 0, 5, When.Live, "Scales what one player heal is worth on a knight, before the cap.");
            yield return Neg(GroupHealing, K.HealReductionAfterHit, D, 0, 1, When.Live, "protect.healReductionAfterHit", "How much healing on a just-hit knight is reduced.");
            yield return Neg(GroupHealing, K.HealReductionSeconds, D, 0, 60, When.Live, "protect.healReductionSeconds", "How long after a hit the reduction lasts.");
            yield return Neg(GroupHealing, K.HealBetweenWaves, D, 0, 1, When.Live, "protect.healBetweenWaves", "Share every standing knight regains when a wave is fully cleared.");
            yield return Neg(GroupHealing, K.HealBudgetPerWave, D, 0, 1, When.Live, "protect.healBudgetPerWave", "The most a knight regains per wave from every source together (0 = no budget).");

            // ---- monsters: the wave mix and the goals.json role values ----
            yield return Pos(GroupMonsters, K.SeekShare, D, 0, 1, When.NextWave, "protect.seekShare", "Share of each wave that goes straight for the knights.");
            yield return Neg(GroupMonsters, K.ShareBreach, D, 0, 1, When.NextWave, "protect.waveMix.breach", "Share of each mixed wave that is the breach squad (melee, all on one knight).");
            yield return Neg(GroupMonsters, K.ShareRunners, D, 0, 1, When.NextWave, "protect.waveMix.runners", "Share of each mixed wave that is runners.");
            yield return Neg(GroupMonsters, K.ShareRanged, D, 0, 1, When.NextWave, "protect.waveMix.ranged", "Share of each mixed wave that is ranged (archers and casters).");
            yield return Neg(GroupMonsters, K.ShareHunters, D, 0, 1, When.NextWave, "protect.waveMix.hunters", "Share of each mixed wave that hunts players (melee).");
            yield return Neg(GroupMonsters, K.ShareSeekers, D, 0, 1, When.NextWave, "protect.waveMix.seekers", "Share of each mixed wave that goes for any knight (melee).");
            yield return Neg(GroupMonsters, K.ShareCorrupted, D, 0, 1, When.NextWave, "protect.waveMix.corrupted", "Share of each mixed wave that is corrupted (when assaults are off).");
            yield return Neg(GroupMonsters, K.MinBreach, L, 0, 10, When.NextWave, "protect.waveMix.minBreach", "At least this many in the breach squad.");
            yield return Neg(GroupMonsters, K.MinRanged, L, 0, 20, When.NextWave, "protect.waveMix.minRanged", "At least this many ranged.");
            yield return Neg(GroupMonsters, K.MinCorrupted, L, 0, 5, When.NextWave, "protect.waveMix.minCorrupted", "At least this many corrupted (share mode).");
            yield return Neg(GroupMonsters, K.MaxCorrupted, L, 0, 10, When.NextWave, "protect.waveMix.maxCorrupted", "At most this many corrupted (share mode).");
            yield return Pos(GroupMonsters, K.RunnerHealthMult, D, 0.1, 1, When.NextWave, "protect.specialWaves[type=runners].healthMult", "Runner health as a share of normal.");
            yield return Pos(GroupMonsters, K.RunnerSpeedMult, D, 1, 3, When.NextWave, "protect.specialWaves[type=runners].speedMult", "Runner movement speed.");
            yield return Pos(GroupMonsters, K.RunnerAttackSpeedMult, D, 1, 3, When.NextWave, "protect.specialWaves[type=runners].attackSpeedMult", "Runner swing speed (and how fast its hits wear a knight down).");
            yield return Pos(GroupMonsters, K.RangedKeepDistance, D, 5, 40, When.NextWave, "protect.specialWaves[type=ranged].holdDistance", "Metres ranged and caster monsters stop at from their target.");
            yield return Pos(GroupMonsters, K.CorruptedHealthMult, D, 1, 5, When.NextSpawn, "protect.corrupted.healthMult", "Corrupted health multiplier.");
            yield return Pos(GroupMonsters, K.CorruptedSelfMult, D, 1, 10, When.Live, "protect.corrupted.selfMult", "How much harder a corrupted monster hits a knight (still under min_death_seconds).");
            yield return Pos(GroupMonsters, K.CorruptedEmpowerMult, D, 1, 5, When.Live, "protect.corrupted.empowerMult", "How much harder monsters near a corrupted one hit a knight.");
            yield return Neg(GroupMonsters, K.CorruptedAssaultEveryWaves, L, 0, 10, When.NextWave, "protect.corrupted.assaultEveryWaves", "Corrupted assault every this many waves (0 = corrupted come by share_corrupted instead).");
            yield return Pos(GroupMonsters, K.CorruptedAssaultFromWave, L, 1, 10, When.NextWave, "protect.corrupted.assaultFromWave", "The first assault wave.");
            yield return Pos(GroupMonsters, K.CorruptedAssaultCount, L, 1, 5, When.NextWave, "protect.corrupted.assaultCount", "Corrupted monsters per assault (each sent at one knight).");
            yield return Txt(GroupMonsters, K.CorruptedTint, "none", When.NextSpawn, "protect.corrupted.tintPalette",
                "The corrupted color: a client palette (0x04001381 cyan, 0x04001378 crimson, 0x0400137D violet, 0x0400137A green) or none.",
                WorldEventHoldRules.CheckCorruptedTint);

            // ---- monsters: per role ----
            foreach (var role in K.RoleNames)
            {
                var name = char.ToUpperInvariant(role[0]) + role.Substring(1);

                // runner_health_mult and corrupted_health_mult are the goals.json rows above.
                if (role != "runner" && role != "corrupted")
                    yield return Neg(GroupMonsters, K.Role(role, K.RoleHealthMult), D, 0.1, 10, When.NextSpawn, null,
                        $"{name} monsters' health multiplier. Not set: no change.");

                yield return Val(GroupMonsters, K.Role(role, K.RolePlayerDamageMult), D, "1", 0, 5, When.Live, $"Multiplies {role} monsters' damage to players (melee, missile and spells).");
                yield return Val(GroupMonsters, K.Role(role, K.RoleNpcDamageMult), D, "1", 0, 5, When.Live, $"Multiplies {role} monsters' damage to the knights, before the min_death_seconds cap.");

                // runner_attack_speed_mult is the goals.json row above.
                if (role != "runner")
                    yield return Neg(GroupMonsters, K.Role(role, K.RoleAttackSpeedMult), D, 1, 3, When.NextSpawn, null,
                        $"{name} monsters' melee swing speed (and how fast their hits wear a knight down). Not set: no change.");

                yield return Val(GroupMonsters, K.Role(role, K.RoleLevelOffset), L, "0", -50, 50, When.NextWave,
                    $"{name} slots of a mixed wave are drawn from the event's level band moved by this many levels (0 = no move).");
            }

            yield return Neg(GroupMonsters, K.CasterShareOfRanged, D, 0, 1, When.NextWave, null,
                "Share of the ranged slots filled by casters when the family has both casters and archers. Not set: no preference.");

            // ---- waves ----
            yield return Pos(GroupWaves, K.WaveCountBase, L, 1, 20, When.NextRun, "protect.waves.base", "Waves the group must survive (the minimum wave count).");
            yield return Neg(GroupWaves, K.WaveCountPerPlayer, D, 0, 5, When.NextRun, "protect.waves.perParticipant", "Extra waves per player.");
            yield return Pos(GroupWaves, K.WaveCountCap, L, 1, 20, When.NextRun, "protect.waves.cap", "The most waves (also limited by max_waves).");
            yield return Val(GroupWaves, K.MaxWaves, L, "10", 1, 20, When.NextRun, "Hard ceiling on waves for any hold (default ProtectDef.MaxHoldWaves).");
            yield return Pos(GroupWaves, K.WaveSizeBase, L, 1, 50, When.NextWave, "protect.waveSize.base", "Monsters in a wave before any player is counted (the base wave size).");
            yield return Neg(GroupWaves, K.WaveSizePerPlayer, D, 0, 20, When.NextWave, "protect.waveSize.perParticipant", "Extra monsters per player (per-player growth).");
            yield return Pos(GroupWaves, K.WaveSizeCap, L, 1, 200, When.NextWave, "protect.waveSize.cap", "The largest a wave gets (the max size).");
            yield return Val(GroupWaves, K.WaveSizeBasePlayers, L, "0", 0, 35, When.NextWave, "The player count the base wave size covers: growth starts after this many players (0 = from the first).");
            yield return Val(GroupWaves, K.WaveSizeMaxPlayers, L, "0", 0, 100, When.NextWave, "Growth stops at this many players (0 = no limit; the cap still applies).");
            yield return Val(GroupWaves, K.FinalWaveBonus, D, "0.25", 0, 2, When.NextWave, "The final wave is this share bigger, at least one more (0 = no bonus).");
            yield return Val(GroupWaves, K.FirstWaveDelaySeconds, D, "5", 0, 120, When.Live,
                "The first wave may come once the knights stand, or this long after the start (default 5 s).");
            yield return Neg(GroupWaves, K.WaveIntervalSeconds, D, 0, 600, When.Live, "protect.waveIntervalSeconds", "The next wave comes after this long (wave interval).");
            yield return Neg(GroupWaves, K.BreatherSeconds, D, 0, 60, When.Live, "protect.minWaveGapSeconds",
                "Never two waves closer together than this (goals.json default ProtectDef.DefaultMinWaveGapSeconds 8).");
            yield return Neg(GroupWaves, K.MaxWaveIntervalSeconds, D, 0, 600, When.Live, "protect.maxWaveIntervalSeconds", "The next wave comes after this long whatever is alive (0 = no ceiling).");
            yield return Neg(GroupWaves, K.NextWaveAliveShare, D, 0, 1, When.Live, "protect.nextWaveAliveShare",
                "The next wave may come early once no more than this share of the last is alive (goals.json default ProtectDef.DefaultNextWaveAliveShare 0.25).");
            yield return Neg(GroupWaves, K.SpawnRadius, D, 0, 80, When.NextWave, "protect.spawnRadius", "Metres from the anchor the waves spawn at (without portals).");
            yield return Pos(GroupWaves, K.PortalCount, L, 1, 8, When.NextRun, "protect.portals.count", "Spawn portals placed around the ring.");
            yield return Pos(GroupWaves, K.PortalRadius, D, 5, 80, When.NextRun, "protect.portals.radius", "Metres from the anchor the portals stand.");
            yield return Pos(GroupWaves, K.HoldSeconds, L, 60, 3600, When.Live, "holdSeconds", "Reaching this with enough knights standing is a win (the hold timer). Must fit inside max_seconds.");
            yield return Pos(GroupWaves, K.MaxSeconds, L, 360, 3600, When.NextRun, null,
                "The event time limit for a Hold run. Not set: the run's own limit (WorldEvent.DefaultMaxDurationSeconds, 600). At least WorldEvent.DefaultMinDurationSeconds + 60.");
            yield return Neg(GroupWaves, K.AbandonSeconds, L, 0, 3600, When.NextRun, null,
                "A Hold run with nobody present ends after this long (0 = never). Not set: the run's own timer (WorldEvent.DefaultAbandonAfterSeconds, 90). --abandon still wins for one run.");

            // ---- the finale boss ----
            yield return Pos(GroupFinale, K.FinaleHealthMult, D, 1, 10, When.NextSpawn, "protect.finaleBossHealthMult",
                "The finale boss's extra health multiplier (goals.json default ProtectDef.DefaultFinaleBossHealthMult 3).");
            yield return Pos(GroupFinale, K.FinaleHealth, L, 1000, 10000000, When.NextSpawn, null, "A flat max health for the finale boss in place of the multiplier. Not set: the multiplier.");
            yield return Neg(GroupFinale, K.FinaleScale, D, 0, 2.5, When.NextSpawn, "protect.finaleBossScale",
                "The finale boss's size: 0 = as tall as the named bosses, else 1 to 2.5 (goals.json default ProtectDef.DefaultFinaleBossScale 1.75).");
            yield return Neg(GroupFinale, K.FinaleMinKillSeconds, D, 0, 300, When.Live, "protect.bossMinKillSeconds",
                "Its damage to the knights: alone it needs this long to empty one (0 = no cap of its own; goals.json default ProtectDef.DefaultBossMinKillSeconds 50).");
            yield return Neg(GroupFinale, K.FinaleLeadSeconds, D, 0, 30, When.NextWave, "protect.finaleBossLeadSeconds",
                "It stands alone this long before the rest of the final wave (goals.json default ProtectDef.DefaultFinaleBossLeadSeconds 5).");
            yield return Pick(GroupFinale, K.FinaleTarget, "", new[] { ProtectDef.FinaleTargetNpcs, ProtectDef.FinaleTargetPlayers, ProtectDef.FinaleTargetBoth }, When.NextSpawn, "protect.bossTargetsProtected",
                "Who it goes for: npcs (the knights, with the swap and pull), players, or both (whichever is nearer). Not set: goals.json bossTargetsProtected (true = npcs; false = the boss's own seeking, no choice).");
            yield return Pos(GroupFinale, K.FinaleSwapSeconds, D, 5, 120, When.Live, "protect.bossSwapSeconds", "It turns on another knight after this long at the least.");
            yield return Pos(GroupFinale, K.FinaleSwapMaxSeconds, D, 5, 120, When.Live, "protect.bossSwapMaxSeconds", "It turns on another knight after this long at the most.");
            yield return Neg(GroupFinale, K.FinalePullShare, D, 0, 0.5, When.Live, "protect.bossPullShare", "Players pull it off a knight by dealing this share of its health in the window (0 = only a taunt).");
            yield return Pos(GroupFinale, K.FinalePullWindowSeconds, D, 2, 30, When.Live, "protect.bossPullWindowSeconds", "The pull window.");
            yield return Pos(GroupFinale, K.FinalePullSeconds, D, 3, 60, When.Live, "protect.bossPullSeconds", "How long a pull holds it.");
            yield return Val(GroupFinale, K.FinalePlayerDamageMult, D, "1", 0, 5, When.Live, "Multiplies its damage to players.");
            yield return Val(GroupFinale, K.FinaleLevelOffset, L, "0", -50, 50, When.NextSpawn, "It is picked as if the players were this many levels higher (negative: lower).");
            yield return Val(GroupFinale, K.FinaleAddCount, L, "0", 0, 10, When.Live, "While it lives, this many adds join it every finale_add_interval_seconds (0 = none).");
            yield return Val(GroupFinale, K.FinaleAddIntervalSeconds, D, "30", 5, 300, When.Live, "Seconds between waves of adds.");
            yield return Val(GroupFinale, K.FinaleEnrageSeconds, D, "0", 0, 1800, When.Live, "After this long alive it enrages: its damage is multiplied by finale_enrage_mult (0 = never).");
            yield return Val(GroupFinale, K.FinaleEnrageMult, D, "1.5", 1, 5, When.Live, "The enraged damage multiplier.");
            yield return Pos(GroupFinale, K.FinaleBossWcid, L, 0, int.MaxValue, When.NextSpawn, null, "A forced finale boss weenie (wcid). Not set or 0: the family champion.");

            // ---- internals: constants exposed as settings (defaults are the values the Hold engine slices will use) ----
            yield return Val(GroupInternals, K.FinaleFallbackScale, D, "3.08", 0.5, 12, When.NextSpawn,
                "The finale boss's size when its height cannot be read (finale_scale 0; default 3.08).");
            yield return Val(GroupInternals, K.FinaleHeightScaleMin, D, "0.5", 0.1, 5, When.NextSpawn, "The smallest the height match may scale the finale boss (default 0.5).");
            yield return Val(GroupInternals, K.FinaleHeightScaleMax, D, "12", 1, 20, When.NextSpawn, "The largest the height match may scale the finale boss (default 12).");
            yield return Val(GroupInternals, K.BossDamageKeepSeconds, D, "30", 10, 120, When.Live, "How long player damage on the finale boss is remembered for the pull.");
            yield return Val(GroupInternals, K.HitIntervalSeconds, D, "2", 0.5, 10, When.Live,
                "A monster's first hit on a knight counts as this many seconds of attacking (default 2 s).");
            yield return Val(GroupInternals, K.HitIntervalMaxSeconds, D, "4", 1, 20, When.Live, "The longest gap one hit on a knight may count for (default 4 s).");
            yield return Val(GroupInternals, K.CapBurstSeconds, D, "2", 0, 10, When.Live, "Burst allowed on top of the min_death_seconds rate, in seconds of it (default 2 s).");
            yield return Val(GroupInternals, K.SpawnClearance, D, "10", 0, 50, When.NextWave, "Waves spawn at least this many metres outside the knights' ring (default ProtectDef.MinSpawnClearance).");
            yield return Val(GroupInternals, K.RangedSlack, D, "1.5", 0, 10, When.Live, "A ranged monster closes in again once this many metres beyond its keep-distance (default 1.5 m).");
            yield return Val(GroupInternals, K.HealerMemorySeconds, D, "20", 0, 120, When.Live, "How long a player who healed a knight stays a hunter target (default 20 s).");
            yield return Val(GroupInternals, K.WaveResolveGraceSeconds, D, "15", 5, 120, When.Live,
                "A wave the spawner never reported counts as resolved after this long (default 15 s).");

            // ---- rewards (the chain's reward rows are a later slice) ----
            yield return Pos(GroupRewards, K.ParticipantsPerCache, L, 1, 100, When.StageEnd, null,
                "Caches per player: one cache for every this many players present (1 = a cache each). Not set: the reward profile's participantsPerCache.");
            yield return Val(GroupRewards, K.MinContribution, D, "0", 0, 0.5, When.Live,
                "The share of the run's damage a player must deal for the crate; below it they get the coal prize (0 = off).");
            yield return Pick(GroupRewards, K.FailureReward, "consolation", new[] { "consolation", "partial", "none" }, When.Live, null,
                "What a failed run's cache pays: consolation (the consolation crate), partial (the success crate, no bonus) or none.");

            // ---- participation ----
            yield return Pos(GroupParticipation, K.ParticipantRadius, D, 10, 300, When.Live, null,
                "Players within this many metres of the anchor count: presence, audience scaling, local lines and the cache. Not set: the source's rewardRadius.");
            yield return Val(GroupParticipation, K.MinPresenceSeconds, D, "0", 0, 1800, When.Live,
                "A claimant needs this much (active) time inside the radius during the run for the crate; less gets the coal prize (0 = off).");
            yield return Val(GroupParticipation, K.AfkSeconds, D, "0", 0, 1800, When.Live,
                "A player who has not moved or dealt damage for this long (or is /afk) stops counting: not in the audience scaling, and idle time is not presence (0 = off). Hold runs only.");
            yield return Flag(GroupParticipation, K.AdminsCount, false, When.Live, "Admins and other staff count toward the audience scaling and presence (off: never).");

            // ---- messages ----
            yield return Pick(GroupMessages, K.Broadcast, "world", new[] { "world", "local" }, When.Live, null,
                "World Event lines during a Hold run: world, or local (only players within the participant radius). The teaser, start, outcome and time-left lines stay world-wide.");
            yield return Txt(GroupMessages, K.MilestonePercents, "75,50,25", When.Live, null,
                "The knights' health milestone lines, in percent (e.g. \"80,60,40,20\"; \"none\" = no lines).",
                WorldEventHoldRules.CheckMilestonePercents);
        }

        // ---- the typed properties behind the goal-scoped rows ----------------------------------------------------

        private static IEnumerable<WorldEventSettingRow> GoalRows(params SettingKind[] kinds) =>
            Default.Rows.Where(r => !r.Scope.IsGlobal && kinds.Contains(r.Kind));

        /// <summary>The shipped value of a row's property: the code default for a number or bool row, empty for a Choice or Text row.</summary>
        public static string PropertyDefaultText(WorldEventSettingRow r) =>
            r.Kind == SettingKind.Choice || r.Kind == SettingKind.Text ? "" : r.CodeDefault;

        internal static IEnumerable<(string Key, Property<long> Property)> GoalScopedLongDefaults() =>
            GoalRows(SettingKind.Long).Select(r => (r.PropertyName, new Property<long>(long.Parse(r.CodeDefault, CultureInfo.InvariantCulture), PropertyDescription(r))));

        internal static IEnumerable<(string Key, Property<double> Property)> GoalScopedDoubleDefaults() =>
            GoalRows(SettingKind.Double).Select(r => (r.PropertyName, new Property<double>(double.Parse(r.CodeDefault, CultureInfo.InvariantCulture), PropertyDescription(r))));

        internal static IEnumerable<(string Key, Property<bool> Property)> GoalScopedBoolDefaults() =>
            GoalRows(SettingKind.Bool).Select(r => (r.PropertyName, new Property<bool>(bool.Parse(r.CodeDefault), PropertyDescription(r))));

        internal static IEnumerable<(string Key, Property<string> Property)> GoalScopedStringDefaults() =>
            GoalRows(SettingKind.Choice, SettingKind.Text).Select(r => (r.PropertyName, new Property<string>("", PropertyDescription(r))));

        /// <summary>The server property's description, built from the row so the two never disagree.</summary>
        public static string PropertyDescription(WorldEventSettingRow r)
        {
            var scope = r.Scope.IsGlobal ? "World Events" : $"World Events {r.Scope.GoalType} goal";
            string unset;

            switch (r.Unset)
            {
                case SettingUnsetRule.NegativeIsUnset:
                    unset = r.JsonPath != null ? $"-1 = not set (uses goals.json {r.JsonPath}); 0 is a value." : "-1 = not set (no preference: the code's own behaviour); 0 is a value.";
                    break;
                case SettingUnsetRule.NonPositiveIsUnset:
                    unset = r.JsonPath != null ? $"0 = not set (uses goals.json {r.JsonPath})." : "0 = not set (the code's own value).";
                    break;
                case SettingUnsetRule.EmptyIsUnset:
                    unset = r.JsonPath != null ? $"Empty = not set (uses goals.json {r.JsonPath})." : $"Empty = not set (uses {(r.CodeDefault.Length == 0 ? "none" : r.CodeDefault)}).";
                    break;
                default:
                    unset = "";
                    break;
            }

            return $"(non-retail function) {scope} ({r.Group}): {r.Description} Range {r.RangeText}. {unset} Applies: {WorldEventSettingRow.AppliesText(r.Applies)}. Scope {r.Scope}".Replace("  ", " ");
        }
    }
}
