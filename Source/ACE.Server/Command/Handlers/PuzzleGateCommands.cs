using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /puzzlegate: developer authoring loop for the puzzle-gate experiences (ACE.Server.PuzzleGates).
    /// Everything it places is transient and in memory: nothing is written to any database, and a restart
    /// or a landblock unload removes it.
    /// </summary>
    public static class PuzzleGateCommands
    {
        /// <summary>The beams cap is PuzzleGateTunables.MaxBeams (= PuzzleGateColours.Palette.Length), never a literal.</summary>
        private static readonly string Usage =
            "Usage:\n" +
            $"  /puzzlegate place <sigil|beam|odd|shuffle> [n=<4-5 sigil/odd (default: drawn), 2-5 beam>] [beams=<{PuzzleGateTunables.MinBeams}-{PuzzleGateTunables.MaxBeams}>] [rounds=<1-5>] [seed=<int>]\n" +
            "              [lockout=<sec>] [ambush=<wcid>[x<1-5>]] [diff=scale|yaw|glow] [spots=<K>] [radius=<m>] [axis=down|up]\n" +
            "  /puzzlegate reroll [<id>] [seed=<int>]\n" +
            "  /puzzlegate clear [<id>|all]      (no id = nearest within 50 m)\n" +
            "  /puzzlegate list\n" +
            "  /puzzlegate reveal [<id>]          (Developer+: the current answer)\n" +
            "  /puzzlegate spot add|list|clear    (shuffle spots, recorded at your position)\n" +
            ThreadPuzzleSiteTour.Usage;

        [CommandHandler("puzzlegate", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Puzzle-gate experiences: place, reroll, clear, list, reveal, spot, site.",
            "place <type> [key=value ...] | reroll [<id>] [seed=<int>] | clear [<id>|all] | list | reveal [<id>] | spot add|list|clear | site <dungeonId> [<siteId> ...] | site next|prev\n\n" +
            "Builds the puzzle in front of you, facing you: lever row 3 m ahead, gate 7 m ahead.\n" +
            "place and reroll always print the seed; place the same seed to reproduce a layout.\n" +
            "site walks a Thread dungeon's curated puzzle sites: it teleports you to the site and places that puzzle as a run would.")]
        public static void HandlePuzzleGate(Session session, params string[] parameters)
        {
            var admin = session?.Player;

            if (admin == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "/puzzlegate needs a player in the world.");
                return;
            }

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToList();

            switch (sub)
            {
                case "place":
                {
                    if (!PuzzleGateOptions.TryParse(rest, out var options, out var error))
                    {
                        Write(session, $"/puzzlegate place: {error}\n\n{Usage}");
                        return;
                    }

                    Write(session, PuzzleGateManager.Place(admin, options));
                    return;
                }

                case "reroll":
                {
                    if (!TryParseReroll(rest, out var id, out var seed, out var error))
                    {
                        Write(session, $"/puzzlegate reroll: {error}\n\n{Usage}");
                        return;
                    }

                    Write(session, PuzzleGateManager.Reroll(admin, id, seed));
                    return;
                }

                case "clear":
                    if (rest.Count > 1)
                    {
                        Write(session, $"/puzzlegate clear: unexpected '{string.Join(" ", rest.Skip(1))}'.\n\n{Usage}");
                        return;
                    }

                    Write(session, PuzzleGateManager.Clear(admin, rest.Count == 1 ? rest[0] : null));
                    return;

                case "list":
                    foreach (var line in PuzzleGateManager.List())
                        Write(session, line);
                    return;

                case "reveal":
                {
                    int? id = null;

                    if (rest.Count > 1 || (rest.Count == 1 && !TryParseId(rest[0], out id)))
                    {
                        Write(session, $"/puzzlegate reveal: unexpected '{string.Join(" ", rest)}'. Expected an id or nothing.");
                        return;
                    }

                    Write(session, PuzzleGateManager.Reveal(admin, id));
                    return;
                }

                case "site":
                case "sites":
                    HandleSite(session, admin, rest);
                    return;

                case "spot":
                case "spots":
                {
                    var action = rest.Count > 0 ? rest[0].ToLowerInvariant() : "list";

                    switch (action)
                    {
                        case "add":
                            Write(session, PuzzleGateManager.SpotAdd(admin));
                            return;
                        case "list":
                            foreach (var line in PuzzleGateManager.SpotList(admin))
                                Write(session, line);
                            return;
                        case "clear":
                            Write(session, PuzzleGateManager.SpotClear(admin));
                            return;
                        default:
                            Write(session, $"Unknown /puzzlegate spot action '{rest[0]}'. Expected add, list or clear.");
                            return;
                    }
                }

                default:
                    // Name the unknown token back (SkyDecorCommands convention) so a typo is visible.
                    Write(session, $"Unknown /puzzlegate subcommand '{parameters[0]}'.\n\n{Usage}");
                    return;
            }
        }

        // =============================================================================================
        // puzzlegate-sweep: server console only
        // =============================================================================================

        /// <summary>
        /// Places every curated puzzle site's puzzle (each eligible type at its largest lever count, as a gate and as a
        /// reward host) through the real PlaceForRun into a throwaway Thread copy per dungeon, one dungeon at a time,
        /// checks each placement (PuzzleSiteSweep) and writes a TSV. --write-disabled writes the kill switch for the
        /// failures into puzzle-gates.json. Server console only: the sweep loads landblocks and runs for minutes.
        /// </summary>
        [CommandHandler("puzzlegate-sweep", AccessLevel.Developer, CommandHandlerFlag.ConsoleInvoke, 0,
            "Sweeps every curated puzzle site through a throwaway Thread copy and writes a TSV of placement checks.",
            SweepArgs.Usage)]
        public static void HandlePuzzleGateSweep(Session session, params string[] parameters)
        {
            // Server console only. The web admin panel also dispatches ConsoleInvoke handlers with a null session; its
            // dispatch is told apart by WebCommandContext.Current (an AsyncLocal it sets around the call). The
            // command-classification row (in_game_only) already keeps the web from offering it; this is the backstop.
            if (ACE.Server.Command.Web.WebCommandContext.Current != null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "puzzlegate-sweep is server-console only: it loads throwaway landblocks for minutes and --write-disabled writes server files.");
                return;
            }

            if (!SweepArgs.TryParse(parameters, out var args, out var error))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"puzzlegate-sweep: {error}\n{SweepArgs.Usage}");
                return;
            }

            if (!PuzzleSiteSweepRunner.Start(args, line => CommandHandlerHelper.WriteOutputInfo(null, line), out error))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"puzzlegate-sweep: {error}");
                return;
            }

            CommandHandlerHelper.WriteOutputInfo(session, $"puzzlegate-sweep: started ({args.DungeonId ?? "all"}); progress and the TSV path print here.");
        }

        // =============================================================================================
        // /puzzlegate site: tour the curated Thread puzzle sites
        // =============================================================================================

        /// <summary>Per-admin cursor: the dungeon last listed or used and the index of the site last used (-1 = none yet).</summary>
        private static readonly ConcurrentDictionary<uint, (string Dungeon, int Index)> SiteCursor = new ConcurrentDictionary<uint, (string, int)>();

        /// <summary>How long a site placement waits for the destination landblock to finish loading after the teleport.</summary>
        private const double SiteArrivalTimeoutSeconds = 30.0;

        /// <summary>Per-admin supersession counter: each site placement command bumps it, and a pending arrival poll that no longer holds the latest value drops itself.</summary>
        private static readonly ConcurrentDictionary<uint, int> SiteGeneration = new ConcurrentDictionary<uint, int>();

        private static void HandleSite(Session session, Player admin, IReadOnlyList<string> tokens)
        {
            if (!ThreadPuzzleSiteTour.TryParse(tokens, out var args, out var error))
            {
                Write(session, $"/puzzlegate site: {error}\n\n{ThreadPuzzleSiteTour.Usage}");
                return;
            }

            var store = ThreadDungeonManager.Store;
            var cursor = SiteCursor.TryGetValue(admin.Guid.Full, out var c) ? c : (Dungeon: (string)null, Index: -1);

            var dungeonId = args.Action == PuzzleSiteAction.Step ? cursor.Dungeon : args.DungeonId;

            if (dungeonId == null)
            {
                Write(session, "/puzzlegate site: no dungeon yet. Start with /puzzlegate site <dungeonId>.");
                return;
            }

            // Resolve the dungeon id case-insensitively against the dungeons that actually have sites.
            var known = store.PuzzleSites.Keys.Where(k => store.GetPuzzleSites(k).Count > 0).ToList();
            var resolved = known.FirstOrDefault(k => string.Equals(k, dungeonId, StringComparison.OrdinalIgnoreCase));

            if (resolved == null)
            {
                Write(session, $"No puzzle sites for dungeon '{dungeonId}'. Dungeons with sites: {(known.Count == 0 ? "(none loaded)" : ThreadPuzzleSiteTour.ValidDungeons(known))}.");
                return;
            }

            var sites = store.GetPuzzleSites(resolved);

            if (args.Action == PuzzleSiteAction.List)
            {
                SiteCursor[admin.Guid.Full] = (resolved, cursor.Dungeon == resolved ? cursor.Index : -1);

                foreach (var line in ThreadPuzzleSiteTour.ListPage(resolved, sites, args.Page))
                    Write(session, line);

                return;
            }

            int index;

            if (args.Action == PuzzleSiteAction.Step)
            {
                index = ThreadPuzzleSiteTour.Step(cursor.Index, sites.Count, args.Delta);
            }
            else
            {
                index = ThreadPuzzleSiteTour.IndexOf(sites, args.SiteId);

                if (index < 0)
                {
                    Write(session, $"No site '{args.SiteId}' in {resolved}. Valid sites: {ThreadPuzzleSiteTour.ValidSites(sites)}.");
                    return;
                }
            }

            var site = sites[index];
            var seed = args.Seed ?? Random.Shared.Next(1, int.MaxValue);

            if (!ThreadPuzzleSiteTour.TryBuildPick(site, args, seed, out var pick, out error))
            {
                Write(session, $"/puzzlegate site: {error}");
                return;
            }

            if (!ThreadPuzzleSitePicker.TryBuildOptions(pick, out var options, out error))
            {
                Write(session, $"/puzzlegate site: options refused: {error}");
                return;
            }

            var model = ThreadPuzzlePass.ModelFor(site, pick.IsReward);

            if (model == null || site.Anchor == null)
            {
                Write(session, $"/puzzlegate site: site {site.Id} has no placeable gate model (a resident-door site is not supported yet).");
                return;
            }

            SiteCursor[admin.Guid.Full] = (resolved, index);

            // The shared (instance 0) copy of the landblock, facing the site yaw: exactly the anchor a run uses, minus the run instance.
            var rotation = PuzzleGateGenerator.YawQuaternion(site.Yaw);
            var anchor = new Position(site.Anchor.Cell, site.Anchor.X, site.Anchor.Y, site.Anchor.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, 0);

            var spots = pick.Type == PuzzleGateType.Shuffle
                ? site.ShuffleSpots.Where(s => s != null).Select(s => new Vector3(s.X, s.Y, s.Z)).ToList()
                : new List<Vector3>();

            Write(session, $"Site {index + 1}/{sites.Count} {resolved}/{site.Id} [{site.Kind.ToString().ToLowerInvariant()}]{ThreadPuzzleSiteTour.DisabledTag(site)} {pick.Type.ToString().ToLowerInvariant()} n={pick.N} seed={seed}: teleporting; the puzzle is placed on arrival.");

            var generation = SiteGeneration.AddOrUpdate(admin.Guid.Full, 1, (_, g) => g + 1);

            // No follow-up argument: Player.Teleport re-queues itself WITHOUT the follow-up when the admin has a fog
            // colour to clear (Player_Location.cs, the delayTelport branch), which would silently drop the placement.
            // The teleport is queued on its own, and the arrival poll below runs independently of it; the poll
            // waits until the admin is in the destination landblock and it has finished populating, so it also
            // covers that 1 s fog delay.
            WorldManager.ThreadSafeTeleport(admin, new Position(anchor));

            var started = DateTime.UtcNow;
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.25);
            chain.AddAction(admin, () => PlaceOnArrival(session, admin, anchor, site, pick, options, model, spots, started, generation));
            chain.EnqueueChain();
        }

        /// <summary>
        /// The arrival poll. The first pass runs on the world thread or the admin's landblock queue (wherever the
        /// admin is 0.25 s after the teleport was queued); the retries are chained on the admin's own action queue, which
        /// follows the admin into the destination landblock. Same shape as Player.OnTeleportComplete's bounded
        /// re-queue while a landblock finishes loading (Player_Location.cs, OnTeleportComplete). Drops quietly when the
        /// admin is gone, and bails when a newer site command superseded it.
        /// </summary>
        private static void PlaceOnArrival(Session session, Player admin, Position anchor, ACE.Server.ThreadDungeons.Defs.PuzzleSiteDef site, ThreadPuzzlePick pick, PuzzleGateOptions options,
            PuzzleGateModel model, IReadOnlyList<Vector3> spots, DateTime startedUtc, int generation)
        {
            if (admin == null || admin.IsDestroyed || admin.Session == null)
                return;

            var landblock = admin.CurrentLandblock;
            var arrived = landblock != null && landblock.Id.Landblock == anchor.LandblockId.Landblock && landblock.Instance == anchor.Instance;
            var superseded = !SiteGeneration.TryGetValue(admin.Guid.Full, out var latest) || latest != generation;

            switch (ThreadPuzzleSiteTour.DecideArrival(superseded, arrived, landblock != null && landblock.CreateWorldObjectsCompleted, (DateTime.UtcNow - startedUtc).TotalSeconds, SiteArrivalTimeoutSeconds))
            {
                case SiteArrival.Superseded:
                    return;

                case SiteArrival.Place:
                {
                    var placement = PuzzleGateManager.PlaceForAdminSite(admin, landblock, options, pick.Seed, anchor, site.Yaw, spots, model, site.Layout, site.Id, out var error);

                    Write(session, placement == null
                        ? $"/puzzlegate site: {site.Id} not placed: {error}"
                        : $"Puzzle gate #{placement.Id} ({pick.Type.ToString().ToLowerInvariant()}) queued at site {site.Id} seed={pick.Seed}. /puzzlegate reveal {placement.Id} shows the answer.");
                    return;
                }

                case SiteArrival.TimedOut:
                    Write(session, $"/puzzlegate site: {site.Id} not placed: you did not arrive in the shared landblock 0x{anchor.LandblockId.Landblock:X4} within {SiteArrivalTimeoutSeconds:0} s (are you in another realm?). Run the command again.");
                    return;
            }

            var chain = new ActionChain();
            chain.AddDelaySeconds(0.25);
            chain.AddAction(admin, () => PlaceOnArrival(session, admin, anchor, site, pick, options, model, spots, startedUtc, generation));
            chain.EnqueueChain();
        }

        /// <summary>reroll tokens: an optional bare id and an optional seed=&lt;int&gt;, in any order.</summary>
        internal static bool TryParseReroll(IReadOnlyList<string> tokens, out int? id, out int? seed, out string error)
        {
            id = null;
            seed = null;
            error = null;

            foreach (var token in tokens)
            {
                if (token.StartsWith("seed=", StringComparison.OrdinalIgnoreCase))
                {
                    if (seed.HasValue || !int.TryParse(token.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                    {
                        error = $"bad or repeated seed '{token}'.";
                        return false;
                    }

                    seed = s;
                    continue;
                }

                if (!id.HasValue && TryParseId(token, out var parsed))
                {
                    id = parsed;
                    continue;
                }

                error = $"unknown token '{token}'.";
                return false;
            }

            return true;
        }

        private static bool TryParseId(string token, out int? id)
        {
            id = null;

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
                return false;

            id = value;
            return true;
        }

        private static void Write(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text);
    }
}
