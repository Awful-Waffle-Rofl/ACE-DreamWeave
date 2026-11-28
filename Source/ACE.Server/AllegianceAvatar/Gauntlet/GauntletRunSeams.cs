using System;
using System.Collections.Generic;

using ACE.Database.Models.Shard;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>Why a run ended: the allegiance_gauntlet_run.end_Reason column (0 TimeLimit, 1 Aborted, 2 Restart, 3 InstanceLost, 4 Admin).</summary>
public enum GauntletEndReason : byte
{
    TimeLimit = 0,
    Aborted = 1,
    Restart = 2,
    InstanceLost = 3,
    Admin = 4,
}

/// <summary>One online player standing inside an arena copy. <see cref="Player"/> is null only in tests.</summary>
public readonly record struct GauntletOccupant(uint Guid, Player Player);

/// <summary>A read-only view of one run, for the admin list and the status command.</summary>
public sealed record GauntletRunInfo(
    uint RunKey,
    uint RunId,
    uint AvatarId,
    string SiteId,
    uint Instance,
    GauntletState State,
    int Wave,
    int Level,
    int WavesCleared,
    int SecondsLeft,
    int Headcount,
    int Fought,
    string LastRefusal,
    string NpcAt = null,
    GauntletAuraInfo Aura = null);

/// <summary>
/// The run's aura as the status command shows it: its tuned radius and ratings (the run's snapshot), and, while the fighter
/// is fallen, the whole seconds until it returns (null while it stands).
/// </summary>
public sealed record GauntletAuraInfo(double Radius, int DamageRating, int ResistRating, int? FallenSecondsLeft = null);

// ---- the world seams (Task 7) ---------------------------------------------------------------------------------------

/// <summary>
/// Everything a run does to the live world, behind one seam so the run is testable with no landblock. The live
/// implementation is <see cref="LiveGauntletWorld"/>. Every method is called on the world thread, except that an action
/// handed to <see cref="Enqueue"/> runs later on the arena landblock's thread.
/// </summary>
internal interface IGauntletWorld
{
    /// <summary>Allocates an instance and loads the arena copy with the run's hold attached. False, with a reason, when it did not load.</summary>
    bool OpenCopy(GauntletRun run, out uint instance, out string failure);

    /// <summary>True while the copy's primary is still registered under the instance.</summary>
    bool IsCopyLoaded(uint instance);

    /// <summary>Queues <paramref name="action"/> on the landblock that holds the fight circle's centre. False when that landblock is gone.</summary>
    bool Enqueue(GauntletRun run, Action action);

    /// <summary>The members of the run's allegiance standing in the copy now (alive, dead or held).</summary>
    IReadOnlyList<GauntletOccupant> Occupants(GauntletRun run);

    /// <summary>Sends one line to everyone in the copy.</summary>
    void TellInside(GauntletRun run, string text);

    /// <summary>Sends one line to every online member of an allegiance (AvatarAnnouncer.ToAllegiance). Returns the guids it reached.</summary>
    ICollection<uint> ToAllegiance(uint monarchGuid, string text);

    /// <summary>Sends one line to everyone in the copy except the characters in <paramref name="except"/>.</summary>
    void TellInsideExcept(GauntletRun run, string text, ICollection<uint> except);

    /// <summary>Sends one line to one character, if online.</summary>
    void TellCharacter(uint characterGuid, string text);

    /// <summary>Sends everyone in the copy to their stamp (lifestone fallback) with <paramref name="line"/>, then lets the copy unload.</summary>
    void EvictAndRelease(GauntletRun run, string line);
}

/// <summary>The history writes (the shard DAO through SerializedShardDatabase). Each row handed in is the caller's own copy.</summary>
internal interface IGauntletStore
{
    void InsertRun(AllegianceGauntletRun row, Action<bool, uint> callback);
    void SaveRunEnd(AllegianceGauntletRun row);
    void SaveWave(AllegianceGauntletWave row);

