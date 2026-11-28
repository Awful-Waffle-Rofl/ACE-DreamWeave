using System;
using System.Collections.Concurrent;
using System.Threading;

using log4net;

using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// A rep's recorder life cycle. <see cref="Attach"/> subscribes a live session: RepStarted creates the rep's recorder (its
    /// sink is AnalyticsManager.RecordTrialEvent), and RepEnded finishes it into the trial_run row (AnalyticsManager.RecordTrialRun)
    /// and nulls TrialRep.Recorder, after which every hook drops its event.
    ///
    /// The run row is written on the PLAYER'S queue, not at RepEnded. The last kill's XP is applied by a queued action on that
    /// queue (Player.GrantXP enqueues UpdateXpAndLevel), and the kill reaches the session through the room landblock's queue,
    /// which can run first. Writing the row behind it on the player's queue makes xp_earned include that kill. A player who is
    /// offline or logging out has nothing that will reliably drain, and the row is written at once.
    ///
    /// Safety net: a player's queue can stop draining (Player_Tick's save-failed early return, a logout racing the enqueue), so
    /// every deferred row is also held in a pending set, and <see cref="SweepOverdue"/> (TrialManager.TickAt, on the world
    /// thread after the landblock passes have joined) writes any row still unwritten <see cref="SweepAfterSecs"/> after its rep
    /// ended. A row is written exactly once, whichever runs first.
    /// </summary>
    internal static class TrialRunLog
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: where trial_event rows go.</summary>
        internal static Action<AnalyticsDatabase.TrialEventRow> EventSink = AnalyticsManager.RecordTrialEvent;

        /// <summary>Test seam: where trial_run rows go.</summary>
        internal static Action<AnalyticsDatabase.TrialRunRow> RunSink = AnalyticsManager.RecordTrialRun;

        /// <summary>Test seam: the wall clock (started_utc, ended_utc).</summary>
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        /// <summary>Test seam: the rep character's level at rep start, or null when it is not online.</summary>
        internal static Func<uint, int?> PlayerLevel = guid => TrialManager.OnlinePlayer(guid)?.Level;

        /// <summary>How long after its rep ended a deferred row may stay unwritten before the sweep writes it.</summary>
        public const double SweepAfterSecs = 5.0;

        /// <summary>Test seam: runs the row write behind whatever the player's queue already holds, or at once when the player is offline or logging out.</summary>
        internal static Action<uint, Action> Defer = DeferToPlayer;

        private static void DeferToPlayer(uint guid, Action write)
        {
            var player = TrialManager.OnlinePlayer(guid);

            if (player == null || player.IsLoggingOut)
                write();
            else
                player.EnqueueAction(new ActionEventDelegate(write));
        }

        private sealed class Pending
        {
            public double EndedAt;
            public Action Write;
        }

        /// <summary>Deferred rows not yet written, by rep.</summary>
        private static readonly ConcurrentDictionary<TrialRep, Pending> pending = new ConcurrentDictionary<TrialRep, Pending>();

        /// <summary>Deferred rows not yet written (tests and diagnostics).</summary>
        internal static int PendingCount => pending.Count;

        /// <summary>Writes every deferred row whose rep ended more than <see cref="SweepAfterSecs"/> before <paramref name="now"/>.</summary>
        internal static void SweepOverdue(double now)
        {
            foreach (var kv in pending)
            {
                if (now - kv.Value.EndedAt <= SweepAfterSecs)
                    continue;

                log.Warn($"[TRIAL] rep {kv.Key.Number} run {kv.Key.RunKey}: the player's queue did not write the run row within {SweepAfterSecs}s; writing it from the sweep");
                kv.Value.Write();
            }
        }

        internal static void ResetForTests()
        {
            EventSink = AnalyticsManager.RecordTrialEvent;
            RunSink = AnalyticsManager.RecordTrialRun;
            UtcNow = () => DateTime.UtcNow;
            PlayerLevel = guid => TrialManager.OnlinePlayer(guid)?.Level;
            Defer = DeferToPlayer;
            pending.Clear();
        }

        public static void Attach(TrialSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            session.RepStarted += rep => Begin(session, rep);
            session.RepEnded += (rep, endState) => End(session, rep, endState);
        }

        /// <summary>RepStarted: the rep's recorder, started at the rep's session-clock start.</summary>
        internal static void Begin(TrialSession session, TrialRep rep)
        {
            try
            {
                rep.CharLevelAtStart = PlayerLevel(session.PlayerGuid) ?? session.Archetype?.TargetLevel ?? 0;
                rep.Recorder = new TrialRecorder(rep.RunKey, rep.StartedAt, UtcNow(), EventSink);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] trial {session?.TrialUuid} rep {rep?.Number}: creating the recorder threw; the rep runs unrecorded", ex);
            }
        }

        /// <summary>RepEnded: the run row, written behind the player's queue (see the class summary). Once per rep.</summary>
        internal static void End(TrialSession session, TrialRep rep, string endState)
        {
            var recorder = rep?.Recorder;

            if (recorder == null)
                return;

            var endedAt = rep.EndedAt;
            var endedUtc = UtcNow();
            var written = 0;

            void Write()
            {
                // Exactly once, whichever of the player's queue and the sweep gets here first.
                if (Interlocked.Exchange(ref written, 1) != 0)
                    return;

                pending.TryRemove(rep, out _);

                AnalyticsDatabase.TrialRunRow row = null;

                try
                {
                    if (!ReferenceEquals(rep.Recorder, recorder))
                        return;

                    row = recorder.Finish(endState, endedAt, endedUtc, Facts(session, rep));
                    rep.Recorder = null;
                    RunSink(row);
                }
                catch (Exception ex)
                {
                    rep.Recorder = null;
                    log.Error($"[TRIAL] trial {session.TrialUuid} rep {rep.Number}: writing the run row threw", ex);
                }
                finally
                {
                    Tee(session, rep, row);
                }
            }

            pending[rep] = new Pending { EndedAt = endedAt, Write = Write };

            try
            {
                Defer(session.PlayerGuid, Write);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] trial {session.TrialUuid} rep {rep.Number}: deferring the run row threw; writing it now", ex);
                Write();
            }
        }

        /// <summary>
        /// The session's run-row tee (a campaign member's tallies, TrialCampaigns.OnRunRow): the row just written, or null when the
        /// write failed. Once per rep, on the writing thread, after the row went to the sink. A throw is logged and swallowed.
        /// </summary>
        private static void Tee(TrialSession session, TrialRep rep, AnalyticsDatabase.TrialRunRow row)
        {
            try
            {
                session.RunRowWritten?.Invoke(rep, row);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] trial {session.TrialUuid} rep {rep.Number}: the run-row tee threw", ex);
            }
        }

        /// <summary>Every trial_run column Finish cannot derive from the events. The three pred_* columns come from the boss's spawn prediction (null when none was made).</summary>
        internal static TrialRunFacts Facts(TrialSession session, TrialRep rep)
        {
            var spec = rep.Gem?.Spec;
            var boss = rep.BossPrediction;

            return new TrialRunFacts
            {
                TrialUuid = session.TrialUuid,
                Rep = rep.Number,
                ScenarioId = session.Scenario?.Id,
                ScenarioHash = session.ScenarioSha,
                ArchetypeId = session.Archetype?.Id,
                ArchetypeChecksum = session.ArchetypeSha,
                StateDigest = rep.StateDigest,
                DigestDrift = rep.DigestDrift,
                Driver = session.Driver ?? "manual",
                ClientCount = 1,
                CharacterId = session.PlayerGuid,
                CharLevel = rep.CharLevelAtStart,
                GemLevel = spec?.Level ?? 0,
                GemLevelPressed = rep.Gem?.PressedLevel,
                Tier = spec?.Tier ?? 0,
                DungeonId = spec?.DungeonId ?? "",
                Family = rep.Plan?.FamilyId ?? spec?.Family ?? "",
                Seed = rep.Gem?.RepSeed ?? 0,
                ModsJson = rep.Gem?.ModsJson ?? "",
                XpMultiplier = rep.Plan?.XpMultiplier ?? 1.0,
                BossXpMultiplier = rep.Plan?.BossXpMultiplier ?? 1.0,
                LumMultiplier = rep.Plan?.LumMultiplier ?? 1.0,
                PredPlayerHitsToKillBoss = boss == null ? null : TrialPrediction.Column(boss.PlayerHitsToKill),
                PredBossHitsToKillPlayer = boss == null ? null : TrialPrediction.Column(boss.CreatureHitsToKillPlayer),
                PredBossHitChance = boss == null ? null : TrialPrediction.Column(boss.CreatureHitChance),
            };
        }
    }
}
