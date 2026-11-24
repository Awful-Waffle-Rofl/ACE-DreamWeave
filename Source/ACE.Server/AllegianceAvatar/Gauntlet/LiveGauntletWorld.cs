using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>The production collaborators of the Gauntlet manager and its runs.</summary>
internal static class LiveGauntletEnvironment
{
    public static GauntletEnvironment Build()
    {
        var hooks = GauntletRunHooks.Live;

        // The gate is placed by the run's lifecycle; Task 10 owns what a use of it does (GauntletGate).
        if (hooks.Gate is NoGauntletHooks)
            hooks.Gate = new LiveGauntletGateHooks();

        // Task 9, the death hold: the same instance Player.Die latches through, so the gate and the hold read one registry.
        if (hooks.Death is NoGauntletHooks)
            hooks.Death = LiveGauntletDeathHooks.Instance;

        // The caches, the claim and the direct delivery (Task 12).
        if (hooks.Rewards is NoGauntletHooks)
            hooks.Rewards = new LiveGauntletRewardHooks();

        // The Avatar fighter (Task 8): spawned at Staging, restamped at each wave start, respawned, removed.
        if (hooks.Fighter is NoGauntletHooks)
            hooks.Fighter = new LiveGauntletFighterHooks();

        // The ring of wisps that marks the fight circle: placed at Staging, removed at close.
        if (hooks.Ring is NoGauntletHooks)
            hooks.Ring = new LiveGauntletRingHooks();

        return new GauntletEnvironment
        {
            UtcNow = () => DateTime.UtcNow,
            Tuning = GauntletTuning.FromPropertyManager,
            Sites = () => GauntletSiteStore.Current.Sites,
            World = new LiveGauntletWorld(),
            Store = new LiveGauntletStore(),
            Spawner = new LiveGauntletSpawnSink(),
            Favor = new LiveGauntletFavor(),
            Records = new LiveGauntletRecords(),
            Planner = PlannerFor(),
            Hooks = hooks,
            MemberLookup = avatarId => p => AvatarRights.IsMemberForAdmission(p, AvatarManager.Get(avatarId)),
            Random = new Random(),
        };
    }

    /// <summary>
    /// The live wave planner: a GauntletWavePlanner over the tuning the RUN hands it (its TryStart snapshot), never a fresh
    /// PropertyManager read, so a tunable changed mid-run reaches only the next run. The seams are for tests; null is live.
    /// </summary>
    internal static GauntletWavePlanSource PlannerFor(GauntletWavePlanner.PlanBuild build = null, Func<int, GroupScaling> groupFor = null)
        => (tuning, site, wave, headcount, seed, label) => new GauntletWavePlanner(tuning, build, groupFor).Plan(site, wave, headcount, seed, label);
}

