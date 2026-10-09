using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using log4net;

using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The policy seam for scored wrong answers in a run (architect R4). The run host calls it for every SCORED
    /// wrong pull - never a refused, stale, solved or unarmed one. Production installs the IP-wide fail policy
    /// (ThreadPuzzleFailPolicySink); <see cref="NoopThreadPuzzleFailSink"/> remains for tests.
    /// </summary>
    public interface IThreadPuzzleFailSink
    {
        void OnScoredWrong(ThreadDungeonRun run, Player player, PuzzleGatePlacement placement);
    }

    /// <summary>A sink with no policy. Tests swap it in; production does not use it.</summary>
    public sealed class NoopThreadPuzzleFailSink : IThreadPuzzleFailSink
    {
        public static readonly NoopThreadPuzzleFailSink Instance = new NoopThreadPuzzleFailSink();

        private NoopThreadPuzzleFailSink() { }

        public void OnScoredWrong(ThreadDungeonRun run, Player player, PuzzleGatePlacement placement) { }
    }

    /// <summary>
    /// The host of a run's puzzle placement. A GATE placement only reports scored wrongs to the fail sink; the
    /// REWARD placement additionally owns the run's reward seal: it refuses pulls (no penalty) until the run's
    /// kills are done, lifts the seal on a solve, and lifts it on ANY removal without a solve (fail-open).
    /// Ambush is always off (user ruling: no ambush creatures in runs). No admin special-casing anywhere: an
    /// admin inside a run is an ordinary player to this host.
    /// </summary>
    public sealed class ThreadPuzzleRunHost : IPuzzleGateHost
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The injectable fail sink, read at call time. Production: the IP-wide fail policy.</summary>
        public static IThreadPuzzleFailSink FailSink = ThreadPuzzleFailPolicySink.Instance;

        /// <summary>
        /// Test seam for what an unseal triggers; production is ThreadDungeonManager.OnRewardUnsealed. A swappable
        /// delegate on the same pattern as ThreadDungeonManager.GemDestroyer.
        /// </summary>
        internal static Action<ThreadDungeonRun> RewardUnsealed = ThreadDungeonManager.OnRewardUnsealed;

        public ThreadDungeonRun Run { get; }

        public bool IsReward { get; }

        public ThreadPuzzleRunHost(ThreadDungeonRun run, bool isReward)
        {
            Run = run ?? throw new ArgumentNullException(nameof(run));
            IsReward = isReward;
        }

        public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.Run;

        public uint RunId => Run.RunId;

        public bool AllowAmbush => false;

        public string CheckActivation(PuzzleGatePlacement placement, Player player)
        {
            if (IsReward && !Run.IsRewardArmed)
                return PuzzleGateText.RewardNotArmed;

            return null;
        }

        public void OnSolved(PuzzleGatePlacement placement, Player player)
        {
            if (!IsReward)
                return;

            // OnSolved runs on the run copy's landblock thread (HandleActivation), so the follow-up runs inline.
            if (UnsealRun(Run, onRunLandblockThread: true))
                log.Info($"[DYNDUNGEON] {Run} reward scene solved by {player?.Name ?? "-"}; reward unsealed");
        }

        public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored)
        {
            if (!scored)
                return;

            try
            {
                (FailSink ?? NoopThreadPuzzleFailSink.Instance).OnScoredWrong(Run, player, placement);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {Run} puzzle fail sink threw", ex);
            }
        }

        public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved)
        {
            if (!IsReward || solved)
                return;

            // Fail-open: the reward scene is gone without a solve, so nothing could ever lift the seal. OnRemoved can
            // arrive on a command thread or the world thread (a clear, the watchdog's reap, EndRun), so the
            // follow-up is marshalled onto the run copy's landblock.
            if (UnsealRun(Run, onRunLandblockThread: false))
                log.Warn($"[DYNDUNGEON] {Run} reward scene removed without a solve ({reason}); reward unsealed");
        }

        public void Report(PuzzleGatePlacement placement, string text) => log.Info($"[DYNDUNGEON] {Run} {text}");

        /// <summary>
        /// Test seam: queue an action onto the run copy's landblock. Production resolves the copy and enqueues; a
        /// copy that is already gone means the run is ending, and the follow-up is dropped (EndRun owns the rest).
        /// </summary>
        internal static Action<ThreadDungeonRun, Action> PostToRunLandblock = (run, action) =>
        {
            var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);

            if (landblock == null)
            {
                log.Info($"[DYNDUNGEON] {run} reward unsealed with its copy gone; no clear follow-up (the run is ending)");
                return;
            }

            landblock.EnqueueAction(new ActionEventDelegate(action));
        };

        /// <summary>
        /// Shared with the watchdog: lift the seal INLINE (so the run's state is decided now, under its own lock),
        /// and on the one winning call run the follow-up - AnnounceCleared and the pooled-loot request, which spawn
        /// objects and read tunables and so belong on the run copy's LANDBLOCK thread (AnnounceCleared's contract).
        /// A caller already on that thread runs it inline; any other caller (the world-thread watchdog, a
        /// command-thread clear, EndRun) has it queued there.
        /// </summary>
        internal static bool UnsealRun(ThreadDungeonRun run, bool onRunLandblockThread)
        {
            if (run == null || !run.UnsealReward())
                return false;

            if (onRunLandblockThread)
            {
                RunFollowUp(run);
                return true;
            }

            try
            {
                PostToRunLandblock(run, () => RunFollowUp(run));
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} could not queue the reward unseal follow-up", ex);
            }

            return true;
        }

        private static void RunFollowUp(ThreadDungeonRun run)
        {
            try
            {
                RewardUnsealed(run);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} reward unseal follow-up threw", ex);
            }
        }
    }

    /// <summary>
    /// The run's puzzle pass (architect plan step 7): runs on the run copy's landblock thread as part of the door
    /// step, AFTER UnlockDoors and before the first creature batch is enqueued. Picks sites (ThreadPuzzleSitePicker)
    /// and places them through PuzzleGateManager with a run host. Every failure leaves the run open and logs.
    /// Reads no PropertyManager key: every setting it uses was stamped on the run by TryStart.
    /// </summary>
    public static class ThreadPuzzlePass
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: the dungeon's puzzle sites. Production reads the loaded store.</summary>
        internal static Func<string, IReadOnlyList<PuzzleSiteDef>> SiteSource = id => ThreadDungeonManager.Store?.GetPuzzleSites(id) ?? Array.Empty<PuzzleSiteDef>();

        /// <summary>
        /// What the pass will try to place for this run, decided without a world: nothing unless the run was
        /// stamped with puzzle gates enabled and its dungeon has sites; the reward scene only with the reward
        /// switch on AND pooled loot (user ruling: the reward scene exists only when pooled loot is on).
        /// </summary>
        public static List<ThreadPuzzlePick> Plan(ThreadDungeonRun run, IReadOnlyList<PuzzleSiteDef> sites)
        {
            if (run == null || !run.PuzzleGatesEnabled || sites == null || sites.Count == 0)
                return new List<ThreadPuzzlePick>();

            var wantReward = run.PuzzleRewardSceneEnabled && run.PooledLoot;
            return ThreadPuzzleSitePicker.Pick(sites, run.PuzzleGatesPerRun, wantReward, run.Spec?.Seed ?? 0);
        }

        /// <summary>Places the run's puzzles. Landblock thread. Returns how many placements were registered.</summary>
        public static int Run(ThreadDungeonRun run, Landblock landblock)
        {
            if (run == null || landblock == null || !run.PuzzleGatesEnabled)
                return 0;

            var sites = SiteSource(run.Dungeon?.Id);

            // A resident site is never placed (ThreadPuzzleSitePicker.IsPlaceable); say so once per run so a curated
            // file full of them is not silently inert.
            foreach (var resident in sites.Where(s => s?.GateModel != null && s.Kind == PuzzleSiteKind.Gate && s.GateModel.Kind == PuzzleGateModelKind.Resident))
                log.Info($"[DYNDUNGEON] {run} puzzle site {resident.Id} is a resident-door site; skipped (not supported yet)");

            var picks = Plan(run, sites);
            var placed = 0;

            foreach (var pick in picks)
            {
                try
                {
                    // The reward scene is NOT placed at populate: the run is sealed pending, and the kill that arms it
                    // places the scene near the clearing player (PlaceRewardAtArming). The curated pick is kept as the
                    // arming placer's last resort.
                    if (pick.IsReward)
                    {
                        DeferReward(run, pick);
                        continue;
                    }

                    if (TryPlace(run, landblock, pick))
                        placed++;
                }
                catch (Exception ex)
                {
                    // A reward placement that threw before SealReward never sealed, so the run stays clearable.
                    log.Error($"[DYNDUNGEON] {run} puzzle site {pick.Site?.Id} threw while placing; the run stays open", ex);
                }
            }

            // EndRun can land on the world thread while this pass runs; its ClearForRun may then have swept before
            // these registrations existed. Sweep again so nothing outlives an ended run.
            if (placed > 0 && run.State == ThreadDungeonRunState.Ended)
                PuzzleGateManager.ClearForRun(run.RunId);

            return placed;
        }

        /// <summary>
        /// The reward pick at populate: seal the run PENDING (the scene is placed at arming) and, on success, build the
        /// dungeon's walk graph now so the arming kill only reads the cache (a cold dat build costs tens of
        /// milliseconds, too much for that kill's landblock tick). Returns whether the run was sealed.
        /// </summary>
        internal static bool DeferReward(ThreadDungeonRun run, ThreadPuzzlePick pick)
        {
            if (!run.SealRewardPending(pick))
            {
                log.Warn($"[DYNDUNGEON] {run} reward scene could not be sealed pending (state {run.State}); the run clears without one");
                return false;
            }

            run.MarkHasPuzzles(); // the watchdog must see a pending seal even in a run with no gate puzzle
            log.Info($"[DYNDUNGEON] {run} reward scene deferred to arming (curated site={pick.Site?.Id})");

            if (!TryGetWalkGraph(run, out _, out var why))
                log.Info($"[DYNDUNGEON] {run} walk graph unavailable at populate ({why}); the arming placement will use straight-line distance");

            return true;
        }

        private static bool TryPlace(ThreadDungeonRun run, Landblock landblock, ThreadPuzzlePick pick)
        {
            var placement = PlacePick(run, landblock, pick, out var model);

            if (placement == null)
                return false;

            run.MarkHasPuzzles();

            // The seal goes on in this same landblock action, before the queued spawn can run, so a spawn failure's
            // OnRemoved always finds a seal to lift.
            if (pick.IsReward && !run.SealReward(placement))
                log.Warn($"[DYNDUNGEON] {run} reward scene #{placement.Id} placed but the run could not be sealed (state {run.State}); it is cosmetic");

            if (!pick.IsReward)
                run.RecordPlacedGatePick(pick);

            log.Info($"[DYNDUNGEON] {run} puzzle #{placement.Id} {(pick.IsReward ? "reward" : "gate")} site={pick.Site.Id} type={pick.Type.ToString().ToLowerInvariant()} n={pick.N} seed={pick.Seed} model={model}");
            return true;
        }

        /// <summary>
        /// Test seam: places one pick and returns its placement, or null (the reason already logged). Production is
        /// <see cref="PlacePick"/>; a test swaps it to drive the arming state machine without a world.
        /// </summary>
        internal static ArmingPlaceFn ArmingPlacer = PlaceOnRunLandblock;

        internal delegate PuzzleGatePlacement ArmingPlaceFn(ThreadDungeonRun run, ThreadPuzzlePick pick, out string error);

        /// <summary>Production arming placement: resolve the run copy's landblock, then place the pick there.</summary>
        private static PuzzleGatePlacement PlaceOnRunLandblock(ThreadDungeonRun run, ThreadPuzzlePick pick, out string error)
        {
            var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);

            if (landblock == null)
            {
                error = "run landblock is gone";
                return null;
            }

            var placement = PlacePick(run, landblock, pick, out _);
            error = placement == null ? $"site {pick.Site?.Id} refused (see the line above)" : null;
            return placement;
        }

        /// <summary>
        /// The reward scene's ARMING placement: the kill that brought a pending-sealed run to its clear fraction
        /// calls this (ThreadDungeonManager.AnnounceRewardArmed). Chooses the site near <paramref name="player"/>
        /// (ThreadPuzzleRewardSelector), places it and attaches it to the seal. FAIL-OPEN: any failure - no site, a
        /// missing landblock, a refused placement, a throw - unseals at once and the run clears normally. Called on
        /// the thread that recorded the kill, which is where AnnounceCleared already runs; the unseal follow-up runs
        /// inline there for the same reason. Returns the attached placement, or null when the run was unsealed (or
        /// was not pending).
        /// </summary>
        public static PuzzleGatePlacement PlaceRewardAtArming(ThreadDungeonRun run, System.Numerics.Vector3? player, string playerName, uint playerCell = 0)
        {
            if (run == null || !run.IsRewardPending)
                return null;

            string failure;

            // Set the moment a placement is registered, through a ref so it survives a throw after registration: a
            // registered placement the run did not take must be cleared, never left in the copy as an orphan.
            PuzzleGatePlacement registered = null;

            try
            {
                failure = TryPlaceRewardAtArming(run, player, playerName, playerCell, ref registered);

                if (failure == null)
                    return registered;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} reward scene placement at arming threw", ex);
                failure = "threw: " + ex.GetType().Name;
            }

            // Unseal FIRST, so this call wins the seal and logs the arming WARN; the orphan's OnRemoved then finds
            // nothing left to lift.
            if (ThreadPuzzleRunHost.UnsealRun(run, onRunLandblockThread: true))
                log.Warn($"[DYNDUNGEON] {run} reward scene could not be placed at arming ({failure}); reward unsealed");

            if (registered != null)
            {
                try
                {
                    PuzzleGateManager.ClearRunPlacement(registered, "reward scene refused at arming");
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} could not clear the refused arming placement #{registered.Id}", ex);
                }
            }

            return null;
        }

        /// <summary>
        /// EndRun's half of the fail-open rule for a PENDING seal: there is no placement whose removal would lift it,
        /// so the ending run lifts it directly. True when this call lifted it. The caller has already marked the run
        /// Ended, so the clear check UnsealReward re-runs is a no-op and no clear follow-up is owed.
        /// </summary>
        internal static bool ReleasePendingSealOnEnd(ThreadDungeonRun run) => run != null && run.IsRewardPending && run.UnsealReward();

        /// <summary>
        /// Returns null on success (placement attached, and in <paramref name="registered"/>) or the failure reason. On a
        /// failure <paramref name="registered"/> holds any placement that was registered but not attached; the caller
        /// clears it.
        /// </summary>
        private static string TryPlaceRewardAtArming(ThreadDungeonRun run, System.Numerics.Vector3? player, string playerName, uint playerCell, ref PuzzleGatePlacement registered)
        {
            if (run.State != ThreadDungeonRunState.Active)
                return $"run is {run.State}";

            var sites = SiteSource(run.Dungeon?.Id);

            // The walk measure from the clearing player, when the graph and the player's cell allow one; otherwise the
            // selector's straight-line rule, and the reason goes on the INFO line.
            var walk = WalkFrom(run, sites, playerCell, player, out var walkWhy, out var blockedCount, out var openWalk);
            Func<uint, Vector3, float?> walkTo = walk == null ? null : walk.DistanceTo;

            // A site the walk reaches only with this run's closed gates open is behind one of them: the straight-line
            // fallback (nothing reachable on foot) must not choose it either.
            Func<uint, Vector3, bool> behindClosedGate = walk == null || openWalk == null
                ? null
                : (c, pt) => walk.DistanceTo(c, pt) == null && openWalk.DistanceTo(c, pt) != null;

            var choice = ThreadPuzzleRewardSelector.Select(sites, run.PlacedGatePicks, run.RewardFallbackPick, run.Spec?.Seed ?? 0, player, walkTo, behindClosedGate);

            if (choice == null)
                return "no candidate site";

            var placement = ArmingPlacer(run, choice.Pick, out var error);

            if (placement == null)
                return error ?? $"site {choice.Pick.Site?.Id} refused";

            registered = placement;
            run.MarkHasPuzzles();

            // The run moved on between the claim and here (an EndRun, or a fail-open path lifted the seal): the caller
            // clears this one placement (never the run's gates, which a live run still needs).
            if (!run.AttachRewardPlacement(placement))
                return $"run no longer pending (state {run.State})";

            log.Info($"[DYNDUNGEON] {run} {ArmingLine(placement.Id, choice, playerName, blockedCount, walkWhy)}");
            return null;
        }

        /// <summary>
        /// The arming INFO line's fields after the run tag: site, kind, type, n, player, the straight dist=, the walk=
        /// that chose it ("-" when the walk rule did not), rule=walk|straight, blocked_doorways=, walk_skipped=&lt;reason&gt;
        /// only when the walk was not used, and same_floor=. Pure.
        /// </summary>
        internal static string ArmingLine(int placementId, ThreadRewardArmingChoice choice, string playerName, int blockedCount, string walkWhy)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var dist = choice.Distance < 0 ? "-" : choice.Distance.ToString("0.0", inv);
            var walked = choice.Walk.HasValue ? choice.Walk.Value.ToString("0.0", inv) : "-";
            var kind = choice.CuratedFallback ? "curated" : choice.GateAsReward ? "gate-as-reward" : "reward";
            var why = choice.Rule == ThreadPuzzleRewardSelector.RuleWalk ? "" : $" walk_skipped={choice.WalkNote ?? walkWhy ?? "-"}";

            return $"reward scene #{placementId.ToString(inv)} placed at arming site={choice.Pick.Site.Id} kind={kind} type={choice.Pick.Type.ToString().ToLowerInvariant()} n={choice.Pick.N.ToString(inv)} player={playerName ?? "-"} dist={dist} walk={walked} rule={choice.Rule} blocked_doorways={blockedCount.ToString(inv)}{why} same_floor={(choice.SameFloor ? "true" : "false")}";
        }

        // ---- walk distance -----------------------------------------------------------------------------------

        /// <summary>Test seam: the dungeon's walk graph by landblock. Production: the dat-built, per-landblock cached graph.</summary>
        internal static Func<ushort, DungeonWalkGraph> WalkGraphSource = landblock => DungeonWalkGraphSource.Get(landblock, out _);

        /// <summary>
        /// Test seam: the site ids of this run's GATE puzzles still standing closed (registered, not solved).
        /// Production asks the puzzle registry.
        /// </summary>
        internal static Func<ThreadDungeonRun, IReadOnlyCollection<string>> UnsolvedGateSiteIds = run => PuzzleGateManager.UnsolvedGateSiteIds(run.RunId);

        /// <summary>
        /// The walk field from (<paramref name="cell"/>, <paramref name="from"/>) through the run's dungeon, with this run's
        /// still-closed gate doorways blocked. Null with the reason when no walk can be measured: no dungeon, no graph,
        /// no position, or a cell the graph does not know (the player is not in the dungeon's cells).
        /// </summary>
        internal static DungeonWalkGraph.WalkField WalkFrom(ThreadDungeonRun run, IReadOnlyList<PuzzleSiteDef> sites, uint cell, Vector3? from, out string why, out int blockedCount)
            => WalkFrom(run, sites, cell, from, out why, out blockedCount, out _);

        /// <param name="open">The same walk with every gate open; null unless a doorway was blocked (it would equal the walk).</param>
        internal static DungeonWalkGraph.WalkField WalkFrom(ThreadDungeonRun run, IReadOnlyList<PuzzleSiteDef> sites, uint cell, Vector3? from, out string why, out int blockedCount,
            out DungeonWalkGraph.WalkField open)
        {
            blockedCount = 0;
            open = null;

            if (!TryGetWalkGraph(run, out var graph, out why))
                return null;

            if (!from.HasValue || cell == 0)
            {
                why = "no-player-cell";
                return null;
            }

            if (!graph.HasCell(cell))
            {
                why = "cell-not-in-graph";
                return null;
            }

            var blocked = BlockedDoorways(graph, sites, SafeUnsolved(run));
            blockedCount = blocked.Count;

            var field = graph.From(cell, from.Value, blocked);
            why = field == null ? "no-field" : null;

            if (field != null && blocked.Count > 0)
                open = graph.From(cell, from.Value);

            return field;
        }

        private static bool TryGetWalkGraph(ThreadDungeonRun run, out DungeonWalkGraph graph, out string why)
        {
            graph = null;

            if (run?.Dungeon == null || run.Dungeon.Landblock == 0)
            {
                why = "no-dungeon";
                return false;
            }

            try
            {
                graph = WalkGraphSource(run.Dungeon.Landblock);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} walk graph for 0x{run.Dungeon.Landblock:X4} threw", ex);
                graph = null;
            }

            why = graph == null ? "no-graph" : null;
            return graph != null;
        }

        private static IReadOnlyCollection<string> SafeUnsolved(ThreadDungeonRun run)
        {
            try
            {
                return UnsolvedGateSiteIds(run) ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} could not list its closed gates; the walk ignores them", ex);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// The doorway each still-closed gate puzzle holds shut, as unordered cell pairs to keep the walk out of. A gate
        /// site records its approach-side anchor and yaw, and the gate stands GateDistance ahead of it (the same point
        /// PuzzleGateManager spawns it at); the doorway is the portal nearest that point (DungeonWalkGraph.NearestDoorway).
        /// A gate with no portal close enough blocks nothing. Pure.
        /// </summary>
        public static HashSet<(uint, uint)> BlockedDoorways(DungeonWalkGraph graph, IReadOnlyList<PuzzleSiteDef> sites, IReadOnlyCollection<string> closedSiteIds)
        {
            var blocked = new HashSet<(uint, uint)>();

            if (graph == null || sites == null || closedSiteIds == null || closedSiteIds.Count == 0)
                return blocked;

            foreach (var site in sites)
            {
                if (site?.Anchor == null || site.Kind != PuzzleSiteKind.Gate || site.Id == null || !closedSiteIds.Contains(site.Id))
                    continue;

                var doorway = graph.NearestDoorway(GatePoint(site));

                if (doorway.HasValue)
                    blocked.Add(doorway.Value);
            }

            return blocked;
        }

        /// <summary>Where a site's gate stands: GateDistance ahead of the anchor along its yaw (PuzzleGateLayout.GateLocalPoint).</summary>
        public static Vector3 GatePoint(PuzzleSiteDef site)
        {
            var distance = (site.Layout ?? PuzzleLayoutParams.Default).GateDistance;
            return PuzzleGateGenerator.ToWorld(new Vector3(site.Anchor.X, site.Anchor.Y, site.Anchor.Z), site.Yaw, new Vector3(0f, distance, 0f));
        }

        /// <summary>
        /// The armed notice for a run's attached reward scene: one line per recipient, each measured from where THAT
        /// member stands (their own walk, the same closed-gate blocking), plus the boss-chamber clause decided once for
        /// the scene. Built on the arming kill's thread; reads the static graph and the registry only.
        /// </summary>
        public static RewardArmedNotice BuildArmedNotice(ThreadDungeonRun run, PuzzleGatePlacement placement)
        {
            var anchor = placement?.Anchor;

            if (anchor == null)
                return new RewardArmedNotice(null, null, null, 0, false);

            TryGetWalkGraph(run, out var graph, out _);

            HashSet<(uint, uint)> blocked = null;

            if (graph != null)
                blocked = BlockedDoorways(graph, SiteSource(run.Dungeon?.Id), SafeUnsolved(run));

            var boss = run?.Dungeon?.BossAnchor;
            var bossChamber = false;

            if (boss != null)
            {
                var bossPos = new Vector3(boss.X, boss.Y, boss.Z);
                var toBoss = graph?.From(anchor.Cell, anchor.Pos, blocked)?.DistanceTo(boss.Cell, bossPos);
                bossChamber = ThreadPuzzleRewardSelector.IsBossChamber(anchor.Cell, anchor.Pos, boss.Cell, bossPos, toBoss);
            }

            return new RewardArmedNotice(graph, blocked, anchor.Pos, anchor.Cell, bossChamber);
        }

        /// <summary>The per-recipient armed line builder (see <see cref="BuildArmedNotice"/>).</summary>
        public sealed class RewardArmedNotice
        {
            private readonly DungeonWalkGraph graph;
            private readonly HashSet<(uint, uint)> blocked;

            public RewardArmedNotice(DungeonWalkGraph graph, HashSet<(uint, uint)> blocked, Vector3? anchor, uint anchorCell, bool bossChamber)
            {
                this.graph = graph;
                this.blocked = blocked;
                Anchor = anchor;
                AnchorCell = anchorCell;
                BossChamber = bossChamber;
            }

            public Vector3? Anchor { get; }

            public uint AnchorCell { get; }

            public bool BossChamber { get; }

            /// <summary>This member's walk to the scene, or null when it cannot be measured (the line then uses a straight line).</summary>
            public float? WalkFor(uint memberCell, Vector3? member)
            {
                if (graph == null || !Anchor.HasValue || !member.HasValue || memberCell == 0)
                    return null;

                return graph.From(memberCell, member.Value, blocked)?.DistanceTo(AnchorCell, Anchor.Value);
            }

            public string LineFor(bool inRunCopy, uint memberCell, Vector3? member)
                => ThreadPuzzleRewardSelector.ArmedLineForRecipient(inRunCopy, member, Anchor, inRunCopy ? WalkFor(memberCell, member) : null, BossChamber);

            /// <summary>The line for a member at <paramref name="location"/>: its cell starts their walk, its position the rest.</summary>
            public string LineFor(bool inRunCopy, Position location)
                => LineFor(inRunCopy, location?.Cell ?? 0, location?.Pos);
        }

        /// <summary>Builds and registers one pick's placement (its spawn queued). Null with the reason logged.</summary>
        private static PuzzleGatePlacement PlacePick(ThreadDungeonRun run, Landblock landblock, ThreadPuzzlePick pick, out PuzzleGateModel model)
        {
            var site = pick.Site;
            model = null;

            if (!ThreadPuzzleSitePicker.TryBuildOptions(pick, out var options, out var error))
            {
                log.Warn($"[DYNDUNGEON] {run} puzzle site {site.Id} {pick.Type} n={pick.N}: options refused: {error}");
                return null;
            }

            var rotation = PuzzleGateGenerator.YawQuaternion(site.Yaw);
            var anchor = new Position(site.Anchor.Cell, site.Anchor.X, site.Anchor.Y, site.Anchor.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, run.Instance);

            model = ModelFor(site, pick.IsReward);

            if (model == null)
            {
                log.Warn($"[DYNDUNGEON] {run} puzzle site {site.Id}: no placeable gate model");
                return null;
            }

            var spots = pick.Type == PuzzleGateType.Shuffle
                ? site.ShuffleSpots.Where(s => s != null).Select(s => new Vector3(s.X, s.Y, s.Z)).ToList()
                : new List<Vector3>();

            var host = new ThreadPuzzleRunHost(run, pick.IsReward);
            var placement = PuzzleGateManager.PlaceForRun(landblock, options, pick.Seed, anchor, site.Yaw, spots, host, model, site.Layout, out error, site.Id);

            if (placement == null)
            {
                log.Warn($"[DYNDUNGEON] {run} puzzle site {site.Id} {pick.Type}: not placed: {error}; the run stays open");
                return null;
            }

            return placement;
        }

        /// <summary>The gate model for a site: the reward scene's focal stand-in, or the curated door/barrier.</summary>
        public static PuzzleGateModel ModelFor(PuzzleSiteDef site, bool isReward)
        {
            if (isReward)
                return PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, PuzzleGateTunables.RewardFocalScript);

            var m = site?.GateModel;

            if (m == null)
                return null;

            switch (m.Kind)
            {
                case PuzzleGateModelKind.Door:
                    return PuzzleGateModel.Door(m.Wcid, m.Scale);
                case PuzzleGateModelKind.Barrier:
                    return PuzzleGateModel.Barrier(m.Wcid, m.Scale, m.Panels, site.Doorway?.Width ?? 0f);
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// The per-run puzzle watchdog, called from ThreadDungeonManager.Tick on the world thread for every run that
    /// placed puzzles. Reaps the run's dead placements (their hosts hear OnRemoved, which unseals a reward), and
    /// unseals a sealed run whose reward placement is no longer registered at all.
    /// </summary>
    public static class ThreadPuzzleWatchdog
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static void LogPendingStall(ThreadDungeonRun run)
            => log.Warn($"[DYNDUNGEON] {run} reward scene could not be placed at arming (armed with no placement past {ThreadDungeonRun.PendingArmedGrace.TotalSeconds:0}s); reward unsealed");

        /// <summary>Test seam: is this placement still alive? Production asks the manager's registry.</summary>
        internal static Func<object, bool> PlacementAlive = p => PuzzleGateManager.IsRegistered(p as PuzzleGatePlacement);

        /// <summary>Test seam: reap the run's dead placements. Production is PuzzleGateManager.ReapForRun.</summary>
        internal static Func<uint, DateTime, int> ReapRun = PuzzleGateManager.ReapForRun;

        /// <summary>Returns true when this call lifted a seal.</summary>
        public static bool Check(ThreadDungeonRun run, DateTime now)
        {
            if (run == null || !run.HasPuzzles)
                return false;

            ReapRun(run.RunId, now);

            if (!run.IsRewardSealed)
                return false;

            // Sealed PENDING (no reward scene yet, placed at arming): hold while the kills are still being done; once
            // armed, the arming kill places the scene synchronously, so a pending seal still armed with nothing
            // placed after the grace means that path was skipped - unseal (fail-open).
            if (run.IsRewardPending)
            {
                if (!run.PendingRewardStalled(now))
                    return false;

                if (ThreadPuzzleRunHost.UnsealRun(run, onRunLandblockThread: false))
                {
                    LogPendingStall(run);
                    return true;
                }

                return false;
            }

            var placement = run.RewardPuzzle;

            if (placement != null && PlacementAlive(placement))
                return false;

            // World thread: the follow-up is queued onto the run copy's landblock, never run here.
            return ThreadPuzzleRunHost.UnsealRun(run, onRunLandblockThread: false);
        }
    }
}