    /// <summary>The synchronous startup close (end_Reason 2 Restart). False when the write failed.</summary>
    bool CloseOpenRuns(DateTime nowUtc, out int closed);
}

/// <summary>Creates and places wave creatures (GauntletSpawner). Both calls run on the arena landblock's thread.</summary>
internal interface IGauntletSpawnSink
{
    /// <summary>The boss health floor's pack term (GauntletSpawner.ObservedMaxNonBoss). Called before a boss spawns.</summary>
    uint ObservedMaxNonBoss(GauntletWavePlan wave);

    /// <summary>One planned spawn, placed and woken; null when placement failed.</summary>
    IGauntletSpawnedCreature Spawn(GauntletWavePlan wave, PortalWaveSpawn spawn, uint observedMaxNonBoss, GauntletRun owner);
}

/// <summary>A placed wave creature, as the run sees it. Every member is called on the arena landblock's thread.</summary>
internal interface IGauntletSpawnedCreature
{
    uint Guid { get; }

    /// <summary>The max health it entered the world with (P_GauntletSpawnMaxHealth): its score denominator.</summary>
    uint SpawnMax { get; }

    bool IsAlive { get; }

    /// <summary>The damage its history totals, every attacker summed (the GetLiveWaveFraction "dealt").</summary>
    double SampleDealt();

    /// <summary>A value that changes whenever it takes or deals damage (health and its attack clock).</summary>
    long ActivityStamp();

    /// <summary>Wakes it at the nearest member when it is idle (GauntletSpawner.EngageEnemy).</summary>
    void Engage();

    /// <summary>Moves it to <paramref name="centre"/> (the straggler pull). False when it cannot be moved there.</summary>
    bool PullTo(Position centre);

    void Destroy();
}

/// <summary>The Favor side of a run. The purchase reserved the price; the run commits or releases it.</summary>
internal interface IGauntletFavor
{
    void Commit(uint avatarId, ulong price);
    void Release(uint avatarId, ulong price);

    /// <summary>Returns a price that was already committed (the admin refund). False when that is not possible.</summary>
    bool RefundCommitted(uint avatarId, ulong price);
}

/// <summary>The record cache (GauntletRecords, Task 13). The cooldown and run count go ONLY through <see cref="NoteRunCommitted"/>.</summary>
internal interface IGauntletRecordSink
{
    long SnapshotTop();
    bool Bank(uint avatarId, long scoreCenti, int participants, uint? runId, DateTime nowUtc);
    bool TryAnnounce(uint runId, uint avatarId, uint? monarchGuid, long scoreCenti, long snapshotTop, int fought);
    void NoteRunCommitted(uint avatarId, DateTime nowUtc);
    DateTime? LastRunAt(uint avatarId);
}

/// <summary>
/// Plans one wave (GauntletWavePlanner.Plan) under <paramref name="tuning"/>, the run's own snapshot taken at TryStart, so
/// one run plans every wave with one tuning. May throw; the run treats a throw as a refusal.
/// </summary>
internal delegate GauntletWavePlan GauntletWavePlanSource(GauntletTuning tuning, GauntletSiteDef site, int wave, int headcount, int runSeed, string label);

// ---- hooks for later tasks ------------------------------------------------------------------------------------------

/// <summary>
/// Task 8, the Avatar fighter. Every call is made on the world thread, outside the run's lock. The run works with the
/// no-op default: no fighter fights, nothing else changes. Report a fighter death with <see cref="GauntletRun.OnFighterDied"/>;
/// the run then counts it, tells the arena, and calls <see cref="Respawn"/> after allegiance_gauntlet_fighter_respawn_seconds.
/// </summary>
internal interface IGauntletFighterHooks
{
    /// <summary>The copy loaded and Staging began: place the fighter at <see cref="GauntletRun.CentrePosition"/>.</summary>
    void Spawn(GauntletRun run);

