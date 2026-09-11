using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common.Performance;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Physics.Common;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Entity
{
    /// <summary>
    /// the gist of a landblock is that, generally, everything on it publishes
    /// to and subscribes to everything else in the landblock.  x/y in an outdoor
    /// landblock goes from 0 to 192.  "indoor" (dungeon) landblocks have no
    /// functional limit as players can't freely roam in/out of them
    /// </summary>
    public class Landblock : IActor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static float AdjacencyLoadRange { get; } = 96f;
        public static float OutdoorChatRange { get; } = 75f;
        public static float IndoorChatRange { get; } = 25f;
        public static float MaxXY { get; } = 192f;
        public static float MaxObjectRange { get; } = 192f;
        public static float MaxObjectGhostRange { get; } = 250f;


        public LandblockId Id { get; }

        /// <summary>
        /// The instance this landblock copy belongs to, laid out as
        /// [1 bit ephemeral][15 bits realmId][16 bits shortInstanceId].
        /// Always 0 in Phase 1 of the instancing port (base world only).
        /// </summary>
        public uint Instance { get; }

        /// <summary>
        /// The 64-bit (instance, landblock) key identifying this specific landblock copy
        /// </summary>
        public ulong LongId => (ulong)Instance << 32 | Id.Raw;

        /// <summary>
        /// Set exactly when this landblock is a live ephemeral instance
        /// (a temporary on-demand copy); null for permanent landblocks.
        /// </summary>
        internal Realms.EphemeralRealm InnerRealmInfo { get; set; }

        /// <summary>
        /// True if this landblock copy is an ephemeral (temporary, unpersisted) instance
        /// </summary>
        public bool IsEphemeral
        {
            get
            {
                ACE.Entity.Position.ParseInstanceID(Instance, out var isEphemeralRealm, out _, out _);
                return isEphemeralRealm;
            }
        }

        /// <summary>
        /// Flag indicates if this landblock is permanently loaded (for example, towns on high-traffic servers)
        /// </summary>
        public bool Permaload = false;

        /// <summary>
        /// Flag indicates if this landblock has no keep alive objects
        /// </summary>
        public bool HasNoKeepAliveObjects = true;

        /// <summary>
        /// This must be true before a player enters a landblock.
        /// This prevents a player from possibly pasing through a door that hasn't spawned in yet, and other scenarios.
        /// </summary>
        public bool CreateWorldObjectsCompleted { get; private set; }

        private DateTime lastActiveTime;

        /// <summary>
        /// Dormant landblocks suppress Monster AI ticking and physics processing
        /// </summary>
        public bool IsDormant;

        private readonly Dictionary<ObjectGuid, WorldObject> worldObjects = new Dictionary<ObjectGuid, WorldObject>();
        private readonly Dictionary<ObjectGuid, WorldObject> pendingAdditions = new Dictionary<ObjectGuid, WorldObject>();
        private readonly List<ObjectGuid> pendingRemovals = new List<ObjectGuid>();

        // Cache used for Tick efficiency
        private readonly List<Player> players = new List<Player>();
        private readonly LinkedList<Creature> sortedCreaturesByNextTick = new LinkedList<Creature>();
        private readonly LinkedList<WorldObject> sortedWorldObjectsByNextHeartbeat = new LinkedList<WorldObject>();
        private readonly LinkedList<WorldObject> sortedGeneratorsByNextGeneratorUpdate = new LinkedList<WorldObject>();
        private readonly LinkedList<WorldObject> sortedGeneratorsByNextRegeneration = new LinkedList<WorldObject>();

        /// <summary>
        /// This is used to detect and manage cross-landblock group (which is potentially cross-thread) operations.
        /// </summary>
        public LandblockGroup CurrentLandblockGroup { get; internal set; }

        public List<Landblock> Adjacents = new List<Landblock>();

        private readonly ActionQueue actionQueue = new ActionQueue();

        /// <summary>
        /// Landblocks heartbeat every 5 seconds
        /// </summary>
        private static readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(5);

        private DateTime lastHeartBeat = DateTime.MinValue;

        /// <summary>
        /// Landblock items will be saved to the database every 5 minutes
        /// </summary>
        private static readonly TimeSpan databaseSaveInterval = TimeSpan.FromMinutes(5);

        private DateTime lastDatabaseSave = DateTime.MinValue;

        /// <summary>
        /// Landblocks which have been inactive for this many seconds will be dormant
        /// </summary>
        private static readonly TimeSpan dormantInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Landblocks which have been inactive for this many seconds will be unloaded
        /// </summary>
        public static readonly TimeSpan UnloadInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// TRUE when an idle landblock may be queued for destruction, judged AT THE INSTANT OF THE CALL.
        /// This is a per-call guarantee, not a tick-wide one: it says only that no player was on the
        /// landblock when the heartbeat asked, and arrivals go on happening after that. The tick-wide
        /// guarantee - that a landblock a player is standing on is never actually torn down - comes from
        /// this check TOGETHER WITH <see cref="TryClaimForDestruction"/>, which re-asks immediately before
        /// the destructive work in LandblockManager.UnloadLandblocks. Neither half is sufficient alone.
        ///
        /// <para/>
        /// The player check is not redundant with the idle clock, because the two are not updated by the
        /// same thing. lastActiveTime is refreshed ONLY by Player.Heartbeat -> Player.NotifyLandblocks ->
        /// SetActive, which runs on a ~5 second per-player cadence; nothing in the object-ADD path touches
        /// it (see AddWorldObjectInternal). So a player who teleports into a landblock during the final
        /// player-heartbeat period of its 5 minute idle countdown lands on a landblock whose clock is still
        /// expired, and the heartbeat below then queues it for destruction in the SAME world tick - the
        /// landblock heartbeat runs in TickMultiThreadedWork, the player heartbeat that would have called
        /// SetActive runs later in TickSingleThreadedWork, and UnloadLandblocks runs after both
        /// (LandblockManager.Tick). Unload() has no player check either: it removes the player from the
        /// landblock, nulls their CurrentLandblock, destroys their PhysicsObj and releases the physics
        /// cells, while their Location still names this landblock. The result on the client is terrain and
        /// nothing else - no NPCs, no portals, no sky decor - until they relog and DoPlayerEnterWorld
        /// rebuilds the landblock. Diagnosed 2026-09-09 from a stage exit out of The Sealed Stair, where
        /// the player re-entered landblock 0x21B0 instance 0x00010000 about three seconds before the
        /// unload deadline they had themselves set when they entered the dungeon five minutes earlier.
        /// </summary>
        public static bool ShouldQueueForUnload(int playerCount, DateTime lastActiveTime, DateTime now)
        {
            if (playerCount > 0)
                return false;

            return lastActiveTime + UnloadInterval < now;
        }


        /// <summary>
        /// The clientlib backing store landblock
        /// Eventually these classes could be merged, but for now they are separate...
        /// </summary>
        public Physics.Common.Landblock PhysicsLandblock { get; }

        public CellLandblock CellLandblock { get; }
        public LandblockInfo LandblockInfo { get; }

        /// <summary>
        /// The landblock static meshes for
        /// collision detection and physics simulation
        /// </summary>
        public LandblockMesh LandblockMesh { get; private set; }
        public List<ModelMesh> LandObjects { get; private set; }
        public List<ModelMesh> Buildings { get; private set; }
        public List<ModelMesh> WeenieMeshes { get; private set; }
        public List<ModelMesh> Scenery { get; private set; }


        public readonly RateMonitor Monitor5m = new RateMonitor();
        private readonly TimeSpan last5mClearInteval = TimeSpan.FromMinutes(5);
        private DateTime last5mClear;
        public readonly RateMonitor Monitor1h = new RateMonitor();
        private readonly TimeSpan last1hClearInteval = TimeSpan.FromHours(1);
        private DateTime last1hClear;
        private bool monitorsRequireEventStart = true;

        // Used for cumulative ServerPerformanceMonitor event recording
        private readonly Stopwatch stopwatch = new Stopwatch();


        private EnvironChangeType fogColor;

        public EnvironChangeType FogColor
        {
            get
            {
                if (LandblockManager.GlobalFogColor.HasValue)
                    return LandblockManager.GlobalFogColor.Value;

                return fogColor;
            }
            set => fogColor = value;
        }


        public Landblock(LandblockId id, uint instance)
        {
            //log.DebugFormat("Landblock({0:X8})", (id.Raw | 0xFFFF));

            Id = id;
            Instance = instance;

            CellLandblock = DatManager.CellDat.ReadFromDat<CellLandblock>(Id.Raw | 0xFFFF);
            LandblockInfo = DatManager.CellDat.ReadFromDat<LandblockInfo>((uint)Id.Landblock << 16 | 0xFFFE);

            lastActiveTime = DateTime.UtcNow;

            var cellLandblock = DBObj.GetCellLandblock(Id.Raw | 0xFFFF);
            PhysicsLandblock = new Physics.Common.Landblock(cellLandblock, instance);
        }

        public void Init(bool reload = false)
        {
            if (!reload)
                PhysicsLandblock.PostInit();

            // This population task is fire-and-forget, and on .NET 5+ an unobserved Task exception is not
            // rethrown on the finalizer thread and never reaches Program.cs's AppDomain.UnhandledException
            // hook (no TaskScheduler.UnobservedTaskException handler is registered anywhere in this
            // solution either). Without the continuation below, a throw anywhere in here is COMPLETELY
            // silent - no log line at any level - and the operational symptom is severe: CreateWorldObjects
            // does its DB reads and object construction before it queues the action that sets
            // CreateWorldObjectsCompleted, so a throw in that phase leaves the flag false forever. The
            // landblock then stays permanently empty, and any player who teleported in is held in the
            // pre-materialize "pink bubble" state by Player.OnTeleportComplete's retry loop.
            // Faulted-only: OnlyOnFaulted means the continuation is skipped (cancelled) on success, and the
            // Task.Wait/Result is never called, so this is the only thing that ever observes the exception.
            Task.Run(() =>
            {
                CreateWorldObjects();

                SpawnDynamicShardObjects();

                SpawnEncounters();

                SpawnSkyDecor();
            }).ContinueWith(t =>
            {
                log.Error($"Landblock 0x{Id.Landblock:X4} instance 0x{Instance:X8}: the population task faulted. This landblock will stay empty, and any player inside it is held pre-materialize (pink bubble) until Player.OnTeleportComplete's bounded wait expires.", t.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted);

            //LoadMeshes(objects);
        }

        /// <summary>
        /// Monster Locations, Generators<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void CreateWorldObjects()
        {
            // Realms Phase 3: a realm's override rows replace base content for its instances
            ACE.Entity.Position.ParseInstanceID(Instance, out _, out var realmId, out _);
            var objects = DatabaseManager.World.GetCachedInstancesByLandblock(Id.Landblock, realmId);

            // Threads (WaffleACE): a run's private copy loads the dungeon's doors, levers, plates and
            // hotspots but none of its generators, hand-placed creatures or chests, and every retail portal
            // becomes the run's exit portal. The filter returns a NEW list - the cached one is shared by
            // every copy of this landblock and must never be touched. The run is attached to the ephemeral
            // realm BEFORE this landblock is constructed (RealmManager.GetNewThreadDungeonLandblock), which is
            // what makes it visible here; a null Run is every other ephemeral or base instance.
            var dungeonRun = InnerRealmInfo?.Run;
            if (dungeonRun != null)
            {
                // The filter drops a wcid the world db has no weenie for (KeyNotFoundException) and counts it.
                // Anything else out of these lambdas is a real world-db failure - a cold cache, a dead
                // connection - and it must NOT be mistaken for a missing row: silently dropping on it would
                // build a copy with no exit portal, no doors, or no dungeon at all. Log it with enough context
                // to identify the row and rethrow, so the copy is abandoned half-built and the manager's
                // "landblock never loaded" path ends the run, instead of a player walking into a broken one.
                var filterWcid = 0u;
                try
                {
                    objects = ACE.Server.ThreadDungeons.ThreadDungeonContentFilter.Filter(objects,
                        wcid => { filterWcid = wcid; return DatabaseManager.World.GetCachedWeenie(wcid)?.WeenieType ?? throw new KeyNotFoundException($"wcid {wcid}"); },
                        wcid => { filterWcid = wcid; return DatabaseManager.World.GetCachedWeenie(wcid)?.PropertiesGenerator?.Count > 0; },
                        dungeonRun.ExitPortalWcid, out var filterStats);
                    log.Info($"[DYNDUNGEON] 0x{Id.Landblock:X4} instance 0x{Instance:X8} content filter: {filterStats}, shard statics skipped");
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] 0x{Id.Landblock:X4} instance 0x{Instance:X8}: content filter failed while resolving wcid {filterWcid} - abandoning this copy rather than loading it half-filtered", ex);
                    throw;
                }
            }

            // Threads: a run's copy is retail-pristine and loads NO shard-persisted statics, so no
            // persisted door, chest or portal state from the base world leaks into it. Ephemeral instances
            // never WRITE shard state (SaveDB returns early on IsEphemeral, see below in this file), so
            // reading it back is an inconsistency rather than a feature; and a persisted biota would defeat
            // the exit-portal clone above, because CreateNewWorldObjects prefers the biota over the weenie
            // whenever one exists for the row's guid, and GetStaticObjectsByLandblock is keyed on the
            // landblock's static guid range alone - it cannot tell one instance's copy from another's.
            // Note the type: this is ACE.Database.Models.Shard.Biota, NOT the ACE.Entity.Models.Biota that
            // `using ACE.Entity.Models` binds a bare `Biota` to in this file.
            var shardObjects = dungeonRun != null
                ? new List<ACE.Database.Models.Shard.Biota>()
                : DatabaseManager.Shard.BaseDatabase.GetStaticObjectsByLandblock(Id.Landblock);
            var factoryObjects = WorldObjectFactory.CreateNewWorldObjects(objects, shardObjects);

            // Forensic trail for "the room was empty" reports. Prod runs at DEBUG, so this is the primary
            // record of what a landblock copy actually resolved: which realm's rows it asked for, how many
            // world rows and shard statics came back, and how many objects the factory built from them.
            // Everything read here is an already-materialized list, so the guard keeps the cost at zero
            // when Debug is off - no enumeration and no interpolation outside it.
            if (log.IsDebugEnabled)
                log.Debug($"Landblock 0x{Id.Landblock:X4} instance 0x{Instance:X8}: CreateWorldObjects resolved realm {realmId}, {objects.Count} world rows, {shardObjects.Count} shard statics, {factoryObjects.Count} objects built");

            // world-db content is instance-agnostic - everything this landblock copy
            // loads lives in this copy's instance
            foreach (var fo in factoryObjects)
                fo.Location.Instance = Instance;

            actionQueue.EnqueueAction(new ActionEventDelegate(() =>
            {
                // for mansion linking
                var houses = new List<House>();

                foreach (var fo in factoryObjects)
                {
                    // Last-resort per-object containment: this action runs on the world thread, so anything
                    // thrown here escapes through ActionQueue.RunActions and LandblockManager.TickMultiThreadedWork
                    // to WorldManager's fatal handler, which stops the world. One bad content row must cost one
                    // object, not the whole server. The narrow, known cause is guarded upstream (see
                    // WorldObject_Setup.cs); this catch is what keeps the next unknown one survivable.
                    try
                    {
                        WorldObject parent = null;
                        if (fo.WeenieType == WeenieType.House)
                        {
                            var house = fo as House;
                            Houses.Add(house);

                            if (fo.HouseType == HouseType.Mansion)
                            {
                                houses.Add(house);
                                house.LinkedHouses.Add(houses[0]);

                                if (houses.Count > 1)
                                {
                                    houses[0].LinkedHouses.Add(house);
                                    parent = houses[0];
                                }
                            }
                        }

                        AddWorldObject(fo);
                        fo.ActivateLinks(objects, shardObjects, parent);

                        if (fo.PhysicsObj != null)
                            fo.PhysicsObj.Order = 0;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.CreateWorldObjects: skipped 0x{fo.Guid}:{fo.Name} [{fo.WeenieClassId} - {fo.WeenieType}] after an unhandled exception", ex);
                    }
                }

                CreateWorldObjectsCompleted = true;

                // The other half of the trail: what is actually in the landblock at the moment the gate
                // Player.OnTeleportComplete waits on opens. Objects added by the loop above are still in
                // pendingAdditions at this point (AddWorldObjectInternal stages them there; they move into
                // worldObjects on the next tick), so both dictionaries have to be counted for this number
                // to mean anything. Two Count reads, and only when Debug is on.
                if (log.IsDebugEnabled)
                    log.Debug($"Landblock 0x{Id.Landblock:X4} instance 0x{Instance:X8}: CreateWorldObjectsCompleted, {worldObjects.Count + pendingAdditions.Count} objects present");

                PhysicsLandblock.SortObjects();
            }));
        }

        /// <summary>
        /// Corpses<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void SpawnDynamicShardObjects()
        {
            var dynamics = DatabaseManager.Shard.BaseDatabase.GetDynamicObjectsByLandblock(Id.Landblock, Instance);

            // WaffleACE world events (Docs/WorldEvents/TECH-DESIGN.md 2.9): this is the single point where shard
            // biotas re-enter the world, so it is also where anything a crashed event left behind gets caught.
            // Drops every biota carrying PropertyInt.WorldEventId and queues its deletion from the shard.
            // Never throws - on any failure it hands back the unfiltered list and the landblock loads as before.
            dynamics = WorldEvents.WorldEventOrphanFilter.FilterAndSweep(Id.Landblock, Instance, dynamics);

            // WaffleACE Mule Vendor (Docs/MuleVendor/DESIGN.md 7.2, risk R1): an account vault container's
            // Location is a bookkeeping pointer that keeps the orphan purge from deleting it, NOT a
            // placement - and it matches every clause of the query above, because carrying no Container
            // and no Wielder is the whole premise of R1. Without this, activating the reserved landblock,
            // or any landblock account_vault_landblock has been pointed at, would put every account's
            // vault into the live world as a lootable backpack. Unlike the sweep above, this NEVER
            // deletes what it drops: that is live player property, and it stays in the shard.
            dynamics = AccountVault.AccountVaultSpawnFilter.Filter(Id.Landblock, Instance, dynamics);

            var factoryShardObjects = WorldObjectFactory.CreateWorldObjects(dynamics);

            actionQueue.EnqueueAction(new ActionEventDelegate(() =>
            {
                foreach (var fso in factoryShardObjects)
                {
                    // per-object containment - see the matching catch in CreateWorldObjects
                    try
                    {
                        AddWorldObject(fso);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.SpawnDynamicShardObjects: skipped 0x{fso.Guid}:{fso.Name} [{fso.WeenieClassId} - {fso.WeenieType}] after an unhandled exception", ex);
                    }
                }
            }));
        }

        /// <summary>
        /// Spawns the semi-randomized monsters scattered around the outdoors<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void SpawnEncounters()
        {
            // Threads: a run copy has no retail creatures by construction, and encounters are a
            // second creature source that CreateWorldObjects' content filter never sees.
            if (InnerRealmInfo?.Run != null)
                return;

            // Realms Phase 4: a realm may suppress this landblock's encounters entirely
            ACE.Entity.Position.ParseInstanceID(Instance, out _, out var realmId, out _);

            // get the encounter spawns for this landblock
            var encounters = DatabaseManager.World.GetCachedEncountersByLandblock(Id.Landblock, realmId);

            foreach (var encounter in encounters)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(encounter.WeenieClassId);

                if (wo == null) continue;

                actionQueue.EnqueueAction(new ActionEventDelegate(() =>
                {
                    // per-object containment - see the matching catch in CreateWorldObjects
                    try
                    {
                        SpawnEncounter(encounter, wo);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.SpawnEncounters: skipped 0x{wo.Guid}:{wo.Name} [{wo.WeenieClassId} - {wo.WeenieType}] after an unhandled exception", ex);
                    }
                }));
            }
        }

        /// <summary>
        /// Places one encounter spawn. Extracted from SpawnEncounters so that its queued action body
        /// can be wrapped in a per-object try/catch.
        /// </summary>
        private void SpawnEncounter(Encounter encounter, WorldObject wo)
        {
            var xPos = Math.Clamp(encounter.CellX * 24.0f, 0.5f, 191.5f);
            var yPos = Math.Clamp(encounter.CellY * 24.0f, 0.5f, 191.5f);

            var pos = new Physics.Common.Position();
            pos.ObjCellID = (uint)(Id.Landblock << 16) | 1;
            pos.Frame = new Physics.Animation.AFrame(new Vector3(xPos, yPos, 0), Quaternion.Identity);
            pos.adjust_to_outside();

            pos.Frame.Origin.Z = PhysicsLandblock.GetZ(pos.Frame.Origin);

            wo.Location = new Position(pos.ObjCellID, pos.Frame.Origin, pos.Frame.Orientation);
            wo.Location.Instance = Instance;

            var sortCell = LScape.get_landcell(pos.ObjCellID, Instance) as SortCell;
            if (sortCell != null && sortCell.has_building())
            {
                wo.Destroy();
                return;
            }

            if (PropertyManager.GetBool("override_encounter_spawn_rates").Item)
            {
                wo.RegenerationInterval = PropertyManager.GetDouble("encounter_regen_interval").Item;

                wo.ReinitializeHeartbeats();

                if (wo.Biota.PropertiesGenerator != null)
                {
                    // While this may be ugly, it's done for performance reasons.
                    // Common weenie properties are not cloned into the bota on creation. Instead, the biota references simply point to the weenie collections.
                    // The problem here is that we want to update one of those common collection properties. If the biota is referencing the weenie collection,
                    // then we'll end up updating the global weenie (from the cache), instead of just this specific biota.
                    if (wo.Biota.PropertiesGenerator == wo.Weenie.PropertiesGenerator)
                    {
                        wo.Biota.PropertiesGenerator = new List<PropertiesGenerator>(wo.Weenie.PropertiesGenerator.Count);

                        foreach (var record in wo.Weenie.PropertiesGenerator)
                            wo.Biota.PropertiesGenerator.Add(record.Clone());
                    }

                    foreach (var profile in wo.Biota.PropertiesGenerator)
                        profile.Delay = (float)PropertyManager.GetDouble("encounter_delay").Item;
                }
            }

            if (!AddWorldObject(wo))
                wo.Destroy();
        }

        // ===========================================================================================
        // Sky decor (WaffleACE fork)
        //
        // Server-generated spinning sky scenery, described by rows in the world DB's sky_decor_region
        // table and materialised HERE, in code, on landblock activation. Deliberately built on the
        // SpawnEncounters shape rather than on landblock_instance rows: nothing is persisted, nothing
        // is allocated from the guid registry, and a volume/size/height/colour edit is one UPDATE plus
        // `/sky-decor reload`.
        //
        // Sky decor is PLACED ONCE and never touched again: the objects keep the weenie's own Static rig,
        // and there is deliberately no per-tick step anywhere in this class for them. The only two things
        // that ever move a cloud are a landblock reload and /sky-decor reload, both of which destroy and
        // re-derive the whole set.
        //
        // The layout math lives in ACE.Server.Entity.SkyDecorLayout and is pure - see that file for the
        // determinism contract. This half is only the world plumbing.
        // ===========================================================================================

        /// <summary>
        /// Every sky-decor object this landblock currently owns. Held so a rebuild can destroy exactly
        /// what it spawned rather than guessing from worldObjects.
        ///
        /// Mutated only from the action queue (the landblock's own thread); read from a command thread by
        /// <see cref="GetSkyDecorCount"/>. <see cref="skyDecorLock"/> covers both, because a List being
        /// appended to on one thread cannot be safely enumerated on another. Contention is nil - the
        /// mutations happen at landblock activation and on /sky-decor reload, and nothing else touches it.
        /// </summary>
        private readonly List<WorldObject> skyDecorObjects = new List<WorldObject>();

        private readonly object skyDecorLock = new object();

        /// <summary>
        /// What the last rebuild PLANNED, for /sky-decor info: clouds is the number of pairs (which is
        /// what `density` counts), objects is what those pairs expand to. Reported alongside the live
        /// object count rather than instead of it, because a gap between planned and live is exactly how
        /// a run of failed spawns shows itself.
        /// </summary>
        private int skyDecorPlannedClouds;

        private int skyDecorPlannedObjects;

        /// <summary>
        /// What the dice produced before the no-overlap cull ran, for the "clouds N of M candidates" line
        /// in /sky-decor info. The gap between this and skyDecorPlannedClouds is the only visible measure
        /// of how hard min_separation is biting, and the owner tunes that number by eye.
        /// </summary>
        private int skyDecorPlannedCandidates;

        /// <summary>
        /// Sky decor for this landblock<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void SpawnSkyDecor()
        {
            if (IsDungeon)
                return;

            // Warmed off the world thread on purpose: the very first outdoor landblock of a server's life
            // is the one that pays for the table read, and this task is the right place for it. Nothing
            // else is decided here - see RebuildSkyDecor for why.
            if (SkyDecorOverrides.EffectiveRegions().Count == 0)
                return;

            EnqueueSkyDecorRebuild();
        }

        /// <summary>
        /// The ONLY way sky decor ever enters this landblock - the initial spawn and /sky-decor reload
        /// both go through here, queueing the exact same idempotent body.
        ///
        /// That sameness is load-bearing, not tidiness. LandblockManager.GetLandblock adds a landblock to
        /// loadedLandblocks (LandblockManager.cs:551) BEFORE calling Init (:564), and Init spawns its work
        /// on a background Task, so /sky-decor reload can enumerate a landblock and queue a rebuild for it
        /// while that landblock's own first rebuild is still on its way to the queue. With one idempotent
        /// body the order stops mattering: each run destroys what is there and re-derives the whole set
        /// from the current cache, so whichever lands last wins and the block can never hold two
        /// generations. An initial spawn that only ADDED would double the clouds in exactly that window.
        /// </summary>
        private void EnqueueSkyDecorRebuild()
        {
            actionQueue.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    RebuildSkyDecor();
                }
                catch (Exception ex)
                {
                    log.Error($"Landblock 0x{Id}.RebuildSkyDecor: failed", ex);
                }
            }));
        }

        /// <summary>
        /// Destroys this landblock's sky decor and re-derives it from the region cache. Runs on the
        /// landblock's own thread, from the action queue only.
        ///
        /// The region set is re-read HERE rather than captured by the caller: a rebuild queued before a
        /// /sky-decor reload invalidated the cache would otherwise re-apply the stale region set, which is
        /// the same ordering hazard as the doubling one and just as invisible.
        ///
        /// It comes from SkyDecorOverrides, never from the database cache directly, so a /sky-decor set
        /// override is honoured by every rebuild - including the first activation of a landblock that
        /// loads after the override was made.
        /// </summary>
        private void RebuildSkyDecor()
        {
            DestroySkyDecorObjects();

            if (IsDungeon)
                return;

            var regions = SkyDecorOverrides.EffectiveRegions();

            if (regions.Count == 0)
                return;

            Position.ParseInstanceID(Instance, out _, out var realmId, out _);

            var covering = regions.Where(r => r.Covers(Id.Landblock, realmId)).ToList();

            if (covering.Count == 0)
                return;

            SpawnSkyDecorForRegions(covering);
        }

        /// <summary>
        /// The world-thread half: plan each covering region and put its clouds in the world. Overlapping
        /// regions are ADDITIVE - each contributes its own clouds, from its own seed.
        /// </summary>
        private void SpawnSkyDecorForRegions(List<SkyDecorRegion> regions)
        {
            var plannedCandidates = 0;
            var plannedClouds = 0;
            var plannedObjects = 0;

            foreach (var region in regions)
            {
                // Deduped inside, to one line per distinct palette string - a typo in a palette would
                // otherwise warn once per landblock per region, which buries the message rather than
                // delivering it. This is the load path, so a bad token is skipped and logged; the strict
                // path is /sky-decor set, which refuses the edit outright.
                SkyDecorColours.WarnAboutPalette(region);

                var plan = SkyDecorLayout.PlanBlock(region, Id.Landblock, SkyDecorGroundZ, SkyDecorIsWaterCell);

                var clouds = plan.Clouds;

                plannedCandidates += plan.Candidates;
                plannedClouds += clouds.Count;

                foreach (var c in clouds)
                    plannedObjects += c.Partner != null ? 2 : 1;

                // A planned cloud is a primary disc plus, usually, a partner disc. Both are spawned the
                // same way and both are ordinary sky decor from here on - the partner is not a child of
                // the primary and nothing links them at runtime, because nothing needs to: neither ever
                // moves, and a rebuild re-derives both together.
                foreach (var cloud in clouds.SelectMany(c => c.WithPartner()))
                {
                    // per-object containment - see the matching catch in CreateWorldObjects
                    try
                    {
                        var wo = SpawnSkyDecorCloud(cloud);

                        if (wo != null)
                        {
                            lock (skyDecorLock)
                                skyDecorObjects.Add(wo);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.SpawnSkyDecor: skipped wcid {cloud.WeenieClassId} from region '{region.Name}' after an unhandled exception", ex);
                    }
                }
            }

            lock (skyDecorLock)
            {
                skyDecorPlannedCandidates = plannedCandidates;
                skyDecorPlannedClouds = plannedClouds;
                skyDecorPlannedObjects = plannedObjects;
            }
        }

        /// <summary>
        /// Creates and places one planned cloud. Returns null when the object could not be created or
        /// could not enter the world.
        /// </summary>
        private WorldObject SpawnSkyDecorCloud(SkyDecorCloud cloud)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(cloud.WeenieClassId);

            if (wo == null)
                return null;

            // Stamped BEFORE the object enters the world, on the same reasoning the world-event spawner
            // stamps WorldEventId: no window in which a landblock database save could see this as an
            // ordinary persistable dynamic (see WorldObject_Database.IsDynamicThatShouldPersistToShard).
            wo.IsSkyDecor = true;

            // Mandatory. A dynamic-guid Generic with no TimeToRot is decayable
            // (WorldObject_Decay.cs:27-44), and the landblock heartbeat would rot the sky away.
            wo.TimeToRot = -1;

            wo.ObjScale = cloud.Scale;

            // 0 has to leave the property UNSET rather than write an explicit 0: WorldObject_Networking
            // only sets the PhysicsDesc translucency flag when Math.Abs(Translucency ?? 0) >= 0.001
            // (WorldObject_Networking.cs:522), so below that threshold the property is never sent at all,
            // and a stray explicit 0 costs a wire flag for nothing.
            if (cloud.Translucency >= 0.001f)
                wo.Translucency = cloud.Translucency;

            // AND directly, not instead - the same rule and the same reason as WorldEventSpawner.cs:1832.
            wo.SetProperty(PropertyFloat.MotionSpeed, cloud.Spin);

            if (wo.CurrentMotionState?.MotionState != null)
                wo.CurrentMotionState.MotionState.ForwardSpeed = cloud.Spin;

            // The weenie's own PhysicsState (0x815 - Static, Ethereal, IgnoreCollisions, LightingOn) is
            // used exactly as it ships. Static is what holds a sky object in place with no gravity and no
            // per-frame physics, which is the whole reason nothing here needs a tick.

            var cellId = SkyDecorLayout.CellId(Id.Landblock, cloud.X, cloud.Y);

            wo.Location = new Position(cellId, new Vector3(cloud.X, cloud.Y, cloud.OriginZ), new Quaternion(cloud.AnglesX, cloud.AnglesY, cloud.AnglesZ, cloud.AnglesW));
            wo.Location.Instance = Instance;

            if (!AddWorldObject(wo))
            {
                wo.Destroy();
                return null;
            }

            return wo;
        }

        /// <summary>Terrain height at a landblock-local point, for the layout math.</summary>
        private float SkyDecorGroundZ(float x, float y)
        {
            return PhysicsLandblock.GetZ(new Vector3(x, y, 0));
        }

        /// <summary>
        /// True when terrain cell (cellX, cellY) touches water. The dat gives a terrain code per VERTEX
        /// (9x9 per block, index vx * 9 + vy); a cell is the square between four of them, and any wet
        /// corner is treated as wet - a shoreline cell is exactly the case a region with over_water = 0
        /// is trying to avoid. LandDefs.TerrainType.WaterRunning (0x10) is the first water value.
        /// </summary>
        private bool SkyDecorIsWaterCell(int cellX, int cellY)
        {
            var terrain = CellLandblock?.Terrain;

            if (terrain == null || terrain.Count < 81)
                return false;

            for (var dx = 0; dx <= 1; dx++)
            {
                for (var dy = 0; dy <= 1; dy++)
                {
                    var idx = (cellX + dx) * 9 + (cellY + dy);

                    if (idx < 0 || idx >= terrain.Count)
                        continue;

                    var type = (terrain[idx] & 0x7C) >> 2;

                    if (type >= (int)LandDefs.TerrainType.WaterRunning)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Destroys every sky-decor object this landblock owns and forgets them. Must run on the world
        /// thread (every caller routes through the action queue).
        ///
        /// Tolerant of entries that are ALREADY destroyed, because a whole pre-existing command reaches
        /// them behind this class's back: /reload-landblock calls DestroyAllNonPlayerObjects
        /// (DeveloperCommands.cs:3422), which destroys every non-persisted object in the landblock - sky
        /// decor included - and knows nothing about this list. Clearing the list here is what stops those
        /// corpses being counted by /sky-decor info and held alive by this reference, and it works because
        /// a rebuild always destroys before it spawns.
        ///
        /// The IsSkyDecor filter is defence in depth: nothing else is ever added to this list, so it
        /// should be redundant - but the list is the only thing standing between a bookkeeping slip and
        /// Destroy() being called on somebody else's object, and the flag makes that impossible rather
        /// than merely unlikely.
        /// </summary>
        private void DestroySkyDecorObjects()
        {
            List<WorldObject> doomed;

            lock (skyDecorLock)
            {
                doomed = new List<WorldObject>(skyDecorObjects);

                skyDecorObjects.Clear();

                skyDecorPlannedCandidates = 0;
                skyDecorPlannedClouds = 0;
                skyDecorPlannedObjects = 0;
            }

            foreach (var wo in doomed)
            {
                if (wo.IsSkyDecor && !wo.IsDestroyed)
                    wo.Destroy();
            }
        }

        /// <summary>
        /// /sky-decor reload: drop this landblock's decor and re-derive it from the region cache. Queued
        /// rather than run inline so it happens on the landblock's own thread, between ticks, and through
        /// the very same body as the initial spawn - see <see cref="EnqueueSkyDecorRebuild"/> for why that
        /// is the fix for the reload-versus-first-activation race rather than a stylistic choice.
        /// </summary>
        public void ReloadSkyDecor()
        {
            EnqueueSkyDecorRebuild();
        }

        /// <summary>
        /// How many LIVE sky-decor objects this landblock owns, and what the last rebuild planned. For
        /// /sky-decor info. Clouds are PAIRS (what `density` counts); objects are what they expand to.
        ///
        /// Destroyed entries are excluded rather than assumed absent: /reload-landblock can destroy them
        /// without this class hearing about it (see <see cref="DestroySkyDecorObjects"/>), and a count
        /// that included those corpses would report a doubled sky that is not actually there - the exact
        /// misreading this command exists to prevent.
        /// </summary>
        public (int Live, int PlannedClouds, int PlannedObjects, int PlannedCandidates) GetSkyDecorCounts()
        {
            lock (skyDecorLock)
            {
                var live = 0;

                foreach (var wo in skyDecorObjects)
                {
                    if (wo.IsSkyDecor && !wo.IsDestroyed)
                        live++;
                }

                return (live, skyDecorPlannedClouds, skyDecorPlannedObjects, skyDecorPlannedCandidates);
            }
        }

        /// <summary>
        /// Loads the meshes for the landblock<para />
        /// This isn't used by ACE, but we still retain it for the following reason:<para />
        /// its useful, concise, high level overview code for everything needed to load landblocks, all their objects, scenery, polygons
        /// without getting into all of the low level methods that acclient uses to do it
        /// </summary>
        private void LoadMeshes(List<LandblockInstance> objects)
        {
            LandblockMesh = new LandblockMesh(Id);
            LoadLandObjects();
            LoadBuildings();
            LoadWeenies(objects);
            LoadScenery();
        }

        /// <summary>
        /// Loads the meshes for the static landblock objects,
        /// also known as obstacles
        /// </summary>
        private void LoadLandObjects()
        {
            LandObjects = new List<ModelMesh>();

            foreach (var obj in LandblockInfo.Objects)
                LandObjects.Add(new ModelMesh(obj.Id, obj.Frame));
        }

        /// <summary>
        /// Loads the meshes for the buildings on the landblock
        /// </summary>
        private void LoadBuildings()
        {
            Buildings = new List<ModelMesh>();

            foreach (var obj in LandblockInfo.Buildings)
                Buildings.Add(new ModelMesh(obj.ModelId, obj.Frame));
        }

        /// <summary>
        /// Loads the meshes for the weenies on the landblock
        /// </summary>
        private void LoadWeenies(List<LandblockInstance> objects)
        {
            WeenieMeshes = new List<ModelMesh>();

            foreach (var obj in objects)
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(obj.WeenieClassId);
                WeenieMeshes.Add(
                    new ModelMesh(weenie.GetProperty(PropertyDataId.Setup) ?? 0,
                    new DatLoader.Entity.Frame(new Position(obj.ObjCellId, obj.OriginX, obj.OriginY, obj.OriginZ, obj.AnglesX, obj.AnglesY, obj.AnglesZ, obj.AnglesW, Instance))));
            }
        }

        /// <summary>
        /// Loads the meshes for the scenery on the landblock
        /// </summary>
        private void LoadScenery()
        {
            Scenery = Entity.Scenery.Load(this);
        }

        /// <summary>
        /// This should be called before TickLandblockGroupThreadSafeWork() and before Tick()
        /// </summary>
        public void TickPhysics(double portalYearTicks, ConcurrentBag<WorldObject> movedObjects)
        {
            if (IsDormant)
                return;

            Monitor5m.Restart();
            Monitor1h.Restart();
            monitorsRequireEventStart = false;

            ProcessPendingWorldObjectAdditionsAndRemovals();

            foreach (WorldObject wo in worldObjects.Values)
            {
                // set to TRUE if object changes landblock
                var landblockUpdate = wo.UpdateObjectPhysics();

                if (landblockUpdate)
                    movedObjects.Add(wo);
            }

            Monitor5m.Pause();
            Monitor1h.Pause();
        }

        /// <summary>
        /// This will tick anything that can be multi-threaded safely using LandblockGroups as thread boundaries
        /// This should be called after TickPhysics() and before Tick()
        /// </summary>
        public void TickMultiThreadedWork(double currentUnixTime)
        {
            if (monitorsRequireEventStart)
            {
                Monitor5m.Restart();
                Monitor1h.Restart();
            }
            else
            {
                Monitor5m.Resume();
                Monitor1h.Resume();
            }

            stopwatch.Restart();
            // This will consist of the following work:
            // - this.CreateWorldObjects
            // - this.SpawnDynamicShardObjects
            // - this.SpawnEncounters
            // - Adding items back onto the landblock from failed player movements: Player_Inventory.cs DoHandleActionPutItemInContainer()
            // - Executing trade between two players: Player_Trade.cs FinalizeTrade()
            actionQueue.RunActions();
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_RunActions, stopwatch.Elapsed.TotalSeconds);

            ProcessPendingWorldObjectAdditionsAndRemovals();

            // When a WorldObject Ticks, it can end up adding additional WorldObjects to this landblock
            if (!IsDormant)
            {
                stopwatch.Restart();
                while (sortedCreaturesByNextTick.Count > 0) // Monster_Tick()
                {
                    var first = sortedCreaturesByNextTick.First.Value;

                    // If they wanted to run before or at now
                    if (first.NextMonsterTickTime <= currentUnixTime)
                    {
                        sortedCreaturesByNextTick.RemoveFirst();

                        // Remove-then-act: the creature is already out of sortedCreaturesByNextTick, so an
                        // escaping throw would both stop the world (WorldManager's fatal handler does NOT
                        // crash the process, it stops the tick) AND drop this creature from the tick list
                        // forever. Contain it, log the culprit, and re-add in the finally so the loss cannot
                        // happen either way. One bad object must cost one object, not the whole server.
                        try
                        {
                            first.Monster_Tick(currentUnixTime);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"Landblock 0x{Id}.TickMultiThreadedWork: Monster_Tick threw for 0x{first.Guid}:{first.Name} [{first.WeenieClassId} - {first.WeenieType}]", ex);

                            // Monster_Tick normally advances NextMonsterTickTime near its top, but the chess
                            // piece / passive pet branches above that do not, so it can still be in the past
                            // here. Bump it by the standard interval (Creature.monsterTickInterval, which is
                            // not visible from here) rather than calling back into the failing object: that
                            // both preserves this fixed-interval list's ordering and stops the while loop
                            // below from re-picking the same creature forever inside a single tick.
                            if (first.NextMonsterTickTime <= currentUnixTime)
                                first.NextMonsterTickTime = currentUnixTime + 0.2;
                        }
                        finally
                        {
                            sortedCreaturesByNextTick.AddLast(first); // All creatures tick at a fixed interval
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_Monster_Tick, stopwatch.Elapsed.TotalSeconds);
            }

            stopwatch.Restart();
            while (sortedGeneratorsByNextGeneratorUpdate.Count > 0)
            {
                var first = sortedGeneratorsByNextGeneratorUpdate.First.Value;

                // If they wanted to run before or at now
                if (first.NextGeneratorUpdateTime <= currentUnixTime)
                {
                    sortedGeneratorsByNextGeneratorUpdate.RemoveFirst();

                    // Remove-then-act, same contract as the Monster_Tick loop above: contain the throw, log
                    // the generator, and re-add in the finally so a bad generator is never silently dropped
                    // from the update list.
                    try
                    {
                        first.GeneratorUpdate(currentUnixTime);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.TickMultiThreadedWork: GeneratorUpdate threw for 0x{first.Guid}:{first.Name} [{first.WeenieClassId} - {first.WeenieType}]", ex);

                        // GeneratorUpdate sets NextGeneratorUpdateTime at its END, so a throw leaves it in the
                        // past. Bump it by the same +5 the happy path uses rather than calling back into the
                        // failing object, or this while loop re-picks it forever within a single tick.
                        if (first.NextGeneratorUpdateTime <= currentUnixTime)
                            first.NextGeneratorUpdateTime = currentUnixTime + 5;
                    }
                    finally
                    {
                        //InsertWorldObjectIntoSortedGeneratorUpdateList(first);
                        sortedGeneratorsByNextGeneratorUpdate.AddLast(first);
                    }
                }
                else
                {
                    break;
                }
            }
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_GeneratorUpdate, stopwatch.Elapsed.TotalSeconds);

            stopwatch.Restart();
            while (sortedGeneratorsByNextRegeneration.Count > 0) // GeneratorRegeneration()
            {
                var first = sortedGeneratorsByNextRegeneration.First.Value;

                //Console.WriteLine($"{first.Name}.Landblock_Tick_GeneratorRegeneration({currentUnixTime})");

                // If they wanted to run before or at now
                if (first.NextGeneratorRegenerationTime <= currentUnixTime)
                {
                    sortedGeneratorsByNextRegeneration.RemoveFirst();

                    // Remove-then-act, same contract as the loops above: contain the throw, log the
                    // generator, and re-insert in the finally so a bad generator is never silently dropped
                    // from the regeneration list.
                    try
                    {
                        first.GeneratorRegeneration(currentUnixTime);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.TickMultiThreadedWork: GeneratorRegeneration threw for 0x{first.Guid}:{first.Name} [{first.WeenieClassId} - {first.WeenieType}]", ex);

                        // GeneratorRegeneration sets NextGeneratorRegenerationTime at its END, so a throw
                        // leaves it in the past and the sorted re-insert below would put it straight back at
                        // the head. Back it off rather than calling into the failing object for a new time.
                        if (first.NextGeneratorRegenerationTime <= currentUnixTime)
                            first.NextGeneratorRegenerationTime = currentUnixTime + 5;
                    }
                    finally
                    {
                        InsertWorldObjectIntoSortedGeneratorRegenerationList(first); // Generators can have regnerations at different intervals
                    }
                }
                else
                {
                    break;
                }
            }
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_GeneratorRegeneration, stopwatch.Elapsed.TotalSeconds);

            // Heartbeat
            stopwatch.Restart();
            if (lastHeartBeat + heartbeatInterval <= DateTime.UtcNow)
            {
                var thisHeartBeat = DateTime.UtcNow;

                ProcessPendingWorldObjectAdditionsAndRemovals();

                // Decay world objects
                if (lastHeartBeat != DateTime.MinValue)
                {
                    foreach (var wo in worldObjects.Values)
                    {
                        if (wo.IsDecayable())
                            wo.Decay(thisHeartBeat - lastHeartBeat);
                    }
                }

                if (!Permaload && HasNoKeepAliveObjects)
                {
                    if (lastActiveTime + dormantInterval < thisHeartBeat)
                    {
                        if (!IsDormant)
                        {
                            var spellProjectiles = worldObjects.Values.Where(i => i is SpellProjectile).ToList();
                            foreach (var spellProjectile in spellProjectiles)
                            {
                                spellProjectile.PhysicsObj.set_active(false);
                                spellProjectile.Destroy();
                            }
                        }

                        IsDormant = true;
                    }
                    // players.Count is current here: the heartbeat block above called
                    // ProcessPendingWorldObjectAdditionsAndRemovals first, so anyone who arrived earlier in
                    // this world tick is already in the list rather than still staged in pendingAdditions.
                    if (ShouldQueueForUnload(players.Count, lastActiveTime, thisHeartBeat))
                        LandblockManager.AddToDestructionQueue(this);
                }

                //log.Info($"Landblock {Id.ToString()}.Tick({currentUnixTime}).Landblock_Tick_Heartbeat: thisHeartBeat: {thisHeartBeat.ToString()} | lastHeartBeat: {lastHeartBeat.ToString()} | worldObjects.Count: {worldObjects.Count()}");
                lastHeartBeat = thisHeartBeat;
            }
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_Heartbeat, stopwatch.Elapsed.TotalSeconds);

            // Database Save
            stopwatch.Restart();
            if (lastDatabaseSave + databaseSaveInterval <= DateTime.UtcNow)
            {
                ProcessPendingWorldObjectAdditionsAndRemovals();

                SaveDB();
                lastDatabaseSave = DateTime.UtcNow;
            }
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_Database_Save, stopwatch.Elapsed.TotalSeconds);

            Monitor5m.Pause();
            Monitor1h.Pause();
        }

        /// <summary>
        /// This will tick everything that should be done single threaded on the main ACE World thread
        /// This should be called after TickPhysics() and after Tick()
        /// </summary>
        public void TickSingleThreadedWork(double currentUnixTime)
        {
            if (monitorsRequireEventStart)
            {
                Monitor5m.Restart();
                Monitor1h.Restart();
            }
            else
            {
                Monitor5m.Resume();
                Monitor1h.Resume();
            }

            ProcessPendingWorldObjectAdditionsAndRemovals();

            stopwatch.Restart();
            foreach (var player in players)
            {
                // Per-player containment. LandblockManager's per-landblock catch keeps a throw here from
                // stopping the world, but it aborts the REST of TickSingleThreadedWork for this landblock -
                // every player after this one in the list, and the whole heartbeat loop below. Against a
                // deterministic fault (a corrupted biota field hit on every tick) that is not a transient
                // skip, it is an indefinite silent freeze for everyone else on the landblock, logged only as
                // an opaque per-landblock error. One bad player must cost one player's tick.
                //
                // No back-off or removal on failure: unlike the sorted lists below, this loop is a plain pass
                // over Landblock.players and re-picks nothing within a tick, so a repeat offender simply logs
                // once per tick rather than hot-looping. Removing them from the landblock from inside a catch
                // is a behavior change, not containment.
                try
                {
                    player.Player_Tick(currentUnixTime);
                }
                catch (Exception ex)
                {
                    log.Error($"Landblock 0x{Id}.TickSingleThreadedWork: Player_Tick threw for 0x{player.Guid}:{player.Name}", ex);
                }
            }
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_Player_Tick, stopwatch.Elapsed.TotalSeconds);

            stopwatch.Restart();
            while (sortedWorldObjectsByNextHeartbeat.Count > 0) // Heartbeat()
            {
                var first = sortedWorldObjectsByNextHeartbeat.First.Value;

                // If they wanted to run before or at now
                if (first.NextHeartbeatTime <= currentUnixTime)
                {
                    sortedWorldObjectsByNextHeartbeat.RemoveFirst();

                    // Remove-then-act: the object is already out of sortedWorldObjectsByNextHeartbeat, so an
                    // escaping throw would stop the world AND silently stop this object's heartbeat forever.
                    // Contain it, log the culprit, and re-insert in the finally.
                    try
                    {
                        first.Heartbeat(currentUnixTime);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Landblock 0x{Id}.TickSingleThreadedWork: Heartbeat threw for 0x{first.Guid}:{first.Name} [{first.WeenieClassId} - {first.WeenieType}]", ex);

                        // Heartbeat sets NextHeartbeatTime at its END, so after a throw it is always still in
                        // the past and the sorted re-insert below would put the object straight back at the
                        // head - an infinite loop inside this single tick. Bump it here by the default
                        // heartbeat cadence (CachedHeartbeatInterval is not visible from here) instead of
                        // calling back into the object that just failed.
                        if (first.NextHeartbeatTime <= currentUnixTime)
                            first.NextHeartbeatTime = currentUnixTime + 5;
                    }
                    finally
                    {
                        InsertWorldObjectIntoSortedHeartbeatList(first); // WorldObjects can have heartbeats at different intervals
                    }
                }
                else
                {
                    break;
                }
            }
            ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Landblock_Tick_WorldObject_Heartbeat, stopwatch.Elapsed.TotalSeconds);

            Monitor5m.RegisterEventEnd();
            Monitor1h.RegisterEventEnd();
            monitorsRequireEventStart = true;

            if (DateTime.UtcNow - last5mClear >= last5mClearInteval)
            {
                Monitor5m.ClearEventHistory();
                last5mClear = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - last1hClear >= last1hClearInteval)
            {
                Monitor1h.ClearEventHistory();
                last1hClear = DateTime.UtcNow;
            }
        }

        private void ProcessPendingWorldObjectAdditionsAndRemovals()
        {
            if (pendingAdditions.Count > 0)
            {
                foreach (var kvp in pendingAdditions)
                {
                    worldObjects[kvp.Key] = kvp.Value;

                    if (kvp.Value is Player player)
                        players.Add(player);
                    else if (kvp.Value is Creature creature)
                        sortedCreaturesByNextTick.AddLast(creature);

                    InsertWorldObjectIntoSortedHeartbeatList(kvp.Value);
                    InsertWorldObjectIntoSortedGeneratorUpdateList(kvp.Value);
                    InsertWorldObjectIntoSortedGeneratorRegenerationList(kvp.Value);

                    if (kvp.Value.WeenieClassId == 80007) // Landblock KeepAlive weenie (ACE custom)
                        HasNoKeepAliveObjects = false;
                }

                pendingAdditions.Clear();
            }

            if (pendingRemovals.Count > 0)
            {
                foreach (var objectGuid in pendingRemovals)
                {
                    if (worldObjects.Remove(objectGuid, out var wo))
                    {
                        if (wo is Player player)
                            players.Remove(player);
                        else if (wo is Creature creature)
                            sortedCreaturesByNextTick.Remove(creature);

                        sortedWorldObjectsByNextHeartbeat.Remove(wo);
                        sortedGeneratorsByNextGeneratorUpdate.Remove(wo);
                        sortedGeneratorsByNextRegeneration.Remove(wo);

                        if (wo.WeenieClassId == 80007) // Landblock KeepAlive weenie (ACE custom)
                        {
                            var keepAliveObject = worldObjects.Values.FirstOrDefault(w => w.WeenieClassId == 80007);

                            if (keepAliveObject == null)
                                HasNoKeepAliveObjects = true;
                        }
                    }
                }

                pendingRemovals.Clear();
            }
        }

        private void InsertWorldObjectIntoSortedHeartbeatList(WorldObject worldObject)
        {
            // If you want to add checks to exclude certain object types from heartbeating, you would do it here
            if (worldObject.NextHeartbeatTime == double.MaxValue)
                return;

            if (sortedWorldObjectsByNextHeartbeat.Count == 0)
            {
                sortedWorldObjectsByNextHeartbeat.AddFirst(worldObject);
                return;
            }

            if (sortedWorldObjectsByNextHeartbeat.Last.Value.NextHeartbeatTime <= worldObject.NextHeartbeatTime)
            {
                sortedWorldObjectsByNextHeartbeat.AddLast(worldObject);
                return;
            }

            var currentNode = sortedWorldObjectsByNextHeartbeat.First;

            while (currentNode != null)
            {
                if (worldObject.NextHeartbeatTime <= currentNode.Value.NextHeartbeatTime)
                {
                    sortedWorldObjectsByNextHeartbeat.AddBefore(currentNode, worldObject);
                    return;
                }

                currentNode = currentNode.Next;
            }

            sortedWorldObjectsByNextHeartbeat.AddLast(worldObject); // This line really shouldn't be hit
        }

        private void InsertWorldObjectIntoSortedGeneratorUpdateList(WorldObject worldObject)
        {
            // If you want to add checks to exclude certain object types from heartbeating, you would do it here
            if (worldObject.NextGeneratorUpdateTime == double.MaxValue)
                return;

            if (sortedGeneratorsByNextGeneratorUpdate.Count == 0)
            {
                sortedGeneratorsByNextGeneratorUpdate.AddFirst(worldObject);
                return;
            }

            if (sortedGeneratorsByNextGeneratorUpdate.Last.Value.NextGeneratorUpdateTime <= worldObject.NextGeneratorUpdateTime)
            {
                sortedGeneratorsByNextGeneratorUpdate.AddLast(worldObject);
                return;
            }

            var currentNode = sortedGeneratorsByNextGeneratorUpdate.First;

            while (currentNode != null)
            {
                if (worldObject.NextGeneratorUpdateTime <= currentNode.Value.NextGeneratorUpdateTime)
                {
                    sortedGeneratorsByNextGeneratorUpdate.AddBefore(currentNode, worldObject);
                    return;
                }

                currentNode = currentNode.Next;
            }

            sortedGeneratorsByNextGeneratorUpdate.AddLast(worldObject); // This line really shouldn't be hit
        }

        private void InsertWorldObjectIntoSortedGeneratorRegenerationList(WorldObject worldObject)
        {
            // If you want to add checks to exclude certain object types from heartbeating, you would do it here
            if (worldObject.NextGeneratorRegenerationTime == double.MaxValue)
                return;

            if (sortedGeneratorsByNextRegeneration.Count == 0)
            {
                sortedGeneratorsByNextRegeneration.AddFirst(worldObject);
                return;
            }

            if (sortedGeneratorsByNextRegeneration.Last.Value.NextGeneratorRegenerationTime <= worldObject.NextGeneratorRegenerationTime)
            {
                sortedGeneratorsByNextRegeneration.AddLast(worldObject);
                return;
            }

            var currentNode = sortedGeneratorsByNextRegeneration.First;

            while (currentNode != null)
            {
                if (worldObject.NextGeneratorRegenerationTime <= currentNode.Value.NextGeneratorRegenerationTime)
                {
                    sortedGeneratorsByNextRegeneration.AddBefore(currentNode, worldObject);
                    return;
                }

                currentNode = currentNode.Next;
            }

            sortedGeneratorsByNextRegeneration.AddLast(worldObject); // This line really shouldn't be hit
        }

        public void ResortWorldObjectIntoSortedGeneratorRegenerationList(WorldObject worldObject)
        {
            if (sortedGeneratorsByNextRegeneration.Contains(worldObject))
            {
                sortedGeneratorsByNextRegeneration.Remove(worldObject);
                InsertWorldObjectIntoSortedGeneratorRegenerationList(worldObject);
            }
        }

        public void EnqueueAction(IAction action)
        {
            actionQueue.EnqueueAction(action);
        }

        /// <summary>
        /// This will fail if the wo doesn't have a valid location.
        /// </summary>
        public bool AddWorldObject(WorldObject wo)
        {
            if (wo.Location == null)
            {
                log.DebugFormat("Landblock 0x{0} failed to add 0x{1:X8} {2}. Invalid Location", Id, wo.Biota.Id, wo.Name);
                return false;
            }

            return AddWorldObjectInternal(wo);
        }

        public void AddWorldObjectForPhysics(WorldObject wo)
        {
            AddWorldObjectInternal(wo);
        }

        private bool AddWorldObjectInternal(WorldObject wo)
        {
            if (LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded)
            {
                if (CurrentLandblockGroup != null && CurrentLandblockGroup != LandblockManager.CurrentMultiThreadedTickingLandblockGroup.Value)
                {
                    // Prevent possible multi-threaded crash
                    // The following scenario can happen rarely in ACE, all in the same call stack with no ActionQueue usage:
                    // Moster successfully lands an attack on a player that procs a cloak spell
                    // The code goes through and does the LaunchSpellProjectiles() which adds the spell projectiles to (presumably) the players landblock
                    // For some unknown reason, the LandblockGroup/Thread where the monster exists seems to be a different LandblockGroup/Thread where the spells are added to.
                    // Maybe there's a player death race condition? Maybe it's a teleport race condition? I dunno.
                    // Because this happens so rarely, and, it only seems to affect cloak projectiles, and, cloak projectiles are pretty benign, we simply don't add the object, and only log it as a warning.
                    if (wo.WeenieType == WeenieType.ProjectileSpell)
                    {
                        log.Warn($"Landblock 0x{Id} entered AddWorldObjectInternal in a cross-thread operation for a ProjectileSpell. This is normally not an issue unless it's happening more than once an hour.");
                        return false;
                    }

                    log.Error($"Landblock 0x{Id} entered AddWorldObjectInternal in a cross-thread operation.");
                    log.Error($"Landblock 0x{Id} CurrentLandblockGroup: {CurrentLandblockGroup}");
                    log.Error($"LandblockManager.CurrentMultiThreadedTickingLandblockGroup.Value: {LandblockManager.CurrentMultiThreadedTickingLandblockGroup.Value}");

                    log.Error($"wo: 0x{wo.Guid}:{wo.Name} [{wo.WeenieClassId} - {wo.WeenieType}], previous landblock 0x{wo.CurrentLandblock?.Id}");

                    if (wo.WeenieType == WeenieType.ProjectileSpell)
                    {
                        if (wo.ProjectileSource != null)
                            log.Error($"wo.ProjectileSource: 0x{wo.ProjectileSource?.Guid}:{wo.ProjectileSource?.Name}, position: {wo.ProjectileSource?.Location}");

                        if (wo.ProjectileTarget != null)
                            log.Error($"wo.ProjectileTarget: 0x{wo.ProjectileTarget?.Guid}:{wo.ProjectileTarget?.Name}, position: {wo.ProjectileTarget?.Location}");
                    }

                    log.Error(System.Environment.StackTrace);

                    log.Error("PLEASE REPORT THIS TO THE ACE DEV TEAM !!!");

                    // This may still crash...
                }
            }

            // A Setup DID that does not name a parseable SetupModel is a content defect that used to
            // terminate the world thread from inside InitPhysicsObj (see WorldObject_Setup.cs).
            // Refuse the object here instead, on the same terms as any other failed spawn.
            if (!WorldObject.IsValidSetupId(wo.SetupTableId))
            {
                // ERROR once per wcid, Debug thereafter: a generator wired to a bad-setup wcid re-enters
                // this path on every regen tick, and an unbounded ERROR stream would bury the first report.
                var message = $"AddWorldObjectInternal: couldn't spawn 0x{wo.Guid}:{wo.Name} [{wo.WeenieClassId} - {wo.WeenieType}] at {wo.Location?.ToLOCString()} - PropertyDataId.Setup 0x{wo.SetupTableId:X8} is not a valid setup model";

                if (WorldObject.ShouldReportBadSetupId("AddWorldObjectInternal", wo.WeenieClassId))
                    log.Error(message);
                else if (log.IsDebugEnabled)
                    log.Debug(message);

                wo.CurrentLandblock = null;

                if (wo.Generator != null)
                    wo.NotifyOfEvent(RegenerationType.PickUp); // Notify generator the generated object is effectively destroyed, use Pickup to catch both cases.

                return false;
            }

            wo.CurrentLandblock = this;

            // an object arriving from another instance (e.g. a player teleporting into
            // an ephemeral copy) must be re-registered under its new instance
            if (wo.PhysicsObj != null && wo.PhysicsObj.KnownInstance != Instance)
            {
                Physics.Managers.ServerObjectManager.RemoveServerObject(wo.PhysicsObj);
                wo.PhysicsObj.KnownInstance = Instance;
                Physics.Managers.ServerObjectManager.AddServerObject(wo.PhysicsObj);
            }

            if (wo.PhysicsObj == null)
                wo.InitPhysicsObj();
            else
                wo.PhysicsObj.set_object_guid(wo.Guid, Instance);  // re-add to ServerObjectManager

            if (wo.PhysicsObj.CurCell == null)
            {
                var success = wo.AddPhysicsObj();
                if (!success)
                {
                    wo.CurrentLandblock = null;

                    if (wo.Generator != null)
                    {
                        if (log.IsDebugEnabled)
                            log.Debug($"AddWorldObjectInternal: couldn't spawn 0x{wo.Guid}:{wo.Name} [{wo.WeenieClassId} - {wo.WeenieType}] at {wo.Location.ToLOCString()} from generator {wo.Generator.WeenieClassId} - 0x{wo.Generator.Guid}:{wo.Generator.Name}");
                        wo.NotifyOfEvent(RegenerationType.PickUp); // Notify generator the generated object is effectively destroyed, use Pickup to catch both cases.
                    }
                    else if (wo.IsGenerator) // Some generators will fail random spawns if they're circumference spans over water or cliff edges
                    {
                        if (log.IsDebugEnabled)
                            log.Debug($"AddWorldObjectInternal: couldn't spawn generator 0x{wo.Guid}:{wo.Name} [{wo.WeenieClassId} - {wo.WeenieType}] at {wo.Location.ToLOCString()}");
                    }
                    else if (wo.ProjectileTarget == null && !(wo is SpellProjectile))
                        log.Warn($"AddWorldObjectInternal: couldn't spawn 0x{wo.Guid}:{wo.Name} [{wo.WeenieClassId} - {wo.WeenieType}] at {wo.Location.ToLOCString()}");

                    return false;
                }
            }

            if (!worldObjects.ContainsKey(wo.Guid))
                pendingAdditions[wo.Guid] = wo;
            else
                pendingRemovals.Remove(wo.Guid);

            // broadcast to nearby players
            wo.NotifyPlayers();

            if (wo is Player player)
                player.SetFogColor(FogColor);

            if (wo is Corpse && wo.Level.HasValue)
            {
                var corpseLimit = PropertyManager.GetLong("corpse_spam_limit").Item;
                var corpseList = worldObjects.Values.Union(pendingAdditions.Values).Where(w => w is Corpse && w.Level.HasValue && w.VictimId == wo.VictimId).OrderBy(w => w.CreationTimestamp);

                if (corpseList.Count() > corpseLimit)
                {
                    var corpse = GetObject(corpseList.First(w => w.TimeToRot > Corpse.EmptyDecayTime).Guid);

                    if (corpse != null)
                    {
                        log.Warn($"[CORPSE] Landblock.AddWorldObjectInternal(): {wo.Name} (0x{wo.Guid}) exceeds the per player limit of {corpseLimit} corpses for 0x{Id.Landblock:X4}. Adjusting TimeToRot for oldest {corpse.Name} (0x{corpse.Guid}), CreationTimestamp: {corpse.CreationTimestamp} ({Common.Time.GetDateTimeFromTimestamp(corpse.CreationTimestamp ?? 0).ToLocalTime():yyyy-MM-dd HH:mm:ss}), to Corpse.EmptyDecayTime({Corpse.EmptyDecayTime}).");
                        corpse.TimeToRot = Corpse.EmptyDecayTime;
                    }
                }
            }

            return true;
        }

        public void RemoveWorldObject(ObjectGuid objectId, bool adjacencyMove = false, bool fromPickup = false, bool showError = true)
        {
            RemoveWorldObjectInternal(objectId, adjacencyMove, fromPickup, showError);
        }

        /// <summary>
        /// Should only be called by physics/relocation engines -- not from player
        /// </summary>
        /// <param name="objectId">The object ID to be removed from the current landblock</param>
        /// <param name="adjacencyMove">Flag indicates if object is moving to an adjacent landblock</param>
        public void RemoveWorldObjectForPhysics(ObjectGuid objectId, bool adjacencyMove = false)
        {
            RemoveWorldObjectInternal(objectId, adjacencyMove);
        }

        private void RemoveWorldObjectInternal(ObjectGuid objectId, bool adjacencyMove = false, bool fromPickup = false, bool showError = true)
        {
            if (LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded)
            {
                if (CurrentLandblockGroup != null && CurrentLandblockGroup != LandblockManager.CurrentMultiThreadedTickingLandblockGroup.Value)
                {
                    log.Error($"Landblock 0x{Id} entered RemoveWorldObjectInternal in a cross-thread operation.");
                    log.Error($"Landblock 0x{Id} CurrentLandblockGroup: {CurrentLandblockGroup}");
                    log.Error($"LandblockManager.CurrentMultiThreadedTickingLandblockGroup.Value: {LandblockManager.CurrentMultiThreadedTickingLandblockGroup.Value}");

                    log.Error($"objectId: 0x{objectId}");

                    log.Error(System.Environment.StackTrace);

                    log.Error("PLEASE REPORT THIS TO THE ACE DEV TEAM !!!");

                    // This may still crash...
                }
            }

            if (worldObjects.TryGetValue(objectId, out var wo))
                pendingRemovals.Add(objectId);
            else if (!pendingAdditions.Remove(objectId, out wo))
            {
                if (showError)
                    log.Warn($"RemoveWorldObjectInternal: Couldn't find {objectId.Full:X8}");
                return;
            }

            wo.CurrentLandblock = null;

            // Weenies can come with a default of 0 (Instant Rot) or -1 (Never Rot). If they still have that value, we want to retain it.
            // We also want to make sure fromPickup is true so that we're not clearing out TimeToRot on server shutdown (unloads all landblocks and removed all objects).
            if (fromPickup && wo.TimeToRot.HasValue && wo.TimeToRot != 0 && wo.TimeToRot != -1)
                wo.TimeToRot = null;

            if (!adjacencyMove)
            {
                // really remove it - send message to client to remove object.
                //
                // the DeleteObject payload is the object guid plus its ObjectInstance sequence, with
                // no recipient-dependent component, so one message serves every recipient (admins
                // included) instead of one construction per player inside RemoveTrackedObject.
                // Built eagerly here rather than lazily, because EnqueueActionBroadcast defers the
                // delegate onto each player's own action queue - a lazy init inside the delegate
                // would not be running on a single thread.
                //
                // ObjectInstance is only ever assigned at login (Player.PlayerEnterWorld); nothing
                // advances it during removal, so building the message now yields the same bytes the
                // deferred per-player construction produced. fromPickup takes the PickupEvent branch
                // in RemoveTrackedObject and sends no DeleteObject at all.
                var sharedDelete = fromPickup ? null : new GameMessageDeleteObject(wo);

                wo.EnqueueActionBroadcast(p => p.RemoveTrackedObject(wo, fromPickup, sharedDelete));

                wo.PhysicsObj.DestroyObject();
            }
        }

        public void EmitSignal(WorldObject emitter, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            foreach (var wo in worldObjects.Values.Where(w => w.HearLocalSignals).ToList())
            {
                if (emitter == wo) continue;

                if (emitter.IsWithinUseRadiusOf(wo, wo.HearLocalSignalsRadius))
                {
                    //Console.WriteLine($"{wo.Name}.EmoteManager.OnLocalSignal({emitter.Name}, {message})");
                    wo.EmoteManager.OnLocalSignal(emitter, message);
                }
            }
        }

        /// <summary>
        /// Check to see if we are close enough to interact.   Adds a fudge factor of 1.5f
        /// </summary>
        public bool WithinUseRadius(Player player, ObjectGuid targetGuid, out bool validTargetGuid, float? useRadius = null)
        {
            var target = GetObject(targetGuid);

            validTargetGuid = target != null;

            if (target != null)
                return player.IsWithinUseRadiusOf(target, useRadius);

            return false;
        }

        /// <summary>
        /// Returns landblock objects with physics initialized
        /// </summary>
        public ICollection<WorldObject> GetWorldObjectsForPhysicsHandling()
        {
            // If a missile is destroyed when it runs it's UpdateObjectPhysics(), it will remove itself from the landblock, thus, modifying the worldObjects collection.

            ProcessPendingWorldObjectAdditionsAndRemovals();

            return worldObjects.Values;
        }

        public List<WorldObject> GetAllWorldObjectsForDiagnostics()
        {
            // We do not ProcessPending here, and we return ToList() to avoid cross-thread issues.
            // This can happen if we "loadalllandblocks" and do a "serverstatus".
            return worldObjects.Values.ToList();
        }

        public WorldObject GetObject(uint objectId)
        {
            return GetObject(new ObjectGuid(objectId));
        }

        /// <summary>
        /// This will return null if the object was not found in the current or adjacent landblocks.
        /// </summary>
        public WorldObject GetObject(ObjectGuid guid, bool searchAdjacents = true)
        {
            if (pendingRemovals.Contains(guid))
                return null;

            if (worldObjects.TryGetValue(guid, out var worldObject) || pendingAdditions.TryGetValue(guid, out worldObject))
                return worldObject;

            if (searchAdjacents)
            {
                foreach (Landblock lb in Adjacents)
                {
                    if (lb != null)
                    {
                        var wo = lb.GetObject(guid, false);

                        if (wo != null)
                            return wo;
                    }
                }
            }

            return null;
        }

        public WorldObject GetWieldedObject(uint objectGuid, bool searchAdjacents = true)
        {
            return GetWieldedObject(new ObjectGuid(objectGuid), searchAdjacents); // todo fix
        }

        /// <summary>
        /// Searches this landblock (and possibly adjacents) for an ObjectGuid wielded by a creature
        /// </summary>
        public WorldObject GetWieldedObject(ObjectGuid guid, bool searchAdjacents = true)
        {
            // search creature wielded items in current landblock
            var creatures = worldObjects.Values.OfType<Creature>();
            foreach (var creature in creatures)
            {
                var wieldedItem = creature.GetEquippedItem(guid);
                if (wieldedItem != null)
                {
                    if ((wieldedItem.CurrentWieldedLocation & EquipMask.Selectable) != 0)
                        return wieldedItem;

                    return null;
                }
            }

            // try searching adjacent landblocks if not found
            if (searchAdjacents)
            {
                foreach (var adjacent in Adjacents)
                {
                    if (adjacent == null) continue;

                    var wieldedItem = adjacent.GetWieldedObject(guid, false);
                    if (wieldedItem != null)
                        return wieldedItem;
                }
            }
            return null;
        }

        /// <summary>
        /// Sets a landblock to active state, with the current time as the LastActiveTime
        /// </summary>
        /// <param name="isAdjacent">Public calls to this function should always set isAdjacent to false</param>
        public void SetActive(bool isAdjacent = false)
        {
            lastActiveTime = DateTime.UtcNow;
            IsDormant = false;

            if (isAdjacent || PhysicsLandblock == null || PhysicsLandblock.IsDungeon) return;

            // for outdoor landblocks, recursively call 1 iteration to set adjacents to active
            foreach (var landblock in Adjacents)
            {
                if (landblock != null)
                    landblock.SetActive(true);
            }
        }

        /// <summary>
        /// Last-moment check, immediately before the destructive work: TRUE when this landblock may
        /// actually be torn down, FALSE when a player has arrived since it was queued.
        ///
        /// <para/>
        /// <see cref="ShouldQueueForUnload"/> is a check-instant guarantee at the heartbeat, and the
        /// heartbeat is not the last word. Several arrival paths never touch WorldManager's action queue and
        /// run LATER in the same world tick than the heartbeat that queued this landblock: PortalRecall and
        /// PortalSending call Player.Teleport directly on the player's own action queue, which is pumped
        /// from TickSingleThreadedWork, and so does every direct session.Player.Teleport in the admin,
        /// developer, instance and advocate command handlers. A player who lands that way is staged into
        /// pendingAdditions after the heartbeat has already decided, and LandblockManager.UnloadLandblocks
        /// then runs at the end of the same tick. This method is what makes "never unload a landblock a
        /// player is standing on" a tick-wide invariant rather than a per-heartbeat one; the two together
        /// are what close the hole.
        ///
        /// <para/>
        /// The refusal must be honoured by the CALLER, which is why this returns bool instead of being an
        /// early return inside <see cref="Unload"/>: UnloadLandblocks goes on to remove the landblock from
        /// loadedLandblocks, from its landblock group, from the adjacency caches and from the landblock
        /// dictionary regardless of what Unload did. An early return inside Unload alone would deregister
        /// the landblock everywhere while its objects, its player and its physics cells stayed behind - a
        /// stuck, un-ticked landblock, strictly worse than the bug being fixed.
        /// </summary>
        public bool TryClaimForDestruction()
        {
            // a player staged during this tick is still in pendingAdditions; flush before counting
            ProcessPendingWorldObjectAdditionsAndRemovals();

            if (players.Count == 0)
                return true;

            // Do not hand a genuinely occupied landblock straight back to the next heartbeat, which would
            // re-queue it immediately and spin. This is the same pair of assignments SetActive makes, minus
            // its recursion into Adjacents: the arriving player's own heartbeat owns that, and refreshing
            // neighbours from inside the drain loop would touch landblocks it is concurrently draining.
            lastActiveTime = DateTime.UtcNow;
            IsDormant = false;

            return false;
        }

        /// <summary>
        /// Handles the cleanup process for a landblock
        /// This method is called by LandblockManager
        /// </summary>
        public void Unload()
        {
            var landblockID = Id.Raw | 0xFFFF;

            //log.DebugFormat("Landblock.Unload({0:X8})", landblockID);

            ProcessPendingWorldObjectAdditionsAndRemovals();

            SaveDB();

            // remove all objects
            // ephemeral instances fully destroy their contents (including any db rows
            // written mid-session) - nothing in a temporary instance survives it
            foreach (var wo in worldObjects.ToList())
            {
                if (!wo.Value.BiotaOriginatedFromOrHasBeenSavedToDatabase() || (IsEphemeral && !(wo.Value is Player)))
                    wo.Value.Destroy(false, true);
                else
                    RemoveWorldObjectInternal(wo.Key);
            }

            ProcessPendingWorldObjectAdditionsAndRemovals();

            actionQueue.Clear();

            // remove physics landblock
            LScape.unload_landblock(landblockID, Instance);

            PhysicsLandblock.release_shadow_objs();
        }

        public void DestroyAllNonPlayerObjects()
        {
            ProcessPendingWorldObjectAdditionsAndRemovals();

            SaveDB();

            // remove all objects
            foreach (var wo in worldObjects.Where(i => !(i.Value is Player)).ToList())
            {
                if (!wo.Value.BiotaOriginatedFromOrHasBeenSavedToDatabase())
                    wo.Value.Destroy(false);
                else
                    RemoveWorldObjectInternal(wo.Key);
            }

            ProcessPendingWorldObjectAdditionsAndRemovals();

            actionQueue.Clear();
        }

        private void SaveDB()
        {
            // ephemeral instances are never persisted - nothing inside them may
            // overwrite base-world state (statics share guids across instances)
            if (IsEphemeral)
                return;

            var biotas = new Collection<(Biota biota, ReaderWriterLockSlim rwLock)>();

            foreach (var wo in worldObjects.Values)
            {
                if (wo.IsStaticThatShouldPersistToShard() || wo.IsDynamicThatShouldPersistToShard())
                    AddWorldObjectToBiotasSaveCollection(wo, biotas);
            }

            DatabaseManager.Shard.SaveBiotasInParallel(biotas, null);
        }

        private void AddWorldObjectToBiotasSaveCollection(WorldObject wo, Collection<(Biota biota, ReaderWriterLockSlim rwLock)> biotas)
        {
            if (wo.ChangesDetected)
            {
                wo.SaveBiotaToDatabase(false);
                biotas.Add((wo.Biota, wo.BiotaDatabaseLock));
            }

            if (wo is Container container)
            {
                foreach (var item in container.Inventory.Values)
                    AddWorldObjectToBiotasSaveCollection(item, biotas);
            }
        }

        /// <summary>
        /// This is only used for very specific instances, such as broadcasting player deaths to the destination lifestone block
        /// This is a rarely used method to broadcast network messages to all of the players within a landblock,
        /// and possibly the adjacent landblocks.
        /// </summary>
        public void EnqueueBroadcast(ICollection<Player> excludeList, bool adjacents, Position pos = null, float? maxRangeSq = null, params GameMessage[] msgs)
        {
            var players = worldObjects.Values.OfType<Player>();

            // for landblock death broadcasts:
            // exclude players that have already been broadcast to within range of the death
            if (excludeList != null)
                players = players.Except(excludeList);

            // broadcast messages to player in this landblock
            foreach (var player in players)
            {
                if (pos != null && maxRangeSq != null)
                {
                    var distSq = player.Location.SquaredDistanceTo(pos);
                    if (distSq > maxRangeSq)
                        continue;
                }
                player.Session.Network.EnqueueSend(msgs);
            }

            // if applicable, iterate into adjacent landblocks
            if (adjacents)
            {
                foreach (var adjacent in this.Adjacents.Where(adj => adj != null))
                    adjacent.EnqueueBroadcast(excludeList, false, pos, maxRangeSq, msgs);
            }
        }

        private bool? isDungeon;

        /// <summary>
        /// Returns TRUE if this landblock is a dungeon,
        /// with no traversable overworld
        /// </summary>
        public bool IsDungeon
        {
            get
            {
                // return cached value
                if (isDungeon != null)
                    return isDungeon.Value;

                // hack for NW island
                // did a worldwide analysis for adding watercells into the formula,
                // but they are inconsistently defined for some of the edges of map unfortunately
                if (Id.LandblockX < 0x08 && Id.LandblockY > 0xF8)
                {
                    isDungeon = false;
                    return isDungeon.Value;
                }

                // a dungeon landblock is determined by:
                // - all heights being 0
                // - having at least 1 EnvCell (0x100+)
                // - contains no buildings
                foreach (var height in CellLandblock.Height)
                {
                    if (height != 0)
                    {
                        isDungeon = false;
                        return isDungeon.Value;
                    }
                }
                isDungeon = LandblockInfo != null && LandblockInfo.NumCells > 0 && LandblockInfo.Buildings != null && LandblockInfo.Buildings.Count == 0;
                return isDungeon.Value;
            }
        }

        private bool? hasDungeon;

        /// <summary>
        /// Returns TRUE if this landblock contains a dungeon
        //
        /// If a landblock contains both a dungeon + traversable overworld,
        /// this field will return TRUE, whereas IsDungeon will return FALSE
        /// 
        /// This property should only be used in very specific scenarios,
        /// such as determining if a landblock contains a mansion basement
        /// </summary>
        public bool HasDungeon
        {
            get
            {
                // return cached value
                if (hasDungeon != null)
                    return hasDungeon.Value;

                hasDungeon = LandblockInfo != null && LandblockInfo.NumCells > 0 && LandblockInfo.Buildings != null && LandblockInfo.Buildings.Count == 0;
                return hasDungeon.Value;
            }
        }


        public List<House> Houses = new List<House>();

        public void SetFogColor(EnvironChangeType environChangeType)
        {
            if (environChangeType.IsFog())
            {
                FogColor = environChangeType;

                foreach (var lb in Adjacents)
                    lb.FogColor = environChangeType;

                foreach(var player in players)
                {
                    player.SetFogColor(FogColor);
                }
            }
        }

        public void SendEnvironSound(EnvironChangeType environChangeType)
        {
            if (environChangeType.IsSound())
            {
                SendEnvironChange(environChangeType);

                foreach (var lb in Adjacents)
                    lb.SendEnvironChange(environChangeType);
            }
        }

        public void SendEnvironChange(EnvironChangeType environChangeType)
        {
            foreach (var player in players)
            {
                player.SendEnvironChange(environChangeType);
            }
        }

        public void SendCurrentEnviron()
        {
            foreach (var player in players)
            {
                if (FogColor.IsFog())
                {
                    player.SetFogColor(FogColor);
                }
                else
                {
                    player.SendEnvironChange(FogColor);
                }
            }
        }

        public void DoEnvironChange(EnvironChangeType environChangeType)
        {
            if (environChangeType.IsFog())
                SetFogColor(environChangeType);
            else
                SendEnvironSound(environChangeType);
        }
    }
}
