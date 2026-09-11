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
        private const string Usage = "/dd status | list | give <level> <tier> <modCount> [dungeon|any] [family|any] [entries] [player] | end [0xInstance] | reload";

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
                case "reload":
                    ThreadDungeonManager.Reload();
                    Reply(session, $"Reloaded: {ThreadDungeonManager.Store.Dungeons.Count} dungeons, {ThreadDungeonManager.Store.Bosses.Count} bosses, {ThreadDungeonManager.Store.Modifiers.Count} modifiers, {ThreadDungeonManager.Store.Diagnostics.Count} diagnostics.");
                    break;
                default: Reply(session, Usage); break;
            }
        }

        private static void HandleStatus(Session session)
        {
            if (!ThreadDungeonManager.IsEnabled) { Reply(session, "Threads are not enabled on this server."); return; }
            var run = ThreadDungeonManager.GetRunForOwner(session.Player.Guid.Full);
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
                sb.AppendLine($"0x{r.Instance:X8} {r.Dungeon.Id} owner={r.OwnerName} {r.State} trash={r.TrashKilled}/{r.TrashSpawned} (need {r.ClearTarget}) boss={(r.BossKilled ? "dead" : r.BossSpawned ? "alive" : "none")} progress={r.ClearProgress:P0}/{r.ClearFraction:P0} expires={r.ExpiresUtc:HH:mm}Z");
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
                run = ThreadDungeonManager.GetRunForOwner(session.Player.Guid.Full);
            else
            {
                var text = a[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a[0].Substring(2) : a[0];
                run = uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var inst) ? ThreadDungeonManager.GetRun(inst) : null;
            }
            if (run == null) { Reply(session, "No such live run."); return; }
            ThreadDungeonManager.EndRun(run, $"ended by {session.Player.Name}");
            Reply(session, $"Ended 0x{run.Instance:X8} ({run.Dungeon.Id}, owner {run.OwnerName}).");
        }

        private static void Reply(Session session, string message)
            => CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
    }
}
