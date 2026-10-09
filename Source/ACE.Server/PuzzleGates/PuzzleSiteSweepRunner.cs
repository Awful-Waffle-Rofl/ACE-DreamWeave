using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;

using log4net;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.PuzzleGates
{
    /// <summary>Parsed puzzlegate-sweep arguments.</summary>
    public sealed class SweepArgs
    {
        /// <summary>Null = every enabled dungeon with sites.</summary>
        public string DungeonId { get; init; }

        /// <summary>Null = every eligible type.</summary>
        public IReadOnlyList<PuzzleGateType> Types { get; init; }

        public string OutPath { get; init; }

        public bool WriteDisabled { get; init; }

        /// <summary>The puzzle-gates.json --write-disabled edits; null = the one the server loaded (ThreadDungeonStore.ResolveDataFolder).</summary>
        public string JsonPath { get; init; }

        /// <summary>Run even with players online (the probe takes landblock-thread time on the world tick).</summary>
        public bool Force { get; init; }

        /// <summary>Seeds swept per (site, role, type) row: 1 (default) to <see cref="MaxSeeds"/>.</summary>
        public int Seeds { get; init; } = 1;

        public const int MaxSeeds = 10;

        public const string Usage = "puzzlegate-sweep [<dungeonId>|all] [types=sigil,beam,odd,shuffle] [seeds=<1-10>] [out=<tsv path>] [--write-disabled] [json=<puzzle-gates.json path>] [--force]";

        /// <summary>Pure.</summary>
        public static bool TryParse(IReadOnlyList<string> tokens, out SweepArgs args, out string error)
        {
            args = null;
            error = null;

            string dungeon = null, outPath = null, json = null;
            List<PuzzleGateType> types = null;
            var write = false;
            var force = false;
            var seeds = 1;

            foreach (var token in tokens ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (string.Equals(token, "--write-disabled", StringComparison.OrdinalIgnoreCase))
                {
                    write = true;
                    continue;
                }

                if (string.Equals(token, "--force", StringComparison.OrdinalIgnoreCase))
                {
                    force = true;
                    continue;
                }

                var eq = token.IndexOf('=');

                if (eq < 0)
                {
                    if (dungeon != null)
                    {
                        error = $"unexpected '{token}': one dungeon id (or all) only.";
                        return false;
                    }

                    dungeon = token;
                    continue;
                }

                var key = token.Substring(0, eq).ToLowerInvariant();
                var value = token.Substring(eq + 1);

                switch (key)
                {
                    case "types":
                        types = new List<PuzzleGateType>();

                        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!PuzzleGateOptions.TryParseType(name, out var t))
                            {
                                error = $"unknown type '{name}'. Expected sigil, beam, odd or shuffle.";
                                return false;
                            }

                            if (!types.Contains(t))
                                types.Add(t);
                        }

                        if (types.Count == 0)
                        {
                            error = "types= needs at least one type.";
                            return false;
                        }

                        break;

                    case "out":
                        outPath = value;
                        break;

                    case "seeds":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seeds) || seeds < 1 || seeds > MaxSeeds)
                        {
                            error = $"seeds= takes 1 to {MaxSeeds}.";
                            return false;
                        }

                        break;

                    case "json":
                        json = value;
                        break;

                    default:
                        error = $"unknown option '{token}'.";
                        return false;
                }
            }

            if (string.Equals(dungeon, "all", StringComparison.OrdinalIgnoreCase))
                dungeon = null;

            args = new SweepArgs { DungeonId = dungeon, Types = types, OutPath = outPath, WriteDisabled = write, JsonPath = json, Force = force, Seeds = seeds };
            return true;
        }
    }

    /// <summary>The live half of puzzlegate-sweep. Server console only; see PuzzleSiteSweep for the checks.</summary>
    public static class PuzzleSiteSweepRunner
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static int running;

        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(120); // one landblock action: a snapshot, or one probe leg
        private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(60);

        // Harness reasons (after the timeouts they quote): rows the sweep could not check, never content failures.
        internal static readonly string LoadTimeoutWhy = $"the throwaway copy did not finish loading within {LoadTimeout.TotalSeconds:0} s";
        internal static readonly string SpawnTimeoutWhy = $"the queued spawn did not run within {SpawnTimeout.TotalSeconds:0} s";
        internal const string NotClearedWhy = "previous row not cleared";

        /// <summary>The lever count the sweep places for a type: the most a run could give this site.</summary>
        public static int SweepN(PuzzleGateType type, int maxN)
        {
            switch (type)
            {
                case PuzzleGateType.Sigil:
                case PuzzleGateType.Odd:
                    return Math.Max(PuzzleGateTunables.MinPickN, Math.Min(PuzzleGateTunables.MaxPickN, maxN));
                default:
                    return ThreadPuzzleSitePicker.LeverCount(type, maxN, 0);
            }
        }

        /// <summary>A stable seed per (dungeon, site, role, type), so a failing row reproduces with /puzzlegate site ... seed=.</summary>
        public static int SweepSeed(string dungeon, string site, string role, PuzzleGateType type, int k = 0)
        {
            unchecked
            {
                var h = 2166136261u;

                foreach (var ch in k == 0 ? $"{dungeon}/{site}/{role}/{type}" : $"{dungeon}/{site}/{role}/{type}/{k}")
                    h = (h ^ ch) * 16777619u;

                return (int)(h & 0x7FFFFFFEu) + 1;
            }
        }

        /// <summary>The (role, type) rows to sweep for a site, in order: the gate role first for a gate site, then the reward host role.</summary>
        public static List<(string Role, PuzzleGateType Type, int N)> Plan(PuzzleSiteDef site, IReadOnlyList<PuzzleGateType> typeFilter)
        {
            var rows = new List<(string, PuzzleGateType, int)>();
            var types = ThreadPuzzleSitePicker.EligibleTypes(site).Where(t => typeFilter == null || typeFilter.Contains(t)).ToList();
            var roles = site.Kind == PuzzleSiteKind.Gate ? new[] { "gate", "reward" } : new[] { "reward" };

            foreach (var role in roles)
                foreach (var t in types)
                    rows.Add((role, t, SweepN(t, site.MaxN)));

            return rows;
        }

        /// <summary>Starts the sweep on a background thread. False with a reason when one is already running.</summary>
        public static bool Start(SweepArgs args, Action<string> output, out string error)
        {
            error = null;

            // The probe and the placements run as landblock actions inside the world tick. Paced one leg per action, but
            // still world-tick time: never on a populated server unless asked.
            var online = PlayerManager.GetOnlineCount();

            if (online > 0 && !args.Force)
            {
                error = $"{online} player(s) online. The sweep spends world-tick time on every row (placements, the walk probe); run it on an empty server, or add --force.";
                return false;
            }

            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                error = "a sweep is already running.";
                return false;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    Run(args, output);
                }
                catch (Exception ex)
                {
                    log.Error("[PUZZLE_SWEEP] sweep threw", ex);
                    output($"puzzlegate-sweep: threw {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref running, 0);
                }
            })
            { IsBackground = true, Name = "puzzlegate-sweep" };

            thread.Start();
            return true;
        }

        private static void Run(SweepArgs args, Action<string> output)
        {
            var store = ThreadDungeonManager.Store;
            var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var dungeons = store.Dungeons.Values
                .Where(d => d.Enabled && store.GetPuzzleSites(d.Id).Count > 0)
                .Where(d => args.DungeonId == null || string.Equals(d.Id, args.DungeonId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();

            if (dungeons.Count == 0)
            {
                output($"puzzlegate-sweep: no enabled dungeon with puzzle sites matches '{args.DungeonId ?? "all"}'.");
                return;
            }

            var outPath = args.OutPath ?? Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                $"puzzlegate-sweep-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tsv");

            var rows = new List<(SweepRecord Record, SweepOutcome Outcome)>();

            using (var writer = new StreamWriter(outPath, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                writer.WriteLine(PuzzleSiteSweep.TsvNote);
                writer.WriteLine(PuzzleSiteSweep.TsvHeader);

                foreach (var dungeon in dungeons)
                {
                    output($"puzzlegate-sweep: {dungeon.Id} (0x{dungeon.Landblock:X4}), {store.GetPuzzleSites(dungeon.Id).Count} site(s)...");

                    foreach (var row in SweepDungeon(dungeon, store.GetPuzzleSites(dungeon.Id), args.Types, args.Seeds))
                    {
                        rows.Add(row);
                        writer.WriteLine(PuzzleSiteSweep.TsvRow(row.Record, row.Outcome));
                    }

                    writer.Flush();

                    foreach (var line in PuzzleSiteSweep.Summary(rows.Where(r => r.Record.Dungeon == dungeon.Id)))
                        output("puzzlegate-sweep: " + line);
                }
            }

            output($"puzzlegate-sweep: wrote {rows.Count} row(s) to {outPath}");

            foreach (var line in PuzzleSiteSweep.Summary(rows))
                if (line.StartsWith("TOTAL", StringComparison.Ordinal))
                    output("puzzlegate-sweep: " + line);

            var decisions = PuzzleSiteSweep.Decide(rows, stamp);
            output($"puzzlegate-sweep: {decisions.Count} site(s) to switch off ({decisions.Count(d => !d.RewardOnly)} disabled, {decisions.Count(d => d.RewardOnly)} reward-disabled).");

            if (!args.WriteDisabled || decisions.Count == 0)
            {
                if (decisions.Count > 0)
                    output("puzzlegate-sweep: not written (run with --write-disabled to write them into puzzle-gates.json).");
                return;
            }

            var json = args.JsonPath;

            if (json == null)
            {
                var folder = ThreadDungeonStore.ResolveDataFolder(out var reason);

                if (folder == null)
                {
                    output($"puzzlegate-sweep: --write-disabled: no Thread data folder ({reason}); nothing written.");
                    return;
                }

                json = Path.Combine(folder, "puzzle-gates.json");
            }

            WriteDisabled(json, decisions, output);
        }

        /// <summary>Applies the decisions to the file's text (PuzzleSiteKillSwitch), keeping its BOM state and every other byte.</summary>
        private static void WriteDisabled(string json, List<PuzzleSiteSweep.DisableDecision> decisions, Action<string> output)
        {
            var bytes = File.ReadAllBytes(json);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var changed = 0;

            foreach (var d in decisions)
            {
                try
                {
                    if (PuzzleSiteKillSwitch.TryApply(text, d.Dungeon, d.SiteId, d.RewardOnly, d.Reason, out var next))
                    {
                        text = next;
                        changed++;
                        output($"puzzlegate-sweep: {(d.RewardOnly ? "reward-disabled" : "disabled")} {d.Dungeon}/{d.SiteId}");
                    }
                }
                catch (FormatException ex)
                {
                    output($"puzzlegate-sweep: {d.Dungeon}/{d.SiteId} not written: {ex.Message}");
                }
            }

            if (changed == 0)
                return;

            // Atomic: a temp file in the same folder, then File.Replace, so a crash never leaves a half-written file.
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(json)) ?? ".", Path.GetFileName(json) + ".sweep-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(bom));
                File.Replace(temp, json, null);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            output($"puzzlegate-sweep: wrote {changed} switch(es) into {json}. Restart the server, or run /dd reload in game as an Admin, to apply them.");
        }

        // ---- one dungeon ------------------------------------------------------------------------------------

        /// <summary>How long the sweep waits for a cleared row's objects to be destroyed and out of their cells.</summary>
        private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(10);

        /// <summary>The prefix of every SkipReason the sweep itself caused: an unchecked row, never a content failure.</summary>
        public const string HarnessPrefix = "harness: ";

        private static IEnumerable<(SweepRecord Record, SweepOutcome Outcome)> SweepDungeon(DungeonEntryDef dungeon, IReadOnlyList<PuzzleSiteDef> sites, IReadOnlyList<PuzzleGateType> typeFilter, int seeds)
        {
            var results = new List<(SweepRecord Record, SweepOutcome Outcome)>();
            var graph = DungeonWalkGraphSource.Get(dungeon.Landblock, out var graphError);

            if (graph == null)
                log.Warn($"[PUZZLE_SWEEP] {dungeon.Id}: no walk graph ({graphError}); check d is unchecked");

            var landblock = RealmManager.GetNewThreadSweepLandblock(new LandblockId(((uint)dungeon.Landblock << 16) | 0xFFFF), dungeon.ExitPortalWcid, out var instance);

            try
            {
                var loaded = WaitFor(() => landblock.CreateWorldObjectsCompleted, LoadTimeout);

                // Objects of the last row whose clear did not finish in time (empty = clean). A non-empty list is
                // re-checked before every row, and every row it still blocks is unchecked, never placed on top of it.
                var leftovers = new List<WorldObject>();

                foreach (var site in sites)
                {
                    foreach (var (role, type, n) in Plan(site, typeFilter))
                    {
                        for (var k = 0; k < seeds; k++)
                        {
                            var seed = SweepSeed(dungeon.Id, site.Id, role, type, k);
                            SweepRecord record;

                            if (!loaded)
                                record = Harness(dungeon, site, role, type, n, seed, LoadTimeoutWhy);
                            else if (leftovers == null)
                            {
                                record = Harness(dungeon, site, role, type, n, seed, NotClearedWhy + " (its objects could not be read)");
                                leftovers = new List<WorldObject>();
                            }
                            else if (leftovers.Count > 0 && !AllCleared(landblock, leftovers))
                                record = Harness(dungeon, site, role, type, n, seed, NotClearedWhy);
                            else
                                record = SweepOne(dungeon, site, role, type, n, seed, landblock, instance, graph, out leftovers);

                            results.Add((record, PuzzleSiteSweep.Evaluate(record, graph)));
                        }
                    }
                }

                return results;
            }
            finally
            {
                // Pace: this copy is gone before the next dungeon's loads.
                landblock.Permaload = false;
                LandblockManager.AddToDestructionQueue(landblock);

                if (!WaitFor(() => LandblockManager.GetEphemeralLandblock(instance) == null, UnloadTimeout))
                    log.Warn($"[PUZZLE_SWEEP] {dungeon.Id}: throwaway copy 0x{instance:X8} still registered after {UnloadTimeout.TotalSeconds:0} s");
            }
        }

        /// <summary>An unchecked row the sweep itself caused (a timeout, an action that never ran): never a content failure.</summary>
        internal static SweepRecord Harness(DungeonEntryDef dungeon, PuzzleSiteDef site, string role, PuzzleGateType type, int n, int seed, string why)
        {
            var r = Base(dungeon, site, role, type, n);

            return new SweepRecord
            {
                Dungeon = r.Dungeon, SiteId = r.SiteId, SiteKind = r.SiteKind, Role = r.Role, Type = r.Type, N = r.N, SiteState = r.SiteState,
                Seed = seed, SkipReason = HarnessPrefix + why,
            };
        }

        private sealed record RowKey(string Dungeon, string SiteId, string SiteKind, string Role, PuzzleGateType Type, int N, string SiteState);

        private static RowKey Base(DungeonEntryDef dungeon, PuzzleSiteDef site, string role, PuzzleGateType type, int n)
            => new RowKey(dungeon.Id, site.Id, (site.KindName ?? site.Kind.ToString()).ToLowerInvariant(), role, type, n,
                site.Disabled ? "disabled" : site.RewardDisabled ? "reward-disabled" : "");

        /// <summary>One row, timed end to end (place, spawn wait, snapshot, probe, clear and its wait).</summary>
        private static SweepRecord SweepOne(DungeonEntryDef dungeon, PuzzleSiteDef site, string role, PuzzleGateType type, int n, int seed, Landblock landblock, uint instance, DungeonWalkGraph graph,
            out List<WorldObject> leftovers)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var record = SweepOneUntimed(dungeon, site, role, type, n, seed, landblock, instance, graph, out leftovers);
            return record with { ElapsedMs = clock.ElapsedMilliseconds, Seed = seed };
        }

        /// <summary>
        /// Places one (site, role, type, seed) through the real PlaceForRun, waits for the spawn, snapshots it on the
        /// landblock thread, walks the probe one leg per landblock action, then clears it and waits until its objects
        /// are destroyed and out of their cells. Every timeout or action that did not run is a HARNESS skip (unchecked);
        /// only a refused placement or a host-reported spawn failure is a SpawnError (check a).
        /// </summary>
        private static SweepRecord SweepOneUntimed(DungeonEntryDef dungeon, PuzzleSiteDef site, string role, PuzzleGateType type, int n, int seed, Landblock landblock, uint instance, DungeonWalkGraph graph,
            out List<WorldObject> leftovers)
        {
            leftovers = new List<WorldObject>();

            var key = Base(dungeon, site, role, type, n);
            var isReward = role == "reward";

            SweepRecord Make(string skip = null, string spawnError = null) => new SweepRecord
            {
                Dungeon = key.Dungeon, SiteId = key.SiteId, SiteKind = key.SiteKind, Role = key.Role, Type = key.Type, N = key.N, SiteState = key.SiteState,
                SkipReason = skip, SpawnError = spawnError,
            };

            SweepRecord HarnessSkip(string why) => Make(skip: HarnessPrefix + why);

            if (site.Anchor == null)
                return Make(skip: "site has no anchor");

            var model = ThreadPuzzlePass.ModelFor(site, isReward);

            if (model == null)
                return Make(skip: "no placeable gate model (a resident-door site)");

            var pick = new ThreadPuzzlePick(site, type, n, seed, isReward);

            if (!ThreadPuzzleSitePicker.TryBuildOptions(pick, out var options, out var optionsError))
                return Make(skip: "options refused: " + optionsError);

            var rotation = PuzzleGateGenerator.YawQuaternion(site.Yaw);
            var anchor = new Position(site.Anchor.Cell, site.Anchor.X, site.Anchor.Y, site.Anchor.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, instance);

            var spots = type == PuzzleGateType.Shuffle
                ? site.ShuffleSpots.Where(s => s != null).Select(s => new Vector3(s.X, s.Y, s.Z)).ToList()
                : new List<Vector3>();

            var host = new SweepHost(instance);
            var placement = PuzzleGateManager.PlaceForRun(landblock, options, seed, anchor, site.Yaw, spots, host, model, site.Layout, out var placeError, site.Id);

            if (placement == null)
                return Make(spawnError: "not placed: " + placeError);

            try
            {
                if (!WaitFor(() => { lock (placement.Sync) return placement.State != PuzzlePlacementState.Spawning; }, SpawnTimeout))
                    return HarnessSkip(SpawnTimeoutWhy);

                PuzzlePlacementState state;

                lock (placement.Sync)
                    state = placement.State;

                if (state != PuzzlePlacementState.Live)
                    return host.LastReport != null ? Make(spawnError: host.LastReport) : HarnessSkip($"placement ended {state} with no report");

                SweepRecord snapshot = null;
                ProbePlan probePlan = null;
                Exception snapshotError = null;

                var ran = OnLandblock(landblock, () =>
                {
                    try
                    {
                        lock (placement.Sync)
                            snapshot = Snapshot(placement, landblock, instance, site, key, graph, out probePlan);
                    }
                    catch (Exception ex)
                    {
                        snapshotError = ex;
                    }
                });

                if (!ran)
                    return HarnessSkip("the landblock did not run the snapshot action");

                if (snapshotError != null)
                {
                    log.Warn($"[PUZZLE_SWEEP] snapshot threw for {key.Dungeon}/{key.SiteId} {key.Role} {key.Type}", snapshotError);
                    return HarnessSkip($"snapshot threw {snapshotError.GetType().Name}: {snapshotError.Message}");
                }

                if (snapshot == null)
                    return HarnessSkip("the placement left Live before the snapshot");

                var run = RunProbe(landblock, instance, snapshot, probePlan, graph);
                return snapshot with { Probe = run.Legs, ProbeNote = run.Note, ProbeMs = run.ProbeMs, ProbeStartOffset = run.StartOffset, ProbeStartNote = run.StartNote, StartMs = run.StartMs, MaxActionMs = run.MaxActionMs };
            }
            finally
            {
                List<WorldObject> owned = null;

                OnLandblock(landblock, () =>
                {
                    lock (placement.Sync)
                        owned = placement.GateParts.Concat(placement.RoundObjects).Where(o => o != null).ToList();
                });

                PuzzleGateManager.ClearRunPlacement(placement, "puzzlegate-sweep");

                // The destroy is queued on the landblock behind the clear. The next row must not stand in this row's
                // objects: wait until each is destroyed and out of its cell. Objects that could not even be read count
                // as not cleared (null), so the next row is unchecked rather than placed blind.
                if (owned == null)
                    leftovers = null;
                else if (!WaitFor(() => AllCleared(landblock, owned), ClearTimeout))
                    leftovers = owned;
            }
        }

        /// <summary>
        /// True when every object is destroyed and has left its cell (read on the landblock thread). False when it is
        /// not, or the read action did not run.
        /// </summary>
        private static bool AllCleared(Landblock landblock, IReadOnlyList<WorldObject> objects)
        {
            var cleared = false;

            if (!OnLandblock(landblock, () => cleared = objects.All(o => o.IsDestroyed && (o.PhysicsObj == null || o.PhysicsObj.CurCell == null))))
                return false;

            return cleared;
        }

        /// <summary>What the probe needs from a snapshot: its targets, and this placement's gate-part guids (always solid).</summary>
        private sealed class ProbePlan
        {
            public IReadOnlyList<ProbeTarget> Targets { get; init; }
            public HashSet<uint> GateGuids { get; init; }

            /// <summary>Horizontal direction a player comes from: away from the gate (opposite the site's forward axis).</summary>
            public Vector3 Approach { get; init; }
        }

        /// <summary>A used object's cylinder and use range, as the server's use check reads them (WorldObject.IsWithinUseRadiusOf).</summary>
        private static UseReach? ReachOf(WorldObject wo, string what)
        {
            if (wo?.PhysicsObj == null)
                return null;

            var useRadius = wo.UseRadius ?? PuzzleSiteSweep.DefaultUseRadius;
            var source = string.Format(CultureInfo.InvariantCulture, "{0} wcid {1}{2}", what, wo.WeenieClassId, wo.UseRadius.HasValue ? "" : " (default)");
            return new UseReach(wo.PhysicsObj.GetRadius(), wo.PhysicsObj.GetHeight(), useRadius, source);
        }

        /// <summary>Reads the placement as it stands and plans the probe. Landblock thread, under p.Sync. Never walks.</summary>
        private static SweepRecord Snapshot(PuzzleGatePlacement p, Landblock landblock, uint instance, PuzzleSiteDef site, RowKey key, DungeonWalkGraph graph, out ProbePlan probePlan)
        {
            probePlan = null;
            var plan = p.Plan;

            if (plan == null || p.State != PuzzlePlacementState.Live)
                return null;

            var objects = new List<SweepObject>();

            // Gate parts: planned from the model's part locals, the same way SpawnGate placed them.
            var parts = p.GateModel.PartLocals(plan.Gate.LocalPosition);

            for (var i = 0; i < p.GateParts.Count; i++)
            {
                var planned = i < parts.Count ? PuzzleGateGenerator.ToWorld(p.Anchor.Pos, p.AnchorYawDeg, parts[i]) : Vector3.Zero;
                PuzzleGateManager.TryResolve(landblock, instance, planned, p.GateModel.RequiresDoor, out var cell, out var resolved);
                objects.Add(Read(p.GateParts[i], PuzzleRole.Gate, i, resolved, cell, instance));
            }

            // Round objects: the same order as plan.Specs (SpawnRound registers each spec in order).
            for (var i = 0; i < p.RoundObjects.Count; i++)
            {
                var spec = i < plan.Specs.Count ? plan.Specs[i] : default;
                var ground = spec.Role == PuzzleRole.Candidate || spec.Role == PuzzleRole.Gate;
                PuzzleGateManager.TryResolve(landblock, instance, spec.Position, ground, out var cell, out var resolved);
                objects.Add(Read(p.RoundObjects[i], spec.Role, spec.Slot, resolved, cell, instance));
            }

            // Shuffle: the spots no lever stands on this round must also be valid lever points.
            var extra = new List<SweepPoint>();
            var leverPoints = plan.Specs.Where(s => s.Role == PuzzleRole.Candidate).Select(s => s.Position).ToList();
            var generatorSpots = p.Generator?.Spots ?? Array.Empty<Vector3>();

            for (var i = 0; i < generatorSpots.Count; i++)
            {
                if (leverPoints.Any(l => Vector3.Distance(l, generatorSpots[i]) < 0.01f))
                    continue;

                PuzzleGateManager.TryResolve(landblock, instance, generatorSpots[i], true, out var cell, out var resolved);
                extra.Add(new SweepPoint(i, resolved, cell));
            }

            var record = new SweepRecord
            {
                Dungeon = key.Dungeon,
                SiteId = key.SiteId,
                SiteKind = key.SiteKind,
                Role = key.Role,
                Type = key.Type,
                N = key.N,
                SiteState = key.SiteState,
                PlannedGateParts = parts.Count,
                PlannedRoundObjects = plan.Specs.Count,
                Objects = objects,
                ExtraLeverPoints = extra,
                BeamTargets = plan.BeamTargets,
                Axis = p.Options.Axis,
                AnchorCell = site.Anchor.Cell,
                Anchor = new Vector3(site.Anchor.X, site.Anchor.Y, site.Anchor.Z),
                GatePoint = ThreadPuzzlePass.GatePoint(site),
            };

            // ---- c: the walk probe's targets (walked later, one leg per landblock action) ----
            // Lever targets are the LIVE levers where they actually stand (a lever slid aside on spawn is walked to where
            // it is, so check b can ask the probe whether the drift matters); a lever that did not enter falls back to
            // its planned point.
            var probeLevers = plan.Specs.Select((s, i) => (Spec: s, Index: i)).Where(x => x.Spec.Role == PuzzleRole.Candidate).Select(x =>
            {
                var live = x.Index < p.RoundObjects.Count ? p.RoundObjects[x.Index] : null;

                if (live != null && !live.IsDestroyed && live.PhysicsObj?.CurCell != null && live.Location != null)
                    return (x.Spec.Slot, live.Location.Pos, live.Location.Cell);

                PuzzleGateManager.TryResolve(landblock, instance, x.Spec.Position, true, out var cell, out var resolved);
                return (x.Spec.Slot, resolved, cell);
            }).ToList();

            // What a player uses at each target: the lever standing there (its cylinder and UseRadius); a further shuffle
            // spot takes a lever's. The focal is not a target: players use levers, not the focal.
            var leverReach = new Dictionary<int, UseReach>();

            for (var i = 0; i < p.RoundObjects.Count && i < plan.Specs.Count; i++)
                if (plan.Specs[i].Role == PuzzleRole.Candidate && ReachOf(p.RoundObjects[i], "lever") is UseReach lr)
                    leverReach[plan.Specs[i].Slot] = lr;

            UseReach? anyLever = leverReach.Count > 0 ? leverReach.OrderBy(kv => kv.Key).First().Value : null;

            var targets = PuzzleSiteSweep.ProbeTargets(probeLevers, extra, null).Select(t =>
            {
                UseReach? reach;

                if (t.Label.StartsWith("lever ", StringComparison.Ordinal) && int.TryParse(t.Label.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && leverReach.TryGetValue(n - 1, out var r))
                    reach = r;
                else
                    reach = anyLever;   // no lever stands at this point: a lever's cylinder and range, sight aimed at the point itself

                return t with { Reach = reach };
            }).ToList();

            var forward = PuzzleGateGenerator.ToWorld(record.Anchor, site.Yaw, new Vector3(0f, 1f, 0f)) - record.Anchor;

            probePlan = new ProbePlan
            {
                Targets = targets,
                GateGuids = new HashSet<uint>(p.GateParts.Where(g => g != null).Select(g => g.Guid.Full)),
                Approach = new Vector3(-forward.X, -forward.Y, 0f),
            };

            return record;
        }

        /// <summary>What one row's probe produced, with its landblock-thread timings.</summary>
        private sealed record ProbeRun(List<ProbeLeg> Legs, string Note, double ProbeMs, float StartOffset, string StartNote = null, double StartMs = 0, double MaxActionMs = 0);

        /// <summary>
        /// Walks the probe body as a player would, ONE LANDBLOCK ACTION PER LEG so world ticks interleave.
        /// The start: the anchor's region cell (its own cell; when it is in no cell, the cell of the nearest approach-side
        /// point, PuzzleWalkProbe.AnchorRegion), then the nearest standing point within PuzzleWalkProbe.StartRadius, in the
        /// region cell first, else in a cell the walk graph reaches from it with a gate role's doorway closed
        /// (PuzzleSiteSweep.StartReach). No standing room = a blocked "anchor start" leg (content); a start outside the
        /// region cell with no graph to prove it reachable = an unchecked leg, never a pass. Then for each target an out
        /// leg that ends as soon as the player could use the object there (PuzzleWalkProbe.UseRange: range less a
        /// margin, and in sight), and a back leg to within StandRadius of that start (ReturnArrival). A gate role routes
        /// with its doorway blocked and fails a leg that enters any cell the walk graph cannot reach from the region
        /// without passing the gate. Never throws: a throw, or an action that did not run, becomes a note or an
        /// unchecked leg. Times every action (Stopwatch ticks): the total, the start search, and the longest action.
        /// </summary>
        private static ProbeRun RunProbe(Landblock landblock, uint instance, SweepRecord record, ProbePlan plan, DungeonWalkGraph graph)
        {
            long probeTicks = 0;
            long maxTicks = 0;
            double Ms() => Interlocked.Read(ref probeTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double ToMs(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            PuzzleWalkProbe probe = null;
            var abandoned = false;

            // Runs one probe action on the landblock, timed. False = the action did not run (the probe is then abandoned).
            bool Timed(Action action, out Exception error)
            {
                Exception caught = null;

                var ran = OnLandblock(landblock, () =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                    }

                    Interlocked.Add(ref probeTicks, clock.ElapsedTicks);

                    if (clock.ElapsedTicks > Interlocked.Read(ref maxTicks))
                        Interlocked.Exchange(ref maxTicks, clock.ElapsedTicks);
                });

                error = caught;

                if (!ran)
                    abandoned = true;

                return ran;
            }

            try
            {
                if (plan == null)
                    return new ProbeRun(null, "no probe plan", 0, 0f);

                // Retail doors are unlocked in a run and players open them: walk through those, never through a gate part.
                if (!OnLandblock(landblock, () => probe = new PuzzleWalkProbe(instance, o => PuzzleWalkProbe.IsPassableDoor(o, plan.GateGuids))))
                {
                    abandoned = true;
                    return new ProbeRun(null, HarnessPrefix + "the probe body action did not run", 0, 0f);
                }

                if (probe == null || !probe.Ready)
                    return new ProbeRun(null, $"the probe body (Setup 0x{PuzzleWalkProbe.BodySetup:X8}) did not load", 0, 0f);

                HashSet<(uint, uint)> blocked = null;

                if (graph != null && record.Role == "gate")
                {
                    var doorway = graph.NearestDoorway(record.GatePoint);

                    if (doorway.HasValue)
                        blocked = new HashSet<(uint, uint)> { doorway.Value };
                }

                // ---- the start: where a player stands near the anchor, in the anchor's region ----
                PuzzleWalkProbe.BodyState? start = null;
                var startOffset = 0f;
                uint region = 0;
                var regionPoint = record.Anchor;
                string regionHow = null;
                Func<uint, bool> reach = null;
                var startTicksBefore = Interlocked.Read(ref probeTicks);

                if (!Timed(() =>
                {
                    start = probe.FindAnchorStart(record.AnchorCell, record.Anchor, plan.Approach, (cell, point) =>
                    {
                        regionPoint = point;
                        return PuzzleSiteSweep.StartReach(graph, cell, point, blocked);
                    }, out region, out reach, out regionHow, out startOffset);
                }, out var startError))
                    return new ProbeRun(null, HarnessPrefix + "the start search action did not run", Ms(), 0f);

                var startMs = ToMs(Interlocked.Read(ref probeTicks) - startTicksBefore);

                if (startError != null)
                {
                    log.Warn($"[PUZZLE_SWEEP] probe start search threw for {record.Dungeon}/{record.SiteId}", startError);
                    return new ProbeRun(null, $"probe start search threw {startError.GetType().Name}: {startError.Message}", Ms(), 0f, null, startMs, ToMs(maxTicks));
                }

                if (region == 0)
                {
                    var noRegion = ProbeLeg.Blocked("anchor", "start", record.Anchor, regionHow);
                    return new ProbeRun(new List<ProbeLeg> { noRegion }, null, Ms(), 0f, null, startMs, ToMs(maxTicks));
                }

                if (start == null)
                {
                    var none = ProbeLeg.Blocked("anchor", "start", record.Anchor, string.Format(CultureInfo.InvariantCulture,
                        "no standing room within {0:0.0} m (floor up to {1:0.00} m above or {2:0.00} m below the anchor's height) in 0x{3:X8}{4}", PuzzleWalkProbe.StartRadius, probe.StepUpHeight, probe.MaxStartRise, region,
                        reach == null ? "" : " or a cell the walk graph reaches from it" + (blocked == null ? "" : " with the gate doorway closed")));
                    return new ProbeRun(new List<ProbeLeg> { none }, null, Ms(), 0f, regionHow, startMs, ToMs(maxTicks));
                }

                if (start.Value.Cell != region && reach == null)
                {
                    var unproven = ProbeLeg.Unchecked("anchor", "start", string.Format(CultureInfo.InvariantCulture,
                        "the start stands in 0x{0:X8}, not the anchor's region 0x{1:X8}, and no walk graph proves it reachable", start.Value.Cell, region));
                    return new ProbeRun(new List<ProbeLeg> { unproven }, null, Ms(), startOffset, regionHow, startMs, ToMs(maxTicks));
                }

                var from = start.Value;

                // A gate row needs its doorway closed, its region proven on the approach side, and a gate field; its legs
                // then stay in that field. Otherwise the row is unchecked. A reward row is unrestricted.
                var guard = PuzzleSiteSweep.GateGuardFor(graph, record.Role == "gate", record.AnchorCell, record.Anchor, region, regionPoint, blocked);

                if (guard.UncheckedReason != null)
                    return new ProbeRun(new List<ProbeLeg> { ProbeLeg.Unchecked("anchor", "start", guard.UncheckedReason) }, null, Ms(), startOffset, regionHow, startMs, ToMs(maxTicks));

                var allowed = guard.Allowed;

                var legs = new List<ProbeLeg>();

                foreach (var t in plan.Targets)
                {
                    if (abandoned)
                    {
                        legs.Add(ProbeLeg.Unchecked(t.Label, "out", HarnessPrefix + "an earlier probe action did not run"));
                        legs.Add(ProbeLeg.Unchecked(t.Label, "back", HarnessPrefix + "an earlier probe action did not run"));
                        continue;
                    }

                    // ---- out: from the start until the server's use check passes ----
                    var outRoute = graph?.Route(from.Cell, from.Pos, t.Cell, t.Point, blocked);
                    ProbeLeg outLeg = null;
                    var outEnd = from;
                    List<PuzzleWalkProbe.BodyState> outTrail = null;

                    var rule = t.Reach.HasValue ? probe.UseRange(t.Cell, t.Point, t.Reach.Value) : null;

                    if (!Timed(() => outLeg = probe.WalkFrom(t.Label, "out", from, t.Point, outRoute, allowed, rule, out outEnd, out outTrail), out var outError))
                    {
                        legs.Add(ProbeLeg.Unchecked(t.Label, "out", HarnessPrefix + "the leg action did not run"));
                        legs.Add(ProbeLeg.Unchecked(t.Label, "back", HarnessPrefix + "the leg action did not run"));
                        continue;
                    }

                    if (outError != null)
                    {
                        log.Warn($"[PUZZLE_SWEEP] probe leg threw for {record.Dungeon}/{record.SiteId} {t.Label} out", outError);
                        legs.Add(ProbeLeg.Unchecked(t.Label, "out", $"probe threw {outError.GetType().Name}: {outError.Message}"));
                        legs.Add(ProbeLeg.Unchecked(t.Label, "back", "the out leg threw"));
                        continue;
                    }

                    legs.Add(outLeg);

                    // ---- back: from where the player used the object (or, after a failed out leg, the nearest standing
                    // point by the target) to within StandRadius of the start ----
                    ProbeLeg backLeg = null;

                    if (!Timed(() =>
                    {
                        PuzzleWalkProbe.BodyState? backFrom = outLeg.Outcome == ProbeOutcome.Pass ? outEnd : probe.FindStart(t.Cell, t.Point, null, out _);

                        if (backFrom == null)
                        {
                            backLeg = ProbeLeg.Blocked(t.Label, "back", t.Point, string.Format(CultureInfo.InvariantCulture, "target not standable within {0:0.0} m", PuzzleWalkProbe.StartRadius));
                            return;
                        }

                        var backRoute = graph?.Route(backFrom.Value.Cell, backFrom.Value.Pos, from.Cell, from.Pos, blocked);
                        backLeg = probe.WalkFrom(t.Label, "back", backFrom.Value, from.Pos, backRoute, allowed, probe.ReturnRule(from), out _);

                        // A player walks back the way they came: retrace the out leg before calling the return blocked.
                        if (backLeg.Outcome == ProbeOutcome.Blocked && outLeg.Outcome == ProbeOutcome.Pass && outTrail != null)
                        {
                            var retraced = probe.Retrace(t.Label, outEnd, outTrail, allowed, probe.ReturnRule(from), out _);
                            backLeg = retraced.Outcome == ProbeOutcome.Pass ? retraced : backLeg with { Reason = $"{backLeg.Reason}; retracing the out leg: {retraced.Reason}" };
                        }
                    }, out var backError))
                    {
                        legs.Add(ProbeLeg.Unchecked(t.Label, "back", HarnessPrefix + "the leg action did not run"));
                        continue;
                    }

                    if (backError != null)
                    {
                        log.Warn($"[PUZZLE_SWEEP] probe leg threw for {record.Dungeon}/{record.SiteId} {t.Label} back", backError);
                        legs.Add(ProbeLeg.Unchecked(t.Label, "back", $"probe threw {backError.GetType().Name}: {backError.Message}"));
                        continue;
                    }

                    legs.Add(backLeg);
                }

                return new ProbeRun(legs, null, Ms(), startOffset, regionHow, startMs, ToMs(maxTicks));
            }
            catch (Exception ex)
            {
                log.Warn($"[PUZZLE_SWEEP] probe threw for {record.Dungeon}/{record.SiteId} {record.Role} {record.Type}", ex);
                return new ProbeRun(null, $"probe threw {ex.GetType().Name}: {ex.Message}", Ms(), 0f, null, 0, ToMs(maxTicks));
            }
            finally
            {
                // A late action may still be using the body after a timeout: only release it when every action ran.
                if (probe != null && !abandoned)
                    OnLandblock(landblock, () => probe.Dispose());
            }
        }

        private static SweepObject Read(WorldObject wo, PuzzleRole role, int slot, Vector3 planned, uint expectedCell, uint instance)
        {
            var entered = wo != null && !wo.IsDestroyed && wo.PhysicsObj?.CurCell != null && wo.Location != null;

            // Overlapping cells at a boundary: the planned point may lie in the lever's physics cell AND in a lower-
            // numbered one, which is what AdjustCell (first match) reports. Ask the physics cell itself, at the floor
            // point and at the lever's body height (physics files an object by its body, above the floor), for the
            // planned point and for where the lever actually stands.
            bool? plannedInCell = null, finalInCell = null;

            if (entered && (wo.Location.Cell & 0xFFFF) >= 0x100 && ACE.Server.Physics.Common.LScape.get_landcell(wo.Location.Cell, instance) is ACE.Server.Physics.Common.EnvCell env)
            {
                var lift = new Vector3(0f, 0f, PuzzleSiteSweep.BodyLift(wo.PhysicsObj.GetHeight()));
                plannedInCell = env.point_in_cell(planned) || env.point_in_cell(planned + lift);
                finalInCell = env.point_in_cell(wo.Location.Pos) || env.point_in_cell(wo.Location.Pos + lift);
            }

            return new SweepObject
            {
                Role = role,
                Slot = slot,
                Planned = planned,
                ExpectedCell = expectedCell,
                Entered = entered,
                Cell = entered ? wo.Location.Cell : 0,
                Final = entered ? wo.Location.Pos : null,
                PlannedInCell = plannedInCell,
                FinalInCell = finalInCell,
                FinalRotation = entered ? wo.Location.Rotation : null,
            };
        }

        // ---- plumbing ------------------------------------------------------------------------------------------

        private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < until)
            {
                if (condition())
                    return true;

                Thread.Sleep(50);
            }

            return condition();
        }

        /// <summary>Runs <paramref name="action"/> on the landblock's queue and waits for it. False on a timeout.</summary>
        private static bool OnLandblock(Landblock landblock, Action action)
        {
            // Not disposed: on a timeout the action may still run later and Set() it.
            var done = new ManualResetEventSlim(false);

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    done.Set();
                }
            }));

            return done.Wait(ActionTimeout);
        }

        /// <summary>
        /// The sweep's placement host: a non-zero RunId (the throwaway instance) so the placement is not admin-managed and
        /// ClearRunPlacement clears it, no policy (PolicyMode None: no fail sink, no IP ledger), no ambush, and every pull
        /// refused (nobody can enter the copy anyway). It records the manager's reports so a failed spawn names its reason.
        /// </summary>
        private sealed class SweepHost : IPuzzleGateHost, IPuzzleSweepHost
        {
            private readonly ConcurrentQueue<string> reports = new ConcurrentQueue<string>();

            public SweepHost(uint instance) => RunId = instance;

            public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.None;

            public uint RunId { get; }

            public bool AllowAmbush => false;

            public string LastReport => reports.LastOrDefault();

            public string CheckActivation(PuzzleGatePlacement placement, Player player) => "This puzzle is a sweep fixture.";

            public void OnSolved(PuzzleGatePlacement placement, Player player) { }

            public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored) { }

            public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) { }

            public void Report(PuzzleGatePlacement placement, string text) => reports.Enqueue(text);
        }
    }
}
