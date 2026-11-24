using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Database.Models.Shard;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// One Allegiance Gauntlet run: the state machine driver (the spec's run state table, through
/// <see cref="GauntletStateMachine"/>), the wave timer and stream, the straggler pull, the record banking and the history
/// writes. It composes a <see cref="GauntletArenaHold"/> (admission, keep-alive and the member hold of the copy) and drives
/// its state; it does not re-implement those interfaces.
///
/// THREADING. <see cref="Tick"/> runs on the world thread (GauntletManager, beside AvatarManager.Tick). Creature work runs on
/// the arena landblock's thread through <see cref="IGauntletWorld.Enqueue"/>: spawning, the 1 s monitor (dealt and activity
/// samples, the engage retry, the straggler pull) and the end-of-run clear. Creature deaths
/// (<see cref="OnGauntletCreatureDied"/>) arrive on a landblock thread, and contact (<see cref="OnMemberContact"/>) on any
/// thread. One lock (<c>sync</c>) guards the run's mutable state and is held only for bookkeeping: every world effect, hook
/// and write a tick decides on is collected and run after the lock is released, and the wave plan is built outside it.
/// Contact has its own lock, so a hit never waits on a tick. The state, the wave number and the member snapshot are
/// published through volatile fields, so the creature-side reads (<see cref="IsLive"/>, <see cref="CurrentWave"/>,
/// <see cref="OnlineMembersInside"/>) take no lock.
///
/// ONLY THE WAVE TIMER ENDS A RUN (owner ruling). An empty arena, a wipe, the fighter's death or a wave whose plan was
/// refused never does: a refused wave keeps its timer running, re-plans every <see cref="ReplanSeconds"/>, and can never
/// count as Cleared, because "cleared" needs a plan with at least one spawn whose every spawn has entered and died.
/// </summary>
public sealed class GauntletRun : IGauntletCreatureOwner, IGauntletContactSink, IGauntletGateTarget, IGauntletHoldRun
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The hold's idle window while the run lives. A run cannot outlive it in practice (each wave is at most a few minutes and every wave is harder).</summary>
    public static readonly TimeSpan LifetimeWindow = TimeSpan.FromHours(24);

    /// <summary>How often a refused wave re-plans while its timer runs.</summary>
    public const double ReplanSeconds = 5.0;

    /// <summary>The creature monitor cadence (dealt and activity samples, engage retry, straggler pull).</summary>
    public const double MonitorSeconds = 1.0;

    /// <summary>Staging countdown lines, in seconds left.</summary>
    internal static readonly int[] StagingWarnings = { 60, 30, 10, 5, 4, 3, 2, 1 };

    /// <summary>Wave timer lines, in seconds left.</summary>
    internal static readonly int[] WaveWarnings = { 60, 30, 10 };

    // ---- identity ----------------------------------------------------------------------------------------------

    /// <summary>The manager's key for this run (never 0). Stable from TryStart, unlike <see cref="RunId"/>, which the database assigns.</summary>
    public uint RunKey { get; }

    public uint AvatarId { get; }
    public uint? MonarchGuid { get; }
    public uint BuyerGuid { get; }
    public ulong Price { get; }
    public Position NpcLocation { get; }
    public GauntletSiteDef Site { get; }
    public int RunSeed { get; }
    public DateTime StartedAt { get; }

    /// <summary>The copy's hold. Its state IS the run's state.</summary>
    public GauntletArenaHold Hold { get; }

    internal GauntletTuning Tuning { get; }
    private readonly GauntletRules rules;
    private readonly GauntletEnvironment env;
    private readonly Func<Player, bool> memberLookup;

    private readonly object sync = new object();
    private readonly object contactSync = new object();

    // ---- published state (lock-free reads) -----------------------------------------------------------------------

    private uint runId;
    private uint instance;
    private int currentWave;
    private GauntletOccupant[] occupants = Array.Empty<GauntletOccupant>();

    // ---- guarded by sync -------------------------------------------------------------------------------------------

    private DateTime stagingEndsAt;
    private int stagingWarned = int.MaxValue;
    private bool favorCommitted;
    private bool reservationReleased;
    private bool refundIssued;
    private long snapshotTop;
    private bool wentActive;
    private DateTime? committedAt;
    private int wavesCleared;
    private int peakHeadcount;
    private WaveRun wave;
    private DateTime intermissionEndsAt;
    private DateTime rewardEndsAt;
    private bool rewardingBegun;
    private DateTime? fighterReturnsAt;
    private DateTime nextFighterCheckAt;
    private DateTime nextGateCheckAt;
    private GauntletEndReason? endReason;
    private DateTime? endedAt;
    private long scoreCenti;
    private int eligibleAtEnd;
    private int entryRotation;
    private string lastRefusal;
    private readonly List<AllegianceGauntletWave> pendingWaves = new List<AllegianceGauntletWave>();
    private bool runRowDirty;

    /// <summary>The next wave, planned at Intermission start so the fighter is restamped from it then. Guarded by sync.</summary>
    private sealed class NextWave
    {
        public int Wave;
        public int Headcount;
        public GauntletWavePlan Plan;
        public string Refusal;
    }

    private NextWave nextWave;
    private (int Wave, int Headcount)? prePlanNeeded;
    private int cratesClaimed;

    // ---- guarded by contactSync ------------------------------------------------------------------------------------

    private readonly Dictionary<uint, HashSet<int>> contacts = new Dictionary<uint, HashSet<int>>();

    /// <summary>The waves in which a character dealt damage (or their pet did) or supported another member: crate eligibility.</summary>
    private readonly Dictionary<uint, HashSet<int>> activeContacts = new Dictionary<uint, HashSet<int>>();

    /// <summary>One wave in progress.</summary>
    private sealed class WaveRun
    {
        public int Number;
        public int Headcount;
        public GauntletWavePlan Plan;
        public DateTime StartedAt;
        public DateTime SpawnBase;
        public DateTime ClockStart;
        public int LimitSeconds;
        public int Issued;
        public int Resolved;
        public int Failed;
        public int Killed;
        public int PlayerDeaths;
        public int FighterDeaths;
        public uint? ObservedMax;
        public bool Refused;
        public int ReplanAttempts;
        public DateTime NextReplanAt;
        public DateTime NextMonitorAt;
        public int Warned = int.MaxValue;
        public readonly Dictionary<uint, Tracker> Trackers = new Dictionary<uint, Tracker>();
        public readonly List<DungeonRole> FailedRoles = new List<DungeonRole>();
        public readonly HashSet<uint> DiedBeforeReport = new HashSet<uint>();

        public bool StreamDone => !Refused && Plan != null && Plan.Spawns.Count > 0 && Resolved >= Plan.Spawns.Count;
        public int Alive => Trackers.Values.Count(t => t.Alive);
    }

    private sealed class Tracker
    {
        public IGauntletSpawnedCreature Handle;
        public DungeonRole Role;
        public uint Max;
        public bool Alive = true;
        public double Dealt;
        public long Stamp;
        public DateTime LastActivityAt;
    }

    internal GauntletRun(uint runKey, GauntletStartRequest request, GauntletSiteDef site, int runSeed, GauntletTuning tuning, GauntletEnvironment env)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        RunKey = runKey;
        AvatarId = request.AvatarId;
        MonarchGuid = request.MonarchGuid;
        BuyerGuid = request.BuyerGuid;
        Price = request.Price;
        NpcLocation = request.NpcLocation;
        StartedAt = request.NowUtc;
        Site = site ?? throw new ArgumentNullException(nameof(site));
        RunSeed = runSeed;
        Tuning = GauntletTuning.Sanitized(tuning ?? GauntletTuning.Defaults);
        rules = new GauntletRules(Tuning);
        this.env = env ?? throw new ArgumentNullException(nameof(env));

        var lookup = env.MemberLookup?.Invoke(AvatarId);
        memberLookup = p => SafeIsMember(lookup, p);

        Hold = new GauntletArenaHold(memberLookup, LifetimeWindow, GauntletState.Purchased);
    }

    /// <summary>
    /// The admission lookup, FAILING CLOSED: a lookup that throws refuses (AllegianceManager.Players is a plain Dictionary,
    /// also read from the teleport path on landblock threads, so a concurrent swear or break can throw there).
    /// </summary>
    internal static bool SafeIsMember(Func<Player, bool> lookup, Player p)
    {
        if (p == null || lookup == null)
            return false;

        try
        {
            return lookup(p);
        }
        catch (Exception ex)
        {
            log.Warn($"[GAUNTLET] the membership lookup for 0x{p.Guid.Full:X8} threw; refusing ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    // ---- public reads ------------------------------------------------------------------------------------------------

    public GauntletState State => Hold.State;

    public bool IsTerminal => GauntletStateMachine.IsTerminal(State);

    /// <summary>The database run_Id, or 0 until the insert's callback arrives.</summary>
    public uint RunId => Volatile.Read(ref runId);

    /// <summary>The arena copy's instance, or 0 before it loaded.</summary>
    public uint Instance => Volatile.Read(ref instance);

    public GauntletEndReason? EndReason { get { lock (sync) return endReason; } }

    public long ScoreCenti { get { lock (sync) return scoreCenti; } }

    public int WavesCleared { get { lock (sync) return wavesCleared; } }

    /// <summary>The highest wave this run reached (started): the eligibility rule's "waves the run reached".</summary>
    public int WavesReached => Volatile.Read(ref currentWave);

    /// <summary>The key TryAnnounce dedupes on: the run_Id, or (if the insert never answered) the run key with the top bit set.</summary>
    internal uint AnnounceKey => RunId != 0 ? RunId : 0x80000000u | RunKey;

    /// <summary>True when <paramref name="player"/> is a current member of the run's allegiance (fail-closed).</summary>
    public bool IsMember(Player player) => memberLookup(player);

    /// <summary>The gate's admission: the hold's answer (an admitting state and a current member).</summary>
    public bool Admits(Player player) => Hold.Admits(player);

    /// <summary>Seconds left on the player's death hold (Task 9's hook), or 0.</summary>
    public double DeathHoldSecondsLeft(Player player)
    {
        try
        {
            return env.Hooks.Death.HoldSecondsLeft(this, player);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} death hold lookup threw", ex);
            return 0;
        }
    }

    public Position CentrePosition => ToPosition(Site.Centre);

    public Position PenPosition => ToPosition(Site.Pen);

    /// <summary>Task 9: the hold length this run captured (its sanitized allegiance_gauntlet_death_hold_seconds).</summary>
    long IGauntletHoldRun.DeathHoldSeconds => Tuning.DeathHoldSeconds;

    /// <summary>The next arrival point in rotation, in the arena's instance. Null before the copy loaded or with no entries.</summary>
    public Position NextEntryPoint()
    {
        if (Instance == 0 || Site.Entries == null || Site.Entries.Count == 0)
            return null;

        int i;
        lock (sync)
            i = entryRotation++;

        return ToPosition(Site.Entries[(int)((uint)i % (uint)Site.Entries.Count)]);
    }

    private Position ToPosition(DungeonSpawnPointDef p)
        => p == null ? null : new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, Instance);

    /// <summary>
    /// Where the selling Avatar stands, for the start lines and the status: its landblock and a /loc-style cell and offset,
    /// "landblock 0xA9B4 (0xA9B40019 [84.0 96.0 20.0])". Null for no location. Pure.
    /// </summary>
    internal static string NpcLocationText(Position at)
    {
        if (at == null)
            return null;

        var cell = at.Cell;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "landblock 0x{0:X4} (0x{1:X8} [{2:F1} {3:F1} {4:F1}])",
            cell >> 16, cell, at.PositionX, at.PositionY, at.PositionZ);
    }

    /// <summary>The gate's whereabouts for an allegiance line: "beside the Avatar at {where}", or just "beside the Avatar".</summary>
    private string GateWhereText()
    {
        var where = NpcLocationText(NpcLocation);
        return where == null ? "beside the Avatar" : $"beside the Avatar at {where}";
    }

    /// <summary>A read-only view for the admin list and the status command.</summary>
    public GauntletRunInfo Info(DateTime nowUtc)
    {
        lock (sync)
        {
            var secondsLeft = 0.0;

            switch (State)
            {
                case GauntletState.Staging: secondsLeft = (stagingEndsAt - nowUtc).TotalSeconds; break;
                case GauntletState.Active when wave != null: secondsLeft = wave.LimitSeconds - (nowUtc - wave.ClockStart).TotalSeconds; break;
                case GauntletState.Intermission: secondsLeft = (intermissionEndsAt - nowUtc).TotalSeconds; break;
                case GauntletState.Rewarding: secondsLeft = (rewardEndsAt - nowUtc).TotalSeconds; break;
            }

            return new GauntletRunInfo(RunKey, RunId, AvatarId, Site.Id, Instance, State, Volatile.Read(ref currentWave),
                wave?.Plan?.Level ?? (wave != null ? rules.LevelOf(wave.Number) : 0), wavesCleared, (int)Math.Max(0, Math.Ceiling(secondsLeft)),
                wave?.Headcount ?? 0, FoughtCount, lastRefusal, NpcLocationText(NpcLocation));
        }
    }

    // ---- contact (Task 11 feeds it; Task 12 reads it) ---------------------------------------------------------------

    /// <summary>
    /// IGauntletContactSink: a member had contact with a Gauntlet creature of <paramref name="wave"/>. Any thread. The first
    /// contact is recorded whoever the member is: an exempt (admin/trial/leaderboard-exempt) account does not unrank the run.
    /// </summary>
    public void OnMemberContact(Player member, int wave, bool active)
    {
        if (member == null)
            return;

        // A held member (dead, in the pen) earns nothing, whatever they reach from the edge.
        if (DeathHoldSecondsLeft(member) > 0)
            return;

        var guid = member.Guid.Full;
        RecordContact(guid, wave, active);
    }

    /// <summary>
    /// GauntletHooks.OnBeneficialAct: a member, standing inside the circle, healed or buffed another member or the fighter
    /// during <paramref name="wave"/>. Counts as active contact (toward a crate).
    /// </summary>
    public void OnMemberSupport(Player member, int wave)
    {
        // Only while a wave is live: support in the break would credit the wave just cleared, which it did not help.
        if (State != GauntletState.Active)
            return;

        OnMemberContact(member, wave, active: true);
    }

    /// <summary>True when <paramref name="p"/> is inside the arena circle (horizontal distance from the centre, outdoor metres).</summary>
    public bool InsideCircle(Position p)
    {
        if (p == null)
            return false;

        var lb = (ushort)(p.Cell >> 16);
        var dx = OutdoorSiteGeometry.ToGlobalX(lb, p.PositionX) - CentreGx;
        var dy = OutdoorSiteGeometry.ToGlobalY(lb, p.PositionY) - CentreGy;
        return dx * dx + dy * dy <= Radius * Radius;
    }

    /// <summary>
    /// The bookkeeping half of <see cref="OnMemberContact"/>, Player-free. Waves below 1 are ignored. An active contact also
    /// counts toward a crate; any contact counts the character as having fought.
    /// </summary>
    internal void RecordContact(uint characterGuid, int wave, bool active = true)
    {
        if (characterGuid == 0 || wave < 1)
            return;

        lock (contactSync)
        {
            if (!contacts.TryGetValue(characterGuid, out var set))
                contacts[characterGuid] = set = new HashSet<int>();

            set.Add(wave);

            if (active)
            {
                if (!activeContacts.TryGetValue(characterGuid, out var activeSet))
                    activeContacts[characterGuid] = activeSet = new HashSet<int>();

                activeSet.Add(wave);
            }
        }
    }

    /// <summary>The distinct waves in which <paramref name="characterGuid"/> had contact (a copy; empty when none). Any thread.</summary>
    public IReadOnlySet<int> ContactWaves(uint characterGuid)
    {
        lock (contactSync)
            return contacts.TryGetValue(characterGuid, out var set) ? new HashSet<int>(set) : new HashSet<int>();
    }

    /// <summary>How many distinct characters had contact in <paramref name="wave"/>.</summary>
    internal int ContactedInWave(int wave)
    {
        lock (contactSync)
            return contacts.Values.Count(set => set.Contains(wave));
    }

    /// <summary>
    /// The headcount a wave after an intermission scales with: the members inside now, or the members who fought the wave
    /// just cleared, whichever is more. Leaving during the break therefore cannot shrink the next wave. Pure.
    /// </summary>
    internal static int NextWaveHeadcount(int occupantsNow, int contactedLastWave) => Math.Max(occupantsNow, contactedLastWave);

    /// <summary>The distinct waves in which <paramref name="characterGuid"/> dealt damage or supported (a copy; empty when none).</summary>
    public IReadOnlySet<int> ActiveWaves(uint characterGuid)
    {
        lock (contactSync)
            return activeContacts.TryGetValue(characterGuid, out var set) ? new HashSet<int>(set) : new HashSet<int>();
    }

    /// <summary>Every character with contact in this run (the "fought" set).</summary>
    public IReadOnlyCollection<uint> Fought
    {
        get
        {
            lock (contactSync)
                return contacts.Keys.ToList();
        }
    }

    public int FoughtCount
    {
        get
        {
            lock (contactSync)
                return contacts.Count;
        }
    }

    /// <summary>
    /// Crate eligibility (GauntletRules.IsEligible) over this character's ACTIVE waves (damage dealt, by them or their pet,
    /// or support given) and the waves the run reached. Only being hit never earns a crate.
    /// </summary>
    public bool IsEligible(uint characterGuid) => rules.IsEligible(ActiveWaves(characterGuid), WavesReached);

    /// <summary>Every eligible character.</summary>
    public IReadOnlyList<uint> EligibleCharacters() => Fought.Where(IsEligible).ToList();

    /// <summary>The waves counted toward a crate for <paramref name="characterGuid"/>, and the waves a crate needs.</summary>
    public (int Counted, int Required) WaveCredit(uint characterGuid)
        => (GauntletRules.CountedWaves(ActiveWaves(characterGuid), WavesReached), rules.RequiredWaves(WavesReached));

    /// <summary>The Rewarding announcement to the arena and the allegiance.</summary>
    internal static string CachesLine(int requiredWaves, int windowSeconds)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(windowSeconds / 60.0));
        return $"Gauntlet Caches have appeared at the arena centre. Members who fought or helped in {requiredWaves} {(requiredWaves == 1 ? "wave" : "waves")} may claim one crate within {minutes} {(minutes == 1 ? "minute" : "minutes")}.";
    }

    public const string EarnedLine = "You have earned a Gauntlet crate.";

    /// <summary>The line a member who fought too few waves gets, at Rewarding and from a refused claim.</summary>
    internal static string ShortLine(int counted, int required)
        => $"You fought or helped others in {counted} of the {required} {(required == 1 ? "wave" : "waves")} needed for a crate.";

    /// <summary>At Rewarding: the caches line inside and to the allegiance, then each fought member's own standing.</summary>
    private void AnnounceRewards(int windowSeconds)
    {
        var required = rules.RequiredWaves(WavesReached);
        var line = CachesLine(required, windowSeconds);

        // Once per member: the allegiance send first, then everyone inside it did not reach (a member whose allegiance
        // lookup failed, or a run with no monarch, still hears it in the arena).
        ICollection<uint> reached = null;

        if (MonarchGuid is uint monarch)
            reached = env.World.ToAllegiance(monarch, line);

        env.World.TellInsideExcept(this, line, reached);

        foreach (var guid in Fought)
        {
            var (counted, _) = WaveCredit(guid);
            env.World.TellCharacter(guid, counted >= required ? EarnedLine : ShortLine(counted, required));
        }
    }

    // ---- IGauntletCreatureOwner --------------------------------------------------------------------------------------

    /// <summary>True while the run owns its waves: Active or Intermission.</summary>
    public bool IsLive
    {
        get
        {
            var s = State;
            return s == GauntletState.Active || s == GauntletState.Intermission;
        }
    }

    public int CurrentWave => Volatile.Read(ref currentWave);

    public double CentreGx => OutdoorSiteGeometry.ToGlobalX((ushort)(Site.Centre.Cell >> 16), Site.Centre.X);

    public double CentreGy => OutdoorSiteGeometry.ToGlobalY((ushort)(Site.Centre.Cell >> 16), Site.Centre.Y);

    public double Radius => Site.Radius;

    /// <summary>The members inside at the last world tick (a published snapshot). Any thread, no lock.</summary>
    public IEnumerable<Player> OnlineMembersInside()
    {
        var snapshot = Volatile.Read(ref occupants);
        return snapshot.Where(o => o.Player != null).Select(o => o.Player).ToList();
    }

    /// <summary>Creature.Die (through GauntletSpawner.OnCreatureDied) on the creature's landblock thread.</summary>
    public void OnGauntletCreatureDied(Creature creature)
    {
        if (creature == null)
            return;

        NoteCreatureDied(creature.Guid.Full, creature.P_GauntletWave);
    }

    /// <summary>The bookkeeping half of the death callback. A creature of an earlier wave (a straggler) is ignored.</summary>
    internal void NoteCreatureDied(uint guid, int creatureWave)
    {
        lock (sync)
        {
            if (wave == null || creatureWave != wave.Number || !IsLive)
                return;

            if (wave.Trackers.TryGetValue(guid, out var t))
            {
                if (t.Alive)
                {
                    t.Alive = false;
                    wave.Killed++;
                }
            }
            else
            {
                // A death reported before the spawn result was recorded (another thread): applied when it is.
                wave.DiedBeforeReport.Add(guid);
            }
        }
    }

    // ---- reports from later tasks ---------------------------------------------------------------------------------------

    /// <summary>Task 9: a member died in the arena. Counted on the current wave's row.</summary>
    public void OnMemberDied(Player player)
    {
        lock (sync)
        {
            if (wave != null && IsLive)
                wave.PlayerDeaths++;
        }
    }

    /// <summary>
    /// Task 8: the fighter fell. Counted on the current wave's row; the arena is told; <see cref="IGauntletFighterHooks.Respawn"/>
    /// is called after allegiance_gauntlet_fighter_respawn_seconds. Never ends the run.
    /// </summary>
    public void OnFighterDied()
    {
        var now = env.UtcNow();

        lock (sync)
        {
            if (!IsLive && State != GauntletState.Staging)
                return;

            if (wave != null)
                wave.FighterDeaths++;

            fighterReturnsAt = now.AddSeconds(Tuning.FighterRespawnSeconds);
        }

        SafeTell($"The Avatar has fallen. It returns in {Tuning.FighterRespawnSeconds} seconds.");
    }

    /// <summary>
    /// A claim paid one of this run's crates (GauntletClaims, through GauntletRunClaimAdapter). Counted on the run row's
    /// crates_Claimed. A claim that finishes after the run closed (a direct delivery answers later) writes the row itself,
    /// because a terminal run is never ticked again.
    /// </summary>
    internal void NoteCrateClaimed()
    {
        AllegianceGauntletRun row = null;

        lock (sync)
        {
            cratesClaimed++;

            if (IsTerminal && RunId != 0)
                row = BuildRunRowLocked(RunId, env.UtcNow());
            else
                runRowDirty = true;
        }

        if (row != null)
            SafeStore(s => s.SaveRunEnd(row));
    }

    /// <summary>Crates paid so far.</summary>
    public int CratesClaimed { get { lock (sync) return cratesClaimed; } }

    // ---- database id -------------------------------------------------------------------------------------------------------

    /// <summary>The run insert's callback (a database thread). Writes held for the id flush on the next tick.</summary>
    internal void OnInserted(bool ok, uint id)
    {
        if (!ok || id == 0)
        {
            log.Error($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}): the history row insert failed; its wave and end rows are not written");
            return;
        }

        Volatile.Write(ref runId, id);
    }

    internal AllegianceGauntletRun NewRunRow() => new AllegianceGauntletRun
    {
        AvatarId = AvatarId,
        MonarchGuid = MonarchGuid,
        SiteId = Site.Id,
        StartedAt = StartedAt,
        FavorPrice = Price,
        Ranked = 1,
    };

    // ---- the tick (world thread) ----------------------------------------------------------------------------------------

    /// <summary>One step of the run at <paramref name="now"/>. World thread. Never throws to the caller for a world fault in an effect.</summary>
    internal void Tick(DateTime now)
    {
        if (IsTerminal)
            return;

        if (State == GauntletState.Purchased)
        {
            OpenCopy(now);
            return;
        }

        // The copy went away (unloaded or failed): InstanceLost, from any live state.
        if (!SafeIsCopyLoaded())
        {
            OnInstanceLost(now);
            return;
        }

        RefreshOccupants();

        var after = new List<Action>();
        (int Wave, int Headcount, int Attempt)? planNeeded = null;
        NextWave install = null;
        (int Wave, int Headcount)? prePlan = null;

        lock (sync)
        {
            if (IsTerminal)
                return;

            FlushPendingWrites();

            switch (State)
            {
                case GauntletState.Staging:
                    planNeeded = TickStaging(now, after);
                    break;

                case GauntletState.Active:
                    planNeeded = TickActive(now, after);
                    break;

                case GauntletState.Intermission:
                    if (now >= intermissionEndsAt && Transition(GauntletEvent.IntermissionOver))
                    {
                        var pre = nextWave;
                        nextWave = null;

                        // Members who joined during the break count: a headcount above the pre-plan's discards it, and the
                        // wave is planned now (its stats then arrive without a second buff clear).
                        var headcount = NextHeadcountLocked();

                        if (pre != null && pre.Wave == wavesCleared + 1 && pre.Refusal == null && pre.Plan != null && headcount <= pre.Headcount)
                            install = pre;
                        else
                            planNeeded = (wavesCleared + 1, Math.Max(headcount, pre?.Headcount ?? 0), 0);
                    }
                    TickFighter(now, after);
                    break;

                case GauntletState.Rewarding:
                    if (now >= rewardEndsAt && rewardingBegun)
                    {
                        Transition(GauntletEvent.ClaimWindowOver);
                        CloseLocked(GauntletState.Closed, "The Allegiance Gauntlet is over. The arena fades around you.", after);
                    }
                    break;
            }

            // While anyone may still enter, the gate must stand: a rotted or unloaded gate is placed again.
            if (!IsTerminal && GauntletArenaHold.AdmitsIn(State) && now >= nextGateCheckAt)
            {
                nextGateCheckAt = now.AddSeconds(GateCheckSeconds);
                var npc = NpcLocation;
                after.Add(() => env.Hooks.Gate.Ensure(this, npc));
            }

            prePlan = prePlanNeeded;
            prePlanNeeded = null;
        }

        RunAll(after);

        if (prePlan is { } next)
            PrePlan(next.Wave, next.Headcount);

        if (install != null)
            PlanAndInstall(install.Wave, install.Headcount, 0, now, install.Plan);
        else if (planNeeded is { } p)
            PlanAndInstall(p.Wave, p.Headcount, p.Attempt, now);
    }

    /// <summary>The next wave's headcount: max(occupants now, members who fought the wave just cleared). Under sync.</summary>
    private int NextHeadcountLocked() => rules.ClampHeadcount(NextWaveHeadcount(OccupantCount, ContactedInWave(wavesCleared)));

    /// <summary>
    /// Intermission start: plans the next wave now (outside the lock) and restamps the fighter from it, clearing the members'
    /// buffs, so buffs cast during the break are kept into the wave. A refused or failed plan is planned again when the
    /// intermission ends; the fighter is still restamped (buffs cleared) with no plan.
    /// </summary>
    private void PrePlan(int n, int headcount)
    {
        var (plan, refusal) = BuildPlan(n, headcount, 0);

        lock (sync)
        {
            if (State != GauntletState.Intermission || wavesCleared + 1 != n)
                return;

            nextWave = new NextWave { Wave = n, Headcount = headcount, Plan = plan, Refusal = refusal };
        }

        var restampWith = refusal == null ? plan : null;
        RunAll(new List<Action> { () => env.Hooks.Fighter.Restamp(this, restampWith, clearBuffs: true) });
    }

    /// <summary>Builds wave <paramref name="n"/>'s plan with the run's tuning. Never throws: a throw or an empty plan is a refusal.</summary>
    private (GauntletWavePlan Plan, string Refusal) BuildPlan(int n, int headcount, int attempt)
    {
        GauntletWavePlan plan = null;
        string refusal = null;

        try
        {
            // A re-plan varies the run seed, so a refused pick is not simply repeated.
            plan = env.Planner(Tuning, Site, n, headcount, unchecked(RunSeed + attempt * 7919), $"run {RunKey}");
        }
        catch (Exception ex)
        {
            refusal = $"the plan build threw: {ex.GetType().Name}: {ex.Message}";
            log.Error($"[GAUNTLET] run {RunKey} wave {n}: {refusal}", ex);
        }

        if (plan != null)
        {
            if (plan.Refusal != null)
                refusal = plan.Refusal;
            else if (plan.Spawns == null || plan.Spawns.Count == 0)
                refusal = "the plan has no spawns";
        }
        else
            refusal ??= "the planner returned no plan";

        return (plan, refusal);
    }

    private bool SafeIsCopyLoaded()
    {
        try
        {
            return env.World.IsCopyLoaded(Instance);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} copy check threw; treated as loaded this tick", ex);
            return true;
        }
    }

    private int OccupantCount => Volatile.Read(ref occupants).Length;

    private void RefreshOccupants()
    {
        try
        {
            var list = env.World.Occupants(this) ?? Array.Empty<GauntletOccupant>();
            Volatile.Write(ref occupants, list.GroupBy(o => o.Guid).Select(g => g.First()).ToArray());
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} occupant scan threw; keeping the last snapshot", ex);
        }
    }

    private bool Transition(GauntletEvent ev)
    {
        if (!GauntletStateMachine.TryNext(State, ev, out var next))
            return false;

        Hold.State = next;
        return true;
    }

    // ---- Purchased -> Staging ---------------------------------------------------------------------------------------------

    private void OpenCopy(DateTime now)
    {
        bool ok;
        uint opened = 0;
        string failure = null;

        try
        {
            ok = env.World.OpenCopy(this, out opened, out failure);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} copy open threw", ex);
            ok = false;
            failure = ex.Message;
        }

        if (opened != 0)
            Volatile.Write(ref instance, opened);

        var after = new List<Action>();

        lock (sync)
        {
            if (State != GauntletState.Purchased)
                return;

            if (!ok)
            {
                log.Error($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) at {Site.Id}: the arena did not load ({failure}); aborted, Favor released");
                Transition(GauntletEvent.LoadFailed);
                AbortLocked(GauntletEndReason.Aborted, now, "The Allegiance Gauntlet arena could not be opened. The Favor is returned.", after);
            }
            else
            {
                Transition(GauntletEvent.Loaded);
                stagingEndsAt = now.AddSeconds(Tuning.StagingSeconds);
                log.Info($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) staging at {Site.Id}, instance 0x{Instance:X8}, {Tuning.StagingSeconds} s");

                var monarch = MonarchGuid;
                var staging = Tuning.StagingSeconds;
                var npc = NpcLocation;

                after.Add(() => env.Hooks.Gate.Place(this, npc));
                after.Add(() => env.Hooks.Fighter.Spawn(this));
                after.Add(() => env.Hooks.Ring.Place(this));

                if (monarch.HasValue)
                    after.Add(() => env.World.ToAllegiance(monarch.Value,
                        $"[Allegiance Gauntlet] Your allegiance's Gauntlet opens in {staging} seconds. Use the Gauntlet Gate {GateWhereText()} to enter."));
            }
        }

        RunAll(after);
    }

    // ---- Staging -> Active(1) or Aborted ------------------------------------------------------------------------------------

    private (int, int, int)? TickStaging(DateTime now, List<Action> after)
    {
        var left = (stagingEndsAt - now).TotalSeconds;

        if (left > 0)
        {
            var line = CrossedWarning(StagingWarnings, left, ref stagingWarned);

            if (line > 0)
                after.Add(() => env.World.TellInside(this, $"The Allegiance Gauntlet begins in {line} second{(line == 1 ? "" : "s")}."));

            TickFighter(now, after);
            return null;
        }

        if (OccupantCount < 1)
        {
            Transition(GauntletEvent.StagingOverEmpty);
            log.Info($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}): nobody entered during staging; aborted, Favor released");
            AbortLocked(GauntletEndReason.Aborted, now, "Nobody entered the Allegiance Gauntlet, so it is called off. The Favor is returned.", after);
            return null;
        }

        Transition(GauntletEvent.StagingOverWithMembers);

        // Wave 1 spawning commits the Favor, the cooldown (through the record cache only) and the record snapshot.
        favorCommitted = true;
        wentActive = true;
        committedAt = now;
        var avatarId = AvatarId;
        var price = Price;
        snapshotTop = SafeRecords(r => r.SnapshotTop(), 0L);
        runRowDirty = true;

        after.Add(() => env.Favor.Commit(avatarId, price));
        after.Add(() => env.Records.NoteRunCommitted(avatarId, now));

        // The start line again, for members who missed it during Staging: the gate still admits during the waves.
        if (MonarchGuid is uint monarch)
        {
            var wave1Line = $"[Allegiance Gauntlet] Wave 1 has begun. Join through the Gauntlet Gate {GateWhereText()}.";
            after.Add(() => env.World.ToAllegiance(monarch, wave1Line));
        }

        log.Info($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) active with {OccupantCount} inside; {price} Favor committed; server top snapshot {snapshotTop}");

        return (1, rules.ClampHeadcount(OccupantCount), 0);
    }

    // ---- planning (outside the lock) ------------------------------------------------------------------------------------------

    /// <param name="prebuilt">The plan built at Intermission start (the fighter is already restamped from it), or null to plan now.</param>
    private void PlanAndInstall(int n, int headcount, int attempt, DateTime now, GauntletWavePlan prebuilt = null)
    {
        var (plan, refusal) = prebuilt != null ? (prebuilt, (string)null) : BuildPlan(n, headcount, attempt);

        var after = new List<Action>();

        lock (sync)
        {
            if (State != GauntletState.Active)
                return;

            var fresh = wave == null || wave.Number != n;

            if (fresh)
            {
                Volatile.Write(ref currentWave, n);
                peakHeadcount = Math.Max(peakHeadcount, headcount);

                wave = new WaveRun
                {
                    Number = n,
                    Headcount = headcount,
                    StartedAt = now,
                    LimitSeconds = plan?.TimeLimitSeconds > 0 ? plan.TimeLimitSeconds : rules.TimeLimitSeconds(n),
                    NextMonitorAt = now,
                };
            }

            wave.ReplanAttempts = attempt;

            if (refusal != null)
            {
                wave.Refused = true;
                wave.Plan = null;
                wave.NextReplanAt = now.AddSeconds(ReplanSeconds);

                if (fresh)
                    wave.ClockStart = now;

                lastRefusal = $"wave {n}: {refusal}";

                // Admin-visible: never an empty wave counted as cleared; the timer keeps running and the pick is retried.
                log.Error($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) wave {n} has no spawns ({refusal}); it cannot clear, its timer keeps running, re-planning in {ReplanSeconds} s (attempt {attempt + 1})");
            }
            else
            {
                wave.Refused = false;
                wave.Plan = plan;
                wave.SpawnBase = now;
                wave.Issued = 0;
                wave.Resolved = 0;

                // A re-plan after an attempt whose every spawn failed: those failures belong to the dropped plan.
                wave.Failed = 0;
                wave.FailedRoles.Clear();

                if (fresh)
                    wave.ClockStart = now.AddSeconds(Math.Max(0.0, plan.Spawns.Min(s => s.Delay)));

                log.Info($"[GAUNTLET] run {RunKey} wave {n} L{plan.Level} H{headcount}{(plan.IsBoss ? " boss" : "")}: {plan.Spawns.Count} spawns, plan build {plan.Stats.ElapsedTotalMs:0.0} ms, planning {plan.TotalMs:0.0} ms");
            }

            if (fresh)
            {
                var level = plan?.Level ?? rules.LevelOf(n);
                var limit = wave.LimitSeconds;
                var boss = rules.IsBossWave(n);
                after.Add(() => env.World.TellInside(this, boss
                    ? $"Wave {n} (level {level}) - a boss wave! You have {limit} seconds."
                    : $"Wave {n} (level {level}) begins! You have {limit} seconds."));
            }

            // The stats of a wave that arrived without a break (wave 1, or a mid-wave re-plan after a refusal) are applied
            // without clearing buffs; a pre-built plan was stamped, buffs cleared, at Intermission start.
            if (refusal == null && prebuilt == null)
            {
                var restampWith = plan;
                after.Add(() => env.Hooks.Fighter.Restamp(this, restampWith, clearBuffs: false));
            }

            // The first batch goes out now rather than on the next tick.
            if (refusal == null)
                IssueDueSpawns(now, after);
        }

        RunAll(after);
    }

    // ---- Active(n) ---------------------------------------------------------------------------------------------------------

    private (int, int, int)? TickActive(DateTime now, List<Action> after)
    {
        if (wave == null)
            return null;

        (int, int, int)? replan = null;

        if (wave.Refused && now >= wave.NextReplanAt)
        {
            wave.NextReplanAt = now.AddSeconds(ReplanSeconds);
            replan = (wave.Number, wave.Headcount, wave.ReplanAttempts + 1);
        }

        IssueDueSpawns(now, after);

        // A stream that is done with NO creature in the world (every placement failed, or every batch could not be queued)
        // is an empty wave: never Cleared. It is refused like a plan refusal, so its timer keeps running and it re-plans.
        if (wave.StreamDone && wave.Trackers.Count == 0)
            RefuseEmptyStreamLocked(now);

        if (now >= wave.NextMonitorAt)
        {
            wave.NextMonitorAt = now.AddSeconds(MonitorSeconds);
            QueueMonitor(now, after);
        }

        var elapsed = Math.Max(0.0, (now - wave.ClockStart).TotalSeconds);
        var outcome = ThreadEncounterPortalWaves.Decide(anchorStanding: true, wave.StreamDone, wave.Alive, elapsed, wave.LimitSeconds);

        switch (outcome)
        {
            case PortalWaveOutcome.Cleared:
                WaveClearedLocked(now, elapsed, after);
                return null;

            case PortalWaveOutcome.TimedOut:
                EndLocked(GauntletEvent.TimerExpired, GauntletEndReason.TimeLimit, now, after);
                return null;
        }

        var line = CrossedWarning(WaveWarnings, wave.LimitSeconds - elapsed, ref wave.Warned);

        if (line > 0)
        {
            var n = wave.Number;
            after.Add(() => env.World.TellInside(this, $"{line} seconds remain in wave {n}."));
        }

        TickFighter(now, after);
        return replan;
    }

    /// <summary>
    /// The smallest threshold that <paramref name="left"/> has just crossed and that was not announced yet, or 0. Marks it,
    /// so a late tick that skips several thresholds says only the latest one.
    /// </summary>
    internal static int CrossedWarning(int[] thresholds, double left, ref int warned)
    {
        var crossed = 0;

        foreach (var t in thresholds)
        {
            if (left <= t && t < warned)
                crossed = crossed == 0 ? t : Math.Min(crossed, t);
        }

        if (crossed == 0)
            return 0;

        warned = crossed;
        return crossed;
    }

    /// <summary>Queues every planned spawn whose delay has passed as one landblock action.</summary>
    private void IssueDueSpawns(DateTime now, List<Action> after)
    {
        if (wave?.Plan == null || wave.Refused)
            return;

        var spawns = wave.Plan.Spawns;
        var due = new List<PortalWaveSpawn>();

        while (wave.Issued < spawns.Count && wave.SpawnBase.AddSeconds(spawns[wave.Issued].Delay) <= now)
            due.Add(spawns[wave.Issued++]);

        if (due.Count == 0)
            return;

        var w = wave;
        var needObserved = w.Plan.IsBoss && w.ObservedMax == null && due.Any(s => s.Entry?.Role == DungeonRole.Boss);

        after.Add(() =>
        {
            if (!env.World.Enqueue(this, () => SpawnBatch(w, due, needObserved)))
            {
                // The landblock is gone: the spawns can never enter. The copy check ends the run on the next tick.
                lock (sync)
                {
                    foreach (var s in due)
                        ResolveFailed(w, s);
                }
            }
        });
    }

    /// <summary>The arena landblock's half of the stream: place each due spawn and record what happened.</summary>
    private void SpawnBatch(WaveRun w, List<PortalWaveSpawn> due, bool needObserved)
    {
        uint observed;

        lock (sync)
            observed = w.ObservedMax ?? 0;

        if (needObserved)
        {
            // The boss health floor needs the pack's max: measured before any boss of the batch is placed.
            try
            {
                observed = env.Spawner.ObservedMaxNonBoss(w.Plan);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] run {RunKey} wave {w.Number}: observing the pack max threw; the boss floor uses 0", ex);
                observed = 0;
            }

            lock (sync)
                w.ObservedMax = observed;
        }

        foreach (var s in due)
        {
            bool stale;

            lock (sync)
                stale = !ReferenceEquals(wave, w) || !IsLive;

            if (stale)
            {
                lock (sync)
                    ResolveFailed(w, s, warn: false);
                continue;
            }

            IGauntletSpawnedCreature handle = null;

            try
            {
                handle = env.Spawner.Spawn(w.Plan, s, observed, this);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] run {RunKey} wave {w.Number}: spawning wcid {s.Entry?.Wcid} threw", ex);
            }

            var destroy = false;

            lock (sync)
            {
                if (handle == null)
                {
                    ResolveFailed(w, s);
                }
                else if (!ReferenceEquals(wave, w) || !IsLive)
                {
                    w.Resolved++;
                    destroy = true;
                }
                else
                {
                    var t = new Tracker
                    {
                        Handle = handle,
                        Role = s.Entry?.Role ?? DungeonRole.Trash,
                        Max = handle.SpawnMax,
                        LastActivityAt = env.UtcNow(),
                    };

                    if (w.DiedBeforeReport.Remove(handle.Guid))
                    {
                        t.Alive = false;
                        w.Killed++;
                    }

                    w.Trackers[handle.Guid] = t;
                    w.Resolved++;
                }
            }

            if (destroy)
                SafeDestroy(handle);
        }
    }

    /// <summary>
    /// The wave's stream finished but not one spawn entered the world. Under sync. The wave becomes refused (it cannot clear,
    /// its clock keeps running) and re-plans after <see cref="ReplanSeconds"/>, with the reason admin-visible.
    /// </summary>
    private void RefuseEmptyStreamLocked(DateTime now)
    {
        var w = wave;
        var planned = w.Plan?.Spawns.Count ?? 0;

        w.Refused = true;
        w.Plan = null;
        w.NextReplanAt = now.AddSeconds(ReplanSeconds);
        lastRefusal = $"wave {w.Number}: none of its {planned} spawns could be placed";

        log.Error($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) wave {w.Number}: none of its {planned} spawns entered the world; it cannot clear, its timer keeps running, re-planning in {ReplanSeconds} s");
    }

    /// <summary>A spawn that never entered counts as done and dead (the "cleared" rule), with a warning.</summary>
    private void ResolveFailed(WaveRun w, PortalWaveSpawn s, bool warn = true)
    {
        w.Resolved++;
        w.Failed++;
        w.FailedRoles.Add(s.Entry?.Role ?? DungeonRole.Trash);

        if (warn)
            log.Warn($"[GAUNTLET] run {RunKey} wave {w.Number}: wcid {s.Entry?.Wcid} ({s.Entry?.Role}) could not be placed; counted as done and dead");
    }

    /// <summary>The 1 s creature monitor: samples dealt and activity, retries the wake, and pulls idle stragglers to the centre.</summary>
    private void QueueMonitor(DateTime now, List<Action> after)
    {
        var w = wave;
        var alive = w.Trackers.Values.Where(t => t.Alive).ToList();

        if (alive.Count == 0)
            return;

        // Once the stream is done, every creature idle for straggler_seconds is pulled, however many are alive: a wave can
        // only end on its timer or its last death, so an idle creature far out would otherwise just run the clock down.
        var pulled = new HashSet<Tracker>(w.StreamDone
            ? alive.Where(t => (now - t.LastActivityAt).TotalSeconds >= Tuning.StragglerSeconds)
            : Enumerable.Empty<Tracker>());

        if (pulled.Count > 0)
        {
            foreach (var t in pulled)
                t.LastActivityAt = now;   // at most one pull per straggler_seconds

            log.Info($"[GAUNTLET] run {RunKey} wave {w.Number}: pulling {pulled.Count} idle straggler(s) to the centre");
        }

        var centre = CentrePosition;

        after.Add(() => env.World.Enqueue(this, () =>
        {
            foreach (var t in alive)
            {
                try
                {
                    var h = t.Handle;

                    if (!h.IsAlive)
                        continue;

                    var dealt = h.SampleDealt();
                    var stamp = h.ActivityStamp();

                    if (pulled.Contains(t) && !h.PullTo(centre))
                        log.Warn($"[GAUNTLET] run {RunKey} wave {w.Number}: straggler 0x{h.Guid:X8} could not be pulled to the centre");

                    h.Engage();

                    lock (sync)
                    {
                        if (stamp != t.Stamp || dealt != t.Dealt)
                            t.LastActivityAt = env.UtcNow();

                        t.Stamp = stamp;
                        t.Dealt = dealt;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[GAUNTLET] run {RunKey} wave {w.Number}: monitoring 0x{t.Handle?.Guid:X8} threw", ex);
                }
            }
        }));
    }

    private void WaveClearedLocked(DateTime now, double elapsed, List<Action> after)
    {
        var w = wave;
        Transition(GauntletEvent.WaveCleared);
        wavesCleared = w.Number;
        intermissionEndsAt = now.AddSeconds(Tuning.IntermissionSeconds);
        runRowDirty = true;

        // Plan the next wave now, so the fighter is restamped from it at the start of the break.
        nextWave = null;
        prePlanNeeded = (w.Number + 1, NextHeadcountLocked());

        QueueWaveRow(w, elapsed, outcome: 0, fractionCenti: 100);

        var cleared = wavesCleared;
        var fought = FoughtCount;
        var id = RunId;
        after.Add(() => env.Records.Bank(AvatarId, cleared * 100L, fought, id == 0 ? null : id, now));

        var seconds = (int)Math.Round(elapsed);
        var pause = Tuning.IntermissionSeconds;
        after.Add(() => env.World.TellInside(this, $"Wave {cleared} cleared in {seconds} seconds! The next wave begins in {pause} seconds."));
        after.Add(() => env.Hooks.Fighter.WaveCleared(this, cleared));

        log.Info($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) cleared wave {cleared} in {elapsed:0.0} s");
    }

    // ---- Ending -> Rewarding -------------------------------------------------------------------------------------------------

    /// <summary>The fraction of the live wave destroyed (GauntletScore.Fraction) from the monitor's latest samples.</summary>
    private double LiveFractionLocked()
    {
        var w = wave;

        if (w?.Plan == null)
            return 0;

        var est = w.Plan.UnspawnedHealthEstimate;
        double Estimate(DungeonRole r) => est != null && est.TryGetValue(r, out var v) ? v : 0;

        var rows = new List<GauntletSpawnedRow>();

        foreach (var t in w.Trackers.Values)
            rows.Add(new GauntletSpawnedRow(t.Role.ToString(), t.Max, t.Dealt, t.Alive));

        // A spawn that never entered counts as done and dead: full weight on both sides at its role's estimate.
        foreach (var role in w.FailedRoles)
        {
            var m = Estimate(role);
            rows.Add(new GauntletSpawnedRow(role.ToString(), m, m, false));
        }

        var unspawned = w.Plan.Spawns.Skip(w.Resolved)
            .GroupBy(s => s.Entry?.Role ?? DungeonRole.Trash)
            .Select(g => new GauntletUnspawned(g.Key.ToString(), g.Count(), Estimate(g.Key)))
            .ToList();

        return GauntletScore.Fraction(rows, unspawned);
    }

    /// <summary>
    /// The single end path for a run that went Active: TimerExpired, InstanceLost or AdminAbort, from Active or Intermission.
    /// Scores (the partial only on a timer or admin end mid-wave), writes the wave and run rows, banks, announces once,
    /// clears the remaining monsters, then enters Rewarding. An InstanceLost end pays straight to packs and closes.
    /// </summary>
    private void EndLocked(GauntletEvent ev, GauntletEndReason reason, DateTime now, List<Action> after)
    {
        var from = State;

        if (!Transition(ev))
            return;

        endReason = reason;
        endedAt = now;

        var midWave = from == GauntletState.Active && wave != null;
        var partial = midWave && reason != GauntletEndReason.InstanceLost;
        var fraction = partial ? LiveFractionLocked() : 0.0;

        scoreCenti = GauntletScore.Centi(wavesCleared, fraction, partial);

        if (midWave)
        {
            var elapsed = Math.Max(0.0, (now - wave.ClockStart).TotalSeconds);
            QueueWaveRow(wave, elapsed, outcome: 1, fractionCenti: (int)Math.Min(100, Math.Floor(fraction * 100)));
        }

        eligibleAtEnd = EligibleCharacters().Count;
        runRowDirty = true;

        // The remaining monsters go now, on their landblock (nothing to clear once the copy is gone).
        var leftovers = wave?.Trackers.Values.Where(t => t.Alive).Select(t => t.Handle).ToList() ?? new List<IGauntletSpawnedCreature>();

        if (reason != GauntletEndReason.InstanceLost && leftovers.Count > 0)
            after.Add(() => env.World.Enqueue(this, () => { foreach (var h in leftovers) SafeDestroy(h); }));

        after.Add(() => env.Hooks.Fighter.Remove(this));

        var score = scoreCenti;
        var fought = FoughtCount;
        var id = RunId;
        var key = AnnounceKey;
        var snapshot = snapshotTop;
        var monarch = MonarchGuid;

        after.Add(() => env.Records.Bank(AvatarId, score, fought, id == 0 ? null : id, now));
        after.Add(() => env.Records.TryAnnounce(key, AvatarId, monarch, score, snapshot, fought));

        if (reason != GauntletEndReason.InstanceLost)
        {
            var endLine = reason == GauntletEndReason.Admin
                ? $"An administrator has ended the Allegiance Gauntlet at {Player.FormatWaveScore(score)}."
                : $"Time is up. The Allegiance Gauntlet ends at {Player.FormatWaveScore(score)}.";
            after.Add(() => env.World.TellInside(this, endLine));
        }

        log.Info($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) ended ({reason}) at {score} centi: {wavesCleared} cleared, fraction {fraction:0.000}, {fought} fought, {eligibleAtEnd} eligible");

        Transition(GauntletEvent.EndingDone);

        if (reason == GauntletEndReason.InstanceLost)
        {
            after.Add(() => env.Hooks.Rewards.DeliverDirect(this));
            Transition(GauntletEvent.InstanceLost);
            CloseLocked(GauntletState.Closed, null, after);
            return;
        }

        // Rewarding: the hook places the caches and says how long they stay; the default window until it answers.
        rewardEndsAt = now.AddSeconds(NoGauntletHooks.DefaultClaimWindowSeconds);
        rewardingBegun = false;

        after.Add(() =>
        {
            int window;

            try
            {
                window = env.Hooks.Rewards.BeginRewarding(this);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] run {RunKey} BeginRewarding threw; the default claim window applies", ex);
                window = NoGauntletHooks.DefaultClaimWindowSeconds;
            }

            lock (sync)
            {
                rewardEndsAt = now.AddSeconds(Math.Max(0, window));
                rewardingBegun = true;
            }

            try
            {
                AnnounceRewards(window);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] run {RunKey} reward announcement threw", ex);
            }
        });
    }

    private void OnInstanceLost(DateTime now)
    {
        var after = new List<Action>();

        lock (sync)
        {
            if (IsTerminal)
                return;

            log.Warn($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}): the arena instance 0x{Instance:X8} is gone in {State}");

            switch (State)
            {
                case GauntletState.Staging:
                    Transition(GauntletEvent.InstanceLost);
                    AbortLocked(GauntletEndReason.InstanceLost, now, "The Allegiance Gauntlet arena was lost before the first wave. The Favor is returned.", after);
                    break;

                case GauntletState.Active:
                case GauntletState.Intermission:
                    EndLocked(GauntletEvent.InstanceLost, GauntletEndReason.InstanceLost, now, after);
                    break;

                case GauntletState.Rewarding:
                    Transition(GauntletEvent.InstanceLost);
                    CloseLocked(GauntletState.Closed, null, after);
                    break;
            }
        }

        RunAll(after);
    }

    // ---- admin ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The admin abort. Before wave 1 the run aborts and its reservation is released (always: nothing was spent). From Active
    /// it ends like a timer expiry but with reason Admin (the partial counts); from Intermission it ends with no partial;
    /// from Rewarding it closes. <paramref name="refund"/> also returns a price that was already committed.
    /// Returns the line for the admin.
    /// </summary>
    internal string Abort(bool refund, DateTime now)
    {
        var after = new List<Action>();
        string result;

        lock (sync)
        {
            switch (State)
            {
                case GauntletState.Purchased:
                case GauntletState.Staging:
                    Transition(GauntletEvent.AdminAbort);
                    AbortLocked(GauntletEndReason.Admin, now, "An administrator has called off the Allegiance Gauntlet. The Favor is returned.", after);
                    result = $"Run {RunKey} aborted before wave 1; its {Price} Favor reservation was released.";
                    break;

                case GauntletState.Active:
                case GauntletState.Intermission:
                    EndLocked(GauntletEvent.AdminAbort, GauntletEndReason.Admin, now, after);
                    result = $"Run {RunKey} ended by admin at {scoreCenti} centi.";
                    break;

                case GauntletState.Rewarding:
                    Transition(GauntletEvent.AdminAbort);
                    CloseLocked(GauntletState.Closed, "An administrator has closed the Allegiance Gauntlet.", after);
                    result = $"Run {RunKey} closed during Rewarding.";
                    break;

                default:
                    return $"Run {RunKey} is already {State}.";
            }

            // At most one refund per run: an abort from Active leaves the run in Rewarding, where a second abort still finds it.
            if (refund && favorCommitted && refundIssued)
            {
                result += " The committed Favor was already refunded.";
            }
            else if (refund && favorCommitted)
            {
                refundIssued = true;
                var avatarId = AvatarId;
                var price = Price;
                after.Add(() =>
                {
                    if (env.Favor.RefundCommitted(avatarId, price))
                        return;

                    // Not made: a later abort of this run may try again.
                    lock (sync)
                        refundIssued = false;

                    log.Error($"[GAUNTLET] run {RunKey}: the admin refund of {price} committed Favor to Avatar {avatarId} could not be made; restore it by hand");
                });
                result += $" Refund of {Price} committed Favor requested.";
            }
        }

        RunAll(after);
        return result;
    }

    // ---- terminal paths ------------------------------------------------------------------------------------------------------

    /// <summary>An end before wave 1: the reservation goes back, the run row closes, and the copy is let go.</summary>
    private void AbortLocked(GauntletEndReason reason, DateTime now, string allegianceLine, List<Action> after)
    {
        endReason = reason;
        endedAt = now;
        Hold.State = GauntletState.Aborted;
        runRowDirty = true;

        if (!favorCommitted && !reservationReleased)
        {
            reservationReleased = true;
            var avatarId = AvatarId;
            var price = Price;
            after.Add(() => env.Favor.Release(avatarId, price));
        }

        WriteRunRowLocked(now, after);

        if (MonarchGuid.HasValue && allegianceLine != null)
        {
            var monarch = MonarchGuid.Value;
            after.Add(() => env.World.ToAllegiance(monarch, allegianceLine));
        }

        TerminalEffects(GauntletState.Aborted, "The Allegiance Gauntlet is called off. The arena fades around you.", after);
    }

    /// <summary>Closed after Rewarding (or an InstanceLost end): the run row's final write and the terminal effects.</summary>
    private void CloseLocked(GauntletState terminal, string line, List<Action> after)
    {
        Hold.State = terminal;
        WriteRunRowLocked(env.UtcNow(), after);
        TerminalEffects(terminal, line, after);
    }

    private void TerminalEffects(GauntletState terminal, string line, List<Action> after)
    {
        var wasRewarding = rewardingBegun;

        after.Add(() => env.Hooks.Gate.Remove(this));
        after.Add(() => env.Hooks.Fighter.Remove(this));
        after.Add(() => env.Hooks.Ring.Remove(this));

        if (wasRewarding)
            after.Add(() => env.Hooks.Rewards.EndRewarding(this));

        after.Add(() => env.Hooks.Death.ReleaseAll(this));

        if (Instance != 0)
            after.Add(() => env.World.EvictAndRelease(this, line ?? "The Allegiance Gauntlet is over. The arena fades around you."));

        log.Info($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) {terminal} ({endReason})");
    }

    // ---- fighter respawn timer ------------------------------------------------------------------------------------------------

    /// <summary>How often the run asks the gate hook to place a gate that is gone.</summary>
    public const double GateCheckSeconds = 5.0;

    /// <summary>How often the run asks the fighter hook to replace a fighter that left the world without dying.</summary>
    public const double FighterCheckSeconds = 5.0;

    private void TickFighter(DateTime now, List<Action> after)
    {
        if (now >= nextFighterCheckAt)
        {
            nextFighterCheckAt = now.AddSeconds(FighterCheckSeconds);
            after.Add(() => env.Hooks.Fighter.Ensure(this));
        }

        if (fighterReturnsAt is not { } at || now < at)
            return;

        fighterReturnsAt = null;
        after.Add(() => env.Hooks.Fighter.Respawn(this));
        after.Add(() => env.World.TellInside(this, "The Avatar rejoins the fight."));
    }

    // ---- history writes -------------------------------------------------------------------------------------------------------

    private void QueueWaveRow(WaveRun w, double seconds, byte outcome, int fractionCenti)
    {
        pendingWaves.Add(new AllegianceGauntletWave
        {
            Wave = (ushort)Math.Clamp(w.Number, 0, ushort.MaxValue),
            Level = (uint)Math.Max(0, w.Plan?.Level ?? rules.LevelOf(w.Number)),
            Headcount = (ushort)Math.Clamp(w.Headcount, 0, ushort.MaxValue),
            Planned = (ushort)Math.Clamp(w.Plan?.Spawns.Count ?? 0, 0, ushort.MaxValue),
            Killed = (ushort)Math.Clamp(w.Killed, 0, ushort.MaxValue),
            Seconds = (float)seconds,
            Outcome = outcome,
            FractionCenti = (byte)Math.Clamp(fractionCenti, 0, 100),
            PlayerDeaths = (ushort)Math.Clamp(w.PlayerDeaths, 0, ushort.MaxValue),
            FighterDeaths = (ushort)Math.Clamp(w.FighterDeaths, 0, ushort.MaxValue),
            Family = w.Plan?.Plan?.FamilyId,
        });
    }

    private void WriteRunRowLocked(DateTime now, List<Action> after)
    {
        var id = RunId;

        if (id == 0)
            return;   // flushed once the id arrives (FlushPendingWrites)

        var row = BuildRunRowLocked(id, now);
        runRowDirty = false;
        after.Add(() => env.Store.SaveRunEnd(row));
    }

    /// <summary>Writes the wave rows and the run row held for the database id, once it has arrived. Under sync.</summary>
    private void FlushPendingWrites()
    {
        var id = RunId;

        if (id == 0)
            return;

        foreach (var row in pendingWaves)
        {
            row.RunId = id;
            SafeStore(s => s.SaveWave(row));
        }

        pendingWaves.Clear();

        if (runRowDirty)
        {
            runRowDirty = false;
            var row = BuildRunRowLocked(id, env.UtcNow());
            SafeStore(s => s.SaveRunEnd(row));
        }
    }

    /// <summary>Flushes any held writes now (a terminal run is never ticked again). Called by the manager when it drops the run.</summary>
    internal void FlushOnRemove()
    {
        lock (sync)
        {
            FlushPendingWrites();

            if (RunId == 0 && (pendingWaves.Count > 0 || runRowDirty))
                log.Error($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) is dropped with no history row id; {pendingWaves.Count} wave row(s) and the end row are lost");
        }
    }

    private AllegianceGauntletRun BuildRunRowLocked(uint id, DateTime now)
    {
        var row = NewRunRow();
        row.RunId = id;
        row.ActiveAt = wentActive ? StartedAtActive : null;
        row.WavesCleared = (ushort)Math.Clamp(wavesCleared, 0, ushort.MaxValue);
        row.ScoreCenti = (uint)Math.Clamp(scoreCenti, 0, uint.MaxValue);
        row.Participants = (ushort)Math.Clamp(FoughtCount, 0, ushort.MaxValue);
        row.Eligible = (ushort)Math.Clamp(eligibleAtEnd, 0, ushort.MaxValue);
        row.PeakHeadcount = (ushort)Math.Clamp(peakHeadcount, 0, ushort.MaxValue);
        row.CratesClaimed = (ushort)Math.Clamp(cratesClaimed, 0, ushort.MaxValue);
        row.FavorCommitted = (byte)(favorCommitted ? 1 : 0);
        row.Ranked = 1;

        if (endReason.HasValue && (IsTerminal || State == GauntletState.Rewarding || State == GauntletState.Ending))
        {
            row.EndedAt = endedAt ?? now;
            row.EndReason = (byte)endReason.Value;
        }

        return row;
    }

    private DateTime? StartedAtActive => wentActive ? committedAt : null;

    /// <summary>When wave 1 committed the Favor (the cooldown's start), or null while the run has not gone Active.</summary>
    public DateTime? CommittedAt { get { lock (sync) return committedAt; } }

    // ---- small helpers ----------------------------------------------------------------------------------------------------------

    private T SafeRecords<T>(Func<IGauntletRecordSink, T> f, T fallback)
    {
        try
        {
            return f(env.Records);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} record cache call threw", ex);
            return fallback;
        }
    }

    private void SafeStore(Action<IGauntletStore> f)
    {
        try
        {
            f(env.Store);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} history write threw", ex);
        }
    }

    private void SafeTell(string text)
    {
        try
        {
            env.World.TellInside(this, text);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} message threw", ex);
        }
    }

    private void SafeDestroy(IGauntletSpawnedCreature h)
    {
        try
        {
            h?.Destroy();
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {RunKey} destroying 0x{h?.Guid:X8} threw", ex);
        }
    }

    private void RunAll(List<Action> actions)
    {
        foreach (var a in actions)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] run {RunKey} (Avatar {AvatarId}) effect threw", ex);
            }
        }
    }

    public override string ToString() => $"Gauntlet run {RunKey} (id {RunId}, Avatar {AvatarId}, {Site.Id}, {State})";
}
