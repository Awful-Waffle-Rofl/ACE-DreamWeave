using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Landblock = ACE.Server.Entity.Landblock;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Shared builders for the Thread encounter tests (ThreadEncounterStoreTests, ThreadEncounterPickerTests,
    /// ThreadEncounterPassTests, OutdoorEncounterPlacementTests). No world, no database, no PropertyManager read.
    /// </summary>
    internal static class ThreadEncounterTestFixtures
    {
        public const ushort DungeonLb = 0x0150;

        public static uint DungeonCell(int n = 0x018A) => ((uint)DungeonLb << 16) | (uint)n;

        public static uint OutdoorCell(ushort landblock, float x, float y)
            => ((uint)landblock << 16) | (uint)((int)(x / 24) * 8 + (int)(y / 24) + 1);

        public static DungeonSpawnPointDef Point(uint cell, float x, float y, float z = 0f, float clearance = 4f, int depth = 3, string kind = null, bool curated = true)
            => new DungeonSpawnPointDef { Cell = cell, X = x, Y = y, Z = z, Clearance = clearance, Depth = depth, Kind = kind, Curated = curated };

        public static ThreadEncounterTypeDef Type(string id, string kind = "fake", double weight = 1.0, bool enabled = true, Action<ThreadEncounterPlacementDef> placement = null)
        {
            var p = new ThreadEncounterPlacementDef();
            placement?.Invoke(p);
            return new ThreadEncounterTypeDef { Id = id, Kind = kind, Weight = weight, Enabled = enabled, Placement = p };
        }

        /// <summary>A candidate at a dungeon point, with explicit entry and boss distances.</summary>
        public static ThreadEncounterCandidate Candidate(DungeonSpawnPointDef p, float entry = 100f, float boss = 100f, bool outdoor = false)
            => new ThreadEncounterCandidate(p, ThreadEncounterGeometry.Global(p, outdoor), entry, boss);

        public static ThreadEncounterPickRequest Request(IEnumerable<ThreadEncounterTypeDef> types, IEnumerable<ThreadEncounterCandidate> candidates,
            int seed = 7, int max = 2, double chance = 1.0, bool outdoor = false)
            => new ThreadEncounterPickRequest
            {
                Types = types.ToList(),
                Candidates = candidates.ToList(),
                Enabled = true,
                MaxPerRun = max,
                SlotChance = chance,
                RunSeed = seed,
                IsOutdoor = outdoor,
            };

        /// <summary>A dungeon run on landblock 0x0150 over <paramref name="points"/>, Active, stamped with encounters ON, with a populate context.</summary>
        public static ThreadDungeonRun DungeonRun(IEnumerable<DungeonSpawnPointDef> points, int seed = 11, int max = 2, double chance = 1.0, bool context = true)
        {
            var dungeon = new DungeonEntryDef
            {
                Id = "enc_test",
                Landblock = DungeonLb,
                ExitPortalWcid = 1003601,
                Entry = "0x0150018A 0 0 0 1 0 0 0",
                Points = points.ToList(),
                BossAnchor = Point(DungeonCell(0x0190), 200f, 0f),
            };

            return Run(dungeon, seed, max, chance, context);
        }

        /// <summary>An outdoor run (kind outdoor, primary 0x5456) over <paramref name="points"/>, Active, encounters ON.</summary>
        public static ThreadDungeonRun OutdoorRun(IEnumerable<DungeonSpawnPointDef> points, int seed = 11, int max = 4, double chance = 1.0,
            IEnumerable<ushort> loadedLandblocks = null)
        {
            var dungeon = new DungeonEntryDef
            {
                Id = "ow_test",
                Kind = DungeonEntryDef.KindOutdoor,
                Landblock = 0x5456,
                ExitPortalWcid = 1003604,
                Entry = $"0x{OutdoorCell(0x5456, 10, 10):X8} 10 10 0 1 0 0 0",
                Points = points.ToList(),
                BossAnchor = Point(OutdoorCell(0x5456, 100, 100), 100f, 100f, kind: "outdoor"),
            };

            return Run(dungeon, seed, max, chance, true, loadedLandblocks);
        }

        private static ThreadDungeonRun Run(DungeonEntryDef dungeon, int seed, int max, double chance, bool context, IEnumerable<ushort> loaded = null)
        {
            var spec = new DungeonGemSpec(dungeon.Id, 200, 6, "any", seed, new (string, double)[0], 0, 0);
            var run = new ThreadDungeonRun(0x80005678u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180))
            {
                EncountersEnabled = true,
                EncountersMaxPerRun = max,
                EncounterSlotChance = chance,
            };

            // One planned and placed creature: the run is Active and stays so (nothing killed).
            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);

            if (context)
            {
                var map = (loaded ?? Enumerable.Empty<ushort>()).ToDictionary(id => id, id => (Landblock)null);
                run.MarkEncounterContext(new ThreadEncounterRunContext(null, 0u, null, map, null));
            }

            return run;
        }

        /// <summary>An encounters store holding <paramref name="types"/>.</summary>
        public static ThreadEncounterStore Store(params ThreadEncounterTypeDef[] types)
        {
            var json = "{\"version\":1,\"types\":[" + string.Join(",", types.Select(TypeJson)) + "]}";
            return ThreadEncounterStore.Parse(json, null, new List<string>());
        }

        private static string TypeJson(ThreadEncounterTypeDef t)
        {
            var p = t.Placement ?? new ThreadEncounterPlacementDef();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return "{" +
                   $"\"id\":\"{t.Id}\",\"kind\":\"{t.Kind}\",\"enabled\":{(t.Enabled ? "true" : "false")},\"weight\":{t.Weight.ToString(inv)}," +
                   "\"placement\":{" +
                   $"\"minClearance\":{p.MinClearance.ToString(inv)},\"minEntryWalk\":{p.MinEntryWalk.ToString(inv)},\"minBossDist\":{p.MinBossDist.ToString(inv)},\"minSpacing\":{p.MinSpacing.ToString(inv)}" +
                   (p.MinDepth.HasValue ? $",\"minDepth\":{p.MinDepth.Value}" : "") +
                   (p.MaxDepth.HasValue ? $",\"maxDepth\":{p.MaxDepth.Value}" : "") +
                   (p.RunKinds != null && p.RunKinds.Count > 0 ? ",\"runKinds\":[" + string.Join(",", p.RunKinds.Select(k => $"\"{k}\"")) + "]" : "") +
                   "}}";
        }

        /// <summary>A recording encounter type.</summary>
        internal sealed class FakeType : IThreadEncounterType
        {
            public FakeType(string kind = "fake") { Kind = kind; }

            public string Kind { get; }

            public Func<ThreadEncounterPlaceRequest, (bool Ok, string Failure)> OnPlace { get; set; }

            public List<ThreadEncounterPlaceRequest> Placed { get; } = new List<ThreadEncounterPlaceRequest>();

            public List<(ThreadEncounterInstance Instance, string Why)> Ended { get; } = new List<(ThreadEncounterInstance, string)>();

            public Func<ThreadEncounterTypeDef, ThreadEncounterCandidate, string> Reject { get; set; }

            public string RejectPoint(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, bool isOutdoor) => Reject?.Invoke(def, candidate);

            public Func<ThreadEncounterTypeDef, ThreadDungeonRun, string> RunReject { get; set; }

            public string RejectRun(ThreadEncounterTypeDef def, ThreadDungeonRun run) => RunReject?.Invoke(def, run);

            public bool Place(ThreadEncounterPlaceRequest request, out string failure)
            {
                Placed.Add(request);
                var (ok, why) = OnPlace?.Invoke(request) ?? (true, null);
                failure = why;
                return ok;
            }

            public void OnRunEnded(ThreadEncounterInstance instance, string why) => Ended.Add((instance, why));
        }

        /// <summary>
        /// Saves every ThreadEncounterPass seam and the type registry, installs test doubles (a store, a type source, a
        /// recording enqueue that defers the action, no walk graph, an accept-all probe) and restores all of it on Dispose.
        /// </summary>
        internal sealed class Seams : IDisposable
        {
            private readonly Func<ThreadEncounterStore> store = ThreadEncounterPass.StoreSource;
            private readonly Func<string, IThreadEncounterType> types = ThreadEncounterPass.TypeSource;
            private readonly Func<ushort, DungeonWalkGraph> walk = ThreadEncounterPass.WalkGraphSource;
            private readonly ThreadEncounterPass.GroundProbeFn probe = ThreadEncounterPass.GroundProbe;
            private readonly ThreadEncounterPass.EnqueueFn enqueue = ThreadEncounterPass.EnqueueOnLandblock;
            private readonly ThreadEncounterPass.ResolveFn resolve = ThreadEncounterPass.ResolveLandblock;
            private readonly Action<ushort, Landblock, Action> post = ThreadEncounterPass.PostToLandblock;

            public List<(ushort Landblock, Action<Landblock> Action)> Queued { get; } = new List<(ushort, Action<Landblock>)>();

            public Dictionary<string, IThreadEncounterType> Registry { get; } = new Dictionary<string, IThreadEncounterType>(StringComparer.Ordinal);

            public List<(DungeonSpawnPointDef Point, float Radius)> Probed { get; } = new List<(DungeonSpawnPointDef, float)>();

            public Func<ThreadEncounterCandidate, float, bool> Probe { get; set; } = (c, r) => true;

            public Seams(ThreadEncounterStore encounterStore, params IThreadEncounterType[] impls)
            {
                foreach (var impl in impls)
                    Registry[impl.Kind] = impl;

                ThreadEncounterPass.StoreSource = () => encounterStore;
                ThreadEncounterPass.TypeSource = kind => kind != null && Registry.TryGetValue(kind, out var t) ? t : null;
                ThreadEncounterPass.WalkGraphSource = _ => null;
                ThreadEncounterPass.GroundProbe = (run, c, r) => { Probed.Add((c.Point, r)); return Probe(c, r); };
                ThreadEncounterPass.EnqueueOnLandblock = (run, id, action) => { Queued.Add((id, action)); return true; };
            }

            /// <summary>Use the production enqueue (the run's populate-context landblock map) instead of the recorder.</summary>
            public void UseProductionEnqueue() => ThreadEncounterPass.EnqueueOnLandblock = enqueue;

            /// <summary>Runs every queued action (each with a null landblock), in order, and clears the queue.</summary>
            public void Drain()
            {
                var pending = Queued.ToList();
                Queued.Clear();

                foreach (var (_, action) in pending)
                    action(null);
            }

            public void Dispose()
            {
                ThreadEncounterPass.StoreSource = store;
                ThreadEncounterPass.TypeSource = types;
                ThreadEncounterPass.WalkGraphSource = walk;
                ThreadEncounterPass.GroundProbe = probe;
                ThreadEncounterPass.EnqueueOnLandblock = enqueue;
                ThreadEncounterPass.ResolveLandblock = resolve;
                ThreadEncounterPass.PostToLandblock = post;
            }

            /// <summary>
            /// The production enqueue with its two inner seams faked: landblock ids in <paramref name="held"/> resolve (to a
            /// null Landblock), anything else does not; every post is recorded into <see cref="Queued"/> under its id.
            /// </summary>
            public void UseProductionEnqueueWithHeld(params ushort[] held)
            {
                ThreadEncounterPass.EnqueueOnLandblock = enqueue;

                var heldSet = new HashSet<ushort>(held);
                ThreadEncounterPass.ResolveLandblock = (ThreadDungeonRun run, ushort id, out Landblock landblock) => { landblock = null; return heldSet.Contains(id); };
                ThreadEncounterPass.PostToLandblock = (id, landblock, action) => Queued.Add((id, _ => action()));
            }
        }
    }
}
