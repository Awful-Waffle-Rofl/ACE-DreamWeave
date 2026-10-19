using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>The stage 1 finale boss and the stage 2 champion, set separately.</summary>
    public static partial class HoldSettings
    {
        private static readonly string[] FinaleTargets = { ProtectDef.FinaleTargetNpcs, ProtectDef.FinaleTargetPlayers, ProtectDef.FinaleTargetBoth };

        static partial void RegisterBosses(List<HoldSetting> r)
        {
            // ---- the finale (stage 1) ----
            r.Add(Num(GroupFinale, HoldKeys.FinaleHealthMult, ProtectDef.DefaultFinaleBossHealthMult, 1, 10, HoldApplies.NextSpawn, "protect.finaleBossHealthMult", false,
                "The finale boss's extra health multiplier.", g => F(g.Protect?.FinaleBossHealthMult ?? 0)));
            r.Add(Int(GroupFinale, HoldKeys.FinaleHealth, 0, 1000, 10000000, HoldApplies.NextSpawn, null, true,
                "A flat max health for the finale boss in place of the multiplier. Not set: the multiplier."));
            r.Add(Num(GroupFinale, HoldKeys.FinaleScale, ProtectDef.DefaultFinaleBossScale, 0, 2.5, HoldApplies.NextSpawn, "protect.finaleBossScale", false,
                "The finale boss's size: 0 = as tall as the named bosses, else 1 to 2.5.", g => F(g.Protect?.FinaleBossScale ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinaleMinKillSeconds, ProtectDef.DefaultBossMinKillSeconds, 0, 300, HoldApplies.Live, "protect.bossMinKillSeconds", false,
                "Its damage to the knights: alone it needs this long to empty one (0 = no cap of its own; else 20 to 300).", g => F(g.Protect?.BossMinKillSeconds ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinaleLeadSeconds, ProtectDef.DefaultFinaleBossLeadSeconds, 0, 30, HoldApplies.NextWave, "protect.finaleBossLeadSeconds", false,
                "It stands alone this long before the rest of the final wave.", g => F(g.Protect?.FinaleBossLeadSeconds ?? 0)));
            r.Add(Pick(GroupFinale, HoldKeys.FinaleTarget, ProtectDef.FinaleTargetNpcs, FinaleTargets, HoldApplies.NextSpawn, "protect.bossTargetsProtected", false,
                "Who it goes for: npcs (the knights, with the swap and pull), players, or both (whichever is nearer).",
                g => g.Protect?.FinaleTargetMode));
            r.Add(Num(GroupFinale, HoldKeys.FinaleSwapSeconds, 15, 5, 120, HoldApplies.Live, "protect.bossSwapSeconds", false,
                "It turns on another knight after this long at the least.", g => F(g.Protect?.BossSwapSeconds ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinaleSwapMaxSeconds, 20, 5, 120, HoldApplies.Live, "protect.bossSwapMaxSeconds", false,
                "... and at the most.", g => F(g.Protect?.BossSwapMaxSeconds ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinalePullShare, 0.06, 0, 0.5, HoldApplies.Live, "protect.bossPullShare", false,
                "Players pull it off a knight by dealing this share of its health in the window (0 = only a taunt).", g => F(g.Protect?.BossPullShare ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinalePullWindowSeconds, 8, 2, 30, HoldApplies.Live, "protect.bossPullWindowSeconds", false,
                "The pull window.", g => F(g.Protect?.BossPullWindowSeconds ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinalePullSeconds, 10, 3, 60, HoldApplies.Live, "protect.bossPullSeconds", false,
                "How long a pull holds it.", g => F(g.Protect?.BossPullSeconds ?? 0)));
            r.Add(Num(GroupFinale, HoldKeys.FinalePlayerDamageMult, 1, 0, 5, HoldApplies.Live, null, true,
                "Multiplies its damage to players."));
            r.Add(Int(GroupFinale, HoldKeys.FinaleLevelOffset, 0, RoleLevelOffsetMin, RoleLevelOffsetMax, HoldApplies.NextSpawn, null, true,
                "It is picked as if the players were this many levels higher (negative: lower)."));
            r.Add(Int(GroupFinale, HoldKeys.FinaleAddCount, 0, 0, 10, HoldApplies.Live, null, true,
                "While it lives, this many adds join it every finale_add_interval_seconds (0 = none)."));
            r.Add(Num(GroupFinale, HoldKeys.FinaleAddIntervalSeconds, 30, 5, 300, HoldApplies.Live, null, true,
                "Seconds between waves of adds."));
            r.Add(Num(GroupFinale, HoldKeys.FinaleEnrageSeconds, 0, 0, 1800, HoldApplies.Live, null, true,
                "After this long alive it enrages: its damage (to players and knights) is multiplied by finale_enrage_mult (0 = never)."));
            r.Add(Num(GroupFinale, HoldKeys.FinaleEnrageMult, 1.5, 1, 5, HoldApplies.Live, null, true,
                "The enraged damage multiplier."));
            r.Add(Int(GroupFinale, HoldKeys.FinaleBossWcid, 0, 0, int.MaxValue, HoldApplies.NextSpawn, null, true,
                "A forced finale boss weenie (wcid). Not set or 0: the family champion, as today."));

            // ---- the champion (stage 2 of a chain) ----
            r.Add(Num(GroupChampion, HoldKeys.ChampionHealthMult, 1, 0.1, 10, HoldApplies.NextSpawn, null, true,
                "Multiplies the chained champion's health (on top of the named-boss sizing). Stage 2 only."));
            r.Add(Int(GroupChampion, HoldKeys.ChampionHealth, 0, 1000, 100000000, HoldApplies.NextSpawn, null, true,
                "A flat max health for the chained champion in place of its sizing. Not set: the sizing."));
            r.Add(Num(GroupChampion, HoldKeys.ChampionPlayerDamageMult, 1, 0.1, 5, HoldApplies.Live, null, true,
                "Multiplies the chained champion's damage to players, without touching the global world_events_boss_* (it divides the hits_to_kill the steering aims for)."));
            r.Add(Int(GroupChampion, HoldKeys.ChampionLevelOffset, 0, RoleLevelOffsetMin, RoleLevelOffsetMax, HoldApplies.NextEvent, null, true,
                "Stage 2 is sized as if the players were this many levels higher: its waves, adds and the champion's health curve. The champion's own model and level are fixed."));
            r.Add(Int(GroupChampion, HoldKeys.ChampionAddCount, 0, 0, 10, HoldApplies.Live, null, true,
                "While the champion lives, this many adds join it every chain_champion_add_interval_seconds (0 = none)."));
            r.Add(Num(GroupChampion, HoldKeys.ChampionAddIntervalSeconds, 30, 5, 300, HoldApplies.Live, null, true,
                "Seconds between waves of adds."));
            r.Add(Num(GroupChampion, HoldKeys.ChampionEnrageSeconds, 0, 0, 1800, HoldApplies.Live, null, true,
                "After this long alive the champion enrages: its damage to players is multiplied by chain_champion_enrage_mult (0 = never)."));
            r.Add(Num(GroupChampion, HoldKeys.ChampionEnrageMult, 1.5, 1, 5, HoldApplies.Live, null, true,
                "The enraged damage multiplier."));
            r.Add(Int(GroupChampion, HoldKeys.ChampionArrivalSeconds, WorldEvent.DefaultMinDurationSeconds, 30, 1800, HoldApplies.StageEnd, null, true,
                "The champion arrives this long after stage 2 goes live. Not set: stage 1's minimum duration (300 s), as today."));

            // ---- internals ----
            r.Add(Num(GroupInternals, HoldKeys.FinaleFallbackScale, WorldEventFinaleBoss.FallbackNamedScale, 0.5, 12, HoldApplies.NextSpawn, null, true,
                "The finale boss's size when its height cannot be read (finale_scale 0)."));
            r.Add(Num(GroupInternals, HoldKeys.FinaleHeightScaleMin, WorldEventFinaleBoss.MinHeightScale, 0.1, 5, HoldApplies.NextSpawn, null, true,
                "The smallest the height match may scale the finale boss."));
            r.Add(Num(GroupInternals, HoldKeys.FinaleHeightScaleMax, WorldEventFinaleBoss.MaxHeightScale, 1, 20, HoldApplies.NextSpawn, null, true,
                "The largest the height match may scale the finale boss."));
            r.Add(Num(GroupInternals, HoldKeys.BossDamageKeepSeconds, 30, 10, 120, HoldApplies.Live, null, true,
                "How long player damage on the finale boss is remembered for the pull."));
        }

        // ---- the boss mechanics' reads ----

        /// <summary>The finale boss's or the champion's own player damage multiplier (with enrage); 1 for anything else.</summary>
        private static double BossPlayerDamageMult(Creature c)
        {
            if (c == null)
                return 1.0;

            var now = ACE.Common.Time.GetUnixTime();

            if (c.WorldEventFinaleBoss)
                return Number(HoldKeys.FinalePlayerDamageMult, 1.0) * FinaleEnrageMult(c, now);

            if (c.P_WorldEvent is WorldEvent evt && evt.IsChainChampionStage && evt.Spawner.BossGuid == c.Guid.Full && !BossDamageSteered())
                return Number(HoldKeys.ChampionPlayerDamageMult, 1.0) * ChampionEnrageMult(c.WorldEventBossSpawnedAt, now);

            return 1.0;
        }

        /// <summary>The finale boss's enrage multiplier now: finale_enrage_mult once it has lived finale_enrage_seconds (else 1).</summary>
        public static double FinaleEnrageMult(Creature boss, double now) =>
            EnrageMult(boss?.WorldEventBossSpawnedAt ?? 0, now, HoldKeys.FinaleEnrageSeconds, HoldKeys.FinaleEnrageMult);

        /// <summary>The champion's enrage multiplier now.</summary>
        public static double ChampionEnrageMult(double spawnedAt, double now) =>
            EnrageMult(spawnedAt, now, HoldKeys.ChampionEnrageSeconds, HoldKeys.ChampionEnrageMult);

        /// <summary>The enrage rule (D6 - pure over the settings): after <c>seconds</c> alive (0 = never), the multiplier.</summary>
        public static double EnrageMult(double spawnedAt, double now, string secondsKey, string multKey)
        {
            var seconds = Number(secondsKey, 0);

            if (!(seconds > 0) || !(spawnedAt > 0) || now - spawnedAt < seconds)
                return 1.0;

            return Number(multKey, 1.5);
        }

        /// <summary>True when world_events_boss_damage_scaling_enabled steers a named boss's damage (the shipped default).</summary>
        public static bool BossDamageSteered()
        {
            try
            {
                return PropertyManager.TryGetCachedBool("world_events_boss_damage_scaling_enabled", out var on) ? on : WorldEvent.DefaultBossDamageScalingEnabled;
            }
            catch
            {
                return WorldEvent.DefaultBossDamageScalingEnabled;
            }
        }

        /// <summary>
        /// The champion's damage dials (stage 2 of a chain): the steering's hits-to-kill divided by
        /// chain_champion_player_damage_mult and its enrage, so the champion hits that much harder without touching the
        /// global world_events_boss_* properties. Any other run: the dials unchanged.
        /// </summary>
        public static BossDamageDials ChampionDials(BossDamageDials dials, bool championStage, double spawnedAt, double now)
        {
            if (!championStage)
                return dials;

            var mult = Number(HoldKeys.ChampionPlayerDamageMult, 1.0) * ChampionEnrageMult(spawnedAt, now);

            if (!(mult > 0) || Math.Abs(mult - 1.0) < 1e-9)
                return dials;

            return new BossDamageDials(dials.HitsToKill / mult, dials.RatingMin, dials.RatingMax, dials.SampleHits, dials.StepCap);
        }

        /// <summary>
        /// The champion's health multiplier (chain_champion_health_mult; 1 when not set) for a chained stage, else 1.
        /// </summary>
        public static double ChampionHealthMult(bool championStage) => championStage ? Number(HoldKeys.ChampionHealthMult, 1.0) : 1.0;

        /// <summary>chain_champion_health for a chained stage when set, else null.</summary>
        public static int? ChampionHealth(bool championStage) =>
            championStage && IsSet(HoldKeys.ChampionHealth) ? Integer(HoldKeys.ChampionHealth, 0) : (int?)null;

        /// <summary>
        /// The chained stage's minimum duration, when its champion arrives: chain_champion_arrival_seconds when set (kept 30 s
        /// under the stage's time limit), else <paramref name="inherited"/> (stage 1's, as before).
        /// </summary>
        public static int ChampionArrival(int inherited, int maxDuration)
        {
            if (!IsSet(HoldKeys.ChampionArrivalSeconds))
                return inherited;

            return Math.Max(0, Math.Min(Integer(HoldKeys.ChampionArrivalSeconds, inherited), maxDuration - 30));
        }

        /// <summary>
        /// An audience moved by <paramref name="offset"/> levels (D6 - pure): every sampled level, or the median and P90 when
        /// the estimate has no list, kept within 1 to 275. 0 returns it unchanged.
        /// </summary>
        public static AudienceEstimate ShiftAudience(AudienceEstimate estimate, int offset)
        {
            if (offset == 0)
                return estimate;

            if (estimate.Levels != null && estimate.Levels.Count > 0)
                return WorldEventRosterSelector.EstimateFromLevels(estimate.Levels.Select(l => Math.Clamp(l + offset, 1, 275)).ToList());

            return new AudienceEstimate(estimate.Count, Math.Clamp(estimate.MedianLevel + offset, 1, 275), Math.Clamp(estimate.P90Level + offset, 1, 275), estimate.PowerSum);
        }
    }
}
