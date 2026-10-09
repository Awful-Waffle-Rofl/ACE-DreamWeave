using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// The live layer of the puzzle gates: registry, spawning, activation routing, reshuffle, wrong-answer
    /// path, clear / reap and telemetry. The pure decisions live in PuzzleGateGenerator, PuzzleGateRules and
    /// PuzzleGatePlacement; this class only moves world objects.
    /// <para/>
    /// THREADING (fan-out invariant 9). Commands run off the landblock thread; activations arrive on it. Every
    /// world mutation - spawn, destroy, reshuffle, gate open, ambush - is QUEUED onto the placement's own
    /// landblock (landblock.EnqueueAction) and runs under the placement's Sync lock, so the activation that
    /// triggers a reshuffle and any activation queued behind it are strictly ordered. A lever retired by a
    /// reshuffle routes as StaleRound and is never scored (PuzzleGatePlacement.Route).
    /// <para/>
    /// A queued action is NOT a promise: a landblock unload clears its action queue (Landblock.Unload) and
    /// /reload-landblock destroys every non-persisted object (Landblock.DestroyAllNonPlayerObjects). So a
    /// placement whose gate is destroyed, or whose spawn never ran, is reaped on the next list / clear /
    /// place / reroll / activation instead of being trusted.
    /// </summary>
    public static class PuzzleGateManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<int, PuzzleGatePlacement> Placements = new ConcurrentDictionary<int, PuzzleGatePlacement>();

        /// <summary>Recorded shuffle spots per admin guid. In memory only; lost on restart by design.</summary>
        private static readonly ConcurrentDictionary<uint, List<Position>> Spots = new ConcurrentDictionary<uint, List<Position>>();

        private static int nextId;

        /// <summary>Terrain snap for ground objects outdoors is skipped when the terrain is further than this from the planned height (a roof, a bridge).</summary>
        private const float GroundSnapMaxDelta = 2.0f;

        // =============================================================================================
        // Commands (command thread)
        // =============================================================================================

        /// <summary>
        /// Validates, registers and QUEUES a placement at the admin's position and heading. Returns the admin
        /// reply; the spawn result follows as a second line once the landblock has run it.
        /// </summary>
        public static string Place(Player admin, PuzzleGateOptions options)
        {
            Reap(DateTime.UtcNow);

            if (!TryGetAdminLandblock(admin, out var landblock, out var error))
                return error;

            if (!CheckWeenies(options, PuzzleGateModel.Default, out error))
                return error;

            var seed = options.Seed ?? NewSeed();
            var anchor = new Position(admin.Location);
            var yaw = AnchorYawDeg(anchor);
            var spots = RecordedSpotsFor(admin.Guid.Full, anchor);

            var placement = Register(Interlocked.Increment(ref nextId), options, seed, anchor, yaw, spots, admin.Guid.Full, landblock);

            var spotNote = options.Type == PuzzleGateType.Shuffle
                ? (spots.Count >= PuzzleGateTunables.MinSpots ? $" using {spots.Count} recorded spot(s)" : $" on a {options.Radius.ToString("0.#", CultureInfo.InvariantCulture)} m ring (fewer than 2 recorded spots here)")
                : "";

            return $"Puzzle gate #{placement.Id} ({TypeName(options.Type)}) queued seed={seed} yaw={yaw.ToString("0.#", CultureInfo.InvariantCulture)}{spotNote}.";
        }

        /// <summary>Destroys a placement and places it again at the same anchor with the same options and a new seed.</summary>
        public static string Reroll(Player admin, int? id, int? seed)
        {
            var now = DateTime.UtcNow;
            Reap(now);

            if (!TryFind(admin, id, out var old, out var error))
                return error;

            if (!TryGetAdminLandblock(admin, out _, out error))
                return error;

            if (!CheckWeenies(old.Options, old.GateModel, out error))
                return error;

            ClearPlacement(old, "reroll", PuzzleRemovalReason.Cleared);

            var newSeed = seed ?? NewSeed();
            var placement = Register(old.Id, old.Options, newSeed, old.Anchor, old.AnchorYawDeg, old.RecordedSpots, admin.Guid.Full, old.Landblock, old.Host, old.GateModel, old.Layout, old.SiteId);

            return $"Puzzle gate #{placement.Id} rerolled seed={newSeed} (was {old.Seed}).";
        }

        /// <summary>clear [id|all]; no target = the nearest placement within range.</summary>
        public static string Clear(Player admin, string target)
        {
            var now = DateTime.UtcNow;
            Reap(now);

            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                var all = AdminPlacements().ToList();

                foreach (var p in all)
                    ClearPlacement(p, "clear all", PuzzleRemovalReason.Cleared);

                return $"Cleared {all.Count} puzzle gate(s).";
            }

            int? id = null;

            if (target != null)
            {
                if (!int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return $"Unknown clear target '{target}'. Expected an id, 'all', or nothing for the nearest.";

                id = parsed;
            }

            if (!TryFind(admin, id, out var placement, out var error))
                return error;

            ClearPlacement(placement, "clear", PuzzleRemovalReason.Cleared);
            return $"Cleared puzzle gate #{placement.Id}.";
        }

        public static List<string> List()
        {
            var now = DateTime.UtcNow;
            var reaped = Reap(now);

            var lines = new List<string>();

            if (reaped > 0)
                lines.Add($"Reaped {reaped} puzzle gate(s) whose gate was destroyed or whose spawn never ran.");

            var all = AdminPlacements().OrderBy(p => p.Id).ToList();

            if (all.Count == 0)
            {
                lines.Add("No puzzle gates are placed.");
                return lines;
            }

            foreach (var p in all)
            {
                lock (p.Sync)
                {
                    lines.Add(p.Summary(now));
                    lines.Add("   " + p.TelemetryLine(p.State == PuzzlePlacementState.Solved ? "solved" : "open", now));
                }
            }

            return lines;
        }

        /// <summary>Developer-and-up only (the command's access level): the current round's answer. Never routed to a player channel.</summary>
        public static string Reveal(Player admin, int? id)
        {
            Reap(DateTime.UtcNow);

            if (!TryFind(admin, id, out var p, out var error))
                return error;

            lock (p.Sync)
            {
                if (p.Plan == null)
                    return $"Puzzle gate #{p.Id} has no round yet ({p.State.ToString().ToLowerInvariant()}).";

                var sb = new StringBuilder();
                sb.Append($"#{p.Id} {TypeName(p.Type)} seed={p.Seed} n={p.Generator?.N}: {p.Plan.Describe()}");

                if (p.Type == PuzzleGateType.Beam)
                    sb.Append($" beams={p.Options.Beams}");

                if (p.Type == PuzzleGateType.Shuffle && p.Generator != null)
                    sb.Append($" spots={p.Generator.Spots.Count}");

                return sb.ToString();
            }
        }

        public static string SpotAdd(Player admin)
        {
            var list = Spots.GetOrAdd(admin.Guid.Full, _ => new List<Position>());
            int count;

            lock (list)
            {
                list.Add(new Position(admin.Location));
                count = list.Count(s => s.InstancedLandblock == admin.Location.InstancedLandblock);
            }

            return $"Spot recorded ({count} on this landblock instance). /puzzlegate place shuffle uses them when there are at least {PuzzleGateTunables.MinSpots}.";
        }

        public static List<string> SpotList(Player admin)
        {
            var lines = new List<string>();

            if (!Spots.TryGetValue(admin.Guid.Full, out var list))
            {
                lines.Add("No spots recorded.");
                return lines;
            }

            lock (list)
            {
                if (list.Count == 0)
                    lines.Add("No spots recorded.");

                for (var i = 0; i < list.Count; i++)
                {
                    var here = list[i].InstancedLandblock == admin.Location.InstancedLandblock ? " (this landblock)" : "";
                    lines.Add($"  {i + 1}: {list[i].ToLOCString()} inst=0x{list[i].Instance:X8}{here}");
                }
            }

            return lines;
        }

        public static string SpotClear(Player admin)
        {
            var removed = Spots.TryRemove(admin.Guid.Full, out var list) ? list.Count : 0;
            return $"Cleared {removed} recorded spot(s).";
        }

        // =============================================================================================
        // Activation hook (WorldObject.OnActivate)
        // =============================================================================================

        /// <summary>
        /// Called from WorldObject.OnActivate for an object carrying P_PuzzleGate. Only QUEUES the activation
        /// onto the placement's landblock: running it inline would let a reshuffle destroy the lever while its
        /// own OnActivate is still executing the ActivationResponse dispatch below the hook.
        /// </summary>
        public static void OnActivated(WorldObject lever, Player player)
        {
            var placement = lever?.P_PuzzleGate;

            if (placement == null || player == null)
                return;

            if (placement.Landblock is not Landblock landblock)
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() => HandleActivation(placement, lever, player)));
        }

        // =============================================================================================
        // Landblock thread
        // =============================================================================================

        private static void HandleActivation(PuzzleGatePlacement p, WorldObject lever, Player player)
        {
            var now = DateTime.UtcNow;
            var reap = false;

            // Host callbacks are collected under the lock and run AFTER it is released (IPuzzleGateHost contract),
            // so a host that takes its own locks - a run's state lock - can never order against p.Sync.
            Action after = null;

            lock (p.Sync)
            {
                var route = p.Route(lever.Guid.Full, lever.IsDestroyed, out var slot);

                if (route != PuzzleActivationRoute.Current)
                {
                    if (log.IsDebugEnabled)
                        log.Debug($"[PUZZLE_GATE] id={p.Id} activation ignored route={route} lever=0x{lever.Guid.Full:X8} player={player.Name}");
                    return;
                }

                if (p.GateMissing())
                {
                    reap = true;
                }
                else if (!p.Rules.Solved && p.Host.CheckActivation(p, player) is string refusal)
                {
                    // The host's veto (the run reward scene before the run's kills are done): not scored, not
                    // counted, no reset and no lockout - the pull simply did not happen.
                    Tell(player, refusal);

                    log.Info($"[PUZZLE_GATE] id={p.Id} activation refused by host player={player.Name} type={TypeName(p.Type)} run={p.Host.RunId} pulled={slot + 1} reason={refusal}");
                }
                else
                {
                    var landblock = (Landblock)p.Landblock;
                    var correct = p.IsCorrect(slot);
                    var sinceReshuffle = (long)(now - p.LastReshuffleUtc).TotalMilliseconds;

                    p.RecordActivation(now);
                    p.LastPullSlot = slot;

                    var result = p.Rules.Activate(correct, now);

                    log.Info(ActivationLine(p, player.Name, slot, correct, result, sinceReshuffle));

                    switch (result)
                    {
                        case PuzzleActivation.Refused:
                            Tell(player, PuzzleGateText.Locked);

                            // A wrong lever during the lockout: the rules refused it, so it is not scored.
                            if (!correct)
                                after = () => p.Host.OnWrong(p, player, false);
                            break;

                        case PuzzleActivation.AlreadySolved:
                            Tell(player, PuzzleGateText.AlreadySolved);
                            break;

                        case PuzzleActivation.RoundAdvanced:
                            Tell(player, PuzzleGateText.RoundAdvanced);
                            after = Reshuffle(p, landblock, now);
                            break;

                        case PuzzleActivation.Solved:
                            Tell(player, PuzzleGateText.SolvedFor(p.GateModel.Form, p.GateModel.SolveAction));
                            p.MarkSolved(player.Name, now);
                            RemoveGateForSolve(p, lever);
                            log.Info(p.TelemetryLine("solved", now));

                            // The host hears the solve FIRST (the reward scene's unseal), and only then does a scene
                            // that tears down on its solve leave the registry: its OnRemoved then arrives solved=true,
                            // so the fail-open unseal path is never reached and nothing warns.
                            after = SolvedFollowUp(p, player);
                            break;

                        case PuzzleActivation.Wrong:
                            // Fan-out invariant 6: the ONE wrong-answer path for every type. Rules already
                            // reset progress, counted the wrong and started the lockout.
                            Tell(player, PuzzleGateText.WrongAnswer);

                            if (p.Host.AllowAmbush)
                                SpawnAmbush(p, landblock);

                            var reshuffleFailed = Reshuffle(p, landblock, now);
                            after = () =>
                            {
                                p.Host.OnWrong(p, player, true);
                                reshuffleFailed?.Invoke();
                            };
                            break;
                    }
                }
            }

            after?.Invoke();

            if (reap)
                ClearPlacement(p, "gate destroyed", PuzzleRemovalReason.Reaped);
        }

        /// <summary>
        /// The per-pull INFO line: enough that one grep on the player's name answers "I picked the right lever and
        /// it failed me". expected is the 1-based correct slot, pulled the 1-based slot the player activated, cues
        /// what each lever looked like this round (PuzzleGateCues; the correct one starred). Reads the placement
        /// state only, under p.Sync.
        /// </summary>
        internal static string ActivationLine(PuzzleGatePlacement p, string playerName, int slot, bool correct, PuzzleActivation result, long sinceReshuffleMs)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "[PUZZLE_GATE] id={0} activation player={1} type={2} run={3} round={4} expected={5} pulled={6} correct={7} result={8} ms_since_reshuffle={9} cues={10}",
                p.Id, playerName, TypeName(p.Type), p.Host.RunId, p.Plan?.RoundIndex, p.Plan != null ? p.Plan.AnswerSlot + 1 : 0, slot + 1,
                correct, result, sinceReshuffleMs, PuzzleGateCues.Describe(p.Plan));
        }

        /// <summary>Test seam: the visible fade-and-destroy a solve applies. Production is WorldObject.FadeOutAndDestroy.</summary>
        internal static Action<WorldObject> FadeOut = wo => wo.FadeOutAndDestroy();

        /// <summary>
        /// A solve's effect on the gate objects, by the model's rule (PuzzleGateTunables.SolveActionFor): a door
        /// with a real open animation opens through OpenObjectiveGate; anything else - a zero-length-animation door,
        /// a barrier, a focal object - fades out and is destroyed. A model that tears its whole scene down on a solve
        /// (<see cref="PuzzleGateModel.TearsDownOnSolve"/>: the Thread reward scene's focal object) also fades out every
        /// lever, light, indicator and beam host, and the placement's lists are emptied so the clear that follows
        /// (<see cref="FinishSolve"/>) has nothing left to cut short mid-fade. A gate puzzle keeps its levers, which
        /// answer with the "spent" line. Landblock thread, under p.Sync.
        /// </summary>
        internal static void RemoveGateForSolve(PuzzleGatePlacement p, WorldObject lever)
        {
            var teardown = p.GateModel.TearsDownOnSolve;

            if (p.GateModel.SolveAction == PuzzleGateSolveAction.Open && !teardown)
            {
                foreach (var part in p.GateParts)
                    if (part != null && !part.IsDestroyed)
                        WorldObject.OpenObjectiveGate(part, lever);

                return;
            }

            p.GateRemovedBySolve = true;

            foreach (var part in p.GateParts)
            {
                if (part == null || part.IsDestroyed)
                    continue;

                // A barrier must stop blocking now, not after the fade: unlock it first so a monster's
                // collision-open (Door.ActOnUse) or a player is not held for the fade's second.
                if (part is Door door && door.IsLocked)
                    door.IsLocked = false;

                FadeOut(part);
            }

            if (!teardown)
                return;

            // The levers stop answering at once (no P_PuzzleGate, and the round retired), then fade with the rest.
            p.RetireRound();

            foreach (var wo in p.RoundObjects)
            {
                if (wo == null)
                    continue;

                wo.P_PuzzleGate = null;

                if (!wo.IsDestroyed)
                    FadeOut(wo);
            }

            p.RoundObjects.Clear();
            p.GateParts.Clear();
            p.Gate = null;
        }

        /// <summary>
        /// What runs once p.Sync is released after a solve: the host hears it, then <see cref="FinishSolve"/> runs in a
        /// finally, so a host that throws still lets a tearing-down scene leave the registry (and lifts the reap hold).
        /// </summary>
        internal static Action SolvedFollowUp(PuzzleGatePlacement p, Player player) => () =>
        {
            try
            {
                p.Host.OnSolved(p, player);
            }
            finally
            {
                FinishSolve(p);
            }
        };

        /// <summary>
        /// After the host has heard a solve: a placement whose model tears down on its solve leaves the registry now
        /// (its objects are already fading, see <see cref="RemoveGateForSolve"/>). The host's OnRemoved arrives with
        /// solved=true, which the run host ignores, so no second unseal and no "removed without a solve" WARN. Any
        /// other placement stays registered until its run ends or an admin clears it. Outside p.Sync.
        /// </summary>
        internal static void FinishSolve(PuzzleGatePlacement p)
        {
            if (p == null)
                return;

            // From here a reap may take it (AwaitingSolveFinish turns false); the clear below normally gets there first.
            p.MarkSolveFinished();

            if (!p.GateModel.TearsDownOnSolve)
                return;

            ClearPlacement(p, "scene solved", PuzzleRemovalReason.Cleared);
        }

        /// <summary>The initial spawn: gate once, then round 1. Fails closed - anything that entered is destroyed.</summary>
        private static void SpawnInitial(PuzzleGatePlacement p)
        {
            var now = DateTime.UtcNow;
            string failure = null;

            lock (p.Sync)
            {
                if (p.State != PuzzlePlacementState.Spawning)
                    return; // cleared before the queued spawn ran

                var landblock = (Landblock)p.Landblock;
                var instance = p.Anchor.Instance;

                var input = new PuzzleGateInput
                {
                    AnchorPosition = p.Anchor.Pos,
                    AnchorYawDeg = p.AnchorYawDeg,
                    Options = p.Options,
                    Seed = p.Seed,
                    RecordedSpots = p.RecordedSpots,
                    Layout = p.Layout,
                    SpotValidator = v => TryResolve(landblock, instance, v, true, out _, out _),
                };

                if (!PuzzleGateGenerator.TryCreate(input, out var generator, out var error))
                {
                    failure = error;
                }
                else
                {
                    p.Generator = generator;

                    var plan = generator.Next();

                    if (!SpawnGate(p, landblock, plan, out error))
                    {
                        failure = "gate: " + error;
                    }
                    else if (!SpawnRound(p, landblock, plan, now, out error))
                    {
                        failure = error;
                    }
                }

                if (failure != null)
                {
                    p.MarkFailed();
                    DestroyWorldObjects(p);
                }
                else
                {
                    p.MarkLive(now);
                }
            }

            if (failure != null)
            {
                Placements.TryRemove(new KeyValuePair<int, PuzzleGatePlacement>(p.Id, p));
                log.Warn($"[PUZZLE_GATE] id={p.Id} type={TypeName(p.Type)} seed={p.Seed} spawn failed at {p.Anchor.ToLOCString()}: {failure}{p.SweepTag}");
                p.Host.Report(p, $"Puzzle gate #{p.Id} failed to spawn: {failure}");
                NotifyRemoved(p, PuzzleRemovalReason.SpawnFailed, false);
                return;
            }

            p.Host.Report(p, $"Puzzle gate #{p.Id} ({TypeName(p.Type)}) is live: seed={p.Seed}, n={p.Generator.N}, {p.Options.Rounds} round(s), {p.RoundObjects.Count + p.GateParts.Count} object(s).");

            SchedulePromptScan(p);
        }

        /// <summary>
        /// Spawns every gate object of the placement's model (PuzzleGateModel.PartLocals: one door, N barrier
        /// panels, or one focal object), each registered in p.GateParts BEFORE EnterWorld and carrying the
        /// puzzle-gate marker. On any failure it destroys every part already spawned and returns false.
        /// </summary>
        private static bool SpawnGate(PuzzleGatePlacement p, Landblock landblock, PuzzlePlan plan, out string error)
        {
            error = null;
            var model = p.GateModel;
            var prepare = model.RequiresDoor ? (Func<WorldObject, string>)PrepareGate : PrepareFocal;
            var parts = model.PartLocals(plan.Gate.LocalPosition);

            for (var i = 0; i < parts.Count; i++)
            {
                var world = PuzzleGateGenerator.ToWorld(p.Anchor.Pos, p.AnchorYawDeg, parts[i]);
                var scale = plan.Gate.Scale * model.Scale;
                var script = model.Script != 0 ? model.Script : plan.Gate.Script;

                // Doors and barrier panels stand on the ground; a focal object hangs at its planned height.
                var wo = CreateWcid(p, landblock, model.Wcid, world, plan.Gate.Orientation, scale, script, model.RequiresDoor, prepare, out error);

                if (wo == null)
                {
                    DestroyGateParts(p);
                    return false;
                }

                p.GateParts.Add(wo); // registered before EnterWorld

                if (p.Gate == null)
                    p.Gate = wo;

                if (!Enter(wo, out error))
                {
                    p.GateParts.Remove(wo);
                    DestroyGateParts(p);
                    return false;
                }
            }

            return true;
        }

        private static void DestroyGateParts(PuzzleGatePlacement p)
        {
            foreach (var wo in p.GateParts)
            {
                if (wo != null && !wo.IsDestroyed)
                    wo.Destroy();
            }

            p.GateParts.Clear();

            if (p.Gate != null && !p.Gate.IsDestroyed)
                p.Gate.Destroy();

            p.Gate = null;
        }

        // ---- approach prompt -------------------------------------------------------------------------

        /// <summary>
        /// The approach-prompt loop: a delayed action on the placement's OWN landblock queue every
        /// PromptScanSeconds, re-queued by itself until PuzzlePromptTracker.Decide says Stop (solved, failed,
        /// cleared or reaped). Nothing is added to the landblock tick. Per placement per second the loop allocates
        /// one ActionChain with its delay and action links and the closure that re-queues PromptScan; the scan
        /// itself is a for-loop over the landblock's player list, and the prompt tracker allocates only when its
        /// dictionary or reusable buffer has to grow. Sending a prompt allocates that one chat message. If the landblock unloads, its
        /// queue is cleared and the loop simply never runs again.
        /// </summary>
        private static void SchedulePromptScan(PuzzleGatePlacement p)
        {
            if (p.Landblock is not Landblock landblock)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(PuzzleGateTunables.PromptScanSeconds);
            chain.AddAction(landblock, () => PromptScan(p));
            chain.EnqueueChain();
        }

        private static void PromptScan(PuzzleGatePlacement p)
        {
            PuzzlePromptScan decision;

            lock (p.Sync)
            {
                decision = PuzzlePromptTracker.Decide(p.State);

                if (decision == PuzzlePromptScan.Scan && p.Landblock is Landblock landblock)
                {
                    var players = landblock.PlayersOnLandblockThread;
                    var text = PuzzleGateText.PromptFor(p.Type, p.Options.Beams);

                    p.Prompts.BeginScan();

                    for (var i = 0; i < players.Count; i++)
                    {
                        var player = players[i];
                        var at = player?.Location;

                        if (at == null)
                            continue;

                        if (p.Prompts.Observe(player.Guid.Full, Vector3.Distance(at.Pos, p.PromptCentre)))
                        {
                            Tell(player, text);

                            if (log.IsDebugEnabled)
                                log.Debug($"[PUZZLE_GATE] id={p.Id} prompt player={player.Name} text={text}");
                        }
                    }

                    p.Prompts.EndScan();
                }
            }

            if (decision != PuzzlePromptScan.Stop)
                SchedulePromptScan(p);
        }

        /// <summary>
        /// Spawns every answer-bearing object of <paramref name="plan"/> in spec (slot) order and starts the
        /// round. On any failure it destroys what this round spawned and returns false.
        /// </summary>
        private static bool SpawnRound(PuzzleGatePlacement p, Landblock landblock, PuzzlePlan plan, DateTime now, out string error)
        {
            error = null;
            var candidates = new List<(uint Guid, int Slot)>();

            foreach (var spec in plan.Specs)
            {
                var wo = Create(p, landblock, spec.Role, spec.Position, spec.Orientation, spec.Scale, spec.Script, null, out error);

                if (wo == null)
                {
                    error = $"{spec.Role.ToString().ToLowerInvariant()} slot {spec.Slot + 1}: {error}";
                    DestroyRound(p);
                    return false;
                }

                // Registered BEFORE EnterWorld (fan-out invariant 5).
                p.RoundObjects.Add(wo);

                if (spec.Role == PuzzleRole.Candidate)
                    wo.P_PuzzleGate = p;

                if (!Enter(wo, out error))
                {
                    p.RoundObjects.Remove(wo);
                    wo.P_PuzzleGate = null;
                    error = $"{spec.Role.ToString().ToLowerInvariant()} slot {spec.Slot + 1}: {error}";
                    DestroyRound(p);
                    return false;
                }

                if (spec.Role == PuzzleRole.Candidate)
                    candidates.Add((wo.Guid.Full, spec.Slot));
            }

            p.BeginRound(plan, candidates, now);
            return true;
        }

        /// <summary>
        /// Fan-out invariant 4: destroys and recreates EVERY answer-bearing object from the placement's RNG
        /// stream. The gate is never respawned. Retiring the round first is what turns any activation already
        /// queued against an old lever into a StaleRound no-op.
        /// </summary>
        /// <returns>
        /// Null when the next round spawned. On a failure, the host notification to run once p.Sync is released
        /// (the placement is already cleared and its objects destroyed by then).
        /// </returns>
        private static Action Reshuffle(PuzzleGatePlacement p, Landblock landblock, DateTime now)
        {
            p.RetireRound();
            DestroyRound(p);

            var plan = p.Generator.Next();

            if (SpawnRound(p, landblock, plan, now, out var error))
                return null;

            // A round that cannot spawn leaves a gate nobody can open; fail the whole placement loudly.
            log.Warn($"[PUZZLE_GATE] id={p.Id} reshuffle to round {plan.RoundIndex} failed: {error}");
            var report = $"Puzzle gate #{p.Id} reshuffle failed and the placement was cleared: {error}";

            var wasSolved = p.State == PuzzlePlacementState.Solved;
            p.MarkCleared();
            Placements.TryRemove(new KeyValuePair<int, PuzzleGatePlacement>(p.Id, p));
            DestroyWorldObjects(p);

            if (!wasSolved)
                log.Info(p.TelemetryLine("cleared", now));

            return ReshuffleFailedFollowUp(p, report, wasSolved);
        }

        /// <summary>The host side of a failed reshuffle, run after p.Sync is released: the report, then OnRemoved (once).</summary>
        private static Action ReshuffleFailedFollowUp(PuzzleGatePlacement p, string report, bool wasSolved) => () =>
        {
            p.Host.Report(p, report);
            NotifyRemoved(p, PuzzleRemovalReason.ReshuffleFailed, wasSolved);
        };

        /// <summary>Tells the host its placement is gone, exactly once per placement, never under p.Sync.</summary>
        private static void NotifyRemoved(PuzzleGatePlacement p, PuzzleRemovalReason reason, bool solved)
        {
            if (!p.TryClaimRemovalNotice())
                return;

            try
            {
                p.Host.OnRemoved(p, reason, solved);
            }
            catch (Exception ex)
            {
                log.Error($"[PUZZLE_GATE] id={p.Id} host OnRemoved threw ({reason})", ex);
            }
        }

        private static void SpawnAmbush(PuzzleGatePlacement p, Landblock landblock)
        {
            var wcid = p.Options.AmbushWcid;

            if (wcid == 0)
                return;

            p.Ambush.RemoveAll(a => a == null || a.IsDestroyed || (a is Creature c && c.IsDead));

            var count = Math.Min(p.Options.AmbushCount, PuzzleGateTunables.MaxLiveAmbush - p.Ambush.Count);

            for (var i = 0; i < count; i++)
            {
                var local = new Vector3((i - (count - 1) / 2.0f) * PuzzleGateTunables.LeverSpacing, PuzzleGateTunables.AmbushRowDistance, 0.0f);
                var world = PuzzleGateGenerator.ToWorld(p.Anchor.Pos, p.AnchorYawDeg, local);
                var facing = PuzzleGateGenerator.YawQuaternion(p.AnchorYawDeg + 180.0f);

                var wo = CreateWcid(p, landblock, wcid, world, facing, 1.0f, 0, true, PrepareAmbush, out var error);

                if (wo == null)
                {
                    log.Warn($"[PUZZLE_GATE] id={p.Id} ambush wcid {wcid}: {error}");
                    continue;
                }

                p.Ambush.Add(wo);

                if (!Enter(wo, out error))
                {
                    p.Ambush.Remove(wo);
                    log.Warn($"[PUZZLE_GATE] id={p.Id} ambush wcid {wcid}: {error}");
                }
            }
        }

        // ---- spawn primitives ------------------------------------------------------------------------

        private static WorldObject Create(PuzzleGatePlacement p, Landblock landblock, PuzzleRole role, Vector3 at, Quaternion rotation, float scale, uint script,
            Func<WorldObject, string> prepare, out string error)
        {
            // Levers and the gate stand on the ground; lights hang at their planned height.
            var ground = role == PuzzleRole.Candidate || role == PuzzleRole.Gate;
            return CreateWcid(p, landblock, PuzzleGateTunables.WcidFor(role), at, rotation, scale, script, ground, prepare, out error);
        }

        /// <summary>
        /// Creates one object and stamps everything BEFORE EnterWorld (fan-out invariant 5): an
        /// instance-stamped position with the full world quaternion, scale, VisualEffectScript (so
        /// Player.TrackObject's VisualEffectManager.SendTo replays it to every client that builds the object,
        /// including the first), TimeToRot -1 and IsTransientSpawn. Returns null with a reason; never enters.
        /// </summary>
        private static WorldObject CreateWcid(PuzzleGatePlacement p, Landblock landblock, uint wcid, Vector3 at, Quaternion rotation, float scale, uint script, bool ground,
            Func<WorldObject, string> prepare, out string error)
        {
            var instance = p.Anchor.Instance;

            if (!TryResolve(landblock, instance, at, ground, out var cell, out var pos))
            {
                error = string.Format(CultureInfo.InvariantCulture, "wcid {0} at ({1:0.00}, {2:0.00}, {3:0.00}) is outside every cell of landblock 0x{4:X4}", wcid, at.X, at.Y, at.Z, landblock.Id.Landblock);
                return null;
            }

            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
            {
                error = $"wcid {wcid} did not create - is its weenie in the world database?";
                return null;
            }

            wo.Location = new Position(cell, pos, rotation) { Instance = instance };

            if (Math.Abs(scale - 1.0f) > 1e-6f)
                wo.ObjScale = (wo.ObjScale ?? 1.0f) * scale;

            if (script != 0)
                wo.VisualEffectScript = script;

            wo.TimeToRot = -1;
            wo.IsTransientSpawn = true;

            error = prepare?.Invoke(wo);

            if (error != null)
            {
                wo.Destroy();
                return null;
            }

            return wo;
        }

        /// <summary>EnterWorld, destroying the object on a refusal or a throw.</summary>
        private static bool Enter(WorldObject wo, out string error)
        {
            error = null;
            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[PUZZLE_GATE] EnterWorld threw for wcid {wo.WeenieClassId} at {wo.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (entered)
                return true;

            error = $"wcid {wo.WeenieClassId} failed to enter the world at {wo.Location?.ToLOCString()}";
            wo.P_PuzzleGate = null;
            wo.Destroy();
            return false;
        }

        /// <summary>
        /// The gate must be a Door: OpenObjectiveGate's Door branch is what unlocks and opens it, and that
        /// branch never consults Active. Active is forced off and the door locked as belt and braces, so a
        /// mis-authored weenie still cannot be clicked open (Door.ActOnUse lets a player BEHIND a locked door
        /// open it, and only Active = 0 keeps that path unreachable).
        /// </summary>
        private static string PrepareGate(WorldObject wo)
        {
            if (wo is not Door door)
                return $"wcid {wo.WeenieClassId} is a {wo.WeenieType}, not a Door";

            door.Active = false;
            door.IsLocked = true;

            // Never pickable or keyable, whatever the weenie says: no ResistLockpick row means a lockpick cannot
            // open it (Lock.cs), and an empty LockCode means no key matches (UnlockerHelper refuses an empty code).
            door.RemoveProperty(PropertyInt.ResistLockpick);
            door.LockCode = "";

            // The runtime marker: ThreadDungeonSpawner.UnlockDoors skips it, so a run's door pass can never
            // unlock a puzzle gate whatever order the two run in.
            door.IsPuzzleGateObject = true;
            return null;
        }

        /// <summary>
        /// A focal gate object (the Thread reward scene's stand-in target): any weenie type, never a Door, so there
        /// is nothing to lock. Marked like a gate so the door pass and anything else that walks a copy can tell it
        /// apart from content.
        /// </summary>
        private static string PrepareFocal(WorldObject wo)
        {
            if (wo is Door)
                return $"wcid {wo.WeenieClassId} is a Door; a focal gate must not be one";

            wo.IsPuzzleGateObject = true;
            return null;
        }

        /// <summary>
        /// An ambush is summoned for free by a wrong pull, so its death must pay nothing. The real guard is the
        /// runtime flag, read by Creature.OnDeath / Creature.Die through Creature.IsRewardlessDeath. Stripping
        /// the reward-bearing properties here is defence in depth only: emotes (a Death emote can pay out) are
        /// not strippable per instance, which is exactly why the death-path guard has to exist.
        /// </summary>
        private static string PrepareAmbush(WorldObject wo)
        {
            if (wo is not Creature creature)
                return $"wcid {wo.WeenieClassId} is a {wo.WeenieType}, not a Creature";

            creature.IsPuzzleAmbush = true;

            creature.RemoveProperty(PropertyInt.XpOverride);
            creature.RemoveProperty(PropertyInt.LuminanceAward);
            creature.RemoveProperty(PropertyDataId.DeathTreasureType);
            creature.RemoveProperty(PropertyString.KillQuest);
            creature.RemoveProperty(PropertyString.KillQuest2);
            creature.RemoveProperty(PropertyString.KillQuest3);
            creature.RemoveProperty(PropertyInt.DeathSpawnWcid);
            creature.RemoveProperty(PropertyString.ObjectiveLockKey);
            creature.RemoveProperty(PropertyBool.SpeedChallengeBoss);
            creature.DeathTreasureOverride = null;

            return null;
        }

        /// <summary>
        /// Resolves a landblock-local point to a cell of THIS landblock. Never crosses into a neighbour: a
        /// placement only ever touches its own landblock's thread. Dungeons use AdjustCell (the lookup
        /// PvpLiveAdapters.PlaceZoneMarker uses); outdoors the point must lie inside the 192 m block and
        /// Position.GetCell picks a building or cave cell when one holds it. Ground objects on an outdoor land
        /// cell snap to terrain when it is within GroundSnapMaxDelta of the planned height.
        /// </summary>
        internal static bool TryResolve(Landblock landblock, uint instance, Vector3 point, bool ground, out uint cell, out Vector3 resolved)
        {
            cell = 0;
            resolved = point;

            if (float.IsNaN(point.X) || float.IsNaN(point.Y) || float.IsNaN(point.Z))
                return false;

            if (landblock.IsDungeon)
            {
                var c = AdjustCell.Get(landblock.Id.Landblock, instance).GetCell(point);

                if (c == null)
                    return false;

                cell = c.Value;
                return true;
            }

            if (point.X < 0 || point.Y < 0 || point.X >= Position.BlockLength || point.Y >= Position.BlockLength)
                return false;

            var probe = new Position(SkyDecorLayout.CellId(landblock.Id.Landblock, point.X, point.Y), point.X, point.Y, point.Z, 0f, 0f, 0f, 1f, instance);

            cell = probe.GetCell();

            if (cell == 0)
                return false;

            if (ground && (cell & 0xFFFF) < 0x100)
            {
                var terrain = landblock.PhysicsLandblock.GetZ(new Vector3(point.X, point.Y, 0));

                if (Math.Abs(terrain - point.Z) <= GroundSnapMaxDelta)
                    resolved = new Vector3(point.X, point.Y, terrain);
            }

            return true;
        }

        // ---- destroy ---------------------------------------------------------------------------------

        private static void DestroyRound(PuzzleGatePlacement p)
        {
            foreach (var wo in p.RoundObjects)
            {
                wo.P_PuzzleGate = null;

                if (!wo.IsDestroyed)
                    wo.Destroy();
            }

            p.RoundObjects.Clear();
        }

        /// <summary>Destroys everything the placement owns. Ambush creatures are destroyed, never Die()'d.</summary>
        private static void DestroyWorldObjects(PuzzleGatePlacement p)
        {
            DestroyRound(p);

            foreach (var wo in p.Ambush)
            {
                if (wo != null && !wo.IsDestroyed)
                    wo.Destroy();
            }

            p.Ambush.Clear();

            DestroyGateParts(p);
        }

        // =============================================================================================
        // Registry
        // =============================================================================================

        private static PuzzleGatePlacement Register(int id, PuzzleGateOptions options, int seed, Position anchor, float yaw, IReadOnlyList<Vector3> spots, uint placedBy, object landblock,
            IPuzzleGateHost host = null, PuzzleGateModel gateModel = null, PuzzleLayoutParams layout = null, string siteId = null)
        {
            var placement = new PuzzleGatePlacement(id, options, seed, anchor, yaw, spots, placedBy, landblock, DateTime.UtcNow, host, gateModel, layout) { SiteId = siteId };

            if (!Placements.TryAdd(id, placement))
            {
                placement = new PuzzleGatePlacement(Interlocked.Increment(ref nextId), options, seed, anchor, yaw, spots, placedBy, landblock, DateTime.UtcNow, host, gateModel, layout) { SiteId = siteId };
                Placements[placement.Id] = placement;
            }

            ((Landblock)landblock).EnqueueAction(new ActionEventDelegate(() => SpawnInitial(placement)));

            return placement;
        }

        /// <summary>
        /// Removes a placement and queues the destroy of everything it owns onto its landblock. Idempotent.
        /// Logs the terminal "cleared" line only for a placement that was never solved (a solved one already
        /// logged "solved").
        /// </summary>
        private static void ClearPlacement(PuzzleGatePlacement p, string reason, PuzzleRemovalReason removal)
        {
            var now = DateTime.UtcNow;
            bool wasSolved;

            lock (p.Sync)
            {
                if (p.State == PuzzlePlacementState.Cleared)
                {
                    Placements.TryRemove(new KeyValuePair<int, PuzzleGatePlacement>(p.Id, p));
                    return;
                }

                wasSolved = p.State == PuzzlePlacementState.Solved;
                p.MarkCleared();
            }

            Placements.TryRemove(new KeyValuePair<int, PuzzleGatePlacement>(p.Id, p));

            if (wasSolved)
                log.Debug($"[PUZZLE_GATE] id={p.Id} removed after solve ({reason})");
            else
                log.Info(p.TelemetryLine("cleared", now));

            // Queued even when the landblock may be gone: an unloaded landblock already destroyed every
            // non-persisted object, so the action not running loses nothing.
            if (p.Landblock is Landblock landblock)
            {
                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    lock (p.Sync)
                        DestroyWorldObjects(p);
                }));
            }

            NotifyRemoved(p, removal, wasSolved);
        }

        // =============================================================================================
        // Thread runs
        // =============================================================================================

        /// <summary>
        /// Registers a run placement and QUEUES its spawn onto <paramref name="landblock"/>. Called on that
        /// landblock's own thread by the run's puzzle pass, so the queued spawn runs ahead of anything the pass
        /// enqueues after it (the first creature batch). Returns null with a reason when a weenie it needs is
        /// missing; the host is then NOT notified (nothing was registered).
        /// </summary>
        public static PuzzleGatePlacement PlaceForRun(Landblock landblock, PuzzleGateOptions options, int seed, Position anchor, float yawDeg,
            IReadOnlyList<Vector3> spots, IPuzzleGateHost host, PuzzleGateModel gateModel, PuzzleLayoutParams layout, out string error, string siteId = null)
        {
            if (landblock == null || options == null || anchor == null || host == null || gateModel == null)
            {
                error = "missing landblock, options, anchor, host or gate model";
                return null;
            }

            if (!CheckWeenies(options, gateModel, out error))
                return null;

            return Register(Interlocked.Increment(ref nextId), options, seed, anchor, yawDeg, spots ?? Array.Empty<Vector3>(), 0, landblock, host, gateModel, layout, siteId);
        }

        /// <summary>Last curated-site placement per admin guid, so the next /puzzlegate site clears it first.</summary>
        private static readonly ConcurrentDictionary<uint, int> AdminSitePlacements = new ConcurrentDictionary<uint, int>();

        /// <summary>
        /// /puzzlegate site: places a curated site's puzzle exactly as a run would (the caller supplies the same
        /// options, model, layout and spots) but under the ADMIN host, so it has RunId 0 and list / reveal / clear /
        /// reroll manage it. Clears this admin's previous site placement first. Landblock thread or command thread.
        /// </summary>
        public static PuzzleGatePlacement PlaceForAdminSite(Player admin, Landblock landblock, PuzzleGateOptions options, int seed, Position anchor, float yawDeg,
            IReadOnlyList<Vector3> spots, PuzzleGateModel gateModel, PuzzleLayoutParams layout, string siteId, out string error)
        {
            error = null;

            if (admin == null || landblock == null || options == null || anchor == null || gateModel == null)
            {
                error = "missing admin, landblock, options, anchor or gate model";
                return null;
            }

            Reap(DateTime.UtcNow);

            if (!CheckWeenies(options, gateModel, out error))
                return null;

            // The read-modify-write of this admin's previous site placement is one atomic step, so two concurrent
            // site commands can never both see "nothing to clear" and leave two placements.
            lock (AdminSitePlacements)
            {
                if (AdminSitePlacements.TryRemove(admin.Guid.Full, out var previousId)
                    && Placements.TryGetValue(previousId, out var previous) && ShouldClearPrevious(previous))
                    ClearPlacement(previous, "site replaced", PuzzleRemovalReason.Cleared);

                var placement = Register(Interlocked.Increment(ref nextId), options, seed, anchor, yawDeg, spots ?? Array.Empty<Vector3>(), admin.Guid.Full, landblock,
                    SiteTourPuzzleGateHost.Instance, gateModel, layout, siteId);

                AdminSitePlacements[admin.Guid.Full] = placement.Id;
                return placement;
            }
        }

        /// <summary>Pure: is this the admin-managed, still-live placement a new site command should clear first? Never a run's placement.</summary>
        internal static bool ShouldClearPrevious(PuzzleGatePlacement previous)
            => previous != null && IsAdminManaged(previous) && previous.State != PuzzlePlacementState.Cleared;

        /// <summary>
        /// The curated site ids of <paramref name="runId"/>'s GATE puzzles that still stand closed: registered, spawning
        /// or live, never solved. The reward scene (a model that tears down on its solve) is not a gate and is skipped.
        /// Read by the reward placer's walk, which must not route through a closed doorway. Safe from any thread.
        /// </summary>
        public static List<string> UnsolvedGateSiteIds(uint runId)
        {
            var ids = new List<string>();

            if (runId == 0)
                return ids;

            foreach (var p in Placements.Values)
            {
                if (p.Host.RunId != runId || p.SiteId == null || p.GateModel.TearsDownOnSolve)
                    continue;

                PuzzlePlacementState state;

                lock (p.Sync)
                    state = p.State;

                if (state == PuzzlePlacementState.Spawning || state == PuzzlePlacementState.Live)
                    ids.Add(p.SiteId);
            }

            return ids;
        }

        /// <summary>Is this placement still in the registry? (Removed = cleared, reaped or failed.)</summary>
        public static bool IsRegistered(PuzzleGatePlacement p) => p != null && Placements.TryGetValue(p.Id, out var live) && ReferenceEquals(live, p);

        /// <summary>
        /// Clears every placement belonging to <paramref name="runId"/> (EndRun). Each host hears OnRemoved
        /// (Cleared) once; the destroy is queued onto the run copy's landblock. Safe from any thread.
        /// </summary>
        public static int ClearForRun(uint runId)
        {
            if (runId == 0)
                return 0;

            var cleared = 0;

            foreach (var p in Placements.Values.ToList())
            {
                if (p.Host.RunId != runId)
                    continue;

                ClearPlacement(p, "run ended", PuzzleRemovalReason.Cleared);
                cleared++;
            }

            return cleared;
        }

        /// <summary>
        /// Clears ONE run placement (the reward-at-arming placer's orphan: registered, then refused by the run). Its host
        /// hears OnRemoved (Cleared) once; the destroy is queued onto its landblock. A no-op for an admin placement or
        /// null. Safe from any thread.
        /// </summary>
        public static void ClearRunPlacement(PuzzleGatePlacement p, string reason)
        {
            if (p == null || IsAdminManaged(p))
                return;

            ClearPlacement(p, reason, PuzzleRemovalReason.Cleared);
        }

        /// <summary>
        /// The run watchdog's half (ThreadDungeonManager.Tick, world thread): reaps every placement of
        /// <paramref name="runId"/> that PuzzleGatePlacement.ShouldReap says is dead - its gate destroyed by a
        /// landblock reload or unload, or a spawn that never ran - so its host hears OnRemoved. The manager's own
        /// reap is lazy (it runs only on an admin command or an activation), which a run cannot wait for.
        /// </summary>
        public static int ReapForRun(uint runId, DateTime now)
        {
            if (runId == 0)
                return 0;

            var reaped = 0;

            foreach (var p in Placements.Values.ToList())
            {
                if (p.Host.RunId != runId)
                    continue;

                bool reap;

                lock (p.Sync)
                    reap = PuzzleGatePlacement.ShouldReap(p.State, p.GateMissing(), now - p.QueuedUtc, p.AwaitingSolveFinish);

                if (!reap)
                    continue;

                ClearPlacement(p, "reaped (run watchdog)", PuzzleRemovalReason.Reaped);
                reaped++;
            }

            return reaped;
        }

        /// <summary>
        /// Drops every ADMIN placement PuzzleGatePlacement.ShouldReap says is dead. Returns how many. Run placements
        /// are reaped by their run's watchdog (ReapForRun), never by an admin command.
        /// </summary>
        private static int Reap(DateTime now)
        {
            var reaped = 0;

            foreach (var p in AdminPlacements().ToList())
            {
                bool reap;

                lock (p.Sync)
                    reap = PuzzleGatePlacement.ShouldReap(p.State, p.GateMissing(), now - p.QueuedUtc, p.AwaitingSolveFinish);

                if (!reap)
                    continue;

                ClearPlacement(p, "reaped", PuzzleRemovalReason.Reaped);
                reaped++;
            }

            return reaped;
        }

        /// <summary>
        /// Is this a placement the /puzzlegate test tool manages? Only placements with no run (host RunId 0). A Thread
        /// run's puzzles are owned by the run - its seal, watchdog and EndRun - and the admin tool must not clear,
        /// reroll, reveal or list them: a "clear all" would unseal every live reward. This protects runs from the
        /// test tool; it is not an access-level exception (admins inside a run are ordinary players to it).
        /// </summary>
        internal static bool IsAdminManaged(PuzzleGatePlacement p) => p != null && p.Host.RunId == 0;

        private static IEnumerable<PuzzleGatePlacement> AdminPlacements() => Placements.Values.Where(IsAdminManaged);

        /// <summary>Test seam: put a placement in the registry without queueing a spawn (tests have no landblock).</summary>
        internal static void TrackForTest(PuzzleGatePlacement p) => Placements[p.Id] = p;

        /// <summary>Test seam: drop a placement from the registry without any side effect.</summary>
        internal static void UntrackForTest(PuzzleGatePlacement p) => Placements.TryRemove(new KeyValuePair<int, PuzzleGatePlacement>(p.Id, p));

        /// <summary>Test seam for the reshuffle-failure closure: what Reshuffle hands back once a round cannot spawn.</summary>
        internal static Action ReshuffleFailedForTest(PuzzleGatePlacement p, string report, bool wasSolved) => ReshuffleFailedFollowUp(p, report, wasSolved);

        // =============================================================================================
        // Helpers
        // =============================================================================================

        /// <summary>
        /// The admin's heading in the pure core's convention. Verified equal to the heading Position.InFrontOf
        /// uses (atan2(2wz, 1 - 2z^2), forward = (-sin, cos)) for any pure-yaw rotation, which is what a
        /// player's Location carries; PuzzleGateGenerator.YawDegFromQuaternion is the general form of the same
        /// formula. This is the ONE place the live layer converts a rotation to a yaw.
        /// </summary>
        public static float AnchorYawDeg(Position anchor) => PuzzleGateGenerator.YawDegFromQuaternion(anchor.Rotation);

        private static bool TryGetAdminLandblock(Player admin, out Landblock landblock, out string error)
        {
            landblock = admin?.CurrentLandblock;
            error = null;

            if (landblock == null || admin.Location == null)
            {
                error = "/puzzlegate needs you in the world.";
                return false;
            }

            if (landblock.Id.Landblock != admin.Location.LandblockId.Landblock || landblock.Instance != admin.Location.Instance)
            {
                error = "Your landblock is changing; try again in a moment.";
                return false;
            }

            return true;
        }

        /// <summary>Fails with a clear admin line when a wcid the placement needs has no weenie in the world database.</summary>
        private static bool CheckWeenies(PuzzleGateOptions options, PuzzleGateModel gateModel, out string error)
        {
            var needed = new List<(uint Wcid, string What)>
            {
                (gateModel.Wcid, gateModel.Form == PuzzleGateForm.Focal ? "puzzle focal object" : "Puzzle Gate"),
                (PuzzleGateTunables.LeverWcid, "Puzzle Lever"),
            };

            if (options.Type == PuzzleGateType.Sigil || options.Type == PuzzleGateType.Beam)
                needed.Add((PuzzleGateTunables.LightWcid, "Puzzle Light"));

            if (options.AmbushWcid != 0)
                needed.Add((options.AmbushWcid, "ambush creature"));

            var missing = needed.Where(n => DatabaseManager.World.GetCachedWeenie(n.Wcid) == null).ToList();

            if (missing.Count == 0)
            {
                error = null;
                return true;
            }

            error = "Missing weenie(s) in the world database: " + string.Join(", ", missing.Select(m => $"{m.What} ({m.Wcid})")) + ". Apply the puzzle-gate weenie SQL first.";
            return false;
        }

        private static List<Vector3> RecordedSpotsFor(uint adminGuid, Position anchor)
        {
            if (!Spots.TryGetValue(adminGuid, out var list))
                return new List<Vector3>();

            lock (list)
                return list.Where(s => s.InstancedLandblock == anchor.InstancedLandblock).Select(s => s.Pos).ToList();
        }

        /// <summary>The placement named by <paramref name="id"/>, or the nearest one within range in the admin's instance.</summary>
        private static bool TryFind(Player admin, int? id, out PuzzleGatePlacement placement, out string error)
        {
            error = null;

            if (id.HasValue)
            {
                if (Placements.TryGetValue(id.Value, out placement))
                {
                    if (IsAdminManaged(placement))
                        return true;

                    placement = null;
                    error = $"Puzzle gate #{id.Value} belongs to a Thread run; /puzzlegate does not manage run puzzles.";
                    return false;
                }

                error = $"No puzzle gate #{id.Value}. /puzzlegate list shows them.";
                return false;
            }

            placement = null;
            var best = float.MaxValue;
            var here = admin?.Location;

            if (here != null)
            {
                foreach (var p in AdminPlacements())
                {
                    if (p.Anchor.Instance != here.Instance)
                        continue;

                    var d = p.Anchor.DistanceTo(here);

                    if (d <= PuzzleGateTunables.NearestRange && d < best)
                    {
                        best = d;
                        placement = p;
                    }
                }
            }

            if (placement != null)
                return true;

            error = $"No puzzle gate within {PuzzleGateTunables.NearestRange:0} m. Give an id (/puzzlegate list).";
            return false;
        }

        private static int NewSeed() => Random.Shared.Next(1, int.MaxValue);

        private static string TypeName(PuzzleGateType type) => type.ToString().ToLowerInvariant();

        private static void Tell(Player player, string text)
        {
            player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        /// <summary>The admin host's report channel: the placing admin's chat. A placement nobody placed (PlacedBy 0) tells no one.</summary>
        internal static void TellPlacer(PuzzleGatePlacement p, string text)
        {
            if (p == null || p.PlacedBy == 0)
                return;

            var admin = PlayerManager.GetOnlinePlayer(p.PlacedBy);
            admin?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
