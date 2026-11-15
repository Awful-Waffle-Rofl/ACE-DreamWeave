using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The boon order window (/fray boons): a plan window listing all 18
    /// boons that you click into order, saved to your character).
    ///
    /// The AC client draws no clickable list a server can fill, so the window is a
    /// chain of the client's own yes/no boxes, the one dialog a server can open, chain and close. The first box shows the
    /// whole order. Yes starts choosing: each box then asks about one boon ("make it #1?"). Yes takes it, No shows the
    /// next one. Saying No to every boon left in a row ends it, and so does letting a box time out. Each Yes saves at
    /// once, so a half-finished order is kept; the boons left out fall back to the type plan.
    /// </summary>
    public static class FraysBoonOrderWindow
    {
        private sealed class Choosing
        {
            public List<string> Chosen = new List<string>();
            public List<string> Left = new List<string>();
            public int At;
            public int NoInARow;
            public System.DateTime AskedAt;
        }

        private static readonly ConcurrentDictionary<uint, Choosing> open = new ConcurrentDictionary<uint, Choosing>();

        /// <summary>Opens the first box: the player's order and every boon. False when another yes/no box is already up.</summary>
        public static bool Open(Player player)
        {
            if (player?.Session == null)
                return false;

            open.TryRemove(player.Guid.Full, out _);
            return player.ConfirmationManager.EnqueueSend(new Confirmation_FraysBoonOrder(player.Guid, start: true), OverviewText(FraysManager.Record(player)));
        }

        /// <summary>The first box's text: the order, the boons outside it, and how to change it.</summary>
        public static string OverviewText(FraysPlayerRecord rec)
        {
            var order = (rec.BoonOrder ?? new List<string>()).Where(k => FraysBoons.Find(k) != null).ToList();
            var sb = new StringBuilder();
            sb.Append("Your Ember Boon order\n\n");
            sb.Append("When you do not pick a boon in time, the highest one here that is offered is taken. ");
            sb.Append($"If none of them is offered, your type plan picks ({FraysBoons.PlanText(rec.BoonPlan)}).\n\n");

            if (order.Count == 0)
                sb.Append("No order set yet.\n");
            else
                for (var i = 0; i < order.Count; i++)
                    sb.Append($"#{i + 1} {Short(FraysBoons.Find(order[i]))}\n");

            var rest = FraysBoons.All.Where(b => !order.Contains(b.Key)).ToList();

            if (rest.Count > 0)
            {
                sb.Append(order.Count == 0 ? "\nThe boons:\n" : "\nNot in your order:\n");

                foreach (var b in rest)
                    sb.Append($"- {Short(b)}\n");
            }

            sb.Append("\nYes: choose a new order, one boon at a time. No: keep this one.");
            return sb.ToString();
        }

        private static string Short(FraysBoonDef b) => $"{b.Name}{(b.Rare ? " (rare)" : "")}: {b.Text}";

        /// <summary>A box was answered (Confirmation_FraysBoonOrder).</summary>
        public static void Answer(Player player, bool start, bool yes, bool timeout)
        {
            if (player == null)
                return;

            var key = player.Guid.Full;

            if (start)
            {
                if (!yes || timeout)
                    return;

                var rec = FraysManager.Record(player);
                var current = (rec.BoonOrder ?? new List<string>()).Where(k => FraysBoons.Find(k) != null).ToList();
                var s = new Choosing();
                // Your current order comes round first, so keeping most of it is a few quick Yes clicks.
                s.Left.AddRange(current);
                s.Left.AddRange(FraysBoons.All.Select(b => b.Key).Where(k => !current.Contains(k)));
                open[key] = s;
                Ask(player, s);
                return;
            }

            if (!open.TryGetValue(key, out var c))
                return;

            // The client answers a box that timed out by itself, so a late answer counts as the timeout it is.
            if (timeout || (System.DateTime.UtcNow - c.AskedAt).TotalSeconds >= 29)
            {
                open.TryRemove(key, out _);
                Finish(player, c, "The boon order window timed out.");
                return;
            }

            if (yes)
            {
                var pick = c.Left[c.At];
                c.Chosen.Add(pick);
                c.Left.RemoveAt(c.At);
                c.NoInARow = 0;

                if (c.At >= c.Left.Count)
                    c.At = 0;

                var rec = FraysManager.Record(player);
                rec.BoonOrder = c.Chosen.ToList();
                FraysManager.SaveRecord(player, rec);
            }
            else
            {
                c.NoInARow++;
                c.At = c.Left.Count == 0 ? 0 : (c.At + 1) % c.Left.Count;
            }

            if (c.Left.Count == 0 || c.NoInARow >= c.Left.Count)
            {
                open.TryRemove(key, out _);
                Finish(player, c, c.Left.Count == 0 ? "Every boon is in your order." : "Done.");
                return;
            }

            Ask(player, c);
        }

        private static void Ask(Player player, Choosing c)
        {
            c.AskedAt = System.DateTime.UtcNow;
            var b = FraysBoons.Find(c.Left[c.At]);
            var n = c.Chosen.Count + 1;
            var text = new StringBuilder();
            text.Append($"Boon order: choose your #{n}\n\n");
            text.Append($"{b.Name}{(b.Rare ? " (rare)" : "")} ({b.Tag})\n{b.Text}\n\n");
            text.Append($"Yes: make it #{n}. No: show the next boon ({c.Left.Count} left).\n\n");
            text.Append(c.Chosen.Count == 0 ? "Chosen so far: none. Your old order stays until you say Yes once.\n" : $"Chosen so far: {FraysBoons.OrderText(c.Chosen)}\n");
            text.Append($"\nTo finish, say No to all {c.Left.Count - c.NoInARow} boons still to come round, or let this box time out. Every Yes is saved at once.");

            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_FraysBoonOrder(player.Guid, start: false), text.ToString()))
            {
                open.TryRemove(player.Guid.Full, out _);
                Finish(player, c, "Another window opened, so the boon order window closed.");
            }
        }

        private static void Finish(Player player, Choosing c, string why)
        {
            var rec = FraysManager.Record(player);
            FraysManager.Tell(player, c.Chosen.Count == 0
                ? $"{why} Your boon order is unchanged: {FraysBoons.OrderText(rec.BoonOrder)}."
                : $"{why} Your boon order: {FraysBoons.OrderText(rec.BoonOrder)}. Boons left out fall back to your type plan ({FraysBoons.PlanText(rec.BoonPlan)}).");
        }
    }

    /// <summary>One box of the boon order window (FraysBoonOrderWindow): the overview, or "make it #N?".</summary>
    public class Confirmation_FraysBoonOrder : Confirmation
    {
        public bool Start { get; }

        public Confirmation_FraysBoonOrder(ObjectGuid playerGuid, bool start) : base(playerGuid, ConfirmationType.Yes_No)
        {
            Start = start;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false) =>
            FraysBoonOrderWindow.Answer(Player, Start, response, timeout);
    }
}
