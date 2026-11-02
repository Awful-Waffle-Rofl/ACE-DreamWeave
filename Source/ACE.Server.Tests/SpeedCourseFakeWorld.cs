using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.PuzzleGates;
using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A recording <see cref="ISpeedCourseWorld"/> for the course controller's tests. No landblock, no database, no
    /// PropertyManager. <see cref="Post"/> queues; <see cref="Drain"/> runs the queue (and anything it queues) to empty.
    /// </summary>
    internal sealed class SpeedCourseFakeWorld : ISpeedCourseWorld
    {
        public const uint FakeInstance = 0x80089123u;

        public uint Instance { get; set; } = FakeInstance;

        public bool CreateWorldObjectsCompleted { get; set; } = true;

        public SpeedRunnerView RunnerView { get; set; } = new SpeedRunnerView { Online = true, BoundHere = true };

        public DungeonSpawnPlan Plan { get; set; }

        public List<PuzzleSiteDef> Sites { get; } = new List<PuzzleSiteDef>();

        public Dictionary<uint, int> Levels { get; } = new Dictionary<uint, int>();

        public HashSet<uint> FailingWcids { get; } = new HashSet<uint>();

        public Dictionary<string, bool> Seals { get; } = new Dictionary<string, bool>();

        public int BuildPlanCalls;
        public int ReapCalls;
        public int ClearCalls;
        public bool RefusePuzzles;

        /// <summary>Site ids whose every placement is refused.</summary>
        public readonly HashSet<string> RefuseSites = new HashSet<string>();

        public readonly List<(DungeonSpawnPlanEntry Entry, SpeedCreatureStamp Stamp, SpeedCreatureHandle Handle)> Spawns
            = new List<(DungeonSpawnPlanEntry, SpeedCreatureStamp, SpeedCreatureHandle)>();

        public readonly List<(PuzzleSiteDef Site, ThreadPuzzlePick Pick, PuzzleGateOptions Options, IPuzzleGateHost Host, int Id)> Placements
            = new List<(PuzzleSiteDef, ThreadPuzzlePick, PuzzleGateOptions, IPuzzleGateHost, int)>();

        public readonly List<(string Key, string Token, double Weight)> Contributions = new List<(string, string, double)>();

        public readonly HashSet<uint> Dead = new HashSet<uint>();
        public readonly Dictionary<uint, double> Health = new Dictionary<uint, double>();

        public readonly Queue<Action> Posted = new Queue<Action>();

        public readonly List<string> Info = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();

        private uint nextGuid = 0x80001000;
        private int nextPlacement = 500;

        /// <summary>Max health handed back for each spawn, by wcid; 1000 otherwise.</summary>
        public Dictionary<uint, uint> MaxHealthByWcid { get; } = new Dictionary<uint, uint>();

        public SpeedRunnerView Runner() => RunnerView;

        /// <summary>The gem spec of the latest BuildPlan call.</summary>
        public DungeonGemSpec LastSpec { get; private set; }

        public DungeonSpawnPlan BuildPlan(DungeonGemSpec spec, out string error)
        {
            LastSpec = spec;
            BuildPlanCalls++;
            error = Plan == null ? "no plan in the fake" : null;
            return Plan;
        }

        public int LevelOf(uint wcid) => Levels.TryGetValue(wcid, out var l) ? l : 0;

        /// <summary>What PickFallbackBoss returns (0 = the family fields nothing).</summary>
        public uint FallbackBoss { get; set; }

        public readonly List<(string Family, int Level, HashSet<uint> Drawn)> FallbackBossCalls = new List<(string, int, HashSet<uint>)>();

        public uint PickFallbackBoss(string family, int level, IReadOnlyCollection<uint> alreadyDrawn, Random rng)
        {
            FallbackBossCalls.Add((family, level, new HashSet<uint>(alreadyDrawn ?? Array.Empty<uint>())));
            return FallbackBoss;
        }

        public readonly List<(uint Wcid, DungeonSpawnPointDef Point)> PlacedItems = new List<(uint, DungeonSpawnPointDef)>();

        /// <summary>True: every PlaceItem fails (and is not recorded); PlaceItemCalls still counts it.</summary>
        public bool FailPlaceItem;
        public int PlaceItemCalls;

        public bool PlaceItem(uint wcid, DungeonSpawnPointDef point)
        {
            PlaceItemCalls++;

            if (FailPlaceItem)
                return false;

            PlacedItems.Add((wcid, point));
            return true;
        }

        /// <summary>Runs inside PlacePuzzle before it records the placement (e.g. to end the run mid-placement).</summary>
        public Action<PuzzleSiteDef> OnPlacePuzzle;

        /// <summary>True: PuzzleSites throws (a failure in puzzle placement outside any one slot).</summary>
        public bool ThrowOnPuzzleSites;

        public IReadOnlyList<PuzzleSiteDef> PuzzleSites(string dungeonId)
            => ThrowOnPuzzleSites ? throw new InvalidOperationException("puzzle sites threw in the fake") : Sites;

        public SpeedCreatureHandle Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, SpeedCreatureStamp stamp, out uint maxHealth)
        {
            maxHealth = 0;

            if (FailingWcids.Contains(entry.Wcid))
            {
                Spawns.Add((entry, stamp, SpeedCreatureHandle.None));
                return SpeedCreatureHandle.None;
            }

            var handle = new SpeedCreatureHandle(nextGuid++);
            maxHealth = MaxHealthByWcid.TryGetValue(entry.Wcid, out var h) ? h : 1000u;
            Spawns.Add((entry, stamp, handle));
            MaxHp[handle.Guid] = maxHealth;

            // The spawn-time guard the live world writes before EnterWorld.
            if (stamp?.GuardFloorFraction != null)
                Guards[handle.Guid] = (stamp.GuardFloorFraction.Value >= 1.0, SpeedBossPhaseRules.FloorFor(maxHealth, stamp.GuardFloorFraction.Value));
            return handle;
        }

        public bool IsAlive(SpeedCreatureHandle h) => !h.IsNone && Spawns.Any(s => s.Handle.Equals(h)) && !Dead.Contains(h.Guid);

        public double HealthFraction(SpeedCreatureHandle h) => IsAlive(h) ? (Health.TryGetValue(h.Guid, out var f) ? f : 1.0) : 0.0;

        /// <summary>The max health the spawn handed back, by guid.</summary>
        public readonly Dictionary<uint, uint> MaxHp = new Dictionary<uint, uint>();

        public uint MaxHealth(SpeedCreatureHandle h) => IsAlive(h) && MaxHp.TryGetValue(h.Guid, out var m) ? m : 0u;

        /// <summary>Current health: MaxHealth x the Health fraction (1.0 unless a test set one), floored at the guard's floor like Creature.UpdateVital.</summary>
        public uint CurrentHealth(SpeedCreatureHandle h)
        {
            if (!IsAlive(h))
                return 0u;

            var raw = (uint)Math.Round(MaxHealth(h) * HealthFraction(h));
            return Guards.TryGetValue(h.Guid, out var g) && g.Floor > 0 && raw < g.Floor ? g.Floor : raw;
        }

        /// <summary>The live guard per guid, and every SetBossGuard call in order.</summary>
        public readonly Dictionary<uint, (bool Immune, uint Floor)> Guards = new Dictionary<uint, (bool, uint)>();
        public readonly List<(uint Guid, bool Immune, uint Floor)> GuardCalls = new List<(uint, bool, uint)>();

        public void SetBossGuard(SpeedCreatureHandle h, bool immune, uint floorHp)
        {
            if (!IsAlive(h))
                return;

            Guards[h.Guid] = (immune, floorHp);
            GuardCalls.Add((h.Guid, immune, floorHp));
        }

        /// <summary>Sets a creature's raw health fraction; <see cref="CurrentHealth"/> reads it through the guard's floor (Creature.UpdateVital's pin).</summary>
        public void Damage(SpeedCreatureHandle h, double fraction) => Health[h.Guid] = fraction;

        public readonly Dictionary<uint, (float X, float Y)> Positions = new Dictionary<uint, (float, float)>();

        public bool TryGetPosition(SpeedCreatureHandle h, out float x, out float y)
        {
            x = 0f;
            y = 0f;

            if (!IsAlive(h) || !Positions.TryGetValue(h.Guid, out var p))
                return false;

            x = p.X;
            y = p.Y;
            return true;
        }

        public bool? SealLatched(string key)
        {
            OnSealLatched?.Invoke(key);
            return Seals.TryGetValue(key, out var latched) ? latched : (bool?)null;
        }

        /// <summary>Called on every SealLatched read, before it answers (a test can End the run mid-tick from here).</summary>
        public Action<string> OnSealLatched;

        public int PlacePuzzle(PuzzleSiteDef site, ThreadPuzzlePick pick, PuzzleGateOptions options, IPuzzleGateHost host, out string error)
        {
            OnPlacePuzzle?.Invoke(site);

            if (RefusePuzzles || RefuseSites.Contains(site.Id))
            {
                error = "refused by the fake";
                return 0;
            }

            error = null;
            var id = ++nextPlacement;
            Placements.Add((site, pick, options, host, id));
            return id;
        }

        public void ReapPuzzles(DateTime now) => ReapCalls++;

        public int ClearPuzzles()
        {
            ClearCalls++;
            return Placements.Count;
        }

        /// <summary>The Seal's live tokens: every Contribute lands here; a test adds one directly to stand in for a contributor object (the lectern).</summary>
        public readonly HashSet<string> SealTokens = new HashSet<string>();

        /// <summary>The next this-many Contribute calls throw before recording anything.</summary>
        public int ThrowOnContribute;

        /// <summary>The next this-many LogError calls throw after recording the line (a failure inside a spawn step).</summary>
        public int ThrowOnLogError;

        public void Contribute(string key, string token, double weight)
        {
            if (ThrowOnContribute > 0)
            {
                ThrowOnContribute--;
                throw new InvalidOperationException("contribute threw in the fake");
            }

            Contributions.Add((key, token, weight));
            SealTokens.Add(token);
        }

        public bool? SealHasToken(string key, string token) => SealTokens.Contains(token);

        public readonly List<string> Tells = new List<string>();

        public void Tell(string text)
        {
            if (!string.IsNullOrEmpty(text))
                Tells.Add(text);
        }

        /// <summary>Every SetSealDoor call, in order (key, shut).</summary>
        public readonly List<(string Key, bool Shut)> SealDoorCalls = new List<(string, bool)>();

        public void SetSealDoor(string key, bool shut)
        {
            SealDoorCalls.Add((key, shut));
            OnSetSealDoor?.Invoke(key, shut);
        }

        /// <summary>Called after every SetSealDoor is recorded (a test can End the run from inside the shut).</summary>
        public Action<string, bool> OnSetSealDoor;

        public void Post(Action action) => Posted.Enqueue(action);

        public void Drain()
        {
            var guard = 0;

            while (Posted.Count > 0)
            {
                if (++guard > 10000)
                    throw new InvalidOperationException("the posted queue never drained");

                Posted.Dequeue()();
            }
        }

        public void Log(string line) => Info.Add(line);

        public void LogWarn(string line) => Warnings.Add(line);

        public void LogError(string line)
        {
            Errors.Add(line);

            if (ThrowOnLogError > 0)
            {
                ThrowOnLogError--;
                throw new InvalidOperationException("log error threw in the fake");
            }
        }

        // ---- fixtures -------------------------------------------------------------------------------------

        public static PuzzleSiteDef Site(string id, int maxN, params PuzzleGateType[] types)
        {
            var site = new PuzzleSiteDef
            {
                Id = id,
                Kind = PuzzleSiteKind.Gate,
                Anchor = new PuzzleSitePointDef { Cell = 0x00890148, X = 118, Y = -210, Z = -12 },
                Yaw = 270,
                MaxN = maxN,
                Types = types.ToList(),
                GateModel = new PuzzleGateModelDef { Kind = PuzzleGateModelKind.Door, Wcid = 1006854, Scale = 0.958f },
            };

            if (types.Contains(PuzzleGateType.Shuffle))
            {
                site.ShuffleSpots.Add(new PuzzleSitePointDef { Cell = 0x00890148, X = 117, Y = -209, Z = -12 });
                site.ShuffleSpots.Add(new PuzzleSitePointDef { Cell = 0x00890148, X = 117, Y = -211, Z = -12 });
            }

            return site;
        }

        /// <summary>The Black Book site plan (GEOMETRY.md): P1 and P3 offer all four types, P2 beam and shuffle only.</summary>
        public void AddBlackBookSites()
        {
            var all = new[] { PuzzleGateType.Sigil, PuzzleGateType.Beam, PuzzleGateType.Odd, PuzzleGateType.Shuffle };
            Sites.Add(Site("bb_p1", 5, all));
            Sites.Add(Site("bb_p2", 3, PuzzleGateType.Beam, PuzzleGateType.Shuffle));
            Sites.Add(Site("bb_p3", 5, all));
        }

        public static DungeonSpawnPointDef Point(float x, float y) => new DungeonSpawnPointDef { Cell = 0x00890150, X = x, Y = y, Z = -12 };

        /// <summary>Six trash, two elites and a boss, the boss last (the builder's order).</summary>
        public static DungeonSpawnPlan SamplePlan()
        {
            var plan = new DungeonSpawnPlan { BossWcid = 300 };

            for (var i = 0; i < 6; i++)
                plan.Entries.Add(new DungeonSpawnPlanEntry(100u + (uint)i, DungeonRole.Trash, Point(10 + i, -10)));

            plan.Entries.Add(new DungeonSpawnPlanEntry(200, DungeonRole.Elite, Point(40, -40)));
            plan.Entries.Add(new DungeonSpawnPlanEntry(201, DungeonRole.Elite, Point(41, -41)));
            plan.Entries.Add(new DungeonSpawnPlanEntry(300, DungeonRole.Boss, Point(99, -99)));

            return plan;
        }
    }

    /// <summary>A hazard driver that records what the run told it.</summary>
    internal sealed class RecordingHazardDriver : ISpeedHazardDriver
    {
        public readonly List<SpeedCourseRunSnapshot> Ticks = new List<SpeedCourseRunSnapshot>();
        public readonly List<string> Stops = new List<string>();

        public void Tick(SpeedCourseRun run, in SpeedCourseRunSnapshot snapshot) => Ticks.Add(snapshot);

        public void Stop(SpeedCourseRun run, string reason) => Stops.Add(reason);
    }
}
