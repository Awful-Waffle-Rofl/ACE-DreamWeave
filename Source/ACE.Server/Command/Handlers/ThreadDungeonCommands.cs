using System;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Threads WP-10 (IMPLEMENTATION-PLAN task-10-brief.md). `/dd status` is Player-facing; every
    /// other subcommand is Admin-only and refused before any other work happens. `/dd give` mirrors exactly
    /// how ThreadDungeonGemHandler composes and writes a rolled gem's spec/LongDesc/Structure so an admin
    /// gem is indistinguishable in shape from one a player rolled.
    /// </summary>
    public static class ThreadDungeonCommands
    {
        private const string Usage = "/dd status | list | give <level> <tier> <modCount> [dungeon|any] [family|any] [entries] [player] | end [0xInstance] | reload | guide <player> [show|reset|set N]";

        [CommandHandler("dd", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Threads: status, and admin controls", Usage)]
        public static void HandleDd(Session session, params string[] parameters)
        {
            if (parameters == null || parameters.Length == 0) { Reply(session, Usage); return; }

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToArray();

            if (sub != "status" && session.AccessLevel < AccessLevel.Admin) { Reply(session, "Only /dd status is available to players."); return; }

            switch (sub)
            {
                case "status": HandleStatus(session); break;
                case "list": HandleList(session); break;
                case "give": HandleGive(session, rest); break;
                case "end": HandleEnd(session, rest); break;
                case "guide": HandleGuide(session, rest); break;
                case "reload":
                    ThreadDungeonManager.Reload();
                {
                    // The store also re-reads puzzle-gates.json (ThreadDungeonStore.LoadFromServerConfig), so a site's
                    // "disabled" kill switch takes effect for every run that picks or arms after this; placed puzzles stay.
                    var store = ThreadDungeonManager.Store;
                    var sites = store.PuzzleSites.Values.SelectMany(s => s).ToList();
                    Reply(session, $"Reloaded: {store.Dungeons.Count} dungeons, {store.Bosses.Count} bosses, {store.Modifiers.Count} modifiers, {sites.Count} puzzle sites ({sites.Count(s => s.Disabled)} disabled, {sites.Count(s => !s.Disabled && s.RewardDisabled)} reward-disabled), {store.Diagnostics.Count} diagnostics.");
                    break;
                }
                default: Reply(session, Usage); break;
            }
        }

        /// <summary>
        /// Threads pooled loot (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 6). Runs on the world thread
        /// (commands are dispatched from WorldManager's inbound queue), so reading the run and its caches here
        /// never overlaps the landblock tick; the placement itself is queued onto the run landblock.
        /// </summary>
        [CommandHandler("spawncache", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Threads: form your Thread Cache beside you, or move it to you", "")]
        public static void HandleSpawnCache(Session session, params string[] parameters)
        {
            var player = session.Player;
            var now = DateTime.UtcNow;
            // Group Threads (Task 11): the run the player actually stands in, not the run they own - an owner
            // holding a held cleared run alongside a newer live one must resolve to the run under their feet.
            var run = player == null ? null : ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance);

            // One inside rule for every pooled-loot path (Instance AND Landblock), shared with delivery and the exit guard.
            var inside = run != null && ThreadCachePlacer.IsOwnerInside(run, player);
            var state = run?.State ?? ThreadDungeonRunState.Ended;
            var hasUnclaimed = run != null && (run.IsGroup ? run.HasUnclaimedLootFor(player.Guid.Full) : run.HasUnclaimedLoot);
            var holdsItems = run != null && (run.IsGroup ? ThreadLootPool.AnyCacheHoldsItemsFor(run, player.Guid.Full) : ThreadLootPool.AnyCacheHoldsItems(run));

            var refusal = SpawnCacheRules.FirstRefusal(run != null, inside, state,
                run != null && run.SpawnCacheCooldownElapsed(now),
                run != null && run.IsCachePlacementInProgress(now),
                hasUnclaimed,
                holdsItems);

            if (run != null && SpawnCacheRules.StampsCooldown(refusal))
                run.StampSpawnCacheCooldown(now);

            if (refusal != SpawnCacheRefusal.None)
            {
                Reply(session, SpawnCacheRules.MessageFor(refusal));
                return;
            }

            if (ThreadCachePlacer.RequestPlacement(run, SpawnCacheRules.ModeFor(hasUnclaimed), player.Guid.Full) == CacheRequestOutcome.Busy)
                Reply(session, SpawnCacheRules.FormingMessage);
        }

        private static void HandleStatus(Session session)
        {
            if (!ThreadDungeonManager.IsEnabled) { Reply(session, "Threads are not enabled on this server."); return; }
            var player = session.Player;
            var run = ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance);
            if (run == null) { Reply(session, "You have no dungeon open."); return; }
            var left = run.ExpiresUtc - DateTime.UtcNow;
            Reply(session, $"{run.Dungeon.Name}: {run.State}, trash={run.TrashKilled}/{run.TrashSpawned} (need {run.ClearTarget}), boss {(run.BossKilled ? "slain" : run.BossSpawned ? "alive" : "none")}, progress={run.ClearProgress:P0}/{run.ClearFraction:P0}, closes in {Math.Max(0, (int)left.TotalMinutes)} min.");
        }

        private static void HandleList(Session session)
        {
            var runs = ThreadDungeonManager.LiveRuns;
            if (runs.Count == 0) { Reply(session, "No live runs."); return; }
            var sb = new StringBuilder();
            foreach (var r in runs)
                sb.AppendLine($"0x{r.Instance:X8} {r.Dungeon.Id} owner={r.OwnerName} roster={r.Group.RosterSize} {r.State} trash={r.TrashKilled}/{r.TrashSpawned} (need {r.ClearTarget}) boss={(r.BossKilled ? "dead" : r.BossSpawned ? "alive" : "none")} progress={r.ClearProgress:P0}/{r.ClearFraction:P0} expires={r.ExpiresUtc:HH:mm}Z");
            Reply(session, sb.ToString().TrimEnd());
        }

        private static void HandleGive(Session session, string[] a)
        {
            if (a.Length < 3 || !int.TryParse(a[0], out var level) || !int.TryParse(a[1], out var tier) || !int.TryParse(a[2], out var modCount))
            { Reply(session, Usage); return; }

            var dungeonId = a.Length > 3 ? a[3] : DungeonGemSpec.Any;
            var family = a.Length > 4 ? a[4] : DungeonGemSpec.Any;
            var entries = a.Length > 5 && int.TryParse(a[5], out var e) ? Math.Clamp(e, 0, 99) : 3;

            Player target;
            if (a.Length > 6)
            {
                target = PlayerManager.GetOnlinePlayer(a[6]);
                if (target == null) { Reply(session, $"Player '{a[6]}' is not online."); return; }
            }
            else
            {
                target = session.Player;
            }

            var store = ThreadDungeonManager.Store;
            DungeonGemSpec spec;
            try
            {
                spec = DungeonGemRoller.Roll(level, tier, modCount, store.Modifiers, store.Dungeons, dungeonId, family, new Random());
            }
            catch (ArgumentException ex)
            {
                Reply(session, ex.Message);
                return;
            }

            var entryCount = (ushort)entries;

            // One builder for every producer of a gem (the Fragment Press is the other), so an admin gem
            // and a pressed gem cannot drift on spec string, entries or description. See DungeonGemFactory.
            var dungeonName = DungeonGemFactory.ResolveDungeonName(spec);
            var gem = DungeonGemFactory.CreateGem(spec, entryCount, entryCount, dungeonName, out var createError);
            if (gem == null) { Reply(session, createError); return; }

            if (!target.TryCreateInInventoryWithNetworking(gem))
            {
                gem.Destroy();
                Reply(session, $"{target.Name} has no room for the gem.");
                return;
            }

            Reply(session, $"Gave {target.Name} a Thread Gem: {spec.Serialize()} entries={entryCount}");
        }

        private static void HandleEnd(Session session, string[] a)
        {
            ThreadDungeonRun run;
            if (a.Length == 0)
                run = ThreadDungeonManager.GetRunForMember(session.Player.Guid.Full, session.Player.Location.Instance);
            else
            {
                var text = a[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a[0].Substring(2) : a[0];
                run = uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var inst) ? ThreadDungeonManager.GetRun(inst) : null;
            }
            if (run == null) { Reply(session, "No such live run."); return; }
            ThreadDungeonManager.EndRun(run, $"ended by {session.Player.Name}");
            Reply(session, $"Ended 0x{run.Instance:X8} ({run.Dungeon.Id}, owner {run.OwnerName}).");
        }

        /// <summary>
        /// /dd guide &lt;player&gt; [show|reset|set N], admin-only like every /dd subcommand but status. Online
        /// players only. show prints the ladder state; reset clears the won rung AND bumps the grant serial, so any
        /// guide item the player still holds goes stale and the Thread-Guide issues rung 50 afresh; set N writes
        /// the won rung (0 or a ladder rung) and leaves the serial alone.
        /// </summary>
        private static void HandleGuide(Session session, string[] a)
        {
            if (a.Length == 0) { Reply(session, Usage); return; }

            var target = PlayerManager.GetOnlinePlayer(a[0]);
            if (target == null) { Reply(session, $"Player '{a[0]}' is not online."); return; }

            var action = a.Length > 1 ? a[1].ToLowerInvariant() : "show";

            switch (action)
            {
                case "show":
                    break;

                case "reset":
                    target.ThreadGuideLevel = 0;
                    target.ThreadGuideGrantSerial = target.ThreadGuideGrantSerial + 1;
                    break;

                case "set":
                    if (a.Length < 3 || !int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                        || (level != 0 && !ThreadGuideLadder.IsRungLevel(level)))
                    {
                        Reply(session, $"set takes 0 or a guide rung ({ThreadGuideLadder.FirstRung}..{ThreadGuideLadder.LastRung} in steps of {ThreadGuideLadder.RungStep}).");
                        return;
                    }
                    target.ThreadGuideLevel = level;
                    break;

                default:
                    Reply(session, Usage);
                    return;
            }

            var next = ThreadGuideLadder.NextRung(target.ThreadGuideLevel);
            Reply(session, $"{target.Name}: guide level {target.ThreadGuideLevel}, next rung {(next?.ToString(CultureInfo.InvariantCulture) ?? "none (complete)")}, serial {target.ThreadGuideGrantSerial}, lifetime clears {target.ThreadClearsLifetime}, notice shown {target.ThreadGuideNoticeShown}.");
        }

        private static void Reply(Session session, string message)
            => CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
    }
}
