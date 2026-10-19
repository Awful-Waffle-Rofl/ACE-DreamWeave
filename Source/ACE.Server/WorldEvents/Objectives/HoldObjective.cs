using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// goals.json type "Hold" - protect the targets. The run places the goal's protect.wcid targets (wards)
    /// around the anchor; a share of every wave goes straight for them. The objective:
    ///
    ///   * SUCCEEDS once holdSeconds have passed since the run went Active with at least minSurvivors
    ///     targets still standing (minSurvivors is clamped to the number actually placed);
    ///   * FAILS (<see cref="WorldEventOutcome.FailedObjectiveLost"/>) the moment fewer than that are left;
    ///   * ABORTS (<see cref="WorldEventOutcome.AbortedError"/>) when no target was placed at all within
    ///     <see cref="PlacementGraceSeconds"/> of going Active - a run with nothing to protect is a content or
    ///     placement fault, not a win and not the players' loss.
    ///
    /// The success check sits ahead of the run timers on the same tick (WorldEvent.TickActive), and
    /// WorldEventManager.TryStart refuses holdSeconds above the run's maximum duration, so a held run always
    /// resolves as Success rather than FailedTimeout.
    /// </summary>
    public sealed class HoldObjective : IWorldEventObjective
    {
        /// <summary>Seconds after going Active before "nothing was placed" becomes an abort.</summary>
        public const double PlacementGraceSeconds = 5.0;

        private readonly GoalDef goal;
        private readonly Func<IReadOnlyCollection<uint>> protectedGuids;
        private readonly Func<IReadOnlyCollection<uint>> liveProtectedGuids;
        private readonly Func<double> activeAt;
        private readonly WorldEventParticipation ledger;

        private double now;

        private string lastKillerName;
        private string topDamagerName;

        /// <param name="family">The run's composed monster family display name, for the {family}/{Family} progress tokens. Null reads as the generic fallback.</param>
        /// <param name="waves">Wave-based holds: the run's wave counts, read fresh on every check. Null reads as no waves.</param>
        public HoldObjective(GoalDef goal, Func<IReadOnlyCollection<uint>> protectedGuids,
            Func<IReadOnlyCollection<uint>> liveProtectedGuids, Func<double> activeAt, WorldEventParticipation ledger,
            Func<string> family = null, Func<HoldWaveCounts> waves = null, Func<string> targetHealth = null)
        {
            this.goal = goal;
            this.protectedGuids = protectedGuids ?? (() => Array.Empty<uint>());
            this.liveProtectedGuids = liveProtectedGuids ?? (() => Array.Empty<uint>());
            this.activeAt = activeAt ?? (() => 0);
            this.ledger = ledger;
            this.family = family ?? (() => null);
            this.waves = waves ?? (() => default);
            this.targetHealth = targetHealth ?? (() => "");
        }

        /// <summary>Every standing target's health ("Sir Draithon 80%, Sir Donovan 64%"), for the {targetHealth} token.</summary>
        private readonly Func<string> targetHealth;

        private readonly Func<string> family;
        private readonly Func<HoldWaveCounts> waves;

        /// <summary>Wave-based holds: the sized wave target, raised as the crowd grows, never lowered. Null until first sized.</summary>
        private int? targetWaves;

        /// <summary>True when the goal counts waves (protect.waves).</summary>
        public bool IsWaveBased => Protect?.IsWaveBased ?? false;

        /// <summary>The wave target (protect.waves for the crowd). 0 for a time-only hold.</summary>
        public int TargetWaves
        {
            get
            {
                if (!IsWaveBased)
                    return 0;

                targetWaves ??= CapWaves(Protect.Waves.Resolve(0));

                return targetWaves.Value;
            }
        }

        /// <summary>A resolved wave count, kept within [1, <see cref="ProtectDef.MaxHoldWaves"/>].</summary>
        public static int CapWaves(int resolved) => Math.Clamp(resolved, 1, ProtectDef.EffectiveMaxHoldWaves);

        /// <summary>The wave counts as of now.</summary>
        public HoldWaveCounts Waves => waves();

        /// <summary>Wave-based holds: re-size the wave target against a fresh audience, upwards only.</summary>
        public void OnAudienceResampled(int participantCount)
        {
            if (!IsWaveBased)
                return;

            var current = TargetWaves;

            // Once the finale has started the total is settled: raising it then would turn the finale into
            // an ordinary wave and send more after it.
            if (Waves.Started >= current)
                return;

            var resolved = CapWaves(Protect.Waves.Resolve(Math.Max(0, participantCount)));

            if (resolved > current)
                targetWaves = resolved;
        }

        private double MinSeconds => Math.Max(0, Protect?.MinSeconds ?? ProtectDef.DefaultMinSeconds);

        /// <summary>
        /// Wave-based holds (D6 - pure): whether the cadence may spawn the next wave now. Never once the total
        /// (at most <see cref="ProtectDef.MaxHoldWaves"/>) has started, and not before that wave's paced start
        /// (<see cref="EarliestWaveStart"/>): a group that clears fast waits, it is not sent extra waves.
        /// </summary>
        public static bool WantsAnotherWave(int started, int target, double elapsed, double minSeconds)
        {
            var cap = Math.Min(target, ProtectDef.EffectiveMaxHoldWaves);

            if (started >= cap)
                return false;

            return elapsed >= EarliestWaveStart(started + 1, cap, minSeconds);
        }

        /// <summary>
        /// The earliest a wave may start (D6 - pure): wave <paramref name="waveNumber"/> of
        /// <paramref name="target"/> waits until (n - 1) / (N - 1) of <paramref name="minSeconds"/>, so the waves
        /// are spread evenly and the last starts at minSeconds. Wave 1 (and a one-wave hold) starts at once.
        /// </summary>
        public static double EarliestWaveStart(int waveNumber, int target, double minSeconds)
        {
            if (waveNumber <= 1 || target <= 1 || !(minSeconds > 0) || !double.IsFinite(minSeconds))
                return 0;

            var n = Math.Min(waveNumber, target);

            return minSeconds * (n - 1) / (target - 1);
        }

        /// <summary>This run's <see cref="WantsAnotherWave(int, int, double, double)"/>; always true for a time-only hold.</summary>
        public bool WantsAnotherWave(double now)
        {
            if (!IsWaveBased)
                return true;

            var start = activeAt();
            var elapsed = start > 0 ? Math.Max(0, now - start) : 0;

            return WantsAnotherWave(Waves.Started, TargetWaves, elapsed, MinSeconds);
        }

        /// <summary>Whether the wave about to start (1-based <paramref name="waveNumber"/>) is the final counted wave, which is made a little bigger.</summary>
        public bool IsFinalWave(int waveNumber) => IsWaveBased && waveNumber == TargetWaves;

        /// <summary>
        /// Wave-based holds (D6 - pure): the waves are won when every counted wave (the finale last) has started,
        /// every wave that landed has been cleared and nothing is left standing. No minimum time: minSeconds
        /// only paces the waves (<see cref="EarliestWaveStart"/>), it never holds back a won run.
        /// </summary>
        public static bool WavesWon(HoldWaveCounts counts, int target)
        {
            var cap = Math.Min(target, ProtectDef.EffectiveMaxHoldWaves);

            // Resolved: the in-game bug where the run "won" 9 ms after the finale started, because the finale's
            // monsters (and its champion) had not been placed yet, so nothing of it was alive to count.
            return cap > 0 && counts.Started >= cap && counts.Resolved >= counts.Started
                && counts.Landed > 0 && counts.Cleared >= counts.Landed && counts.Live == 0;
        }

        /// <summary>The local line at the start of each wave: "Wave 3 of 7 - 3 of 3 targets stand." ASCII only.</summary>
        public string WaveStartText(int waveNumber)
        {
            var health = targetHealth() ?? "";
            var line = $"{WorldEventHoldRules.WaveProgress(waveNumber, TargetWaves)} - {Alive} of {Placed} {NounPlural} stand";

            return string.IsNullOrEmpty(health) ? line + "." : $"{line} ({health}).";
        }

        private ProtectDef Protect => goal?.Protect;

        private int HoldSeconds => Math.Max(0, goal?.HoldSeconds ?? 0);

        private string Noun => Protect?.EffectiveNoun ?? ProtectDef.DefaultNoun;

        private string NounPlural => Protect?.EffectiveNounPlural ?? ProtectDef.DefaultNounPlural;

        /// <summary>How many targets were placed (ever adopted).</summary>
        public int Placed => (protectedGuids() ?? Array.Empty<uint>()).Count;

        /// <summary>How many placed targets are still standing.</summary>
        public int Alive
        {
            get
            {
                var placed = protectedGuids() ?? Array.Empty<uint>();
                var live = liveProtectedGuids() ?? Array.Empty<uint>();

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

        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            OnDeathCore(creature?.Guid.Full ?? 0, creature?.GetProperty(PropertyInt.WorldEventId) != null,
                WorldEventMvpResolver.ResolveName(lastDamager), WorldEventMvpResolver.ResolveName(topDamager));
        }

        /// <summary>
        /// Testable core (D6). A protected target's own death changes nothing here - <see cref="Alive"/> reads
        /// the spawner's live set, which the death has already updated - and its killer is a monster, so it is
        /// never the MVP. Any other event creature's death records the killing blow and top damager for the
        /// KillingBlow MVP rules.
        /// </summary>
        public void OnDeathCore(uint guid, bool wasEventCreature, string lastDamagerName, string topDamagerName)
        {
            if (!wasEventCreature)
                return;

            if ((protectedGuids() ?? Array.Empty<uint>()).Contains(guid))
                return;

            lastKillerName = lastDamagerName;
            this.topDamagerName = topDamagerName;
        }

        public void Tick(double now)
        {
            this.now = now;
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

                return IsWaveBased && WavesWon(Waves, TargetWaves);
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
        /// Tokens: {alive}, {total}, {lost}, {noun}, {nounPlural}, {remaining} (the hold time left, m:ss).
        /// ASCII only, like every player-facing string.
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

                // {lost}, {remaining} and the wave tokens first; the shared protect-line filler then does the
                // family, the nouns and the counts, so progress text and target lines read the tokens identically.
                if (template.Contains("{targetHealth}"))
                    template = template.Replace("{targetHealth}", targetHealth() ?? "");

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
    /// A wave-based hold's wave counts: waves STARTED (picked and sent), LANDED (at least one monster
    /// placed), CLEARED (every monster of that wave dead) and the wave monsters still standing.
    /// </summary>
    public readonly struct HoldWaveCounts
    {
        public readonly int Started;
        public readonly int Landed;
        public readonly int Cleared;
        public readonly int Live;

        /// <summary>
        /// Waves the spawner has finished placing (landed or failed). A wave is started on the world thread and
        /// placed a moment later on its landblock, so until it resolves its monsters are not yet counted.
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
