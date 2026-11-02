using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.SpeedCourses
{
    /// <summary>
    /// One live speed-course run: the per-run 0x0089 (or any course) instance of one runner. Populates the instance
    /// with Threads-scaled monsters once it has finished creating its content objects, places the three puzzle gates,
    /// polls the Seal, spawns the held boss with the season's objective item when the Seal latches, feeds the hazard
    /// driver, and tears everything down when the run ends.
    /// <para/>
    /// THREADING. <see cref="Tick"/>, the populate continuations and the puzzle solve callback run on the instance
    /// landblock's thread (the manager enqueues the tick there; PuzzleGateManager calls OnSolved there). A puzzle
    /// removal can arrive on any thread and is marshalled there through <see cref="ISpeedCourseWorld.Post"/>.
    /// <see cref="End"/> may run on the world thread (the instance landblock is gone) or the landblock thread; it is
    /// idempotent and touches only thread-safe things (the puzzle registry, the hazard driver's Stop, the ended flag).
    /// </summary>
    public sealed class SpeedCourseRun : ISpeedPuzzleOwner
    {
        /// <summary>Creatures entered per landblock action while populating, so one populate never stalls a tick.</summary>
        public const int SpawnBatchSize = 8;

        /// <summary>Re-places of one puzzle site after a removal without a solve, before the run gives up on it (ERROR).</summary>
        public const int MaxPuzzleReplaceTries = 2;

        /// <summary>Boss spawn attempts after the Seal latched, one per tick, before the run gives up (ERROR).</summary>
        public const int MaxBossSpawnAttempts = 3;

        /// <summary>Objective item placements (the no-boss finish), one per tick, before the run gives up (ERROR).</summary>
        public const int MaxObjectivePlaceAttempts = 3;

        private readonly ISpeedCourseWorld world;
        private readonly double registeredAt;

        private int ended;
        private bool everBound;

        private bool populateStarted;
        private bool spawningDone;
        private DungeonSpawnPlan plan;
        private readonly Queue<(DungeonSpawnPlanEntry Entry, SpeedCreatureStamp Stamp, string Slot, GuardianState Guardian)> pendingSpawns
            = new Queue<(DungeonSpawnPlanEntry, SpeedCreatureStamp, string, GuardianState)>();

        /// <summary>The token (weight 1) the run contributes to a guardian door once all of its guardians are down.</summary>
        public const string GuardianToken = "guardians";

        /// <summary>One course guardian's progress this run. Landblock thread only.</summary>
        private sealed class GuardianState
        {
            public GuardianState(SpeedGuardianDef def) => Def = def;

            public SpeedGuardianDef Def { get; }

            /// <summary>The spawned creature; None while it is queued (or if it was never fielded).</summary>
            public SpeedCreatureHandle Handle;

            /// <summary>The run could not field it (no plan, no creature, a failed spawn): it counts as down, unkilled.</summary>
            public bool Granted;

            /// <summary>The runner has been told this guardian's doors unsealed (once per killed guardian).</summary>
            public bool Announced;
        }

        /// <summary>Every guardian of the course with a door key, in course order. Built at populate.</summary>
        private readonly List<GuardianState> guardians = new List<GuardianState>();

        /// <summary>The distinct door keys of <see cref="guardians"/>, in course order, and each key's guardians. Built at populate.</summary>
        private readonly List<(string Key, List<GuardianState> Group)> guardianDoors = new List<(string, List<GuardianState>)>();

        /// <summary>Guardian door keys the run has contributed to (exactly once each).</summary>
        private readonly HashSet<string> guardianDoorsOpened = new HashSet<string>(StringComparer.Ordinal);
        private uint observedMaxNonBoss;
        private int spawned;

        private DungeonSpawnPlanEntry heldBoss;
        private int bossAttempts;
        private bool bossGaveUp;

        /// <summary>K1/K2 entries held out of the instance until their holdUntilSolved puzzle opens, by named slot.</summary>
        private readonly Dictionary<string, (DungeonSpawnPlanEntry Entry, SpeedCreatureStamp Stamp)> heldNamed
            = new Dictionary<string, (DungeonSpawnPlanEntry, SpeedCreatureStamp)>(StringComparer.Ordinal);

        /// <summary>Puzzle slots whose way is open: solved, or given up by the run (no gate stands there any more).</summary>
        private readonly HashSet<string> openedPuzzleSlots = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Named slots whose kill token the run has contributed.</summary>
        private readonly HashSet<string> namedTokenContributed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Tokens the runner has been told about (earned or granted), in the order told.</summary>
        private readonly List<string> announced = new List<string>();

        /// <summary>Tokens the run contributed because the course could not offer them.</summary>
        private readonly HashSet<string> granted = new HashSet<string>(StringComparer.Ordinal);

        private bool introSent;
        private bool sealOpenAnnounced;

        private Random puzzleRng;
        private readonly Dictionary<string, PuzzleGateType> slotTypes = new Dictionary<string, PuzzleGateType>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> replaceTries = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> placementIds = new Dictionary<string, int>(StringComparer.Ordinal);

        public SpeedCourseRun(SpeedCourseDef course, int seasonId, int seasonLevelFloor, uint objectiveWcid, int playerLevel, uint ownerGuid,
            int runSeed, ISpeedCourseWorld world, double now, ISpeedHazardDriver hazards = null)
        {
            Course = course ?? throw new ArgumentNullException(nameof(course));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            SeasonId = seasonId;
            ObjectiveWcid = objectiveWcid;
            OwnerGuid = ownerGuid;
            RunSeed = runSeed;
            RunLevel = SpeedCourseRules.RunLevel(playerLevel, seasonLevelFloor, course.LevelFloor, course.FixedLevel);
            Hazards = hazards ?? NoopSpeedHazardDriver.Instance;
            registeredAt = now;
        }

        public SpeedCourseDef Course { get; }

        public int SeasonId { get; }

        public uint ObjectiveWcid { get; }

        public uint OwnerGuid { get; }

        public int RunSeed { get; }

        /// <summary>The level this run is played at (SpeedCourseRules.RunLevel, fixed at registration).</summary>
        public int RunLevel { get; }

        public uint Instance => world.Instance;

        public ISpeedCourseWorld World => world;

        public ISpeedHazardDriver Hazards { get; private set; }

        /// <summary>Replaces the hazard driver. Called once by the manager right after construction, before the first tick.</summary>
        public void AttachHazardDriver(ISpeedHazardDriver driver) => Hazards = driver ?? NoopSpeedHazardDriver.Instance;

        public bool IsEnded => Volatile.Read(ref ended) != 0;

        public string EndReason { get; private set; }

        /// <summary>Called once from <see cref="End"/> (the manager drops its registry entry).</summary>
        public Action<SpeedCourseRun> Ended { get; set; }

        public bool Populated => spawningDone;

        public SpeedCreatureHandle K1 { get; private set; }

        public SpeedCreatureHandle K2 { get; private set; }

        public SpeedCreatureHandle Boss { get; private set; }

        public bool BossHeld => heldBoss != null && Boss.IsNone && !bossGaveUp;

        public int SpawnedCount => spawned;

        /// <summary>The snapshot the last tick handed the hazard driver.</summary>
        public SpeedCourseRunSnapshot LastSnapshot { get; private set; }

        /// <summary>The type each puzzle slot was given this run (empty until populate).</summary>
        public IReadOnlyDictionary<string, PuzzleGateType> SlotTypes => slotTypes;

        public override string ToString() => $"speed:0x{Instance:X8} season={SeasonId} level={RunLevel}";

        // =====================================================================================================
        // Tick (landblock thread)
        // =====================================================================================================

        public void Tick(double now)
        {
            if (IsEnded)
                return;

            var runner = world.Runner();

            var endReason = SpeedCourseRules.DecideRunnerEnd(runner.Online, runner.BoundHere, everBound, now - registeredAt, SpeedCourseRules.RunnerBindGraceSeconds);

            if (runner.BoundHere)
                everBound = true;

            if (endReason != null)
            {
                End(endReason);
                return;
            }

            if (!populateStarted)
            {
                if (!world.CreateWorldObjectsCompleted)
                    return;

                Populate();
            }

            if (populateStarted)
                world.ReapPuzzles(DateTime.UtcNow);

            if (runner.ClockRebased && !introSent)
            {
                introSent = true;
                world.Tell(Course.Messages?.Intro);
            }

            ObserveNamedDeaths();
            CheckGuardianDoors();
            UpdateSealMessages();

            if (spawningDone && Boss.IsNone && !ObjectiveItemPlaced && !objectivePlaceGaveUp)
            {
                var latched = world.SealLatched(Course.SealKey);

                if (latched == null)
                {
                    if (!sealMissingLogged)
                    {
                        sealMissingLogged = true;
                        world.LogError($"[SPEEDCOURSE] {this} no objective gate keyed '{Course.SealKey}' is in the instance; the Seal cannot open and the boss cannot spawn (content error)");
                    }
                }
                else if (latched == true)
                {
                    if (BossHeld)
                        TrySpawnBoss();
                    else
                        PlaceObjectiveItem(bossGaveUp ? "the boss failed to spawn" : "no boss candidate");
                }
            }

            var snapshot = Snapshot(now, runner);
            LastSnapshot = snapshot;

            try
            {
                Hazards.Tick(this, snapshot);
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} hazard driver tick threw: {ex}");
            }

            TickSealDoor(snapshot, runner);
        }

        /// <summary>The Seal door is shut (this run shut it and has not opened it). Written on the landblock thread and at End, so volatile.</summary>
        private volatile bool sealDoorShut;

        private void TickSealDoor(in SpeedCourseRunSnapshot snapshot, SpeedRunnerView runner)
        {
            var region = Course.SealLock?.Region;

            if (region == null)
                return;

            var inside = runner.HasPosition && region.Contains(runner.X, runner.Y, runner.Z);
            var latched = world.SealLatched(Course.SealKey) == true;

            switch (SpeedCourseRules.DecideSealDoor(latched, snapshot.BossSpawned, snapshot.BossDead, inside, sealDoorShut))
            {
                case SpeedSealDoorDecision.Shut:
                    sealDoorShut = true;

                    // THE END RACE: End can run off-thread (SpeedCourseManager.Register) between this tick's IsEnded check
                    // and here, read sealDoorShut false, and post no open; the door would then stay shut for good. End
                    // sets `ended` (Interlocked, a full fence) BEFORE it reads sealDoorShut; this side writes sealDoorShut
                    // and, after a full fence (a volatile write then a volatile read may be reordered), reads IsEnded. So
                    // at least one side sees the other: End posts the open, or this re-opens it here.
                    Interlocked.MemoryBarrier();
                    world.SetSealDoor(Course.SealKey, true);

                    if (IsEnded)
                    {
                        sealDoorShut = false;
                        world.SetSealDoor(Course.SealKey, false);
                        world.Log($"[SPEEDCOURSE] {this} Seal door re-opened: the run ended while it was shutting");
                        break;
                    }

                    world.Log($"[SPEEDCOURSE] {this} Seal door shut behind the runner");
                    break;

                case SpeedSealDoorDecision.Open:
                    sealDoorShut = false;
                    world.SetSealDoor(Course.SealKey, false);
                    world.Log($"[SPEEDCOURSE] {this} Seal door opened (boss dead)");
                    break;
            }
        }

        public SpeedCourseRunSnapshot Snapshot(double now, SpeedRunnerView runner)
        {
            var bossSpawned = !Boss.IsNone;
            var bossAlive = bossSpawned && world.IsAlive(Boss);

            return new SpeedCourseRunSnapshot
            {
                Now = now,
                Runner = runner,
                Populated = spawningDone,
                K1Spawned = !K1.IsNone,
                K1Dead = !K1.IsNone && !world.IsAlive(K1),
                K2Spawned = !K2.IsNone,
                K2Dead = !K2.IsNone && !world.IsAlive(K2),
                BossSpawned = bossSpawned,
                BossDead = bossSpawned && !bossAlive,
                BossHealthFraction = bossAlive ? world.HealthFraction(Boss) : 0.0,
            };
        }

        // =====================================================================================================
        // Populate
        // =====================================================================================================

        private SpeedNamedDef Named(string slot) => Course.Named?.FirstOrDefault(n => n != null && string.Equals(n.Slot, slot, StringComparison.Ordinal));

        private void Populate()
        {
            populateStarted = true;

            // End can land on the world thread at any time; an ended run places nothing.
            if (IsEnded)
                return;

            var spec = SpeedCourseRules.BuildGemSpec(Course, RunLevel, OwnerGuid);

            try
            {
                plan = world.BuildPlan(spec, out var error);

                if (plan == null)
                    world.LogError($"[SPEEDCOURSE] {this} population plan could not be built ({error}); the course opens with no monsters and no boss");
            }
            catch (Exception ex)
            {
                plan = null;
                world.LogError($"[SPEEDCOURSE] {this} population plan threw; the course opens with no monsters and no boss: {ex}");
            }

            // Puzzles first: each placement's spawn is queued on this landblock ahead of the first creature batch.
            try
            {
                PlacePuzzles();
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle placement threw; giving up every slot not placed: {ex}");

                if (!IsEnded)
                    GiveUpUnplacedPuzzles("puzzle placement threw");
            }

            // End may have run (world thread) while these placements registered, so its ClearPuzzles can have swept
            // before they existed. Sweep again so nothing outlives an ended run (ThreadPuzzlePass does the same).
            if (IsEnded)
            {
                ClearAfterLateEnd("populate");
                return;
            }

            foreach (var g in Course.Guardians ?? new List<SpeedGuardianDef>())
                if (g != null && GuardianKeys(g).Any())
                    guardians.Add(new GuardianState(g));

            foreach (var key in guardians.SelectMany(g => GuardianKeys(g.Def)).Distinct(StringComparer.Ordinal))
                guardianDoors.Add((key, guardians.Where(g => GuardianKeys(g.Def).Contains(key, StringComparer.Ordinal)).ToList()));

            if (plan == null)
            {
                spawningDone = true;
                GrantForNamed(SpeedNamedSlots.K1, "no population plan");
                GrantForNamed(SpeedNamedSlots.K2, "no population plan");

                foreach (var g in guardians)
                    GrantGuardian(g, "no population plan");

                return;
            }

            var k1Def = Named(SpeedNamedSlots.K1);
            var k2Def = Named(SpeedNamedSlots.K2);
            var bossDef = Named(SpeedNamedSlots.Boss);

            var placed = SpeedCourseRules.PlaceNamed(plan.Entries, k1Def?.Position, k2Def?.Position, bossDef?.Position, e => world.LevelOf(e.Wcid));

            foreach (var entry in placed.Entries)
            {
                if (entry.Role == DungeonRole.Boss)
                {
                    // Held back: the boss enters only when the Seal latches. The first boss entry is the one.
                    heldBoss ??= entry;
                    continue;
                }

                if (ReferenceEquals(entry, placed.K1))
                    QueueOrHoldNamed(entry, k1Def);
                else if (ReferenceEquals(entry, placed.K2))
                    QueueOrHoldNamed(entry, k2Def);
                else
                    pendingSpawns.Enqueue((entry, null, null, null));
            }

            QueueGuardians();

            if (heldBoss == null)
                ResolveFallbackBoss(bossDef);

            if (placed.K1 == null || placed.K2 == null)
            {
                world.LogWarn($"[SPEEDCOURSE] {this} plan had too few entries for the named slots (K1={(placed.K1 != null)} K2={(placed.K2 != null)})");

                if (placed.K1 == null)
                    GrantForNamed(SpeedNamedSlots.K1, "the plan had no creature for it");

                if (placed.K2 == null)
                    GrantForNamed(SpeedNamedSlots.K2, "the plan had no creature for it");
            }

            world.Log($"[SPEEDCOURSE] {this} plan: family={plan.FamilyId ?? "none"} entries={plan.Entries.Count} queued={pendingSpawns.Count} boss={plan.BossWcid} bossLevel={plan.BossLevel} held={(heldBoss != null)}");

            SpawnStep();
        }

        /// <summary>
        /// The named creature's stamp: its slot and display name. It carries NO objective key: the run contributes the
        /// slot's token itself when it sees the creature dead (<see cref="ObserveNamedDeaths"/>), so the runner hears the
        /// course's line instead of the generic objective one.
        /// </summary>
        private static SpeedCreatureStamp NamedStamp(SpeedNamedDef def)
        {
            if (def == null)
                return null;

            return new SpeedCreatureStamp
            {
                Slot = def.Slot,
                DisplayName = string.IsNullOrWhiteSpace(def.DisplayName) ? null : def.DisplayName,
            };
        }

        /// <summary>Queues a named entry, or holds it when its def waits on a puzzle that is not open yet.</summary>
        private void QueueOrHoldNamed(DungeonSpawnPlanEntry entry, SpeedNamedDef def)
        {
            var hold = def?.HoldUntilSolved;

            if (hold != null && !openedPuzzleSlots.Contains(hold) && Course.Puzzles?.Any(p => p != null && p.Slot == hold) == true)
            {
                heldNamed[def.Slot] = (entry, NamedStamp(def));
                world.Log($"[SPEEDCOURSE] {this} named {def.Slot} (wcid {entry.Wcid}) held until puzzle {hold} opens");
                return;
            }

            pendingSpawns.Enqueue((entry, NamedStamp(def), def?.Slot, null));
        }

        /// <summary>
        /// Queues one clone per guardian behind the plan's own creatures (SpeedCourseRules.PlaceGuardians, which never
        /// touches the plan). A guardian the plan has no creature for is granted at once, so its door is never left shut
        /// with nothing to kill.
        /// </summary>
        private void QueueGuardians()
        {
            if (guardians.Count == 0)
                return;

            var placements = SpeedCourseRules.PlaceGuardians(plan.Entries, guardians.Select(g => g.Def).ToList(), e => world.LevelOf(e.Wcid));

            foreach (var g in guardians)
            {
                var placement = placements.FirstOrDefault(p => ReferenceEquals(p.Guardian, g.Def));

                if (placement == null)
                {
                    GrantGuardian(g, "the plan had no creature for it");
                    continue;
                }

                var stamp = new SpeedCreatureStamp
                {
                    Slot = g.Def.Id,
                    DisplayName = string.IsNullOrWhiteSpace(g.Def.DisplayName) ? null : g.Def.DisplayName,
                    Scale = g.Def.Scale > 0f ? g.Def.Scale : 1f,
                };

                pendingSpawns.Enqueue((placement.Entry, stamp, g.Def.Id, g));
            }
        }

        /// <summary>A guardian's distinct, non-blank door keys in authored order.</summary>
        private static IEnumerable<string> GuardianKeys(SpeedGuardianDef def)
            => (def?.DoorKeys ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal);

        /// <summary>
        /// The run could not field <paramref name="g"/>: it counts as down, so every one of its doors can still open (never
        /// through GrantForNamed, which knows only the K1/K2 slots and would leave the doors shut for good).
        /// </summary>
        private void GrantGuardian(GuardianState g, string reason)
        {
            if (g == null || g.Granted)
                return;

            g.Granted = true;
            world.LogWarn($"[SPEEDCOURSE] {this} guardian {g.Def.Id} (doors '{string.Join("', '", GuardianKeys(g.Def))}') not fielded: {reason}; it counts as down");

            CheckGuardianDoors();
        }

        /// <summary>
        /// For each guardian door key not yet opened: once every guardian naming that key is dead or granted, contributes
        /// <see cref="GuardianToken"/> (weight 1) to the door exactly once. The runner is told the course's guardianDoor
        /// line once per killed guardian, the first time one of its doors opens (a Cell Keeper opens both doors of its room
        /// at once: one line, not two). Contributes through the objective-lock path and never polls the door's latch (the
        /// live gate lookup caches a single key). An ended run contributes nothing.
        /// </summary>
        private void CheckGuardianDoors()
        {
            // Every tick: nothing to do before populate or once every door is open.
            if (IsEnded || guardianDoorsOpened.Count == guardianDoors.Count)
                return;

            var tell = false;

            foreach (var (key, group) in guardianDoors)
            {
                if (guardianDoorsOpened.Contains(key))
                    continue;

                if (group.Any(g => !g.Granted && (g.Handle.IsNone || world.IsAlive(g.Handle))))
                    continue;

                // Marked only once the contribution went through: a throw leaves the door to be retried next tick.
                try
                {
                    world.Contribute(key, GuardianToken, 1);
                }
                catch (Exception ex)
                {
                    world.LogError($"[SPEEDCOURSE] {this} guardian door '{key}' contribution threw; retrying next tick: {ex}");
                    continue;
                }

                guardianDoorsOpened.Add(key);

                var killed = group.Count(g => !g.Granted);
                world.Log($"[SPEEDCOURSE] {this} guardian door '{key}' opened ({killed} killed, {group.Count - killed} granted)");

                foreach (var g in group)
                {
                    if (g.Granted || g.Announced)
                        continue;

                    g.Announced = true;
                    tell = true;
                }
            }

            if (tell)
                world.Tell(Course.Messages?.GuardianDoor);
        }

        /// <summary>True once the run has contributed to the guardian door keyed <paramref name="key"/>.</summary>
        public bool GuardianDoorOpened(string key) => key != null && guardianDoorsOpened.Contains(key);

        /// <summary>
        /// A puzzle's way is open (solved, or given up by the run so no gate stands there): every named creature held for
        /// it enters now. Landblock thread.
        /// </summary>
        private void OpenPuzzleSlot(string puzzleSlot)
        {
            if (puzzleSlot == null || !openedPuzzleSlots.Add(puzzleSlot))
                return;

            foreach (var slot in heldNamed.Keys.ToList())
            {
                if (!string.Equals(Named(slot)?.HoldUntilSolved, puzzleSlot, StringComparison.Ordinal))
                    continue;

                var (entry, stamp) = heldNamed[slot];
                heldNamed.Remove(slot);

                world.Log($"[SPEEDCOURSE] {this} puzzle {puzzleSlot} open; named {slot} (wcid {entry.Wcid}) enters");

                if (spawningDone)
                    SpawnOne(entry, stamp, slot, null);
                else
                    pendingSpawns.Enqueue((entry, stamp, slot, null));
            }
        }

        /// <summary>True while a named slot is held for its puzzle.</summary>
        public bool IsNamedHeld(string slot) => heldNamed.ContainsKey(slot);

        /// <summary>
        /// One creature into the instance; a named slot that fails is given up and its tokens granted, a guardian that
        /// fails is granted (its door opens).
        /// </summary>
        private void SpawnOne(DungeonSpawnPlanEntry entry, SpeedCreatureStamp stamp, string slot, GuardianState guardian)
        {
            SpeedCreatureHandle handle;
            uint maxHealth;

            try
            {
                handle = world.Spawn(plan, entry, stamp ?? new SpeedCreatureStamp { ObservedMaxNonBoss = observedMaxNonBoss }, out maxHealth);
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} spawn of wcid {entry.Wcid} threw: {ex}");
                handle = SpeedCreatureHandle.None;
                maxHealth = 0;
            }

            if (handle.IsNone)
            {
                if (guardian != null)
                {
                    world.LogError($"[SPEEDCOURSE] {this} guardian {guardian.Def.Id} (wcid {entry.Wcid}) failed to spawn");
                    GrantGuardian(guardian, "it failed to spawn");
                }
                else if (slot != null)
                {
                    world.LogError($"[SPEEDCOURSE] {this} named {slot} (wcid {entry.Wcid}) failed to spawn");
                    GrantForNamed(slot, $"{slot} failed to spawn");
                }

                return;
            }

            spawned++;

            if (maxHealth > observedMaxNonBoss)
                observedMaxNonBoss = maxHealth;

            if (guardian != null)
                guardian.Handle = handle;
            else if (slot == SpeedNamedSlots.K1)
                K1 = handle;
            else if (slot == SpeedNamedSlots.K2)
                K2 = handle;
        }

        /// <summary>One batch of creatures, then the next batch queued behind whatever else the landblock has to do.</summary>
        private void SpawnStep()
        {
            if (IsEnded)
                return;

            var batch = 0;
            GuardianState current = null;

            try
            {
                while (pendingSpawns.Count > 0 && batch < SpawnBatchSize)
                {
                    var (entry, stamp, slot, guardian) = pendingSpawns.Dequeue();
                    batch++;
                    current = guardian;

                    SpawnOne(entry, stamp, slot, guardian);
                    current = null;
                }
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} spawn step threw; {pendingSpawns.Count} creature(s) abandoned: {ex}");

                // An abandoned guardian would hold its door shut for good: it counts as down instead. That includes the
                // one being spawned when the step threw (already dequeued) unless it made it into the world.
                var abandoned = pendingSpawns.Where(p => p.Guardian != null).Select(p => p.Guardian).ToList();

                if (current != null && current.Handle.IsNone)
                    abandoned.Insert(0, current);

                pendingSpawns.Clear();

                foreach (var g in abandoned)
                    GrantGuardian(g, "its spawn was abandoned");
            }

            if (pendingSpawns.Count > 0)
            {
                world.Post(SpawnStep);
                return;
            }

            spawningDone = true;
            world.Log($"[SPEEDCOURSE] {this} populated: {spawned} creature(s) placed, K1={K1} K2={K2}, boss held={BossHeld}");
        }

        // =====================================================================================================
        // The boss
        // =====================================================================================================

        /// <summary>
        /// The plan has no Boss entry (no curated row and no promotion, or no boss anchor). First fallback: the course
        /// family's own boss candidate at the run level (DungeonRosterSelector.PickBossCandidate), held for the Seal and
        /// shaped through ShapeCreature like any boss. The plan's boss level is raised to run level + 1 when the
        /// builder left it unset, the same floor the builder applies, so the stand-in outranks the gem. If the family
        /// fields nothing either, the Seal latch places the objective item directly (<see cref="PlaceObjectiveItem"/>).
        /// </summary>
        private void ResolveFallbackBoss(SpeedNamedDef bossDef)
        {
            var drawn = new HashSet<uint>(plan.Entries.Select(e => e.Wcid));
            uint candidate = 0;

            try
            {
                candidate = world.PickFallbackBoss(Course.Family, RunLevel, drawn, new Random(RunSeed));
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} fallback boss pick threw: {ex}");
            }

            if (candidate != 0 && bossDef?.Position != null)
            {
                heldBoss = new DungeonSpawnPlanEntry(candidate, DungeonRole.Boss, bossDef.Position);
                plan.BossWcid = candidate;

                if (plan.BossLevel <= 0)
                    plan.BossLevel = RunLevel + 1;

                world.LogWarn($"[SPEEDCOURSE] {this} plan has no boss entry (family={plan.FamilyId ?? "none"}); fallback boss wcid {candidate} from family {Course.Family} at level {plan.BossLevel}");
                return;
            }

            world.LogWarn($"[SPEEDCOURSE] {this} plan has no boss entry and family {Course.Family} fields no boss candidate at level {RunLevel}; the Seal latch will place the objective item directly");
        }

        /// <summary>
        /// The last-resort finish: the objective item on the floor at the BOSS position, so the run can always be
        /// finished. Once per run; a failed placement retries on the next tick.
        /// </summary>
        private void PlaceObjectiveItem(string why)
        {
            var point = Named(SpeedNamedSlots.Boss)?.Position;

            if (ObjectiveWcid == 0 || point == null)
            {
                if (!objectivePlaceGaveUp)
                    world.LogError($"[SPEEDCOURSE] {this} cannot place the objective item ({why}): objective wcid {ObjectiveWcid}, boss point {(point == null ? "missing" : "set")}; the run cannot finish");

                objectivePlaceGaveUp = true;
                return;
            }

            objectivePlaceAttempts++;

            bool placedItem;

            try
            {
                placedItem = world.PlaceItem(ObjectiveWcid, point);
            }
            catch (Exception ex)
            {
                placedItem = false;
                world.LogError($"[SPEEDCOURSE] {this} objective item placement threw: {ex}");
            }

            if (placedItem)
            {
                ObjectiveItemPlaced = true;
                world.LogWarn($"[SPEEDCOURSE] {this} Seal '{Course.SealKey}' latched with no boss ({why}); objective wcid {ObjectiveWcid} placed on the floor at the boss point");
            }
            else if (objectivePlaceAttempts >= MaxObjectivePlaceAttempts)
            {
                objectivePlaceGaveUp = true;
                world.LogError($"[SPEEDCOURSE] {this} objective wcid {ObjectiveWcid} could not be placed ({why}) after {objectivePlaceAttempts} attempts; giving up - the run cannot finish");
            }
            else
            {
                world.LogWarn($"[SPEEDCOURSE] {this} objective wcid {ObjectiveWcid} could not be placed ({why}, attempt {objectivePlaceAttempts}); retrying next tick");
            }
        }

        private bool objectivePlaceGaveUp;
        private int objectivePlaceAttempts;
        private bool sealMissingLogged;

        /// <summary>True once the no-boss fallback put the objective item on the floor.</summary>
        public bool ObjectiveItemPlaced { get; private set; }

        private void TrySpawnBoss()
        {
            bossAttempts++;

            var bossDef = Named(SpeedNamedSlots.Boss);

            if (ObjectiveWcid == 0)
                world.LogError($"[SPEEDCOURSE] {this} season {SeasonId} has no objective wcid; the boss spawns without the objective item");

            var stamp = new SpeedCreatureStamp
            {
                Slot = SpeedNamedSlots.Boss,
                DisplayName = string.IsNullOrWhiteSpace(bossDef?.DisplayName) ? null : bossDef.DisplayName,
                TreasureWcid = ObjectiveWcid,
                ObservedMaxNonBoss = observedMaxNonBoss,
                Scale = bossDef != null && bossDef.Scale > 0f ? bossDef.Scale : 1f,
                GuardFloorFraction = Course.BossPhases != null && Course.BossPhases.Count > 0 ? Course.BossPhases[0].AtFraction : (double?)null,
            };

            SpeedCreatureHandle handle;

            try
            {
                handle = world.Spawn(plan, heldBoss, stamp, out _);
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} boss spawn threw: {ex}");
                handle = SpeedCreatureHandle.None;
            }

            if (!handle.IsNone)
            {
                Boss = handle;
                world.Log($"[SPEEDCOURSE] {this} Seal '{Course.SealKey}' latched; boss wcid {heldBoss.Wcid} spawned {handle} carrying wcid {ObjectiveWcid}");
                return;
            }

            if (bossAttempts >= MaxBossSpawnAttempts)
            {
                bossGaveUp = true;
                world.LogError($"[SPEEDCOURSE] {this} boss wcid {heldBoss.Wcid} failed to spawn {bossAttempts} times after the Seal latched; giving up - the run cannot finish");
            }
            else
            {
                world.LogWarn($"[SPEEDCOURSE] {this} boss wcid {heldBoss.Wcid} failed to spawn (attempt {bossAttempts}); retrying next tick");
            }
        }

        // =====================================================================================================
        // Puzzles
        // =====================================================================================================

        private IEnumerable<SpeedPuzzleDef> PuzzlesInSlotOrder()
            => SpeedPuzzleSlots.All.Select(s => Course.Puzzles?.FirstOrDefault(p => p != null && string.Equals(p.Slot, s, StringComparison.Ordinal))).Where(p => p != null);

        private void PlacePuzzles()
        {
            puzzleRng = new Random(RunSeed);

            var sites = world.PuzzleSites(Course.DungeonId) ?? Array.Empty<PuzzleSiteDef>();
            var defs = PuzzlesInSlotOrder().ToList();
            var resolved = new List<(SpeedPuzzleDef Def, PuzzleSiteDef Site)>();

            foreach (var def in defs)
            {
                var site = sites.FirstOrDefault(s => s != null && string.Equals(s.Id, def.SiteId, StringComparison.Ordinal));

                if (site == null || site.Anchor == null)
                {
                    world.LogError($"[SPEEDCOURSE] {this} puzzle {def.Slot}: site '{def.SiteId}' is not a puzzle site of '{Course.DungeonId}'; not placed");
                    GiveUpPuzzle(def, "its site is missing");
                    continue;
                }

                resolved.Add((def, site));
            }

            var allowed = resolved.Select(r => (IReadOnlyList<string>)ThreadPuzzleSitePicker.EligibleTypes(r.Site).Select(t => t.ToString()).ToList()).ToList();
            var types = SpeedCourseRules.PickPuzzleTypesPerSite(puzzleRng, allowed);

            if (types == null)
            {
                // Content error (GEOMETRY.md guarantees a distinct assignment exists). Fail open: every site still gets a
                // puzzle, so the course stays finishable, with a type drawn from its own set alone.
                world.LogError($"[SPEEDCOURSE] {this} no distinct puzzle type assignment exists for sites [{string.Join(", ", resolved.Select(r => r.Site.Id))}]; drawing each site independently");
                types = allowed.Select(a => a.Count == 0 ? null : a[puzzleRng.Next(a.Count)]).ToList();
            }

            for (var i = 0; i < resolved.Count; i++)
            {
                if (IsEnded)
                    break;

                var (def, site) = resolved[i];

                if (types[i] == null || !Enum.TryParse<PuzzleGateType>(types[i], out var type))
                {
                    world.LogError($"[SPEEDCOURSE] {this} puzzle {def.Slot}: site '{site.Id}' offers no placeable type; not placed");
                    GiveUpPuzzle(def, "its site offers no placeable type");
                    continue;
                }

                slotTypes[def.Slot] = type;

                bool placedOk;

                try
                {
                    placedOk = PlaceSlot(def, site, type);
                }
                catch (Exception ex)
                {
                    // One slot's throw must not leave the later slots unvisited: this slot is given up (its token granted,
                    // whatever it held released) and the loop carries on.
                    world.LogError($"[SPEEDCOURSE] {this} puzzle {def.Slot} site={site.Id} placement threw; giving the slot up: {ex}");
                    GiveUpPuzzle(def, "its placement threw");
                    continue;
                }

                // A refused first placement is a removal without a solve: the same bounded re-place applies.
                if (!placedOk)
                    OnPuzzleRemoved(def, PuzzleRemovalReason.SpawnFailed, false);
            }
        }

        /// <summary>
        /// After a throw anywhere in puzzle placement: every course slot that holds no placement and was never opened is
        /// given up, so no slot is left both gateless and closed (K1 held forever, its token never granted).
        /// </summary>
        private void GiveUpUnplacedPuzzles(string why)
        {
            foreach (var def in PuzzlesInSlotOrder().ToList())
            {
                if (placementIds.ContainsKey(def.Slot) || openedPuzzleSlots.Contains(def.Slot))
                    continue;

                GiveUpPuzzle(def, why);
            }
        }

        /// <summary>Places one slot's puzzle with a fresh seed. False when it was refused (logged).</summary>
        private bool PlaceSlot(SpeedPuzzleDef def, PuzzleSiteDef site, PuzzleGateType type)
        {
            var n = ThreadPuzzleSitePicker.LeverCount(type, site.MaxN, (uint)puzzleRng.Next());
            var seed = puzzleRng.Next(1, int.MaxValue);
            var pick = new ThreadPuzzlePick(site, type, n, seed, false);

            var lockout = (int)Math.Clamp(Math.Round(def.LockoutSeconds), PuzzleGateTunables.MinLockoutSeconds, PuzzleGateTunables.MaxLockoutSeconds);
            var rounds = Math.Clamp(def.Rounds, PuzzleGateTunables.MinRounds, PuzzleGateTunables.MaxRounds);
            var extra = new[]
            {
                "rounds=" + rounds.ToString(CultureInfo.InvariantCulture),
                "lockout=" + lockout.ToString(CultureInfo.InvariantCulture),
            };

            if (!ThreadPuzzleSitePicker.TryBuildOptions(pick, extra, out var options, out var error))
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle {def.Slot} site={site.Id} type={type}: options refused: {error}");
                return false;
            }

            var host = new SpeedPuzzleGateHost(this, def);
            var id = world.PlacePuzzle(site, pick, options, host, out error);

            if (id == 0)
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle {def.Slot} site={site.Id} type={type}: not placed: {error}");
                return false;
            }

            placementIds[def.Slot] = id;
            world.Log($"[SPEEDCOURSE] {this} puzzle {def.Slot} #{id} site={site.Id} type={type.ToString().ToLowerInvariant()} n={n} seed={seed} rounds={rounds} lockout={lockout}s token={def.Token ?? "-"}");
            return true;
        }

        /// <summary>Test view: the placement id currently standing for a slot, or 0.</summary>
        public int PlacementIdFor(string slot) => placementIds.TryGetValue(slot, out var id) ? id : 0;

        public void OnPuzzleSolved(SpeedPuzzleDef puzzle, Player player)
        {
            if (IsEnded || puzzle == null)
                return;

            world.Log($"[SPEEDCOURSE] {this} puzzle {puzzle.Slot} solved by {player?.Name ?? "-"}; token {puzzle.Token ?? "-"}");

            if (!string.IsNullOrEmpty(puzzle.Token))
                EarnToken(puzzle.Token, $"puzzle {puzzle.Slot} solved");

            OpenPuzzleSlot(puzzle.Slot);
        }

        /// <summary>
        /// The run gives a puzzle slot up (never placed, or removed without a solve past the re-place cap): no gate stands
        /// there, so whatever it held enters, and its token, which the course can no longer offer, is granted.
        /// </summary>
        private void GiveUpPuzzle(SpeedPuzzleDef puzzle, string why)
        {
            if (!string.IsNullOrEmpty(puzzle.Token))
                GrantToken(puzzle.Token, $"puzzle {puzzle.Slot} given up: {why}");

            OpenPuzzleSlot(puzzle.Slot);
        }

        public void OnPuzzleRemoved(SpeedPuzzleDef puzzle, PuzzleRemovalReason reason, bool solved)
        {
            if (IsEnded || solved || puzzle == null)
                return;

            // Any thread: the decision and the re-place run on the instance landblock.
            world.Post(() => ReplaceAfterRemoval(puzzle, reason));
        }

        private void ReplaceAfterRemoval(SpeedPuzzleDef puzzle, PuzzleRemovalReason reason)
        {
            if (IsEnded)
                return;

            placementIds.Remove(puzzle.Slot);

            // Already given up (or solved): no gate goes back on an open slot.
            if (openedPuzzleSlots.Contains(puzzle.Slot))
                return;

            replaceTries.TryGetValue(puzzle.Slot, out var tries);

            if (tries >= MaxPuzzleReplaceTries)
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle {puzzle.Slot} site={puzzle.SiteId} removed without a solve ({reason}) after {tries} re-place(s); giving up - the gate is gone");
                GiveUpPuzzle(puzzle, $"removed without a solve after {tries} re-place(s)");
                return;
            }

            replaceTries[puzzle.Slot] = tries + 1;

            var site = (world.PuzzleSites(Course.DungeonId) ?? Array.Empty<PuzzleSiteDef>())
                .FirstOrDefault(s => s != null && string.Equals(s.Id, puzzle.SiteId, StringComparison.Ordinal));

            if (site == null || !slotTypes.TryGetValue(puzzle.Slot, out var type))
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle {puzzle.Slot} removed ({reason}) and its site or type is unknown; not re-placed");
                GiveUpPuzzle(puzzle, "its site or type is unknown");
                return;
            }

            world.LogWarn($"[SPEEDCOURSE] {this} puzzle {puzzle.Slot} removed without a solve ({reason}); re-placing (try {tries + 1} of {MaxPuzzleReplaceTries})");

            bool placedOk;

            try
            {
                placedOk = PlaceSlot(puzzle, site, type);
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle {puzzle.Slot} re-place threw; giving the slot up: {ex}");
                GiveUpPuzzle(puzzle, "its re-place threw");
                return;
            }

            // A refused re-place is itself a removal without a solve: count it and try again until the cap.
            if (!placedOk)
            {
                OnPuzzleRemoved(puzzle, PuzzleRemovalReason.SpawnFailed, false);
                return;
            }

            if (IsEnded)
                ClearAfterLateEnd($"re-place of {puzzle.Slot}");
        }

        /// <summary>The second sweep for placements registered while End ran on another thread.</summary>
        private void ClearAfterLateEnd(string where)
        {
            try
            {
                var cleared = world.ClearPuzzles();
                world.Log($"[SPEEDCOURSE] {this} ended during {where}; swept {cleared} puzzle placement(s) again");
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle sweep after a late end threw: {ex}");
            }
        }

        // =====================================================================================================
        // Seal tokens and the runner's lines (landblock thread)
        // =====================================================================================================

        /// <summary>Contributes the kill token of every named creature the run has seen die, once each.</summary>
        private void ObserveNamedDeaths()
        {
            ObserveNamedDeath(SpeedNamedSlots.K1, K1);
            ObserveNamedDeath(SpeedNamedSlots.K2, K2);
        }

        private void ObserveNamedDeath(string slot, SpeedCreatureHandle handle)
        {
            if (handle.IsNone || namedTokenContributed.Contains(slot) || world.IsAlive(handle))
                return;

            namedTokenContributed.Add(slot);

            var token = Named(slot)?.Token;

            if (!string.IsNullOrEmpty(token))
                EarnToken(token, $"named {slot} died");
        }

        /// <summary>Contributes a course token the runner earned, with nobody notified, then tells the runner.</summary>
        public void EarnToken(string token, string why)
        {
            if (IsEnded || string.IsNullOrEmpty(token))
                return;

            var weight = Course.WeightOf(token);
            world.Contribute(Course.SealKey, token, weight);
            world.Log($"[SPEEDCOURSE] {this} token {token} weight {weight} to '{Course.SealKey}' ({why})");

            UpdateSealMessages();
        }

        /// <summary>
        /// Contributes <paramref name="token"/> because the course could not offer it this run (a source the run gave
        /// up on, or a hazard that cannot start). Once per token; nothing when the token is already earned. Logged WARN,
        /// and the runner is told the course's "granted" line instead of the progress line.
        /// </summary>
        public void GrantToken(string token, string reason)
        {
            if (IsEnded || string.IsNullOrEmpty(token) || granted.Contains(token) || announced.Contains(token))
                return;

            if (world.SealHasToken(Course.SealKey, token) == true)
                return;

            granted.Add(token);
            world.LogWarn($"[SPEEDCOURSE] {this} granting token {token}: {reason}");

            var weight = Course.WeightOf(token);
            world.Contribute(Course.SealKey, token, weight);

            UpdateSealMessages();
        }

        /// <summary>True once <paramref name="token"/> was granted rather than earned.</summary>
        public bool WasGranted(string token) => granted.Contains(token);

        /// <summary>
        /// A named slot the run could not field: grants its own kill token and the token of every hazard that waits on its
        /// death (K2 -> the south memory's south_rite), since neither can be earned without it.
        /// </summary>
        private void GrantForNamed(string slot, string reason)
        {
            GrantToken(Named(slot)?.Token, reason);

            foreach (var h in Course.Hazards ?? new List<SpeedHazardDef>())
            {
                if (h?.Trigger == null || string.IsNullOrEmpty(h.Token))
                    continue;

                if (string.Equals(h.Trigger.Type, SpeedHazardTriggers.OnNamedDeadAndInRegion, StringComparison.Ordinal)
                    && string.Equals(h.Trigger.Slot, slot, StringComparison.Ordinal))
                    GrantToken(h.Token, $"{reason} (hazard '{h.Id}' waits on {slot})");
            }
        }

        /// <summary>
        /// Tells the runner every course token that is now live on the Seal and has not been told yet (in token-table
        /// order), then the Seal's opening once it latched. Reads the lock, so a contributor object's token (the east
        /// lectern) is told as soon as the run sees it.
        /// </summary>
        private void UpdateSealMessages()
        {
            if (IsEnded || Course.Tokens == null)
                return;

            foreach (var token in Course.Tokens.Keys)
            {
                if (announced.Contains(token) || world.SealHasToken(Course.SealKey, token) != true)
                    continue;

                announced.Add(token);

                var groups = SpeedCourseRules.ProgressText(Course, announced);
                var template = granted.Contains(token) ? Course.Messages?.Granted : Course.Messages?.Progress;
                world.Tell(SpeedCourseRules.FormatMessage(template, Course.LabelOf(token), groups));
            }

            if (!sealOpenAnnounced && world.SealLatched(Course.SealKey) == true)
            {
                sealOpenAnnounced = true;
                world.Tell(Course.Messages?.SealOpen);
            }
        }

        /// <summary>Tokens told to the runner so far, in order.</summary>
        public IReadOnlyList<string> AnnouncedTokens => announced;

        public void Log(string line) => world.Log($"[SPEEDCOURSE] {this} {line}");

        // =====================================================================================================
        // Teardown (any thread)
        // =====================================================================================================

        /// <summary>
        /// Ends the run once: clears its puzzle gates, stops the hazard driver, and tells the manager. Idempotent; the
        /// creatures stay in the instance, which goes away with it.
        /// </summary>
        public void End(string reason)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0)
                return;

            EndReason = reason;

            var cleared = 0;

            try
            {
                cleared = world.ClearPuzzles();
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} puzzle clear threw at teardown: {ex}");
            }

            // The runner must never be stuck inside after the run ends. End can run on any thread, so the open is posted
            // onto the landblock thread (a no-op there if the landblock is already gone).
            if (sealDoorShut)
            {
                sealDoorShut = false;

                try
                {
                    var key = Course.SealKey;
                    world.Post(() => world.SetSealDoor(key, false));
                }
                catch (Exception ex)
                {
                    world.LogError($"[SPEEDCOURSE] {this} Seal door open at teardown threw: {ex}");
                }
            }

            try
            {
                Hazards.Stop(this, reason);
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} hazard driver stop threw: {ex}");
            }

            world.Log($"[SPEEDCOURSE] {this} ended ({reason}); {cleared} puzzle placement(s) cleared, {spawned} creature(s) were placed, boss {(Boss.IsNone ? "never spawned" : "spawned")}");

            try
            {
                Ended?.Invoke(this);
            }
            catch (Exception ex)
            {
                world.LogError($"[SPEEDCOURSE] {this} ended callback threw: {ex}");
            }
        }
    }
}
