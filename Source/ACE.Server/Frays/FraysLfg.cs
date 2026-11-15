using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The group finder (like WoW's finder). /fray lfg join puts a player in the queue
    /// for the group camps; /fray lfg on its own shows who waits; /fray lfg leave leaves. When lfg_min players wait
    /// (group_min_size, at least 3), each gets a yes/no popup. If enough say yes within lfg_answer_seconds, the server forms
    /// the fellowship (the first to say yes leads) and sends everyone to a free group camp on the Sky Fray. Anyone who says
    /// no leaves the queue; the rest wait for the next match. The Fellowship Steward shows the same queue.
    /// </summary>
    public static class FraysLfg
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object sync = new object();

        /// <summary>Who waits, in the order they joined, and when.</summary>
        private static readonly List<(uint Guid, double At)> queue = new List<(uint, double)>();

        private sealed class Match
        {
            public readonly List<uint> Asked = new List<uint>();
            public readonly List<uint> Yes = new List<uint>();
            public readonly HashSet<uint> No = new HashSet<uint>();
            public double Until;
            public int Id;
        }

        private static Match match;
        private static int nextMatchId = 1;

        public const int MaxGroup = 9;

        public static int Need => Math.Max(3, FraysSettings.Int(FraysSettings.GroupMinSize));

        /// <summary>Pure: who a match asks: the first <paramref name="max"/> waiting, once at least <paramref name="need"/> wait.</summary>
        public static List<uint> PickMatch(IReadOnlyList<uint> waiting, int need, int max) =>
            waiting.Count < need ? new List<uint>() : waiting.Take(max).ToList();

        /// <summary>Pure: whether a match forms a group.</summary>
        public static bool Forms(int yes, int need) => yes >= need;

        public static string Join(Player player)
        {
            if (player.Fellowship != null)
                return "You are already in a fellowship. Bring it to a group camp on the Sky Fray, or leave it to use the group finder.";

            lock (sync)
            {
                if (queue.Any(q => q.Guid == player.Guid.Full))
                    return $"You are already waiting ({queue.Count} in the queue, {Need} make a group).";

                queue.Add((player.Guid.Full, Time.GetUnixTime()));
                return $"You join the group finder: {queue.Count} waiting, {Need} make a group. You get a yes/no when a group is found. /fray lfg leave to leave.";
            }
        }

        public static string Leave(Player player)
        {
            lock (sync)
                return queue.RemoveAll(q => q.Guid == player.Guid.Full) > 0 ? "You leave the group finder." : "You are not in the group finder.";
        }

        public static string Text()
        {
            lock (sync)
            {
                var names = queue.Select(q => PlayerManager.GetOnlinePlayer(q.Guid)?.Name).Where(n => n != null).ToList();
                var head = $"Group finder: {names.Count} waiting, {Need} make a group (the group camps on the Sky Fray).";
                return names.Count == 0 ? head + " Nobody waits yet. /fray lfg join to wait." : head + " Waiting: " + string.Join(", ", names) + ". /fray lfg join | leave.";
            }
        }

        /// <summary>Once a second (FraysManager.Tick): drops the gone, starts a match, closes a match.</summary>
        public static void Tick(double now)
        {
            List<(Player P, int Id)> ask = null;
            Match close = null;

            lock (sync)
            {
                var stale = FraysSettings.Number(FraysSettings.LfgQueueMinutes) * 60;
                queue.RemoveAll(q => PlayerManager.GetOnlinePlayer(q.Guid) is not Player p || p.Fellowship != null || now - q.At > stale);

                if (match != null && (now >= match.Until || match.Asked.All(g => match.Yes.Contains(g) || match.No.Contains(g))))
                {
                    close = match;
                    match = null;
                }
                else if (match == null)
                {
                    var picked = PickMatch(queue.Select(q => q.Guid).ToList(), Need, MaxGroup);

                    if (picked.Count > 0)
                    {
                        match = new Match { Until = now + FraysSettings.Number(FraysSettings.LfgAnswerSeconds), Id = nextMatchId++ };
                        match.Asked.AddRange(picked);
                        ask = picked.Select(g => (PlayerManager.GetOnlinePlayer(g), match.Id)).Where(x => x.Item1 != null).ToList();
                    }
                }
            }

            if (ask != null)
            {
                var names = string.Join(", ", ask.Select(a => a.P.Name));

                foreach (var (p, id) in ask)
                {
                    FraysManager.Tell(p, $"Group finder: a group is ready ({names}). Answer the popup to join.");
                    p.ConfirmationManager.EnqueueSend(new Confirmation_FraysLfg(p.Guid, id), $"A Fray group is ready: {names}. Join it and go to a group camp on the Sky Fray?");
                }
            }

            if (close != null)
                Close(close);
        }

        /// <summary>A yes or no from the popup.</summary>
        public static void Answer(Player player, int matchId, bool yes)
        {
            lock (sync)
            {
                if (match == null || match.Id != matchId || !match.Asked.Contains(player.Guid.Full))
                {
                    FraysManager.Tell(player, "That group has already formed or broken up.");
                    return;
                }

                if (yes)
                {
                    if (!match.Yes.Contains(player.Guid.Full))
                        match.Yes.Add(player.Guid.Full);
                    FraysManager.Tell(player, $"You said yes ({match.Yes.Count} of {match.Asked.Count}, {Need} needed).");
                }
                else
                {
                    match.No.Add(player.Guid.Full);
                    queue.RemoveAll(q => q.Guid == player.Guid.Full);
                    FraysManager.Tell(player, "You said no and leave the group finder.");
                }
            }
        }

        private static void Close(Match m)
        {
            var yes = m.Yes.Select(PlayerManager.GetOnlinePlayer).Where(p => p != null && p.Fellowship == null).ToList();

            if (!Forms(yes.Count, Need))
            {
                foreach (var p in yes)
                    FraysManager.Tell(p, $"Group finder: only {yes.Count} said yes ({Need} needed). You stay in the queue for the next group.");

                lock (sync)
                {
                    // Those who did not answer leave the queue, so a match is not held up by the same away player again.
                    queue.RemoveAll(q => m.Asked.Contains(q.Guid) && !m.Yes.Contains(q.Guid));
                }

                return;
            }

            lock (sync)
                queue.RemoveAll(q => m.Asked.Contains(q.Guid));

            var leader = yes[0];
            var chain = new ActionChain();
            chain.AddAction(leader, () => Form(leader, yes.Skip(1).ToList()));
            chain.EnqueueChain();
        }

        private static void Form(Player leader, List<Player> others)
        {
            try
            {
                leader.FellowshipCreate("Fray Group", true);

                if (leader.Fellowship == null)
                {
                    FraysManager.Tell(leader, "Group finder: the fellowship could not be made. Try /fray lfg join again.");
                    return;
                }

                foreach (var o in others)
                    leader.FellowshipRecruit(o);

                var all = new List<Player> { leader };
                all.AddRange(others.Where(o => o.Fellowship == leader.Fellowship));

                // A free group camp: the middle one only for a big enough group.
                var fire = FraysManager.Fires.Where(f => f.Def.Group && !f.Lit && (!f.Def.BigGroup || all.Count >= FraysSettings.Int(FraysSettings.GroupMinSizeMiddle)))
                    .OrderBy(f => f.Def.BigGroup).FirstOrDefault();

                if (fire == null)
                {
                    foreach (var p in all)
                        FraysManager.Tell(p, $"Group finder: your fellowship is made ({string.Join(", ", all.Select(a => a.Name))}), but every group camp is lit. Wait for one to burn out.");
                    return;
                }

                var i = 0;
                foreach (var p in all)
                {
                    var pos = fire.FirePosition;
                    pos.PositionX += 2 + (i % 3);
                    pos.PositionY += 2 + (i / 3);
                    i++;

                    if (!p.IsDead && !p.PKTimerActive)
                        p.Teleport(pos);

                    FraysManager.Tell(p, $"Group finder: your fellowship is made ({string.Join(", ", all.Select(a => a.Name))}). You are sent to {fire.Def.RoomName}. Use its campfire to light it together.");
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] group finder form threw", ex);
            }
        }

        /// <summary>The Fellowship Steward: the queue, then a yes/no to join it.</summary>
        public static void Talk(Player player)
        {
            FraysManager.Tell(player, "Fellowship Steward says, \"Group camps on the Sky Fray share one Heat, and every Snarl there gives each member a Sky Seal for cosmetics. I keep the group finder: wait here, and I will gather a fellowship for you.\"");
            FraysManager.Tell(player, Text());

            lock (sync)
            {
                if (player.Fellowship != null || queue.Any(q => q.Guid == player.Guid.Full))
                    return;
            }

            player.ConfirmationManager.EnqueueSend(new Confirmation_FraysLfgJoin(player.Guid), "Join the group finder queue for the Sky Fray group camps?");
        }
    }

    /// <summary>The group finder's "a group is ready" yes/no.</summary>
    public class Confirmation_FraysLfg : Confirmation
    {
        public int MatchId { get; }

        public Confirmation_FraysLfg(ObjectGuid playerGuid, int matchId) : base(playerGuid, ConfirmationType.Yes_No) => MatchId = matchId;

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (timeout || PlayerManager.GetOnlinePlayer(PlayerGuid) is not Player player)
                return;

            FraysLfg.Answer(player, MatchId, response);
        }
    }

    /// <summary>The Fellowship Steward's "join the queue?" yes/no.</summary>
    public class Confirmation_FraysLfgJoin : Confirmation
    {
        public Confirmation_FraysLfgJoin(ObjectGuid playerGuid) : base(playerGuid, ConfirmationType.Yes_No) { }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (timeout || !response || PlayerManager.GetOnlinePlayer(PlayerGuid) is not Player player)
                return;

            FraysManager.Tell(player, FraysLfg.Join(player));
        }
    }
}
