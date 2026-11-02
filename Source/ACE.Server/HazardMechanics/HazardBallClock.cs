using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>What a wave should do next, as <see cref="BallWaveClock.NextStep"/> answers it.</summary>
    public enum BallWaveStep
    {
        /// <summary>Some ball of the wave is still spawning, waiting to launch, or rolling.</summary>
        Wait,

        /// <summary>Every ball of the wave has finished or died; a later wave is still to run.</summary>
        StartNextWave,

        /// <summary>Every ball of the last wave has finished or died: end the run Completed.</summary>
        Complete,
    }

    public enum BallState
    {
        Spawned,
        Launched,
        Ended,
        Dead,

        /// <summary>Still outstanding when the timeout fallback fired: finished, but NOT proof the ball was lost.</summary>
        WrittenOff,

        /// <summary>
        /// hit_lag &gt; 0 only: the leg ENDED (the visible roll is over) but the last hit_lag seconds of its path are still
        /// owed their lagged sweep (<see cref="BallHits"/>). Hittable, NOT finished: the wave waits for it. Never entered
        /// with hit_lag 0, and never by a ball that died before its leg ended.
        /// </summary>
        Tail,
    }

    /// <summary>
    /// What a finished wave looked like, captured by <see cref="BallWaveClock.Close"/> BEFORE the next wave resets the
    /// clock. The mechanic decides the run's fate from this snapshot alone (<see cref="BallVerdicts"/>).
    /// </summary>
    public readonly struct BallWaveResult
    {
        public BallWaveResult(BallWaveStep step, int wave, int waves, bool allLost, int runLost, int runWrittenOff)
        {
            RunWrittenOff = runWrittenOff;
            Step = step;
            Wave = wave;
            Waves = waves;
            AllLost = allLost;
            RunLost = runLost;
        }

        public BallWaveStep Step { get; }
        public int Wave { get; }
        public int Waves { get; }

        /// <summary>Every planned ball of this wave is PROVEN lost (failed to spawn, or its object died). A timeout write-off is not proof.</summary>
        public bool AllLost { get; }

        /// <summary>Balls lost over the whole run so far (for the log line); includes the written-off ones.</summary>
        public int RunLost { get; }

        /// <summary>Balls the timeout fallback wrote off over the whole run (not proven lost; a lag spike looks the same).</summary>
        public int RunWrittenOff { get; }
    }

    /// <summary>
    /// PURE verdicts for a balls run (shared dead-handle policy). <see cref="HazardOutcome.Aborted"/> means the mechanic
    /// could not continue (content lost, no routes); a finished script is Completed; null means keep going.
    /// </summary>
    public static class BallVerdicts
    {
        /// <summary>A wave that planned no routes at all: nothing can roll in this region.</summary>
        public static (HazardOutcome Outcome, string Reason) EmptyPlan(int wave) => (HazardOutcome.Aborted, $"no routes in wave {wave}");

        /// <summary>What the run does after a wave: null = wait or start the next wave, else the end.</summary>
        public static (HazardOutcome Outcome, string Reason)? WaveVerdict(BallWaveResult r)
        {
            if (r.Step == BallWaveStep.Wait)
                return null;

            if (r.AllLost)
                return (HazardOutcome.Aborted, $"all balls lost in wave {r.Wave}/{r.Waves}");

            if (r.Step == BallWaveStep.Complete)
                return (HazardOutcome.Completed, $"{r.Waves} wave(s) run, {r.RunLost} ball(s) lost, {r.RunWrittenOff} written off by timeout");

            return null;
        }
    }

    /// <summary>
    /// The PURE lifecycle of a balls run (shared invariant 6): which balls are hittable now, whether the wave is over
    /// and what comes next. The mechanic feeds it events (spawned, spawn failed, launched, leg ended, died) and asks;
    /// it holds no handle, no world and no clock of its own, so every guard here is tested without a landblock.
    ///
    /// A BALL IS HITTABLE ONLY WHILE <see cref="BallState.Launched"/> OR <see cref="BallState.Tail"/>: not while it waits
    /// out its lead at the start line, not once its lagged sweep is done, not once dead. A wave is OVER when every ball it
    /// planned is accounted for: failed to spawn, ended its leg AND its tail, or died. That is what ends a wave, not a
    /// clock; the mechanic keeps a timeout fallback sized by <see cref="BallRules.Settle"/> plus hit_lag for a ball that
    /// vanishes silently. A wave whose only unfinished balls are tails is <see cref="TailOnly"/>: over on screen, still
    /// swept, and not counted in flight by the cadence caps (<see cref="BallCadence.InFlight"/>, <see cref="VisibleRemaining"/>).
    ///
    /// DEAD HANDLE POLICY (all patterns, the same as the wisps' and the memory game's): a dead ball is lost and
    /// dropped, the wave carries on without it, and the run is Completed after the last wave however many balls
    /// were lost - EXCEPT a wave in which every planned ball was PROVEN lost (<see cref="WaveAllLost"/>), which ends the
    /// run Aborted.
    /// </summary>
    public sealed class BallWaveClock
    {
        private readonly Dictionary<int, BallState> states = new Dictionary<int, BallState>();
        private readonly Dictionary<int, double> sweptTo = new Dictionary<int, double>();

        private int expected;
        private int failedSpawns;
        private int unreportedWrittenOff;
        private double lastNow = double.NaN;

        public BallWaveClock(int waves)
        {
            Waves = Math.Max(1, waves);
        }

        public int Waves { get; }

        /// <summary>1-based number of the wave in progress (0 before the first).</summary>
        public int Wave { get; private set; }

        /// <summary>Balls lost to a failed spawn or a death before their leg ended, over the whole run.</summary>
        public int Lost { get; private set; }

        /// <summary>Balls the timeout fallback wrote off over the whole run (a subset of <see cref="Lost"/>).</summary>
        public int WrittenOff { get; private set; }

        /// <summary>Balls that actually spawned, over the whole run.</summary>
        public int Spawned { get; private set; }

        public bool IsLastWave => Wave >= Waves;

        private double maxDelta;

        /// <summary>The longest gap between two consecutive driver ticks seen (seconds); the 100 ms default until two are seen.</summary>
        public double TickSeconds => maxDelta > 0.0 ? maxDelta : HazardDefaults.TickMs / 1000.0;

        /// <summary>Call every driver tick: derives the tick length from consecutive <c>ctx.Now</c> values (the context does not expose it).</summary>
        public void ObserveNow(double now)
        {
            if (!double.IsNaN(lastNow) && now > lastNow)
                maxDelta = Math.Max(maxDelta, now - lastNow);

            lastNow = now;
        }

        /// <summary>Begins the next wave of <paramref name="expectedBalls"/> planned balls. Forgets the previous wave's balls.</summary>
        public int StartWave(int expectedBalls)
        {
            states.Clear();
            sweptTo.Clear();
            expected = Math.Max(0, expectedBalls);
            failedSpawns = 0;
            unreportedWrittenOff = 0;
            return ++Wave;
        }

        public void OnSpawned(int id)
        {
            states[id] = BallState.Spawned;
            Spawned++;
        }

        /// <summary>A planned ball that never spawned (no cell, refused entry): the wave carries on without it.</summary>
        public void OnSpawnFailed()
        {
            failedSpawns++;
            Lost++;
        }

        public void OnLaunched(int id)
        {
            if (states.TryGetValue(id, out var s) && s == BallState.Spawned)
                states[id] = BallState.Launched;
        }

        /// <summary>The ball's leg ended. <paramref name="tail"/> (hit_lag &gt; 0): it enters <see cref="BallState.Tail"/>, still owed its lagged sweep; else it is <see cref="BallState.Ended"/>.</summary>
        public void OnLegEnd(int id, bool tail = false)
        {
            if (states.TryGetValue(id, out var s) && (s == BallState.Launched || s == BallState.Spawned))
                states[id] = tail ? BallState.Tail : BallState.Ended;
        }

        /// <summary>The ball's lagged tail is swept (or its object died in the tail): it is <see cref="BallState.Ended"/>, never lost.</summary>
        public void OnTailDone(int id)
        {
            if (states.TryGetValue(id, out var s) && s == BallState.Tail)
                states[id] = BallState.Ended;
        }

        public bool InTail(int id) => states.TryGetValue(id, out var s) && s == BallState.Tail;

        /// <summary>Balls whose leg ended and whose lagged tail is still owed its sweep.</summary>
        public int Tails => states.Values.Count(s => s == BallState.Tail);

        /// <summary>The ball's object died. Counts as lost unless its leg had already ended.</summary>
        public void OnDied(int id)
        {
            if (states.TryGetValue(id, out var s) && (s == BallState.Spawned || s == BallState.Launched))
            {
                states[id] = BallState.Dead;
                Lost++;
            }
        }

        public BallState? StateOf(int id) => states.TryGetValue(id, out var s) ? s : (BallState?)null;

        public bool IsHittable(int id) => states.TryGetValue(id, out var s) && (s == BallState.Launched || s == BallState.Tail);

        /// <summary>The ids that may hit a player right now: rolling, or in their lagged tail.</summary>
        public IReadOnlyList<int> Hittable() => states.Where(kv => kv.Value == BallState.Launched || kv.Value == BallState.Tail).Select(kv => kv.Key).ToList();

        /// <summary>Ids that are not yet resolved (spawned or launched): the ones to check for a dead object.</summary>
        public IReadOnlyList<int> Unresolved() => states.Where(kv => kv.Value == BallState.Spawned || kv.Value == BallState.Launched).Select(kv => kv.Key).ToList();

        /// <summary>The run time up to which this ball's path has been swept for hits, or null before its first sweep.</summary>
        public double? SweptTo(int id) => sweptTo.TryGetValue(id, out var t) ? t : (double?)null;

        public void SetSweptTo(int id, double t) => sweptTo[id] = t;

        /// <summary>The planned balls not yet accounted for (the complement of <see cref="WaveOver"/>): unspawned, waiting for their lead, rolling, or in their lagged tail.</summary>
        public int Remaining => Math.Max(0, expected - failedSpawns - unreportedWrittenOff - states.Values.Count(s => s == BallState.Ended || s == BallState.Dead || s == BallState.WrittenOff));

        /// <summary>
        /// <see cref="Remaining"/> without the balls in their lagged tail: the balls still on their way or rolling, as a
        /// player sees them. What a cadence ball budget counts (<see cref="BallCadence.BallsInFlight"/>). Equal to Remaining
        /// with hit_lag 0.
        /// </summary>
        public int VisibleRemaining => Math.Max(0, Remaining - Tails);

        /// <summary>Every planned ball is accounted for: failed to spawn, ended (its tail swept too), or dead.</summary>
        public bool WaveOver => Wave > 0 && failedSpawns + unreportedWrittenOff + states.Values.Count(s => s == BallState.Ended || s == BallState.Dead || s == BallState.WrittenOff) >= expected;

        /// <summary>
        /// The wave is not over, but only lagged tails are left (<see cref="VisibleRemaining"/> 0): over on screen, still
        /// swept. Never true with hit_lag 0. The cadence caps do not count such a wave in flight.
        /// </summary>
        public bool TailOnly => Wave > 0 && !WaveOver && VisibleRemaining == 0;

        /// <summary>
        /// The wave is over and EVERY planned ball is proven lost: it failed to spawn or its object died. The mechanic
        /// ends such a run Aborted (the wisps' "all wisps lost", the memory game's lost pad); a wave that lost only
        /// some of its balls carries on. A ball written off by the timeout fallback is NOT proof of loss (a lag spike
        /// delays the leg-end report), so a wave the fallback finished alone never aborts.
        /// </summary>
        public bool WaveAllLost => WaveOver && expected > 0 && failedSpawns + states.Values.Count(s => s == BallState.Dead) >= expected;

        /// <summary>
        /// Snapshots the finished wave. Call it BEFORE anything starts the next wave (StartWave resets the per-wave
        /// state), and decide the run's fate from the result only (<see cref="BallVerdicts.WaveVerdict"/>).
        /// </summary>
        public BallWaveResult Close() => new BallWaveResult(NextStep(), Wave, Waves, WaveAllLost, Lost, WrittenOff);

        public BallWaveStep NextStep() => !WaveOver ? BallWaveStep.Wait : IsLastWave ? BallWaveStep.Complete : BallWaveStep.StartNextWave;

        /// <summary>
        /// The timeout fallback: every ball still unresolved is written off (counted lost for the log, finished for the
        /// wave, NOT proof for an abort) so the wave can end. A ball still in its lagged tail ended its leg, so it is
        /// ended (not lost, not counted in the return).
        /// </summary>
        public int ForceResolve()
        {
            var n = 0;

            foreach (var id in states.Where(kv => kv.Value == BallState.Tail).Select(kv => kv.Key).ToList())
                states[id] = BallState.Ended;

            foreach (var id in Unresolved())
            {
                states[id] = BallState.WrittenOff;
                Lost++;
                WrittenOff++;
                n++;
            }

            // balls that never even reported a spawn or a failure
            var unreported = expected - failedSpawns - unreportedWrittenOff - states.Count;

            if (unreported > 0)
            {
                unreportedWrittenOff += unreported;
                Lost += unreported;
                WrittenOff += unreported;
                n += unreported;
            }

            return n;
        }
    }

    /// <summary>
    /// PURE: the ball budget of a cadence run (args max_balls, or the run's live count, which wins): a due wave launches
    /// when nothing is in flight or in-flight + the wave's planned size fits. No budget (both null) admits everything,
    /// so a run with neither is exactly the cadence it was.
    /// </summary>
    public static class BallBudget
    {
        public const int Min = 1;

        public const int Max = 512;

        /// <summary>The budget: <paramref name="liveCount"/> when given, else <paramref name="maxBalls"/>, clamped to Min..Max; null when both are null.</summary>
        public static int? For(int? liveCount, int? maxBalls)
        {
            var asked = liveCount ?? maxBalls;
            return asked.HasValue ? Math.Clamp(asked.Value, Min, Max) : (int?)null;
        }

        /// <summary>True when a wave of <paramref name="waveSize"/> balls may launch with <paramref name="inFlight"/> balls already planned or rolling. An empty flight always admits (a wave bigger than the budget still runs, alone).</summary>
        public static bool Admits(int? budget, int inFlight, int waveSize)
            => budget == null || inFlight <= 0 || waveSize <= 0 || inFlight + waveSize <= budget.Value;
    }

    /// <summary>
    /// THE CADENCE MODE of a balls run (args wave_every, opt-in): waves launch at fixed run times
    /// (<see cref="BallRules.LaunchTimes"/>) and OVERLAP. Wave k+1 never waits on wave k's balls, so several waves can be
    /// in flight at once, each with its own <see cref="BallWaveClock"/> (one wave each) for its hittable balls, its
    /// timeout write-off and its own verdict. PURE: the mechanic asks <see cref="Due"/> every tick with the run's elapsed
    /// time and launches what it returns; nothing a ball does can delay a launch, except the opt-in max_in_flight cap.
    ///
    /// VERDICTS (the same dead-handle policy as the sequential mode): a wave whose every planned ball is PROVEN lost
    /// ends the run Aborted; the run is Completed once the last wave has launched AND every open wave has finished.
    /// </summary>
    public sealed class BallCadence
    {
        private readonly IReadOnlyList<double> launchAt;
        private readonly Dictionary<int, BallWaveClock> open = new Dictionary<int, BallWaveClock>();

        private int closedLost;
        private int closedWrittenOff;
        private int closedSpawned;

        private readonly BallParams p;

        /// <summary>distinct_dirs: the walls each open wave is using (row: one, cross: two). Empty when the run is not distinct.</summary>
        private readonly Dictionary<int, BallSide[]> wallsOf = new Dictionary<int, BallSide[]>();

        private readonly string pattern;

        /// <summary>
        /// <paramref name="pattern"/> matters only with distinct_dirs=1 on a row or cross: then a due wave is also held
        /// while no wall is free for it (<see cref="Due"/>). Without it (or the arg) nothing is held for walls.
        /// </summary>
        public BallCadence(BallParams p, string pattern = null)
        {
            this.p = p;
            this.pattern = pattern;
            launchAt = BallRules.LaunchTimes(p);
            Waves = launchAt.Count;
        }

        /// <summary>True when the run draws each wave's walls from the free ones and holds a wave while none is (distinct_dirs=1, row or cross).</summary>
        public bool Distinct => p.DistinctDirs && (pattern == BallPatterns.Row || pattern == BallPatterns.Cross);

        /// <summary>The walls the open waves in flight are using (distinct_dirs; a <see cref="BallWaveClock.TailOnly"/> wave's walls are free). Empty when the run is not distinct.</summary>
        public IReadOnlyCollection<BallSide> BusyWalls => wallsOf.Where(kv => !(open.TryGetValue(kv.Key, out var c) && c.TailOnly)).SelectMany(kv => kv.Value).ToList();

        /// <summary>True when the last <see cref="Due"/> held a wave because no wall was free for it (distinct_dirs).</summary>
        public bool HeldForWalls { get; private set; }

        public int Waves { get; }

        /// <summary>Waves launched so far.</summary>
        public int Launched { get; private set; }

        /// <summary>Run seconds of every launch, wave 1 first (at 0).</summary>
        public IReadOnlyList<double> LaunchTimes => launchAt;

        public bool AllLaunched => Launched >= Waves && HeldWave == 0;

        /// <summary>Balls planned and not yet accounted for, over every open wave, lagged tails excluded (<see cref="BallWaveClock.VisibleRemaining"/>; the same as Remaining with hit_lag 0).</summary>
        public int BallsInFlight => open.Values.Sum(c => c.VisibleRemaining);

        /// <summary>The wave a ball budget is holding (planned, counted launched, not opened), or 0. While one is held no later wave is handed out, so the waves stay in order.</summary>
        public int HeldWave { get; private set; }

        /// <summary>What the budget saw when it held <see cref="HeldWave"/>: balls in flight, the wave's size and the budget.</summary>
        public (int InFlight, int Size, int Budget) HeldFor { get; private set; }

        private object heldPlan;

        /// <summary>
        /// <see cref="Due"/> with the ball budget: each due wave is PLANNED by <paramref name="plan"/> (null = stop; the
        /// callback has ended the run), then launched through <paramref name="open"/> (false = stop) if
        /// <see cref="BallBudget.Admits"/> says so, else it is HELD with its plan, kept as it is (no re-plan, so the
        /// random numbers are drawn in the same order as without a budget) and retried first on the next call. With
        /// <paramref name="budget"/> returning null nothing is ever held and this is Due-then-plan-then-open, wave by wave.
        /// </summary>
        public void Launch<T>(double elapsed, Func<int, T> plan, Func<T, int> size, Func<int?> budget, Func<int, T, bool> open) where T : class
        {
            if (heldPlan != null)
            {
                var held = (T)heldPlan;

                if (!BallBudget.Admits(budget(), BallsInFlight, size(held)))
                    return;

                var wave = HeldWave;

                heldPlan = null;
                HeldWave = 0;
                lastLaunchAt = elapsed;

                if (!open(wave, held))
                    return;
            }

            foreach (var n in Due(elapsed))
            {
                var planned = plan(n);

                if (planned == null)
                    return;

                var limit = budget();
                var inFlight = BallsInFlight;
                var waveSize = size(planned);

                if (!BallBudget.Admits(limit, inFlight, waveSize))
                {
                    // wave n stays counted as launched (its time came) but is not open; later waves go back to unlaunched
                    Launched = n;
                    HeldWave = n;
                    heldPlan = planned;
                    HeldFor = (inFlight, waveSize, limit.Value);
                    return;
                }

                if (!open(n, planned))
                    return;
            }
        }

        /// <summary>The waves still open (launched and not yet finished), lowest first.</summary>
        public IReadOnlyList<int> OpenWaves => open.Keys.OrderBy(k => k).ToList();

        public BallWaveClock ClockOf(int wave) => open.TryGetValue(wave, out var c) ? c : null;

        /// <summary>Balls lost over the whole run, open waves included.</summary>
        public int Lost => closedLost + open.Values.Sum(c => c.Lost);

        public int WrittenOff => closedWrittenOff + open.Values.Sum(c => c.WrittenOff);

        public int Spawned => closedSpawned + open.Values.Sum(c => c.Spawned);

        /// <summary>
        /// The waves due by <paramref name="elapsed"/> run seconds that have not launched yet, in order (a slow tick can
        /// make two due at once). Each is counted launched as it is returned; open it with <see cref="Open"/> before
        /// the next call. With max_in_flight a due wave is HELD while that wave's cap (<see cref="CapFor"/>) of earlier
        /// waves is still open, and comes back from the first call after one of them closes; later waves queue behind
        /// it in order. Without it nothing is ever held.
        /// </summary>
        public IReadOnlyList<int> Due(double elapsed)
        {
            var due = new List<int>();
            HeldForWalls = false;

            // a wave held by the ball budget goes first (Launch); nothing is handed out behind it
            if (HeldWave != 0)
                return due;

            while (Launched < Waves && elapsed + 1e-9 >= DueAt(Launched + 1))
            {
                var cap = CapFor(Launched + 1);

                if (cap.HasValue && InFlight + due.Count >= cap.Value)
                {
                    Held = true;
                    return due;
                }

                // distinct_dirs: every wave due in this call will take a wall of each axis it uses, so count them too
                if (Distinct && !BallPatterns.WallFree(pattern, BusyWalls, due.Count))
                {
                    Held = true;
                    HeldForWalls = true;
                    return due;
                }

                due.Add(++Launched);
                lastLaunchAt = elapsed;
            }

            Held = false;
            return due;
        }

        private double lastLaunchAt;

        /// <summary>
        /// When wave <paramref name="wave"/> is due. Uncapped: its fixed time (<see cref="LaunchTimes"/>). Capped
        /// (max_in_flight): the schedule SLIDES, wave k+1 being due its period (launchAt[k+1] - launchAt[k]) after wave
        /// k ACTUALLY launched, so a wave the cap held does not leave a backlog that then launches in a burst; the
        /// spacing between launches is never under the cadence's own.
        /// </summary>
        public double DueAt(int wave)
        {
            if (wave <= 1 || !p.MaxInFlight.HasValue)
                return launchAt[wave - 1];

            return lastLaunchAt + (launchAt[wave - 1] - launchAt[wave - 2]);
        }

        /// <summary>True when the last <see cref="Due"/> held a wave whose time had come because the in-flight cap was full.</summary>
        public bool Held { get; private set; }

        /// <summary>The in-flight cap for wave <paramref name="wave"/> (max_in_flight ramped and rounded), or null when uncapped.</summary>
        public int? CapFor(int wave) => p.MaxInFlight.HasValue ? BallRules.ForWave(p, wave).MaxInFlight : null;

        /// <summary>
        /// Waves launched and not yet finished, except those with only lagged tails left (<see cref="BallWaveClock.TailOnly"/>):
        /// the max_in_flight count is what a player SEES rolling, so a wave frees its slot at its visible end while its
        /// tail is still swept (the wave stays open, and the run is not Completed, until the tail is done). The same as
        /// the open count with hit_lag 0.
        /// </summary>
        public int InFlight => open.Values.Count(c => !c.TailOnly);

        /// <summary>Starts tracking launched wave <paramref name="wave"/> of <paramref name="expectedBalls"/> planned balls.</summary>
        public BallWaveClock Open(int wave, int expectedBalls, IEnumerable<BallSide> walls = null)
        {
            var clock = new BallWaveClock(1);
            clock.StartWave(expectedBalls);
            open[wave] = clock;

            // distinct_dirs: the wave holds its walls from now (its spawn and lead included) until it closes
            if (Distinct && walls != null)
                wallsOf[wave] = walls.Distinct().ToArray();

            return clock;
        }

        /// <summary>
        /// Wave <paramref name="wave"/>'s snapshot, or null while it is still running (or not open). A finished wave is
        /// removed and its counts kept for the run totals; the run's fate then comes from <see cref="Verdict"/>.
        /// </summary>
        public BallWaveResult? Close(int wave)
        {
            if (!open.TryGetValue(wave, out var clock))
                return null;

            var result = clock.Close();

            if (result.Step == BallWaveStep.Wait)
                return null;

            open.Remove(wave);
            wallsOf.Remove(wave);
            closedLost += clock.Lost;
            closedWrittenOff += clock.WrittenOff;
            closedSpawned += clock.Spawned;
            return result;
        }

        /// <summary>
        /// What the run does after wave <paramref name="wave"/> closed with <paramref name="result"/>: Aborted when every
        /// planned ball of it was proven lost, Completed when it was the last open wave after the last launch, else null.
        /// </summary>
        public (HazardOutcome Outcome, string Reason)? Verdict(int wave, BallWaveResult result)
        {
            if (result.AllLost)
                return (HazardOutcome.Aborted, $"all balls lost in wave {wave}/{Waves}");

            if (AllLaunched && open.Count == 0)
                return (HazardOutcome.Completed, $"{Waves} wave(s) run on a cadence, {Lost} ball(s) lost, {WrittenOff} written off by timeout");

            return null;
        }
    }

    /// <summary>
    /// PURE: the points a moving ball must be tested at so a fast ball cannot step over a player between two driver
    /// ticks (HazardContext.HitPlayers tests one centre at a time).
    /// </summary>
    public static class BallSweep
    {
        /// <summary>
        /// The positions from <paramref name="t0"/> to <paramref name="t1"/> (run seconds), no two more than
        /// <paramref name="maxStep"/> apart in the xy plane; the point at t0 itself only when
        /// <paramref name="includeStart"/> (the previous sweep already tested it). Empty when t1 &lt;= t0 and the
        /// start is not wanted.
        /// </summary>
        public static IReadOnlyList<Vector3> SweepPoints(Func<double, Vector3> positionAt, double t0, double t1, float maxStep, bool includeStart = false)
        {
            var points = new List<Vector3>();

            if (t1 < t0)
                return points;

            var a = positionAt(t0);

            if (t1 == t0)
            {
                if (includeStart)
                    points.Add(a);

                return points;
            }

            var b = positionAt(t1);
            var step = Math.Max(0.05f, maxStep);
            var n = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)) / step));

            for (var i = includeStart ? 0 : 1; i <= n; i++)
                points.Add(i == 0 ? a : i == n ? b : positionAt(t0 + (t1 - t0) * i / n));

            return points;
        }

        /// <summary>The same over a flying path, the times clamped into the path's own span.</summary>
        public static IReadOnlyList<Vector3> SweepPoints(HazardPath path, double t0, double t1, float maxStep, bool includeStart = false)
            => SweepPoints(t => path.PositionAt(Math.Clamp(t, path.T0, path.EndTime)), Math.Max(t0, path.T0), Math.Min(t1, path.EndTime), maxStep, includeStart);
    }

    /// <summary>
    /// PURE: THE ONE HIT ROUTINE of a ball (shared invariant 3), with its latency compensation (args hit_lag,
    /// <see cref="BallParams.HitLag"/>). The mechanic calls it at the leg end (<see cref="LegEnded"/>, from the driver's
    /// AdvanceMotion, before the end snap) and every tick for every hittable ball (<see cref="Tick"/>); the hit itself is
    /// the caller's <c>hit</c> (ctx.HitPlayers at one centre), so this is tested without a world.
    ///
    /// THE INVARIANT. Every point of a ball's path at path time t is hit-tested against player positions no earlier than
    /// server time t + hit_lag: a sweep at server time now ends at <see cref="LaggedEnd"/> = min(now - hit_lag, leg end).
    /// Nothing is skipped: the sweep is contiguous (<see cref="BallWaveClock.SweptTo"/>), in steps of at most half a reach
    /// (<see cref="BallSweep"/>), the leg's start point included, and a sweep that would end before the leg's start does
    /// nothing at all (it does not mark the start swept).
    ///
    /// THE TAIL. With hit_lag &gt; 0 a leg end does NOT sweep to the end: the ball enters <see cref="BallState.Tail"/>, its
    /// object is kept (HazardContext.HitPlayers refuses a dead handle), and each tick sweeps the finished path further
    /// until now - hit_lag reaches its end; only then is it finished and the caller destroys it. A tail ball whose object
    /// dies is finished without a hit. A ball that DIED before its leg ended never enters a tail and never hits again.
    /// hit_lag 0 is exactly the old routine: the leg end sweeps to the end, ends the ball and has it destroyed.
    /// </summary>
    public static class BallHits
    {
        /// <summary>Slack on "now - hit_lag reached the leg end" (run seconds are doubles: 1.15 - 0.15 is not 1.0).</summary>
        private const double Epsilon = 1e-9;

        /// <summary>The path time a sweep at server time <paramref name="now"/> may reach: min(now - hit_lag, leg end).</summary>
        public static double LaggedEnd(HazardPath path, double now, double hitLag) => Math.Min(now - Math.Max(0.0, hitLag), path.EndTime);

        /// <summary>True once the lagged sweep has reached the leg end: now - hit_lag &gt;= EndTime.</summary>
        public static bool TailDone(HazardPath path, double now, double hitLag) => now - Math.Max(0.0, hitLag) + Epsilon >= path.EndTime;

        /// <summary>
        /// Tests the ball at every point of <paramref name="path"/> it covered since its last sweep up to path time
        /// <paramref name="t1"/> (no two points more than <paramref name="maxStep"/> apart; the start point on the first
        /// sweep), and records how far it got. Nothing when t1 is before the path's start. Returns the hits.
        /// </summary>
        public static int SweepTo(BallWaveClock clock, int id, HazardPath path, double t1, float maxStep, Func<Vector3, int> hit)
        {
            // the lag reaches back before the leg started: nothing of it is due yet, and the start point must still be tested later
            if (t1 < path.T0)
                return 0;

            var swept = clock.SweptTo(id);
            var points = BallSweep.SweepPoints(path, swept ?? path.T0, t1, maxStep, includeStart: swept == null);
            var hits = 0;

            foreach (var pt in points)
                hits += hit(pt);

            clock.SetSweptTo(id, Math.Min(t1, path.EndTime));
            return hits;
        }

        /// <summary>
        /// The leg of ball <paramref name="id"/> ended (<paramref name="path"/> is the finished path, still on the handle
        /// before the end snap clears it). It is kept in <paramref name="tail"/> (the caller's per-ball slot) for the tail
        /// sweep. hit_lag 0: sweep to the end, end the ball, return true (destroy it). hit_lag &gt; 0: the ball enters its tail
        /// and false is returned (keep the object; <see cref="Tick"/> sweeps the tail and says when to destroy it).
        /// </summary>
        public static bool LegEnded(BallWaveClock clock, int id, HazardPath path, ref HazardPath tail, double hitLag, float maxStep, Func<Vector3, int> hit)
        {
            tail = path;

            if (!(hitLag > 0.0))
            {
                if (path != null)
                    SweepTo(clock, id, path, path.EndTime, maxStep, hit);

                clock.OnLegEnd(id);
                return true;
            }

            clock.OnLegEnd(id, tail: true);
            return false;
        }

        /// <summary>
        /// One driver tick for hittable ball <paramref name="id"/>, given its handle as it is now: <paramref name="alive"/>
        /// (HazardHandle.Alive) and <paramref name="handlePath"/> (HazardHandle.Path, the leg in flight or null at rest).
        /// Rolling: sweeps the handle's path, only while alive, to <see cref="LaggedEnd"/>. In its tail: sweeps
        /// <paramref name="tailPath"/> (the finished path <see cref="LegEnded"/> kept) the same way while alive. Returns
        /// true when the tail is finished (swept to its end, or the object died): the caller destroys the ball.
        /// </summary>
        public static bool Tick(BallWaveClock clock, int id, bool alive, HazardPath handlePath, HazardPath tailPath, double now, double hitLag, float maxStep, Func<Vector3, int> hit)
        {
            // a dead object is never swept (HazardContext.HitPlayers also refuses it); a ball at rest has no live path
            var livePath = alive ? handlePath : null;

            if (clock.InTail(id))
            {
                if (alive && tailPath != null)
                    SweepTo(clock, id, tailPath, LaggedEnd(tailPath, now, hitLag), maxStep, hit);

                if (alive && tailPath != null && !TailDone(tailPath, now, hitLag))
                    return false;

                clock.OnTailDone(id);
                return true;
            }

            if (livePath != null)
                SweepTo(clock, id, livePath, LaggedEnd(livePath, now, hitLag), maxStep, hit);

            return false;
        }
    }
}
