using System;
using System.Collections.Generic;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.SpeedCourses
{
    /// <summary>A creature the course controller spawned, by guid. <see cref="None"/> for a spawn that failed.</summary>
    public readonly struct SpeedCreatureHandle : IEquatable<SpeedCreatureHandle>
    {
        public SpeedCreatureHandle(uint guid) => Guid = guid;

        public uint Guid { get; }

        public bool IsNone => Guid == 0;

        public static readonly SpeedCreatureHandle None = default;

        public bool Equals(SpeedCreatureHandle other) => Guid == other.Guid;

        public override bool Equals(object obj) => obj is SpeedCreatureHandle h && Equals(h);

        public override int GetHashCode() => (int)Guid;

        public override string ToString() => IsNone ? "none" : $"0x{Guid:X8}";
    }

    /// <summary>
    /// What the controller writes on one spawned creature beyond the Threads shaping (which every speed creature
    /// gets with Run null and Rewards false). Applied AFTER ShapeCreature and BEFORE EnterWorld.
    /// </summary>
    public sealed class SpeedCreatureStamp
    {
        /// <summary>K1 | K2 | BOSS, or null for a pack creature. Log text only.</summary>
        public string Slot { get; init; }

        /// <summary>Replaces the creature's name when set (the course's named creatures).</summary>
        public string DisplayName { get; init; }

        /// <summary>Objective contribution on death: the Seal key, the token and its weight. Null key: none.</summary>
        public string ObjectiveKey { get; init; }

        public string ObjectiveToken { get; init; }

        public int ObjectiveWeight { get; init; } = 1;

        /// <summary>
        /// Non-zero: the creature's create list is REPLACED by a fresh collection holding exactly one Treasure row
        /// for this wcid (shade 0, so it always drops). The boss carries the season's objective item this way.
        /// </summary>
        public uint TreasureWcid { get; init; }

        /// <summary>The largest non-boss max health placed so far; ShapeCreature reads it for a Boss entry only.</summary>
        public uint ObservedMaxNonBoss { get; init; }

        /// <summary>A runtime scale multiplier (Creature.ApplyRuntimeScaleMultiplier); 1 leaves the weenie's size.</summary>
        public float Scale { get; init; } = 1f;

        /// <summary>
        /// Non-null: the creature is a phased boss. Before EnterWorld it gets the "immune" monster-effect record (inert
        /// until switched on), a health floor of SpeedBossPhaseRules.FloorFor(max health, this fraction), and starts
        /// immune when the fraction is 1 (the entry phase). Null: no guard.
        /// </summary>
        public double? GuardFloorFraction { get; init; }
    }

    /// <summary>The runner as the world sees them right now.</summary>
    public readonly struct SpeedRunnerView
    {
        /// <summary>The runner has a live session (anywhere).</summary>
        public bool Online { get; init; }

        /// <summary>The runner's speed run is armed AND bound to this course's instance AND they stand in it.</summary>
        public bool BoundHere { get; init; }

        /// <summary>The run's clock was rebased by the season's start object.</summary>
        public bool ClockRebased { get; init; }

        /// <summary>True when the position below is meaningful (online and inside this instance).</summary>
        public bool HasPosition { get; init; }

        public uint Cell { get; init; }

        public float X { get; init; }

        public float Y { get; init; }

        public float Z { get; init; }
    }

    /// <summary>
    /// Course controller -> world. Live: <see cref="LiveSpeedCourseWorld"/>. Tests: a fake. Every call except
    /// <see cref="Post"/> and <see cref="ClearPuzzles"/> is made on the instance landblock's thread (the run's tick,
    /// a queued continuation, or a puzzle host callback that the manager guarantees runs there).
    /// </summary>
    public interface ISpeedCourseWorld
    {
        /// <summary>The ephemeral instance id; also the puzzle-gate RunId of every placement this course owns.</summary>
        uint Instance { get; }

        /// <summary>The instance landblock has finished creating its static and content objects.</summary>
        bool CreateWorldObjectsCompleted { get; }

        SpeedRunnerView Runner();

        /// <summary>
        /// The Threads population plan for <paramref name="spec"/> over the course dungeon, priced off the same
        /// tunables a solo Thread reads, with the monster count pinned to the curated point count (one creature per
        /// curated point). Null with <paramref name="error"/> when it cannot be built.
        /// </summary>
        DungeonSpawnPlan BuildPlan(DungeonGemSpec spec, out string error);

        /// <summary>A wcid's live weenie level (PlaceNamed's tie-break for promoting trash to K1/K2).</summary>
        int LevelOf(uint wcid);

        /// <summary>
        /// The no-boss fallback: DungeonRosterSelector.PickBossCandidate over species table <paramref name="family"/>
        /// at <paramref name="level"/>, skipping <paramref name="alreadyDrawn"/> when it can. 0 when the family is
        /// unknown or fields nothing.
        /// </summary>
        uint PickFallbackBoss(string family, int level, IReadOnlyCollection<uint> alreadyDrawn, Random rng);

        /// <summary>
        /// The last-resort finish: creates <paramref name="wcid"/> (the season objective item) on the floor at
        /// <paramref name="point"/> in this instance. False when it could not be created or entered.
        /// </summary>
        bool PlaceItem(uint wcid, DungeonSpawnPointDef point);

        /// <summary>The course dungeon's curated puzzle sites.</summary>
        IReadOnlyList<PuzzleSiteDef> PuzzleSites(string dungeonId);

        /// <summary>
        /// Creates <paramref name="entry"/>'s creature at its point in this instance, shapes it through
        /// ThreadDungeonSpawner.ShapeCreature (Run null, Rewards false, solo effort), strips its kill-task strings,
        /// applies <paramref name="stamp"/>, and enters it. None on failure; <paramref name="maxHealth"/> is its max
        /// health as entered.
        /// </summary>
        SpeedCreatureHandle Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, SpeedCreatureStamp stamp, out uint maxHealth);

        bool IsAlive(SpeedCreatureHandle h);

        /// <summary>Current / max health, 0..1; 0 for a dead or missing creature.</summary>
        double HealthFraction(SpeedCreatureHandle h);

        /// <summary>Max health; 0 for a dead or missing creature.</summary>
        uint MaxHealth(SpeedCreatureHandle h);

        /// <summary>Current health; 0 for a dead or missing creature.</summary>
        uint CurrentHealth(SpeedCreatureHandle h);

        /// <summary>
        /// The phased boss's guard: immune (the "immune" record filters every hit to 0; the grey shield-up cue plays on every
        /// immune call, shield-down on a real change, as LivePinnacleWorld.SetImmune) and its health floor (Creature.P_HealthFloor; 0 clears it). The boss
        /// keeps attacking either way. A no-op for a dead or missing creature.
        /// </summary>
        void SetBossGuard(SpeedCreatureHandle h, bool immune, uint floorHp);

        /// <summary>A live creature's landblock-local x and y in this instance; false when it is dead, gone or elsewhere.</summary>
        bool TryGetPosition(SpeedCreatureHandle h, out float x, out float y);

        /// <summary>The latch of the objective gate keyed <paramref name="key"/> in this instance; null when no such gate exists.</summary>
        bool? SealLatched(string key);

        /// <summary>
        /// Shuts (<paramref name="shut"/> true) or opens the Seal door: the Door objective gate keyed <paramref name="key"/>.
        /// Shut: the door closes, locks, and refuses every use (even from the far side a locked door normally opens from)
        /// until opened. Open: the Seal's own open path. A no-op when the gate is missing or not a Door. Landblock thread.
        /// </summary>
        void SetSealDoor(string key, bool shut);

        /// <summary>
        /// Places one puzzle gate through PuzzleGateManager.PlaceForRun under <paramref name="host"/>, the way a Thread
        /// run places a curated gate site. Returns the placement id, or 0 with <paramref name="error"/>.
        /// </summary>
        int PlacePuzzle(PuzzleSiteDef site, ThreadPuzzlePick pick, PuzzleGateOptions options, IPuzzleGateHost host, out string error);

        /// <summary>Reaps this course's dead placements (their hosts hear OnRemoved).</summary>
        void ReapPuzzles(DateTime now);

        /// <summary>Clears every placement this course owns. Safe from any thread.</summary>
        int ClearPuzzles();

        /// <summary>
        /// Contributes <paramref name="token"/> to the objective gate keyed <paramref name="key"/> in this instance, with
        /// nobody notified: the generic objective lines ("The workings shift. n of 8.", "... has opened!") are never
        /// sent for a course token; the run tells the runner its own lines (<see cref="Tell"/>).
        /// </summary>
        void Contribute(string key, string token, double weight);

        /// <summary>
        /// True when <paramref name="token"/> is a live token of the objective gate keyed <paramref name="key"/> (whoever
        /// contributed it: the run, or a contributor object such as the east lectern); false when it is not or the gate
        /// has no lock yet; null when no such gate exists.
        /// </summary>
        bool? SealHasToken(string key, string token);

        /// <summary>System chat to the runner, if online. Landblock thread.</summary>
        void Tell(string text);

        /// <summary>Queues <paramref name="action"/> onto the instance landblock's thread. Safe from any thread.</summary>
        void Post(Action action);

        void Log(string line);

        void LogWarn(string line);

        void LogError(string line);
    }

    /// <summary>
    /// Everything the hazard host (step 7) reads from a course run each tick: the runner, the clock, and the named
    /// creatures. Captured by <see cref="SpeedCourseRun"/> on its landblock thread.
    /// </summary>
    public readonly struct SpeedCourseRunSnapshot
    {
        /// <summary>Seconds on the controller's monotonic clock.</summary>
        public double Now { get; init; }

        public SpeedRunnerView Runner { get; init; }

        public bool ClockRebased => Runner.ClockRebased;

        public bool Populated { get; init; }

        public bool K1Spawned { get; init; }
        public bool K1Dead { get; init; }
        public bool K2Spawned { get; init; }
        public bool K2Dead { get; init; }
        public bool BossSpawned { get; init; }
        public bool BossDead { get; init; }

        /// <summary>Boss current / max health, 0..1; meaningful only while the boss is spawned.</summary>
        public double BossHealthFraction { get; init; }

        /// <summary>
        /// The view SpeedCourseRules.DecideHazard reads for one hazard: this snapshot plus that hazard's own region test
        /// and run history.
        /// </summary>
        public SpeedHazardSnapshot ForHazard(bool anyRunnerInRegion, bool running, double? lastStart, double? lastEndTime, SpeedHazardEnd lastEnd)
            => new SpeedHazardSnapshot
            {
                AnyRunnerInRegion = anyRunnerInRegion,
                ClockRebased = ClockRebased,
                K1Dead = K1Dead,
                K2Dead = K2Dead,
                BossSpawned = BossSpawned,
                BossDead = BossDead,
                BossHealthFraction = BossHealthFraction,
                Now = Now,
                LastStart = lastStart,
                LastEndTime = lastEndTime,
                LastEnd = lastEnd,
                Running = running,
            };
    }

    /// <summary>
    /// The hazard seam (step 7). The run calls <see cref="Tick"/> on its landblock thread after its own work each
    /// tick, and <see cref="Stop"/> exactly once at teardown (from whichever thread ended the run: the landblock
    /// thread, or the world thread when the instance landblock is already gone), so a driver stops every hazard
    /// run it started.
    /// </summary>
    public interface ISpeedHazardDriver
    {
        void Tick(SpeedCourseRun run, in SpeedCourseRunSnapshot snapshot);

        void Stop(SpeedCourseRun run, string reason);
    }

    /// <summary>The driver of a course that declares no hazards: does nothing.</summary>
    public sealed class NoopSpeedHazardDriver : ISpeedHazardDriver
    {
        public static readonly NoopSpeedHazardDriver Instance = new NoopSpeedHazardDriver();

        private NoopSpeedHazardDriver() { }

        public void Tick(SpeedCourseRun run, in SpeedCourseRunSnapshot snapshot) { }

        public void Stop(SpeedCourseRun run, string reason) { }
    }
}
