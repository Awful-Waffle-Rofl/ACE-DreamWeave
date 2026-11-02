using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.SpeedCourses
{
    /// <summary>The pure arithmetic and checks of the boss phases (<see cref="SpeedCourseDef.BossPhases"/>).</summary>
    public static class SpeedBossPhaseRules
    {
        /// <summary>
        /// The health floor of a phase threshold: ceil(max x fraction), clamped to [1, max]. 0 for a creature with no health.
        /// The ceiling keeps the floor at or above the threshold, so "health &lt;= floor" fires no later than the fraction.
        /// </summary>
        public static uint FloorFor(uint maxHealth, double fraction)
        {
            if (maxHealth == 0 || double.IsNaN(fraction))
                return 0;

            var raw = Math.Ceiling(maxHealth * Math.Clamp(fraction, 0.0, 1.0));

            if (raw < 1)
                return 1;

            return raw >= maxHealth ? maxHealth : (uint)raw;
        }

        /// <summary>
        /// Problems with a course's boss phases: ids present and unique, thresholds strictly descending in (0, 1],
        /// timeoutSeconds &gt; 0, every hazard id a boss_phase hazard of the course used by exactly one phase, and no
        /// boss_phase hazard left out of every phase. Empty when the course has no phases and no boss_phase hazards.
        /// </summary>
        public static List<string> Validate(SpeedCourseDef c)
        {
            var problems = new List<string>();
            var phases = c?.BossPhases ?? new List<SpeedBossPhaseDef>();
            var hazards = (c?.Hazards ?? new List<SpeedHazardDef>()).Where(h => h != null && !string.IsNullOrWhiteSpace(h.Id))
                .GroupBy(h => h.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var phaseIds = new HashSet<string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            double? previous = null;

            for (var i = 0; i < phases.Count; i++)
            {
                var p = phases[i];

                if (p == null)
                {
                    problems.Add($"boss phase {i} is null");
                    continue;
                }

                var who = $"boss phase '{p.Id}'";

                if (string.IsNullOrWhiteSpace(p.Id))
                    problems.Add($"boss phase {i} has no id");
                else if (!phaseIds.Add(p.Id))
                    problems.Add($"{who}: duplicate id");

                if (!(p.AtFraction > 0 && p.AtFraction <= 1))
                    problems.Add($"{who}: atFraction {Num(p.AtFraction)} must be in (0, 1]");
                else if (previous.HasValue && !(p.AtFraction < previous.Value))
                    problems.Add($"{who}: atFraction {Num(p.AtFraction)} must be below the previous phase's {Num(previous.Value)}");

                if (p.AtFraction > 0 && p.AtFraction <= 1)
                    previous = p.AtFraction;

                if (!(p.TimeoutSeconds > 0) || double.IsInfinity(p.TimeoutSeconds))
                    problems.Add($"{who}: timeoutSeconds {Num(p.TimeoutSeconds)} must be finite and > 0");

                if (p.Hazards == null || p.Hazards.Count == 0)
                    problems.Add($"{who}: hazards is empty");
                else
                    foreach (var id in p.Hazards)
                    {
                        if (string.IsNullOrWhiteSpace(id) || !hazards.TryGetValue(id, out var h))
                            problems.Add($"{who}: hazard '{id}' is not a hazard of this course");
                        else if (!string.Equals(h.Trigger?.Type, SpeedHazardTriggers.BossPhase, StringComparison.Ordinal))
                            problems.Add($"{who}: hazard '{id}' must use trigger {SpeedHazardTriggers.BossPhase} (it has '{h.Trigger?.Type}')");
                        else if (!used.Add(id))
                            problems.Add($"{who}: hazard '{id}' is already a step of another phase (or listed twice)");
                    }
            }

            foreach (var h in hazards.Values)
            {
                if (!string.Equals(h.Trigger?.Type, SpeedHazardTriggers.BossPhase, StringComparison.Ordinal))
                    continue;

                if (!used.Contains(h.Id))
                    problems.Add($"hazard '{h.Id}': trigger {SpeedHazardTriggers.BossPhase} but no boss phase lists it (it would never start)");

                if (!string.IsNullOrEmpty(h.Token))
                    problems.Add($"hazard '{h.Id}': a {SpeedHazardTriggers.BossPhase} hazard cannot carry a Seal token");
            }

            return problems;
        }

        private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>What one attempt to start a phase step came to.</summary>
    public enum SpeedBossStepStart
    {
        /// <summary>The hazard is running.</summary>
        Started,

        /// <summary>The start was refused (or is backing off after a refusal); try again on a later tick.</summary>
        Refused,

        /// <summary>The step can never start (hazard_mech_enabled off, unknown hazard): it counts as done.</summary>
        Disabled,
    }

    /// <summary>The boss phase machine -> the course host (the hazard slots) and the world (the boss's guard).</summary>
    public interface ISpeedBossPhasePort
    {
        /// <summary>World.SetBossGuard on the course's boss.</summary>
        void SetGuard(bool immune, uint floorHp);

        /// <summary>Starts boss_phase hazard <paramref name="hazardId"/>.</summary>
        SpeedBossStepStart StartStep(string hazardId, double now);

        /// <summary>Stops hazard <paramref name="hazardId"/> if it is running; its end is then not reported back.</summary>
        void StopStep(string hazardId, string reason);

        void Log(string line);
    }

    /// <summary>
    /// The boss immunity phases of one course run, driven on the landblock thread by the hazard host:
    /// <list type="bullet">
    /// <item>The boss spawns with health floor FloorFor(max, phase 0 threshold), immune when that threshold is 1 (the
    /// world writes it before EnterWorld, from <see cref="SpeedCreatureStamp.GuardFloorFraction"/>).</item>
    /// <item>The next phase starts on the first tick its boss's health is at or below its floor and the runner is in its
    /// triggerRegion (when set): the boss goes immune, pinned at that floor, and the phase's hazards run one after
    /// another. The boss keeps fighting.</item>
    /// <item>A step is done when its hazard ends on its own (any outcome but a host stop), when it cannot start at all
    /// (<see cref="SpeedBossStepStart.Disabled"/>), or when its starts have been refused for
    /// <see cref="RefusalGiveUpSeconds"/> without a break. The phase is done when its last step is, or when
    /// timeoutSeconds have passed since it started (the running step is stopped).</item>
    /// <item>A finished phase drops immunity and lowers the floor to the next phase's, or to 0 after the last: the boss is
    /// then killable.</item>
    /// </list>
    /// </summary>
    public sealed class SpeedBossPhaseMachine
    {
        /// <summary>A step whose starts keep being refused this long is skipped (SpeedCourseHazardHost.RefusalGrantSeconds).</summary>
        public const double RefusalGiveUpSeconds = SpeedCourseHazardHost.RefusalGrantSeconds;

        private readonly IReadOnlyList<SpeedBossPhaseDef> phases;
        private readonly ISpeedBossPhasePort port;

        private uint maxHealth;
        private bool armed;
        private bool finished;

        private double phaseStartedAt;
        private string runningStep;
        private double? firstRefusedAt;

        /// <summary>The immunity the boss holds as far as the machine knows: the spawn guard's (immune when phase 0 is the entry phase), then each SetGuard's.</summary>
        private bool guardImmune;

        public SpeedBossPhaseMachine(IReadOnlyList<SpeedBossPhaseDef> phases, ISpeedBossPhasePort port)
        {
            this.phases = phases ?? throw new ArgumentNullException(nameof(phases));
            this.port = port ?? throw new ArgumentNullException(nameof(port));

            guardImmune = phases.Count > 0 && phases[0] != null && phases[0].AtFraction >= 1.0;
        }

        /// <summary>The phase running or next to run; phases.Count once every phase is done.</summary>
        public int PhaseIndex { get; private set; }

        /// <summary>A phase is running (the boss is immune).</summary>
        public bool InPhase { get; private set; }

        /// <summary>The step of the running phase.</summary>
        public int StepIndex { get; private set; }

        /// <summary>Every phase is done: the boss's floor is 0.</summary>
        public bool Killable => PhaseIndex >= phases.Count && !InPhase;

        /// <summary>The floor the boss holds right now (0 once killable).</summary>
        public uint CurrentFloor => armed && PhaseIndex < phases.Count ? SpeedBossPhaseRules.FloorFor(maxHealth, phases[PhaseIndex].AtFraction) : 0u;

        /// <summary>
        /// One tick. <paramref name="runnerIn"/> answers whether the runner stands in a phase's triggerRegion.
        /// Nothing happens before the boss is alive; a dead boss ends the machine (its running step is stopped).
        /// </summary>
        public void Tick(double now, bool bossAlive, uint currentHealth, uint bossMaxHealth, Func<SpeedHazardRegionDef, bool> runnerIn)
        {
            if (finished)
                return;

            if (!bossAlive)
            {
                if (armed)
                {
                    finished = true;

                    if (runningStep != null)
                        port.StopStep(runningStep, "the boss is dead");

                    runningStep = null;
                    InPhase = false;
                }

                return;
            }

            if (!armed)
            {
                if (bossMaxHealth == 0)
                    return;

                armed = true;
                maxHealth = bossMaxHealth;
                port.Log($"boss phases armed: max health {maxHealth}, first floor {CurrentFloor} ({phases.Count} phase(s))");
            }
            else if (!InPhase && PhaseIndex < phases.Count && bossMaxHealth > 0 && bossMaxHealth != maxHealth)
            {
                // Max health drifted (a vital buff or debuff) between phases: the pending floor follows the live max, so
                // the next threshold stays the authored fraction of the boss's real health. A running phase keeps the
                // floor it started with (its health is pinned there).
                var before = CurrentFloor;
                maxHealth = bossMaxHealth;
                var after = CurrentFloor;

                if (after != before)
                {
                    port.SetGuard(guardImmune, after);
                    port.Log($"boss max health now {maxHealth}: floor {before} -> {after}");
                }
            }

            if (InPhase)
            {
                RunPhase(now);
                return;
            }

            if (PhaseIndex >= phases.Count)
                return;

            var phase = phases[PhaseIndex];
            var floor = SpeedBossPhaseRules.FloorFor(maxHealth, phase.AtFraction);

            if (currentHealth > floor)
                return;

            if (phase.TriggerRegion != null && (runnerIn == null || !runnerIn(phase.TriggerRegion)))
                return;

            InPhase = true;
            StepIndex = 0;
            phaseStartedAt = now;
            runningStep = null;
            firstRefusedAt = null;

            guardImmune = true;
            port.SetGuard(true, floor);
            port.Log($"boss phase '{phase.Id}' started at health {currentHealth}/{maxHealth} (floor {floor}): immune, {phase.Hazards?.Count ?? 0} step(s)");

            RunPhase(now);
        }

        /// <summary>The hazard host heard hazard <paramref name="hazardId"/> end on its own (not a host stop).</summary>
        public void OnStepEnded(string hazardId, double now)
        {
            if (!InPhase || runningStep == null || !string.Equals(runningStep, hazardId, StringComparison.Ordinal))
                return;

            port.Log($"boss phase '{phases[PhaseIndex].Id}' step '{hazardId}' ended");

            runningStep = null;
            NextStep(now);
        }

        private void RunPhase(double now)
        {
            var phase = phases[PhaseIndex];

            if (now - phaseStartedAt >= phase.TimeoutSeconds)
            {
                if (runningStep != null)
                    port.StopStep(runningStep, $"boss phase '{phase.Id}' timed out");

                port.Log($"boss phase '{phase.Id}' timed out after {phase.TimeoutSeconds:0.#}s at step {StepIndex}");
                runningStep = null;
                CompletePhase();
                return;
            }

            // Start the current step; a step that can never start is skipped and the next one tried this same tick.
            var steps = phase.Hazards ?? new List<string>();

            while (InPhase && runningStep == null)
            {
                if (StepIndex >= steps.Count)
                {
                    CompletePhase();
                    return;
                }

                var id = steps[StepIndex];
                var result = port.StartStep(id, now);

                switch (result)
                {
                    case SpeedBossStepStart.Started:
                        firstRefusedAt = null;
                        runningStep = id;
                        return;

                    case SpeedBossStepStart.Disabled:
                        port.Log($"boss phase '{phase.Id}' step '{id}' cannot start; skipped");
                        NextStep(now, startNow: false);
                        break;

                    default:
                        firstRefusedAt ??= now;

                        if (now - firstRefusedAt.Value < RefusalGiveUpSeconds)
                            return;

                        port.Log($"boss phase '{phase.Id}' step '{id}' refused for {now - firstRefusedAt.Value:0}s; skipped");
                        NextStep(now, startNow: false);
                        break;
                }
            }
        }

        private void NextStep(double now, bool startNow = true)
        {
            StepIndex++;
            firstRefusedAt = null;

            var steps = phases[PhaseIndex].Hazards ?? new List<string>();

            if (StepIndex >= steps.Count)
            {
                CompletePhase();
                return;
            }

            if (startNow)
                RunPhase(now);
        }

        private void CompletePhase()
        {
            var done = phases[PhaseIndex];

            InPhase = false;
            runningStep = null;
            firstRefusedAt = null;
            PhaseIndex++;

            var nextFloor = CurrentFloor;
            guardImmune = false;
            port.SetGuard(false, nextFloor);
            port.Log(PhaseIndex < phases.Count
                ? $"boss phase '{done.Id}' done: vulnerable down to floor {nextFloor} (next phase '{phases[PhaseIndex].Id}')"
                : $"boss phase '{done.Id}' done: the last phase; the boss is killable");
        }
    }
}
