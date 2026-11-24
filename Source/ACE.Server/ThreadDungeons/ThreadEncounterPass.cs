using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;

using log4net;

using ACE.Server.Entity.Actions;
using ACE.Server.ThreadDungeons.Defs;

using Landblock = ACE.Server.Entity.Landblock;
using Position = ACE.Entity.Position;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The run's encounter pass (Docs/Threads/ENCOUNTERS.md). ThreadDungeonManager.OnRunPopulated calls
    /// <see cref="OnRunPopulated"/> on the landblock queue right after the run's one MarkPopulated; it plans the run's
    /// encounters with <see cref="ThreadEncounterPicker"/> and enqueues ONE action per pick onto the action queue of the
    /// landblock that pick's point is on, where the type's Place runs. EndRun calls <see cref="OnRunEnded"/>.
    ///
    /// Fail-open everywhere: no fit, an unloaded landblock, an ended run, an unregistered kind or a throwing type leaves
    /// the run exactly as it was (no instance recorded, no counter touched) and logs at debug; nothing here throws into
    /// the populate chain or an action queue. Reads no PropertyManager key: every setting it uses was stamped by TryStart.
    /// </summary>
    public static class ThreadEncounterPass
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: the loaded encounter store. Production reads the Thread store (re-read by /dd reload).</summary>
        internal static Func<ThreadEncounterStore> StoreSource = () => ThreadDungeonManager.Store?.Encounters ?? ThreadEncounterStore.Empty;

        /// <summary>Test seam: the implementation for a kind. Production is the registry.</summary>
        internal static Func<string, IThreadEncounterType> TypeSource = kind => ThreadEncounterTypes.TryGet(kind, out var impl) ? impl : null;

        /// <summary>Test seam: the dungeon walk graph by landblock (the puzzle pass's source). Null = straight-line distances.</summary>
        internal static Func<ushort, DungeonWalkGraph> WalkGraphSource = landblock => DungeonWalkGraphSource.Get(landblock, out _);

        internal delegate bool GroundProbeFn(ThreadDungeonRun run, ThreadEncounterCandidate candidate, float radius);

        /// <summary>Test seam: the outdoor ground probe (the point plus a ring of 4 at the radius). Production reads terrain.</summary>
        internal static GroundProbeFn GroundProbe = ProductionGroundProbe;

        internal delegate bool EnqueueFn(ThreadDungeonRun run, ushort landblockId, Action<Landblock> action);

        /// <summary>
        /// Test seam: queue an action onto the run copy's landblock <paramref name="landblockId"/>. False when the run holds
        /// no such loaded landblock (the pick is then dropped). Production resolves it from the run's populate context.
        /// </summary>
        internal static EnqueueFn EnqueueOnLandblock = ProductionEnqueue;

        internal delegate bool ResolveFn(ThreadDungeonRun run, ushort landblockId, out Landblock landblock);

        /// <summary>
        /// Test seam inside the production enqueue: landblock id -> the run copy's loaded landblock. False when the copy
        /// holds none. Production reads the run's populate-context map.
        /// </summary>
        internal static ResolveFn ResolveLandblock = ProductionResolve;

        /// <summary>Test seam inside the production enqueue: post an action onto a resolved landblock's queue.</summary>
        internal static Action<ushort, Landblock, Action> PostToLandblock = (id, landblock, action) => landblock.EnqueueAction(new ActionEventDelegate(action));

        // ---- populate ------------------------------------------------------------------------------------------

        /// <summary>
        /// Plans and dispatches the run's encounters, once per run. Landblock thread. Never throws.
        ///
        /// The once-per-run latch is claimed only once the run can actually be planned (a populate context and Active).
        /// OnRunPopulated is also reached by a reward unseal (ThreadDungeonManager.OnRewardUnsealed), which can land while
        /// the run is still Starting; claiming then would cost the run its encounters for good. Such an early call records a
        /// provisional "not yet" plan for /dd encounter list, which the real pass replaces. The plan-threw path (no context
        /// ever) leaves exactly that one provisional line.
        /// </summary>
        public static void OnRunPopulated(ThreadDungeonRun run)
        {
            if (run == null)
                return;

            var notYet = run.EncounterContext == null ? "no populate context"
                : run.State != ThreadDungeonRunState.Active ? $"run is {run.State}"
                : null;

            if (notYet != null)
            {
                run.MarkEncounterPlanProvisional(ThreadEncounterPlan.Nothing(notYet));

                if (log.IsDebugEnabled)
                    log.Debug($"[DYNDUNGEON] {run} encounters: not yet ({notYet})");

                return;
            }

            if (!run.TryClaimEncounterPass())
                return;

            try
            {
                RunPass(run);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} encounter pass threw; the run has no encounters", ex);
            }
        }

        /// <summary>The pass body. Returns how many placement actions were enqueued.</summary>
        internal static int RunPass(ThreadDungeonRun run)
        {
            var skipped = SkipReason(run, out var store);

            if (skipped != null)
            {
                run.MarkEncounterPlan(ThreadEncounterPlan.Nothing(skipped));

                if (log.IsDebugEnabled)
                    log.Debug($"[DYNDUNGEON] {run} encounters: none ({skipped})");

                return 0;
            }

            var plan = Plan(run, store);
            run.MarkEncounterPlan(plan);

            if (log.IsDebugEnabled)
            {
                log.Debug($"[DYNDUNGEON] {run} encounters: {plan.CountSummary(run.EncounterSlotChance)}, {plan.Picks.Count} picked");

                foreach (var drop in plan.Dropped)
                    log.Debug($"[DYNDUNGEON] {run} encounters: {drop}");
            }

            foreach (var drop in plan.Dropped)
                run.RecordEncounterOutcome("dropped " + drop);

            return Dispatch(run, plan.Picks, plan.Fallbacks);
        }

        /// <summary>Why the run gets no encounter roll at all, or null (and the store) when it does.</summary>
        internal static string SkipReason(ThreadDungeonRun run, out ThreadEncounterStore store)
        {
            store = null;

            if (!run.EncountersEnabled)
                return "switched off for this run";

            if (run.State != ThreadDungeonRunState.Active)
                return $"run is {run.State}";

            if (run.EncounterContext == null)
                return "no populate context";

            store = StoreSource() ?? ThreadEncounterStore.Empty;

            if (store.IsDisabledFor(run.Dungeon?.Id, out var reason))
                return "dungeon disabled in encounters.json" + (string.IsNullOrEmpty(reason) ? "" : $" ({reason})");

            if (store.Types.Count == 0)
                return "no encounter types";

            return null;
        }

        /// <summary>The run's plan, decided without placing anything.</summary>
        public static ThreadEncounterPlan Plan(ThreadDungeonRun run, ThreadEncounterStore store)
            => ThreadEncounterPicker.Pick(BuildRequest(run, store, BuildCandidates(run)));

        internal static ThreadEncounterPickRequest BuildRequest(ThreadDungeonRun run, ThreadEncounterStore store, IReadOnlyList<ThreadEncounterCandidate> candidates)
        {
            var isOutdoor = run.Dungeon?.IsOutdoor == true;

            return new ThreadEncounterPickRequest
            {
                Types = store?.Types ?? Array.Empty<ThreadEncounterTypeDef>(),
                KindRegistered = kind => TypeSource(kind) != null,
                TypeFilter = (def, c) => TypeSource(def.Kind)?.RejectPointInRun(def, c, candidates, run),
                RunFilter = def => TypeSource(def.Kind)?.RejectRun(def, run),
                Candidates = candidates,
                IsOutdoor = isOutdoor,
                PuzzleSites = PuzzleSitesOf(run, isOutdoor),
                Enabled = run.EncountersEnabled,
                MaxPerRun = run.EncountersMaxPerRun,
                SlotChance = run.EncounterSlotChance,
                CountWeights = store?.CountWeights,
                RunSeed = run.Spec?.Seed ?? 0,
                GroundProbe = isOutdoor ? (c, r) => GroundProbe(run, c, r) : (Func<ThreadEncounterCandidate, float, bool>)null,
            };
        }

        /// <summary>
        /// Every curated point of the run's dungeon, with its entry and boss distances: walked through the dungeon walk
        /// graph when one exists and knows both cells, otherwise straight-line (always straight-line outdoors, which has
        /// no graph). The picker's requirement filter decides which of them an encounter may use.
        /// </summary>
        public static List<ThreadEncounterCandidate> BuildCandidates(ThreadDungeonRun run)
        {
            var result = new List<ThreadEncounterCandidate>();
            var dungeon = run?.Dungeon;

            if (dungeon?.Points == null)
                return result;

            var isOutdoor = dungeon.IsOutdoor;

            Vector3? entry = null;
            uint entryCell = 0;

            if (ThreadDungeonManager.TryParseEntry(dungeon.Entry, out var entryPos))
            {
                entryCell = entryPos.Cell;
                entry = ThreadEncounterGeometry.Global(entryPos.Cell, entryPos.PositionX, entryPos.PositionY, entryPos.PositionZ, isOutdoor);
            }

            var boss = dungeon.BossAnchor;
            Vector3? bossAt = boss == null ? (Vector3?)null : ThreadEncounterGeometry.Global(boss, isOutdoor);

            DungeonWalkGraph.WalkField entryWalk = null, bossWalk = null;

            // The puzzle pass's guard shape (ThreadPuzzlePass.TryGetWalkGraph): no graph for an outdoor site or an unset landblock.
            var noGraph = dungeon.Landblock == 0 || isOutdoor;

            if (!noGraph)
            {
                DungeonWalkGraph graph = null;

                try
                {
                    graph = WalkGraphSource(dungeon.Landblock);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} encounter walk graph for 0x{dungeon.Landblock:X4} threw; using straight-line distances", ex);
                }

                if (graph != null)
                {
                    if (entry.HasValue && graph.HasCell(entryCell))
                        entryWalk = graph.From(entryCell, entry.Value);

                    if (boss != null && graph.HasCell(boss.Cell))
                        bossWalk = graph.From(boss.Cell, new Vector3(boss.X, boss.Y, boss.Z));
                }
            }

            foreach (var p in dungeon.Points)
            {
                if (p == null || !p.Curated)
                    continue;

                var at = ThreadEncounterGeometry.Global(p, isOutdoor);
                var local = new Vector3(p.X, p.Y, p.Z);

                var toEntry = entryWalk?.DistanceTo(p.Cell, local)
                    ?? (entry.HasValue ? ThreadEncounterGeometry.Distance(entry.Value, at, isOutdoor) : float.MaxValue);

                var toBoss = bossWalk?.DistanceTo(p.Cell, local)
                    ?? (bossAt.HasValue ? ThreadEncounterGeometry.Distance(bossAt.Value, at, isOutdoor) : float.MaxValue);

                result.Add(new ThreadEncounterCandidate(p, at, toEntry, toBoss));
            }

            return result;
        }

        /// <summary>This run's puzzle sites (placed gates plus the curated reward pick) in the picker's frame. Dungeons only.</summary>
        internal static List<Vector3> PuzzleSitesOf(ThreadDungeonRun run, bool isOutdoor)
        {
            var sites = new List<Vector3>();

            foreach (var pick in run.PlacedGatePicks)
            {
                if (pick.Site?.Anchor != null)
                    sites.Add(ThreadEncounterGeometry.Global(pick.Site.Anchor.Cell, pick.Site.Anchor.X, pick.Site.Anchor.Y, pick.Site.Anchor.Z, isOutdoor));
            }

            var reward = run.RewardFallbackPick;

            if (reward?.Site?.Anchor != null)
                sites.Add(ThreadEncounterGeometry.Global(reward.Value.Site.Anchor.Cell, reward.Value.Site.Anchor.X, reward.Value.Site.Anchor.Y, reward.Value.Site.Anchor.Z, isOutdoor));

            return sites;
        }

        // ---- dispatch and placement ------------------------------------------------------------------------------

        /// <summary>
        /// One action per pick, each onto the landblock its point is on (Open World Threads R22: an object is created and
        /// entered on its own point's landblock). Returns how many slots were enqueued.
        ///
        /// Fallback (prod 2026-11-21: 46 picked encounters were refused at Place and their slots lost): a slot whose pick
        /// cannot be queued, or whose Place refuses or throws, moves to the next of its planned alternates
        /// (<see cref="ThreadEncounterPlan.Fallbacks"/>, chosen by the picker from the run seed and already clear of every
        /// other slot's spacing and kind cap), each queued onto its own point's landblock like the pick itself. A slot is
        /// dropped, with the same outcome and debug line as before, only once every alternate has failed too.
        /// </summary>
        internal static int Dispatch(ThreadDungeonRun run, IReadOnlyList<ThreadEncounterPick> picks, IReadOnlyDictionary<int, IReadOnlyList<ThreadEncounterPick>> fallbacks = null)
        {
            var queued = 0;

            foreach (var pick in picks ?? Array.Empty<ThreadEncounterPick>())
            {
                var alternates = fallbacks != null && fallbacks.TryGetValue(pick.Slot, out var list) ? list : null;

                if (TryDispatch(run, pick, alternates, 0, out var why) || QueueAlternate(run, pick, why, alternates, 0))
                {
                    queued++;
                    continue;
                }

                run.RecordEncounterOutcome($"slot {pick.Slot} {pick.Def?.Id}: {why}");

                if (log.IsDebugEnabled)
                    log.Debug($"[DYNDUNGEON] {run} encounter slot {pick.Slot} {pick.Def?.Id} at {pick.Point}: {why}; dropped");
            }

            return queued;
        }

        private static bool TryDispatch(ThreadDungeonRun run, ThreadEncounterPick pick, out string why)
            => TryDispatch(run, pick, null, 0, out why);

        /// <summary>
        /// Queues one attempt. <paramref name="alternates"/> and <paramref name="next"/> (the index of the alternate to try
        /// if this attempt's Place fails) travel with it to <see cref="PlaceOne(ThreadDungeonRun, Landblock, ThreadEncounterPick, IThreadEncounterType, IReadOnlyList{ThreadEncounterPick}, int)"/>.
        /// </summary>
        private static bool TryDispatch(ThreadDungeonRun run, ThreadEncounterPick pick, IReadOnlyList<ThreadEncounterPick> alternates, int next, out string why)
        {
            why = null;

            var impl = TypeSource(pick.Def?.Kind);

            if (impl == null)
            {
                why = $"kind '{pick.Def?.Kind}' is not registered";
                return false;
            }

            var landblockId = pick.Point.Landblock;

            try
            {
                if (EnqueueOnLandblock(run, landblockId, landblock => PlaceOne(run, landblock, pick, impl, alternates, next)))
                    return true;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} could not queue encounter slot {pick.Slot} onto 0x{landblockId:X4}", ex);
            }

            why = $"landblock 0x{landblockId:X4} is not loaded in the run copy";
            return false;
        }

        /// <summary>
        /// The fallback step: queues the first of <paramref name="alternates"/> from index <paramref name="from"/> on that
        /// can be queued, recording <paramref name="failedWhy"/> against <paramref name="failed"/> with the alternate it
        /// moved to. An alternate that cannot be queued is recorded and skipped. False (nothing recorded for
        /// <paramref name="failed"/>) when none is left; the caller then records the drop.
        /// </summary>
        private static bool QueueAlternate(ThreadDungeonRun run, ThreadEncounterPick failed, string failedWhy, IReadOnlyList<ThreadEncounterPick> alternates, int from)
        {
            if (alternates == null)
                return false;

            for (var i = Math.Max(0, from); i < alternates.Count; i++)
            {
                var alt = alternates[i];

                if (TryDispatch(run, alt, alternates, i + 1, out var why))
                {
                    Outcome(run, failed, $"{failedWhy}; trying alternate {i + 1} of {alternates.Count}: {alt.Def?.Id} at {alt.Point}");
                    return true;
                }

                Outcome(run, alt, $"alternate {i + 1} of {alternates.Count}: {why}; skipped");
            }

            return false;
        }

        /// <summary>
        /// Places one pick. Runs on the action queue of <paramref name="landblock"/> (the pick's point's landblock). Returns
        /// the recorded instance, or null when nothing was recorded. Never throws.
        /// </summary>
        internal static ThreadEncounterInstance PlaceOne(ThreadDungeonRun run, Landblock landblock, ThreadEncounterPick pick, IThreadEncounterType impl)
            => PlaceOne(run, landblock, pick, impl, null, 0);

        /// <summary>
        /// <see cref="PlaceOne(ThreadDungeonRun, Landblock, ThreadEncounterPick, IThreadEncounterType)"/> for one attempt of a
        /// slot: a refused or throwing Place moves the slot to <paramref name="alternates"/>[<paramref name="next"/>] and on
        /// (<see cref="QueueAlternate"/>); with none left the refusal is recorded as the slot's drop.
        /// </summary>
        internal static ThreadEncounterInstance PlaceOne(ThreadDungeonRun run, Landblock landblock, ThreadEncounterPick pick, IThreadEncounterType impl,
            IReadOnlyList<ThreadEncounterPick> alternates, int next)
        {
            try
            {
                if (run.State == ThreadDungeonRunState.Ended || run.EncountersEnded)
                {
                    Outcome(run, pick, "run ended before placement");
                    return null;
                }

                var context = run.EncounterContext;

                if (context == null)
                {
                    Outcome(run, pick, "no populate context");
                    return null;
                }

                var instance = run.NewEncounterInstance(pick, impl);
                var request = new ThreadEncounterPlaceRequest(run, landblock, instance, context);

                bool placed;
                string failure;

                try
                {
                    placed = impl.Place(request, out failure);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} {instance} Place threw", ex);
                    placed = false;
                    failure = "threw " + ex.GetType().Name;
                }

                if (!placed)
                {
                    run.MarkEncounterInstanceEnded(instance);
                    run.ReleaseEncounterObjects(instance);

                    var why = "not placed: " + (failure ?? "refused");

                    if (QueueAlternate(run, pick, why, alternates, next))
                        return null;

                    Outcome(run, pick, alternates != null && alternates.Count > 0 ? $"{why}; no alternate left ({alternates.Count} planned)" : why);
                    return null;
                }

                if (!run.TryRecordEncounterPlaced(instance))
                {
                    // EndRun's sweep ran while Place was running: it never saw this instance, so it is ended here.
                    EndInstance(instance, "run ended during placement");
                    Outcome(run, pick, "run ended during placement; released");
                    return null;
                }

                run.RecordEncounterOutcome($"placed {instance}");
                log.Info($"[DYNDUNGEON] {run} placed {instance} seed={pick.Seed.ToString(CultureInfo.InvariantCulture)}");
                return instance;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} encounter slot {pick.Slot} placement threw; the run is unchanged", ex);
                return null;
            }
        }

        private static void Outcome(ThreadDungeonRun run, ThreadEncounterPick pick, string why)
        {
            run.RecordEncounterOutcome($"slot {pick.Slot} {pick.Def?.Id}: {why}");

            if (log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] {run} encounter slot {pick.Slot} {pick.Def?.Id} at {pick.Point}: {why}");
        }

        // ---- run end -------------------------------------------------------------------------------------------------

        /// <summary>Calls OnRunEnded once for every placed encounter of the run and releases their back-references. Any thread. Returns the count.</summary>
        public static int OnRunEnded(ThreadDungeonRun run, string why)
        {
            if (run == null)
                return 0;

            var taken = run.TakeEncountersForEnd();

            foreach (var instance in taken)
                EndInstance(instance, why);

            return taken.Count;
        }

        private static void EndInstance(ThreadEncounterInstance instance, string why)
        {
            try
            {
                instance.Type?.OnRunEnded(instance, why);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} OnRunEnded threw", ex);
            }
            finally
            {
                instance.Run.MarkEncounterInstanceEnded(instance);
                instance.Run.ReleaseEncounterObjects(instance);
            }
        }

        // ---- use ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldObject.OnActivate, for an object carrying a P_ThreadEncounter back-reference: hands the use to the
        /// instance's type when it is <see cref="IThreadEncounterUsable"/>. An ended instance, or a type that is not
        /// usable, ignores the use. Never throws into OnActivate.
        /// </summary>
        public static void OnActivated(ACE.Server.WorldObjects.WorldObject used, ACE.Server.WorldObjects.Player player)
        {
            var instance = used?.P_ThreadEncounter;

            if (instance == null || player == null || !(instance.Type is IThreadEncounterUsable usable))
                return;

            try
            {
                usable.OnUse(instance, used, player);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} use by {player.Name} threw", ex);
            }
        }

        /// <summary>
        /// ThreadDungeonManager.OnRunCreatureDied, for every run creature's death: hands a death of a creature carrying a
        /// P_ThreadEncounter back-reference to its type when it is <see cref="IThreadEncounterDeathAware"/>. An ended
        /// instance (its back-references are cleared) or any other type ignores it. Never throws into the kill hook.
        /// </summary>
        public static void OnTrackedCreatureDied(ACE.Server.WorldObjects.Creature creature)
        {
            var instance = creature?.P_ThreadEncounter;

            if (instance == null || !(instance.Type is IThreadEncounterDeathAware aware))
                return;

            try
            {
                aware.OnTrackedCreatureDied(instance, creature);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} death of {creature.Name} threw", ex);
            }
        }

        // ---- helpers the types build on --------------------------------------------------------------------------

        /// <summary>
        /// The run's curated points on the same landblock as <paramref name="around"/>, within <paramref name="radius"/>,
        /// nearest first, the point itself excluded. Outdoor runs offer open-ground points only.
        /// </summary>
        public static IReadOnlyList<DungeonSpawnPointDef> NearbyPoints(ThreadDungeonRun run, ThreadEncounterCandidate around, float radius)
        {
            var dungeon = run?.Dungeon;

            if (dungeon?.Points == null || around == null)
                return Array.Empty<DungeonSpawnPointDef>();

            var isOutdoor = dungeon.IsOutdoor;
            var landblock = around.Landblock;

            return dungeon.Points
                .Where(p => p != null && p.Curated && !ReferenceEquals(p, around.Point) && (ushort)(p.Cell >> 16) == landblock
                            && (!isOutdoor || string.Equals(p.Kind, "outdoor", StringComparison.Ordinal)))
                .Select(p => (Point: p, Distance: ThreadEncounterGeometry.Distance(ThreadEncounterGeometry.Global(p, isOutdoor), around.Global, isOutdoor)))
                .Where(x => x.Distance <= radius)
                .OrderBy(x => x.Distance)
                .Select(x => x.Point)
                .ToList();
        }

        // ---- admin ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// /dd encounter place: force one encounter of <paramref name="kind"/> at the fitting point nearest the admin.
        /// Ignores the run's switch, the slot roll, the weight and spacing; keeps the type's point requirements. Uses the
        /// first encounters.json row of that kind (enabled first), or a default row when the file has none. The placement
        /// itself goes through the same queue and Place path as a rolled pick.
        /// </summary>
        public static bool TryForcePlace(ThreadDungeonRun run, string kind, uint adminCell, Vector3 adminLocal, out string message)
        {
            message = null;

            if (run == null || run.State == ThreadDungeonRunState.Ended)
            {
                message = "No live run here.";
                return false;
            }

            if (run.EncounterContext == null)
            {
                message = "The run has not finished populating (no encounter context yet).";
                return false;
            }

            // Owner rule: one Wandering Peddler per run. Refused here, before dispatch, so the admin gets a clear message.
            if (string.Equals(kind, PeddlerEncounterType.KindName, StringComparison.Ordinal) && run.PeddlerClaimed)
            {
                message = "This run already had its peddler.";
                return false;
            }

            var impl = TypeSource(kind);

            if (impl == null)
            {
                var kinds = ThreadEncounterTypes.Kinds;
                message = $"No encounter type of kind '{kind}' is registered (registered: {(kinds.Count == 0 ? "none" : string.Join(", ", kinds))}).";
                return false;
            }

            var store = StoreSource() ?? ThreadEncounterStore.Empty;
            var rows = store.Types.Where(t => string.Equals(t.Kind, kind, StringComparison.Ordinal)).ToList();
            var def = rows.FirstOrDefault(t => t.Enabled) ?? rows.FirstOrDefault();
            var usedDefault = def == null;

            // No row of this kind: a default row with an EMPTY config object (never Undefined, so a type reading its
            // config sees {} rather than a JsonElement that throws on access).
            def ??= new ThreadEncounterTypeDef { Id = "admin_" + kind, Kind = kind, Config = DefaultConfig() };

            var isOutdoor = run.Dungeon?.IsOutdoor == true;

            if (!(def.Placement ?? new ThreadEncounterPlacementDef()).AllowsRunKind(isOutdoor))
            {
                message = $"{def.Id} is not allowed in {(isOutdoor ? "outdoor" : "dungeon")} runs (placement.runKinds).";
                return false;
            }
            var admin = ThreadEncounterGeometry.Global(adminCell, adminLocal.X, adminLocal.Y, adminLocal.Z, isOutdoor);

            var candidates = BuildCandidates(run);
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            ThreadEncounterCandidate best = null;
            var bestDistance = float.MaxValue;

            foreach (var c in candidates)
            {
                var why = ThreadEncounterPicker.RejectReason(def, c, isOutdoor, isOutdoor ? (cand, r) => GroundProbe(run, cand, r) : (Func<ThreadEncounterCandidate, float, bool>)null,
                    (d, cand) => impl.RejectPointInRun(d, cand, candidates, run));

                if (why != null)
                {
                    reasons[why] = reasons.TryGetValue(why, out var n) ? n + 1 : 1;
                    continue;
                }

                var distance = ThreadEncounterGeometry.Distance(admin, c.Global, isOutdoor);

                if (distance < bestDistance)
                {
                    best = c;
                    bestDistance = distance;
                }
            }

            if (best == null)
            {
                message = $"No point fits {def.Id} ({candidates.Count} candidate(s): " +
                          (reasons.Count == 0 ? "none" : string.Join(", ", reasons.Select(kv => $"{kv.Key} x{kv.Value}"))) + ").";
                return false;
            }

            var seed = Environment.TickCount & 0x7FFFFFFF;
            var pick = new ThreadEncounterPick(-1, def, best, seed == 0 ? 1 : seed);

            if (!TryDispatch(run, pick, out var failure))
            {
                message = $"Could not queue {def.Id}: {failure}.";
                return false;
            }

            run.RecordEncounterOutcome($"admin queued {def.Id} at {best}");
            message = string.Format(CultureInfo.InvariantCulture, "Queued {0} ({1}) at {2}, {3:0.#} m from you.", def.Id, kind, best, bestDistance)
                      + (usedDefault ? " encounters.json has no row of this kind; used a default row (no placement rules, empty config)." : "");
            return true;
        }

        /// <summary>An empty, detached config object for the admin default row.</summary>
        internal static System.Text.Json.JsonElement DefaultConfig()
        {
            using var doc = System.Text.Json.JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// The outdoor probe's height tolerance for a ring of <paramref name="radius"/>: half the radius, at least 1.5 m, and
        /// never looser than the overflow scatter's ThreadDungeonSpawner.ScatterMaxDropZ. Tighter than the scatter on purpose:
        /// an encounter footprint wants flat-ish ground, not merely ground a single creature can stand on. Pure.
        /// </summary>
        internal static float ProbeMaxDropZ(float radius)
            => Math.Min(ThreadDungeonSpawner.ScatterMaxDropZ, Math.Max(1.5f, (float.IsNaN(radius) ? 0f : radius) * 0.5f));

        // ---- production seams ----------------------------------------------------------------------------------------

        private static bool ProductionEnqueue(ThreadDungeonRun run, ushort landblockId, Action<Landblock> action)
        {
            if (!ResolveLandblock(run, landblockId, out var landblock))
                return false;

            PostToLandblock(landblockId, landblock, () => action(landblock));
            return true;
        }

        private static bool ProductionResolve(ThreadDungeonRun run, ushort landblockId, out Landblock landblock)
        {
            landblock = null;
            var context = run?.EncounterContext;

            return context != null && context.Landblocks.TryGetValue(landblockId, out landblock) && landblock != null;
        }

        /// <summary>
        /// The overflow scatter's ground checks (ThreadDungeonSpawner.ProbeOutdoorGround: no building in the cell, walkable)
        /// plus a height check against the curated point at <see cref="ProbeMaxDropZ"/>, at the point and at 4 points on a ring of
        /// <paramref name="radius"/>. A probe point on a landblock the run copy has not loaded is a rejection: the probe
        /// must never cause a landblock load (LScape.get_landblock loads on a miss).
        /// </summary>
        private static bool ProductionGroundProbe(ThreadDungeonRun run, ThreadEncounterCandidate candidate, float radius)
        {
            var context = run?.EncounterContext;

            if (context == null || candidate == null)
                return false;

            var p = candidate.Point;
            var maxDropZ = ProbeMaxDropZ(radius);

            foreach (var (dx, dy) in RingOffsets(radius))
            {
                var position = new Position(p.Cell, p.X + dx, p.Y + dy, p.Z, 0f, 0f, 0f, 1f, run.Instance);

                if (position.Indoors)
                    return false;

                position.SetLandblock();
                position.SetLandCell();

                if (!context.Landblocks.ContainsKey(position.LandblockId.Landblock))
                    return false;

                if (!ThreadDungeonSpawner.ProbeOutdoorGround(position, p.Z, maxDropZ, out _))
                    return false;
            }

            return true;
        }

        /// <summary>The probe offsets: the point itself, then (when the radius is positive) east, west, north and south of it. Pure.</summary>
        internal static IEnumerable<(float Dx, float Dy)> RingOffsets(float radius)
        {
            yield return (0f, 0f);

            if (!(radius > 0))
                yield break;

            yield return (radius, 0f);
            yield return (-radius, 0f);
            yield return (0f, radius);
            yield return (0f, -radius);
        }
    }
}
