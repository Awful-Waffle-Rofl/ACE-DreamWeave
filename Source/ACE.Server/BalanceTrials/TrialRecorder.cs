using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers.Analytics;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// Every trial_run column that Finish cannot derive from the event stream: scenario, archetype, digest, character, gem,
    /// plan multipliers and predictions. The session fills it from the TrialRep and its setup.
    /// </summary>
    public sealed class TrialRunFacts
    {
        public string TrialUuid { get; init; }
        public int Rep { get; init; }
        public string ScenarioId { get; init; }
        public string ScenarioHash { get; init; }
        public string ArchetypeId { get; init; }
        public string ArchetypeChecksum { get; init; }
        public string StateDigest { get; init; }
        public bool DigestDrift { get; init; }
        public string Driver { get; init; } = "manual";
        public int ClientCount { get; init; } = 1;
        public uint CharacterId { get; init; }
        public int CharLevel { get; init; }
        public int GemLevel { get; init; }
        public int? GemLevelPressed { get; init; }
        public int Tier { get; init; }
        public string DungeonId { get; init; }
        public string Family { get; init; }
        public int Seed { get; init; }
        public string ModsJson { get; init; }
        public double XpMultiplier { get; init; }
        public double BossXpMultiplier { get; init; }
        public double LumMultiplier { get; init; }
        public double? PredPlayerHitsToKillBoss { get; init; }
        public double? PredBossHitsToKillPlayer { get; init; }
        public double? PredBossHitChance { get; init; }
    }

    /// <summary>
    /// The pure per-rep accumulator. It emits one trial_event row per event through the sink and, at rep end, builds the
    /// trial_run row. It reads no world state, no clock and no PropertyManager: every time is a caller-supplied seconds value.
    /// Row conventions: a creature-side wcid is target_wcid on "out" rows and actor_wcid on "in" rows; actor_role is always the
    /// creature's role. Event t_ms is round-half-away-from-zero of (now - startedAt) * 1000, clamped to 0.
    /// Vitals are 0-1 FRACTIONS of max (not percents): OnVitals takes fractions, and the hp_low_pct / stam_low_pct / mana_low_pct
    /// columns hold the lowest fraction seen (despite the _pct name). The vital_low event's amount is an integer column, so it holds
    /// the HP fraction in THOUSANDTHS (0.2 -> 200). The recorder is single-threaded (call it only from the room landblock's thread, or from the player's queue, which never runs at the same time), except OnLumGranted (and, for symmetry, OnXpGranted), which are Interlocked: a fellow's luminance share arrives on another landblock's thread.
    /// damage_type is clamped to its 16-char column by AnalyticsDatabase.WriteTrialEvents (Truncate), so the recorder does not clamp.
    /// v1 limit: crit and combat_type are null on rows whose hook does not know them (the DamageHistory path).
    /// </summary>
    internal sealed class TrialRecorder
    {
        /// <summary>A gap longer than this between player-originated events is idle time.</summary>
        public const double IdleGapSecs = 2.0;

        /// <summary>HP fraction below which a vital_low event fires.</summary>
        public const double VitalLowThreshold = 0.25;

        private readonly string runKey;
        private readonly double startedAt;
        private readonly DateTime startedUtc;
        private readonly Action<AnalyticsDatabase.TrialEventRow> sink;

        private readonly List<double> playerEventTimes = new();
        private readonly Dictionary<uint, double> firstHit = new();
        private readonly Dictionary<DungeonRole, List<double>> ttks = new();

        private int killsTrash, killsElite, hitsOut, missesOut, resistedOut, hitsIn, evadedIn, resistedIn, deaths, consumables, procs;
        private bool bossKilled;
        private long dmgDealt, dmgTaken, xpEarned, lumEarned;
        private double? hpLow, stamLow, manaLow;
        private bool hpWasLow;

        public TrialRecorder(string runKey, double startedAt, DateTime startedUtc, Action<AnalyticsDatabase.TrialEventRow> sink)
        {
            this.runKey = runKey;
            this.startedAt = startedAt;
            this.startedUtc = startedUtc;
            this.sink = sink ?? (_ => { });
        }

        public string RunKey => runKey;

        public DateTime StartedUtc => startedUtc;

        private AnalyticsDatabase.TrialEventRow Row(string kind, string dir, double now) => new()
        {
            RunKey = runKey,
            TMs = (int)Math.Max(0, Math.Round((now - startedAt) * 1000.0, MidpointRounding.AwayFromZero)),
            Kind = kind,
            Dir = dir,
        };

        public void OnSpawn(uint wcid, DungeonRole role, string predJson, double now)
        {
            var r = Row(TrialEventKinds.Spawn, null, now);
            r.ActorWcid = wcid;
            r.ActorRole = role.ToString();
            r.PredJson = predJson;
            sink(r);
        }

        public void OnHitOut(uint targetGuid, uint wcid, DungeonRole role, DamageType type, uint amount, double now)
        {
            hitsOut++;
            dmgDealt += amount;
            playerEventTimes.Add(now);
            firstHit.TryAdd(targetGuid, now);

            var r = Row(TrialEventKinds.Hit, TrialEventKinds.DirOut, now);
            r.TargetWcid = wcid;
            r.ActorRole = role.ToString();
            r.DamageType = type.ToString();
            r.Amount = amount;
            sink(r);
        }

        public void OnHitIn(uint wcid, DungeonRole role, DamageType type, uint amount, double now)
        {
            hitsIn++;
            dmgTaken += amount;

            var r = Row(TrialEventKinds.Hit, TrialEventKinds.DirIn, now);
            r.ActorWcid = wcid;
            r.ActorRole = role.ToString();
            r.DamageType = type.ToString();
            r.Amount = amount;
            sink(r);
        }

        public void OnMissOut(uint wcid, DungeonRole role, CombatType ct, double now)
        {
            missesOut++;
            playerEventTimes.Add(now);

            var r = Row(TrialEventKinds.Miss, TrialEventKinds.DirOut, now);
            r.TargetWcid = wcid;
            r.ActorRole = role.ToString();
            r.CombatType = ct.ToString();
            sink(r);
        }

        public void OnEvadeIn(uint wcid, DungeonRole role, CombatType ct, double now)
        {
            evadedIn++;

            var r = Row(TrialEventKinds.Evade, TrialEventKinds.DirIn, now);
            r.ActorWcid = wcid;
            r.ActorRole = role.ToString();
            r.CombatType = ct.ToString();
            sink(r);
        }

        public void OnResist(bool outgoing, uint wcid, DungeonRole role, uint spellId, double now)
        {
            if (outgoing)
            {
                resistedOut++;
                playerEventTimes.Add(now);
            }
            else
                resistedIn++;

            var r = Row(TrialEventKinds.Resist, outgoing ? TrialEventKinds.DirOut : TrialEventKinds.DirIn, now);
            if (outgoing)
                r.TargetWcid = wcid;
            else
                r.ActorWcid = wcid;
            r.ActorRole = role.ToString();
            r.SpellId = spellId;
            sink(r);
        }

        public void OnKill(uint targetGuid, uint wcid, DungeonRole role, long xpAwarded, long lumAwarded, double now)
        {
            switch (role)
            {
                case DungeonRole.Trash: killsTrash++; break;
                case DungeonRole.Elite: killsElite++; break;
                case DungeonRole.Boss: bossKilled = true; break;
            }

            if (firstHit.TryGetValue(targetGuid, out var first))
            {
                if (!ttks.TryGetValue(role, out var list))
                    ttks[role] = list = new List<double>();
                list.Add(now - first);
                firstHit.Remove(targetGuid);
            }

            var r = Row(TrialEventKinds.Kill, TrialEventKinds.DirOut, now);
            r.TargetWcid = wcid;
            r.ActorRole = role.ToString();
            r.Xp = xpAwarded;
            r.Lum = lumAwarded;
            sink(r);
        }

        public void OnDeath(double now)
        {
            deaths++;
            sink(Row(TrialEventKinds.Death, TrialEventKinds.DirIn, now));
        }

        public void OnConsume(uint itemWcid, double now)
        {
            consumables++;
            playerEventTimes.Add(now);

            var r = Row(TrialEventKinds.Consume, TrialEventKinds.DirOut, now);
            r.ItemWcid = itemWcid;
            sink(r);
        }

        /// <summary>A class-ability proc. <paramref name="spellId"/> is null when the hook site does not know the spell (Player.CastClassAbilityProc).</summary>
        public void OnProc(uint? spellId, double now)
        {
            procs++;
            playerEventTimes.Add(now);

            var r = Row(TrialEventKinds.Proc, TrialEventKinds.DirOut, now);
            r.SpellId = spellId;
            sink(r);
        }

        /// <summary>
        /// XP actually added at the grant site, read at Finish. XP is applied on the recipient's own queue (GrantXP enqueues
        /// UpdateXpAndLevel), so it does not cross threads; it is Interlocked only to match <see cref="OnLumGranted"/>.
        /// </summary>
        public void OnXpGranted(long amount) => System.Threading.Interlocked.Add(ref xpEarned, amount);

        /// <summary>
        /// Luminance actually added, read at Finish. Interlocked, unlike every other member: a fellow's luminance share calls
        /// GrantLuminance directly (Fellowship.SplitLuminance), so it reaches AddLuminance on the earning fellow's landblock thread.
        /// </summary>
        public void OnLumGranted(long amount) => System.Threading.Interlocked.Add(ref lumEarned, amount);

        /// <summary>Samples are 0-1 fractions of max. vital_low fires when hpFrac first drops below 0.25 and re-arms after recovery to 0.25 or more.</summary>
        public void OnVitals(double hpFrac, double stamFrac, double manaFrac, double now)
        {
            hpLow = hpLow.HasValue ? Math.Min(hpLow.Value, hpFrac) : hpFrac;
            stamLow = stamLow.HasValue ? Math.Min(stamLow.Value, stamFrac) : stamFrac;
            manaLow = manaLow.HasValue ? Math.Min(manaLow.Value, manaFrac) : manaFrac;

            var low = hpFrac < VitalLowThreshold;

            if (low && !hpWasLow)
            {
                var r = Row(TrialEventKinds.VitalLow, null, now);
                r.Amount = (long)Math.Round(hpFrac * 1000.0, MidpointRounding.AwayFromZero);
                sink(r);
            }

            hpWasLow = low;
        }

        private static double? Avg(Dictionary<DungeonRole, List<double>> d, DungeonRole role) =>
            d.TryGetValue(role, out var l) && l.Count > 0 ? l.Average() : null;

        public AnalyticsDatabase.TrialRunRow Finish(string endState, double now, DateTime endedUtc, TrialRunFacts facts)
        {
            var duration = Math.Max(0, now - startedAt);
            var xpEarned = System.Threading.Interlocked.Read(ref this.xpEarned);
            var lumEarned = System.Threading.Interlocked.Read(ref this.lumEarned);

            // Idle: every gap over IdleGapSecs between consecutive player events, with rep start and rep end as event times.
            var times = new List<double> { startedAt };
            times.AddRange(playerEventTimes.OrderBy(t => t));
            times.Add(now);

            var idle = 0.0;
            for (int i = 1; i < times.Count; i++)
            {
                var gap = times[i] - times[i - 1];
                if (gap > IdleGapSecs)
                    idle += gap;
            }

            idle = Math.Min(idle, duration);
            var engaged = duration - idle;

            double? PerHour(long total, double secs) => secs > 0 ? total / secs * 3600.0 : null;

            return new AnalyticsDatabase.TrialRunRow
            {
                RunKey = runKey,
                TrialUuid = facts.TrialUuid,
                Rep = facts.Rep,
                ScenarioId = facts.ScenarioId,
                ScenarioHash = facts.ScenarioHash,
                ArchetypeId = facts.ArchetypeId,
                ArchetypeChecksum = facts.ArchetypeChecksum,
                StateDigest = facts.StateDigest,
                DigestDrift = facts.DigestDrift,
                Driver = facts.Driver,
                ClientCount = facts.ClientCount,
                CharacterId = facts.CharacterId,
                CharLevel = facts.CharLevel,
                GemLevel = facts.GemLevel,
                GemLevelPressed = facts.GemLevelPressed,
                Tier = facts.Tier,
                DungeonId = facts.DungeonId,
                Family = facts.Family,
                Seed = facts.Seed,
                ModsJson = facts.ModsJson,
                StartedUtc = startedUtc,
                EndedUtc = endedUtc,
                EndState = endState,
                EngagedSecs = engaged,
                IdleSecs = idle,
                TtkTrashAvg = Avg(ttks, DungeonRole.Trash),
                TtkEliteAvg = Avg(ttks, DungeonRole.Elite),
                TtkBoss = Avg(ttks, DungeonRole.Boss),
                KillsTrash = killsTrash,
                KillsElite = killsElite,
                BossKilled = bossKilled,
                Deaths = deaths,
                DmgDealt = dmgDealt,
                DmgTaken = dmgTaken,
                HitsOut = hitsOut,
                MissesOut = missesOut,
                ResistedOut = resistedOut,
                HitsIn = hitsIn,
                EvadedIn = evadedIn,
                ResistedIn = resistedIn,
                XpEarned = xpEarned,
                LumEarned = lumEarned,
                XpPerHour = PerHour(xpEarned, duration),
                LumPerHour = PerHour(lumEarned, duration),
                XpPerEngagedHour = PerHour(xpEarned, engaged),
                LumPerEngagedHour = PerHour(lumEarned, engaged),
                XpMultiplier = facts.XpMultiplier,
                BossXpMultiplier = facts.BossXpMultiplier,
                LumMultiplier = facts.LumMultiplier,
                HpLowPct = hpLow,
                StamLowPct = stamLow,
                ManaLowPct = manaLow,
                ConsumablesUsed = consumables,
                AbilityProcs = procs,
                PredPlayerHitsToKillBoss = facts.PredPlayerHitsToKillBoss,
                PredBossHitsToKillPlayer = facts.PredBossHitsToKillPlayer,
                PredBossHitChance = facts.PredBossHitChance,
            };
        }
    }
}
