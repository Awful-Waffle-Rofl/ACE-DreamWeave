using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// PvP arena admin commands (Docs/Pvp/DESIGN.md "Commands"). AccessLevel.Admin, matching the precedent of the
    /// other operator tools that mutate live server state rather than only read it (WorldEventCommands' "worldevent",
    /// ProvingGroundsAdminCommands' "resetleaderboard"), unlike the read-only "marketadmin" (Sentinel).
    /// </summary>
    public static class PvpArenaAdminCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string AdminUsage =
            "[ArenaAdmin] Usage: /arenaadmin <list|cancel <id>|clearqueue <1v1|2v2|tugak|bg|all>|testspace <arena_0066|arena_0067|bg_016c|bg_003c>>";

        [CommandHandler("arenaadmin", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "PvP arena admin: inspect and manage live matches.",
            "list                                  - every live match, with its short id, mode, map, state, participants and elapsed time\n" +
            "cancel <id>                           - cancel a live match (unrated once Countdown/Live); accepts an unambiguous short id prefix\n" +
            "clearqueue <1v1|2v2|tugak|bg|all>        - empty a queue (or every queue)\n" +
            "testspace <arena_0066|arena_0067|bg_016c|bg_003c> - allocate a match space for yourself alone and teleport in, for a live map check")]
        public static void HandleArenaAdmin(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            if (parameters == null || parameters.Length == 0)
            {
                Msg(player, AdminUsage);
                return;
            }

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToArray();

            switch (sub)
            {
                case "list":
                    HandleList(player);
                    break;

                case "cancel":
                    HandleCancel(player, rest);
                    break;

                case "clearqueue":
                    HandleClearQueue(player, rest);
                    break;

                case "testspace":
                    HandleTestSpace(player, rest);
                    break;

                default:
                    Msg(player, AdminUsage);
                    break;
            }
        }

        // ---- list ----

        private static void HandleList(Player player)
        {
            var matches = PvpMatchManager.ListMatches();

            if (matches.Count == 0)
            {
                Msg(player, "[ArenaAdmin] No live matches.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[ArenaAdmin] Live matches:");

            foreach (var m in matches.OrderBy(m => m.Age))
                sb.AppendLine(FormatMatchLine(m));

            Msg(player, sb.ToString().TrimEnd());
        }

        /// <summary>One /arenaadmin list row. Pure.</summary>
        internal static string FormatMatchLine(PvpMatchSummary m)
        {
            var map = m.MapName ?? m.MapKey ?? "-";
            var participants = m.ParticipantNames != null && m.ParticipantNames.Count > 0
                ? string.Join(", ", m.ParticipantNames)
                : "-";

            return $"  {ShortId(m.MatchId)}  {ArenaMapCatalog.JoinWord(m.ModeKey)}  {map}  {m.State}  [{participants}]  {FormatElapsed(m.Age)}";
        }

        /// <summary>mm:ss elapsed, for the admin list/cancel output. Pure.</summary>
        internal static string FormatElapsed(TimeSpan age)
        {
            var totalSeconds = (long)Math.Max(0, age.TotalSeconds);
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;

            return $"{minutes}:{seconds:D2}";
        }

        /// <summary>
        /// The short form of a match id used in admin output and /arenaadmin cancel: the first 8 hex characters
        /// of the guid's "N" form. Pure.
        /// </summary>
        internal static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);

        internal enum ShortIdMatchOutcome
        {
            Ok,
            NotFound,
            Ambiguous
        }

        /// <summary>
        /// Matches an admin-typed prefix against every live match's <see cref="ShortId"/> (and, so a full guid
        /// always works too, its full "N" form), case-insensitively. Pure.
        /// </summary>
        internal static (ShortIdMatchOutcome Outcome, Guid MatchId) MatchShortId(IReadOnlyList<Guid> ids, string prefix)
        {
            if (ids == null || string.IsNullOrWhiteSpace(prefix))
                return (ShortIdMatchOutcome.NotFound, default);

            var trimmed = prefix.Trim();

            var matches = ids
                .Where(id => ShortId(id).StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                    || id.ToString("N").StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (matches.Count == 0)
                return (ShortIdMatchOutcome.NotFound, default);

            if (matches.Count > 1)
                return (ShortIdMatchOutcome.Ambiguous, default);

            return (ShortIdMatchOutcome.Ok, matches[0]);
        }

        // ---- cancel ----

        private static void HandleCancel(Player player, string[] args)
        {
            if (args.Length != 1)
            {
                Msg(player, "[ArenaAdmin] Usage: /arenaadmin cancel <id>");
                return;
            }

            var ids = PvpMatchManager.ListMatches().Select(m => m.MatchId).ToList();
            var (outcome, matchId) = MatchShortId(ids, args[0]);

            switch (outcome)
            {
                case ShortIdMatchOutcome.NotFound:
                    Msg(player, $"[ArenaAdmin] No live match matches '{args[0]}'.");
                    return;

                case ShortIdMatchOutcome.Ambiguous:
                    Msg(player, $"[ArenaAdmin] '{args[0]}' matches more than one live match. Use more characters.");
                    return;
            }

            var result = PvpMatchManager.Cancel(matchId);

            Msg(player, result.Outcome == PvpCancelOutcome.Canceled
                ? $"[ArenaAdmin] Match {ShortId(matchId)} canceled ({result.State})."
                : $"[ArenaAdmin] Match {ShortId(matchId)} was not found (it may have just ended).");
        }

        // ---- clearqueue ----

        internal static readonly string[] ClearableModes = { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.FfaKey, BattlegroundModes.RoomKey };

        private static void HandleClearQueue(Player player, string[] args)
        {
            if (args.Length != 1)
            {
                Msg(player, "[ArenaAdmin] Usage: /arenaadmin clearqueue <1v1|2v2|tugak|bg|all>");
                return;
            }

            var arg = ArenaMapCatalog.CanonicalModeWord(args[0]);

            if (arg == "all")
            {
                var total = 0;

                foreach (var mode in ClearableModes)
                    total += PvpMatchManager.ClearQueue(mode);

                Msg(player, $"[ArenaAdmin] Cleared {total} player(s) from all arena queues.");
                return;
            }

            if (!ClearableModes.Contains(arg))
            {
                Msg(player, "[ArenaAdmin] Usage: /arenaadmin clearqueue <1v1|2v2|tugak|bg|all>");
                return;
            }

            var count = PvpMatchManager.ClearQueue(arg);
            Msg(player, $"[ArenaAdmin] Cleared {count} player(s) from the {PvpArenaText.ModeLabel(arg)} queue.");
        }

        // ---- testspace ----

        /// <summary>How many 1-second polls testspace waits for the admin's own arrival before releasing anyway.</summary>
        internal const int TestSpaceReleaseAttempts = 20;

        private static void HandleTestSpace(Player player, string[] args)
        {
            if (args.Length != 1)
            {
                Msg(player, "[ArenaAdmin] Usage: /arenaadmin testspace <arena_0066|arena_0067|bg_016c|bg_003c>");
                return;
            }

            var map = FindTestSpaceMap(args[0].Trim());

            if (map == null)
            {
                Msg(player, $"[ArenaAdmin] Unknown map '{args[0]}'. Use arena_0066, arena_0067, bg_016c or bg_003c.");
                return;
            }

            var provider = new EphemeralMatchSpaceProvider();
            var allocation = provider.Allocate(map, new[] { player });

            if (!allocation.Succeeded)
            {
                log.Warn($"[PVP] testspace: allocating map {map.MapKey} for {player.Name} failed: {allocation.Failure} - {allocation.Detail}");
                Msg(player, $"[ArenaAdmin] Could not allocate {map.MapKey}: {allocation.Failure}.");
                return;
            }

            var space = allocation.Space;

            // FFA F1: a ring point, so it seats cleanly regardless of which arena mode the owner wants to eyeball.
            // Read from the map's own FFA spawn set (SpawnPointsFor), not the global ArenaMapCatalog.FfaSet, so a
            // future map with its own geometry (and its own set) can never throw here.
            var spawn = TestSpaceSpawn(map);

            if (spawn == null)
            {
                log.Warn($"[PVP] testspace: map {map.MapKey} has no FFA spawn points; releasing the space.");
                provider.Release(space);
                Msg(player, $"[ArenaAdmin] {map.MapKey} has no spawn points to use.");
                return;
            }

            try
            {
                var destination = ArenaSpawnPosition.Build(map, spawn, space.Instance);

                player.Teleport(destination);

                Msg(player, $"[ArenaAdmin] Teleporting you to {map.DisplayName} ({map.MapKey}), instance 0x{space.Instance:X8}. " +
                    "The space is released once you leave it.");

                // Release hands the space to the same "watch until empty" destruction path Threads uses
                // (EphemeralMatchSpaceProvider.Release / ThreadDungeonManager.QueueCopyForDestructionWhenEmpty): called
                // while occupied, it WATCHES the instance and queues it for destruction the first tick it is found
                // empty, rather than checking once. It must not be called before the admin has actually arrived,
                // because Release recomputes occupancy itself right then - calling it immediately after Teleport
                // (which only dispatches the arrival, on the player's own action queue) would see nobody there yet and
                // queue the copy for IMMEDIATE destruction out from under the admin. So this polls CurrentLandblock
                // for the admin's own arrival first, for up to TestSpaceReleaseAttempts seconds, and calls Release
                // exactly once, from whichever of the two ends the wait.
                ScheduleTestSpaceRelease(player, provider, space, space.Instance, TestSpaceReleaseAttempts);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] testspace: teleporting {player.Name} into instance 0x{space.Instance:X8} threw", ex);
                provider.Release(space);
                Msg(player, $"[ArenaAdmin] Could not teleport you into {map.MapKey}; the space was released.");
            }
        }

        /// <summary>
        /// The map testspace opens for a key: an arena map from <see cref="ArenaMapCatalog"/>, else a battleground map
        /// bridged to an <see cref="ArenaMap"/> by <see cref="BattlegroundMapCatalog.SpaceMapFor"/> (the same bridge the
        /// coordinator allocates battleground matches through). Null for an unknown key. Pure.
        /// </summary>
        internal static ArenaMap FindTestSpaceMap(string mapKey)
        {
            var arena = ArenaMapCatalog.Find(mapKey);

            if (arena != null)
                return arena;

            return BattlegroundMapCatalog.SpaceMaps.FirstOrDefault(m => string.Equals(m.MapKey, mapKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The testspace teleport point: the FFA set's "F1" ring point, or its first point if "F1" is absent, read
        /// from the MAP's own spawn sets (never the global ArenaMapCatalog.FfaSet) so a future map with its own
        /// geometry and its own FFA set can never throw here. A battleground map has no FFA set; its one battleground
        /// set (every team spawn, then both pens) is used instead, so the first point is a west team spawn. Null only
        /// if the map has neither. Pure.
        /// </summary>
        internal static PvpSpawnPoint TestSpaceSpawn(ArenaMap map)
        {
            var set = map?.SpawnPointsFor(ArenaMapCatalog.FfaKey);

            if (set == null || set.Count == 0)
                set = map?.SpawnPointsFor(BattlegroundModes.RoomKey);

            if (set == null || set.Count == 0)
                return null;

            foreach (var point in set)
            {
                if (point?.Label == "F1")
                    return point;
            }

            return set[0];
        }

        /// <summary>See the release-timing comment in <see cref="HandleTestSpace"/>. World thread, via ActionChain.</summary>
        private static void ScheduleTestSpaceRelease(Player admin, EphemeralMatchSpaceProvider provider, MatchSpace space, uint instance, int attemptsLeft)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.0);
            chain.AddAction(admin, () =>
            {
                try
                {
                    if (admin.CurrentLandblock != null && admin.CurrentLandblock.Instance == instance)
                    {
                        // The admin is in, so the instance is loaded. Release first: it only watches the instance until it
                        // empties, so the ring still goes in after it, and nothing the ring does can stop the release.
                        provider.Release(space);

                        // King of the Hill ring for a solo visual check; its own try so a failure is logged and swallowed.
                        try
                        {
                            PlaceTestSpaceZoneMarkers(admin, space);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[PVP] testspace: placing the zone markers in instance 0x{instance:X8} threw", ex);
                        }

                        // Attack/Defend crystals for a solo visual check; likewise its own try.
                        try
                        {
                            PlaceTestSpaceCrystals(admin, space);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[PVP] testspace: placing the crystals in instance 0x{instance:X8} threw", ex);
                        }

                        return;
                    }

                    if (attemptsLeft <= 1)
                    {
                        log.Warn($"[PVP] testspace: {admin.Name} never confirmed arrival in instance 0x{instance:X8}; releasing anyway.");
                        provider.Release(space);
                        return;
                    }

                    ScheduleTestSpaceRelease(admin, provider, space, instance, attemptsLeft - 1);
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] testspace: releasing instance 0x{instance:X8} threw", ex);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// testspace on a battleground map: places the King of the Hill zone-marker ring with the CURRENT pvp_bg_* dials,
        /// exactly as a match plans it (BattlegroundModes.KothZoneFor + BattlegroundZoneMarkers.Plan), best effort. A
        /// no-op for an arena map; tells the admin when the setting is off or the map has no marker wcid yet.
        /// </summary>
        private static void PlaceTestSpaceZoneMarkers(Player admin, MatchSpace space)
        {
            var layout = BattlegroundMapCatalog.Find(space.MapKey);

            // A map that never hosts King of the Hill (the Attack/Defend mines) gets no ring and no message about one.
            if (layout == null || !layout.Modes.Contains(BattlegroundModes.KothModeKey))
                return;

            var d = BattlegroundTunables.DialSource();

            if (!d.KothMarkersEnabled)
            {
                Msg(admin, "[ArenaAdmin] pvp_bg_koth_markers_enabled is off; no zone markers placed.");
                return;
            }

            var pieces = BattlegroundZoneMarkers.Plan(layout, BattlegroundModes.KothZoneFor(layout, d), d.KothMarkerSpacing, layout.ZoneMarkerWcid, d.KothMarkerZOffset);

            if (pieces.Count == 0)
            {
                Msg(admin, $"[ArenaAdmin] {layout.MapKey} has no zone-marker wcid yet (or the zone radius is not positive); no zone markers placed.");
                return;
            }

            var job = LivePvpMatchSpaces.QueueZoneMarkers(space, pieces);

            log.Info($"[PVP] testspace: {pieces.Count} zone marker(s) queued on instance 0x{space.Instance:X8} for {admin.Name} (status {job.Status}{(job.Detail != null ? "; " + job.Detail : "")})");
            Msg(admin, $"[ArenaAdmin] Queued {pieces.Count} zone markers (radius {d.KothZoneRadius:0.##} m, spacing {d.KothMarkerSpacing:0.##} m, z offset {d.KothMarkerZOffset:0.##} m).");
        }

        /// <summary>
        /// testspace on an Attack/Defend map: places the crystals with the CURRENT pvp_bg_* dials, exactly as a match plans them
        /// (<see cref="TestSpaceCrystalPlan"/>), through the same strict placement a match uses. Each is tagged with a fresh match id
        /// that no player is ever bound to, so the damage gate refuses every hit and the crystals stand for as long as the instance
        /// does. Log only. A no-op for a map with no crystal sites or no crystal wcid.
        /// </summary>
        private static void PlaceTestSpaceCrystals(Player admin, MatchSpace space)
        {
            var layout = BattlegroundMapCatalog.Find(space.MapKey);
            var plan = TestSpaceCrystalPlan(layout, BattlegroundTunables.DialSource());

            if (plan == null)
                return;

            var testMatch = Guid.NewGuid();
            var job = LivePvpMatchSpaces.QueueObjectives(space, layout.CrystalWcid, plan, crystal => new BattlegroundObjectiveTag(testMatch, crystal.Index, CrystalWinCondition.DefenderTeam));

            log.Info($"[PVP] testspace: {plan.Count} crystal(s) (wcid {layout.CrystalWcid}, {plan.HealthPerCrystal} health) queued on instance 0x{space.Instance:X8} for {admin.Name} under unbound test match {testMatch} (status {job.Status}{(job.Detail != null ? "; " + job.Detail : "")}): [{string.Join(", ", plan.Crystals.Select(c => $"#{c.Index} {c.Site.Name} ({c.Compass})"))}]");
        }

        /// <summary>
        /// The crystals testspace places on <paramref name="layout"/>: the plan a match would build from <paramref name="d"/> for one
        /// attacker, or null when the layout has no crystal wcid or the plan has no crystals (every King of the Hill map). Pure.
        /// </summary>
        internal static AttackDefendPlan TestSpaceCrystalPlan(BattlegroundLayout layout, BattlegroundDials d)
        {
            if (layout == null || layout.CrystalWcid == 0 || d == null)
                return null;

            var plan = AttackDefendPlan.Build(layout, d, 1);

            return plan.Count > 0 ? plan : null;
        }
        // ---------------- shared ----------------

        private static void Msg(Player player, string text)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
