using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The group run board: one entry per group camp run, naming its members
    /// ("Tester, Bob, Ann"), with its peak Heat and run score. Kept in the group_runs setting (JSON, the best 100 by score).
    /// /top fray groupruns [week] shows it. The per-player group boards (/top fray group, groupscore) stay as they are.
    /// </summary>
    public static class FraysGroupRuns
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object sync = new object();
        public const int Keep = 100;

        public sealed class Run
        {
            [JsonPropertyName("k")] public string Key { get; set; } = "";
            [JsonPropertyName("f")] public string Fray { get; set; } = "";
            [JsonPropertyName("n")] public List<string> Names { get; set; } = new List<string>();
            [JsonPropertyName("h")] public int Heat { get; set; }
            [JsonPropertyName("s")] public long Score { get; set; }
            [JsonPropertyName("w")] public int Week { get; set; }
        }

        public static List<Run> Read(string text)
        {
            try
            {
                return string.IsNullOrWhiteSpace(text) ? new List<Run>() : JsonSerializer.Deserialize<List<Run>>(text) ?? new List<Run>();
            }
            catch
            {
                return new List<Run>();
            }
        }

        /// <summary>Pure: a member's finish added to the run <paramref name="key"/> (one entry per run, its best numbers).</summary>
        public static List<Run> Add(List<Run> runs, string key, string fray, string name, int heat, long score, int week)
        {
            var run = runs.FirstOrDefault(r => r.Key == key);

            if (run == null)
            {
                run = new Run { Key = key, Fray = fray, Week = week };
                runs.Add(run);
            }

            if (!string.IsNullOrEmpty(name) && !run.Names.Contains(name))
                run.Names.Add(name);

            run.Heat = Math.Max(run.Heat, heat);
            run.Score = Math.Max(run.Score, score);
            return runs.OrderByDescending(r => r.Score).ThenByDescending(r => r.Heat).Take(Keep).ToList();
        }

        /// <summary>A group camper's run ended: their name, the run's peak Heat and score go on the run's entry.</summary>
        public static void Note(string key, string fray, string name, int heat, long score)
        {
            try
            {
                lock (sync)
                {
                    var runs = Add(Read(FraysSettings.Text(FraysSettings.GroupRuns)), key, fray, name, heat, score, FraysWeek.Current);
                    var error = FraysAdmin.SetRaw(FraysSettings.GroupRuns, JsonSerializer.Serialize(runs), null);

                    if (error != null)
                        log.Warn($"[FRAYS] group run board not saved: {error}");
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] group run board threw", ex);
            }
        }

        /// <summary>Pure: the board text, all-time or this week.</summary>
        public static string Text(List<Run> runs, bool weekly, int week)
        {
            var list = runs.Where(r => !weekly || r.Week == week).OrderByDescending(r => r.Score).Take(10).ToList();

            if (list.Count == 0)
                return weekly ? "No group runs this week yet." : "No group runs yet.";

            var lines = new List<string> { $"Fray group runs{(weekly ? " (this week)" : "")}:" };
            lines.AddRange(list.Select((r, i) => $"{i + 1}. {string.Join(", ", r.Names)} - Heat {r.Heat}, score {r.Score:N0}{(string.IsNullOrEmpty(r.Fray) ? "" : $" ({r.Fray})")}"));
            return string.Join("\n", lines);
        }

        public static string Text(bool weekly) => Text(Read(FraysSettings.Text(FraysSettings.GroupRuns)), weekly, FraysWeek.Current);
    }
}