/// <summary>The live world: landblock loading, the arena landblock's queue, occupants, messages and eviction.</summary>
internal sealed class LiveGauntletWorld : IGauntletWorld
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public bool OpenCopy(GauntletRun run, out uint instance, out string failure)
    {
        failure = null;
        instance = LandblockManager.RequestNewEphemeralInstanceIDv1(0);

        var landblock = RealmManager.GetNewGauntletOutdoorLandblock(run.Site.Outdoor, run.Hold, instance);

        if (landblock != null && ReferenceEquals(landblock.InnerRealmInfo?.Hold, run.Hold) && !landblock.IsDungeon)
            return true;

        failure = landblock == null ? "the copy did not load" : landblock.IsDungeon ? "the primary is a dungeon landblock" : "an id collision";

        // Clean up only a copy that is this run's own (an id collision's landblock belongs to someone else). The run aborts
        // right after this returns, which releases the member hold, so a watched copy drains and unloads.
        var copy = landblock ?? LandblockManager.GetEphemeralLandblock(instance);

        if (copy != null && ReferenceEquals(copy.InnerRealmInfo?.Hold, run.Hold))
            ThreadDungeonManager.QueueCopyForDestructionWhenEmpty(copy, occupied: true);

        return false;
    }

    public bool IsCopyLoaded(uint instance) => instance != 0 && LandblockManager.GetEphemeralLandblock(instance) != null;

    public bool Enqueue(GauntletRun run, Action action)
    {
        if (run.Instance == 0 || action == null)
            return false;

        var lb = LandblockManager.GetLoadedLandblock(new LandblockId(run.Site.Centre.Cell & 0xFFFF0000u), run.Instance);

        if (lb == null)
            return false;

        lb.EnqueueAction(new ActionEventDelegate(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] {run} landblock action threw", ex);
            }
        }));

        return true;
    }

    /// <summary>
    /// The online players standing in the copy. Called on the world thread, so it must not walk a landblock's object list
    /// (only that landblock's thread may): it reads the online roster under PlayerManager's own lock instead.
    /// </summary>
    private static List<Player> PlayersInside(uint instance)
    {
        if (instance == 0)
            return new List<Player>();

        return PlayersIn(PlayerManager.GetAllOnline(), instance);
    }

    /// <summary>The players of <paramref name="online"/> whose location is in <paramref name="instance"/>. Pure over the list.</summary>
    internal static List<Player> PlayersIn(IEnumerable<Player> online, uint instance)
    {
        var inside = new List<Player>();

        if (online == null || instance == 0)
            return inside;

        foreach (var p in online)
        {
            if (p?.Location is { } loc && loc.Instance == instance && !inside.Contains(p))
                inside.Add(p);
        }

        return inside;
    }

    public IReadOnlyList<GauntletOccupant> Occupants(GauntletRun run)
        => PlayersInside(run.Instance).Where(run.IsMember).Select(p => new GauntletOccupant(p.Guid.Full, p)).ToList();

    public void TellInside(GauntletRun run, string text)
    {
        foreach (var p in PlayersInside(run.Instance))
            p.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    public ICollection<uint> ToAllegiance(uint monarchGuid, string text) => AvatarAnnouncer.ToAllegiance(monarchGuid, text);

    public void TellInsideExcept(GauntletRun run, string text, ICollection<uint> except)
    {
        foreach (var p in PlayersInside(run.Instance))
        {
            if (except == null || !except.Contains(p.Guid.Full))
                p.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }

    public void TellCharacter(uint characterGuid, string text)
        => PlayerManager.GetOnlinePlayer(characterGuid)?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

    public void EvictAndRelease(GauntletRun run, string line)
    {
        var players = PlayersInside(run.Instance);

        foreach (var p in players)
        {
            try
            {
                if (!string.IsNullOrEmpty(line))
                    p.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

                var exit = p.GetPosition(PositionType.EphemeralRealmExitTo) ?? p.Sanctuary ?? p.Instantiation;

                if (exit != null)
                    WorldManager.ThreadSafeTeleport(p, new Position(exit));
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] {run} evicting {p.Name} threw", ex);
            }
        }

        var primary = LandblockManager.GetEphemeralLandblock(run.Instance);

        if (primary != null && ReferenceEquals(primary.InnerRealmInfo?.Hold, run.Hold))
            ThreadDungeonManager.QueueCopyForDestructionWhenEmpty(primary, occupied: players.Count > 0);
    }
}

/// <summary>The history writes through SerializedShardDatabase (fire and forget), and the synchronous startup close.</summary>
internal sealed class LiveGauntletStore : IGauntletStore
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public void InsertRun(AllegianceGauntletRun row, Action<bool, uint> callback) => DatabaseManager.Shard.InsertGauntletRun(row, callback);

    public void SaveRunEnd(AllegianceGauntletRun row) => DatabaseManager.Shard.SaveGauntletRunEnd(row, ok =>
    {
        if (!ok) log.Error($"[GAUNTLET] run row {row.RunId} write failed");
    });

    public void SaveWave(AllegianceGauntletWave row) => DatabaseManager.Shard.SaveGauntletWave(row, ok =>
    {
        if (!ok) log.Error($"[GAUNTLET] wave row {row.RunId}/{row.Wave} write failed");
    });

    public bool CloseOpenRuns(DateTime nowUtc, out int closed) => DatabaseManager.Shard.BaseDatabase.CloseOpenGauntletRuns(nowUtc, out closed);
}

