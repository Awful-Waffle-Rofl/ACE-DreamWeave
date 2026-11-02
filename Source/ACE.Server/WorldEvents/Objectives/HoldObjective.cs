using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// goals.json type "Hold" - protect the knights. SKELETON (World Events Hold slice H0): the success / fail / progress rules
    /// and the run state it owns (<see cref="Run"/>), but no spawning, no wave cadence and no knights placed - those are later
    /// slices. The factory builds it only while world_events_hold_enabled is on, and no source lists a Hold goal yet, so no
    /// run can reach it in production.
    ///
    ///   * SUCCEEDS once holdSeconds have passed since the run went Active with at least minSurvivors knights still standing
    ///     (minSurvivors is clamped to the number actually placed), or, for a wave-based hold, once every counted wave has
    ///     started, landed and been cleared;
    ///   * FAILS (<see cref="WorldEventOutcome.FailedObjectiveLost"/>) the moment fewer than that are left;
    ///   * ABORTS (<see cref="WorldEventOutcome.AbortedError"/>) when no knight was placed at all within
    ///     <see cref="PlacementGraceSeconds"/> of going Active - a run with nothing to protect is a content or placement fault,
    ///     not a win and not the players' loss.
    ///
    /// Ported from DreamWeave #17's HoldObjective (IsComplete, FailureOutcome, ProgressText and their helpers). Every member
    /// runs on the world thread, like every IWorldEventObjective.
    /// </summary>
    public sealed partial class HoldObjective : IWorldEventObjective
    {
        /// <summary>Seconds after going Active before "nothing was placed" becomes an abort.</summary>
        public const double PlacementGraceSeconds = 5.0;

        private readonly GoalDef goal;
        private readonly Func<IReadOnlyCollection<uint>> protectedGuids;
        private readonly Func<IReadOnlyCollection<uint>> liveProtectedGuids;
        private readonly Func<double> activeAt;
        private readonly WorldEventParticipation ledger;
        private readonly Func<string> family;
        private readonly Func<HoldWaveCounts> waves;
        private readonly Func<string> targetHealth;

        private double now;

        private string lastKillerName;
        private string topDamagerName;

        /// <summary>Wave-based holds: the sized wave target, raised as the crowd grows, never lowered. Null until first sized.</summary>
        private int? targetWaves;

        /// <summary>
        /// The production shape (WorldEventObjectiveFactory): the placed and standing knights are read from
        /// <paramref name="run"/>, and <paramref name="audience"/> (the run's sampled audience, known by OnStaged) sizes the
        /// run's first tuning.
        /// </summary>
        public HoldObjective(GoalDef goal, WorldEventHoldRun run, Func<double> activeAt, WorldEventParticipation ledger,
            Func<int> audience, Func<string> family = null, Func<HoldWaveCounts> waves = null, Func<string> targetHealth = null)
            : this(goal, () => run?.PlacedKnightGuids, () => run?.LiveKnightGuids, activeAt, ledger, family, waves, targetHealth, run)
        {
            this.audience = audience;
        }

        private readonly Func<int> audience;

        /// <summary>
        /// The testable shape (D6): the placed and standing knight guids come from plain funcs.
        /// </summary>
        /// <param name="family">The run's composed monster family display name, for the {family}/{Family} progress tokens. Null reads as the generic fallback.</param>
        /// <param name="waves">Wave-based holds: the run's wave counts, read fresh on every check. Null reads as no waves.</param>
        /// <param name="run">The run state this objective owns; null builds a fresh one with run id 0.</param>
        public HoldObjective(GoalDef goal, Func<IReadOnlyCollection<uint>> protectedGuids,
            Func<IReadOnlyCollection<uint>> liveProtectedGuids, Func<double> activeAt, WorldEventParticipation ledger,
            Func<string> family = null, Func<HoldWaveCounts> waves = null, Func<string> targetHealth = null,
            WorldEventHoldRun run = null)
        {
            this.goal = goal;
            this.protectedGuids = protectedGuids ?? (() => Array.Empty<uint>());
            this.liveProtectedGuids = liveProtectedGuids ?? (() => Array.Empty<uint>());
            this.activeAt = activeAt ?? (() => 0);
            this.ledger = ledger;
            this.family = family ?? (() => null);
            this.waves = waves ?? (() => default);
            this.targetHealth = targetHealth ?? (() => "");
            Run = run ?? new WorldEventHoldRun(0);
        }

        /// <summary>The run state this objective owns, and every Hold creature's tag points at.</summary>
        public WorldEventHoldRun Run { get; }

        /// <summary>
        /// H5: where the spawn portals stand after the start-spot rule's clear-area check (WorldEventHoldStartSpotRule), or null
        /// when it made none. Set once by the factory; the spawner places the portals from it (WorldEventSpawner.SpawnProtected).
        /// </summary>
        public WorldEventAreaPlan AreaPlan { get; private set; }

        /// <summary>H5: the factory hands over the start-spot rule's plan (null for none). World thread, before the run stages.</summary>
        public void SetAreaPlan(WorldEventAreaPlan plan) => AreaPlan = plan;

        private ProtectDef Protect => goal?.Protect;

        /// <summary>The standing knights' health for {targetHealth}: the live spawner's for a production run, else the constructor's func.</summary>
        private string TargetHealthText() => evt != null ? LiveTargetHealth() : targetHealth();

        private int HoldSeconds => Math.Max(0, goal?.HoldSeconds ?? 0);

        private string Noun => Protect?.EffectiveNoun ?? ProtectDef.DefaultNoun;

        private string NounPlural => Protect?.EffectiveNounPlural ?? ProtectDef.DefaultNounPlural;

        /// <summary>max_waves as of the run's tuning (ProtectDef.MaxHoldWaves before OnStaged).</summary>
        private int MaxWaves => Run.Tuning?.MaxWaves > 0 ? Run.Tuning.MaxWaves : ProtectDef.MaxHoldWaves;

        // ---- waves ------------------------------------------------------------------------------------------------

        /// <summary>True when the goal counts waves (protect.waves).</summary>
        public bool IsWaveBased => Protect?.IsWaveBased ?? false;

        /// <summary>The wave target (protect.waves for the crowd). 0 for a time-only hold.</summary>
        public int TargetWaves
        {
            get
            {
                if (!IsWaveBased)
                    return 0;

                targetWaves ??= CapWaves(ResolveWaveTarget(0), MaxWaves);

                return targetWaves.Value;
            }
        }

        /// <summary>A resolved wave count, kept within [1, <paramref name="maxWaves"/>] (default ProtectDef.MaxHoldWaves).</summary>
        public static int CapWaves(int resolved, int maxWaves = ProtectDef.MaxHoldWaves) => Math.Clamp(resolved, 1, Math.Max(1, maxWaves));

        /// <summary>The wave counts as of now.</summary>
        public HoldWaveCounts Waves => evt != null ? LiveWaveCounts() : waves();

        /// <summary>Wave-based holds: re-size the wave target against a fresh audience, upwards only.</summary>
        public void OnAudienceResampled(int participantCount)
        {
            Run.SetParticipantCount(participantCount);

            if (!IsWaveBased)
                return;

            var current = TargetWaves;

            // Once the finale has started the total is settled: raising it then would turn the finale into an ordinary wave
            // and send more after it. The same holds while the finale is the NEXT wave: the run re-samples the audience inside
            // the very wave step that sends it (WorldEvent.SpawnObjectiveWave), so a rise there would send a second finale.
            var started = Math.Max(Waves.Started, wavesStarted);

            if (started + 1 >= current)
                return;

            var resolved = CapWaves(ResolveWaveTarget(Math.Max(0, participantCount)), MaxWaves);

            if (resolved > current)
                targetWaves = resolved;
        }

        /// <summary>
        /// Wave-based holds (D6 - pure): the waves are won when every counted wave (the finale last) has started, every wave
        /// that landed has been cleared and nothing is left standing. No minimum time.
        /// </summary>
        public static bool WavesWon(HoldWaveCounts counts, int target, int maxWaves = ProtectDef.MaxHoldWaves)
        {
            var cap = Math.Min(target, Math.Max(1, maxWaves));

            // Resolved: the contributor's in-game bug where the run "won" 9 ms after the finale started, because the
            // finale's monsters had not been placed yet, so nothing of it was alive to count.
            return cap > 0 && counts.Started >= cap && counts.Resolved >= counts.Started
                && counts.Landed > 0 && counts.Cleared >= counts.Landed && counts.Live == 0;
        }

        /// <summary>The local line at the start of each wave: "Wave 3 of 7 - 3 of 3 envoys stand." ASCII only.</summary>
        public string WaveStartText(int waveNumber)
        {
            var health = TargetHealthText() ?? "";
            var line = $"{WorldEventHoldRules.WaveProgress(waveNumber, TargetWaves)} - {Alive} of {Placed} {NounPlural} stand";

            return string.IsNullOrEmpty(health) ? line + "." : $"{line} ({health}).";
        }

        // ---- knights ----------------------------------------------------------------------------------------------

        /// <summary>How many knights were placed (ever adopted).</summary>
        public int Placed => PlacedGuids().Count;

        /// <summary>How many placed knights are still standing.</summary>
        public int Alive
        {
            get
            {
                var placed = PlacedGuids();
                var live = LiveGuids();

                return live.Count(placed.Contains);
            }
        }

        /// <summary>minSurvivors, clamped to [1, Placed]. 0 while nothing has been placed.</summary>
        public int RequiredSurvivors => RequiredFor(Protect?.MinSurvivors ?? ProtectDef.DefaultMinSurvivors, Placed);

        /// <summary>The pure clamp behind <see cref="RequiredSurvivors"/>.</summary>
        public static int RequiredFor(int minSurvivors, int placed)
        {
            if (placed <= 0)
                return 0;

            return Math.Clamp(minSurvivors, 1, placed);
        }

        /// <summary>Seconds since the run went Active, as of the last <see cref="Tick"/>; 0 before that.</summary>
        private double Elapsed
        {
            get
            {
                var start = activeAt();

                return start > 0 && now > 0 ? Math.Max(0, now - start) : 0;
            }
        }

        /// <summary>Seconds of the hold still to go, never below 0.</summary>
        public int RemainingSeconds => (int)Math.Ceiling(Math.Max(0, HoldSeconds - Elapsed));

        // ---- IWorldEventObjective ---------------------------------------------------------------------------------

        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            // WorldEvent forwards only the deaths of creatures this run owns, so every creature here is an event creature.
            // H6: the run's death reactions first (a knight's last words, the corrupted-kill and rescue heals), on this thread.
            try
            {
                Run.OnCreatureDied(creature, lastDamager?.IsPlayer ?? false, lastDamager?.TryGetPetOwner() != null);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={Run.RunId} hold death reactions threw", ex);
            }

            OnDeathCore(creature?.Guid.Full ?? 0, creature != null,
                WorldEventMvpResolver.ResolveName(lastDamager), WorldEventMvpResolver.ResolveName(topDamager));
        }

        /// <summary>
        /// Testable core (D6). A knight's own death changes nothing here - <see cref="Alive"/> reads the live set, which the
        /// death has already updated - and its killer is a monster, so it is never the MVP. Any other event creature's death
        /// records the killing blow and top damager for the KillingBlow MVP rules.
        /// </summary>
        public void OnDeathCore(uint guid, bool wasEventCreature, string lastDamagerName, string topDamagerName)
        {
            if (!wasEventCreature)
                return;

            if (PlacedGuids().Contains(guid))
                return;

            lastKillerName = lastDamagerName;
            this.topDamagerName = topDamagerName;
        }

        public void Tick(double now)
        {
            this.now = now;

            if (activeAt() > 0 && !Run.IsActive)
                Run.MarkActive();

            Run.Refresh(now);

            // H6: the production run (AttachEvent) publishes the spawner's views, runs its own wave cadence and the run ticks.
            if (evt != null && activeAt() > 0)
                TickLive(now);
        }

        public bool IsComplete
        {
            get
            {
                var placed = Placed;

                if (placed == 0 || activeAt() <= 0 || now <= 0)
                    return false;

                if (Alive < RequiredFor(Protect?.MinSurvivors ?? ProtectDef.DefaultMinSurvivors, placed))
                    return false;

                // holdSeconds: the time-only hold's finish line, and the wave-based hold's safety cap.
                if (Elapsed >= HoldSeconds)
                    return true;

                return IsWaveBased && WavesWon(Waves, TargetWaves, MaxWaves);
            }
        }

        public WorldEventOutcome? FailureOutcome
        {
            get
            {
                if (activeAt() <= 0 || now <= 0)
                    return null;

                var placed = Placed;

                if (placed == 0)
                    return Elapsed >= PlacementGraceSeconds ? WorldEventOutcome.AbortedError : (WorldEventOutcome?)null;

                return Alive < RequiredFor(Protect?.MinSurvivors ?? ProtectDef.DefaultMinSurvivors, placed)
                    ? WorldEventOutcome.FailedObjectiveLost
                    : (WorldEventOutcome?)null;
            }
        }

        /// <summary>
        /// Tokens: {alive}, {total}, {lost}, {noun}, {nounPlural}, {family}, {Family}, {remaining} (the hold time left, m:ss),
        /// {targetHealth}, {waveProgress}, {waves}, {wave}. ASCII only, like every player-facing string.
        /// </summary>
        public string ProgressText
        {
            get
            {
                var total = Placed;

                if (total == 0)
                    return $"no {NounPlural} have been placed yet.";

                var alive = Alive;

                var template = goal?.ProgressTemplate ?? (IsWaveBased ? DefaultWaveProgressTemplate : DefaultProgressTemplate);

                var started = Waves.Started;

                // {lost}, {remaining} and the wave tokens first; the shared line filler then does the family, the nouns and
                // the counts, so progress text and knight lines read the tokens identically.
                if (template.Contains("{targetHealth}"))
                    template = template.Replace("{targetHealth}", TargetHealthText() ?? "");

                template = template
                    .Replace("{lost}", (total - alive).ToString())
                    .Replace("{remaining}", FormatRemaining(RemainingSeconds))
                    .Replace("{waveProgress}", WorldEventHoldRules.WaveProgress(started, TargetWaves))
                    .Replace("{waves}", TargetWaves.ToString())
                    .Replace("{wave}", started.ToString());

                return WorldEventHoldRules.FillLine(template, family(), Noun, NounPlural, null, alive, total);
            }
        }

        /// <summary>The progress line a Hold goal gets when goals.json declares no progressTemplate.</summary>
        public const string DefaultProgressTemplate = "{alive} of {total} {nounPlural} stand. Hold for {remaining}.";

        /// <summary>The progress line a WAVE-based Hold goal gets when goals.json declares no progressTemplate.</summary>
        public const string DefaultWaveProgressTemplate = "{waveProgress} - {alive} of {total} {nounPlural} stand.";

        /// <summary>m:ss, e.g. 6:05.</summary>
        public static string FormatRemaining(int seconds)
        {
            seconds = Math.Max(0, seconds);

            return $"{seconds / 60}:{seconds % 60:D2}";
        }

        /// <summary>"No opinion": a Hold run is won by standing knights and cleared waves, never by a trash kill count.</summary>
        public int? RemainingKills => null;

        /// <summary>
        /// A wave-based hold runs its own cadence (HoldObjective.Cadence.cs) and the core's wave tick stands down; a time-only
        /// hold keeps the core cadence.
        /// </summary>
        public bool ReplacesWaveCadence => IsWaveBased;

        /// <summary>
        /// The core's wave 0 on the Active edge. A wave-based hold answers false: its own cadence sends every wave, the first
        /// once the knights have landed (FirstWaveMayStart). A time-only hold sends the core's wave 0 once the knights have
        /// landed, or after the placement grace.
        /// </summary>
        public bool FirstWaveReady(double now)
        {
            if (IsWaveBased)
                return false;

            var since = activeAt() > 0 ? Math.Max(0, now - activeAt()) : 0;

            return WorldEventHoldRules.FirstWaveMayStart(evt == null || evt.Spawner.HoldKnightsLanded, since, PlacementGraceSeconds);
        }

        /// <summary>The run's settings are resolved here, once: NextRun rows are frozen for the run's life from this point.</summary>
        public void OnStaged(double now)
        {
            var participants = Run.ParticipantCount;

            try
            {
                if (audience != null)
                    participants = audience();
            }
            catch
            {
                // An audience that cannot be read sizes the tuning for the count the run already holds.
            }

            Run.Stage(goal, participants, now);

            // The wave target is sized again against the staged tuning.
            targetWaves = null;
            previewTuning = null;
            stagedOnce = true;

            if (evt != null)
                StageLive(now);
        }

        /// <summary>The run is over: hooks treat its creatures as no longer in a live run.</summary>
        public void OnFinish(WorldEventOutcome outcome, double now)
        {
            // A paying success: one standing knight thanks the defenders (protect.lines.success), queued on its landblock before
            // the cleanup that removes it, with the world copy sent at once (an in-run line: local when the run announces locally). Before MarkInactive, while the knights still count.
            if (WorldEvent.PaysSuccessCrate(outcome))
            {
                try
                {
                    var world = Run.SpeakSuccessLine();

                    // Through the run (announce_locally respected); a test objective with no run broadcasts it.
                    if (!string.IsNullOrEmpty(world))
                    {
                        if (evt != null)
                            evt.AnnounceInRun(world);
                        else
                            WorldEventAnnouncer.Broadcast(world);
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={Run.RunId} hold success line threw", ex);
                }
            }

            Run.MarkInactive();
            escort = null;
        }

        public WorldEventMvp Mvp()
        {
            switch (goal?.RuleKind)
            {
                case MvpRule.MostKills:
                    return WorldEventMvpResolver.MostKills(ledger);

                case MvpRule.KillingBlow:
                    return WorldEventMvpResolver.KillingBlow(lastKillerName);

                case MvpRule.KillingBlowAndTopDamage:
                    return WorldEventMvpResolver.KillingBlowAndTopDamage(lastKillerName, topDamagerName);

                default:
                    return WorldEventMvp.None;
            }
        }
    }

    /// <summary>
    /// A wave-based hold's wave counts: waves STARTED (picked and sent), LANDED (at least one monster placed), CLEARED (every
    /// monster of that wave dead), RESOLVED (the spawner finished placing it, landed or failed) and the wave monsters still
    /// standing.
    /// </summary>
    public readonly struct HoldWaveCounts
    {
        public readonly int Started;
        public readonly int Landed;
        public readonly int Cleared;
        public readonly int Live;

        /// <summary>
        /// Waves the spawner has finished placing (landed or failed). A wave is started on the world thread and placed a
        /// moment later on its landblock, so until it resolves its monsters are not yet counted.
        /// </summary>
        public readonly int Resolved;

        /// <summary>Counts where every started wave has already resolved.</summary>
        public HoldWaveCounts(int started, int landed, int cleared, int live)
            : this(started, landed, cleared, live, started)
        {
        }

        public HoldWaveCounts(int started, int landed, int cleared, int live, int resolved)
        {
            Started = started;
            Landed = landed;
            Cleared = cleared;
            Live = live;
            Resolved = resolved;
        }

        public override string ToString() => $"started={Started} resolved={Resolved} landed={Landed} cleared={Cleared} live={Live}";
    }
}
