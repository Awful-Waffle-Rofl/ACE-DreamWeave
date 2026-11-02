using System;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The goal-agnostic surface between a run and its objective: the seams that read the objective's defaulted hooks. A
    /// later slice adds the accessors its objective actually reads, here. Nothing here names a goal; a goal's own
    /// behaviour lives in its objective and reaches the run only through IWorldEventObjective. Every member runs on the world
    /// thread unless its comment says otherwise.
    /// </summary>
    public sealed partial class WorldEvent
    {
        /// <summary>
        /// The objective's <see cref="Objectives.IWorldEventObjective.PresenceAfkSeconds"/>, sanitised: 0 without an objective,
        /// when the hook throws (logged), and for a non-finite or negative value.
        /// </summary>
        private double ObjectivePresenceAfkSeconds()
        {
            try
            {
                var seconds = Objective?.PresenceAfkSeconds ?? 0;

                return double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} objective PresenceAfkSeconds threw; presence idle rule stays off", ex);
                return 0;
            }
        }

        /// <summary>
        /// The objective's <see cref="Objectives.IWorldEventObjective.ParticipantRadius"/> when it is positive and finite, else
        /// null (the source's own rule). Null without an objective and when the hook throws (logged).
        /// </summary>
        public float? ObjectiveParticipantRadius
        {
            get
            {
                try
                {
                    var r = Objective?.ParticipantRadius;

                    return r is float f && float.IsFinite(f) && f > 0 ? f : (float?)null;
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} objective ParticipantRadius threw; using the source's radius", ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// The objective's <see cref="Objectives.IWorldEventObjective.AnnouncesLocally"/>; false without an objective and when
        /// the hook throws (logged).
        /// </summary>
        public bool ObjectiveAnnouncesLocally
        {
            get
            {
                try
                {
                    return Objective?.AnnouncesLocally ?? false;
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} objective AnnouncesLocally threw; announcing world-wide", ex);
                    return false;
                }
            }
        }

        // ---- consumers of the participation hooks --------------------------------------------------------------------

        /// <summary>
        /// The radius players count within - the audience sample, the presence count behind the wipe and abandon rules, the
        /// local lines, the cache count: <see cref="ObjectiveParticipantRadius"/> when the objective sets one, else the
        /// source's rewardRadius exactly as before.
        /// </summary>
        public float EffectiveParticipantRadius => ObjectiveParticipantRadius ?? Composition?.Source?.RewardRadius ?? 0f;

        /// <summary>
        /// A World Event line during the run (waves, progress, the objective's own lines): world-wide, or only to the players
        /// within <see cref="EffectiveParticipantRadius"/> when <see cref="ObjectiveAnnouncesLocally"/>. The teaser, start,
        /// outcome and time-left lines never come here; they stay world-wide. World thread.
        /// </summary>
        public void AnnounceInRun(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            if (ObjectiveAnnouncesLocally)
                WorldEventAnnouncer.Local(this, line);
            else
                WorldEventAnnouncer.Broadcast(line);
        }

        /// <summary>The objective's <see cref="Objectives.IWorldEventObjective.CountsStaffTowardAudience"/>; false without one and when it throws (logged).</summary>
        internal bool ObjectiveCountsStaff()
        {
            try
            {
                return Objective?.CountsStaffTowardAudience ?? false;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} objective CountsStaffTowardAudience threw; staff do not count", ex);
                return false;
            }
        }

        /// <summary>The objective's <see cref="Objectives.IWorldEventObjective.OutcomeTemplate"/>; the template unchanged without one and when it throws (logged).</summary>
        private string ObjectiveOutcomeTemplate(string template, bool success)
        {
            try
            {
                return Objective == null ? template : Objective.OutcomeTemplate(template, success);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} objective OutcomeTemplate threw; using the template as written", ex);
                return template;
            }
        }

        /// <summary>
        /// True when the character <paramref name="guid"/> has been inside the arena during this run, by the presence ledger
        /// (it has a presence record). False without a ledger. Any thread: the ledger guards itself.
        /// </summary>
        public bool IsPresenceParticipant(uint guid)
        {
            var presence = Presence;

            return presence != null && guid != 0 && presence.TryGetPresentSeconds(guid, out var seconds) && seconds > 0;
        }

        /// <summary>
        /// A wave an objective that runs its own cadence (<see cref="Objectives.IWorldEventObjective.ReplacesWaveCadence"/>)
        /// sends: the run's ordinary wave step - the audience re-sample, the objective resize, the pace evaluation, the roster
        /// pick, the wave index and the theme's local wave line - with the objective's sizing and placement in place of the
        /// spawner's plain wave. <paramref name="initial"/> is the first wave (the wave index stays 0). True when a pick was
        /// handed to <see cref="WorldEventObjectiveWave.Place"/>; false when the pick was empty (the alive cap) or the wave was
        /// not sent at all. World thread.
        /// </summary>
        public bool SpawnObjectiveWave(double now, bool initial, string trigger, WorldEventObjectiveWave wave)
        {
            if (wave?.Place == null || AnchorLandblock == null)
                return false;

            var placed = false;
            var place = wave.Place;

            var sink = new WorldEventObjectiveWave
            {
                SizingTheme = wave.SizingTheme,
                ExtraQuantity = wave.ExtraQuantity,
                SkipThemeWaveLine = wave.SkipThemeWaveLine,
                Place = pick =>
                {
                    placed = true;
                    place(pick);
                }
            };

            SpawnWave(now, initial, trigger, sink);

            lastWaveAt = now;

            return placed;
        }

        /// <summary>When the run went Active, for an objective's own clock (0 before).</summary>
        public double ObjectiveActiveAt => ActiveAt;
    }

    /// <summary>
    /// How an objective that runs its own cadence shapes one wave (<see cref="WorldEvent.SpawnObjectiveWave"/>). Every member
    /// is optional; a null one keeps the run's ordinary behaviour for that step. World thread.
    /// </summary>
    public sealed class WorldEventObjectiveWave
    {
        /// <summary>The theme the pick is sized with (from the run's theme); null keeps the theme.</summary>
        public Func<Defs.SourceThemeDef, Defs.SourceThemeDef> SizingTheme { get; init; }

        /// <summary>Extra creatures on top of the pace controller's quantity bonus, given the sizing theme; null adds none.</summary>
        public Func<Defs.SourceThemeDef, int> ExtraQuantity { get; init; }

        /// <summary>Places the pick (instead of the spawner's plain wave).</summary>
        public Action<WorldEventObjectiveWavePick> Place { get; init; }

        /// <summary>True when the objective says its own wave lines, so the theme's generic local wave line is not sent. False (the default) sends it as before.</summary>
        public bool SkipThemeWaveLine { get; init; }
    }

    /// <summary>One picked wave handed to <see cref="WorldEventObjectiveWave.Place"/>.</summary>
    public readonly struct WorldEventObjectiveWavePick
    {
        public WorldEventObjectiveWavePick(WavePick pick, int waveIndex, LevelBand band, double healthMult, double now, string trigger)
        {
            Pick = pick;
            WaveIndex = waveIndex;
            Band = band;
            HealthMult = healthMult;
            Now = now;
            Trigger = trigger;
        }

        public WavePick Pick { get; }

        public int WaveIndex { get; }

        public LevelBand Band { get; }

        public double HealthMult { get; }

        public double Now { get; }

        public string Trigger { get; }
    }
}
