using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Daily hunts, the Frays' daily kill tasks:
    /// the Fray Huntmaster (dreamweave-content 2000169) offers the day's kill tasks against this week's family, the one
    /// all three Vissidal Frays field. A player takes up to three; kills only count once taken. Each pays Embers when it
    /// is done. Offers, taken hunts and progress all start over at 00:00 UTC (the daily task reset).
    /// These sit beside the automatic daily and weekly tasks (/frays bounty), which need no pick-up.
    /// </summary>
    public static class FraysHunts
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(FraysHunts));

        public const uint HuntmasterWcid = 2000169;

        // The task-givers: a few kill task NPCs that give the dailies and one weekly. Each
        // hands out its own share of the board; the tasks are picked up from them, not by command.
        public const uint SnarlTrackerWcid = 2000176;
        public const uint HeatWardenWcid = 2000177;
        public const uint WeekWardenWcid = 2000178;

        public static readonly uint[] Givers = { HuntmasterWcid, SnarlTrackerWcid, HeatWardenWcid, WeekWardenWcid };

        public static string GiverName(uint wcid) => wcid switch
        {
            SnarlTrackerWcid => "Fray Snarl-Tracker",
            HeatWardenWcid => "Fray Heat-Warden",
            WeekWardenWcid => "Fray Week-Warden",
            _ => "Fray Huntmaster",
        };

        /// <summary>Who hands out <paramref name="t"/>: the weekly, the Heat tasks, the family hunts, or the rest (Snarls, health).</summary>
        public static uint GiverOf(TaskEntry t)
        {
            if (t.Weekly)
                return WeekWardenWcid;

            if (t.Key.StartsWith("heat"))
                return HeatWardenWcid;

            return t.IsHunt ? HuntmasterWcid : SnarlTrackerWcid;
        }

        /// <summary>
        /// Paid with every finished task, on top of its Embers or Vault Keys (the same XP and luminance as the
        /// Marae Lassel bounties: 3% of the level, 20M to 100M, and 25,000 luminance). <paramref name="times"/> scales all of it
        /// (the weekly goal pays fray_goal_task_mult times).
        /// </summary>
        public static void Reward(Player player, double times = 1)
        {
            try
            {
                var pct = FraysSettings.Number(FraysSettings.TaskXpPercent);

                if (pct > 0)
                    player.GrantLevelProportionalXp(pct * times, (long)(FraysSettings.Number(FraysSettings.TaskXpMin) * times), (long)(FraysSettings.Number(FraysSettings.TaskXpMax) * times));

                var lum = (long)Math.Round(FraysSettings.Number(FraysSettings.TaskLuminance) * times);

                if (lum > 0)
                    player.EarnLuminance(lum, XpType.Quest, ShareType.None);
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] task reward for {player?.Name} threw", ex);
            }
        }

        /// <summary>Any creature of the family, counted by size (a swarm one 0.4), like the daily kill task.</summary>
        public const string AnyKey = "any";

        /// <summary>Any Gilded creature of the family (a rich point's pack). The key keeps its old name "elite" so taken hunts still match.</summary>
        public const string EliteKey = "elite";

        public sealed class HuntDef
        {
            public string Key;
            public string Text;
            public double Target;

            /// <summary>The one creature it counts (0: any of the family).</summary>
            public uint Wcid;

            /// <summary>A Heat hunt: reach this Heat in a camp (0: a kill hunt). Pays <see cref="Keys"/> Vault Keys, not Embers.</summary>
            public int Heat;
            public int Keys;
        }

        /// <summary>This week's family (the Vissidal Frays share one), or null when families are off.</summary>
        public static FraysFamily WeekFamily => FraysFamilies.For(FraysDefs.VissidalCoast);

        /// <summary>
        /// The day's offers against <paramref name="family"/>, the same for everyone: any of the family, three of its common
        /// creatures (a different three most days), and its Gilded ones. A rarer creature asks for fewer kills.
        /// </summary>
        public static List<HuntDef> Offers(FraysFamily family, int day, Func<uint, string> nameOf)
        {
            var list = new List<HuntDef>();

            if (family == null)
                return list;

            list.Add(new HuntDef { Key = AnyKey, Text = $"Kill 400 of the {family.Name} (by size: a swarm one counts 0.4)", Target = 400 });

            // The common creatures: Threads families by their spawn weight, the original families' swarm and standard ones.
            var members = family.Table != null
                ? family.Trash.Where(m => m.Weight > 0).GroupBy(m => m.Wcid).Select(g => (Wcid: g.Key, Weight: g.Sum(m => m.Weight))).ToList()
                : new[] { (Wcid: family.Swarm, Weight: 2.0), (Wcid: family.Standard, Weight: 1.0) }.Where(m => m.Wcid != 0).Distinct().ToList();

            var total = members.Sum(m => m.Weight);
            var rng = new Random(unchecked(day * 7919 + 4099));
            var picks = members.OrderByDescending(m => m.Weight).ThenBy(m => m.Wcid).Take(6).OrderBy(_ => rng.Next()).Take(3).ToList();

            foreach (var m in picks)
            {
                var share = total > 0 ? m.Weight / total : 1;
                var target = (int)Math.Clamp(Math.Round(300 * share / 10) * 10, 30, 150);
                list.Add(new HuntDef { Key = $"c{m.Wcid}", Wcid = m.Wcid, Target = target, Text = $"Kill {target}: {nameOf(m.Wcid)}" });
            }

            list.Add(new HuntDef { Key = EliteKey, Text = $"Kill 50 Gilded {family.Name}", Target = 50 });   // Gilded took the elites' place

            // The Heat hunts (the daily Heat chests, as daily kill tasks): climb to Heat 10, 15
            // or 20 in a camp after taking one. They pay 1, 2 and 3 Vault Keys.
            for (var i = 0; i < HeatHunts.Length; i++)
                list.Add(new HuntDef { Key = $"heat{HeatHunts[i]}", Heat = HeatHunts[i], Keys = i + 1, Target = 1,
                    Text = $"Climb to Heat {HeatHunts[i]} in a camp (pays {i + 1} Vault Key{(i == 0 ? "" : "s")})" });

            return list;
        }

        /// <summary>The Heats the daily Heat hunts ask for.</summary>
        public static readonly int[] HeatHunts = { 10, 15, 20 };

        public static List<HuntDef> Today() => Offers(WeekFamily, FraysLongTerm.Today, Name);

        private static string Name(uint wcid)
        {
            try
            {
                return DatabaseManager.World.GetCachedWeenie(wcid)?.GetName() ?? $"creature {wcid}";
            }
            catch
            {
                return $"creature {wcid}";
            }
        }

        /// <summary>A new day: today's offers, nothing taken.</summary>
        public static FraysPlayerRecord Roll(FraysPlayerRecord rec, int day)
        {
            if (rec.HuntDay != day)
            {
                rec.HuntDay = day;
                rec.HuntTaken.Clear();
                rec.HuntProgress.Clear();
                rec.HuntDone.Clear();
            }

            return rec;
        }

        /// <summary>What a kill adds to a hunt: 0 when it does not count.</summary>
        public static double Counts(HuntDef hunt, FraysFamily family, uint wcid, double work, bool gilded)
        {
            if (hunt.Heat > 0)
                return 0;

            if (family == null || !family.Contains(wcid))
                return 0;

            return hunt.Key switch
            {
                AnyKey => work,
                EliteKey => gilded ? 1 : 0,
                _ => hunt.Wcid == wcid ? 1 : 0,
            };
        }

        /// <summary>A Frays kill credited to <paramref name="player"/>: progress on their taken hunts, paid as each is done.</summary>
        public static void OnKill(Player player, uint wcid, double work, bool gilded)
        {
            if (player == null)
                return;

            var rec = Roll(FraysManager.Record(player), FraysLongTerm.Today);

            if (rec.HuntTaken.Count == 0)
                return;

            var family = WeekFamily;
            var offers = Today();
            var changed = false;

            foreach (var key in rec.HuntTaken.ToList())
            {
                var hunt = offers.FirstOrDefault(h => h.Key == key);
                var add = hunt == null ? 0 : Counts(hunt, family, wcid, work, gilded);

                if (add <= 0)
                    continue;

                rec.HuntProgress.TryGetValue(key, out var have);
                have += add;
                rec.HuntProgress[key] = have;
                changed = true;

                if (have >= hunt.Target)
                {
                    Complete(player, rec, hunt);
                }
            }

            if (changed)
                FraysManager.SaveRecord(player, rec);
        }

        /// <summary>A hunt is done: off the taken list, onto the done list, and paid (Embers, or a Heat hunt's Vault Keys).</summary>
        private static void Complete(Player player, FraysPlayerRecord rec, HuntDef hunt)
        {
            rec.HuntTaken.Remove(hunt.Key);
            rec.HuntDone.Add(hunt.Key);
            FraysMetrics.TasksCompleted.Add(1);

            var giver = hunt.Heat > 0 ? HeatWardenWcid : HuntmasterWcid;
            Ready(player, rec, giver, hunt.Text, hunt.Keys > 0 ? 0 : FraysSettings.Int(FraysSettings.HuntEmbers), hunt.Keys);
        }

        /// <summary>
        /// A task is finished: it waits for hand-in at <paramref name="giver"/>, like a Marae Lassel kill task, with no
        /// popup window.
        /// </summary>
        public static void Ready(Player player, FraysPlayerRecord rec, uint giver, string text, int embers, int keys, int seals = 0)
        {
            rec.TaskReady.Add(new FraysTaskReady { Giver = giver, Text = text, Embers = embers, Keys = keys, Seals = seals });
            FraysManager.Tell(player, $"Task complete: {text}. Return to the {GiverName(giver)} for your reward.");
        }

        /// <summary>Hands in every finished task <paramref name="giver"/> takes: its Embers or Vault Keys, XP and luminance.</summary>
        private static List<string> HandIn(Player player, FraysPlayerRecord rec, uint giver)
        {
            var said = new List<string>();

            foreach (var r in rec.TaskReady.Where(r => r.Giver == giver).ToList())
            {
                rec.TaskReady.Remove(r);
                Reward(player);

                if (r.Keys > 0)
                    FraysLongTerm.Give(player, FraysLongTerm.DreamKey, r.Keys);

                if (r.Seals > 0)
                    FraysLongTerm.Give(player, FraysSky.Seal, r.Seals);

                if (r.Embers > 0)
                {
                    player.FraysAddStackable(FraysDefs.EmberWcid, r.Embers);
                    FraysMetrics.AddPositive(FraysMetrics.EmbersTask, r.Embers);
                }

                said.Add($"Well done: {r.Text}. Here is what I owe you: " +
                    (r.Keys > 0 ? $"{r.Keys} Vault Key{(r.Keys == 1 ? "" : "s")}" : r.Seals > 0 ? $"{r.Seals} Sky Seal{(r.Seals == 1 ? "" : "s")}" : $"{r.Embers:N0} Embers") + ", with XP and luminance.");
            }

            return said;
        }

        /// <summary>Pure: the Heat hunts a climb from <paramref name="from"/> to <paramref name="to"/> finishes.</summary>
        public static List<HuntDef> HeatDone(IEnumerable<HuntDef> taken, int from, int to) =>
            taken.Where(h => h.Heat > 0 && from < h.Heat && to >= h.Heat).ToList();

        /// <summary>A camper climbed from <paramref name="from"/> to <paramref name="to"/>: any taken Heat hunt it crossed is done.</summary>
        public static void OnHeat(Player player, int from, int to)
        {
            if (player == null || to < HeatHunts[0])
                return;

            var rec = Roll(FraysManager.Record(player), FraysLongTerm.Today);

            if (rec.HuntTaken.Count == 0)
                return;

            var done = HeatDone(Today().Where(h => rec.HuntTaken.Contains(h.Key)), from, to);

            if (done.Count == 0)
                return;

            foreach (var h in done)
            {
                rec.HuntProgress[h.Key] = 1;
                Complete(player, rec, h);
            }

            FraysManager.SaveRecord(player, rec);
        }

        // ---- the task board (bounties and hunts are one list of daily and weekly tasks picked up
        // from the village) ---------------------------------------------------------------------------------------------

        /// <summary>One line of the task board: a hunt (today's kill and Heat tasks) or a daily or weekly task.</summary>
        public sealed class TaskEntry
        {
            public string Key;
            public string Text;
            public bool Weekly;
            public bool IsHunt;
            public double Target;
            public string Reward;
        }

        /// <summary>The board's tasks for <paramref name="rec"/> (rolled to today and this week): daily first, then weekly.</summary>
        public static List<TaskEntry> Tasks(FraysPlayerRecord rec)
        {
            var list = Today().Select(h => new TaskEntry
            {
                Key = h.Key, Text = h.Text, IsHunt = true, Target = h.Target,
                Reward = h.Keys > 0 ? $"{h.Keys} Vault Key{(h.Keys == 1 ? "" : "s")}" : $"{FraysSettings.Int(FraysSettings.HuntEmbers)} Embers",
            }).ToList();

            foreach (var key in rec.BountyGoals)
            {
                var d = FraysLongTerm.BountyPool.FirstOrDefault(b => b.Key == key);

                if (d != null)
                    list.Add(new TaskEntry
                    {
                        Key = key, Text = d.Text, Weekly = d.Weekly, Target = d.Target,
                        Reward = key == "w_group" ? $"{FraysLongTerm.TaskSeals(key)} Sky Seals" : $"{FraysSettings.Int(d.Weekly ? FraysSettings.WeeklyTaskEmbers : FraysSettings.BountyEmbers)} Embers",
                    });
            }

            return list.OrderBy(t => t.Weekly).ToList();
        }

        private static FraysPlayerRecord Prep(Player player) => FraysLongTerm.RollBountyDay(Roll(FraysManager.Record(player), FraysLongTerm.Today));

        private static bool Done(FraysPlayerRecord rec, TaskEntry t) => t.IsHunt ? rec.HuntDone.Contains(t.Key) : rec.BountyPaid.Contains(t.Key);

        private static bool Taken(FraysPlayerRecord rec, TaskEntry t) => t.IsHunt ? rec.HuntTaken.Contains(t.Key) : rec.BountyTaken.Contains(t.Key);

        private static double Have(FraysPlayerRecord rec, TaskEntry t) =>
            (t.IsHunt ? rec.HuntProgress : rec.BountyProgress).TryGetValue(t.Key, out var have) ? have : 0;

        /// <summary>
        /// The board: every task (or just <paramref name="giver"/>'s), each open, taken (with progress) or done. The numbers
        /// are the whole board's, so /fray tasks drop works from any of them.
        /// </summary>
        public static string Board(Player player, uint giver = 0)
        {
            var rec = Prep(player);
            FraysManager.SaveRecord(player, rec);
            var tasks = Tasks(rec);

            var lines = new List<string> { giver == 0 ? "THE TASK BOARD" : GiverName(giver).ToUpperInvariant(), "" };

            if (WeekFamily != null)
                lines.Add($"This week the {WeekFamily.Name} walk the Frays. Kills only count once a task is taken.");

            lines.Add("Daily tasks are new at 00:00 UTC, the weekly task at the weekly reset.");
            lines.Add($"Each finished task also pays XP and {FraysSettings.Int(FraysSettings.TaskLuminance):N0} luminance.");

            var lastWeekly = (bool?)null;

            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];

                if (giver != 0 && GiverOf(t) != giver)
                    continue;

                if (lastWeekly != t.Weekly)
                {
                    lines.Add("");
                    lines.Add(t.Weekly ? "WEEKLY" : "DAILY");
                    lastWeekly = t.Weekly;
                }

                var state = Done(rec, t) ? (rec.TaskReady.Any(r => r.Text == t.Text) ? $"[done - hand in to the {GiverName(GiverOf(t))}]" : "[done]")
                    : Taken(rec, t) ? $"[taken {Math.Floor(Math.Min(Have(rec, t), t.Target)):N0} of {t.Target:N0}]"
                    : "[open]";
                lines.Add($"{i + 1}. {state} {t.Text} - {t.Reward}" + (giver == 0 ? $" ({GiverName(GiverOf(t))})" : ""));
            }

            lines.Add("");
            lines.Add(giver == 0
                ? "Tasks are picked up from the task-givers in the village. /fray tasks drop <number> lets one go (its progress is lost)."
                : "/fray tasks shows the whole board, /fray tasks drop <number> lets one go (its progress is lost).");
            return string.Join("\n", lines);
        }

        private static string TakeOne(FraysPlayerRecord rec, TaskEntry t)
        {
            if (Done(rec, t))
                return $"You finished that task already: {t.Text}.";

            if (Taken(rec, t))
                return $"You already have that task: {t.Text}.";

            (t.IsHunt ? rec.HuntTaken : rec.BountyTaken).Add(t.Key);
            return $"Task taken: {t.Text}.";
        }

        /// <summary>The open tasks <paramref name="giver"/> hands out.</summary>
        private static List<TaskEntry> OpenFrom(FraysPlayerRecord rec, uint giver) =>
            Tasks(rec).Where(t => GiverOf(t) == giver && !Done(rec, t) && !Taken(rec, t)).ToList();

        /// <summary>/fray tasks drop &lt;n&gt;.</summary>
        public static string Drop(Player player, int number)
        {
            var rec = Prep(player);
            var tasks = Tasks(rec);

            if (number < 1 || number > tasks.Count || !Taken(rec, tasks[number - 1]))
                return $"You do not have task {number}.";

            var t = tasks[number - 1];
            (t.IsHunt ? rec.HuntTaken : rec.BountyTaken).Remove(t.Key);
            (t.IsHunt ? rec.HuntProgress : rec.BountyProgress).Remove(t.Key);
            FraysManager.SaveRecord(player, rec);
            return $"Task dropped: {t.Text}.";
        }

        /// <summary>
        /// Talking to a task-giver, the way the Marae Lassel kill task NPCs work, with no popup
        /// window: it says everything in chat. Finished tasks are handed in and paid, open ones are taken, and taken ones
        /// report their count.
        /// </summary>
        public static void Talk(Player player, WorldObject npc)
        {
            var giver = npc.WeenieClassId;
            var rec = Prep(player);
            var lines = HandIn(player, rec, giver);

            foreach (var t in OpenFrom(rec, giver))
            {
                TakeOne(rec, t);
                lines.Add($"Task taken: {t.Text}. It pays {t.Reward}, with XP and luminance. Come back to me when it is done.");
            }

            foreach (var t in Tasks(rec).Where(t => GiverOf(t) == giver && Taken(rec, t)))
            {
                if (lines.Any(l => l.StartsWith($"Task taken: {t.Text}.")))
                    continue;

                lines.Add($"{t.Text}: {Math.Floor(Math.Min(Have(rec, t), t.Target)):N0} of {t.Target:N0} so far.");
            }

            FraysManager.SaveRecord(player, rec);

            if (lines.Count == 0)
                lines.Add(giver == WeekWardenWcid
                    ? "You have done my task this week. Come back after the weekly reset."
                    : "You have done all I have for today. Come back after the daily reset at 00:00 UTC.");

            foreach (var line in lines)
                player.Session?.Network.EnqueueSend(new GameEventTell(npc, line, player, ChatMessageType.Tell));
        }
    }
}