/// <summary>GauntletSpawner on the arena landblock.</summary>
internal sealed class LiveGauntletSpawnSink : IGauntletSpawnSink
{
    public uint ObservedMaxNonBoss(GauntletWavePlan wave)
        => GauntletSpawner.ObservedMaxNonBoss(wave, wcid => WorldObjectFactory.CreateNewWorldObject(wcid) as Creature, $"wave {wave.Wave} pack");

    public IGauntletSpawnedCreature Spawn(GauntletWavePlan wave, PortalWaveSpawn spawn, uint observedMaxNonBoss, GauntletRun owner)
    {
        var point = spawn.Entry?.Point;

        if (point == null)
            return null;

        var lb = LandblockManager.GetLoadedLandblock(new LandblockId(point.Cell & 0xFFFF0000u), owner.Instance);

        if (lb == null)
            return null;

        var creature = GauntletSpawner.Spawn(wave, spawn, observedMaxNonBoss, owner, lb, owner.Instance, owner.Tuning,
            OutdoorPerimeterSettings.FromTunables(), $"run {owner.RunKey} wave {wave.Wave}");

        return creature == null ? null : new LiveSpawnedCreature(creature, owner);
    }
}

/// <summary>A placed Gauntlet creature. Every member runs on its landblock's thread.</summary>
internal sealed class LiveSpawnedCreature : IGauntletSpawnedCreature
{
    private readonly Creature c;
    private readonly GauntletRun owner;

    public LiveSpawnedCreature(Creature creature, GauntletRun owner)
    {
        c = creature;
        this.owner = owner;
    }

    public uint Guid => c.Guid.Full;
    public uint SpawnMax => c.P_GauntletSpawnMaxHealth;
    public bool IsAlive => !c.IsDead && !c.IsDestroyed;

    public double SampleDealt() => c.DamageHistory?.TotalDamage.Values.Sum(i => (double)i.TotalDamage) ?? 0;

    public long ActivityStamp() => unchecked(((long)c.Health.Current << 32) ^ BitConverter.DoubleToInt64Bits(c.NextAttackTime));

    public void Engage() => GauntletSpawner.EngageEnemy(c, owner, out _);

    public bool PullTo(Position centre)
    {
        if (centre == null || c.Location == null || c.Location.InstancedLandblock != centre.InstancedLandblock)
            return false;

        c.FakeTeleport(centre);
        return true;
    }

    public void Destroy()
    {
        if (c.IsDestroyed)
            return;

        c.P_Gauntlet = null;
        c.Destroy();
    }
}

/// <summary>The Avatar's Favor (AvatarManager's reservation API).</summary>
internal sealed class LiveGauntletFavor : IGauntletFavor
{
    public void Commit(uint avatarId, ulong price) => AvatarManager.CommitDebit(avatarId, price);

    public void Release(uint avatarId, ulong price) => AvatarManager.ReleaseReservation(avatarId, price);

    /// <summary>The admin refund of a committed price: credited through AvatarManager.RefundFavor, which respects the Favor cap.</summary>
    public bool RefundCommitted(uint avatarId, ulong price) => AvatarManager.RefundFavor(avatarId, price);
}

/// <summary>GauntletRecords (Task 13).</summary>
internal sealed class LiveGauntletRecords : IGauntletRecordSink
{
    public long SnapshotTop() => GauntletRecords.SnapshotTop();

    public bool Bank(uint avatarId, long scoreCenti, int participants, uint? runId, DateTime nowUtc)
        => GauntletRecords.Bank(avatarId, scoreCenti, participants, runId, nowUtc);

