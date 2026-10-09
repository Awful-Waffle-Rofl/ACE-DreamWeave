using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Util;
using ACE.Server.Pvp.Templates;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The live <see cref="IPvpPlayerGateway"/>. Called on the world thread by the coordinator. Every change to a
    /// player is queued on that player's own action queue (DESIGN "Changes to players"), and every queued action
    /// carries its own try/catch, because an exception inside an EnqueueAction escapes the caller's try/catch.
    /// </summary>
    internal sealed partial class LivePvpPlayerGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(LivePvpPlayerGateway));

        private static Player Online(uint characterId) => PlayerManager.GetOnlinePlayer(characterId);

        public PvpPlayerFacts GetFacts(uint characterId)
        {
            var p = Online(characterId);

            if (p == null)
                return null;

            var fellows = p.Fellowship?.GetFellowshipMembers()?.Keys.ToList() ?? new List<uint>();

            return new PvpPlayerFacts(
                CharacterId: p.Guid.Full,
                Name: p.Name,
                Level: p.Level ?? 1,
                // Same-IP key: the session endpoint address (DESIGN "Rating" same-IP rules).
                IpKey: p.Session?.EndPointC2S?.Address?.ToString(),
                // "Already in any instance": an ephemeral instance, not merely a non-zero instance id (realm routing
                // gives every non-retail realm a non-zero instance even outside any ephemeral copy).
                InInstance: p.Location?.IsEphemeralRealm ?? false,
                InRespite: p.MinimumTimeSincePk != null,
                PkTimerActive: p.PKTimerActive,
                IsOlthoi: p.IsOlthoiPlayer,
                IsMule: p.IsMule,
                IsDead: p.IsDead,
                IsTeleporting: p.Teleporting,
                OnPkFacet: p.IsOnPkFacet,
                FellowshipMemberIds: fellows,
                PreferredTemplateKey: p.PvpTemplatePreference,
                IsTemplateAccount: IsOnTemplateAccount(p),
                IsPvpTemplated: p.IsPvpTemplated,
                // The allegiance monarch, for the battleground clanmate split (0 = no allegiance).
                MonarchId: p.MonarchId ?? 0);
        }

        /// <summary>PvP Template Facets: true when the player's account is pvp_template_account (compared by name, ignoring case).</summary>
        private static bool IsOnTemplateAccount(Player p)
        {
            var account = PvpTemplateSettings.AccountSource();

            return !string.IsNullOrWhiteSpace(account) && p.Account?.AccountName != null
                && string.Equals(p.Account.AccountName, account.Trim(), StringComparison.OrdinalIgnoreCase);
        }


        // ---------------- battlegrounds (IBattlegroundPlayerGateway) ----------------

        public BattlegroundZoneSample SampleZone(uint characterId, uint matchInstance)
        {
            var p = Online(characterId);

            if (p == null)
                return null;

            // Read the way GetPresence reads: a snapshot of fields the landblock thread writes. A torn read costs one
            // 5-second score tick at worst; the drain itself re-checks everything on the player's own queue.
            var location = p.Location;
            var inInstance = location != null && location.Instance == matchInstance;

            return new BattlegroundZoneSample(
                characterId,
                TeamIndex: 0,
                IpKey: null,
                InInstance: inInstance,
                IsDead: p.IsDead,
                IsTeleporting: p.Teleporting,
                X: inInstance ? location.PositionX : 0,
                Y: inInstance ? location.PositionY : 0,
                Z: inInstance ? location.PositionZ : 0);
        }

        public void DrainVitals(uint characterId, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal)
        {
            Online(characterId)?.DrainForBattlegroundZone(matchId, matchInstance, health, stamina, mana, lethal);
        }

        public void Respawn(uint characterId, Guid matchId, Position spawn)
        {
            var p = Online(characterId);

            if (p == null)
            {
                log.Info($"[PVP] 0x{characterId:X8} is offline at their respawn in match {matchId}");
                return;
            }

            p.RespawnInBattleground(matchId, spawn);
        }

        public bool IsInDeathProcess(uint characterId) => Online(characterId)?.IsInDeathProcess ?? false;

        public void ExitMatchFromPen(uint characterId, string context)
        {
            var p = Online(characterId);

            if (p == null)
            {
                log.Info($"[PVP] 0x{characterId:X8} is offline at the match exit from the pen ({context}); the PvpMatchReturnPkStatus login restore covers them");
                return;
            }

            p.ExitPvpMatch(context, returnFromPen: true);
        }

        public bool IsOnline(uint characterId) => Online(characterId) != null;

        public Position GetCurrentPosition(uint characterId)
        {
            var location = Online(characterId)?.Location;
            return location != null ? new Position(location) : null;
        }

        public PvpPresence GetPresence(uint characterId, uint matchInstance)
        {
            var p = Online(characterId);

            if (p == null)
                return PvpPresence.Offline;

            if (p.Teleporting)
                return PvpPresence.InTransit;

            var location = p.Location;

            if (location == null)
                return PvpPresence.InTransit;

            return location.Instance == matchInstance ? PvpPresence.InInstance : PvpPresence.Elsewhere;
        }

        public void EnterAndTeleport(uint characterId, PvpPlayerBinding binding, Position exitTo, Position spawn)
        {
            var p = Online(characterId);

            if (p == null)
            {
                log.Warn($"[PVP] 0x{characterId:X8} is offline; not placed in match {binding?.Match?.MatchId}");
                return;
            }

            // A mode that explicitly opted out of templates (PvpModeDefinition.Templated false; none does today) is
            // placed the pre-template way: EnterPvpMatch queues itself on the player's action queue and the teleport is
            // queued BEHIND it on the same queue, so the exit, the marker and PK Lite are in place before the player
            // leaves. Every other binding - arena and battleground alike - goes through the template entry sequence.
            if (binding?.Match != null && binding.Untemplated)
            {
                p.EnterPvpMatch(binding, exitTo);

                var plainDestination = new Position(spawn);

                p.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        WorldManager.ThreadSafeTeleport(p, plainDestination);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): the teleport into match {binding.Match.MatchId} threw", ex);
                    }
                }));

                return;
            }

            // PvP Template Facets (Docs/Pvp/TEMPLATES.md "Lifecycle"): every match is templated by default. The template is
            // applied on the player's queue first; only its successful completion callback - already on that queue -
            // binds the player (EnterPvpMatchNow: the exit, the 9075 marker, PK Lite) and teleports them. Nothing is
            // queued blindly behind the apply, because the apply can fail. A failure is reported back as an EntryFailed
            // intent.
            var destination = new Position(spawn);
            var exit = exitTo != null ? new Position(exitTo) : null;
            var matchId = binding?.Match?.MatchId ?? Guid.Empty;

            PvpTemplateEntrySequence.Run(
                binding,
                apply: (definition, id, onComplete) => p.ApplyPvpTemplate(definition, id, onComplete),
                enter: () =>
                {
                    p.EnterPvpMatchNow(binding, exit);
                    return p.PvpBinding?.Match?.MatchId == matchId;
                },
                teleport: () => WorldManager.ThreadSafeTeleport(p, destination),
                restore: reason => p.RestorePvpTemplate(reason),
                reportFailure: (reason, playerCaused, retryable) =>
                {
                    log.Warn($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): not placed in match {matchId}: {reason}{(retryable ? " (busy; the coordinator may retry)" : "")}");
                    PvpMatchManager.Report(PvpMatchManager.EntryFailed(characterId, matchId, DateTime.UtcNow, playerCaused, retryable));
                },
                logError: (what, ex) => log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): {what}", ex));
        }

        public void PublishBinding(uint characterId, PvpPlayerBinding binding)
        {
            var p = Online(characterId);

            if (p == null || p.ReplacePvpBinding(binding))
                return;

            // EnterPvpMatch has not run yet (it is queued on the player's action queue): retry behind it.
            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!p.ReplacePvpBinding(binding))
                        log.Warn($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): could not publish {binding.State} for match {binding.Match?.MatchId}; not bound to it");
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): publishing {binding.State} threw", ex);
                }
            }));
        }

        public void ExitMatch(uint characterId, string context)
        {
            var p = Online(characterId);

            if (p == null)
            {
                log.Info($"[PVP] 0x{characterId:X8} is offline at the match exit ({context}); the PvpMatchReturnPkStatus login restore covers them");
                return;
            }

            p.ExitPvpMatch(context);
        }

        public void ReturnToExit(uint characterId, Position exitTo, uint matchInstance)
        {
            var p = Online(characterId);

            if (p == null || exitTo == null)
                return;

            var destination = new Position(exitTo);

            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    // Only someone still in the arena (or on the way in) is moved. A player already returned by death,
                    // or who left the instance, is where they chose to be.
                    if (!p.Teleporting && p.Location?.Instance != matchInstance)
                        return;

                    p.SetPosition(PositionType.EphemeralRealmExitTo, null);
                    WorldManager.ThreadSafeTeleport(p, destination);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): the return teleport threw", ex);
                }
            }));
        }

        public void Send(uint characterId, string text)
        {
            Online(characterId)?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        public bool SendAcceptPrompt(uint characterId, Guid matchId, string text)
        {
            var p = Online(characterId);

            if (p?.ConfirmationManager == null || p.Session == null)
                return false;

            // ConfirmationManager holds one pending confirmation per type; a second Yes/No is refused and the caller
            // falls back to the /arena accept chat line.
            return p.ConfirmationManager.EnqueueSend(new Confirmation_PvpArenaAccept(p.Guid, matchId), text);
        }

        public void AbortAcceptPrompt(uint characterId, Guid matchId)
        {
            var p = Online(characterId);

            if (p?.ConfirmationManager == null || p.Session == null)
                return;

            if (p.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out var pending) && pending is Confirmation_PvpArenaAccept mine && mine.MatchId == matchId)
                p.ConfirmationManager.EnqueueAbort(ConfirmationType.Yes_No, pending.ContextId);
        }

        /// <summary>
        /// One dispatcher for the process: it holds every pending grant until exactly one path claims it (see
        /// PvpBloodGrantDispatcher for why a grant queued on the player alone can be lost).
        /// </summary>
        private static readonly PvpBloodGrantDispatcher bloodDispatcher = new PvpBloodGrantDispatcher(new LivePvpBloodGrantTargets());

        public void GrantArenaBlood(uint characterId, int amount, PvpBloodGrant grant) => bloodDispatcher.Grant(characterId, amount, grant);

        public string CheckTemplateRoom(uint characterId, PvpTemplateDefinition template)
        {
            var p = Online(characterId);

            return p == null ? null : p.CheckPvpTemplateRoom(template);
        }

        public void RunTemplateBackstop(uint characterId, Guid matchId)
        {
            var p = Online(characterId);

            if (p == null || p.IsLoggingOut)
                return;

            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    // Moved = personal items taken off and verified gone; Survivors = personal items still worn after
                    // the move (a full pack, or a move that did not take). The coordinator ejects on survivors.
                    var result = p.PvpTemplateRunEquippedBackstop();

                    if (result.Moved > 0 || result.Survivors > 0)
                        PvpMatchManager.Report(PvpMatchManager.BackstopFired(characterId, matchId, DateTime.UtcNow, result.Moved, result.Survivors));
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): the template equipped-item backstop threw", ex);
                }
            }));
        }
    }

    /// <summary>The live <see cref="IPvpBloodGrantTargets"/>: PlayerManager, the player's action queue, and WorldManager.ActionQueue.</summary>
    internal sealed class LivePvpBloodGrantTargets : IPvpBloodGrantTargets
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(LivePvpBloodGrantTargets));

        public bool IsOnline(uint characterId) => PlayerManager.GetOnlinePlayer(characterId) != null;

        public bool TryEnqueueOnPlayer(uint characterId, Action action)
        {
            var p = PlayerManager.GetOnlinePlayer(characterId);

            // A logging-out player's queue may never run again (no landblock, or LogOut_Final already under way).
            if (p == null || p.IsLoggingOut)
                return false;

            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {p.Name} (0x{p.Guid.Full:X8}): the queued arena Blood grant threw", ex);
                }
            }));

            return true;
        }

        public bool GrantOnline(uint characterId, int amount, PvpBloodGrant grant)
        {
            var p = PlayerManager.GetOnlinePlayer(characterId);

            if (p == null || p.IsLoggingOut)
                return false;

            p.GrantArenaBlood(amount, grant);
            return true;
        }

        public bool GrantOffline(uint characterId, int amount, PvpBloodGrant grant) => Player.GrantArenaBloodOffline(characterId, amount, grant);

        public void ScheduleOnWorld(double seconds, Action action)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(seconds);
            chain.AddAction(WorldManager.ActionQueue, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error("[PVP] the arena Blood grant fallback threw", ex);
                }
            });
            chain.EnqueueChain();
        }
    }

    /// <summary>
    /// The live <see cref="IPvpMatchSpaces"/>: resolves the online players and delegates to <see cref="EphemeralMatchSpaceProvider"/>.
    /// Also the live <see cref="IBattlegroundMatchSpaces"/>, which is what enables the battleground room.
    /// </summary>
    internal sealed class LivePvpMatchSpaces : IPvpMatchSpaces, IBattlegroundMatchSpaces, IObjectiveMatchSpaces
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(LivePvpMatchSpaces));

        private readonly EphemeralMatchSpaceProvider provider = new EphemeralMatchSpaceProvider();

        /// <summary>
        /// Queues the pen seals on the match instance landblock's OWN action queue (the ThreadDungeonRewardSpawner
        /// TrySummonExit pattern: create, set Location and TimeToRot -1 before EnterWorld, destroy on a refused entry).
        /// The handle must still be the live instance (the same reference check ResolveLive makes); otherwise the job
        /// comes back Failed at once. Visibility is left as the weenie defines it.
        /// </summary>
        public BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            => QueuePlacement(space, pieces, "fixture seals", bestEffort: false, (piece, instance, _) => PlaceSeal(piece, instance));

        /// <summary>
        /// Queues the King of the Hill zone markers exactly as <see cref="SpawnFixtures"/> queues the seals (same landblock
        /// queue, same live-instance check), but best effort: a marker that cannot be placed is skipped (see
        /// <see cref="PlaceZoneMarker"/>). Each one is transient and never rots, and dies with the instance.
        /// </summary>
        public BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => QueueZoneMarkers(space, pieces);

        /// <summary>The body of <see cref="SpawnZoneMarkers"/>, static so /arenaadmin testspace can place a ring into its own space.</summary>
        internal static BattlegroundFixtureJob QueueZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            => QueuePlacement(space, pieces, "zone markers", bestEffort: true, PlaceZoneMarker);

        /// <summary>
        /// Destroys the markers a <see cref="SpawnZoneMarkers"/> job placed, on the instance landblock's own queue behind that
        /// placement (the queue is FIFO), so it removes exactly what the job created. A handle that is no longer the live
        /// instance is a no-op: its markers went with it. Never throws.
        /// </summary>
        public void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed)
        {
            if (placed == null || space?.Handle is not Landblock landblock || !ReferenceEquals(LandblockManager.GetEphemeralLandblock(space.Instance), landblock))
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                var removed = 0;

                foreach (var o in placed.PlacedObjects.ToArray())
                {
                    try
                    {
                        (o as WorldObject)?.Destroy();
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"[PVP] removing a zone marker threw: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                placed.PlacedObjects.Clear();
                log.Info($"[PVP] zone markers on instance 0x{space.Instance:X8}: {removed} removed for the moved hill");
            }));
        }

        private static BattlegroundFixtureJob QueuePlacement(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces, string what, bool bestEffort, Func<BattlegroundSealPiece, uint, BattlegroundFixtureJob, string> place)
        {
            var job = new BattlegroundFixtureJob();

            if (space?.Handle is not Landblock landblock || !ReferenceEquals(LandblockManager.GetEphemeralLandblock(space.Instance), landblock))
            {
                job.Fail($"instance 0x{space?.Instance ?? 0:X8} is not the live match landblock");
                return job;
            }

            var instance = space.Instance;
            var list = pieces ?? Array.Empty<BattlegroundSealPiece>();

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                BattlegroundFixturePlacer.Run(list,
                    () => ReferenceEquals(LandblockManager.GetEphemeralLandblock(instance), landblock),
                    piece => place(piece, instance, job),
                    job,
                    bestEffort);

                if (job.Status == BattlegroundFixtureStatus.Failed)
                    log.Warn($"[PVP] {what} on instance 0x{instance:X8}: {job.Placed} of {list.Count} placed; {job.Detail}");
            }));

            return job;
        }

        /// <summary>
        /// One zone marker; null when it entered the world. The point is checked against the instance's own cells first
        /// (AdjustCell, the lookup WorldObject.AdjustDungeonCells uses) and refused with "outside every cell" when no
        /// cell holds it; otherwise the marker takes the cell that DOES hold it, which on a ring wider than the centre's
        /// cell is not always the planned one. Everything is set before EnterWorld: the instance-stamped position,
        /// TimeToRot -1 so the heartbeat never rots it mid-match, and IsTransientSpawn so no save path ever keeps it
        /// (belt and braces: Landblock.SaveDB already returns early for an ephemeral instance).
        /// </summary>
        private static string PlaceZoneMarker(BattlegroundSealPiece piece, uint instance, BattlegroundFixtureJob job)
        {
            var adjust = AdjustCell.Get(piece.CellId >> 16, instance);
            var cell = ResolveMarkerCell(point => adjust.GetCell(point), new Vector3(piece.X, piece.Y, piece.Z));

            if (cell == null)
                return $"wcid {piece.Wcid} at ({piece.X:0.00}, {piece.Y:0.00}, {piece.Z:0.000}) is outside every cell";

            var wo = WorldObjectFactory.CreateNewWorldObject(piece.Wcid);

            if (wo == null)
                return $"wcid {piece.Wcid} did not create";

            wo.Location = new Position(cell.Value, piece.X, piece.Y, piece.Z, 0f, 0f, piece.RotationZ, piece.RotationW, instance);
            wo.TimeToRot = -1;
            wo.IsTransientSpawn = true;

            if (!wo.EnterWorld())
            {
                var where = wo.Location?.ToLOCString();
                wo.Destroy();
                return $"wcid {piece.Wcid} failed to enter the world at {where}";
            }

            job.PlacedObjects.Add(wo);

            return null;
        }

        /// <summary>How far above a marker's point the cell lookup climbs, and in what steps (see <see cref="ResolveMarkerCell"/>).</summary>
        internal const float MarkerCellSearchStep = 0.25f;

        internal const int MarkerCellSearchSteps = 6;

        /// <summary>
        /// The cell that holds a zone marker's column. A cell's volume starts at its floor, so a marker sunk below the
        /// floor by pvp_bg_koth_marker_z_offset is in NO cell at its own point (on bg_016c at z 0.005: inside at 0.005,
        /// outside at -0.3, measured with `cells --spawncheck`). The point is tried first, then raised in
        /// <see cref="MarkerCellSearchStep"/> steps up to <see cref="MarkerCellSearchSteps"/> times, and the first cell
        /// that holds it is the marker's cell. Null when none does. <paramref name="lookup"/> is AdjustCell.GetCell.
        /// </summary>
        internal static uint? ResolveMarkerCell(Func<Vector3, uint?> lookup, Vector3 point)
        {
            for (var i = 0; i <= MarkerCellSearchSteps; i++)
            {
                var cell = lookup(new Vector3(point.X, point.Y, point.Z + i * MarkerCellSearchStep));

                if (cell != null)
                    return cell;
            }

            return null;
        }

        /// <summary>
        /// Queues the start gates exactly as <see cref="SpawnFixtures"/> queues the pen seals (strict), but records each placed
        /// object so <see cref="RemoveStartGates"/> can take them down at Live.
        /// </summary>
        public BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            => QueuePlacement(space, pieces, "start gates", bestEffort: false, PlaceGate);

        /// <summary>Destroys the gates a <see cref="SpawnStartGates"/> job placed; same queue-behind-the-placement shape as <see cref="RemoveZoneMarkers"/>.</summary>
        public void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed)
        {
            if (placed == null || space?.Handle is not Landblock landblock || !ReferenceEquals(LandblockManager.GetEphemeralLandblock(space.Instance), landblock))
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                var removed = 0;

                foreach (var o in placed.PlacedObjects.ToArray())
                {
                    try
                    {
                        (o as WorldObject)?.Destroy();
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"[PVP] removing a start gate threw: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                placed.PlacedObjects.Clear();
                log.Info($"[PVP] start gates on instance 0x{space.Instance:X8}: {removed} removed");
            }));
        }

        /// <summary>One start gate piece (a pen-seal placement that also records the object); null when it entered the world.</summary>
        private static string PlaceGate(BattlegroundSealPiece piece, uint instance, BattlegroundFixtureJob job)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(piece.Wcid);

            if (wo == null)
                return $"wcid {piece.Wcid} did not create";

            wo.Location = new Position(piece.CellId, piece.X, piece.Y, piece.Z, 0f, 0f, piece.RotationZ, piece.RotationW, instance);
            wo.TimeToRot = -1;
            wo.IsTransientSpawn = true;

            if (!wo.EnterWorld())
            {
                var where = wo.Location?.ToLOCString();
                wo.Destroy();
                return $"wcid {piece.Wcid} failed to enter the world at {where}";
            }

            job.PlacedObjects.Add(wo);

            return null;
        }

        /// <summary>One seal piece; null when it entered the world.</summary>
        private static string PlaceSeal(BattlegroundSealPiece piece, uint instance)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(piece.Wcid);

            if (wo == null)
                return $"wcid {piece.Wcid} did not create";

            // Everything before EnterWorld: the instance-stamped position (pure yaw), and TimeToRot -1 so the landblock
            // heartbeat never rots the seal mid-match.
            wo.Location = new Position(piece.CellId, piece.X, piece.Y, piece.Z, 0f, 0f, piece.RotationZ, piece.RotationW, instance);
            wo.TimeToRot = -1;

            if (!wo.EnterWorld())
            {
                var where = wo.Location?.ToLOCString();
                wo.Destroy();
                return $"wcid {piece.Wcid} failed to enter the world at {where}";
            }

            return null;
        }

        // ---------------- Attack/Defend crystals (IObjectiveMatchSpaces) ----------------

        public BattlegroundFixtureJob SpawnObjectives(MatchSpace space, uint wcid, AttackDefendPlan plan, Func<PlannedCrystal, BattlegroundObjectiveTag> tagFactory)
            => QueueObjectives(space, wcid, plan, tagFactory);

        /// <summary>
        /// The body of <see cref="SpawnObjectives"/>, static so /arenaadmin testspace can place crystals into its own space. Each planned
        /// crystal becomes one piece at its site (full cell id from the space's landblock, pure yaw 0) and goes through the same strict
        /// placement as the pen seals (<see cref="QueuePlacement"/>), with <see cref="PlaceObjective"/> doing the create.
        /// </summary>
        internal static BattlegroundFixtureJob QueueObjectives(MatchSpace space, uint wcid, AttackDefendPlan plan, Func<PlannedCrystal, BattlegroundObjectiveTag> tagFactory)
        {
            var crystalOf = new Dictionary<BattlegroundSealPiece, PlannedCrystal>(ReferenceEqualityComparer.Instance);
            var pieces = new List<BattlegroundSealPiece>();

            foreach (var crystal in plan?.Crystals ?? Array.Empty<PlannedCrystal>())
            {
                var piece = new BattlegroundSealPiece(wcid, ((space?.LandblockId ?? 0) << 16) | crystal.Site.CellLow, crystal.Site.X, crystal.Site.Y, crystal.Site.Z, 1f, 0f);
                crystalOf[piece] = crystal;
                pieces.Add(piece);
            }

            return QueuePlacement(space, pieces, "crystals", bestEffort: false, (piece, instance, job) => PlaceObjective(piece, instance, job, crystalOf[piece], tagFactory));
        }

        /// <summary>
        /// One crystal; null when it entered the world. Everything is set before EnterWorld: the match tag (write-once), the plan's
        /// health as both maximum and current (through WorldEventSpawner.RebasedStartingValue, the same rebase the event bosses use),
        /// the instance-stamped position, TimeToRot -1 and IsTransientSpawn. Anything that is not a Creature, or a tag that will not
        /// set, is refused, because an untagged crystal would leave a corpse and answer to the retail PK rules.
        /// </summary>
        private static string PlaceObjective(BattlegroundSealPiece piece, uint instance, BattlegroundFixtureJob job, PlannedCrystal crystal, Func<PlannedCrystal, BattlegroundObjectiveTag> tagFactory)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(piece.Wcid);

            if (wo == null)
                return $"wcid {piece.Wcid} did not create";

            if (wo is not Creature creature)
            {
                wo.Destroy();
                return $"wcid {piece.Wcid} is not a Creature";
            }

            var tag = tagFactory?.Invoke(crystal);

            if (tag == null || !creature.SetBattlegroundObjective(tag))
            {
                creature.Destroy();
                return $"crystal {crystal.Index} ({crystal.Site.Name}) has no match tag";
            }

            var health = (uint)Math.Max(1, crystal.MaxHealth);
            creature.Health.StartingValue = WorldEvents.WorldEventSpawner.RebasedStartingValue(creature.Health.StartingValue, creature.Health.MaxValue, health);
            creature.Health.Current = creature.Health.MaxValue;

            if (creature.Health.MaxValue != health)
                log.Warn($"[PVP] crystal {crystal.Index} ({crystal.Site.Name}): planned health {health}, the weenie's fixed health part leaves {creature.Health.MaxValue}");

            creature.Location = new Position(piece.CellId, piece.X, piece.Y, piece.Z, 0f, 0f, piece.RotationZ, piece.RotationW, instance);
            creature.TimeToRot = -1;
            creature.IsTransientSpawn = true;

            if (!creature.EnterWorld())
            {
                var where = creature.Location?.ToLOCString();
                creature.Destroy();
                return $"crystal {crystal.Index} ({crystal.Site.Name}, wcid {piece.Wcid}) failed to enter the world at {where}";
            }

            job.PlacedObjects.Add(creature);

            return null;
        }

        /// <summary>
        /// Reads each placed crystal's health. Only once the job is Placed: the status is published with a volatile write AFTER the
        /// landblock thread finished adding to PlacedObjects, and nothing adds to it afterwards, so the list itself is safe to walk
        /// here. The health fields are the landblock thread's and may be read torn; the sample is cosmetic (the under-attack alerts).
        /// </summary>
        public IReadOnlyList<CrystalHealth> SampleObjectives(MatchSpace space, BattlegroundFixtureJob job)
        {
            if (job == null || job.Status != BattlegroundFixtureStatus.Placed)
                return Array.Empty<CrystalHealth>();

            var result = new List<CrystalHealth>(job.PlacedObjects.Count);

            foreach (var o in job.PlacedObjects)
            {
                try
                {
                    if (o is not Creature c || c.BattlegroundObjective == null || c.IsDead)
                        continue;

                    var current = (int)Math.Min(int.MaxValue, c.Health.Current);
                    var max = (int)Math.Min(int.MaxValue, c.Health.MaxValue);

                    if (current > 0)
                        result.Add(new CrystalHealth(c.BattlegroundObjective.Index, current, max));
                }
                catch (Exception ex)
                {
                    log.Debug($"[PVP] sampling a crystal on instance 0x{space?.Instance ?? 0:X8} threw (ignored, cosmetic): {ex.GetType().Name}: {ex.Message}");
                }
            }

            return result;
        }

        public void ScaleObjectives(MatchSpace space, BattlegroundFixtureJob job, int sizedForAttackers, int currentAttackers)
        {
            if (job == null || space?.Handle is not Landblock landblock || !ReferenceEquals(LandblockManager.GetEphemeralLandblock(space.Instance), landblock))
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    // On the landblock's own queue: no hit can land between the read and the write below.
                    var standing = job.PlacedObjects.OfType<Creature>().Where(c => c.BattlegroundObjective != null && !c.IsDead && c.Health.Current > 0).ToList();
                    var before = standing.Select(c => new CrystalHealth(c.BattlegroundObjective.Index, (int)c.Health.Current, (int)c.Health.MaxValue)).ToList();
                    var after = CrystalHealthScaler.Scale(before, sizedForAttackers, currentAttackers);

                    for (var i = 0; i < standing.Count; i++)
                        ApplyObjectiveHealth(standing[i], after[i]);

                    log.Info($"[PVP] crystals on instance 0x{space.Instance:X8} rescaled from {sizedForAttackers} to {currentAttackers} attacker(s): {string.Join("; ", before.Select((b, i) => $"#{b.Index} {b.Current}/{b.Max} -> {after[i].Current}/{after[i].Max}"))}");
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] rescaling the crystals on instance 0x{space.Instance:X8} threw", ex);
                }
            }));
        }

        public void AdjustObjective(MatchSpace space, BattlegroundFixtureJob job, CrystalKillEffect effect, uint killerId)
        {
            if (job == null || space?.Handle is not Landblock landblock || !ReferenceEquals(LandblockManager.GetEphemeralLandblock(space.Instance), landblock))
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    // On the landblock's own queue, like ScaleObjectives: no hit can land between the read and the write below.
                    var crystal = job.PlacedObjects.OfType<Creature>().FirstOrDefault(c => c.BattlegroundObjective != null && c.BattlegroundObjective.Index == effect.CrystalIndex);

                    var line = ApplyKillEffect(crystal, effect, PlayerManager.GetOnlinePlayer(killerId));

                    if (line != null)
                        log.Info($"[PVP] crystal {effect.CrystalIndex} on instance 0x{space.Instance:X8}: kill by 0x{killerId:X8}: {line}");
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] the kill {(effect.IsChip ? "chip" : "heal")} on crystal {effect.CrystalIndex} on instance 0x{space.Instance:X8} threw", ex);
                }
            }));
        }

        /// <summary>
        /// The body of <see cref="AdjustObjective"/> for one crystal, on its landblock thread. Returns a log line, or null when nothing
        /// changed: no crystal, an untagged creature, a crystal already dead or at 0, or an amount of 0. A chip goes through the crystal's
        /// normal damage path, Creature.TakeDamage, with <paramref name="killer"/> as the source (null when the killer is offline), so the
        /// killer is the last damager and a chip to 0 runs OnDeath and Die, which report the destruction to the coordinator once
        /// (Creature_BattlegroundObjective). There is deliberately no second destruction path here. A heal is a plain vital delta, which
        /// UpdateVital clamps to the maximum.
        /// </summary>
        internal static string ApplyKillEffect(Creature crystal, CrystalKillEffect effect, WorldObject killer)
        {
            if (crystal?.BattlegroundObjective == null || crystal.IsDead || crystal.Health.Current == 0)
                return null;

            var before = crystal.Health.Current;
            var amount = CrystalKillRules.Amount((int)Math.Min(int.MaxValue, crystal.Health.MaxValue), effect.Percent);

            if (amount <= 0)
                return null;

            if (effect.IsChip)
            {
                // The chip is a percent of the crystal's maximum, not an attacker's hit: the engaged-defender reduction in TakeDamage must
                // leave it whole (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"). Set and cleared on this landblock thread around the one call.
                crystal.BattlegroundKillChipActive = true;

                try
                {
                    crystal.TakeDamage(killer, DamageType.Undef, amount);
                }
                finally
                {
                    crystal.BattlegroundKillChipActive = false;
                }
            }
            else
                crystal.UpdateVitalDelta(crystal.Health, amount);

            return $"{(effect.IsChip ? "chip" : "heal")} {effect.Percent}% = {amount}: {before} -> {crystal.Health.Current} of {crystal.Health.MaxValue}";
        }

        /// <summary>Sets a crystal's maximum (by rebasing its starting value) and then its current health. Landblock thread only.</summary>
        private static void ApplyObjectiveHealth(Creature crystal, CrystalHealth health)
        {
            crystal.Health.StartingValue = WorldEvents.WorldEventSpawner.RebasedStartingValue(crystal.Health.StartingValue, crystal.Health.MaxValue, (uint)Math.Max(1, health.Max));
            crystal.UpdateVital(crystal.Health, (uint)Math.Max(0, health.Current));
        }

        public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> characterIds)
        {
            var admitted = new List<Player>();

            foreach (var id in characterIds ?? Array.Empty<uint>())
            {
                var p = PlayerManager.GetOnlinePlayer(id);

                if (p == null)
                    return MatchSpaceAllocation.Fail(MatchSpaceFailure.NullParticipant, $"0x{id:X8} is offline");

                admitted.Add(p);
            }

            return provider.Allocate(map, admitted);
        }

        public MatchSpaceReadiness GetReadiness(MatchSpace space) => provider.GetReadiness(space);

        public void Release(MatchSpace space) => provider.Release(space);
    }

    /// <summary>The live <see cref="IPvpResultSink"/>: the SerializedShardDatabase wrappers from PR B.</summary>
    internal sealed class ShardPvpResultSink : IPvpResultSink
    {
        public void LoadRatings(Action<List<PvpRatingRecord>> callback) => DatabaseManager.Shard.GetAllPvpRatings(callback);

        public void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback)
            => DatabaseManager.Shard.SavePvpMatchResult(match, participants, ratingUpserts, callback);

        public void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback) => DatabaseManager.Shard.GetAllPvpTemplates(callback);

        public void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback)
            => DatabaseManager.Shard.SetPvpMatchParticipantTemplates(dbMatchId, stamps, callback);

        public void LoadLatestParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback)
            => DatabaseManager.Shard.GetLatestPvpParticipantTemplates(callback);
    }

    /// <summary>
    /// The live <see cref="IPvpArenaCrierAnnouncer"/>: the MarketAdvertiser pattern (one GameMessageTurbineChat per
    /// line, built once and reused across recipients) applied to TurbineChatChannel.LFG / ChatType.LFG, filtered on
    /// CharacterOption.ListenToLFGChat and skipping Olthoi. Gated on chat_disable_lfg and use_turbine_chat, exactly
    /// as BroadcastToTradeChannel is gated on chat_disable_trade.
    /// </summary>
    internal sealed class LivePvpArenaCrierAnnouncer : IPvpArenaCrierAnnouncer
    {
        public void Announce(IReadOnlyList<string> lines, string senderName)
        {
            if (lines == null || lines.Count == 0)
                return;

            if (PropertyManager.GetBool("chat_disable_lfg").Item || !PropertyManager.GetBool("use_turbine_chat").Item)
                return;

            var clampedSender = ACE.Server.Managers.Market.MarketAdvertiser.ClampSenderName(senderName);

            var messages = new GameMessageTurbineChat[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                messages[i] = new GameMessageTurbineChat(
                    ChatNetworkBlobType.NETBLOB_EVENT_BINARY,
                    ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                    TurbineChatChannel.LFG,
                    clampedSender,
                    lines[i],
                    0,
                    ChatType.LFG);
            }

            foreach (var recipient in PlayerManager.GetAllOnline())
            {
                if (recipient == null || recipient.Session?.Network == null)
                    continue;

                if (!recipient.GetCharacterOption(CharacterOption.ListenToLFGChat) || recipient.IsOlthoiPlayer)
                    continue;

                foreach (var message in messages)
                    recipient.Session.Network.EnqueueSend(message);
            }
        }
    }
}
