using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The v1 <see cref="IMatchSpaceProvider"/>: one private realm-1 ephemeral copy of an arena landblock per
    /// match (Docs/Pvp/DESIGN.md "Match spaces"). INERT until the coordinator (PR C3) constructs and calls it.
    ///
    /// <para/>
    /// <b>Threading.</b> Call every method on the WORLD thread, which is where the coordinator ticks.
    ///   - <see cref="Allocate"/> creates the instance through RealmManager.GetNewEphemeralLandblock, which runs
    ///     LandblockManager.GetLandblock. That call takes LandblockManager's own lock and already runs on the world
    ///     thread for every login (WorldManager.DoPlayerEnterWorld, queued on the world ActionQueue, reaches
    ///     LandblockManager.AddObject -> GetLandblock), so it is safe there. It is also synchronous in cost: the
    ///     Landblock constructor reads the cell dat and Init() runs PhysicsLandblock.PostInit on the calling thread.
    ///   - Release reads the landblock's object list (GetAllWorldObjectsForDiagnostics) the same way
    ///     ThreadDungeonManager.EndRun does, which is a world-thread read.
    ///
    /// <para/>
    /// <b>Readiness.</b> When Allocate returns, the instance is REGISTERED (GetLandblock registers it in the
    /// ephemeral instance registry inside its write lock, before Init runs), so it already resolves for
    /// InstanceRouting and a teleport into it will be accepted. It is not yet POPULATED: Init hands
    /// CreateWorldObjects to Task.Run, which queues the action that sets CreateWorldObjectsCompleted onto the
    /// landblock's own action queue, so the flag flips on a later landblock tick. An arena copy has nothing to
    /// populate in realm 1, so this is normally the next tick or two, but it is never synchronous. The caller
    /// polls <see cref="GetReadiness"/> once per tick (it never blocks) and starts the countdown on Ready. A
    /// teleport sent while Loading is still safe - Player.OnTeleportComplete holds the player until the flag is
    /// set - and a space stuck in Loading means the population task faulted, which the staging timeout ends.
    ///
    /// <para/>
    /// <b>Lifecycle.</b> Nothing new. A refused or half-built instance goes straight to
    /// LandblockManager.AddToDestructionQueue, exactly as Portal and ThreadDungeonManager.TryStart dispose of a
    /// non-dungeon copy. Release goes through ThreadDungeonManager.QueueCopyForDestructionWhenEmpty, the tail
    /// of EndRun: queued now if empty, otherwise watched and queued by the Thread tick once the last player has
    /// left. Release does NOT evict; the coordinator returns every participant first. Independently of all of
    /// this, the landblock heartbeat unloads any copy that has stood empty for Landblock.UnloadInterval.
    /// </summary>
    public sealed class EphemeralMatchSpaceProvider : IMatchSpaceProvider
    {
        private readonly MatchSpaceLifecycle<Player, Landblock> core = new MatchSpaceLifecycle<Player, Landblock>(new LiveMatchInstanceHost());

        public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<Player> admitted) => core.Allocate(map, admitted);

        public MatchSpaceReadiness GetReadiness(MatchSpace s) => core.GetReadiness(s);

        public void Release(MatchSpace s) => core.Release(s);

        /// <summary>The live-server side of the seam: every call here touches RealmManager / LandblockManager.</summary>
        private sealed class LiveMatchInstanceHost : IMatchInstanceHost<Player, Landblock>
        {
            public bool IsRealmRegistered(ushort realmId) => RealmManager.GetRealm(realmId) != null;

            public Landblock CreateInstance(uint landblockId, Player owner, ushort realmId) =>
                RealmManager.GetNewEphemeralLandblock(new LandblockId(landblockId << 16), owner, realmId, openToFellowship: false);

            public uint InstanceOf(Landblock landblock) => landblock.Instance;

            public uint LandblockIdOf(Landblock landblock) => landblock.Id.Landblock;

            public bool IsDungeon(Landblock landblock) => landblock.IsDungeon;

            public bool Admit(Landblock landblock, Player player)
            {
                var realm = landblock.InnerRealmInfo;

                if (realm == null)
                    return false;

                realm.Admit(player);
                return true;
            }

            public InstanceRejection CheckArrival(Position destination, Player player)
            {
                destination.ValidateInstanceDestination(player, out var rejection);
                return rejection;
            }

            public Landblock GetLive(uint instance) => LandblockManager.GetEphemeralLandblock(instance);

            public bool CreateCompleted(Landblock landblock) => landblock.CreateWorldObjectsCompleted;

            public bool HasPlayers(Landblock landblock) => landblock.GetAllWorldObjectsForDiagnostics().OfType<Player>().Any();

            public void Discard(Landblock landblock) => LandblockManager.AddToDestructionQueue(landblock);

            public void QueueForDestructionWhenEmpty(Landblock landblock, bool occupied) =>
                ThreadDungeonManager.QueueCopyForDestructionWhenEmpty(landblock, occupied);

            public uint GuidOf(Player player) => player.Guid.Full;

            public string NameOf(Player player) => player.Name;
        }
    }

    /// <summary>
    /// The seam between <see cref="MatchSpaceLifecycle{TPlayer, TLandblock}"/> and the live server. Generic so the
    /// lifecycle's decisions can be driven from unit tests with plain stand-ins, since neither a Player nor a
    /// Landblock can be built without the client dats and a world database.
    /// </summary>
    internal interface IMatchInstanceHost<TPlayer, TLandblock>
        where TPlayer : class
        where TLandblock : class
    {
        bool IsRealmRegistered(ushort realmId);

        /// <summary>Creates a private copy (never open to the owner's fellowship). May return null or throw.</summary>
        TLandblock CreateInstance(uint landblockId, TPlayer owner, ushort realmId);

        uint InstanceOf(TLandblock landblock);

        uint LandblockIdOf(TLandblock landblock);

        bool IsDungeon(TLandblock landblock);

        /// <summary>Admits a player to the copy's EphemeralRealm. False if the copy has no realm info.</summary>
        bool Admit(TLandblock landblock, TPlayer player);

        /// <summary>The check Player.Teleport runs on arrival (InstanceRouting.ValidateInstanceDestination).</summary>
        InstanceRejection CheckArrival(Position destination, TPlayer player);

        /// <summary>What this instance id resolves to right now, or null.</summary>
        TLandblock GetLive(uint instance);

        bool CreateCompleted(TLandblock landblock);

        bool HasPlayers(TLandblock landblock);

        /// <summary>Queues a copy nobody was ever sent into for destruction.</summary>
        void Discard(TLandblock landblock);

        /// <summary>The shared Thread/arena release path (ThreadDungeonManager.QueueCopyForDestructionWhenEmpty).</summary>
        void QueueForDestructionWhenEmpty(TLandblock landblock, bool occupied);

        uint GuidOf(TPlayer player);

        string NameOf(TPlayer player);
    }

    /// <summary>
    /// Every decision <see cref="EphemeralMatchSpaceProvider"/> makes, behind <see cref="IMatchInstanceHost{TPlayer, TLandblock}"/>.
    /// See the provider for threading. Nothing here throws into the caller for a refusal.
    /// </summary>
    internal sealed class MatchSpaceLifecycle<TPlayer, TLandblock>
        where TPlayer : class
        where TLandblock : class
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IMatchInstanceHost<TPlayer, TLandblock> host;

        public MatchSpaceLifecycle(IMatchInstanceHost<TPlayer, TLandblock> host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Creates and validates the space. Order matters and mirrors Portal's instanced branch: every check that
        /// can refuse runs BEFORE the caller commits anything (stamping exits, flipping PK status), and a refusal
        /// after the copy exists disposes of it, so a failed allocation never leaves an instance behind.
        /// </summary>
        public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<TPlayer> admitted)
        {
            if (map == null)
                return Refuse(MatchSpaceFailure.NoMap, "no map");

            if (admitted == null || admitted.Count == 0)
                return Refuse(MatchSpaceFailure.NoParticipants, $"map {map.MapKey}: no participants");

            if (admitted.Any(p => p == null))
                return Refuse(MatchSpaceFailure.NullParticipant, $"map {map.MapKey}: a participant is null");

            var probe = FirstSpawnPoint(map);

            if (probe == null)
                return Refuse(MatchSpaceFailure.NoSpawnPoints, $"map {map.MapKey}: no spawn points");

            var shapeProblem = MapShapeProblem(map);

            if (shapeProblem != null)
                return Refuse(MatchSpaceFailure.InvalidMap, $"map {map.MapKey}: {shapeProblem}");

            if (!host.IsRealmRegistered(map.RealmId))
                return Refuse(MatchSpaceFailure.RealmNotRegistered, $"map {map.MapKey}: realm {map.RealmId} is not registered");

            var owner = admitted[0];
            TLandblock landblock;

            try
            {
                landblock = host.CreateInstance(map.LandblockId, owner, map.RealmId);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match space: creating an instance of 0x{map.LandblockId:X4} (map {map.MapKey}) threw", ex);
                return Refuse(MatchSpaceFailure.CreateFailed, $"map {map.MapKey}: instance creation threw {ex.GetType().Name}");
            }

            if (landblock == null)
                return Refuse(MatchSpaceFailure.CreateFailed, $"map {map.MapKey}: instance creation returned nothing");

            // From here on the copy exists, so every refusal disposes of it - including an unexpected throw from any
            // host call below, which must never escape into the coordinator's tick.
            try
            {
                return ValidateCreated(map, admitted, owner, landblock, probe);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match space: validating a new instance of 0x{map.LandblockId:X4} (map {map.MapKey}) threw", ex);
                return DisposeAndRefuse(landblock, MatchSpaceFailure.ValidationFailed, $"map {map.MapKey}: validation threw {ex.GetType().Name}");
            }
        }

        private MatchSpaceAllocation ValidateCreated(ArenaMap map, IReadOnlyList<TPlayer> admitted, TPlayer owner, TLandblock landblock, PvpSpawnPoint probe)
        {
            var instance = host.InstanceOf(landblock);

            if (host.LandblockIdOf(landblock) != map.LandblockId)
                return DisposeAndRefuse(landblock, MatchSpaceFailure.LandblockMismatch, $"map {map.MapKey}: asked for 0x{map.LandblockId:X4}, got 0x{host.LandblockIdOf(landblock):X4}");

            if (!host.IsDungeon(landblock))
                return DisposeAndRefuse(landblock, MatchSpaceFailure.NotADungeon, $"map {map.MapKey}: landblock 0x{map.LandblockId:X4} is not a dungeon");

            Position.ParseInstanceID(instance, out var isEphemeral, out var realmId, out _);

            if (!isEphemeral || realmId != map.RealmId)
                return DisposeAndRefuse(landblock, MatchSpaceFailure.InstanceMismatch, $"map {map.MapKey}: instance 0x{instance:X8} is not an ephemeral realm-{map.RealmId} id");

            foreach (var player in admitted)
            {
                if (!host.Admit(landblock, player))
                    return DisposeAndRefuse(landblock, MatchSpaceFailure.AdmitFailed, $"map {map.MapKey}: instance 0x{instance:X8} has no realm info to admit {host.NameOf(player)}");
            }

            // The same check Player.Teleport will run, per participant, against a real destination in this copy.
            var destination = ArenaSpawnPosition.Build(map, probe, instance);

            foreach (var player in admitted)
            {
                var rejection = host.CheckArrival(destination, player);

                if (rejection != InstanceRejection.None)
                    return DisposeAndRefuse(landblock, MatchSpaceFailure.ParticipantRefused, $"map {map.MapKey}: instance 0x{instance:X8} refuses {host.NameOf(player)} ({rejection})");
            }

            var space = new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, instance, host.GuidOf(owner)) { Handle = landblock };

            log.Info($"[PVP] match space allocated: map {map.MapKey} instance 0x{instance:X8}, {admitted.Count} admitted, owner 0x{space.OwnerGuid:X8}");

            return MatchSpaceAllocation.Ok(space);
        }

        /// <summary>Non-blocking; see <see cref="MatchSpaceReadiness"/>.</summary>
        public MatchSpaceReadiness GetReadiness(MatchSpace space)
        {
            var landblock = ResolveLive(space);

            if (landblock == null)
                return MatchSpaceReadiness.Gone;

            return host.CreateCompleted(landblock) ? MatchSpaceReadiness.Ready : MatchSpaceReadiness.Loading;
        }

        /// <summary>
        /// Hands the space to the shared destruction path. A space whose instance already unloaded, or whose id now
        /// resolves to a different copy, is left alone. Safe to call more than once: the watch set is keyed by
        /// instance id, and a repeat queue of a copy already waiting is deduplicated by the unload throttle.
        /// </summary>
        public void Release(MatchSpace space)
        {
            var landblock = ResolveLive(space);

            if (landblock == null)
                return;

            bool occupied;

            try
            {
                occupied = host.HasPlayers(landblock);

                host.QueueForDestructionWhenEmpty(landblock, occupied);
            }
            catch (Exception ex)
            {
                // Left to the landblock heartbeat, which unloads an empty copy after Landblock.UnloadInterval anyway.
                log.Error($"[PVP] match space: releasing map {space.MapKey} instance 0x{space.Instance:X8} threw; leaving it to the idle unload", ex);
                return;
            }

            log.Info($"[PVP] match space released: map {space.MapKey} instance 0x{space.Instance:X8} ({(occupied ? "occupied, queued once empty" : "empty, queued now")})");
        }

        private TLandblock ResolveLive(MatchSpace space)
        {
            if (space?.Handle is not TLandblock handle)
                return null;

            var live = host.GetLive(space.Instance);

            return ReferenceEquals(live, handle) ? handle : null;
        }

        /// <summary>
        /// The map-shape checks ArenaSpawnPosition.Build would otherwise throw for, run before anything is created
        /// so a bad map is a plain refusal. Null when the shape is fine.
        /// </summary>
        internal static string MapShapeProblem(ArenaMap map)
        {
            if (map.LandblockId == 0 || map.LandblockId > 0xFFFF)
                return $"LandblockId 0x{map.LandblockId:X} is not a 16-bit landblock number";

            foreach (var set in map.SpawnSets.Values)
            {
                if (set == null)
                    continue;

                foreach (var point in set)
                {
                    if (point == null)
                        return "a spawn point is null";

                    if (point.CellLow < 0x0100)
                        return $"spawn {point.Label} cell 0x{point.CellLow:X4} is not an EnvCell";
                }
            }

            return null;
        }

        private static PvpSpawnPoint FirstSpawnPoint(ArenaMap map)
        {
            if (map.SpawnSets == null)
                return null;

            foreach (var set in map.SpawnSets.Values)
            {
                if (set != null && set.Count > 0)
                    return set[0];
            }

            return null;
        }

        private MatchSpaceAllocation DisposeAndRefuse(TLandblock landblock, MatchSpaceFailure failure, string detail)
        {
            try
            {
                host.Discard(landblock);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] match space: disposing of a refused instance threw ({detail})", ex);
            }

            return Refuse(failure, detail);
        }

        private static MatchSpaceAllocation Refuse(MatchSpaceFailure failure, string detail)
        {
            log.Warn($"[PVP] match space refused: {failure} - {detail}");
            return MatchSpaceAllocation.Fail(failure, detail);
        }
    }
}