    /// <summary>
    /// The fighter's stats are fixed once, at the run's start, from the first plan it is given (wave 1's); a later call never
    /// changes them (the hooks only clear buffs and keep the shown level at the Avatar's own level). <paramref name="wave"/> is
    /// the wave's plan (null when that plan was refused). The run calls it with
    /// <paramref name="clearBuffs"/> true at each Intermission start, from the next wave's plan, so buffs cast during the break
    /// carry into the wave; and with false when a wave's stats arrive without a break (wave 1 at the end of Staging, or a
    /// refused wave whose re-plan succeeded mid-wave), so those never wipe buffs.
    /// </summary>
    void Restamp(GauntletRun run, GauntletWavePlan wave, bool clearBuffs);

    /// <summary>The respawn hold after a fighter death is over: bring it back at the centre at full health.</summary>
    void Respawn(GauntletRun run);

    /// <summary>The run is Ending or closing: take the fighter out of the world.</summary>
    void Remove(GauntletRun run);

    /// <summary>
    /// A periodic check while the copy is live: a fighter that left the world WITHOUT dying (destroyed by a heartbeat or an
    /// unload, so no death was reported and no respawn is due) is replaced at once. A dead fighter waits for its timer.
    /// </summary>
    void Ensure(GauntletRun run);

    /// <summary>A wave was cleared (world thread, after the run's lock): the fighter's wave-clear line. A no-op by default.</summary>
    void WaveCleared(GauntletRun run, int wave) { }
}

/// <summary>
/// Task 9, the death hold. Report a member's death with <see cref="GauntletRun.OnMemberDied"/> (the per-wave count). The
/// pen and the entry points are on the run (<see cref="GauntletRun.PenPosition"/>, <see cref="GauntletRun.NextEntryPoint"/>).
/// </summary>
internal interface IGauntletDeathHooks
{
    /// <summary>Seconds left on a member's hold, 0 when not held (the gate's refusal reads it).</summary>
    double HoldSecondsLeft(GauntletRun run, Player player);

    /// <summary>The run closed or aborted: end every hold it owns (the eviction follows).</summary>
    void ReleaseAll(GauntletRun run);

    /// <summary>
    /// The run stopped fighting (Rewarding began, caches placed): end every hold now and send each held player who is online
    /// in the arena to <paramref name="caches"/>, the cache area. Anyone else is just dropped.
    /// </summary>
    void ReleaseToCaches(GauntletRun run, Position caches);
}

/// <summary>Task 12, the caches and claims. World thread, outside the run's lock.</summary>
internal interface IGauntletRewardHooks
{
    /// <summary>The run entered Rewarding: place the caches. Returns the claim window in seconds (the profile's claimWindowSeconds).</summary>
    int BeginRewarding(GauntletRun run);

    /// <summary>The run lost its instance: pay eligible online members straight to their packs (the only direct-delivery path).</summary>
    void DeliverDirect(GauntletRun run);

    /// <summary>The claim window is over or the run closed some other way: remove the caches.</summary>
    void EndRewarding(GauntletRun run);
}

/// <summary>
/// Task 10, the gate. Place it beside the selling NPC once the copy has loaded and bind it to the run
/// (GauntletGate.Register); remove and unbind it when the run closes. The run supplies the gate's needs:
/// <see cref="GauntletRun.State"/>, <see cref="GauntletRun.Admits"/>, <see cref="GauntletRun.NextEntryPoint"/> and
/// <see cref="GauntletRun.DeathHoldSecondsLeft"/>.
/// </summary>
internal interface IGauntletGateHooks
{
    void Place(GauntletRun run, Position npcLocation);
    void Remove(GauntletRun run);

    /// <summary>Called every few seconds while the run's hold admits players: place the gate again if it is gone.</summary>
    void Ensure(GauntletRun run, Position npcLocation);
}

/// <summary>The ring of markers on the fight circle. World thread, outside the run's lock.</summary>
internal interface IGauntletRingHooks
{
    /// <summary>The copy loaded and Staging began: mark the fight circle.</summary>
    void Place(GauntletRun run);

