using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Common;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Lifecycle phase of a run (TECH-DESIGN 5.1 - these names are fixed and are read by monitoring).
    /// </summary>
    public enum WorldEventState
    {
        Idle,
        Staged,
        Announced,
        Active,
        Resolved,
        Rewarding,
        Cleanup,
        Done
    }

    /// <summary>
    /// How a run ended (TECH-DESIGN 5.1 - fixed names). Aborted* outcomes pay nothing and skip Rewarding
    /// entirely; Success and every Failed* outcome open a claim window.
    /// </summary>
    public enum WorldEventOutcome
    {
        None,
        Success,

        /// <summary>
        /// The champion goal (family champion or a named boss) never landed in the world - every retry
        /// through <see cref="WorldEvent.BossPlacementGraceSeconds"/> was refused by the physics engine, or
        /// the run had no spawn anchor / no wcid to place at all - and the defenders held the field anyway.
        /// Pays exactly like Success (see <see cref="WorldEvent.PaysSuccessCrate"/>): it opens the claim
        /// window, spawns the caches, and uses the success crate, because nothing about the players' fight
        /// was a failure. Added 2026-08-16 after a stage run's champion never appeared and the run ground on
        /// for nine more minutes structurally unwinnable.
        /// </summary>
        SuccessBossAbsent,

        FailedTimeout,
        FailedWipe,
        FailedNoParticipants,
        AbortedAdmin,
        AbortedShutdown,
        AbortedError
    }

    /// <summary>
    /// One run of a world event: the state machine, the run token, the timers and the single exit path
    /// (TECH-DESIGN 2.2). Everything a later WP hangs off this - the spawner's held objects, the
    /// participation ledger's claim sets, the objective - is either a field here or a seam marked in place.
    ///
    /// Threading: WorldEventManager.Tick and the command handlers both run on the actual world thread.
    /// Creature.Die (arriving via WorldEventManager.OnEventCreatureDied) does not - it runs inside one of
    /// LandblockManager.Tick's Parallel.ForEach worker delegates (LandblockManager.cs:358,420), a
    /// landblock-group thread. What makes every method here safe without a lock is ordering, not a shared
    /// thread: WorldManager.cs:578-585 calls LandblockManager.Tick (which blocks until its Parallel.ForEach
    /// completes) BEFORE WorldEventManager.Tick runs in the same UpdateGameWorld pass, so a run's own tick
    /// never overlaps the death hooks that fed it.
    /// </summary>
    public sealed class WorldEvent
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Timer defaults. Deliberately consts on the event rather than server properties: TECH-DESIGN 5.1
        // fixes the world_events_* property list and these are not on it. WorldEventRequest carries the
        // per-run overrides, which is what the tests and any later tuning use.
        public const int DefaultMaxDurationSeconds = 600;

        /// <summary>
        /// The floor on how long a run lasts (TECH-DESIGN 2.15). Measured from <see cref="ActiveAt"/>.
        ///
        /// A kill-count run used to be over the moment the target was met, which on a big turnout could be
        /// under a minute - the objective resolves against the audience at the FIRST evaluation and never
        /// grows, so twenty people arriving late made an event that was already sized for four evaporate.
        /// Three things hang off this minimum: kill-count completion is deferred until it passes, the wave
        /// cadence stops suppressing waves against the remaining kill count until it passes, and the family
        /// champion arrives at it rather than at a fixed wave index.
        /// </summary>
        public const int DefaultMinDurationSeconds = 300;

        public const int DefaultAbandonAfterSeconds = 90;
        public const int DefaultWipeGraceSeconds = 60;
        public const int DefaultAnnounceLeadSeconds = 30;

        /// <summary>
        /// Default seconds between the teaser broadcast (<see cref="Begin"/>) and <see cref="Stage"/>. Gives
        /// players time to travel to the anchor before the audience sample and the start line, which
        /// deliberately still happen only at <see cref="AnnounceLeadSeconds"/> before Active - the sampler
        /// must measure the crowd the teaser DREW, not the crowd that was there before it.
        /// </summary>
        public const int DefaultTeaserLeadSeconds = 180;

        /// <summary>Upper bound on a per-run or tunable teaser lead, so a bad value cannot hang a run in Idle forever.</summary>
        public const int MaxTeaserLeadSeconds = 3600;

        /// <summary>
        /// How long a champion goal keeps retrying placement past <see cref="championDueAt"/> before the run
        /// gives up and finishes as <see cref="WorldEventOutcome.SuccessBossAbsent"/> (owner decision
        /// 2026-08-16). Placement is retried once per Active tick (~1/s), so this is roughly the retry count
        /// too.
        /// </summary>
        public const int BossPlacementGraceSeconds = 60;

        /// <summary>Fallback when the reward axis carries no usable claim window (rewards.json default is 300).</summary>
        public const int DefaultClaimWindowSeconds = 300;

        public uint RunId { get; }

        public WorldEventState State { get; private set; } = WorldEventState.Idle;

        public WorldEventOutcome Outcome { get; private set; } = WorldEventOutcome.None;

        public WorldEventComposition Composition { get; }

        public WorldEventRequest Request { get; }

        /// <summary>
        /// The goal being pursued. Attached by <see cref="SetObjective"/> immediately after construction in
        /// production (the WP-06 factory needs the event to build one), and may be null only in tests.
        /// </summary>
        public IWorldEventObjective Objective { get; private set; }

        /// <summary>
        /// MVP attribution, the participant count, and the wipe/abandon short-circuit feed (TECH-DESIGN
        /// 2.6, C13). Credited from <see cref="OnCreatureDied"/>; never decides claim eligibility (2.7 is
        /// presence-based).
        /// </summary>
        public WorldEventParticipation Participation { get; }

        /// <summary>
        /// The measured HP-cleared-per-second of this run (C16). Fed from <see cref="OnCreatureDied"/> for
        /// every wave, overflow-champion and objective-source death; never the boss, never decor or npcs.
        /// Read at boss spawn time to size a Named boss when
        /// world_events_boss_throughput_scaling_enabled is on, and reported by "/worldevent status" and the
        /// finish throughput line either way, so the measurement can be compared against the legacy curve
        /// without the flag ever being turned on.
        /// </summary>
        public WorldEventThroughput Throughput { get; } = new WorldEventThroughput();

        /// <summary>
        /// The measured OUTGOING damage controller for this run's named boss (TECH-DESIGN 2.16,
        /// BOSS-STANDARD.md 3.2, flag world_events_boss_damage_scaling_enabled). Fed from
        /// <see cref="OnBossHitPlayer"/> for every hit the boss lands on a counted participant, and read
        /// once per Active tick by <see cref="TickBossDamage"/>. Always present, even for a run with no
        /// boss - it simply never gets a hit.
        /// </summary>
        public WorldEventBossDamageController BossDamage { get; } = new WorldEventBossDamageController();

        /// <summary>
        /// Every object this run spawned - creatures, the rift, the reward caches.
        ///
        /// LOCKING CONVENTION, and every WP must keep it: objects are added from the anchor landblock's
        /// action queue (a landblock tick thread) while cleanup enumerates on the world thread, so THIS
        /// object is the monitor for both this list and <see cref="HeldGuids"/> - take
        /// <c>lock (evt.HeldObjects)</c> around every add and around any enumeration, or use
        /// <see cref="AddHeld"/> / <see cref="HeldSnapshot"/>, which do it for you.
        /// </summary>
        public List<WorldObject> HeldObjects { get; } = new List<WorldObject>();

        /// <summary>
        /// Guids of everything in <see cref="HeldObjects"/>, for the death-hook ownership check. Guarded by
        /// the same monitor as <see cref="HeldObjects"/>.
        ///
        /// This is an EVER-held set and must stay one: nothing removes from it, <see cref="CloseHeld"/>
        /// does not clear it, and neither DestroyAll pass does. Two things depend on that. The generated-add
        /// adoption (<see cref="IsHeld"/>) keys on it, and a run that dropped a destroyed parent would stop
        /// adopting that parent's later children. The cleanup straggler sweep
        /// (<see cref="SweepGeneratedStragglers"/>) keys on it too, and by the time IT runs every parent has
        /// already been queued for destruction - a "still standing" set would match nothing at all.
        /// </summary>
        public HashSet<uint> HeldGuids { get; } = new HashSet<uint>();

        /// <summary>
        /// Places and destroys everything this run puts into the world (TECH-DESIGN 2.4). Constructed with
        /// this run's clock, so its per-wave clear timing (TECH-DESIGN 2.15) is deterministic under test.
        /// </summary>
        public WorldEventSpawner Spawner { get; }

        /// <summary>
        /// The real-time difficulty controller (TECH-DESIGN 2.15). Never null; a theme with no "pace" block
        /// gets one built on the defaults.
        /// </summary>
        public WorldEventPaceController Pace { get; }

        /// <summary>
        /// The landblock the anchor sits in, permaloaded and woken for the duration of the run
        /// (TECH-DESIGN 2.11). Null when the event was staged without a landblock bridge (unit tests) or
        /// when the hold failed, and every spawn path checks it before doing anything.
        /// </summary>
        public Landblock AnchorLandblock { get; private set; }

        // Unix seconds, 0 until set.
        public double TeasedAt;
        public double StagedAt;
        public double ActiveAt;
        public double ResolvedAt;
        public double RewardingAt;
        public double ClaimWindowEndsAt;

        /// <summary>
        /// True once THIS run has actually put a line on the Discord Events feed - the teaser, or the
        /// Active line, or both (2026-09-05). It gates the outcome relay in <see cref="Finish"/>, and it
        /// exists because <see cref="TeasedAt"/> cannot answer the question: Begin stamps TeasedAt before
        /// it knows whether the lead is positive, so a zero-lead run has a non-zero TeasedAt and no teaser.
        ///
        /// What it buys: an abort resolves to "The event at X was cancelled.", and Discord should hear that
        /// ONLY when Discord was told something was coming. A run cancelled before either relaying beat -
        /// most often AbortedShutdown, which fires for every in-flight run on every routine restart - would
        /// otherwise post a cancellation for an event that channel never announced.
        /// </summary>
        private bool relayedToDiscord;

        /// <summary>Last time a creature of this run died. 0 means nobody has ever been credited.</summary>
        public double LastCreditAt;

        /// <summary>
        /// Last time a live participant was seen in the event area. WP-04 feeds this from the participation
        /// ledger; while it stays 0 the wipe rule never fires (TECH-DESIGN 2.2).
        /// </summary>
        public double LastAliveParticipantAt;

        /// <summary>Wave counter. WP-03 increments it as it spawns.</summary>
        public int WaveIndex;

        /// <summary>The audience estimate sampled at Stage.</summary>
        public AudienceEstimate Audience { get; private set; }

        /// <summary>
        /// WP-21 review: <see cref="Audience"/>.Count, safe to read from any thread at any state. Two facts
        /// verified together are what make that true rather than assumed: AudienceEstimate (:39-41 above,
        /// WorldEventRosterSelector.cs) is a non-nullable "public readonly struct", so Audience itself can
        /// never be null and this property can never NRE; and Stage() (:351 below) assigns Audience several
        /// statements AFTER Transition(WorldEventState.Staged, ...) sets State, so a caller (e.g.
        /// WorldEventManager.StatusText, callable off-thread mid-transition, or WorldEventSpawner's
        /// objective-health scaling) that reads Audience.Count while State == Staged sees the struct's
        /// default - Count 0 - rather than a real sample. This property makes that pre-assignment window an
        /// explicit, named 0 instead of leaving it to fall out of the struct default by accident; every
        /// later state (Announced onward) is post-assignment, since Audience is set before the
        /// Announced transition, so this is only ever a real guard during Staged.
        /// </summary>
        public int ResolvedAudienceCount => State == WorldEventState.Staged ? 0 : Audience.Count;

        // Claim gate state lives on the event, so it dies with the run. WP-05 reads and writes these.
        public HashSet<uint> ClaimedCharacters { get; } = new HashSet<uint>();
        public HashSet<uint> ClaimedAccounts { get; } = new HashSet<uint>();
        public HashSet<string> ClaimedIps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Func<AudienceEstimate> audienceSampler;
        private readonly Func<double> clock;
        private readonly IWorldEventLandblockBridge landblockBridge;

        /// <summary>
        /// Test seam for the presence scan (fix/worldevent-abandon-presence-aware), mirroring
        /// <see cref="audienceSampler"/>'s role exactly. Null in production, where
        /// <see cref="CountPlayersPresent"/> goes to <see cref="WorldEventAudienceSampler.CountAliveNear"/>
        /// against the live online-player list; non-null, a test closure can drive the count directly with
        /// no live Player.
        /// </summary>
        private readonly Func<int> presenceCounter;

        /// <summary>
        /// Test seam for the boss placement (D6). Null in production, where IssueChampionPlacement goes to
        /// the spawner and therefore needs a held landblock; non-null, it receives (wcid, healthMult)
        /// instead and stands in for the whole placement, so the WHEN and the WHAT of a boss spawn can be
        /// tested without a Landblock or a Creature.
        ///
        /// Returns whether the placement succeeded, and that return is read SYNCHRONOUSLY: true means
        /// placed on the SAME tick (treated as if bossGuid were set immediately), false means refused on
        /// that tick (not pending). This deliberately does NOT model the live path's asynchrony - the live
        /// WorldEventSpawner.SpawnBoss only ENQUEUES an attempt on the landblock's action queue and resolves
        /// it over one or more LATER ticks via WorldEventSpawner.BossGuid / BossPlacementPending, which is
        /// exactly the distinction the 2026-08-16 fix introduced (see TickChampion's remarks) - but there is
        /// no landblock queue to model in a unit test, so a seam-backed run always resolves an attempt
        /// immediately and DecideChampionTick never sees WaitPending for one.
        /// </summary>
        private readonly Func<uint, double, bool> bossSpawner;

        /// <summary>
        /// Test seam for world_events_boss_throughput_scaling_enabled (C16). Null - production - reads
        /// PropertyManager, wrapped so a shard-config read that throws degrades to "off" rather than
        /// aborting a boss spawn. A unit test has no ShardConfig at all, which is exactly why this exists.
        /// </summary>
        private readonly Func<bool> throughputFlag;

        /// <summary>
        /// Test seam for world_events_teaser_lead_seconds, following <see cref="throughputFlag"/>'s pattern
        /// exactly: null - production - reads PropertyManager, wrapped so a shard-config read that throws
        /// degrades to the compiled default rather than aborting a run.
        /// </summary>
        private readonly Func<int> teaserLeadSource;

        /// <summary>
        /// Test seam for world_event_death_protection (Asheron's Protection, WaffleACE), following
        /// <see cref="throughputFlag"/>'s pattern exactly: null - production - reads PropertyManager, wrapped
        /// so a shard-config read that throws degrades to "off".
        /// </summary>
        private readonly Func<bool> deathProtectionFlag;

        /// <summary>
        /// Test seam for the boss health read <see cref="TickBossHealthMilestones"/> needs (2026-08-29
        /// review fix, D6), mirroring <see cref="bossSpawner"/>'s role for champion PLACEMENT. Null in
        /// production, where the tick reads <see cref="WorldEventSpawner.TryGetBossHealth"/> against a live
        /// Creature via a held landblock (and refuses entirely while <see cref="WorldEventSpawner.BossGuid"/>
        /// is 0); non-null, it stands in for BOTH of those - a test closure can vary (ok, current, max) tick
        /// to tick with no Landblock or Creature at all, which is what lets the milestone latch be driven
        /// through <see cref="Tick"/> rather than only through the pure <see cref="DueBossHealthMilestones"/>
        /// helper directly.
        /// </summary>
        private readonly Func<(bool Ok, uint Current, uint Max)> bossHealthSampler;

        /// <summary>Keys of every landblock the run holds, and therefore the only blocks it may spawn into.</summary>
        private HashSet<ulong> heldBlockKeys = new HashSet<ulong>();

        /// <summary>The subset of the held keys whose Permaload this run is allowed to clear at Finish (R6).</summary>
        private IReadOnlyList<ulong> heldBlockKeysToRelease = new List<ulong>();

        /// <summary>Theme geometry resolved against the anchor at Stage. Empty when there is no anchor.</summary>
        private IReadOnlyList<Position> spawnAnchors = new List<Position>();

        /// <summary>
        /// PhysicsTimer-independent run clock at which the last wave was ATTEMPTED - set whether or not
        /// anything was actually placed, so a full field or a dead-sources early-out cannot make the
        /// cleared-field trigger below re-fire on every tick (WP-18 item 1).
        /// </summary>
        private double lastWaveAt;

        /// <summary>
        /// The index of the last wave attempted, or -1 before wave 0. The cleared-field trigger must never
        /// fire before the opening wave has landed.
        ///
        /// internal, not private (2026-08-29 review fix): SpawnWave never runs without a held landblock
        /// (OnBecameActive's own AnchorLandblock == null guard), which no WorldEvent unit test provides -
        /// see WorldEventProgressAnnouncementTests.Tick_ProgressWaveClause_MatchesTheActualWaveNumber for why
        /// this needs to be settable directly, alongside the already-public WaveIndex, to drive
        /// ProgressDisplayWave's wiring through a real Tick() without a live SpawnWave.
        /// </summary>
        internal int lastWaveIndexSpawned = -1;

        private bool sourcesSpawned;
        private bool championSpawned;

        /// <summary>
        /// Run clock at which the champion first became due (<see cref="ChampionDue"/> first true), 0 until
        /// then. Anchors <see cref="BossPlacementGraceExpired"/>; never reset once set, since a champion goal
        /// is fixed at composition and cannot become due, then not due, then due again.
        /// </summary>
        private double championDueAt;

        /// <summary>
        /// Count of failed champion placement attempts (2026-08-16). Drives the anchor cycled through on
        /// each retry (<see cref="SpawnChampion"/>) and the log-throttle in <see cref="TickChampion"/>.
        /// </summary>
        private int championPlacementAttempts;

        /// <summary>
        /// Whether each wave's trash size was already at its ceiling when it was picked (TECH-DESIGN 2.15).
        /// Read back when that wave clears, and removed then, so this never outgrows the waves in flight.
        /// </summary>
        private readonly Dictionary<int, bool> quantityMaxedByWave = new Dictionary<int, bool>();

        /// <summary>Guarded by the HeldObjects monitor; see <see cref="CloseHeld"/>.</summary>
        private bool heldClosed;

        private bool finishInvoked;
        private bool warned60;
        private bool warned30;

        /// <summary>
        /// Asheron's Protection (WaffleACE): set true the moment <see cref="WorldEventAnnouncer.DeathProtectionStartLine"/>
        /// actually goes out for this run (Stage), so Finish can gate the matching end line on it - toggling
        /// world_event_death_protection mid-run can then never produce an end line with no start line. A
        /// start line with no end line is acceptable only on server shutdown.
        /// </summary>
        private bool deathProtectionStartAnnounced;

        /// <summary>
        /// Run clock at which the last progress announcement fired, or <see cref="ActiveAt"/> if none has
        /// yet (2026-08-29). <see cref="ProgressDue"/> is evaluated against this the same way the wave
        /// cadence is evaluated against lastWaveAt, so the first line fires one full interval after Active,
        /// never at elapsed 0.
        /// </summary>
        private double lastProgressAt;

        /// <summary>Latches each Named-boss health milestone (2026-08-29) so it fires at most once per run.</summary>
        private readonly HashSet<int> bossHealthMilestonesFired = new HashSet<int>();

        /// <summary>
        /// Latches the "presence held this run open" log line (fix/worldevent-abandon-presence-aware) so it
        /// fires at most once per run rather than once per tick for the whole time the field stays occupied.
        /// </summary>
        private bool loggedPresenceHeldOpen;

        /// <param name="objective">May be null at construction; see <see cref="SetObjective"/>.</param>
        /// <param name="audienceSampler">Test seam. Null uses the live online-player scan.</param>
        /// <param name="clock">Test seam. Null uses Time.GetUnixTime.</param>
        /// <param name="landblockBridge">
        /// The bridge to LandblockManager. WorldEventManager.TryStart passes the live one; null means "no
        /// world" (a directly constructed event in a unit test), and the run then skips the landblock hold
        /// and every spawn with a log line rather than reaching into a LandblockManager that has no world
        /// behind it (D6).
        /// </param>
        /// <param name="bossSpawner">Test seam. Null uses the live spawner; see the field's remarks.</param>
        /// <param name="throughputFlag">Test seam. Null reads the server property; see the field's remarks.</param>
        /// <param name="bossHealthSampler">Test seam. Null reads the live Spawner/Creature; see the field's remarks.</param>
        /// <param name="deathProtectionFlag">Test seam. Null reads the server property; see the field's remarks.</param>
        /// <param name="teaserLeadSource">Test seam. Null reads the server property; see the field's remarks.</param>
        /// <param name="presenceCounter">Test seam. Null uses the live online-player scan; see the field's remarks.</param>
        public WorldEvent(uint runId, WorldEventComposition composition, WorldEventRequest request,
            IWorldEventObjective objective, Func<AudienceEstimate> audienceSampler = null, Func<double> clock = null,
            IWorldEventLandblockBridge landblockBridge = null, Func<uint, double, bool> bossSpawner = null,
            Func<bool> throughputFlag = null, Func<(bool Ok, uint Current, uint Max)> bossHealthSampler = null,
            Func<bool> deathProtectionFlag = null, Func<int> teaserLeadSource = null, Func<int> presenceCounter = null)
        {
            RunId = runId;
            Composition = composition;
            Request = request;
            Objective = objective;

            this.audienceSampler = audienceSampler;
            this.clock = clock ?? Time.GetUnixTime;
            this.landblockBridge = landblockBridge;
            this.deathProtectionFlag = deathProtectionFlag;
            this.bossSpawner = bossSpawner;
            this.throughputFlag = throughputFlag;
            this.bossHealthSampler = bossHealthSampler;
            this.teaserLeadSource = teaserLeadSource;
            this.presenceCounter = presenceCounter;

            // Seeded from the composition so the spawner's rebase reads the legacy value even if the boss
            // path never runs - the throughput path overwrites it at spawn time, and nothing else does.
            BossRebaseHealth = composition?.Boss?.BaseHealth ?? 0;

            Participation = new WorldEventParticipation(this.clock);
            Spawner = new WorldEventSpawner(this.clock);
            Pace = new WorldEventPaceController(composition?.Source?.Pace, runId);
        }

        /// <summary>
        /// THE single entry point for adding a spawned object to the held list - creatures from the spawner
        /// and Weave Caches from WorldEventRewardDelivery alike. Takes the HeldObjects monitor, and returns
        /// FALSE once the run has closed its held list (see <see cref="CloseHeld"/>).
        ///
        /// A false return means the caller placed an object the run will never destroy - the spawn was
        /// already in flight on a landblock action queue when the run finished - and the caller must
        /// destroy it rather than leave it in the world with TimeToRot = -1 and nothing to clean it up.
        /// </summary>
        public bool AddHeld(WorldObject wo)
        {
            if (wo == null)
                return false;

            lock (HeldObjects)
            {
                if (heldClosed)
                    return false;

                HeldObjects.Add(wo);
                HeldGuids.Add(wo.Guid.Full);

                return true;
            }
        }

        /// <summary>
        /// Closes the held list: nothing may be adopted after this, because the final cleanup sweep has
        /// already decided what it is destroying. Called once, from CompleteCleanup.
        /// </summary>
        public void CloseHeld()
        {
            lock (HeldObjects)
                heldClosed = true;
        }

        /// <summary>True once the run has stopped accepting held objects.</summary>
        public bool HeldClosed
        {
            get { lock (HeldObjects) return heldClosed; }
        }

        /// <summary>
        /// Guids of the WP-19 reward-cache dressing. Held under the same monitor as
        /// <see cref="HeldObjects"/>, and deliberately a separate set rather than a PropertyBool: the only
        /// existing marker, PropertyBool.WorldEventCache, is what GenericObject.ActOnUse dispatches the
        /// reward claim on (GenericObject.cs:53-54), so flagging a prop with it would let a player claim by
        /// clicking a pile of treasure.
        ///
        /// Membership means one thing: this object shares the CACHE's lifetime, not the run's. The
        /// cache-exempt creature pass at Finish skips it, and DestroyCaches takes it when the claim window
        /// ends.
        /// </summary>
        private readonly HashSet<uint> cacheDecorGuids = new HashSet<uint>();

        /// <summary>Records an object as reward-cache dressing. See <see cref="IsCacheDecor"/>.</summary>
        public void AddCacheDecor(uint guid)
        {
            lock (HeldObjects)
                cacheDecorGuids.Add(guid);
        }

        /// <summary>True when the guid is reward-cache dressing and therefore shares the cache lifetime.</summary>
        public bool IsCacheDecor(uint guid)
        {
            lock (HeldObjects)
                return cacheDecorGuids.Contains(guid);
        }

        /// <summary>A copy of the held list, taken under the HeldObjects monitor, safe to enumerate.</summary>
        public List<WorldObject> HeldSnapshot()
        {
            lock (HeldObjects)
                return new List<WorldObject>(HeldObjects);
        }

        /// <summary>
        /// True when this run has ever held the guid (2026-09-05). The ownership question the
        /// generated-add adoption asks of a GENERATOR: "did this run put you in the world?".
        ///
        /// Deliberately not keyed on Creature.P_WorldEvent, which lives on Creature only - a generator
        /// child can be an item, and those leak the same way. Keyed on the guid, it is also transitive for
        /// free: an adopted add reaches this set through <see cref="AddHeld"/>, so the add's own children
        /// match on the next generation.
        ///
        /// Stays true after <see cref="CloseHeld"/> and after cleanup; see <see cref="HeldGuids"/>.
        /// </summary>
        public bool IsHeld(uint guid)
        {
            lock (HeldObjects)
                return HeldGuids.Contains(guid);
        }

        /// <summary>A copy of <see cref="HeldGuids"/>, safe to mutate and to hand to another thread.</summary>
        public HashSet<uint> HeldGuidsSnapshot()
        {
            lock (HeldObjects)
                return new HashSet<uint>(HeldGuids);
        }

        /// <summary>
        /// True when the given (instance, landblock) key is one this run holds. The spawner's landblock
        /// guard: a candidate position outside the held set is rejected and retried elsewhere.
        /// </summary>
        public bool IsHeldLandblock(ulong key)
        {
            var keys = heldBlockKeys;

            return keys != null && keys.Contains(key);
        }

        /// <summary>
        /// Attaches the objective built by the WP-06 factory, which needs a constructed event to read the
        /// composition from. Refused once the run has left Idle, so an objective can never be swapped
        /// under a live run.
        /// </summary>
        public bool SetObjective(IWorldEventObjective objective)
        {
            if (State != WorldEventState.Idle)
            {
                log.Error($"[WORLDEVENT] run={RunId} refused SetObjective in state {State}");
                return false;
            }

            Objective = objective;
            return true;
        }

        /// <summary>
        /// The run token idiom (TECH-DESIGN 2.2, mirroring Player_WaveChallenge's WaveChallengeRunValid):
        /// every deferred segment captures RunId and bails when this returns false.
        /// </summary>
        public bool RunValid(uint runId) => runId == RunId && State != WorldEventState.Done;

        public string ProgressText => Objective?.ProgressText ?? "";

        /// <summary>Player-facing anchor name, for announcement flavour text.</summary>
        public string AnchorName => Composition?.Anchor?.DisplayName ?? Composition?.Anchor?.Id ?? "somewhere";

        public int AnnounceLeadSeconds => Math.Max(0, Request?.AnnounceLeadSeconds ?? DefaultAnnounceLeadSeconds);

        /// <summary>
        /// Seconds between the teaser broadcast (<see cref="Begin"/>) and <see cref="Stage"/>: the per-run
        /// override when the request set one, otherwise the live world_events_teaser_lead_seconds tunable
        /// (through <see cref="ConfiguredTeaserLead"/>). Clamped to [0, <see cref="MaxTeaserLeadSeconds"/>]
        /// on both ends.
        /// </summary>
        public int TeaserLeadSeconds => Math.Max(0, Math.Min(MaxTeaserLeadSeconds,
            Request?.TeaserLeadSeconds ?? ConfiguredTeaserLead()));

        /// <summary>
        /// world_events_teaser_lead_seconds, through the test seam when one was supplied. A property read
        /// that throws - a unit test has no shard config at all - degrades to the compiled default, which is
        /// the only safe direction for a failure here.
        /// </summary>
        private int ConfiguredTeaserLead()
        {
            if (teaserLeadSource != null)
                return teaserLeadSource();

            try
            {
                return (int)PropertyManager.GetLong("world_events_teaser_lead_seconds").Item;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} could not read world_events_teaser_lead_seconds; using the compiled default", ex);
                return DefaultTeaserLeadSeconds;
            }
        }

        /// <summary>This run's minimum duration in seconds (TECH-DESIGN 2.15), never negative.</summary>
        public int MinDurationSeconds => Math.Max(0, Request?.MinDurationSeconds ?? DefaultMinDurationSeconds);

        /// <summary>
        /// Whether the minimum duration has elapsed (TECH-DESIGN 2.15, D6 - pure). A minimum of 0 or less
        /// is always elapsed, which is what makes the whole feature switchable off per run; a run that is
        /// not Active yet (<paramref name="activeAt"/> 0) never is.
        /// </summary>
        public static bool MinDurationElapsed(double now, double activeAt, int minDurationSeconds)
        {
            if (minDurationSeconds <= 0)
                return true;

            if (activeAt <= 0)
                return false;

            return now >= activeAt + minDurationSeconds;
        }

        /// <summary>
        /// Whether a COMPLETED objective may actually end the run yet (TECH-DESIGN 2.15, D6 - pure).
        ///
        /// kill_count AND destroy_source are both held (owner decision, 2026-08-16): the remaining time is
        /// not dead air, it is the window in which players chase the high score and the global MVP
        /// announcement, so a run that is won early keeps running rather than evaporating. The rift case
        /// carries one extra obligation, discharged in <see cref="SpawnWave"/>: with every rift already
        /// down there would be nothing left to fight, so the "no further waves" early-out is itself held
        /// until the minimum passes.
        ///
        /// kill_boss needs no rule here at all - its champion does not exist until the minimum passes, so
        /// its BossGuid is still 0 and the objective cannot report complete.
        /// </summary>
        public static bool CompletionAllowed(GoalType goal, bool minDurationElapsed)
        {
            return minDurationElapsed || (goal != GoalType.KillCount && goal != GoalType.DestroySource);
        }

        /// <summary>
        /// The start-time duration check (TECH-DESIGN 2.15, D6 - pure). A minimum that meets or exceeds the
        /// maximum would stage a run that can only ever end by timing out, so it is refused rather than
        /// clamped - a clamp would silently hand back a run that is not the one that was asked for.
        /// </summary>
        public static bool ValidateDurations(int minDurationSeconds, int maxDurationSeconds, out string error)
        {
            error = null;

            if (minDurationSeconds < 0)
            {
                error = "--min-duration cannot be negative";
                return false;
            }

            if (maxDurationSeconds < 1)
            {
                error = "the maximum duration must be at least 1 second";
                return false;
            }

            if (minDurationSeconds >= maxDurationSeconds)
            {
                error = $"--min-duration {minDurationSeconds}s must be below the maximum duration {maxDurationSeconds}s";
                return false;
            }

            return true;
        }

        /// <summary>Seconds left on the minimum duration, 0 once it has passed. For the status line.</summary>
        public double MinDurationRemaining(double now)
        {
            if (MinDurationSeconds <= 0)
                return 0;

            if (ActiveAt <= 0)
                return MinDurationSeconds;

            return Math.Max(0, ActiveAt + MinDurationSeconds - now);
        }

        /// <summary>
        /// The crowd-health multiplier from the most recent wave's audience sample (TECH-DESIGN 2.15).
        /// 1.0 until the first wave is picked. Reported by "/worldevent status"; the multiplier a creature
        /// was actually spawned with is this times <see cref="WorldEventPaceController.HealthMult"/>.
        /// </summary>
        public double CrowdHealthMult { get; private set; } = 1.0;

        /// <summary>
        /// The NAMED boss's health multiplier (BOSS-STANDARD.md section 3): clamp(1 + perPlayer *
        /// audience power sum, 1, cap), re-evaluated at every audience re-sample and RAISED ONLY.
        ///
        /// Deliberately not composed with <see cref="CrowdHealthMult"/> or the pace controller's health
        /// step. Those two are tuned for waves - a lot of small things - and a named boss is one thing the
        /// whole server converges on, so it gets a curve of its own that weighs how STRONG the crowd is
        /// rather than how many of it there are. A family champion still takes crowd x pace.
        ///
        /// 1.0 for every composition whose boss is not Named.
        /// </summary>
        public double BossHealthMult { get; private set; } = 1.0;

        /// <summary>
        /// True once a Named boss has been sized from measured throughput rather than the power curve (C16).
        /// Latched at the spawn decision and never cleared: once the boss is standing at a throughput-derived
        /// size, every later re-sample must keep scaling THAT number rather than reverting to the curve.
        /// </summary>
        public bool BossThroughputActive { get; private set; }

        /// <summary>
        /// The aggregate HP-per-second the group had demonstrated at the moment the boss was sized, frozen
        /// there. Later deaths (the boss's own trash, anything still on the field) never move it, so the
        /// boss's size is a function of the WAVE phase, not of the boss fight itself.
        /// </summary>
        public double BossThroughputHps { get; private set; }

        /// <summary>
        /// <see cref="Audience"/>.Count at that same instant, floored at 1. The denominator of the
        /// late-arrival term (WorldEventThroughput.ScaleForAudience).
        /// </summary>
        public int BossThroughputCountAtSpawn { get; private set; }

        /// <summary>
        /// The health the boss is re-based to before its multiplier is applied - read by
        /// WorldEventSpawner.ApplyBaseHealthOverride, which keeps the 0 = "no override" semantics
        /// BossDef.BaseHealth documents.
        ///
        /// bosses.json's baseHealth by default (so the legacy path is byte-identical), and the boss's
        /// throughputFloorHealth on the throughput path - which is what makes the multiplier mean "times the
        /// floor" and lets WorldEventSpawner.RatchetBossHealth recompute every later raise from that floor.
        /// Written once, immediately before the spawn is issued.
        /// </summary>
        public uint BossRebaseHealth { get; private set; }

        /// <summary>
        /// The boss's DamageRating as this run currently believes it to be (TECH-DESIGN 2.16): the AUTHORED
        /// value while nothing has been advised, then whatever the last successfully queued write set. Only
        /// ever moved after <see cref="WorldEventSpawner.ApplyBossDamageRating"/> reports the write was
        /// queued, so it cannot drift from the boss when a write is refused.
        /// </summary>
        public int BossDamageRating { get; private set; }

        /// <summary>
        /// The damage one ordinary hit should do, as of the last evaluation: topMaxHealth / hitsToKill. 0
        /// until a top max health has been sampled. Reported by "/worldevent status".
        /// </summary>
        public double BossDamageTargetHit { get; private set; }

        /// <summary>
        /// The highest Health.MaxValue seen among counted participants in range. Held over when a later
        /// sample finds nobody - an empty sample means "we cannot see the crowd right now", never "the
        /// crowd has no health", and re-sizing the boss to a zero would be the worst possible reading.
        /// </summary>
        public uint BossDamageTopMaxHealth { get; private set; }

        /// <summary>True once the controller has been seeded from the boss's authored rating and the one
        /// spawn-time line has been logged. Latched for the life of the run.</summary>
        private bool bossDamageSeeded;

        /// <summary>Seconds of Active run time at <paramref name="now"/>. 0 before the run goes Active.</summary>
        public double ActiveSeconds(double now)
        {
            if (ActiveAt <= 0 || !double.IsFinite(now))
                return 0;

            var elapsed = now - ActiveAt;

            return elapsed > 0 ? elapsed : 0;
        }

        /// <summary>
        /// The ratchet rule (D6 - pure): a boss health multiplier only ever moves UP. A crowd that thins
        /// out mid-fight must not shrink a health bar players have already been chewing through, so a
        /// smaller candidate is simply discarded rather than applied.
        /// </summary>
        public static double RatchetMult(double current, double candidate)
        {
            if (double.IsNaN(candidate) || double.IsInfinity(candidate))
                return current;

            return candidate > current ? candidate : current;
        }

        /// <summary>
        /// THE single write point for <see cref="Audience"/>. Every re-sample runs the named-boss health
        /// curve against the fresh power sum, raises the multiplier when it has grown, and - if the boss is
        /// already standing - pushes the increase onto it. Never lowers anything.
        ///
        /// The scaling floor (<see cref="WorldEventRosterSelector.WithFloor"/>) is applied HERE and nowhere
        /// else, precisely because this is the single write point: every path that can produce a zero
        /// estimate - a staff-only field, a sampler that threw, an injected sampler in a test, the null
        /// anchor / bad radius early-outs - arrives through this method, so a floor here cannot be bypassed.
        /// A floor pushed down into the sampler would miss the injected and exception paths, and one pushed
        /// into <see cref="WorldEventRosterSelector.EstimateFromLevels"/> would corrupt "/worldevent
        /// simulate", which must still report a genuine zero for an explicitly empty level list.
        ///
        /// The floor is a SCALING input only - participation credit, the reward radius and the reward ledger
        /// all read the real world, not this.
        /// </summary>
        private void SetAudience(AudienceEstimate sampled)
        {
            var estimate = WorldEventRosterSelector.WithFloor(sampled);

            Audience = estimate;

            var boss = Composition?.Boss;

            if (boss == null || boss.Kind != BossKind.Named)
                return;

            var mult = RatchetMult(BossHealthMult, BossCandidateMult(boss, estimate));

            if (!(mult > BossHealthMult))
                return;

            BossHealthMult = mult;

            // BossGuid rather than championSpawned: the flag latches on the failure paths too, and there is
            // nothing standing to ratchet in those cases.
            if (Spawner.BossGuid == 0)
                return;

            try
            {
                Spawner.RatchetBossHealth(this, mult);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} boss health ratchet threw", ex);
            }
        }

        /// <summary>
        /// The multiplier one audience re-sample proposes for a Named boss, before the ratchet sees it.
        ///
        /// On the legacy path that is the power curve, unchanged. On the throughput path (C16) the sizing
        /// rate is FROZEN at spawn, so a re-sample can only move the boss through the crowd term: the frozen
        /// multiplier scaled by (players now / players when it was sized), clamped to the same
        /// throughputCapHealth ceiling the spawn decision used, so a late crowd can never push the boss past
        /// its cap. Both paths still go through <see cref="RatchetMult"/>, which is what makes either one
        /// raise-only.
        /// </summary>
        private double BossCandidateMult(BossDef boss, AudienceEstimate estimate)
        {
            return BossThroughputActive
                ? ThroughputCandidateMult(boss, BossThroughputHps, estimate.Count, BossThroughputCountAtSpawn)
                : boss.ResolveHealthMult(estimate.PowerSum);
        }

        /// <summary>
        /// The throughput half of <see cref="BossCandidateMult"/> (D6 - pure, and public for the tests, the
        /// same shape <see cref="RatchetMult"/> takes): the boss's frozen sizing rate turned back into a
        /// multiplier, scaled by the crowd that is present now against the crowd it was sized for, and
        /// clamped to the same throughputCapHealth ceiling the spawn decision used.
        /// </summary>
        public static double ThroughputCandidateMult(BossDef boss, double frozenHps, int countNow, int countAtSpawn)
        {
            if (boss == null)
                return 1.0;

            var sized = WorldEventThroughput.ResolveBossMult(frozenHps, boss.EffectiveThroughputCalibration,
                boss.EffectiveTargetKillSeconds, boss.EffectiveThroughputFloorHealth, boss.EffectiveThroughputCapHealth);

            var scaled = WorldEventThroughput.ScaleForAudience(sized, countNow, countAtSpawn);

            var ceiling = WorldEventThroughput.CapRatio(boss.EffectiveThroughputFloorHealth, boss.EffectiveThroughputCapHealth);

            return scaled > ceiling ? ceiling : scaled;
        }

        /// <summary>
        /// The entry point TryStart calls. When the teaser lead is positive, broadcasts one teaser line and
        /// leaves the run in Idle; Tick calls Stage() when the lead expires. A lead of 0 stages immediately,
        /// which is the behaviour that predates the teaser.
        /// </summary>
        public void Begin(double now)
        {
            TeasedAt = now;

            var lead = TeaserLeadSeconds;

            if (lead <= 0)
            {
                Stage(now);
                return;
            }

            log.Info($"[WORLDEVENT] run={RunId} teaser lead={lead}s anchor={AnchorName}");

            try
            {
                WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.TeaserLine(Composition?.Source, AnchorName, lead, null), relayToDiscord: true);

                relayedToDiscord = true;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} teaser broadcast failed", ex);
            }
        }

        /// <summary>
        /// Idle -> Staged -> Announced. The audience is sampled here, once, and the announce lead runs from
        /// <see cref="StagedAt"/>; Tick promotes the run to Active when it expires.
        /// </summary>
        public void Stage(double now)
        {
            if (!Transition(WorldEventState.Staged, "stage"))
                return;

            StagedAt = now;

            // The landblock hold runs BEFORE the audience sample on purpose (TECH-DESIGN 2.11): it loads and
            // WAKES the anchor block, so a dormant or unloaded block is live by the time the sampler looks at
            // it. A failed hold aborts the run outright rather than announcing an event nobody can reach.
            if (!HoldLandblocks())
                return;

            ComputeSpawnAnchors();

            SetAudience(SampleAudience());

            log.Info($"[WORLDEVENT] run={RunId} estimate count={Audience.Count} median={Audience.MedianLevel} p90={Audience.P90Level}");

            if (!Transition(WorldEventState.Announced, "announce"))
                return;

            var flavour = Composition?.Source?.StartFlavour;

            if (!string.IsNullOrEmpty(flavour))
                flavour = flavour.Replace("{anchor}", AnchorName);

            log.Info($"[WORLDEVENT] run={RunId} announce lead={AnnounceLeadSeconds}s flavour={flavour ?? "(none)"}");

            WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.StartLine(Composition?.Source, Composition?.FamilyDisplayName, AnchorName));

            // Asheron's Protection (WaffleACE): a second global line, immediately after the ordinary start
            // announcement, only while world events and death protection are both on. Latched so Finish
            // knows whether it is allowed to speak the matching end line.
            if (DeathProtectionEnabled())
            {
                WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.DeathProtectionStartLine);
                deathProtectionStartAnnounced = true;
            }
        }

        /// <summary>
        /// world_events_enabled AND world_event_death_protection, through the test seam when one was
        /// supplied (the seam stands in for BOTH reads at once, following <see cref="ThroughputScalingEnabled"/>'s
        /// pattern). Without a seam, both property reads are wrapped in the SAME try/catch as
        /// <see cref="WorldEventManager.Enabled"/> is not itself exception-safe - a unit test has no shard
        /// config at all, and this must degrade to OFF rather than throw out of Stage().
        /// </summary>
        private bool DeathProtectionEnabled()
        {
            if (deathProtectionFlag != null)
                return deathProtectionFlag();

            try
            {
                return WorldEventManager.Enabled && PropertyManager.GetBool("world_event_death_protection").Item;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} could not read world event death protection properties; treating it as off", ex);

                return false;
            }
        }

        /// <summary>
        /// TECH-DESIGN 2.11 / R4 / R6. Snapshots the prior Permaload state of every loaded landblock BEFORE
        /// taking the hold, permaloads and wakes the anchor (plus its adjacents when the theme asks for
        /// them), and works out which of those blocks this run is allowed to un-permaload at Finish.
        ///
        /// Returns false only when the run was aborted; a skipped hold (no anchor, no bridge) returns true
        /// and simply leaves AnchorLandblock null, which every spawn path already checks.
        /// </summary>
        private bool HoldLandblocks()
        {
            var anchor = Composition?.AnchorPosition;

            if (anchor == null)
            {
                log.Warn($"[WORLDEVENT] run={RunId} has no anchor position; the landblock hold and every spawn are skipped");
                return true;
            }

            if (landblockBridge == null)
            {
                log.Info($"[WORLDEVENT] run={RunId} has no landblock bridge; the landblock hold and every spawn are skipped");
                return true;
            }

            try
            {
                var holdAdjacents = Composition?.Source?.HoldAdjacentLandblocks ?? false;

                var prior = landblockBridge.SnapshotPermaload();

                var landblock = landblockBridge.Hold(anchor, holdAdjacents);

                if (landblock == null)
                {
                    log.Error($"[WORLDEVENT] run={RunId} could not load the anchor landblock at {anchor.ToLOCString()}");
                    Finish(WorldEventOutcome.AbortedError);
                    return false;
                }

                AnchorLandblock = landblock;

                var keys = new List<ulong> { landblock.LongId };

                if (holdAdjacents && landblock.Adjacents != null)
                {
                    foreach (var adjacent in landblock.Adjacents)
                    {
                        if (adjacent != null)
                            keys.Add(adjacent.LongId);
                    }
                }

                heldBlockKeys = new HashSet<ulong>(keys);
                heldBlockKeysToRelease = WorldEventLandblockHold.ComputeRelease(prior, keys);

                log.Info($"[WORLDEVENT] run={RunId} hold blocks={keys.Count} release-at-finish={heldBlockKeysToRelease.Count} anchor=0x{landblock.Id.Raw:X8} instance={landblock.Instance}");

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} landblock hold failed", ex);
                Finish(WorldEventOutcome.AbortedError);
                return false;
            }
        }

        /// <summary>
        /// Resolves the theme's geometry against the anchor once, at Stage. The anchors are reused for every
        /// wave, for the objective spawns and for the champion, so a run's shape stays stable as the
        /// audience estimate moves.
        /// </summary>
        private void ComputeSpawnAnchors()
        {
            var anchor = Composition?.AnchorPosition;
            var theme = Composition?.Source;

            if (anchor == null || theme == null)
            {
                spawnAnchors = new List<Position>();
                return;
            }

            switch (theme.GeometryKind)
            {
                case SourceGeometry.Ring:
                    spawnAnchors = WorldEventGeometry.Ring(anchor, theme.GeometryRadius, theme.GeometryPoints,
                        WorldEventGeometry.DefaultJitterMetres);
                    break;

                case SourceGeometry.Edges:
                    spawnAnchors = WorldEventGeometry.Edges(anchor, theme.GeometryRadius, theme.GeometryPoints,
                        WorldEventGeometry.DefaultJitterMetres);
                    break;

                case SourceGeometry.Disc:
                    // Sampled once here too (WP-16), so spawnAnchors[0] exists for the objective spawns and
                    // the champion, and this 5.2-adjacent log line still reports a real anchor count. The
                    // per-wave RESAMPLE that makes disc different from every other geometry happens in
                    // WaveAnchors, not here - sources and the boss keep using this Stage-time sample.
                    spawnAnchors = WorldEventGeometry.Disc(anchor, theme.GeometryRadius, theme.GeometryPoints);
                    break;

                default:
                    spawnAnchors = WorldEventGeometry.Single(anchor);
                    break;
            }

            log.Info($"[WORLDEVENT] run={RunId} geometry {theme.GeometryKind} radius={theme.GeometryRadius:F1} anchors={spawnAnchors.Count}");
        }

        /// <summary>
        /// The anchors one wave spawns against (WP-16). A disc theme is resampled fresh here every call, so
        /// waves land anywhere under the stack over the run rather than at N fixed points; every other
        /// geometry reuses the Stage-time <see cref="spawnAnchors"/> unchanged, exactly as before this WP.
        /// </summary>
        private IReadOnlyList<Position> WaveAnchors()
        {
            return ResolveWaveAnchors(Composition?.Source, Composition?.AnchorPosition, spawnAnchors, null);
        }

        /// <summary>
        /// The pure rule behind <see cref="WaveAnchors"/> (D6 - no engine object, testable without a
        /// landblock): a disc theme's anchors are resampled fresh from <paramref name="theme"/> and
        /// <paramref name="anchor"/>; every other geometry's staged anchors are handed back unchanged. Kept
        /// static and public so WorldEventStateMachineTests can drive it with a seeded rng.
        /// </summary>
        public static IReadOnlyList<Position> ResolveWaveAnchors(SourceThemeDef theme, Position anchor,
            IReadOnlyList<Position> staged, Random rng)
        {
            if (theme != null && theme.GeometryKind == SourceGeometry.Disc)
                return WorldEventGeometry.Disc(anchor, theme.GeometryRadius, theme.GeometryPoints, rng);

            return staged;
        }

        /// <summary>
        /// One second of run time. Never throws - the manager wraps it too, but a throw from here would
        /// abort a run that might only have a bad objective.
        /// </summary>
        public void Tick(double now)
        {
            switch (State)
            {
                case WorldEventState.Idle:

                    if (TeasedAt > 0 && now >= TeasedAt + TeaserLeadSeconds)
                        Stage(now);

                    break;

                case WorldEventState.Announced:

                    if (now >= StagedAt + AnnounceLeadSeconds)
                    {
                        if (Transition(WorldEventState.Active, "announce lead elapsed"))
                        {
                            ActiveAt = now;

                            // The wipe grace (TECH-DESIGN 2.2) starts counting only once a live participant
                            // has actually been seen, not from the moment the run goes Active - so this is
                            // overwritten by TickActive's feed the instant a credited player is nearby, and
                            // otherwise never advances until the first credit resets it there.
                            LastAliveParticipantAt = ActiveAt;

                            OnBecameActive(now);

                            WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.ActiveLine(
                                Composition?.Source, Composition?.FamilyDisplayName, Composition?.Goal, AnchorName,
                                Composition?.GoalDisplayName), relayToDiscord: true);

                            relayedToDiscord = true;
                        }
                    }

                    break;

                case WorldEventState.Active:

                    TickActive(now);
                    break;

                case WorldEventState.Rewarding:

                    if (ClaimWindowEndsAt > 0 && now >= ClaimWindowEndsAt)
                    {
                        try
                        {
                            WorldEventRewardDelivery.DestroyCaches(this);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[WORLDEVENT] run={RunId} cache destroy threw", ex);
                        }

                        CompleteCleanup(now, "claim window ended");
                    }

                    break;
            }
        }

        /// <summary>
        /// The Announced -> Active edge (TECH-DESIGN 2.4/2.5): the objective spawns go down first, then the
        /// decor and npcs, then wave 0 immediately. Everything here is a no-op without a held landblock.
        ///
        /// The champion is NOT placed here any more (TECH-DESIGN 2.15). Both champion cases - the kill_boss
        /// goal's objective champion, which used to spawn on this edge, and the escalation champion, which
        /// used to spawn at wave index 2 - now arrive together at the minimum-duration mark, from
        /// <see cref="TickChampion"/>.
        /// </summary>
        private void OnBecameActive(double now)
        {
            var theme = Composition?.Source;
            var interval = theme?.EffectiveWaveIntervalSeconds ?? 0;

            lastWaveAt = now;
            lastWaveIndexSpawned = -1;
            lastProgressAt = now;

            if (AnchorLandblock == null)
            {
                log.Info($"[WORLDEVENT] run={RunId} no landblock is held; objective, champion and wave spawns are all skipped");
                return;
            }

            try
            {
                // 2026-08-19: the theme's objectives are FOUGHT only under a DestroySource goal. Under any
                // other goal the identical creatures go down as scenery instead - see ObjectiveSpawnKind.
                var objectiveKind = ObjectiveSpawnKind(Composition?.Goal);

                if (theme != null && theme.Objectives != null && theme.Objectives.Count > 0)
                {
                    // WP-21 fixed-offset objectives REPLACE the anchor-following placement entirely - see
                    // SourceThemeDef.Objectives's remarks.
                    Spawner.SpawnObjectives(this, theme.Objectives, Composition?.AnchorPosition, objectiveKind);
                    sourcesSpawned = SourcesWereSpawned(objectiveKind);
                }
                else if (theme != null && theme.ObjectiveWcid != 0)
                {
                    Spawner.SpawnSources(this, theme.ObjectiveWcid, spawnAnchors, objectiveKind);
                    sourcesSpawned = SourcesWereSpawned(objectiveKind);
                }

                // WP-14 decor, before wave 0 so the scenery is already going up as the first rank lands.
                // Placed once at the geometry CENTRE, not per anchor: a stack of discs is one thing in the
                // sky above the fight, not a ring of them around it.
                IReadOnlyList<DecorDef> decor = theme?.Decor;

                if (decor != null && decor.Count > 0)
                {
                    // WP-25 decor color styles: pick one style uniformly at random and substitute it in
                    // before placement. No seeded rng here - this is the production draw; tests drive
                    // WorldEventSpawner.PickDecorStyle/ApplyDecorStyle directly with a seeded Random.
                    var styles = theme.DecorStyles;

                    if (styles != null && styles.Count > 0)
                    {
                        var style = WorldEventSpawner.PickDecorStyle(styles, null);

                        if (style != null)
                        {
                            decor = WorldEventSpawner.ApplyDecorStyle(decor, style);

                            log.Info($"[WORLDEVENT] run={RunId} decor style [{string.Join(",", style)}] chosen from {styles.Count}");
                        }
                    }

                    Spawner.SpawnDecor(this, decor, Composition?.AnchorPosition);
                }

                // WP-15 spawn-time NPCs, immediately after the decor and for the same reason: the scene the
                // theme describes should be standing before the first rank lands. They are placed around the
                // same geometry CENTRE, each at its own offset, and count toward nothing.
                var npcs = theme?.Npcs;

                if (npcs != null && npcs.Count > 0)
                    Spawner.SpawnNpcs(this, npcs, Composition?.AnchorPosition);

                SpawnWave(now, initial: true);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} initial spawn threw", ex);
            }
        }

        /// <summary>
        /// One wave. <paramref name="initial"/> is wave 0, placed the moment the run goes Active; every
        /// later call advances the wave index first.
        ///
        /// The audience is re-sampled here rather than reused from Stage (PLAN 1.5): a re-sample changes
        /// only the NEXT wave's band and size, never anything already in the world.
        /// </summary>
        private void SpawnWave(double now, bool initial, string trigger = "initial")
        {
            var theme = Composition?.Source;

            // The UNION roster, not Composition.Family: every per-slot draw of a two-family run comes from
            // both families' members at once (TECH-DESIGN 2.3). For a one-family run Roster IS that family's
            // roster, so this path is unchanged there.
            var family = Composition?.Roster;

            if (theme == null || family == null)
                return;

            if (!initial)
                WaveIndex++;

            if (ShouldStopWavesForDeadSources(sourcesSpawned, Spawner.AllSourcesDead,
                    MinDurationElapsed(now, ActiveAt, MinDurationSeconds)))
            {
                log.Info($"[WORLDEVENT] run={RunId} every objective spawn is down; no further waves");
                return;
            }

            SetAudience(SampleAudience());

            // TECH-DESIGN 2.15 item 1: the objective re-sizes against the fresh sample, upwards only. Done
            // before the pick, so this wave is already judged against the target it just produced.
            try
            {
                Objective?.OnAudienceResampled(Audience.Count);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} objective resize threw", ex);
            }

            // TECH-DESIGN 2.15. The pace controller is evaluated HERE, once per pick, against every wave
            // that has finished since the previous pick plus the "oldest wave still standing is stale"
            // signal - so its dials are settled before this wave's size and health are computed, and a wave
            // that cleared while no pick was due is still seen by the next one.
            EvaluatePace(now);

            var alive = Spawner.LiveCount;

            var pick = WorldEventRosterSelector.PickWave(family, Audience, theme, WaveIndex, alive,
                Pace.QuantityBonus, Spawner.OverflowChampionsAlive, null);

            if (pick == null || pick.IsEmpty)
            {
                log.Info($"[WORLDEVENT] run={RunId} wave={WaveIndex} skipped: alive={alive} against maxAlive={theme.EffectiveMaxAlive}");
                return;
            }

            // Read back when this wave clears: a speed-up goes to quantity while quantity still has room,
            // and to health once it does not.
            quantityMaxedByWave[WaveIndex] = pick.Sizing.QuantityMaxed;

            CrowdHealthMult = (theme.CrowdHealth ?? new CrowdHealthDef()).Resolve(Audience.Count);

            var healthMult = CrowdHealthMult * Pace.HealthMult;

            // User directive 2026-08-16: trash is now drawn from a per-slot band sampled from the actual
            // participant levels present, not the audience median, so this is the SPAN of the bands the
            // pick actually drew from (pick.Band) rather than a single median-anchored band. The 5.2 log
            // line's band=lo-hi can therefore be wide for a mixed-level crowd, e.g. "40-317".
            var band = pick.Band;

            // The 5.2 wave line is emitted by the spawner, from inside the landblock closure, once the whole
            // wave has been placed or skipped - so "spawned" is the count that actually entered the world and
            // "alive" is read after the fact. Logging it here would report the count REQUESTED and a predicted
            // alive, both of which overstate reality whenever EnterWorld fails for a creature.
            //
            // WaveAnchors(), not spawnAnchors directly (WP-16): a disc theme resamples fresh anchors for
            // EVERY wave, so monsters land anywhere under the stack rather than at the same N fixed points
            // wave after wave; every other geometry still hands back the Stage-time spawnAnchors unchanged.
            // WP-18 item 1. Beside the fixed 5.2 wave line rather than part of it: that format is what
            // monitoring queries are written against, so the trigger goes on its own line. "cleared" means
            // the field was empty and this wave came early; "interval" means the cadence timer elapsed.
            log.Info($"[WORLDEVENT] run={RunId} wave={WaveIndex} trigger={trigger}");

            // TECH-DESIGN 2.15 difficulty line. Deliberately a SEPARATE line from the fixed 5.2 wave line,
            // which monitoring queries are written against and which must not grow fields.
            log.Info($"[WORLDEVENT] run={RunId} wave={WaveIndex} health=x{healthMult:F2} crowd=x{CrowdHealthMult:F2} " +
                     $"pace=x{Pace.HealthMult:F2} qbonus={Pace.QuantityBonus} champions={pick.Champions.Count}");

            lastWaveIndexSpawned = WaveIndex;

            Spawner.SpawnWave(this, pick, WaveAnchors(), WaveIndex, band, healthMult);

            // Per-wave chatter is local, not server-wide (PLAN 1.10): everyone actually near the fight sees
            // it, without spamming players nowhere near this run.
            WorldEventAnnouncer.Local(this, WorldEventAnnouncer.WaveLine(theme));
        }

        /// <summary>
        /// Feeds the pace controller one pick's worth of evidence (TECH-DESIGN 2.15): every wave that has
        /// cleared since the last pick, tagged with whether ITS pick was already quantity-maxed, plus the
        /// stale-field signal for a wave that is past the window without having cleared at all.
        /// </summary>
        private void EvaluatePace(double now)
        {
            try
            {
                var cleared = Spawner.TakeClearedWaves();

                var observations = new List<PaceObservation>();

                foreach (var wave in cleared)
                {
                    // Absent (a wave whose pick predates this bookkeeping, or one placed by a test) reads as
                    // "not maxed", which sends a speed-up to quantity - the cheaper and more reversible dial.
                    quantityMaxedByWave.TryGetValue(wave.WaveIndex, out var maxed);
                    quantityMaxedByWave.Remove(wave.WaveIndex);

                    observations.Add(new PaceObservation(wave.ClearSeconds, maxed));
                }

                var stale = Spawner.OldestAliveWaveAge(now) > Pace.Tunables.MaxWaveSeconds;

                Pace.Evaluate(observations, stale);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} pace evaluation threw", ex);
            }
        }

        /// <summary>
        /// The champion (TECH-DESIGN 2.15). BOTH champion cases now land here: the kill_boss goal's
        /// objective champion, which used to be placed on the Announced -> Active edge, and the escalation
        /// champion, which used to be placed at wave index 2. Neither is a good rule once a run has a
        /// minimum duration - the first ended the run as soon as somebody killed it, and the second fired
        /// on a wave counter that has nothing to do with elapsed time.
        ///
        /// It is placed at the first Active tick past the minimum, and RETRIED every Active tick after
        /// that (2026-08-16) until it either lands or the run gives up - a stage run once latched a failed
        /// placement permanently and ground on for nine more minutes structurally unwinnable, because a
        /// jittered anchor can be transiently blocked (a wave that just landed on the same spot) without
        /// the run being unwinnable. Two failures are NOT transient - no spawn anchors, or a wcid of 0 - and
        /// those end the run immediately as <see cref="WorldEventOutcome.SuccessBossAbsent"/> rather than
        /// retrying something that can never succeed. Every other failure keeps retrying until
        /// <see cref="BossPlacementGraceExpired"/>, which then ends the run the same way.
        ///
        /// PLACEMENT IS ASYNCHRONOUS (2026-08-16 review fix). <see cref="WorldEventSpawner.SpawnBoss"/> only
        /// constructs the creature and ENQUEUES the actual placement on the anchor landblock's action queue
        /// - it is drained later, on a landblock-group tick, not synchronously here. A first cut of this fix
        /// read <see cref="WorldEventSpawner.BossGuid"/> immediately after the SpawnBoss call and always saw
        /// 0 for the attempt just issued, which made every tick look like a refusal: a brand new creature was
        /// constructed and enqueued every tick, and once the anchor cleared two or more could land (the last
        /// Adopt call silently overwrote the objective's tracked guid). The decision now also reads
        /// <see cref="WorldEventSpawner.BossPlacementPending"/>, so an in-flight attempt is waited out
        /// (<see cref="ChampionTickDecision.WaitPending"/>) rather than reissued, via
        /// <see cref="DecideChampionTick"/> below.
        ///
        /// A NAMED boss (BOSS-STANDARD.md) arrives on exactly the same edge, which is what keeps
        /// KillBossObjective honest: BossGuid stays 0 until the minimum passes, so the objective cannot
        /// complete before then however fast the boss dies afterwards.
        /// </summary>
        private void TickChampion(double now)
        {
            if (championSpawned)
                return;

            var kind = Composition?.Boss?.Kind ?? BossKind.None;
            var minElapsed = MinDurationElapsed(now, ActiveAt, MinDurationSeconds);

            if (!ChampionDue(kind, minElapsed))
                return;

            if (bossSpawner == null && AnchorLandblock == null)
                return; // Skipped - no landblock bridge and no test seam (D6 direct construction).

            if (championDueAt <= 0)
                championDueAt = now;

            // The test seam is synchronous and reports success/failure directly through championSpawned in
            // IssueChampionPlacement rather than through a tracked guid or a pending flag - see the seam
            // field's remarks. It is never "pending" and never carries a real guid, so it always reads as
            // bossGuid 0 / pending false here and resolves through Retry/GiveUp below on every tick until
            // IssueChampionPlacement sets championSpawned itself.
            //
            // Read together via SnapshotBossPlacement (2026-08-16 review fix), not as two separately-locked
            // property reads: BossGuid and BossPlacementPending are set/cleared in two separate lock
            // sections by the producer (Adopt sets bossGuid; the queued delegate's finally clears pending
            // later), so two independent reads here could observe a torn snapshot - bossGuid from before
            // Adopt ran and pending from after the finally cleared it - which looks like "nothing standing,
            // nothing in flight" for an attempt that in fact just landed, on exactly the tick grace expires.
            var (bossGuid, placementPending) = bossSpawner != null
                ? (0u, false)
                : Spawner.SnapshotBossPlacement();

            var graceExpired = BossPlacementGraceExpired(now, championDueAt, BossPlacementGraceSeconds);

            var decision = DecideChampionTick(bossGuid, placementPending, championPlacementAttempts, minElapsed,
                graceExpired, kind);

            switch (decision)
            {
                case ChampionTickDecision.Placed:
                    championSpawned = true;
                    log.Info($"[WORLDEVENT] run={RunId} champion placed guid=0x{bossGuid:X8} kind={kind}");
                    AnnounceBossArrival(kind);
                    break;

                case ChampionTickDecision.WaitPending:
                    // An attempt is already enqueued; the queue drains within a tick or two (every landblock
                    // tick), so nothing to do here but wait for BossGuid or the refusal to show up.
                    break;

                case ChampionTickDecision.GiveUp:
                    log.Warn($"[WORLDEVENT] run={RunId} champion placement grace expired after {championPlacementAttempts} attempts; ending as {WorldEventOutcome.SuccessBossAbsent}");
                    Finish(WorldEventOutcome.SuccessBossAbsent);
                    break;

                case ChampionTickDecision.Retry:
                    try
                    {
                        IssueChampionPlacement();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} champion spawn threw", ex);
                    }

                    break;

                case ChampionTickDecision.NotDue:
                    // Unreachable - ChampionDue already gated entry above; kept so the switch is exhaustive.
                    break;
            }
        }

        /// <summary>
        /// The one-time global "has arrived" line (2026-08-29), fired the instant <see cref="TickChampion"/>
        /// resolves <see cref="ChampionTickDecision.Placed"/>. Named only - a FamilyChampion is a roster pick
        /// with no authored voice, and neither StartLine nor ActiveLine says anything about a boss showing up,
        /// so there is nothing else this would duplicate. Never throws: an announce failure must not be able
        /// to affect champion placement, which has already landed by the time this runs.
        /// </summary>
        private void AnnounceBossArrival(BossKind kind)
        {
            try
            {
                if (kind != BossKind.Named)
                    return;

                var bossName = Composition?.Boss?.DisplayName;
                var line = WorldEventAnnouncer.BossArrivedLine(bossName, AnchorName);

                log.Info($"[WORLDEVENT] run={RunId} announce boss_arrived");
                WorldEventAnnouncer.Broadcast(line);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} boss arrival announce threw", ex);
            }
        }

        /// <summary>
        /// Whether the boss should be placed on this tick (D6 - pure). Both non-None kinds spawn - the
        /// family champion picked from the roster, and the named boss's own wcid - and neither before the
        /// minimum duration has elapsed.
        /// </summary>
        public static bool ChampionDue(BossKind kind, bool minDurationElapsed)
        {
            return kind != BossKind.None && minDurationElapsed;
        }

        /// <summary>
        /// Whether the champion placement retry has run past its grace window (D6 - pure), mirroring
        /// <see cref="MinDurationElapsed"/>'s style. A non-positive grace (or a <paramref name="dueAt"/> of
        /// 0, meaning the champion has never actually become due) never expires.
        /// </summary>
        public static bool BossPlacementGraceExpired(double now, double dueAt, int graceSeconds)
        {
            if (graceSeconds <= 0)
                return false;

            if (dueAt <= 0)
                return false;

            return now >= dueAt + graceSeconds;
        }

        /// <summary>
        /// Whether <paramref name="outcome"/> pays the reward axis's SUCCESS crate rather than the
        /// consolation crate (D6 - pure; see <see cref="WorldEventCacheHandler"/>). SuccessBossAbsent pays
        /// exactly like Success - the champion never showing up is not a defeat for the defenders who held
        /// the field for the whole run.
        /// </summary>
        public static bool PaysSuccessCrate(WorldEventOutcome outcome)
        {
            return outcome == WorldEventOutcome.Success || outcome == WorldEventOutcome.SuccessBossAbsent;
        }

        /// <summary>The five things a single Active tick can conclude about champion placement (2026-08-16).</summary>
        public enum ChampionTickDecision
        {
            /// <summary>No champion goal, or the minimum duration has not elapsed yet.</summary>
            NotDue,

            /// <summary>A boss is standing (<c>bossGuid != 0</c>) - latch <see cref="championSpawned"/>.</summary>
            Placed,

            /// <summary>An attempt is enqueued but not yet resolved - do nothing this tick.</summary>
            WaitPending,

            /// <summary>No attempt in flight and grace has not expired - issue a new one.</summary>
            Retry,

            /// <summary>Grace expired with nothing in flight and nothing standing - end the run.</summary>
            GiveUp
        }

        /// <summary>
        /// The per-tick champion placement decision (D6 - pure, 2026-08-16). Boss placement is asynchronous
        /// - <see cref="WorldEventSpawner.SpawnBoss"/> only ENQUEUES an attempt on the landblock queue, so a
        /// synchronous post-call BossGuid read is worthless (see WorldEventSpawner.BossPlacementPending's
        /// remarks for the defect this caused). Factoring the whole decision into one pure function makes
        /// the truth table - including the two orderings that matter - exhaustively unit testable without a
        /// landblock:
        ///
        ///   * <paramref name="bossGuid"/> != 0 wins over <paramref name="placementPending"/> - a boss that
        ///     is already standing is Placed even if the spawner's pending flag has not yet been cleared by
        ///     its own queued delegate (the two are read from the spawner in the same tick, not atomically);
        ///   * <paramref name="placementPending"/> wins over <paramref name="graceExpired"/> - an attempt
        ///     already in flight is always waited out rather than abandoned, because the queue drains within
        ///     a tick or two (every landblock tick) regardless of how long the grace window has been open.
        /// </summary>
        public static ChampionTickDecision DecideChampionTick(uint bossGuid, bool placementPending,
            int attemptsIssued, bool minElapsed, bool graceExpired, BossKind kind)
        {
            // attemptsIssued does not currently affect the decision - bossGuid/pending/grace are sufficient
            // - but is part of the signature (and the seam) for symmetry with the log/anchor-rotation state
            // the caller keeps alongside it, and so a future attempt-count-based policy has it in hand.
            _ = attemptsIssued;

            if (kind == BossKind.None || !minElapsed)
                return ChampionTickDecision.NotDue;

            if (bossGuid != 0)
                return ChampionTickDecision.Placed;

            if (placementPending)
                return ChampionTickDecision.WaitPending;

            if (graceExpired)
                return ChampionTickDecision.GiveUp;

            return ChampionTickDecision.Retry;
        }

        /// <summary>
        /// Issues ONE champion placement attempt (2026-08-16, called only on
        /// <see cref="ChampionTickDecision.Retry"/>). The two hard failures - no spawn anchors, or a wcid of
        /// 0 - can never resolve themselves mid-run (anchors are computed once at Stage, the family/boss is
        /// fixed at composition) and end the run immediately via <see cref="Finish"/> rather than being
        /// issued as an attempt at all.
        /// </summary>
        private void IssueChampionPlacement()
        {
            if (spawnAnchors == null || spawnAnchors.Count == 0)
            {
                log.Error($"[WORLDEVENT] run={RunId} cannot place a champion: no spawn anchors");
                Finish(WorldEventOutcome.SuccessBossAbsent);
                return;
            }

            var kind = Composition?.Boss?.Kind ?? BossKind.None;

            uint wcid;
            double healthMult;

            // WP-24 follow-up: only the FAMILY champion can be synthetic. A named boss is asked for by id,
            // so it is never a promoted role-0 member.
            var synthetic = false;

            if (kind == BossKind.Named)
            {
                wcid = Composition.Boss.NamedWcid;

                // A named boss scales on the POWER curve and nothing else - not the count-based crowd
                // health and not the pace controller's health step (BOSS-STANDARD.md section 3) - unless
                // the throughput flag and this boss's own floor put it on the measured path instead (C16),
                // which is decided here, once, and also picks the rebase the spawner will apply.
                healthMult = ResolveNamedBossSpawnMult(Composition.Boss);
            }
            else
            {
                var pick = WorldEventRosterSelector.PickChampion(Composition?.Roster, Audience);

                wcid = pick.Wcid;
                synthetic = pick.Synthetic;

                // The champion takes the same composed multiplier a wave spawned at this instant would
                // (TECH-DESIGN 2.15), so it is scaled by the crowd that is actually present when it arrives.
                healthMult = CrowdHealthMult * Pace.HealthMult;
            }

            if (wcid == 0)
            {
                log.Error(kind == BossKind.Named
                    ? $"[WORLDEVENT] run={RunId} boss '{Composition?.Boss?.Id}' carries no wcid"
                    : $"[WORLDEVENT] run={RunId} family '{Composition?.Roster?.Id}' produced no champion " +
                      "in band and had no role-0 member to promote; none placed");

                Finish(WorldEventOutcome.SuccessBossAbsent);
                return;
            }

            // Cycle through every staged anchor on retry (2026-08-16) rather than always [0], so a champion
            // stuck at one blocked anchor gets a shot at the others instead of jittering the same spot.
            var anchor = spawnAnchors[championPlacementAttempts % spawnAnchors.Count];

            championPlacementAttempts++;

            if (championPlacementAttempts == 1 || championPlacementAttempts % 10 == 0)
                log.Warn($"[WORLDEVENT] run={RunId} champion placement attempt n={championPlacementAttempts} issued");
            else
                log.Debug($"[WORLDEVENT] run={RunId} champion placement attempt n={championPlacementAttempts} issued");

            // WP-24 follow-up: a synthetic champion (a role-0 member promoted because the band had no real
            // one) goes through the same rename/health-buff path SpawnWave's overflow champions do - see
            // WorldEventSpawner.ApplySyntheticPromotion.
            if (bossSpawner != null)
            {
                // Test seam (2026-08-16): SYNCHRONOUS, unlike the live path below - true means placed on
                // THIS tick (treat as bossGuid set), false means refused on THIS tick (not pending). There
                // is no landblock queue to model in a unit test, so success is reported directly here
                // rather than through DecideChampionTick's Placed branch on a later tick.
                if (bossSpawner(wcid, healthMult))
                {
                    championSpawned = true;

                    log.Info($"[WORLDEVENT] run={RunId} champion wcid={wcid} synthetic={synthetic} " +
                             $"p90={Audience.P90Level} health=x{healthMult:F2} kind={kind}");

                    // The seam resolves synchronously (see the field's remarks) and therefore never reaches
                    // TickChampion's ChampionTickDecision.Placed branch, which is where the live async path
                    // announces arrival - so this mirrors that call here, or a seam-driven test would never
                    // observe the arrival broadcast a live run always produces on this same edge (2026-08-29
                    // review fix).
                    AnnounceBossArrival(kind);
                }

                return;
            }

            // The live path: SpawnBoss only ENQUEUES the attempt on the anchor landblock's action queue
            // (WorldEventSpawner.Spawn) - success or failure is read back over subsequent ticks via
            // Spawner.BossGuid / Spawner.BossPlacementPending in TickChampion above, never synchronously
            // here (2026-08-16 - the original bug read Spawner.BossGuid immediately after this call and
            // always saw 0 for the attempt just issued).
            Spawner.SpawnBoss(this, wcid, anchor, healthMult, synthetic);
        }

        /// <summary>
        /// THE boss-sizing decision (C16), taken once, at the instant the Named boss placement is issued.
        ///
        /// The throughput path is taken only when all three hold: the flag
        /// world_events_boss_throughput_scaling_enabled is on, this boss carries a non-zero
        /// throughputFloorHealth, and the run has a usable sample (at least minSampleSeconds of Active time
        /// AND something actually cleared). It then FREEZES the measured rate and the audience count, sets
        /// <see cref="BossHealthMult"/> to the sized multiplier - which may be LOWER than the power curve
        /// had it; that is the whole point, and the ratchet only guards a boss that is already standing -
        /// and points <see cref="BossRebaseHealth"/> at the floor, so the spawner rebases to the floor and
        /// every later ratchet recomputes from it.
        ///
        /// Otherwise nothing changes: the power curve's multiplier and bosses.json's baseHealth, exactly as
        /// before the feature existed.
        ///
        /// Never throws: a throw anywhere in the measurement is caught and read as "no sample", which is the
        /// legacy path.
        ///
        /// The mode line is throttled exactly like its sibling "champion placement attempt" line
        /// (<see cref="IssueChampionPlacement"/>): this runs on EVERY issued attempt, and a boss the physics
        /// engine keeps refusing re-issues about once a second for up to
        /// <see cref="BossPlacementGraceSeconds"/>, so only the first attempt and every tenth are Info and
        /// the rest are Debug. Nothing about the decision itself changes with the throttle.
        /// </summary>
        private double ResolveNamedBossSpawnMult(BossDef boss)
        {
            // championPlacementAttempts is still PRE-increment here (IssueChampionPlacement bumps it below
            // this call), so +1 is the attempt number this sizing belongs to - the same number the sibling
            // line prints for the same attempt.
            var attempt = championPlacementAttempts + 1;

            var loud = attempt == 1 || attempt % 10 == 0;

            var elapsed = ActiveSeconds(Now());

            var enabled = ThroughputScalingEnabled();

            var hps = 0.0;
            var cleared = 0.0;
            var hasSample = false;

            try
            {
                hps = Throughput.Throughput(elapsed);
                cleared = Throughput.TotalHealthCleared;
                hasSample = Throughput.HasSample(elapsed, boss.EffectiveMinSampleSeconds);
            }
            catch (Exception ex)
            {
                hasSample = false;

                log.Error($"[WORLDEVENT] run={RunId} boss throughput sample threw; falling back to the power curve", ex);
            }

            // BossThroughputActive is part of the test, not just an output: a placement that is REFUSED is
            // re-issued on the next tick and lands here again, and a mode that flipped back to power between
            // two attempts of the same spawn would leave the multiplier and the rebase describing different
            // bosses. Once a run has sized this boss from measurement, every retry re-sizes it the same way
            // (against the fresher sample - nothing is standing yet, so there is nothing to protect).
            if (BossThroughputActive || (enabled && boss.EffectiveThroughputFloorHealth > 0 && hasSample))
            {
                BossThroughputHps = hps;
                BossThroughputCountAtSpawn = Math.Max(1, Audience.Count);
                BossThroughputActive = true;

                BossHealthMult = WorldEventThroughput.ResolveBossMult(hps, boss.EffectiveThroughputCalibration,
                    boss.EffectiveTargetKillSeconds, boss.EffectiveThroughputFloorHealth, boss.EffectiveThroughputCapHealth);

                BossRebaseHealth = boss.EffectiveThroughputFloorHealth;

                var line = $"[WORLDEVENT] run={RunId} boss sizing mode=throughput hpCleared={cleared:F0} " +
                           $"activeSeconds={elapsed:F0} hps={hps:F1} floor={boss.EffectiveThroughputFloorHealth} " +
                           $"cap={boss.EffectiveThroughputCapHealth} target={boss.EffectiveTargetKillSeconds:F0}s " +
                           $"calibration={boss.EffectiveThroughputCalibration:F2} audience={BossThroughputCountAtSpawn} " +
                           $"mult=x{BossHealthMult:F2}";

                if (loud)
                    log.Info(line);
                else
                    log.Debug(line);
            }
            else
            {
                BossRebaseHealth = boss.BaseHealth;

                var line = $"[WORLDEVENT] run={RunId} boss sizing mode=power mult=x{BossHealthMult:F2} " +
                           $"baseHealth={boss.BaseHealth} flag={enabled} floor={boss.EffectiveThroughputFloorHealth} " +
                           $"sample={hasSample} hpCleared={cleared:F0} activeSeconds={elapsed:F0} hps={hps:F1}";

                if (loud)
                    log.Info(line);
                else
                    log.Debug(line);
            }

            return BossHealthMult;
        }

        /// <summary>
        /// world_events_boss_throughput_scaling_enabled, through the test seam when one was supplied. A
        /// property read that throws - a unit test has no shard config at all - reads as OFF, i.e. the
        /// legacy path, which is the only safe direction for a failure here.
        /// </summary>
        private bool ThroughputScalingEnabled()
        {
            if (throughputFlag != null)
                return throughputFlag();

            try
            {
                return PropertyManager.GetBool("world_events_boss_throughput_scaling_enabled").Item;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} could not read world_events_boss_throughput_scaling_enabled; treating it as off", ex);

                return false;
            }
        }

        // ---- measured boss damage scaling (TECH-DESIGN 2.16) --------------------------------------------

        /// <summary>The shipped default of world_events_boss_damage_scaling_enabled.</summary>
        public const bool DefaultBossDamageScalingEnabled = true;

        /// <summary>
        /// Test seam for the five boss-damage dial properties, following
        /// <see cref="WorldEventRosterSelector.DialSource"/> exactly (this field is never actually null in
        /// production - it is assigned the method group below - but a test may reassign it to a fixed
        /// <see cref="BossDamageDials"/> and must restore it afterward).
        /// </summary>
        internal static Func<BossDamageDials> BossDamageDialSource = ReadBossDamageDials;

        /// <summary>
        /// The raw double read, as its own seam purely so a test can force the failure branch of
        /// <see cref="ReadBossDamageDials"/> deterministically rather than depending on whether the test
        /// host happens to have shard config.
        /// </summary>
        internal static Func<string, double, double> BossDamageDoubleReader =
            (key, fallback) => PropertyManager.GetDouble(key, fallback).Item;

        /// <summary>The raw long read; see <see cref="BossDamageDoubleReader"/>.</summary>
        internal static Func<string, long, long> BossDamageLongReader =
            (key, fallback) => PropertyManager.GetLong(key, fallback).Item;

        /// <summary>
        /// world_events_boss_hits_to_kill / _damage_rating_min / _damage_rating_max / _damage_sample_hits /
        /// _damage_step_cap, through PropertyManager. A read that throws - a unit test has no shard config
        /// at all - reads as the built-in defaults, the same direction WorldEventRosterSelector's band dials
        /// take.
        ///
        /// These are REAL defaults, not the 0-means-JSON override convention the wave/boss dials use: there
        /// is no JSON behind them to fall through to, so a 0 here would mean a literal zero and would be
        /// nonsense for four of the five.
        /// </summary>
        internal static BossDamageDials ReadBossDamageDials()
        {
            try
            {
                return new BossDamageDials(
                    BossDamageDoubleReader("world_events_boss_hits_to_kill", WorldEventBossDamageController.DefaultHitsToKill),
                    (int)BossDamageLongReader("world_events_boss_damage_rating_min", WorldEventBossDamageController.DefaultRatingMin),
                    (int)BossDamageLongReader("world_events_boss_damage_rating_max", WorldEventBossDamageController.DefaultRatingMax),
                    (int)BossDamageLongReader("world_events_boss_damage_sample_hits", WorldEventBossDamageController.DefaultSampleHits),
                    (int)BossDamageLongReader("world_events_boss_damage_step_cap", WorldEventBossDamageController.DefaultStepCap));
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read the boss damage dial properties; falling back to built-in defaults", ex);

                return BossDamageDials.Defaults;
            }
        }

        /// <summary>
        /// world_events_boss_damage_scaling_enabled. A read that throws falls back to the SHIPPED DEFAULT
        /// (true), which is deliberately the opposite direction from
        /// <see cref="ThroughputScalingEnabled"/>'s fallback: throughput sizing is an alternative to a
        /// working power curve, so "off" there is a safe no-op, whereas this feature has no fallback rule
        /// at all - "off" means the boss keeps its authored rating, which is precisely the outcome the
        /// feature exists to prevent.
        /// </summary>
        public bool BossDamageScalingEnabled()
        {
            try
            {
                return PropertyManager.GetBool("world_events_boss_damage_scaling_enabled", DefaultBossDamageScalingEnabled).Item;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} could not read world_events_boss_damage_scaling_enabled; using the shipped default", ex);

                return DefaultBossDamageScalingEnabled;
            }
        }

        /// <summary>
        /// One landed boss hit, forwarded from <see cref="WorldEventBossDamageHook"/>. Called from
        /// landblock-group threads; the controller owns its own monitor.
        /// </summary>
        public void OnBossHitPlayer(int damage, uint defenderMaxHealth, bool crit)
        {
            BossDamage.NoteHit(damage, defenderMaxHealth, crit);
        }

        /// <summary>
        /// The once-per-Active-tick damage decision (TECH-DESIGN 2.16), on the world thread, mirroring the
        /// guards the health ratchet uses: a NAMED boss only, and only while one is actually standing
        /// (<see cref="WorldEventSpawner.BossGuid"/> != 0, the same test <see cref="SetAudience"/> makes
        /// before calling <see cref="WorldEventSpawner.RatchetBossHealth"/>).
        ///
        /// The audience walk is behind the sample gate on purpose. This runs every second, and
        /// WorldEventAudienceSampler.SampleTopMaxHealth walks every online player, so it is only called
        /// once the controller already has a full window of hits to decide on - which for the shipped dials
        /// is a handful of times per boss fight, not once a second.
        ///
        /// Never throws: a damage-scaling failure must not be able to abort a run.
        /// </summary>
        private void TickBossDamage()
        {
            try
            {
                var boss = Composition?.Boss;

                if (boss == null || boss.Kind != BossKind.Named)
                    return;

                if (Spawner.BossGuid == 0)
                    return;

                var enabled = BossDamageScalingEnabled();

                if (!bossDamageSeeded)
                {
                    bossDamageSeeded = true;
                    BossDamageRating = Spawner.BossAuthoredDamageRating;

                    log.Info($"[WORLDEVENT] run={RunId} boss damage scaling={(enabled ? "on" : "off")} rating={BossDamageRating}");
                }

                if (!enabled)
                    return;

                var dials = BossDamageDialSource();

                if (BossDamage.PendingHits < Math.Max(1, dials.SampleHits))
                    return;

                // Before TryAdvise, not after: a true advice consumes the sample window and the one
                // step-cap exemption, so asking for a decision that cannot be written would burn both.
                if (!Spawner.BossDamageWriteAvailable)
                    return;

                var sampled = WorldEventAudienceSampler.SampleTopMaxHealth(Composition?.AnchorPosition,
                    Composition?.Source?.RewardRadius ?? 0f);

                if (sampled > 0)
                    BossDamageTopMaxHealth = sampled;

                var topMaxHealth = BossDamageTopMaxHealth;

                if (topMaxHealth == 0)
                    return;

                var hitsToKill = double.IsFinite(dials.HitsToKill) && dials.HitsToKill > 0
                    ? dials.HitsToKill
                    : WorldEventBossDamageController.DefaultHitsToKill;

                BossDamageTargetHit = topMaxHealth / hitsToKill;

                if (!BossDamage.TryAdvise(BossDamageRating, topMaxHealth, dials, out var newRating, out var diag))
                    return;

                var oldRating = BossDamageRating;

                if (!Spawner.ApplyBossDamageRating(this, newRating))
                    return;

                BossDamageRating = newRating;

                // Every measured field here comes from `diag`, which TryAdvise filled inside its own single
                // lock, so this line is already internally consistent and must NOT be rewritten to read the
                // controller's properties back - that is the tear WorldEventBossDamageController's
                // PendingSnapshot exists to avoid, and by this point the window has been reset anyway.
                log.Info($"[WORLDEVENT] run={RunId} boss damage rating={oldRating}->{newRating} " +
                         $"targetHit={diag.TargetHit:F0} observedMean={diag.ObservedMean:F0} hits={diag.Hits} " +
                         $"crits={diag.Crits} topMaxHealth={topMaxHealth} hitsToKill={hitsToKill:F2}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} boss damage tick threw", ex);
            }
        }

        /// <summary>The shipped default of world_events_boss_health_milestones_enabled.</summary>
        public const bool DefaultBossHealthMilestonesEnabled = true;

        /// <summary>
        /// Test seam for world_events_boss_health_milestones_enabled, following
        /// <see cref="BossDamageDialSource"/>'s pattern: never actually null in production (assigned the
        /// method group below), reassigned by a test and restored afterward.
        /// </summary>
        internal static Func<bool> BossHealthMilestonesEnabledSource = ReadBossHealthMilestonesEnabled;

        /// <summary>
        /// world_events_boss_health_milestones_enabled, through PropertyManager. A read that throws - a unit
        /// test has no shard config at all - reads as the shipped default, exactly like
        /// <see cref="BossDamageScalingEnabled"/>.
        /// </summary>
        internal static bool ReadBossHealthMilestonesEnabled()
        {
            try
            {
                return PropertyManager.GetBool("world_events_boss_health_milestones_enabled",
                    DefaultBossHealthMilestonesEnabled).Item;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read world_events_boss_health_milestones_enabled; using the shipped default", ex);

                return DefaultBossHealthMilestonesEnabled;
            }
        }

        /// <summary>The percent-of-max-health tiers a Named boss announces once each, high to low.</summary>
        public static readonly int[] BossHealthMilestonePercents = { 75, 50, 25 };

        /// <summary>
        /// Whether a milestone tier has been crossed (D6 - pure): current health is at or below
        /// <paramref name="pct"/> percent of <paramref name="max"/>. <paramref name="max"/> 0 (no boss
        /// sampled yet) is never crossed.
        /// </summary>
        public static bool BossHealthMilestoneCrossed(uint current, uint max, int pct)
        {
            if (max == 0)
                return false;

            return current * 100.0 <= (double)max * pct;
        }

        /// <summary>
        /// Which tiers are due on this sample (D6 - pure, and the reason <see cref="TickBossHealthMilestones"/>
        /// is a thin wrapper rather than the whole latch): every entry of <see cref="BossHealthMilestonePercents"/>
        /// that <see cref="BossHealthMilestoneCrossed"/> and is not already in <paramref name="fired"/>.
        /// MUTATES <paramref name="fired"/> by adding each tier it returns, so calling this again with the
        /// SAME set - even against a higher <paramref name="max"/> from a mid-fight
        /// <see cref="WorldEventSpawner.RatchetBossHealth"/> raise, which can push the ratio back above a
        /// tier already announced - can never return that tier a second time. The latch is on the TIER,
        /// never recomputed from the ratio alone.
        /// </summary>
        public static List<int> DueBossHealthMilestones(uint current, uint max, HashSet<int> fired)
        {
            var due = new List<int>();

            foreach (var pct in BossHealthMilestonePercents)
            {
                if (fired.Contains(pct))
                    continue;

                if (!BossHealthMilestoneCrossed(current, max, pct))
                    continue;

                fired.Add(pct);
                due.Add(pct);
            }

            return due;
        }

        /// <summary>
        /// Named-boss health milestones (2026-08-29): a global line the first time the boss's current health
        /// drops to or below 75%, 50%, 25% of its CURRENT max (TryGetBossHealth reads MaxValue fresh every
        /// tick). Never throws.
        /// </summary>
        private void TickBossHealthMilestones()
        {
            try
            {
                var boss = Composition?.Boss;

                if (boss == null || boss.Kind != BossKind.Named)
                    return;

                if (!BossHealthMilestonesEnabledSource())
                    return;

                bool ok;
                uint current;
                uint max;

                if (bossHealthSampler != null)
                {
                    // Test seam (2026-08-29): stands in for BOTH the BossGuid gate and the live read below -
                    // see the field's remarks.
                    (ok, current, max) = bossHealthSampler();
                }
                else
                {
                    if (Spawner.BossGuid == 0)
                        return;

                    ok = Spawner.TryGetBossHealth(out current, out max);
                }

                if (!ok)
                    return;

                foreach (var pct in DueBossHealthMilestones(current, max, bossHealthMilestonesFired))
                {
                    var line = WorldEventAnnouncer.BossHealthMilestoneLine(boss.DisplayName, pct);

                    log.Info($"[WORLDEVENT] run={RunId} announce boss_health {pct}");
                    WorldEventAnnouncer.Broadcast(line);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} boss health milestone tick threw", ex);
            }
        }

        /// <summary>The shipped default of world_events_progress_interval_seconds.</summary>
        public const long DefaultProgressIntervalSeconds = 120;

        /// <summary>Test seam for world_events_progress_interval_seconds; see <see cref="BossHealthMilestonesEnabledSource"/>.</summary>
        internal static Func<long> ProgressIntervalSecondsSource = ReadProgressIntervalSeconds;

        /// <summary>world_events_progress_interval_seconds, through PropertyManager; see <see cref="ReadBossHealthMilestonesEnabled"/>.</summary>
        internal static long ReadProgressIntervalSeconds()
        {
            try
            {
                return PropertyManager.GetLong("world_events_progress_interval_seconds", DefaultProgressIntervalSeconds).Item;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read world_events_progress_interval_seconds; using the shipped default", ex);

                return DefaultProgressIntervalSeconds;
            }
        }

        /// <summary>
        /// Whether a progress line is due (WP-progress, 2026-08-29, D6 - pure): a non-positive
        /// <paramref name="intervalSeconds"/> disables progress entirely (the property's documented 0 =
        /// off), otherwise fires once <paramref name="now"/> reaches <paramref name="lastProgressAt"/> + the
        /// interval - the same cadence shape <see cref="ShouldSpawnNextWave"/>'s interval term uses, which is
        /// what makes the first line land one full interval after Active rather than at elapsed 0
        /// (<see cref="OnBecameActive"/> seeds lastProgressAt to ActiveAt, never to 0).
        /// </summary>
        public static bool ProgressDue(double now, double lastProgressAt, long intervalSeconds)
        {
            if (intervalSeconds <= 0)
                return false;

            return now >= lastProgressAt + intervalSeconds;
        }

        /// <summary>
        /// The 1-based wave number a progress line should display (2026-08-29 review fix, D6 - pure).
        /// <see cref="WaveIndex"/> itself is the wrong source for this: it is 0 both before any wave has
        /// actually spawned AND during the genuine first wave (<see cref="OnBecameActive"/>'s synchronous
        /// "wave 0" placement via <see cref="SpawnWave"/>(initial: true) - the code's own docstring on
        /// SpawnWave calls it that) - so passing it straight into
        /// <see cref="WorldEventAnnouncer.ProgressLine"/> drops the clause entirely during wave 1. And
        /// WaveIndex + 1 is ALSO wrong once any wave has ever been SKIPPED (SpawnWave's `pick == null ||
        /// pick.IsEmpty` early-out): SpawnWave increments WaveIndex on every attempt, landed or not
        /// (`if (!initial) WaveIndex++;` runs BEFORE the pick check), so after one skip WaveIndex permanently
        /// overcounts against how many waves actually stood.
        ///
        /// <paramref name="lastWaveIndexSpawned"/> is the correct source instead, and the ONLY input this
        /// needs: it starts at -1 (set in OnBecameActive) and is written to WaveIndex's CURRENT value only
        /// AFTER a wave actually placed something - so it never advances on a skip, and a run stuck skipping
        /// keeps correctly reporting the last wave that is still actually standing rather than a phantom
        /// later one. -1 means "no wave has spawned yet" - true both for the brief window before
        /// OnBecameActive's initial call resolves and, permanently, for a run whose family/theme is null so
        /// SpawnWave never places anything (every composed run's wave 0 is otherwise unconditional - there is
        /// no goal-kind switch to key off here). 0 (never shown -
        /// <see cref="WorldEventAnnouncer.ProgressLine"/>'s wave &lt;= 0 gate omits the clause) for "nothing
        /// has spawned"; otherwise lastWaveIndexSpawned + 1.
        /// </summary>
        public static int ProgressDisplayWave(int lastWaveIndexSpawned)
        {
            return lastWaveIndexSpawned >= 0 ? lastWaveIndexSpawned + 1 : 0;
        }

        /// <summary>
        /// The periodic Active-phase progress broadcast (owner directive: "Event should continue updating
        /// global messages as it continues"). Called from <see cref="TickActive"/> only on a tick that did
        /// NOT already broadcast a Warn60/Warn30/outcome line - see the caller's remarks. Never throws.
        /// </summary>
        private void TickProgress(double now)
        {
            try
            {
                var interval = ProgressIntervalSecondsSource();

                if (!ProgressDue(now, lastProgressAt, interval))
                    return;

                lastProgressAt = now;

                var maxDuration = Request?.MaxDurationSeconds ?? DefaultMaxDurationSeconds;
                var secondsRemaining = maxDuration > 0
                    ? (int)Math.Max(0, maxDuration - ActiveSeconds(now))
                    : -1;

                var displayWave = ProgressDisplayWave(lastWaveIndexSpawned);

                var line = WorldEventAnnouncer.ProgressLine(Composition?.Goal, Composition?.GoalDisplayName, AnchorName,
                    ProgressText, displayWave, secondsRemaining);

                log.Info($"[WORLDEVENT] run={RunId} announce progress");
                WorldEventAnnouncer.Broadcast(line);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} progress announce threw", ex);
            }
        }

        /// <summary>
        /// One death's contribution to the measured rate (C16). Wave trash, overflow champions and the
        /// objective sources count; the boss, decor and npcs never do
        /// (<see cref="WorldEventThroughput.Counts"/>). The HP is the creature's Health.MaxValue AT DEATH,
        /// so it already carries whatever crowd/pace/objective scaling it was spawned with - which is what
        /// the group actually had to remove.
        /// </summary>
        private void NoteThroughput(Creature creature)
        {
            if (creature == null)
                return;

            var guid = creature.Guid.Full;

            if (!WorldEventThroughput.Counts(guid, Spawner.BossGuid, Spawner.IsDecorGuid(guid), Spawner.IsNpcGuid(guid)))
                return;

            Throughput.NoteCleared(creature.Health.MaxValue);
        }

        /// <summary>
        /// The shortest gap the cleared-field trigger may produce (WP-18 item 1). Without it, killing the
        /// last straggler of a wave that only just landed would chain a second wave inside the same tick.
        /// The interval path is unaffected - this floors the EARLY path only.
        /// </summary>
        public const double MinWaveGapSeconds = 3.0;

        /// <summary>
        /// Whether a theme whose objective spawns are all down should stop producing waves (D6 - pure).
        ///
        /// The rift theme's pressure IS the rifts: once they are all down the run is decided, and another
        /// wave would only be something to fight after the objective is already satisfied. That reasoning
        /// inverts while the minimum duration is still holding the run open (TECH-DESIGN 2.15):
        /// destroy_source completion is now deferred exactly like kill_count, so between the last rift
        /// falling and the minimum expiring there IS a window to fill, and filling it is the whole point -
        /// that window is where the high score and the MVP announcement are decided.
        ///
        /// After the minimum the original early-out applies again, unchanged. A theme that never placed an
        /// objective spawn (<paramref name="sourcesSpawned"/> false) is unaffected either way.
        /// </summary>
        public static bool ShouldStopWavesForDeadSources(bool sourcesSpawned, bool allSourcesDead,
            bool minDurationElapsed)
        {
            return sourcesSpawned && allSourcesDead && minDurationElapsed;
        }

        /// <summary>
        /// Which spawn kind a theme's objective spawns go down as, given the run's goal (2026-08-19, D6 -
        /// pure). DestroySource is the only goal that FIGHTS them, so it is the only one that gets
        /// <see cref="WorldEventSpawnKind.Source"/>; every other goal - and a null/unresolved goal - gets
        /// <see cref="WorldEventSpawnKind.InertObjective"/>, which places the identical creatures as
        /// unattackable, un-labelled scenery.
        ///
        /// This is what lets one source theme serve several goals. The five element_portal_* themes accept
        /// destroy_source, kill_count and kill_boss: under destroy_source the two pillars ARE the objective,
        /// and under the other two they are the set dressing that makes the portal read as a portal.
        /// </summary>
        public static WorldEventSpawnKind ObjectiveSpawnKind(GoalDef goal)
        {
            return goal != null && goal.TypeKind == GoalType.DestroySource
                ? WorldEventSpawnKind.Source
                : WorldEventSpawnKind.InertObjective;
        }

        /// <summary>
        /// Whether a placement of <paramref name="kind"/> counts as having spawned SOURCES (D6 - pure), i.e.
        /// whether <see cref="ShouldStopWavesForDeadSources"/> may ever fire for this run. Only a real Source
        /// does: an inert objective can never die, so treating it as a source would arm a wave cut-off whose
        /// condition (all sources dead) can never be met - harmless in itself, but it would also put a guid
        /// into sourceGuids that DestroySourceObjective would wait on forever if the goal were ever changed.
        /// </summary>
        public static bool SourcesWereSpawned(WorldEventSpawnKind kind)
        {
            return kind == WorldEventSpawnKind.Source;
        }

        /// <summary>
        /// Whether the next wave should go now (WP-18 item 1, D6 - pure). Two ways to say yes:
        ///
        ///   * the cadence timer elapsed (<paramref name="now"/> is at least <paramref name="interval"/>
        ///     past <paramref name="lastWaveAt"/>) - the pre-WP-18 behaviour, unchanged;
        ///   * the field is CLEAR (<paramref name="liveCount"/> 0) and at least
        ///     <paramref name="minGap"/> has passed since the last wave.
        ///
        /// Everything else is a refusal, and the guards are deliberately all here rather than spread over
        /// the caller: the run must be Active, the objective must still be open, the opening wave must
        /// already have been attempted (<paramref name="waveIndexSpawned"/> is -1 before that), and an
        /// interval of 0 or less disables the cadence entirely as it always did.
        ///
        /// WP-20 adds one more refusal ahead of both yes-paths: a non-null <paramref name="remainingKills"/>
        /// that <paramref name="liveCount"/> already meets or exceeds. Null leaves every other term exactly
        /// as it was.
        /// </summary>
        public static bool ShouldSpawnNextWave(bool active, bool objectiveComplete, int waveIndexSpawned,
            int liveCount, double now, double lastWaveAt, double interval, double minGap, int? remainingKills)
        {
            if (!active || objectiveComplete || interval <= 0)
                return false;

            // Wave 0 is placed by OnBecameActive, not by the cadence.
            if (waveIndexSpawned < 0)
                return false;

            // WP-20: never spawn a wave nobody needs to kill. When the objective reports how many more
            // event-creature deaths it needs, and the creatures ALREADY on the field can supply them, this
            // wave would be pure churn - it lands, the player kills the one that finishes the objective,
            // and DestroyEventCreatures deletes the rest in front of them. Null is "no opinion" and never
            // suppresses: destroy_source and kill_boss do not advance on trash deaths at all.
            if (remainingKills != null && liveCount >= remainingKills.Value)
                return false;

            if (now >= lastWaveAt + interval)
                return true;

            if (liveCount > 0)
                return false;

            return now >= lastWaveAt + minGap;
        }

        /// <summary>
        /// Which of the two rules above produced this wave (WP-18 item 1, D6 - pure), for the log line.
        /// Interval wins when both are true, because the cadence would have fired anyway.
        /// </summary>
        public static string NextWaveTrigger(double now, double lastWaveAt, double interval)
        {
            return now >= lastWaveAt + interval ? "interval" : "cleared";
        }

        /// <summary>
        /// The wave cadence. Runs ahead of the objective and timer evaluation so a wave that completes the
        /// objective is judged on the same tick it lands.
        /// </summary>
        private void TickWaves(double now)
        {
            if (AnchorLandblock == null)
                return;

            var interval = Composition?.Source?.EffectiveWaveIntervalSeconds ?? 0;

            // WP-18 item 1: a wave now arrives either because the interval elapsed OR because the field is
            // clear, so a group that wipes a wave in 10 s does not stand around for the remaining 30 s.
            // Spawner.LiveCount is wave trash ONLY and is the right thing to test here: Adopt files a spawn
            // into liveWave exclusively when CountsAsWavePressure(kind) is true, i.e. kind == Wave; decor
            // goes to liveDecor, npcs to liveNpcs, and objective/champion spawns to liveObjective
            // (WorldEventSpawner.cs LiveCount / Adopt / CountsAsWavePressure). So a rift still standing or
            // the Sky Rift discs overhead can never masquerade as "the field is clear".
            var minElapsed = MinDurationElapsed(now, ActiveAt, MinDurationSeconds);

            var active = State == WorldEventState.Active;

            // The same gate the Finish path takes (TECH-DESIGN 2.15): a kill-count objective that has met
            // its target before the minimum is not "complete" for cadence purposes either, or the run would
            // stop producing waves and then stand idle until the minimum expired.
            var complete = (Objective?.IsComplete ?? false)
                && CompletionAllowed(Composition?.Goal?.TypeKind ?? GoalType.KillCount, minElapsed);

            var alive = Spawner.LiveCount;

            // TECH-DESIGN 2.15: before the minimum, WP-20's suppression is switched off entirely. Killing
            // past the target is fine and expected - overshoot costs nothing - but a run that stops
            // spawning at minute one and has four more minutes to serve is an empty field, which is the
            // whole failure this minimum exists to prevent.
            var remaining = minElapsed ? Objective?.RemainingKills : null;

            if (!ShouldSpawnNextWave(active, complete, lastWaveIndexSpawned, alive, now, lastWaveAt,
                    interval, MinWaveGapSeconds, remaining))
            {
                // WP-20: distinguish "the cadence was not due" from "a due wave was suppressed because the
                // field already satisfies the objective". Only the latter is worth a line, and it is worth
                // one every time it happens - the repo owner reads these logs, and a silently skipped wave
                // is worse than a spurious one.
                var wouldHaveSpawned = ShouldSpawnNextWave(active, complete, lastWaveIndexSpawned, alive,
                    now, lastWaveAt, interval, MinWaveGapSeconds, remainingKills: null);

                if (wouldHaveSpawned)
                {
                    log.Info($"[WORLDEVENT] run={RunId} wave={WaveIndex + 1} skipped: alive={alive} satisfies remaining={remaining}");

                    // The slot is consumed, so this reports once per skipped WAVE rather than once per
                    // tick, and the next wave is judged from here rather than piling up a backlog.
                    lastWaveAt = now;
                }

                return;
            }

            var trigger = NextWaveTrigger(now, lastWaveAt, interval);

            try
            {
                SpawnWave(now, initial: false, trigger: trigger);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} wave spawn threw", ex);
            }

            // Stamped whether or not anything was placed, so neither a full field nor a dead-sources
            // early-out queues up a backlog of waves, and the cleared-field trigger cannot re-fire every
            // tick when SpawnWave declined to place anything.
            lastWaveAt = now;
        }

        private void TickActive(double now)
        {
            // Champion BEFORE waves (2026-08-16): a champion due on the same tick as a wave claims the
            // ground first, rather than landing into a spot 16 wave mobs just filled - a stage run latched a
            // placement failure at exactly that collision. The objective is still judged after both, below.
            TickChampion(now);

            TickWaves(now);

            TickBossDamage();

            TickBossHealthMilestones();

            try
            {
                Objective?.Tick(now);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} objective tick threw", ex);
            }

            // TECH-DESIGN 2.15: true once the objective has been MET but the minimum duration is still
            // holding the run open. It is the only way execution reaches the timers below with a satisfied
            // objective, and it is what disarms the two failure verdicts that would otherwise take the run
            // away from players who have already earned it.
            var heldOpen = false;

            if (Objective != null && Objective.IsComplete)
            {
                var minElapsed = MinDurationElapsed(now, ActiveAt, MinDurationSeconds);

                if (CompletionAllowed(Composition?.Goal?.TypeKind ?? GoalType.KillCount, minElapsed))
                {
                    Finish(WorldEventOutcome.Success);
                    return;
                }

                // Held open (TECH-DESIGN 2.15). Kills keep counting past the target and waves keep coming;
                // the run finishes on the first tick past the minimum. Not logged per tick - the status
                // line reports the remaining time, and this state can last minutes.
                heldOpen = true;
            }

            // Computed once per tick (fix/worldevent-abandon-presence-aware) - the world-event tick is
            // already throttled to 1 Hz by WorldEventManager.Tick, so one online-player walk per tick here
            // is fine and feeds both the wipe timer below and PlayersPresent in the timer inputs.
            var presence = CountPlayersPresent();

            // Not fed while the run is held open: LastAliveParticipantAt is the wipe rule's ONLY input, and
            // leaving it to go stale is exactly what would fire FailedWipe below. Skipping the feed rather
            // than only the verdict also means the wipe clock resumes from where the hold left it if a run
            // somehow leaves the held state.
            if (!heldOpen)
                FeedWipeTimer(now, presence);

            var inputs = new WorldEventTimers.TimerInputs
            {
                Now = now,
                ActiveAt = ActiveAt,
                LastCreditAt = LastCreditAt,
                MaxDurationSeconds = Request?.MaxDurationSeconds ?? DefaultMaxDurationSeconds,

                // Both zeroed while the run is held open (TECH-DESIGN 2.15), which is how
                // WorldEventTimers.Evaluate is told to skip a rule. An objective that has already been MET
                // must not then be FAILED because the field went quiet during the minimum it is serving -
                // and a group that finishes early and stands around is precisely the case that produces no
                // further kill credit and no live participant scan hit.
                //
                // FailedTimeout is deliberately NOT disarmed: it is the absolute backstop against a run
                // that never ends. With a minimum below the maximum (ValidateDurations enforces it) the
                // hold always releases into Success first, so it cannot actually fire here.
                AbandonAfterSeconds = heldOpen ? 0 : (Request?.AbandonAfterSeconds ?? DefaultAbandonAfterSeconds),
                WipeGraceSeconds = heldOpen ? 0 : (Request?.WipeGraceSeconds ?? DefaultWipeGraceSeconds),
                AnyoneEverCredited = LastCreditAt > 0,
                LastAliveParticipantAt = LastAliveParticipantAt,
                PlayersPresent = presence > 0
            };

            // Logged once per run (fix/worldevent-abandon-presence-aware), the first tick presence holds
            // the run open at a point it would previously have been abandoned - mirrors the exact condition
            // WorldEventTimers.Evaluate's FailedNoParticipants branches check, just inverted on PlayersPresent.
            if (!loggedPresenceHeldOpen && inputs.PlayersPresent && inputs.AbandonAfterSeconds > 0)
            {
                var elapsed = now - ActiveAt;

                var wouldHaveAbandoned = (!inputs.AnyoneEverCredited && elapsed >= inputs.AbandonAfterSeconds)
                    || (inputs.AnyoneEverCredited && now - inputs.LastCreditAt >= inputs.AbandonAfterSeconds);

                if (wouldHaveAbandoned)
                {
                    loggedPresenceHeldOpen = true;
                    log.Info($"[WORLDEVENT] run={RunId} abandon window held open by {presence} player(s) present");
                }
            }

            var verdict = WorldEventTimers.Evaluate(in inputs, warned60, warned30);

            switch (verdict)
            {
                case TimerVerdict.FailedTimeout:
                    Finish(WorldEventOutcome.FailedTimeout);
                    break;

                case TimerVerdict.FailedWipe:
                    Finish(WorldEventOutcome.FailedWipe);
                    break;

                case TimerVerdict.FailedNoParticipants:
                    Finish(WorldEventOutcome.FailedNoParticipants);
                    break;

                case TimerVerdict.Warn60:
                    warned60 = true;
                    log.Info($"[WORLDEVENT] run={RunId} warning 60 seconds remaining");
                    WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.WarnLine(60));
                    break;

                case TimerVerdict.Warn30:
                    // Warn30 implies the 60 s mark is spent, whether or not it was ever announced -
                    // otherwise the next tick would announce "60 seconds remaining" after "30".
                    warned30 = true;
                    warned60 = true;
                    log.Info($"[WORLDEVENT] run={RunId} warning 30 seconds remaining");
                    WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.WarnLine(30));
                    break;
            }

            // Progress announcements (owner directive, 2026-08-29): only on a tick that did not already
            // broadcast a Warn60/Warn30/terminal line above, and only while still Active - a
            // FailedTimeout/FailedWipe/FailedNoParticipants verdict calls Finish() (which transitions State
            // away from Active) but falls through the switch rather than returning, so the State check below
            // is what actually stops a progress line landing on the same tick a run just ended.
            if (verdict == TimerVerdict.None && State == WorldEventState.Active)
                TickProgress(now);
        }

        /// <summary>
        /// TECH-DESIGN 2.2/2.6 wipe feed: once per tick, if a credited participant is currently alive within
        /// the theme's reward radius of the anchor, OR (fix/worldevent-abandon-presence-aware) any non-staff
        /// living player is present near the anchor regardless of credit, <see cref="LastAliveParticipantAt"/>
        /// advances to now. The presence half closes the same defect the abandon rule had: a fresh group
        /// arriving after the original defenders died would otherwise still lose the run to FailedWipe even
        /// though the field is occupied. WorldEventTimers.Evaluate only ever fires FailedWipe once this stops
        /// advancing for WipeGraceSeconds - see the class remarks on LastAliveParticipantAt for why it starts
        /// at ActiveAt rather than 0.
        /// </summary>
        private void FeedWipeTimer(double now, int presence)
        {
            var anchor = Composition?.AnchorPosition;
            var radius = Composition?.Source?.RewardRadius ?? 0f;

            if (anchor == null || radius <= 0)
                return;

            if (presence > 0 || (Participation.Count > 0 && Participation.AliveNear(anchor, radius) > 0))
                LastAliveParticipantAt = now;
        }

        /// <summary>
        /// Called from Creature.Die (via WorldEventManager.OnEventCreatureDied, wired by WP-03) for a
        /// creature this run owns.
        /// </summary>
        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            LastCreditAt = Now();

            // Before Spawner.OnDied, purely so the classification reads the spawner's sets exactly as they
            // were when this creature was alive. Wrapped like every other forward here: a throw measuring
            // throughput must never cost the run its death bookkeeping (C16).
            try
            {
                NoteThroughput(creature);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} throughput credit threw", ex);
            }

            // Before the objective forward: the objective may complete on this death, and the alive count
            // and the source-guid bookkeeping have to reflect it by then.
            Spawner.OnDied(creature);

            // Also before the objective forward, so a WP-06 objective's Mvp() rule can read an
            // already-updated ledger on the same death that completes it.
            try
            {
                Participation.CreditDamage(creature);
                Participation.CreditKill(lastDamager);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} participation credit threw", ex);
            }

            try
            {
                Objective?.OnCreatureDied(creature, lastDamager, topDamager);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} objective OnCreatureDied threw", ex);
            }

            log.Info($"[WORLDEVENT] run={RunId} death wcid={creature?.WeenieClassId ?? 0} guid=0x{(creature?.Guid.Full ?? 0):X8} lastDamager={lastDamager?.Name ?? "none"} topDamager={topDamager?.Name ?? "none"}");
        }

        /// <summary>
        /// The single exit path (TECH-DESIGN 2.2). Idempotent: the second call is a no-op.
        ///
        /// Steps:
        ///   1. already Done -> return;
        ///   2. record the outcome, move to Resolved;
        ///   3. announce the outcome (try/catch);
        ///   4. Success and every Failed* move to Rewarding, spawn the caches (try/catch) and open the
        ///      claim window - Tick finishes the run when the window closes. Aborted* skip this entirely;
        ///   5. move to Cleanup and destroy every held object;
        ///   6. move to Done and write the finish line.
        ///
        /// Steps 5 and 6 run even when 3 or 4 throw, which is the whole point of the try/catch placement.
        /// </summary>
        public void Finish(WorldEventOutcome outcome)
        {
            var now = Now();

            if (State == WorldEventState.Done)
                return;

            if (finishInvoked)
            {
                // An abort raised DURING the claim window cuts it short rather than being ignored -
                // otherwise a shutdown mid-window would leave every spawned object behind. Any other
                // repeat call is a plain no-op, so a run can only produce one transition set.
                if (State == WorldEventState.Rewarding && IsAborted(outcome))
                {
                    Outcome = outcome;

                    log.Info($"[WORLDEVENT] run={RunId} claim window cut short by {outcome}");

                    try
                    {
                        WorldEventRewardDelivery.DestroyCaches(this);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} cache destroy threw", ex);
                    }

                    CompleteCleanup(now, $"finish:{outcome}");
                }
                else
                    log.Debug($"[WORLDEVENT] run={RunId} finish({outcome}) ignored - already finishing (state={State}, outcome={Outcome})");

                return;
            }

            finishInvoked = true;
            Outcome = outcome;

            if (Transition(WorldEventState.Resolved, $"finish:{outcome}"))
                ResolvedAt = now;

            LogThroughputLine(now);

            try
            {
                log.Info($"[WORLDEVENT] run={RunId} resolved outcome={outcome} progress={(string.IsNullOrEmpty(ProgressText) ? "n/a" : ProgressText)}");

                // WP-06 has not landed an objective implementation yet in this wave, so Objective?.Mvp()
                // is WorldEventMvp.None until then - the composer omits the MVP sentence in that case.
                var mvp = Objective?.Mvp() ?? WorldEventMvp.None;

                // 2026-08-16, SuccessBossAbsent's "never showed" line: a real Named boss speaks its own
                // authored name, everything else (no boss, or the unnamed family champion) falls back to
                // the generic "the champion".
                var boss = Composition?.Boss;
                var bossName = boss != null && boss.Kind != BossKind.None && !string.IsNullOrWhiteSpace(boss.DisplayName)
                    ? boss.DisplayName
                    : "the champion";

                WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.OutcomeLine(
                    outcome, Composition?.Goal, AnchorName, mvp, Participation.TopKiller(), Participation.Count, bossName,
                    Composition?.GoalDisplayName, Composition?.SuccessTemplate, Composition?.FailTemplate,
                    Composition?.ObjectiveNoun, Composition?.ObjectiveNounPlural),
                    relayToDiscord: relayedToDiscord);

                // Asheron's Protection (WaffleACE): the matching end line, immediately after the ordinary
                // outcome announcement, ONLY if the start line actually went out for this run - never on the
                // property being toggled off mid-run with no corresponding start.
                if (deathProtectionStartAnnounced)
                    WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.DeathProtectionEndLine);

                // BOSS-STANDARD.md section 2: a named boss that WON gets the last word, as a second
                // global line right after the outcome. Nothing is said on a Success (the boss lost) and
                // nothing on an abort (in the fiction nothing happened, so there is nothing to gloat
                // about), and a family champion has no authored voice at all.
                if (BossWins(outcome))
                {
                    var failLine = WorldEventAnnouncer.BossFailLine(Composition?.Boss, null);

                    if (!string.IsNullOrEmpty(failLine))
                        WorldEventAnnouncer.Broadcast(failLine);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} outcome announcement threw", ex);
            }

            if (!IsAborted(outcome) && Transition(WorldEventState.Rewarding, $"reward:{outcome}"))
            {
                RewardingAt = now;

                var window = Composition?.Reward?.ClaimWindowSeconds ?? DefaultClaimWindowSeconds;

                if (window < 1)
                    window = DefaultClaimWindowSeconds;

                ClaimWindowEndsAt = now + window;

                try
                {
                    WorldEventRewardDelivery.SpawnCaches(this);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} cache spawn threw", ex);
                }

                // TECH-DESIGN 2.2 step 5 vs 2.7: the creatures go NOW, not at the end of the claim window.
                // A run is over the moment it resolves, and no event monster may linger while players walk
                // up to the caches. Only the caches themselves survive, until DestroyCaches runs at window
                // end - which is why this pass is cache-exempt rather than a full sweep.
                DestroyEventCreatures();

                // Tick closes the run out when the window expires.
                return;
            }

            // Aborted outcomes never reach Rewarding, so there are no caches to exempt and the creature
            // pass is folded into the single cleanup below.
            CompleteCleanup(now, $"finish:{outcome}");
        }

        /// <summary>
        /// The C16 measurement line, emitted once per run at Finish. Deliberately its own line BESIDE the
        /// fixed 5.2 formats (the same rule the 2.15 difficulty line follows): monitoring queries are written
        /// against those formats and they must not grow fields.
        ///
        /// Format:
        ///   [WORLDEVENT] run=&lt;id&gt; throughput hpCleared=&lt;n&gt; activeSeconds=&lt;n&gt; hps=&lt;n&gt;
        ///   bossMode=&lt;power|throughput&gt; bossMult=&lt;n&gt; bossMax=&lt;n&gt; bossTtkSeconds=&lt;n&gt; bossHps=&lt;n&gt;
        ///
        /// hps is what the WAVE phase measured; bossHps is what the group actually achieved against the boss
        /// (bossMax / bossTtkSeconds). Comparing the two is the whole point of the line - it is the evidence
        /// the calibration dial is tuned from. bossTtkSeconds and bossHps are 0 when no boss stood or the
        /// boss outlived the run, which is a "not measured", never a fast kill. Never throws.
        /// </summary>
        private void LogThroughputLine(double now)
        {
            try
            {
                var elapsed = ActiveSeconds(now);
                var cleared = Throughput.TotalHealthCleared;
                var hps = Throughput.Throughput(elapsed);

                var bossMax = Spawner.BossMaxHealth;
                var ttk = Spawner.BossTtkSeconds;
                var bossHps = ttk > 0 ? bossMax / ttk : 0;

                log.Info($"[WORLDEVENT] run={RunId} throughput hpCleared={cleared:F0} activeSeconds={elapsed:F0} " +
                         $"hps={hps:F1} bossMode={(BossThroughputActive ? "throughput" : "power")} " +
                         $"bossMult={BossHealthMult:F2} bossMax={bossMax} bossTtkSeconds={ttk:F0} bossHps={bossHps:F1}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} throughput line threw", ex);
            }
        }

        /// <summary>
        /// The creature half of cleanup: destroys every held object that is NOT a Weave Cache, and emits the
        /// fixed 5.2 cleanup line once, one second later, with the real survivor guids.
        /// </summary>
        private void DestroyEventCreatures()
        {
            try
            {
                Spawner.DestroyAll(this, includeCaches: false);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} creature cleanup threw", ex);
            }
        }

        /// <summary>
        /// Finish steps 5 and 6, shared by the abort path (straight from Resolved) and by the claim-window
        /// expiry in Tick (from Rewarding).
        ///
        /// On the rewarding path the creatures are already gone (DestroyEventCreatures ran at Finish) and
        /// the caches have just been queued for destruction by DestroyCaches, so the sweep here is a safety
        /// net that catches whatever is still standing. On the abort path it is the only cleanup pass, and
        /// it is the one that emits the fixed 5.2 cleanup line.
        /// </summary>
        private void CompleteCleanup(double now, string reason)
        {
            if (Transition(WorldEventState.Cleanup, reason))
            {
                // Captured BEFORE anything below erases the evidence: DestroyAll queues every held object's
                // destroy (which eventually clears CurrentLandblock) and ReleaseLandblockHold nulls
                // AnchorLandblock as its first statement.
                var sweepBlocks = SweepLandblocks();

                try
                {
                    Spawner.DestroyAll(this, includeCaches: true);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} cleanup threw", ex);
                }

                try
                {
                    // Beside the fixed 5.2 cleanup line, never part of it, and with its own leading keyword
                    // so a monitoring query written against "cleanup destroyed=" cannot match it. Emitted
                    // here rather than from DestroyAll so it carries the FINAL counts: an add generated
                    // during the claim window (after the creature pass, before this) is refused and
                    // destroyed, and that is a stray worth seeing.
                    var adds = Spawner.AddCounters;

                    log.Info($"[WORLDEVENT] run={RunId} adds adopted={adds.Adopted} stray={adds.Stray}");
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} could not report the generated-add counts", ex);
                }

                try
                {
                    SweepGeneratedStragglers(sweepBlocks);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} straggler sweep threw", ex);
                }

                try
                {
                    ReleaseLandblockHold();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} landblock release threw", ex);
                }
            }

            var duration = ActiveAt > 0 ? now - ActiveAt : 0;

            if (!Transition(WorldEventState.Done, reason))
            {
                // Unreachable in practice; forced so a stuck run can never block the next TryStart.
                log.Error($"[WORLDEVENT] run={RunId} could not transition {State} -> Done; forcing Done");
                State = WorldEventState.Done;
            }

            log.Info($"[WORLDEVENT] run={RunId} finish outcome={Outcome} participants={Participation.Count} duration={duration:F0}");
        }

        /// <summary>
        /// The landblocks the cleanup straggler sweep runs on (2026-09-05).
        ///
        /// Three sources, unioned: the anchor, the anchor's ADJACENTS, and every block a held object
        /// actually stands on.
        ///
        /// The adjacents are not optional. When the theme sets holdAdjacentLandblocks, heldBlockKeys is
        /// computed from exactly this list at hold time, so these are the blocks the run holds and spawns
        /// on; and <see cref="Landblock.Adjacents"/> is a list of LIVE Landblock references, so getting
        /// from the held keys to sweepable blocks needs no manager lookup at all. Without them the sweep
        /// has a hole: a wave creature that died mid-run on an adjacent block has had its CurrentLandblock
        /// nulled by the landblock's removal path, so if it was the last held object ever placed there, the
        /// held-object scan below never names that block and a straggler on it - exactly the case this
        /// sweep exists for - survives.
        ///
        /// Sweeping a block the run did NOT hold costs nothing and risks nothing: the sweep's predicate is
        /// a generator guid this run held, so a block with no such generator matches nothing.
        ///
        /// MUST be called before DestroyAll and before ReleaseLandblockHold; see the call site.
        /// </summary>
        private List<Landblock> SweepLandblocks()
        {
            var anchor = AnchorLandblock;

            var adjacents = new List<Landblock>();

            if (anchor != null)
            {
                try
                {
                    // Copied rather than enumerated in place: Adjacents is a plain public List that
                    // landblock code rebuilds, and a concurrent rebuild must cost at most the adjacents,
                    // never the whole sweep.
                    var live = anchor.Adjacents;

                    if (live != null)
                        adjacents.AddRange(live);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} could not read the anchor's adjacent landblocks; " +
                              "sweeping the anchor and the held objects' own blocks only", ex);
                    adjacents.Clear();
                }
            }

            var fromHeldObjects = new List<Landblock>();

            foreach (var wo in HeldSnapshot())
            {
                var landblock = wo?.CurrentLandblock;

                if (landblock != null)
                    fromHeldObjects.Add(landblock);
            }

            return WorldEventGeneratedAdds.SweepTargets(anchor, adjacents, fromHeldObjects);
        }

        /// <summary>
        /// The safety net for generator-spawned stragglers (2026-09-05, Task 2).
        ///
        /// WorldObject.OnGeneration adopts an add at birth, which is what normally closes the leak. This
        /// catches whatever a future code path leaks the same way, and - the case adoption structurally
        /// cannot cover - an add whose parent a player killed early enough that the child outlived every
        /// list it was ever in.
        ///
        /// PREDICATE: the object's GeneratorId is a guid this run EVER held. Exact, with no false
        /// positives: GeneratorId is the parent's guid, it is written by GeneratorProfile.Spawn, and it
        /// survives the parent's own destruction (WorldObject.Destroy -> NotifyOfEvent nulls the DESTROYED
        /// object's Generator/GeneratorId, never its children's). A Player has no GeneratorId and so cannot
        /// match; <see cref="WorldEventGeneratedAdds"/>'s caller asserts it regardless.
        ///
        /// THREADING: each block's scan and destroy is enqueued on THAT block's own action queue - never
        /// run here on the world thread, which would read another landblock's object collection
        /// cross-thread and would recycle guids out from under still-pending actions (the reason
        /// WorldEventScene.Clear gives for the same rule). Each block gets its OWN copy of the ever-held
        /// set, because the closure mutates it and two landblock threads would otherwise share one HashSet.
        /// </summary>
        private void SweepGeneratedStragglers(List<Landblock> blocks)
        {
            if (blocks == null || blocks.Count == 0)
                return;

            var everHeld = HeldGuidsSnapshot();

            if (everHeld.Count == 0)
                return;

            foreach (var landblock in blocks)
            {
                var target = landblock;
                var owned = new HashSet<uint>(everHeld);

                landblock.EnqueueAction(new ActionEventDelegate(() => RunStragglerSweep(target, owned)));
            }
        }

        /// <summary>
        /// One block's sweep, running on that block's action queue. Never throws out of the queue.
        /// </summary>
        private void RunStragglerSweep(Landblock landblock, HashSet<uint> owned)
        {
            try
            {
                var objects = landblock.GetAllWorldObjectsForDiagnostics();

                var byGuid = new Dictionary<uint, WorldObject>();
                var candidates = new List<GeneratedCandidate>();

                foreach (var wo in objects)
                {
                    if (wo == null || wo.IsDestroyed)
                        continue;

                    // Cannot happen - a Player is not generated and carries no GeneratorId - and asserted
                    // anyway, because the cost of being wrong here is destroying a player character.
                    if (wo is Player)
                        continue;

                    var generatorId = wo.GeneratorId;

                    if (generatorId == null || generatorId.Value == 0)
                        continue;

                    var guid = wo.Guid.Full;

                    byGuid[guid] = wo;
                    candidates.Add(new GeneratedCandidate(guid, generatorId.Value));
                }

                if (candidates.Count == 0)
                    return;

                var result = WorldEventGeneratedAdds.ResolveStragglers(owned, candidates);

                if (result.Bounded)
                    log.Warn($"[WORLDEVENT] run={RunId} straggler sweep hit its {WorldEventGeneratedAdds.MaxSweepPasses}-pass bound " +
                             $"on landblock 0x{landblock.Id.Raw:X8}; some stragglers may remain");

                if (result.Guids.Count == 0)
                    return;

                var destroyed = 0;

                foreach (var guid in result.Guids)
                {
                    if (!byGuid.TryGetValue(guid, out var wo) || wo == null || wo.IsDestroyed)
                        continue;

                    if (wo is Creature creature && creature.IsDead)
                    {
                        // The same guard DestroyAll takes: Creature.Die queues a delayed chain that builds
                        // the corpse once the death animation finishes, and destroying the creature out
                        // from under that chain would have it corpse an object already removed from the
                        // landblock.
                        continue;
                    }

                    // Already on this block's action queue, so this IS the enqueued context.
                    wo.Destroy();
                    destroyed++;
                }

                if (destroyed > 0)
                    log.Info($"[WORLDEVENT] run={RunId} addsweep destroyed={destroyed} passes={result.Passes} " +
                             $"landblock=0x{landblock.Id.Raw:X8}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} straggler sweep threw on a landblock action queue", ex);
            }
        }

        /// <summary>
        /// Hands the blocks this run brought in back to the ordinary dormancy/unload cycle (TECH-DESIGN 2.11).
        ///
        /// Only the keys computed at Stage are touched: a block that was ALREADY permaloaded when the event
        /// staged - a Server.PreloadedLandblocks entry, or another system's hold - is never cleared, because
        /// Permaload carries no reference count and clearing it would silently unload somebody else's block
        /// five minutes later (R6).
        /// </summary>
        private void ReleaseLandblockHold()
        {
            // Dropped first, so a spawn queued behind this point can never resurrect the hold.
            AnchorLandblock = null;

            if (landblockBridge == null || heldBlockKeysToRelease == null || heldBlockKeysToRelease.Count == 0)
                return;

            landblockBridge.ReleasePermaload(heldBlockKeysToRelease);

            log.Info($"[WORLDEVENT] run={RunId} released the permaload hold on {heldBlockKeysToRelease.Count} landblock(s)");

            heldBlockKeysToRelease = new List<ulong>();
        }

        /// <summary>
        /// The ONE place State is written. Validates against the pure legality table and refuses (never
        /// throws) on an illegal edge.
        /// </summary>
        private bool Transition(WorldEventState to, string reason)
        {
            if (!WorldEventStateMachine.IsLegal(State, to))
            {
                log.Error($"[WORLDEVENT] run={RunId} illegal transition {State} -> {to} reason={reason} - refused");
                return false;
            }

            var from = State;
            State = to;

            log.Info($"[WORLDEVENT] run={RunId} state {from} -> {to} reason={reason}");

            return true;
        }

        /// <summary>
        /// Whether the boss won this run, and so gets its fail line (D6 - pure): the three FAILED
        /// outcomes, never Success and never any Aborted - an abort is an admin action, not a defeat.
        /// </summary>
        public static bool BossWins(WorldEventOutcome outcome)
        {
            return outcome == WorldEventOutcome.FailedTimeout
                || outcome == WorldEventOutcome.FailedWipe
                || outcome == WorldEventOutcome.FailedNoParticipants;
        }

        private static bool IsAborted(WorldEventOutcome outcome)
        {
            return outcome == WorldEventOutcome.AbortedAdmin
                || outcome == WorldEventOutcome.AbortedShutdown
                || outcome == WorldEventOutcome.AbortedError;
        }

        private AudienceEstimate SampleAudience()
        {
            try
            {
                if (audienceSampler != null)
                    return audienceSampler();

                var radius = Composition?.Source?.RewardRadius ?? 0f;

                return WorldEventAudienceSampler.Sample(Composition?.AnchorPosition, radius);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} audience sample threw", ex);
                return new AudienceEstimate(0, 0, 0);
            }
        }

        /// <summary>
        /// Count of non-staff, living players currently near the anchor (fix/worldevent-abandon-presence-aware).
        /// Same radius source as <see cref="SampleAudience"/> and <see cref="FeedWipeTimer"/> -
        /// Composition?.Source?.RewardRadius - so presence, audience and the wipe rule all agree on what
        /// "near" means. Not wrapped in try/catch here: WorldEventAudienceSampler.CountAliveNear already
        /// holds the run open (returns int.MaxValue) on its own failure, and a seam-backed test closure is
        /// not expected to throw.
        /// </summary>
        private int CountPlayersPresent()
        {
            if (presenceCounter != null)
                return presenceCounter();

            var radius = Composition?.Source?.RewardRadius ?? 0f;

            return WorldEventAudienceSampler.CountAliveNear(Composition?.AnchorPosition, radius);
        }

        private double Now() => clock();
    }
}
