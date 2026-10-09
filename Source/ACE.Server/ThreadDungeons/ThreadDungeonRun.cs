using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ACE.Entity;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    public enum ThreadDungeonRunState
    {
        Starting = 0,
        Active = 1,
        Cleared = 2,
        Ended = 3,
    }

    /// <summary>
    /// One live Thread run: the private instance, its owner, the gem that opened it and the kill
    /// ledger. Purely in-memory; a restart ends every run (the gem sweep in ThreadDungeonSweeper is what
    /// reconciles gems bound to a run that no longer exists).
    ///
    /// Threading (TECH-DESIGN section 6, execution-log ruling R10). The mutable half of this object is
    /// written from landblock tick threads - RecordKill runs from Creature.Die() and MarkPopulated from the
    /// spawner's landblock action queue, and landblocks tick in parallel - while ThreadDungeonManager.Tick
    /// reads it from the world thread. Every mutable member therefore lives behind the private
    /// <see cref="stateLock"/>, taken by both the readers and the writers, so a reader can never observe a
    /// half-applied MarkPopulated (Spawned set but State still Starting) and two concurrent kills can never
    /// lose an increment. The lock is uncontended in practice: one run has one landblock, and a landblock
    /// ticks on one thread at a time.
    ///
    /// The immutable half (RunId, OwnerGuid, Spec, Dungeon, StartedUtc, ExpiresUtc, ...) is write-once in the
    /// constructor and is read without the lock. ExitTo and EntryPosition are settable, but by convention
    /// only ThreadDungeonManager.TryStart writes them, before the run is published to the registry and so
    /// before any other thread can see it. They MUST NOT be re-assigned after that: they are read from
    /// landblock threads without the lock, and nothing here would make a later write visible safely.
    ///
    /// THREE members deliberately sit OUTSIDE stateLock, and the exception is narrow. The telemetry
    /// counters <see cref="XpEarned"/> and <see cref="LumEarned"/> and the counter behind
    /// <see cref="StartAttempts"/> are independent scalars: nothing reads them together with the state, no
    /// invariant relates them to killed/spawned, and no decision is taken on them - they are only ever
    /// summed and, once, snapshotted at EndRun. What the lock buys everything else is a CONSISTENT VIEW
    /// across several fields, and there is no such view to protect here. They are written from the XP and
    /// luminance grant path, which is as hot as anything in the server, so they use Interlocked.Add and
    /// Interlocked.Read instead and never contend with a landblock tick that is counting kills.
    ///
    /// The placement ledger (<see cref="RecordPlacement"/>) is likewise its own ConcurrentDictionary rather
    /// than lock-guarded state, for the same reason: it aggregates independently of everything the lock
    /// covers, and it is appended to from the world thread (plan time) and from landblock threads
    /// (placement time) alike.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        private readonly object stateLock = new object();

        /// <summary>The run id IS the ephemeral instance id, so a gem's run= field resolves with one lookup.</summary>
        public uint RunId { get; }

        /// <summary>
        /// Groups every run one GEM USE produced. Minted once per use and carried across every attempt that
        /// use spawns, so the telemetry can tell what the player did from what the server tried.
        ///
        /// It exists for the silent-retry path: a run that cannot be populated winnably is aborted and
        /// retried, and each attempt goes back through TryStart, which allocates a FRESH ephemeral instance
        /// id and registers a NEW run. One gem use that succeeds on its third attempt therefore produces
        /// three rows. Filtering on entry fixes the averages but not the COUNT - three rows still read as
        /// three starts - and the inflation is exactly proportional to the fault rate the data exists to
        /// measure, so the worse placement gets the more it looks like traffic. Nothing else ties the rows
        /// together, so this cannot be recovered after the fact; it has to be minted at the use.
        ///
        /// A GUID, not a counter, and NOT the gem guid. <see cref="RunId"/> recycles across restarts, so any
        /// grouping key derived from it would carry a time-window caveat and a query spanning a restart
        /// would silently fuse unrelated starts. The gem guid is worse still: one gem carries several
        /// entries and reopens runs on separate occasions, so it would fuse starts weeks apart. A fresh GUID
        /// has neither failure mode, and this table is far too low-volume for 32 bytes to matter.
        /// </summary>
        public string StartGroup { get; }
        public uint Instance => RunId;
        public uint OwnerGuid { get; }
        public string OwnerName { get; }
        public uint AccountId { get; }
        public uint GemGuid { get; }
        public DungeonGemSpec Spec { get; }
        public DungeonEntryDef Dungeon { get; }
        public uint ExitPortalWcid => Dungeon.ExitPortalWcid;
        public DateTime StartedUtc { get; }
        public DateTime ExpiresUtc { get; }

        /// <summary>
        /// The owner's level at the moment the run opened, captured once in the constructor and never
        /// updated. 0 when the caller did not supply one (tests, and any future caller that does not have a
        /// Player to hand).
        ///
        /// This is the level that answers "was this gem level appropriate for this player", and it has to be
        /// the START level to answer it. Resolving the level at EndRun instead records a player who levelled
        /// mid-run at their POST-run level, and that bias is not spread evenly: a clear is what banks the
        /// large reward, so the error concentrates in exactly the cleared runs an analyst cares most about.
        /// The end-time level is recorded too, as a separate column, because the pair is also the only way
        /// to see that a run levelled its owner at all.
        /// </summary>
        public int OwnerLevelAtStart { get; }

        private ThreadDungeonRunState state = ThreadDungeonRunState.Starting;
        private string endReason;
        private DateTime? clearedUtc;
        private bool playerObserved;
        private int planned;
        private int spawned;
        private int killed;
        private uint bossWcid;
        private uint bossWcidIntended;
        private bool bossKilled;
        private bool clearedAnnounced;
        private bool bossChestClaimed;
        private bool rewardProfileSet;
        private ACE.Database.Models.World.TreasureDeath lootProfile;
        private double lootQuantityMult = 1.0;
        private IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> salvageAffinities;
        private double bossHealthRatio;
        private bool bossHealthClamped;
        private bool surveyFiled;
        private int? populateMs;

        /// <summary>
        /// The plan-time facts this run's population actually drew, latched by <see cref="MarkPlan"/>.
        /// OUTSIDE stateLock on the same footing as the placement ledger: it is written once, from the world
        /// thread, before this run does anything stateLock covers, and nothing here needs a consistent view
        /// with State/Planned/Spawned/Killed. Null until MarkPlan runs, which never happens for a run whose
        /// population threw before a plan was built.
        /// </summary>
        private DungeonRunPlanRecord planRecord;

        // Telemetry counters. OUTSIDE stateLock on purpose - see the threading note on the class.
        private long xpEarned;
        private long lumEarned;

        /// <summary>0/1 rather than bool so it can be latched with Interlocked from any thread.</summary>
        private int populateReached;

        /// <summary>
        /// Stopwatch.GetTimestamp() taken when the populate chain began; 0 while it never has.
        /// MONOTONIC, not DateTime.UtcNow: the wall clock can step (NTP, a DST-adjacent correction) and a
        /// negative or wildly inflated duration would poison the aggregate rather than one row.
        /// </summary>
        private long populateStartTicks;

        /// <summary>
        /// The run's placement ledger, aggregated on (phase, reason, wcid, role) so a code that fires for
        /// the same creature many times becomes ONE entry with a higher attempt count rather than N
        /// near-identical rows. Concurrent because the world thread appends the plan-time codes and landblock
        /// threads append the placement-time ones.
        ///
        /// Phase is IN the key, not merely carried alongside it. The same reason string is going to be
        /// emitted from both the builder and the spawner once boss killability checking moves to plan time,
        /// and at that point a plan-time and a place-time failure of the same wcid mean different things with
        /// different owners. Keying without phase would merge them into one row and the distinction would be
        /// unrecoverable.
        /// </summary>
        private readonly ConcurrentDictionary<(string Phase, string Reason, uint Wcid, DungeonRole Role), PlacementCounter> placements
            = new ConcurrentDictionary<(string, string, uint, DungeonRole), PlacementCounter>();

        /// <summary>
        /// The mutable half of one ledger entry. A class rather than a struct because the dictionary hands
        /// out a reference that <see cref="RecordPlacement"/> then increments in place with Interlocked;
        /// a struct value would be a copy and the increment would be lost.
        /// </summary>
        private sealed class PlacementCounter
        {
            public int Attempts;

            /// <summary>0/1 rather than bool so it can be latched with Interlocked.Exchange from any thread.</summary>
            public int EntryPlaced;
        }

        public ThreadDungeonRunState State { get { lock (stateLock) return state; } }
        public string EndReason { get { lock (stateLock) return endReason; } }

        /// <summary>
        /// When the run entered Cleared, or null while it never has. Stamped inside
        /// <see cref="CheckClearedLocked"/> under <see cref="stateLock"/>, in the same critical section that
        /// makes the transition, so a reader can never see Cleared without the timestamp that goes with it.
        /// ThreadDungeonManager.Tick reads it to age a finished run out of the registry.
        /// </summary>
        public DateTime? ClearedUtc { get { lock (stateLock) return clearedUtc; } }

        /// <summary>
        /// Has a player ever actually MATERIALISED inside this run's copy? A latch, never cleared: it records
        /// that entry happened at all, not who is inside now.
        ///
        /// It exists to make ThreadDungeonManager's cleared-and-empty reap safe. That reap asks "is anyone
        /// in there", and the honest answer for a run whose owner is still in transit is "not yet" rather
        /// than "no" - a plan that placed nothing reaches Cleared before its owner has finished
        /// materialising, and the reap's probe reads the landblock's object list without flushing pending
        /// additions (Landblock.GetAllWorldObjectsForDiagnostics), so a player added on the last landblock
        /// tick is invisible to it. Requiring this latch first means a run nobody has ever entered can never
        /// be reaped for standing empty; it falls to the TTL and load-timeout branches instead, which is
        /// where a run that never came up belongs.
        /// </summary>
        public bool PlayerEverObserved { get { lock (stateLock) return playerObserved; } }

        /// <summary>
        /// Latches <see cref="PlayerEverObserved"/>. Called from Player.OnTeleportComplete once a player has
        /// left the pink-bubble state inside this instance, and again from the manager's reap probe whenever
        /// it happens to see a player. Idempotent and safe from any thread.
        /// </summary>
        public void MarkPlayerObserved()
        {
            lock (stateLock)
                playerObserved = true;
        }

        public int Planned { get { lock (stateLock) return planned; } }
        public int Spawned { get { lock (stateLock) return spawned; } }
        public int Killed { get { lock (stateLock) return killed; } }
        public uint BossWcid { get { lock (stateLock) return bossWcid; } }
        public bool BossSpawned { get { lock (stateLock) return bossWcid != 0; } }
        public bool BossKilled { get { lock (stateLock) return bossKilled; } }

        /// <summary>
        /// Trash (non-boss) creatures placed: Spawned minus the boss slot when a boss is present. Never
        /// negative. See <see cref="TrashSpawnedLocked"/>.
        /// </summary>
        public int TrashSpawned { get { lock (stateLock) return TrashSpawnedLocked(); } }

        /// <summary>
        /// Trash (non-boss) kills so far: Killed minus the boss kill when the boss is dead. Never negative.
        /// See <see cref="TrashKilledLocked"/>.
        /// </summary>
        public int TrashKilled { get { lock (stateLock) return TrashKilledLocked(); } }

        /// <summary>Caller must hold <see cref="stateLock"/>.</summary>
        private int TrashSpawnedLocked() => Math.Max(0, spawned - (bossWcid != 0 ? 1 : 0));

        /// <summary>Caller must hold <see cref="stateLock"/>.</summary>
        private int TrashKilledLocked() => Math.Max(0, killed - (bossKilled ? 1 : 0));

        /// <summary>
        /// Weighted clear progress in [0, 1] (owner ruling R28, replacing R27's boss-OR-fraction rule). A run
        /// with a boss splits its content into two pools: the boss alone is worth <see cref="BossWeight"/> of
        /// the total, and every trash creature shares the remaining (1 - BossWeight) evenly. A bossless run
        /// (none authored, or the boss failed to place) has no boss pool to withhold: its trash is
        /// renormalized to the FULL 1.0, so it still clears at ClearFraction of its trash rather than being
        /// stuck holding back BossWeight it can never earn.
        ///
        /// TrashSpawned == 0 makes the trash term vacuously 1.0 (fully "cleared" by having nothing left to
        /// kill) - this is what makes a plan that places nothing (Spawned 0, no boss) reach Cleared the
        /// instant MarkPopulated runs, which ThreadDungeonManager.OnRunPopulated and PlayerEverObserved's
        /// doc comment both depend on. Do not regress it.
        ///
        /// Math.Min(1.0, ...) on the trash fraction guards against Killed ever exceeding Spawned (should not
        /// happen, but progress must never read above 1.0 regardless).
        ///
        /// Caller must hold <see cref="stateLock"/>.
        /// </summary>
        private double ClearProgressLocked()
        {
            var trashSpawned = TrashSpawnedLocked();
            var trashKilled = TrashKilledLocked();
            var trashFraction = trashSpawned == 0 ? 1.0 : Math.Min(1.0, (double)trashKilled / trashSpawned);

            if (bossWcid == 0)
                return trashFraction;

            return BossWeight * (bossKilled ? 1.0 : 0.0) + (1.0 - BossWeight) * trashFraction;
        }

        /// <summary>Weighted clear progress in [0, 1]. See <see cref="ClearProgressLocked"/>.</summary>
        public double ClearProgress { get { lock (stateLock) return ClearProgressLocked(); } }

        /// <summary>
        /// The boss wcid the PLAN chose, whether or not it ever entered the world. <see cref="BossWcid"/> is
        /// the one that actually placed.
        ///
        /// There are THREE outcomes, not two, and the wcid pair alone cannot separate the first two - a
        /// consumer that buckets on "placed == 0" will report a bossless dungeon as a placement failure:
        ///
        ///   1. Bossless dungeon. The dungeon has no boss anchor, so no boss was ever asked for. Both wcids
        ///      are 0 and there is NO placement ledger row.
        ///   2. No candidate. A boss anchor exists, but neither the curated bosses.json window nor the run's
        ///      own family could field one, so the plan never chose a wcid. Both wcids are ALSO 0, and there
        ///      IS a ledger row, with reason no_candidate and wcid 0.
        ///   3. Placement failure. The plan chose a boss and it did not reach the world. Intended is set,
        ///      placed is 0, and there is a ledger row carrying a terminal reason and the boss's wcid.
        ///
        /// So the ledger row, not the wcid pair, is what tells case 1 from case 2. Only case 3 is visible in
        /// the wcids alone.
        /// </summary>
        public uint BossWcidIntended { get { lock (stateLock) return bossWcidIntended; } }

        /// <summary>
        /// Achieved boss Health.MaxValue over the largest non-boss Health.MaxValue in the same run, as
        /// measured at placement time; 0.0 when no boss placed or there was no pack to measure against.
        /// A DIFFICULTY signal, not a placement one: the boss this describes entered the world.
        /// </summary>
        public double BossHealthRatio { get { lock (stateLock) return bossHealthRatio; } }

        /// <summary>Did the boss's health target prove unreachable, so StartingValue was clamped to 1?</summary>
        public bool BossHealthClamped { get { lock (stateLock) return bossHealthClamped; } }

        /// <summary>Latched when the daily survey was ACTUALLY filed for this run, not when filing was attempted.</summary>
        public bool SurveyFiled { get { lock (stateLock) return surveyFiled; } }

        /// <summary>
        /// Did this run ever get as far as building a plan and enqueueing a placement chain? A one-shot
        /// latch, never a count and never cleared.
        ///
        /// It exists because it is the ONLY thing that separates two very different runs that both report
        /// spawned = 0: a copy that never finished loading, so nothing was ever attempted, and a plan that
        /// ran and placed nothing. Those want opposite responses - the first is a server fault, the second
        /// is a content or curation gap - and no other column tells them apart.
        /// </summary>
        public bool PopulateReached => Volatile.Read(ref populateReached) != 0;

        /// <summary>
        /// Milliseconds from the start of population to its completion, or NULL while population never
        /// completed. It pairs with <see cref="PopulateReached"/>: the latch says whether it finished, this
        /// says how long it took.
        ///
        /// NULL rather than 0 or -1. A run that never populated has no duration, and NULL is the value that
        /// says so - it also drops out of AVG and percentile queries by itself, where any sentinel would
        /// silently drag them toward zero. That is the same class of error as counting never-entered runs in
        /// a gameplay average.
        /// </summary>
        public int? PopulateMs { get { lock (stateLock) return populateMs; } }

        /// <summary>XP the owner banked while standing inside this run's instance.</summary>
        public long XpEarned => Interlocked.Read(ref xpEarned);

        /// <summary>Luminance the owner banked while standing inside this run's instance.</summary>
        public long LumEarned => Interlocked.Read(ref lumEarned);

        /// <summary>
        /// Banks XP earned inside this run. Called from AnalyticsManager.RecordXp, which is on the grant
        /// path for every kill in the game, so this is an Interlocked.Add and nothing else - see the class
        /// threading note for why it is outside stateLock.
        /// </summary>
        public void AddXp(long amount)
        {
            if (amount != 0)
                Interlocked.Add(ref xpEarned, amount);
        }

        /// <summary>Banks luminance earned inside this run. Same contract as <see cref="AddXp"/>.</summary>
        public void AddLum(long amount)
        {
            if (amount != 0)
                Interlocked.Add(ref lumEarned, amount);
        }

        /// <summary>
        /// Latches <see cref="PopulateReached"/> and starts the populate stopwatch. Called by
        /// ThreadDungeonSpawner once it has claimed the populate latch, which is the point past which a plan
        /// is actually built and a placement chain enqueued. Idempotent and safe from any thread.
        ///
        /// The timestamp is first-write-wins (CompareExchange against 0), so a repeat call cannot restart
        /// the clock and report a populate as faster than it was.
        /// </summary>
        public void MarkPopulateReached()
        {
            Interlocked.CompareExchange(ref populateStartTicks, Stopwatch.GetTimestamp(), 0);
            Interlocked.Exchange(ref populateReached, 1);
        }

        /// <summary>
        /// The plan-time facts this run's population drew, or null when it never got as far as building one.
        /// See <see cref="planRecord"/> for the threading contract.
        /// </summary>
        public DungeonRunPlanRecord PlanRecord => Volatile.Read(ref planRecord);

        /// <summary>
        /// Latches this run's plan-time telemetry. FIRST-WRITE-WINS (CompareExchange against null), so a
        /// call this run should never receive twice cannot silently replace an already-recorded plan with a
        /// second one. Idempotent and safe from any thread; a null record is ignored rather than latched,
        /// since a null means "nothing to record" and must never overwrite a real one that raced ahead of it.
        /// </summary>
        public void MarkPlan(DungeonRunPlanRecord record)
        {
            if (record == null)
                return;

            Interlocked.CompareExchange(ref planRecord, record, null);
        }

        /// <summary>
        /// Appends one placement attempt to the ledger. Safe from any thread and from either producer: the
        /// world thread adds the plan-time codes, landblock threads add the placement-time ones.
        ///
        /// Aggregation is by (phase, reason, wcid, role), so a code that fires ten times for the same
        /// creature in the same phase is ONE entry with attempts = 10, not ten entries. That is also why the
        /// column is called attempts and not entries: one plan entry can contribute several attempts once
        /// anchor fallback lands, and one aggregated entry can cover several distinct plan entries.
        /// </summary>
        /// <param name="phase">DungeonRunTelemetry.Phases.Plan or .Place - part of the key, see the ledger field.</param>
        public void RecordPlacement(string phase, string reason, uint wcid, DungeonRole role)
        {
            if (string.IsNullOrEmpty(reason) || string.IsNullOrEmpty(phase))
                return;

            var counter = placements.GetOrAdd((phase, reason, wcid, role), _ => new PlacementCounter());
            Interlocked.Increment(ref counter.Attempts);
        }

        /// <summary>
        /// Records that a plan entry for this (wcid, role) reached the world after all, so a ledger row that
        /// described a RECOVERY can say the recovery worked.
        ///
        /// Only recovery rows are touched. Every failure code is terminal for the entry it refused - the
        /// creature was destroyed and that entry is gone - so marking one placed because a SIBLING entry of
        /// the same wcid and role succeeded elsewhere would be a straightforward lie, and a pack of eight
        /// identical trash creatures makes that the common case rather than a corner one. Leaving terminal
        /// rows at 0 is what keeps "is_failure = 1 AND entry_placed = 0" an honest count of creatures the run
        /// never got.
        /// </summary>
        public void MarkEntryPlaced(uint wcid, DungeonRole role)
        {
            foreach (var kvp in placements)
            {
                if (kvp.Key.Wcid != wcid || kvp.Key.Role != role || DungeonRunTelemetry.IsFailure(kvp.Key.Reason))
                    continue;

                Interlocked.Exchange(ref kvp.Value.EntryPlaced, 1);
            }
        }

        /// <summary>
        /// An immutable snapshot of the ledger, for the analytics row. Materialised rather than handed out
        /// live: the queued payload must never reference anything the run still writes to.
        /// </summary>
        public IReadOnlyList<DungeonPlacementRecord> SnapshotPlacements()
            => placements
                .Select(kvp => new DungeonPlacementRecord(kvp.Key.Phase, kvp.Key.Reason, kvp.Key.Wcid, kvp.Key.Role,
                    Volatile.Read(ref kvp.Value.Attempts), Volatile.Read(ref kvp.Value.EntryPlaced) != 0))
                .OrderBy(p => p.Phase, StringComparer.Ordinal)
                .ThenBy(p => p.Reason, StringComparer.Ordinal)
                .ThenBy(p => p.Wcid)
                .ThenBy(p => p.Role)
                .ToList();

        /// <summary>
        /// Records the boss's achieved health against its target. Written from the landblock thread that
        /// placed the boss, under the same lock as everything else the spawner reports, and BEFORE
        /// EnterWorld - a boss whose health fell short still places.
        /// </summary>
        public void RecordBossHealth(double ratio, bool clamped)
        {
            lock (stateLock)
            {
                bossHealthRatio = ratio;
                bossHealthClamped = clamped;
            }
        }

        /// <summary>
        /// Latches <see cref="SurveyFiled"/>. Called at the point the survey is actually written to the
        /// owner's ledger, never at the point filing is decided on: the two differ whenever the owner logs
        /// out between the clear and the enqueued action running, and the flag has to mean the survey exists.
        /// </summary>
        public void MarkSurveyFiled()
        {
            lock (stateLock)
                surveyFiled = true;
        }

        /// <summary>
        /// REDEFINED under R28 (was: total kills including the boss needed on the trash-fraction path alone,
        /// with the boss kill itself an alternate path to Cleared - that alternate path no longer exists).
        /// Now: the number of TRASH kills needed to clear, assuming the boss is already dead (for a bossless
        /// run, the trash kills needed outright - there being no boss pool to assume anything about).
        /// Derived by solving ClearProgress >= ClearFraction for trashFraction with bossKilled fixed at true:
        ///   requiredTrashFraction = ClearFraction              (no boss present)
        ///                          = 0                          (BossWeight == 1, trash is worthless)
        ///                          = clamp((ClearFraction - BossWeight) / (1 - BossWeight), 0, 1)  (otherwise)
        /// 0 while TrashSpawned is 0 (Starting, or a plan whose trash placed nothing).
        /// </summary>
        public int ClearTarget { get { lock (stateLock) return ClearTargetLocked(); } }

        /// <summary>Caller must hold <see cref="stateLock"/>.</summary>
        // The 1e-9 epsilon absorbs float error in requiredTrashFraction * trashSpawned (e.g. multiplying a
        // fraction that should land exactly on an integer boundary but comes out a hair over, e.g.
        // 0.875 * 16 == 14.000000000000002) that would otherwise push Math.Ceiling to the next integer.
        private int ClearTargetLocked()
        {
            var trashSpawned = TrashSpawnedLocked();
            if (trashSpawned == 0) return 0;

            double requiredTrashFraction;
            if (bossWcid == 0)
                requiredTrashFraction = ClearFraction;
            else if (1.0 - BossWeight <= 1e-9)
                requiredTrashFraction = 0.0;
            else
                requiredTrashFraction = Math.Clamp((ClearFraction - BossWeight) / (1.0 - BossWeight), 0.0, 1.0);

            return (int)Math.Ceiling(requiredTrashFraction * trashSpawned - 1e-9);
        }

        /// <summary>Where the owner stood when the gem was used; deaths and the exit portal return here.</summary>
        public Position ExitTo { get; set; }
        /// <summary>Instanced entry position inside the copy (dungeon.Entry stamped with Instance).</summary>
        public Position EntryPosition { get; set; }

        /// <summary>
        /// Target weighted clear progress (see <see cref="ClearProgress"/>) needed to clear, owner ruling R28
        /// amending R27's flat kill-fraction rule (which itself replaced R17's kill-everything rule). Read
        /// once from the dynamic_dungeons_clear_fraction tunable by ThreadDungeonManager.TryStart and set
        /// here before the run is published to the registry - same convention as ExitTo/EntryPosition, so it
        /// MUST NOT be re-assigned after that. Defaults to 1.0 (kill everything) for callers, tests included,
        /// that never set it.
        /// </summary>
        public double ClearFraction { get; set; } = 1.0;

        /// <summary>
        /// Share of a run's weighted clear progress that the boss alone is worth when one is present (owner
        /// ruling R28). The remaining (1 - BossWeight) is trash's share, split evenly across every non-boss
        /// run creature. Read once from the dynamic_dungeons_boss_clear_weight tunable by
        /// ThreadDungeonManager.TryStart and set here before the run is published to the registry - same
        /// write-once-before-publication convention as ExitTo/EntryPosition/ClearFraction. Defaults to 0.2 for
        /// callers, tests included, that never set it - deliberately different from ClearFraction's 1.0
        /// no-caller default, since 0.2 is this field's own real production default (see PropertyManager).
        /// </summary>
        public double BossWeight { get; set; } = 0.2;

        /// <summary>
        /// Loot ROLLS one pooled-cache delivery step may materialise before it re-enqueues itself onto the run
        /// landblock's action queue (ThreadCachePlacer.RequestPlacement, and
        /// Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 4). Read once from the
        /// dynamic_dungeons_cache_roll_batch_size tunable by ThreadDungeonManager.TryStart and set here before the
        /// run is published to the registry - the same write-once-before-publication convention as
        /// ExitTo/EntryPosition/ClearFraction, so it MUST NOT be re-assigned after that.
        ///
        /// It is a run field rather than a read at the point of use because the batch steps run on a LANDBLOCK
        /// thread, and PropertyManager reads belong on the world thread (they also throw outright under the MSTest
        /// harness). Defaults to ThreadLootRollBudget.DefaultRollsPerStep, this field's real production default.
        /// </summary>
        public int CacheRollBatchSize { get; set; } = ThreadLootRollBudget.DefaultRollsPerStep;

        /// <summary>
        /// Wall-clock milliseconds one pooled-cache delivery step may spend materialising loot before it re-enqueues
        /// itself: the step stops after the roll during which its Stopwatch crosses this (ThreadLootRollBudget).
        /// <see cref="CacheRollBatchSize"/> stays as the hard roll cap alongside it. Read once from
        /// dynamic_dungeons_cache_step_budget_ms by ThreadDungeonManager.TryStart and set here before the run is
        /// published, on the same write-once convention and for the same landblock-thread reason as
        /// CacheRollBatchSize. Defaults to ThreadLootRollBudget.DefaultStepBudgetMs, its real production default.
        /// </summary>
        public int CacheStepBudgetMs { get; set; } = ThreadLootRollBudget.DefaultStepBudgetMs;

        /// <summary>
        /// Wall-clock milliseconds one loot-trickle step may spend building ledger rolls while the run is Active
        /// (ThreadLootTrickle); 0 turns the trickle off, which is the pre-trickle behaviour. Read once from
        /// dynamic_dungeons_loot_trickle_budget_ms by ThreadDungeonManager.TryStart and stamped before publication, on
        /// the same write-once convention and landblock-thread reason as <see cref="CacheStepBudgetMs"/>.
        /// </summary>
        public int LootTrickleBudgetMs { get; set; } = ThreadLootTrickle.DefaultBudgetMs;

        /// <summary>
        /// The most out-of-world objects the trickle may hold built ahead of the clear
        /// (dynamic_dungeons_loot_trickle_max_prebuilt); past it the trickle pauses and the rest is built at the clear
        /// as before. Stamped like <see cref="LootTrickleBudgetMs"/>.
        /// </summary>
        public int LootTrickleMaxPrebuilt { get; set; } = ThreadLootTrickle.DefaultMaxPrebuilt;

        /// <param name="ownerLevelAtStart">
        /// See <see cref="OwnerLevelAtStart"/>. Optional and defaulted so the existing callers and tests
        /// that have no Player to read a level from read unchanged.
        /// </param>
        /// <param name="startGroup">
        /// See <see cref="StartGroup"/>. Null or empty MINTS a fresh group, which is the right default:
        /// a run nobody grouped is its own group of one, so the column is never null and a caller that
        /// forgets to pass one degrades to today's behaviour rather than to a broken key. Only a retry loop
        /// re-using one gem use's group across its attempts supplies a value.
        /// </param>
        public ThreadDungeonRun(uint runId, uint ownerGuid, string ownerName, uint accountId, uint gemGuid, DungeonGemSpec spec, DungeonEntryDef dungeon, DateTime startedUtc, TimeSpan ttl, int ownerLevelAtStart = 0, string startGroup = null)
            : this(runId, new[] { new RosterSeat(ownerGuid, ownerName, accountId, ownerLevelAtStart) }, gemGuid, spec, dungeon, startedUtc, ttl, GroupScaling.Solo, startGroup)
        {
        }

        /// <summary>
        /// The roster constructor (Group Threads). <paramref name="seats"/>[0] is the OWNER, and OwnerGuid,
        /// OwnerName, AccountId and OwnerLevelAtStart are read from it. The roster is the owner followed by every
        /// other seat in ascending character guid (invariant 4); null seats and repeated guids (the owner's
        /// included) are dropped.
        ///
        /// <paramref name="group"/> is the lock-time snapshot; null reads as <see cref="GroupScaling.Solo"/>. Its
        /// RosterSize must equal the de-duplicated roster count, because N is read from the snapshot everywhere
        /// (invariant 2) and a disagreement would scale the run for a roster it does not have.
        /// </summary>
        /// <exception cref="ArgumentException">seats is null or empty, seats[0] is null, or the group size disagrees with the roster.</exception>
        public ThreadDungeonRun(uint runId, IReadOnlyList<RosterSeat> seats, uint gemGuid, DungeonGemSpec spec, DungeonEntryDef dungeon, DateTime startedUtc, TimeSpan ttl, GroupScaling group, string startGroup = null)
        {
            if (seats == null || seats.Count == 0)
                throw new ArgumentException("A run needs at least one roster seat (the owner).", nameof(seats));

            var ownerSeat = seats[0] ?? throw new ArgumentException("seats[0] must be the owner's seat.", nameof(seats));

            var roster = OrderRoster(seats);
            group ??= GroupScaling.Solo;

            if (group.RosterSize != roster.Count)
                throw new ArgumentException($"Group snapshot is for {group.RosterSize} member(s) but the roster has {roster.Count}.", nameof(group));

            OwnerLevelAtStart = ownerSeat.Level;
            StartGroup = string.IsNullOrEmpty(startGroup) ? NewStartGroup() : startGroup;
            RunId = runId;
            OwnerGuid = ownerSeat.Guid;
            OwnerName = ownerSeat.Name;
            AccountId = ownerSeat.AccountId;
            GemGuid = gemGuid;
            Spec = spec;
            Dungeon = dungeon;
            StartedUtc = startedUtc;
            ExpiresUtc = startedUtc + ttl;

            Group = group;
            InitRoster(roster);
        }

        /// <summary>
        /// Mints a start-group id. "N" format, so it is 32 hex characters with no braces or hyphens and
        /// fits start_group CHAR(32) exactly. One place, so the constructor's default and a retry loop's
        /// own mint cannot produce differently shaped ids.
        /// </summary>
        public static string NewStartGroup() => Guid.NewGuid().ToString("N");

        public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresUtc;

        /// <param name="bossWcid">The boss that ACTUALLY entered the world; 0 for none.</param>
        /// <param name="bossWcidIntended">
        /// The boss the PLAN chose, whether or not it placed. Optional and defaulted so the existing callers
        /// and tests that only care about the clear rule read unchanged; the two differ exactly when a boss
        /// was drawn and failed to place, which is the case the telemetry exists to make visible.
        /// </param>
        public void MarkPopulated(int planned, int spawned, uint bossWcid, uint bossWcidIntended = 0)
        {
            lock (stateLock)
            {
                if (state != ThreadDungeonRunState.Starting) return;
                this.planned = planned;
                this.spawned = spawned;
                this.bossWcid = bossWcid;
                this.bossWcidIntended = bossWcidIntended;

                // Population has completed, so stop the clock. Inside the state check, so only the ONE call
                // that makes the transition records a duration; a later no-op call cannot overwrite it.
                var startTicks = Interlocked.Read(ref populateStartTicks);

                if (startTicks != 0)
                {
                    var elapsed = Stopwatch.GetTimestamp() - startTicks;
                    populateMs = (int)Math.Clamp(elapsed * 1000L / Stopwatch.Frequency, 0L, int.MaxValue);
                }

                state = ThreadDungeonRunState.Active;
                CheckClearedLocked();
            }
        }

        /// <summary>
        /// Counts one run creature's death.
        ///
        /// Starting counts too, and that is not a nicety. The spawner places in batches across several
        /// landblock action-queue steps, and everything an early batch placed is alive and killable while the
        /// later batches are still running - a fast player, or a creature that spawns inside a hazard, can
        /// land a kill before MarkPopulated moves the run to Active. Dropping those kills leaves killed
        /// permanently short of spawned and the run can never reach Cleared. There is no ordering hazard:
        /// CheckClearedLocked only ever fires while Active, and MarkPopulated calls it once the real spawned
        /// count is known, so a kill banked during Starting is simply counted late.
        ///
        /// Cleared and Ended still ignore kills: the ledger is closed.
        /// </summary>
        public void RecordKill(bool isBoss)
        {
            lock (stateLock)
            {
                if (state != ThreadDungeonRunState.Active && state != ThreadDungeonRunState.Starting) return;
                killed++;
                if (isBoss) bossKilled = true;
                CheckClearedLocked();
            }
        }

        /// <summary>
        /// Returns true for the ONE caller that may tell the player the dungeon is clear, and false for every
        /// later one. Two paths reach the announcement - the last creature's death, and the populate step for
        /// a run whose plan placed nothing and so is Cleared the instant it is populated - and a check-then-act
        /// on the State property would let both through and print the message twice.
        /// </summary>
        public bool TryClaimClearedAnnouncement()
        {
            lock (stateLock)
            {
                if (state != ThreadDungeonRunState.Cleared || clearedAnnounced) return false;
                clearedAnnounced = true;
                return true;
            }
        }

        /// <summary>
        /// Returns true for the ONE caller that may spawn this run's boss cache, and false for every later
        /// one. Modelled directly on <see cref="TryClaimClearedAnnouncement"/> above and latched under the
        /// same lock, for the same check-then-act reason: OnRunCreatureDied runs on a landblock thread, and
        /// a second boss death - a plan that somehow placed two, or a re-entrant Die - would otherwise mint
        /// a second chest full of loot.
        ///
        /// Unlike the clear latch there is NO state gate. A boss can die long before the run reaches
        /// Cleared (the trash share of the weighted clear rule is usually still outstanding), so requiring
        /// Cleared here would suppress the chest in the normal case rather than the exceptional one.
        /// </summary>
        public bool TryClaimBossChest()
        {
            lock (stateLock)
            {
                if (bossChestClaimed) return false;
                bossChestClaimed = true;
                return true;
            }
        }

        /// <summary>
        /// The loot profile and loot-quantity multiplier this run's creatures were actually built with,
        /// stamped ONCE by ThreadDungeonSpawner at the same point it calls <see cref="MarkPopulated"/>.
        ///
        /// It exists so the boss cache cannot drift from the run. DungeonPopulationBuilder computes both
        /// (DungeonPopulationBuilder.cs, the plan.Profile assignment) from the gem's tier, its modifiers and
        /// the gem-level reward-scale ratio, and until now discarded the multiplier and kept the profile only
        /// on the creatures. Recomputing either at boss-death time would re-read every tunable involved and
        /// silently pay a different rate than the run was populated at the moment an admin moved one.
        ///
        /// Write-once, latched like every other transition on this class, so a retried or duplicated populate
        /// step cannot overwrite what the creatures were built from.
        /// </summary>
        /// <param name="salvageAffinities">
        /// See <see cref="SalvageAffinities"/>. Appended as a trailing optional parameter - inserting it before
        /// <paramref name="quantityMult"/> would rebind every existing positional caller onto the wrong
        /// argument. Null or empty is the ordinary case: most gems carry no salvage_affinity modifier at all.
        /// </param>
        public void MarkRewardProfile(ACE.Database.Models.World.TreasureDeath profile, double quantityMult,
            IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> salvageAffinities = null)
        {
            lock (stateLock)
            {
                if (rewardProfileSet) return;
                rewardProfileSet = true;
                lootProfile = profile;
                lootQuantityMult = double.IsNaN(quantityMult) || quantityMult <= 0 ? 1.0 : quantityMult;
                this.salvageAffinities = salvageAffinities;
            }
        }

        /// <summary>The run's own loot profile; null until <see cref="MarkRewardProfile"/> has run.</summary>
        public ACE.Database.Models.World.TreasureDeath LootProfile { get { lock (stateLock) return lootProfile; } }

        /// <summary>The run's own loot-quantity multiplier; 1.0 until <see cref="MarkRewardProfile"/> has run.</summary>
        public double LootQuantityMult { get { lock (stateLock) return lootQuantityMult; } }

        /// <summary>
        /// The salvage affinities the run's creatures were stamped with, carried onto the run by the same
        /// write-once call as <see cref="LootProfile"/> so the boss cache's targeted-salvage bag cannot drift
        /// from what the copy's creatures actually carry. Empty (never null) until
        /// <see cref="MarkRewardProfile"/> has run, and empty again afterwards if the gem carried no
        /// salvage_affinity modifier.
        /// </summary>
        public IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> SalvageAffinities
        {
            get { lock (stateLock) return salvageAffinities ?? Array.Empty<(int, uint, double)>(); }
        }

        /// <summary>
        /// The owner's kill-progress line, recomputed from scratch on every kill (owner ruling R32 as amended
        /// 2026-09-08: "After each kill, the player should see a message 'Monsters killed x/y. Boss still
        /// remaining', where x is number killed and y is the number that must be cleared for completion, not
        /// total in dungeon"). This REPLACES the three latched 25/50/75 percentage milestones the first form
        /// of R32 shipped: there is no latch and no threshold to cross any more, because every kill reports.
        ///
        /// The counts are TRASH counts, not totals, and that is what makes the boss its own clause rather
        /// than a fudge in the denominator. Under R28 a run's progress is weighted: the boss is worth
        /// BossWeight on its own and cannot be traded for any number of trash kills, so a single x/y over
        /// "all creatures" could not describe what is actually left to do. <paramref name="required"/> is
        /// <see cref="ClearTarget"/> - the trash kills needed to clear ASSUMING the boss dies - and
        /// <paramref name="bossRemaining"/> carries the other half of the requirement.
        ///
        /// <paramref name="killed"/> is CLAMPED to <paramref name="required"/>. Trash kills legitimately
        /// exceed the target (all 16 trash dead where 14 sufficed) while the run is still open on the boss,
        /// and "16/14" reads as a bug; "14/14. Boss still remaining." says exactly what is left.
        ///
        /// Returns false unless the run is Active, which is the same single check the milestone form used and
        /// carries the same two silence rules:
        ///   * a run still Starting has no settled Spawned count, so y would be a figure the player then
        ///     watches move under them - the spawner is still placing;
        ///   * a run that has reached Cleared has already earned the clear message, which is the only message
        ///     that kill gets. <see cref="RecordKill"/> runs <see cref="CheckClearedLocked"/> inside its own
        ///     critical section, BEFORE the manager asks for a line, so the clearing kill has already left
        ///     Active by the time this is called. The same check covers an empty plan, Cleared the instant
        ///     MarkPopulated runs.
        /// </summary>
        /// <param name="killed">Trash kills so far, capped at <paramref name="required"/>. 0 when false.</param>
        /// <param name="required">Trash kills still needed to clear once the boss is dead. 0 when false.</param>
        /// <param name="bossRemaining">True while a boss that placed in this run is still alive. False when false.</param>
        public bool TryGetKillProgress(out int killed, out int required, out bool bossRemaining)
        {
            killed = 0;
            required = 0;
            bossRemaining = false;

            lock (stateLock)
            {
                if (state != ThreadDungeonRunState.Active) return false;

                // Puzzle reward scene: once a SEALED run's kills are done, the progress line has nothing left to
                // count - the armed notice (ThreadDungeonManager.AnnounceRewardArmed) said so once, and repeating
                // "x/x" on every later kill reads as a stuck run.
                if (rewardSealed && ClearProgressLocked() >= ClearFraction - 1e-9) return false;

                required = ClearTargetLocked();
                killed = Math.Min(TrashKilledLocked(), required);
                bossRemaining = bossWcid != 0 && !bossKilled;
                return true;
            }
        }

        /// <summary>
        /// Owner ruling R28 (2026-09-06, amends R27): a run clears once its weighted ClearProgress reaches
        /// ClearFraction. The boss alone is worth BossWeight (default 0.2) of that progress - killing only the
        /// boss no longer clears the run outright, and killing only trash caps out at (1 - BossWeight)
        /// (default 0.8) with the boss still alive. An empty population (Spawned 0, no boss) still clears the
        /// instant it is populated, since ClearProgressLocked() is vacuously 1.0 there. Caller must hold
        /// <see cref="stateLock"/>.
        /// </summary>
        private void CheckClearedLocked()
        {
            if (state != ThreadDungeonRunState.Active) return;

            // Puzzle reward scene (ThreadDungeonRun_Puzzle.cs): a sealed run does not clear until the seal lifts,
            // and UnsealReward re-runs this check.
            if (rewardSealed) return;

            if (ClearProgressLocked() >= ClearFraction - 1e-9)
            {
                state = ThreadDungeonRunState.Cleared;

                // Stamped here rather than by the caller: this is the ONE place the transition happens, so a
                // Cleared run always carries the moment it finished. ThreadDungeonManager.ShouldEnd ages the
                // run out from this timestamp, and the manager is a pure function that is handed the elapsed
                // time - it never reads the clock itself.
                clearedUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Moves the run to Ended. Returns true for the ONE caller that made the transition and false for
        /// every later one, which is what makes ThreadDungeonManager.EndRun exactly-once: EndRun destroys
        /// the bound gem and evicts the players, and a check-then-act on the State property would let two
        /// threads through the same gate.
        /// </summary>
        public bool MarkEnded(string reason) => MarkEnded(reason, out _);

        /// <summary>
        /// As <see cref="MarkEnded(string)"/>, and additionally hands back the state the run held on the way
        /// in. The telemetry row's end_state is derived from that value, and it has to come out of the SAME
        /// critical section that overwrites it: reading State first and calling MarkEnded afterwards leaves a
        /// window in which a landblock thread lands the clearing kill, and the run would then be filed as
        /// abandoned when the player had in fact just finished it.
        ///
        /// <paramref name="priorState"/> is meaningful for the winning caller only. A losing caller gets
        /// Ended, which is correct - it made no transition and has nothing to report.
        /// </summary>
        public bool MarkEnded(string reason, out ThreadDungeonRunState priorState)
        {
            lock (stateLock)
            {
                priorState = state;
                if (state == ThreadDungeonRunState.Ended) return false;
                state = ThreadDungeonRunState.Ended;
                endReason = reason;
                return true;
            }
        }

        public override string ToString()
        {
            ThreadDungeonRunState s;
            int tk, ts, ct;
            bool bk, bs;
            double progress, clearFraction;

            lock (stateLock)
            {
                s = state;
                tk = TrashKilledLocked();
                ts = TrashSpawnedLocked();
                ct = ClearTargetLocked();
                bk = bossKilled;
                bs = bossWcid != 0;
                progress = ClearProgressLocked();
                clearFraction = ClearFraction;
            }

            return $"run 0x{RunId:X8} {Dungeon?.Id} owner={OwnerName} state={s} trash={tk}/{ts} (need {ct}) boss={(bk ? "dead" : bs ? "alive" : "none")} progress={progress:P0}/{clearFraction:P0}";
        }
    }
}