    public bool TryAnnounce(uint runId, uint avatarId, uint? monarchGuid, long scoreCenti, long snapshotTop, int fought)
        => GauntletRecords.TryAnnounce(runId, avatarId, monarchGuid, scoreCenti, snapshotTop, fought);

    public void NoteRunCommitted(uint avatarId, DateTime nowUtc) => GauntletRecords.NoteRunCommitted(avatarId, nowUtc);

    public DateTime? LastRunAt(uint avatarId) => GauntletRecords.LastRunAt(avatarId);
}

/// <summary>
/// Places the Gauntlet Gate (wcid <see cref="GateWcid"/>, Task 14 content) beside the selling NPC once the copy has loaded,
/// binds it to its run (<see cref="GauntletGate.Register"/>), and removes it when the run closes.
/// </summary>
internal sealed class LiveGauntletGateHooks : IGauntletGateHooks
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public const uint GateWcid = 1008505;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, WorldObject> gates = new System.Collections.Concurrent.ConcurrentDictionary<uint, WorldObject>();

    /// <summary>Test seam: takes a gate out of the world.</summary>
    internal Action<WorldObject> DestroyGate { get; init; } = g => g.Destroy();

    /// <summary>True while a gate is bound for <paramref name="runKey"/>.</summary>
    internal bool HoldsGate(uint runKey) => gates.ContainsKey(runKey);

    /// <summary>
    /// Binds a placed gate to its run (the gate's landblock thread). Remove runs on the world thread and finds nothing to
    /// remove while the gate is not yet in the map, so the run's state is checked again AFTER the gate is published: a run
    /// that went terminal in between takes its gate back out here. False when the gate was not kept.
    /// </summary>
    internal bool TryBind(GauntletRun run, WorldObject gate)
    {
        gates[run.RunKey] = gate;
        GauntletGate.Register(gate.Guid.Full, run);

        if (!run.IsTerminal)
            return true;

        // Only our own entry: a later placement for the same run key is not ours to remove.
        if (((System.Collections.Generic.ICollection<KeyValuePair<uint, WorldObject>>)gates).Remove(new KeyValuePair<uint, WorldObject>(run.RunKey, gate)))
            GauntletGate.Unregister(gate.Guid.Full);

        try { DestroyGate(gate); }
        catch (Exception ex) { log.Error($"[GAUNTLET] {run}: destroying a late gate threw", ex); }

        return false;
    }

    /// <summary>Test seam: runs an action on the landblock holding <paramref name="at"/>. False when that landblock is not loaded.</summary>
    internal Func<Position, Action, bool> EnqueueAt { get; init; } = DefaultEnqueueAt;

    /// <summary>Test seam: builds an unplaced gate (null when the weenie is missing).</summary>
    internal Func<WorldObject> CreateGate { get; init; } = () => WorldObjectFactory.CreateNewWorldObject(GateWcid);

    /// <summary>Test seam: puts the gate into the world beside the NPC. False when it could not enter.</summary>
    internal Func<WorldObject, Position, bool> PlaceGate { get; init; } = DefaultPlaceGate;

    /// <summary>Run keys whose placement is queued and not yet finished, so a re-place never doubles a gate.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> placing = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>();

    private static bool DefaultEnqueueAt(Position at, Action action)
    {
        var lb = LandblockManager.GetLoadedLandblock(at.LandblockId, at.Instance);

        if (lb == null)
            return false;

        lb.EnqueueAction(new ActionEventDelegate(action));
        return true;
    }

    /// <summary>Metres in front of the NPC the gate is first tried at (the summon ladder then tries closer, then the NPC's spot).</summary>
    public const float GateDistance = 1.5f;

    /// <summary>
    /// The summon placer's ladder (MuleSummonHandler.TryPlace, as the Avatar's own summon uses it): in front of the NPC at
    /// <see cref="GateDistance"/>, then closer, then the NPC's own position, each rung with its cell recomputed from the
    /// moved point so a rung across a landblock line lands in the right landblock.
    /// </summary>
    private static bool DefaultPlaceGate(WorldObject gate, Position npcLocation)
    {
        if (MuleSummonHandler.TryPlace(gate, npcLocation, GateDistance, PlaceRung))
            return true;

        log.Warn($"[GAUNTLET] the gate could not enter the world near {npcLocation.ToLOCString()}");
        return false;
    }

    private static bool PlaceRung(WorldObject wo)
    {
        wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
        return wo.EnterWorld();
    }

    public void Place(GauntletRun run, Position npcLocation)
    {
        if (npcLocation == null)
        {
            log.Warn($"[GAUNTLET] {run}: no NPC location, no gate placed");
            return;
        }

        if (!placing.TryAdd(run.RunKey, 0))
            return;   // a placement for this run is already on its way

        bool queued;

        try
        {
            queued = EnqueueAt(npcLocation, () =>
            {
                try
                {
                    PlaceNow(run, npcLocation);
                }
                catch (Exception ex)
                {
                    log.Error($"[GAUNTLET] {run}: placing the gate threw", ex);
                }
                finally
                {
                    placing.TryRemove(run.RunKey, out _);
                }
            });
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {run}: queueing the gate threw", ex);
            queued = false;
        }

        if (!queued)
        {
            placing.TryRemove(run.RunKey, out _);
            log.Warn($"[GAUNTLET] {run}: the NPC's landblock is not loaded, no gate placed");
        }
    }

    /// <summary>The gate's landblock thread: create, place, never rot, bind.</summary>
    private void PlaceNow(GauntletRun run, Position npcLocation)
    {
        if (run.IsTerminal)
            return;

        var gate = CreateGate();

        if (gate == null)
        {
            log.Error($"[GAUNTLET] {run}: gate weenie {GateWcid} is missing, no gate placed");
            return;
        }

        if (!PlaceGate(gate, npcLocation))
        {
            SafeDestroy(run, gate);
            return;
        }

        // A hand-placed object with a dynamic guid and no generator rots on the landblock heartbeat (IsDecayable).
        gate.TimeToRot = -1;

        if (run.IsTerminal)
        {
            SafeDestroy(run, gate);
            return;
        }

        TryBind(run, gate);
    }

    /// <summary>
    /// The run's periodic check while its hold admits players: a gate that is gone (destroyed, or never placed because the
    /// NPC's landblock was not loaded) is placed again. Nothing happens while a placement is pending or the gate stands.
    /// </summary>
    public void Ensure(GauntletRun run, Position npcLocation)
    {
        if (run == null || run.IsTerminal || !GauntletArenaHold.AdmitsIn(run.State) || placing.ContainsKey(run.RunKey))
            return;

        if (gates.TryGetValue(run.RunKey, out var gate))
        {
            if (!gate.IsDestroyed)
                return;

            if (((System.Collections.Generic.ICollection<KeyValuePair<uint, WorldObject>>)gates).Remove(new KeyValuePair<uint, WorldObject>(run.RunKey, gate)))
                GauntletGate.Unregister(gate.Guid.Full);

            log.Warn($"[GAUNTLET] {run}: the gate 0x{gate.Guid.Full:X8} left the world; placing it again");
        }

        Place(run, npcLocation);
    }

    private void SafeDestroy(GauntletRun run, WorldObject gate)
    {
        try { DestroyGate(gate); }
        catch (Exception ex) { log.Error($"[GAUNTLET] {run}: destroying a gate threw", ex); }
    }

    public void Remove(GauntletRun run)
    {
        if (!gates.TryRemove(run.RunKey, out var gate))
            return;

        GauntletGate.Unregister(gate.Guid.Full);

        var lb = gate.CurrentLandblock;

        if (lb == null)
        {
            SafeDestroy(run, gate);
            return;
        }

        lb.EnqueueAction(new ActionEventDelegate(() => SafeDestroy(run, gate)));
    }
}
