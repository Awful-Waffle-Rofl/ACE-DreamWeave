using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.SpeedCourses
{
    /// <summary>
    /// The live <see cref="ISpeedCourseWorld"/>: one course run's ephemeral instance landblock. Every member except
    /// <see cref="Post"/> and <see cref="ClearPuzzles"/> runs on that landblock's thread (see the port's contract).
    /// </summary>
    public sealed class LiveSpeedCourseWorld : ISpeedCourseWorld
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Landblock landblock;
        private readonly uint ownerGuid;

        /// <summary>Spawned creatures by guid. Landblock thread only.</summary>
        private readonly Dictionary<uint, Creature> creatures = new Dictionary<uint, Creature>();

        public LiveSpeedCourseWorld(Landblock landblock, uint ownerGuid)
        {
            this.landblock = landblock ?? throw new ArgumentNullException(nameof(landblock));
            this.ownerGuid = ownerGuid;
        }

        public Landblock Landblock => landblock;

        public uint Instance => landblock.Instance;

        public bool CreateWorldObjectsCompleted => landblock.CreateWorldObjectsCompleted;

        private string Tag => $"speed:0x{Instance:X8}";

        public SpeedRunnerView Runner()
        {
            var player = PlayerManager.GetOnlinePlayer(ownerGuid);

            if (player == null)
                return new SpeedRunnerView { Online = false };

            var location = player.Location;
            var inside = location != null && location.Instance == Instance;

            return new SpeedRunnerView
            {
                Online = true,
                BoundHere = inside && player.SpeedChallengeActive && player.SpeedRunInstance == Instance,
                ClockRebased = player.SpeedRunClockRebased,
                HasPosition = inside,
                Cell = inside ? location.Cell : 0,
                X = inside ? location.PositionX : 0f,
                Y = inside ? location.PositionY : 0f,
                Z = inside ? location.PositionZ : 0f,
            };
        }

        public DungeonSpawnPlan BuildPlan(DungeonGemSpec spec, out string error)
        {
            error = null;

            var store = ThreadDungeonManager.Store;

            if (store == null || spec == null || !store.Dungeons.TryGetValue(spec.DungeonId ?? "", out var dungeon) || dungeon == null)
            {
                error = $"dungeon '{spec?.DungeonId}' is not a Threads index row";
                return null;
            }

            // One creature per curated point: the same point filter the builder applies (curated, not the boss anchor).
            var anchor = dungeon.BossAnchor;
            var curated = dungeon.Points.Count(p => p != null && p.Curated
                && !(anchor != null && (ReferenceEquals(p, anchor) || (p.Cell == anchor.Cell && p.X == anchor.X && p.Y == anchor.Y && p.Z == anchor.Z))));

            var limits = SpeedCourseRules.CourseLimits(ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo), curated);
            var fits = ThreadDungeonSpawner.ResolveFitPredicate(dungeon, out _);

            var plan = DungeonPopulationBuilder.Build(spec, dungeon, store, WorldEventHooks.Store.SpeciesTables,
                ThreadDungeonSpawner.LiveLevelOf, ThreadDungeonSpawner.LiveHealthOf, limits, new Random(spec.Seed),
                ThreadDungeonSpawner.LiveProfileOf, fits);

            // The offline coverage test runs on compiled defaults; live dials (band floor, pool size, fit filter) can differ.
            var shortfall = SpeedCourseRules.ShortPlanWarning(plan, curated);

            if (shortfall != null)
                log.Warn($"[SPEEDCOURSE] instance 0x{Instance:X8} dungeon={spec.DungeonId} level={spec.Level} family={spec.Family}: {shortfall}");

            return plan;
        }

        public int LevelOf(uint wcid) => ThreadDungeonSpawner.LiveLevelOf(wcid);

        public uint PickFallbackBoss(string family, int level, IReadOnlyCollection<uint> alreadyDrawn, Random rng)
        {
            var tables = WorldEventHooks.Store?.SpeciesTables;

            if (family == null || tables == null || !tables.TryGetValue(family, out var table) || table == null)
                return 0;

            return DungeonRosterSelector.PickBossCandidate(table, level, ThreadDungeonSpawner.LiveLevelOf, rng, alreadyDrawn);
        }

        public bool PlaceItem(uint wcid, DungeonSpawnPointDef point)
        {
            if (wcid == 0 || point == null)
                return false;

            WorldObject wo;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            }
            catch (Exception ex)
            {
                log.Error($"[SPEEDCOURSE] {Tag} could not create objective wcid {wcid}", ex);
                return false;
            }

            if (wo == null)
                return false;

            wo.Location = new Position(point.Cell, point.X, point.Y, point.Z, point.QX, point.QY, point.QZ, point.QW, Instance);

            try
            {
                if (wo.EnterWorld())
                    return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SPEEDCOURSE] {Tag} EnterWorld threw for objective wcid {wcid}", ex);
            }

            try { wo.Destroy(); } catch { }
            return false;
        }

        public IReadOnlyList<PuzzleSiteDef> PuzzleSites(string id)
            => ThreadDungeonManager.Store?.GetPuzzleSites(id) ?? (IReadOnlyList<PuzzleSiteDef>)Array.Empty<PuzzleSiteDef>();

        public SpeedCreatureHandle Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, SpeedCreatureStamp stamp, out uint maxHealth)
        {
            maxHealth = 0;

            if (plan == null || entry?.Point == null)
                return SpeedCreatureHandle.None;

            stamp ??= new SpeedCreatureStamp();

            WorldObject wo;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);
            }
            catch (Exception ex)
            {
                log.Error($"[SPEEDCOURSE] {Tag} could not create wcid {entry.Wcid}", ex);
                return SpeedCreatureHandle.None;
            }

            if (wo is not Creature creature)
            {
                log.Warn($"[SPEEDCOURSE] {Tag} wcid {entry.Wcid} is not a Creature; skipped");
                wo?.Destroy();
                return SpeedCreatureHandle.None;
            }

            if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
            {
                log.Error($"[SPEEDCOURSE] {Tag} wcid {entry.Wcid} ({entry.Role}) is not attackable or is an NPC; not placed");
                creature.Destroy();
                return SpeedCreatureHandle.None;
            }

            var p = entry.Point;
            creature.Location = new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, Instance);

            if (creature.Location.LandblockId.Landblock != landblock.Id.Landblock)
            {
                log.Warn($"[SPEEDCOURSE] {Tag} point {creature.Location.ToLOCString()} is not on the course landblock 0x{landblock.Id.Landblock:X4}; dropped");
                creature.Destroy();
                return SpeedCreatureHandle.None;
            }

            // The Threads shaping, exactly as a solo Thread run applies it, minus the run stamp and the rewards.
            ThreadDungeonSpawner.ShapeCreature(creature, plan, entry, stamp.ObservedMaxNonBoss, new CreatureShapeOptions
            {
                Run = null,
                Rewards = false,
                GroupEffort = GroupScaling.Solo.Effort,
                LogTag = Tag,
            });

            // Speed kills must not advance retail kill tasks (the puzzle-gate ambush strip, PuzzleGateManager.PrepareAmbush).
            creature.RemoveProperty(PropertyString.KillQuest);
            creature.RemoveProperty(PropertyString.KillQuest2);
            creature.RemoveProperty(PropertyString.KillQuest3);

            // Speed kills feed no kill-fill vessel and no mule-form token (Creature.FeedsKillProgressHooks).
            creature.P_SpeedCourseCreature = true;

            if (stamp.DisplayName != null)
                creature.Name = stamp.DisplayName;

            // The runtime scale, before EnterWorld so the first CreateObject already carries it (items scale with it).
            if (stamp.Scale > 0f && Math.Abs(stamp.Scale - 1f) > 1e-4f)
                creature.ApplyRuntimeScaleMultiplier(stamp.Scale);

            // The phased boss's guard, before EnterWorld: ApplyMonsterEffectOverlay allocates a fresh effect-state array
            // (LivePinnacleWorld.Spawn's order). The record is inert until P_DigsiteImmune is set; SetBossGuard toggles
            // both later. The entry phase (fraction 1) starts immune and pinned at full health.
            if (stamp.GuardFloorFraction.HasValue)
            {
                if (!creature.ApplyMonsterEffectOverlay(BossImmuneRecord))
                    log.Warn($"[SPEEDCOURSE] {Tag} immune record resolved to nothing on wcid {entry.Wcid}; the boss guard keeps only its health floor");

                creature.P_HealthFloor = SpeedBossPhaseRules.FloorFor(creature.Health.MaxValue, stamp.GuardFloorFraction.Value);
                creature.P_DigsiteImmune = stamp.GuardFloorFraction.Value >= 1.0;
            }

            if (stamp.ObjectiveKey != null)
            {
                creature.ObjectiveLockKey = stamp.ObjectiveKey;
                creature.ObjectiveLockToken = stamp.ObjectiveToken;
                creature.ObjectiveLockWeight = stamp.ObjectiveWeight;
            }

            // ShapeCreature replaced the create list with an empty one; the boss gets a FRESH list of its own holding the
            // objective item, never an edit of a collection the cached weenie may share.
            if (stamp.TreasureWcid != 0)
            {
                creature.Biota.PropertiesCreateList = new System.Collections.ObjectModel.Collection<PropertiesCreateList>
                {
                    new PropertiesCreateList
                    {
                        DestinationType = DestinationType.Treasure,
                        WeenieClassId = stamp.TreasureWcid,
                        StackSize = 1,
                        Shade = 0f,
                    },
                };
            }

            try
            {
                if (!creature.EnterWorld())
                {
                    log.Warn($"[SPEEDCOURSE] {Tag} EnterWorld refused wcid {entry.Wcid} at {creature.Location.ToLOCString()}");
                    creature.Destroy();
                    return SpeedCreatureHandle.None;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[SPEEDCOURSE] {Tag} EnterWorld threw for wcid {entry.Wcid}", ex);
                try { creature.Destroy(); } catch { }
                return SpeedCreatureHandle.None;
            }

            maxHealth = creature.Health.MaxValue;
            creatures[creature.Guid.Full] = creature;
            return new SpeedCreatureHandle(creature.Guid.Full);
        }

        private Creature Get(SpeedCreatureHandle h) => !h.IsNone && creatures.TryGetValue(h.Guid, out var c) ? c : null;

        public bool IsAlive(SpeedCreatureHandle h)
        {
            var c = Get(h);
            return c != null && !c.IsDead && !c.IsDestroyed;
        }

        public double HealthFraction(SpeedCreatureHandle h)
        {
            var c = Get(h);

            if (c == null || c.IsDead || c.IsDestroyed || c.Health.MaxValue == 0)
                return 0.0;

            return Math.Clamp(c.Health.Current / (double)c.Health.MaxValue, 0.0, 1.0);
        }

        public uint MaxHealth(SpeedCreatureHandle h)
        {
            var c = Get(h);
            return c == null || c.IsDead || c.IsDestroyed ? 0u : c.Health.MaxValue;
        }

        public uint CurrentHealth(SpeedCreatureHandle h)
        {
            var c = Get(h);
            return c == null || c.IsDead || c.IsDestroyed ? 0u : c.Health.Current;
        }

        /// <summary>The monster-effect record the phased boss carries; ImmuneEffect reads P_DigsiteImmune (LivePinnacleWorld.ImmuneRecord).</summary>
        public const string BossImmuneRecord = "immune";

        public void SetBossGuard(SpeedCreatureHandle h, bool immune, uint floorHp)
        {
            var c = Get(h);

            if (c == null || c.IsDead || c.IsDestroyed)
                return;

            var changed = c.P_DigsiteImmune != immune;
            c.P_DigsiteImmune = immune;
            c.P_HealthFloor = floorHp;

            // The grey shield cue the digsite immune phase and the Pinnacle use. Shield-up plays on every immune call, since
            // the phase machine calls immune exactly once per phase start, and the entry phase's boss is already immune
            // from spawn (no cue can play before EnterWorld). Shield-down plays only on a real change.
            if (immune || changed)
                ACE.Server.MlDigsite.MlDigsiteProps.PlayScriptOn(c, immune ? PlayScript.ShieldUpGrey : PlayScript.ShieldDownGrey);
        }

        public bool TryGetPosition(SpeedCreatureHandle h, out float x, out float y)
        {
            x = 0f;
            y = 0f;

            var c = Get(h);
            var location = c?.Location;

            if (c == null || c.IsDead || c.IsDestroyed || location == null || location.Instance != Instance
                || location.LandblockId.Landblock != landblock.Id.Landblock)
                return false;

            x = location.PositionX;
            y = location.PositionY;
            return true;
        }

        public bool? SealLatched(string key)
        {
            var gate = FindGate(key);
            return gate == null ? null : gate.P_ObjectiveLock?.Latched == true;
        }

        public bool? SealHasToken(string key, string token)
        {
            var gate = FindGate(key);
            return gate == null ? null : gate.P_ObjectiveLock?.HasToken(token, DateTime.UtcNow) == true;
        }

        public void SetSealDoor(string key, bool shut)
        {
            if (FindGate(key) is not Door door)
            {
                log.Warn($"[SPEEDCOURSE] {Tag} SetSealDoor({key}, {shut}): the objective gate is missing or not a Door; nothing done");
                return;
            }

            if (shut)
            {
                // Refuse every use first (Door.SealedShut), then lock and close, and tell clients about the lock
                // (they render and message the locked state themselves; the same broadcast OpenObjectiveGate sends).
                door.SealedShut = true;

                if (!door.IsLocked)
                {
                    door.IsLocked = true;
                    door.EnqueueBroadcast(new GameMessagePublicUpdatePropertyBool(door, PropertyBool.Locked, true));
                }

                door.Close(door.Guid);
                return;
            }

            door.SealedShut = false;
            WorldObject.OpenObjectiveGate(door, door);
        }

        public void Tell(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var player = PlayerManager.GetOnlinePlayer(ownerGuid);
            player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        // The resolved gate, cached per key: the run polls the Seal several times per tick (latch plus one check per
        // token), and each uncached poll was a full scan of the landblock's objects. Landblock thread only.
        private string cachedGateKey;
        private WorldObject cachedGate;

        /// <summary>
        /// The objective gate keyed <paramref name="key"/> contributions route to (lowest guid), or null. Cached; the cache
        /// is re-resolved when the gate is destroyed, leaves this landblock, or stops being that key's gate.
        /// </summary>
        private WorldObject FindGate(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            var cached = cachedGate;

            if (cached != null && string.Equals(cachedGateKey, key, StringComparison.Ordinal) && !cached.IsDestroyed
                && ReferenceEquals(cached.CurrentLandblock, landblock) && cached.IsObjectiveGate
                && string.Equals(cached.ObjectiveLockKey, key, StringComparison.Ordinal))
                return cached;

            var gate = ScanForGate(key);
            cachedGateKey = key;
            cachedGate = gate;
            return gate;
        }

        private WorldObject ScanForGate(string key)
        {

            var candidates = new List<(uint Guid, string Key, int Required)>();
            var byGuid = new Dictionary<uint, WorldObject>();

            foreach (var wo in landblock.GetAllWorldObjectsForDiagnostics())
            {
                if (!wo.IsObjectiveGate || wo.ObjectiveLockKey == null)
                    continue;

                candidates.Add((wo.Guid.Full, wo.ObjectiveLockKey, wo.ObjectiveLockRequired ?? 0));
                byGuid[wo.Guid.Full] = wo;
            }

            // The gate contributions actually route to (lowest guid), so the poll reads the lock that can latch.
            var guid = WorldObject.SelectObjectiveGateGuid(candidates, key, out _);

            return guid.HasValue && byGuid.TryGetValue(guid.Value, out var gate) ? gate : null;
        }

        public int PlacePuzzle(PuzzleSiteDef site, ThreadPuzzlePick pick, PuzzleGateOptions options, IPuzzleGateHost host, out string error)
        {
            // The same construction ThreadPuzzlePass.PlacePick uses for a curated gate site.
            var rotation = PuzzleGateGenerator.YawQuaternion(site.Yaw);
            var anchor = new Position(site.Anchor.Cell, site.Anchor.X, site.Anchor.Y, site.Anchor.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, Instance);

            var model = ThreadPuzzlePass.ModelFor(site, false);

            if (model == null)
            {
                error = "no placeable gate model";
                return 0;
            }

            var spots = pick.Type == PuzzleGateType.Shuffle
                ? site.ShuffleSpots.Where(s => s != null).Select(s => new Vector3(s.X, s.Y, s.Z)).ToList()
                : new List<Vector3>();

            var placement = PuzzleGateManager.PlaceForRun(landblock, options, pick.Seed, anchor, site.Yaw, spots, host, model, site.Layout, out error, site.Id);

            return placement?.Id ?? 0;
        }

        public void ReapPuzzles(DateTime now) => PuzzleGateManager.ReapForRun(Instance, now);

        public int ClearPuzzles() => PuzzleGateManager.ClearForRun(Instance);

        public void Contribute(string key, string token, double weight)
            => WorldObject.ContributeToObjectiveLock(landblock, key, token, weight, null);

        public void Post(Action action) => landblock.EnqueueAction(new ActionEventDelegate(action));

        public void Log(string line) => log.Info(line);

        public void LogWarn(string line) => log.Warn(line);

        public void LogError(string line) => log.Error(line);
    }
}
