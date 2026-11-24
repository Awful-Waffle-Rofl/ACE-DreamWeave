using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the pack-space gate on the Iron Sill tally clerk (1001061): every Give (Trade Notes) must
    /// sit in a TestSuccess Pay-* set reached through an InqPackSpace in the @done set, so a full pack
    /// refuses before the cooldown is stamped or the Kills row erased. Pure file + regex parsing of
    /// the committed SQL; no database or dat files.
    /// </summary>
    [TestClass]
    public class IronSillClerkRewardGateTests
    {
        private const int Clerk = 1001061;
        private const int Give = 3;
        private const int Tell = 10;
        private const int InqPackSpace = 89;
        private const int CategoryQuestSuccess = 12;
        private const int CategoryTestSuccess = 22;
        private const int CategoryTestFailure = 23;
        private const int TradeNotes = 20630;

        // One Give of one Trade Notes stack per hall, and four halls can pay in one Use.
        private static readonly string[] Halls = { "Heavy", "Medium", "Light", "Boss" };

        private sealed class Action
        {
            public int Order;
            public int Type;
            public string Message;
            public long? Min;
            public long? Max;
            public long? Amount;
            public long? WeenieClassId;
        }

        private sealed class Emote
        {
            public int Category;
            public string Quest;
            public List<Action> Actions = new List<Action>();
        }

        [TestMethod]
        public void EveryGive_SitsInATestSuccessPaySet()
        {
            var emotes = LoadEmotes();
            var holders = emotes.Where(e => e.Actions.Any(a => a.Type == Give)).ToList();

            Assert.AreEqual(Halls.Length, holders.Count, "one Give-bearing set per hall.");
            foreach (var h in holders)
            {
                Assert.AreEqual(CategoryTestSuccess, h.Category, $"a Give outside TestSuccess ({h.Quest}).");
                StringAssert.StartsWith(h.Quest, "Pay-", "Give in a non Pay-* set.");
                Assert.IsTrue(h.Actions.Single(a => a.Type == Give).WeenieClassId == TradeNotes, $"{h.Quest}: Give wcid.");
            }
        }

        [TestMethod]
        public void EachDoneSet_IsALoneInqPackSpaceIntoItsPaySet()
        {
            var emotes = LoadEmotes();

            foreach (var hall in Halls)
            {
                var done = Single(emotes, CategoryQuestSuccess, $"IronSill{hall}Kills@done");
                var gate = done.Actions.Single();
                Assert.AreEqual(InqPackSpace, gate.Type, $"{hall}: @done must be a lone InqPackSpace.");
                Assert.AreEqual($"Pay-{hall}", gate.Message, $"{hall}: InqPackSpace message.");
                Assert.AreEqual(4L, gate.Amount, $"{hall}: InqPackSpace amount (max Gives per Use).");

                // The Give lives in the set the gate names.
                Assert.IsTrue(Single(emotes, CategoryTestSuccess, gate.Message).Actions.Any(a => a.Type == Give), $"{hall}: Pay set must hold the Give.");
            }
        }

        [TestMethod]
        public void EachTestFailureSet_IsALoneTell()
        {
            var emotes = LoadEmotes();

            foreach (var hall in Halls)
            {
                var fail = Single(emotes, CategoryTestFailure, $"Pay-{hall}").Actions.Single();
                Assert.AreEqual(Tell, fail.Type, $"{hall}: TestFailure must be a lone Tell.");
            }
        }

        // Golden literals copied from origin/master's 1001061.sql (pre-gate): every QuestFailure (13) set,
        // as quest|order|type|message|min|max. The gate must not touch the dispatch chain.
        private static readonly string[] QuestFailureGolden =
        {
            "IronSillHeavy|0|30|IronSillHeavyKills@done|250|2147483647",
            "IronSillHeavyKills@done|0|30|IronSillHeavyKills@mid|1|249",
            "IronSillHeavyKills@mid|0|10|You are for the top hall. The gold ones - the big ones. Two hundred and fifty, and mind the sting.|NULL|NULL",
            "IronSillHeavyKills@mid|1|70|IronSillHeavyKills|NULL|NULL",
            "IronSillMedium|0|30|IronSillMediumKills@done|500|2147483647",
            "IronSillMediumKills@done|0|30|IronSillMediumKills@mid|1|499",
            "IronSillMediumKills@mid|0|10|The middle hall, then. The gilded ones, the walking coins. Five hundred. They fall easy; there are just a great many.|NULL|NULL",
            "IronSillMediumKills@mid|1|70|IronSillMediumKills|NULL|NULL",
            "IronSillLight|0|30|IronSillLightKills@done|1000|2147483647",
            "IronSillLightKills@done|0|30|IronSillLightKills@mid|1|999",
            "IronSillLightKills@mid|0|10|The bottom hall. The tarnished ones swarm thick down there - a thousand of them, and you will still not clear it. Thin them anyway.|NULL|NULL",
            "IronSillLightKills@mid|1|70|IronSillLightKills|NULL|NULL",
            "IronSillBoss|0|30|IronSillBossKills@done|1|2147483647",
            "IronSillBossKills@done|0|30|IronSillBossKills@mid|1|0",
            "IronSillBossKills@mid|0|10|And the thing in the sanctum. The Anchor-Gorged. It has our iron in it and it is not giving it back. Put it down.|NULL|NULL",
            "IronSillBossKills@mid|1|70|IronSillBossKills|NULL|NULL",
        };

        private static string Fmt(long? v) => v.HasValue ? v.Value.ToString() : "NULL";

        [TestMethod]
        public void QuestFailureSets_AreUntouchedFromMaster()
        {
            var actual = LoadEmotes().Where(e => e.Category == 13)
                .SelectMany(e => e.Actions.Select(a => $"{e.Quest}|{a.Order}|{a.Type}|{a.Message}|{Fmt(a.Min)}|{Fmt(a.Max)}"))
                .ToList();

            CollectionAssert.AreEqual(QuestFailureGolden, actual, "QuestFailure dispatch/arming sets must match origin/master exactly.");
        }

        [TestMethod]
        public void InqPackSpace_ExistsOnlyInQuestSuccessDoneSets()
        {
            var gated = LoadEmotes().Where(e => e.Actions.Any(a => a.Type == InqPackSpace)).ToList();

            Assert.AreEqual(Halls.Length, gated.Count, "exactly one InqPackSpace-bearing set per hall.");
            foreach (var e in gated)
            {
                Assert.AreEqual(CategoryQuestSuccess, e.Category, $"InqPackSpace outside QuestSuccess ({e.Quest}).");
                Assert.IsTrue(Halls.Any(h => e.Quest == $"IronSill{h}Kills@done"), $"InqPackSpace in a non-@done set ({e.Quest}).");
            }
        }

        private static Emote Single(List<Emote> emotes, int category, string quest)
        {
            var hits = emotes.Where(e => e.Category == category && e.Quest == quest).ToList();
            Assert.AreEqual(1, hits.Count, $"Expected exactly one emote (category {category}, quest '{quest}'), found {hits.Count}.");
            return hits[0];
        }

        private static List<Emote> LoadEmotes()
        {
            var text = File.ReadAllText(FindWeenieFile(Clerk)).Replace("\r\n", "\n");

            var actionCols = Regex.Match(text, @"weenie_properties_emote_action` \((.*?)\)\n").Groups[1].Value
                .Split(',').Select(c => c.Trim().Trim('`')).ToList();
            int Col(string n) => actionCols.IndexOf(n);

            var result = new List<Emote>();
            var blocks = text.Split(new[] { "INSERT INTO `weenie_properties_emote` (" }, StringSplitOptions.None).Skip(1);

            foreach (var block in blocks)
            {
                var cut = block.IndexOf("INSERT INTO `weenie_properties_create_list`", StringComparison.Ordinal);
                var body = cut >= 0 ? block.Substring(0, cut) : block;

                var emote = new Emote();
                var rows = body.Split('\n').Where(l => l.StartsWith("VALUES (") || l.StartsWith("     , (")).ToList();

                var headerFields = SplitFields(rows[0]);
                emote.Category = int.Parse(Regex.Match(headerFields[1], @"\d+").Value);
                emote.Quest = Unquote(headerFields[6]);

                foreach (var line in rows.Skip(1))
                {
                    var f = SplitFields(line);
                    emote.Actions.Add(new Action
                    {
                        Order = int.Parse(f[Col("order")]),
                        Min = Num(f[Col("min")]),
                        Max = Num(f[Col("max")]),
                        Type = int.Parse(Regex.Match(f[Col("type")], @"\d+").Value),
                        Message = Unquote(f[Col("message")]),
                        Amount = Num(f[Col("amount")]),
                        WeenieClassId = Num(f[Col("weenie_Class_Id")]),
                    });
                }

                result.Add(emote);
            }

            return result;
        }

        private static long? Num(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", "").Trim();
            return s == "NULL" || s.Length == 0 ? (long?)null : long.Parse(s);
        }

        private static string Unquote(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", "").Trim();
            if (s == "NULL") return null;
            return s.Substring(1, s.Length - 2).Replace("''", "'");
        }

        /// <summary>Splits one VALUES tuple row into raw field text, honouring quotes and comments.</summary>
        private static List<string> SplitFields(string row)
        {
            var start = row.IndexOf('(');
            var fields = new List<string>();
            var cur = new System.Text.StringBuilder();
            var inQuote = false;
            var depth = 0;

            for (var i = start + 1; i < row.Length; i++)
            {
                var c = row[i];

                if (inQuote)
                {
                    cur.Append(c);
                    if (c == '\'')
                    {
                        if (i + 1 < row.Length && row[i + 1] == '\'') { cur.Append('\''); i++; }
                        else inQuote = false;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < row.Length && row[i + 1] == '*')
                {
                    var end = row.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    cur.Append(row, i, end + 2 - i);
                    i = end + 1;
                    continue;
                }

                if (c == '\'') { inQuote = true; cur.Append(c); continue; }
                if (c == '(') depth++;
                if (c == ')')
                {
                    if (depth == 0) { fields.Add(cur.ToString().Trim()); return fields; }
                    depth--;
                }

                if (c == ',' && depth == 0) { fields.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(c);
            }

            fields.Add(cur.ToString().Trim());
            return fields;
        }

        private static string FindWeenieFile(int wcid)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    var file = Path.Combine(weenies, wcid + ".sql");
                    Assert.IsTrue(File.Exists(file), $"{wcid}: no weenie file at {file}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