    /// <summary>The run closed or aborted: take the markers out.</summary>
    void Remove(GauntletRun run);
}

/// <summary>The no-op defaults: the run reaches Rewarding and then Closed on its own timers with all of them.</summary>
internal sealed class NoGauntletHooks : IGauntletFighterHooks, IGauntletDeathHooks, IGauntletRewardHooks, IGauntletGateHooks, IGauntletRingHooks
{
    public static readonly NoGauntletHooks Instance = new NoGauntletHooks();

    /// <summary>The claim window when no reward hook says otherwise: rewards.json's standard claimWindowSeconds.</summary>
    public const int DefaultClaimWindowSeconds = 300;

    public void Spawn(GauntletRun run) { }
    public void Restamp(GauntletRun run, GauntletWavePlan wave, bool clearBuffs) { }
    public void Respawn(GauntletRun run) { }
    void IGauntletFighterHooks.Remove(GauntletRun run) { }
    public void Ensure(GauntletRun run) { }

    public double HoldSecondsLeft(GauntletRun run, Player player) => 0;
    public void ReleaseAll(GauntletRun run) { }
    public void ReleaseToCaches(GauntletRun run, Position caches) { }

    public int BeginRewarding(GauntletRun run) => DefaultClaimWindowSeconds;
    public void DeliverDirect(GauntletRun run) { }
    public void EndRewarding(GauntletRun run) { }

    public void Place(GauntletRun run, Position npcLocation) { }
    void IGauntletGateHooks.Remove(GauntletRun run) { }
    public void Ensure(GauntletRun run, Position npcLocation) { }

    void IGauntletRingHooks.Place(GauntletRun run) { }
    void IGauntletRingHooks.Remove(GauntletRun run) { }
}

/// <summary>
/// The hook set a manager hands its runs. Tasks 8, 9, 10 and 12 install theirs on <see cref="GauntletRunHooks.Live"/> at
/// startup (before the first run starts); every slot defaults to <see cref="NoGauntletHooks"/>.
/// </summary>
internal sealed class GauntletRunHooks
{
    public IGauntletFighterHooks Fighter { get; set; } = NoGauntletHooks.Instance;
    public IGauntletDeathHooks Death { get; set; } = NoGauntletHooks.Instance;
    public IGauntletRewardHooks Rewards { get; set; } = NoGauntletHooks.Instance;
    public IGauntletGateHooks Gate { get; set; } = NoGauntletHooks.Instance;
    public IGauntletRingHooks Ring { get; set; } = NoGauntletHooks.Instance;

    /// <summary>The set the live manager uses.</summary>
    public static GauntletRunHooks Live { get; } = new GauntletRunHooks();
}

/// <summary>Every collaborator of a run and its manager. Built once by the manager; tests build their own.</summary>
internal sealed class GauntletEnvironment
{
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public Func<GauntletTuning> Tuning { get; init; } = () => GauntletTuning.Defaults;
    /// <summary>The loaded arena sites (GauntletSiteStore.Current.Sites in production).</summary>
    public Func<IReadOnlyList<GauntletSiteDef>> Sites { get; init; } = () => GauntletSiteStore.Current.Sites;
    public IGauntletWorld World { get; init; }
    public IGauntletStore Store { get; init; }
    public IGauntletSpawnSink Spawner { get; init; }
    public IGauntletFavor Favor { get; init; }
    public IGauntletRecordSink Records { get; init; }
    public GauntletWavePlanSource Planner { get; init; }
    public GauntletRunHooks Hooks { get; init; } = new GauntletRunHooks();

    /// <summary>The live membership lookup for an Avatar's run (AvatarRights.IsMemberForAdmission). The run wraps it so it fails closed.</summary>
    public Func<uint, Func<Player, bool>> MemberLookup { get; init; } = _ => _ => false;

    /// <summary>The random source for site picks and run seeds.</summary>
    public Random Random { get; init; } = new Random();
}
