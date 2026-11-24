using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the Marae Lassel hunter-camp CONTENT (the 27 Hea hunters 1008150-1008176, their HuntCount_ quest rows, and the kill tags on the
    /// 73 target creatures and their listed look-alike copies) against each other. See Docs/Marae-Lassel/HUNTER-CAMPS.md.
    ///
    /// WHY: a hunter assigns a species by creating a counter row named HuntCount_&lt;Zone&gt;&lt;Category&gt;&lt;Species&gt;, and the row only moves
    /// when the creature that dies carries the SAME string in KillQuest (45), KillQuest2 (49) or KillQuest3 (50). The name lives in three
    /// places that nothing else ties together - the hunter's emote table, the quest unit, and the target's own weenie SQL - so a typo or a
    /// missing tag is a hunt that can be accepted and never advances, and nothing in the build notices because every one of them is just a
    /// string. These tests are the second reader.
    ///
    /// It reads Content/ and tools/ directly (no database), locating the repo by walking up from the test assembly, so it must run in-tree
    /// (never with --artifacts-path).
    /// </summary>
    [TestClass]
    public class HuntCampContentTests
    {
        private const int FirstHunter = 1008150;
        private const int LastHunter = 1008176;
        private const int ExpectedSpecies = 73;
        private const int WaitMinDelta = 72000;

        // PropertyString 45/49/50 are KillQuest, KillQuest2, KillQuest3 (PropertyString.cs); the two Aun below already carry 45 and 49 for other quests.
        private static readonly HashSet<int> SlotThreeWcids = new HashSet<int> { 1002892, 1002893 };

        /// <summary>
        /// The look-alike copies that DO give hunt credit: copy wcid -> original wcid. Every one is a same-NAME clone of a Plateau hunt target that
        /// spawns on the open plateau (the "UCM Spot 3" tier-8 loot clones, tools/marae-bestiary/generate_generators.py); each carries its
        /// original's HuntCount_ tag in the same slot. Explicit on purpose: a new copy must be added here (or to <see cref="ExcludedCopies"/>).
        /// </summary>
        private static readonly Dictionary<int, int> CreditedCopies = new Dictionary<int, int>
        {
            { 1005875, 1002877 }, { 1005876, 1002881 }, { 1005877, 1002879 }, { 1005878, 1002880 }, { 1005879, 1002885 },
            { 1005880, 1002887 }, { 1005881, 1002878 }, { 1005882, 1002876 }, { 1005883, 1002886 }, { 1005884, 1002884 },
            { 1005885, 1003662 }, { 1005887, 1003661 }, { 1005888, 1003668 }, { 1005889, 1002890 }, { 1005890, 1003663 },
            { 1005891, 1002883 }, { 1005892, 1002889 }, { 1005893, 1002888 }, { 1005894, 1003669 }, { 1005895, 1002882 },
            { 1005896, 1003664 },
        };

        /// <summary>
        /// Same-name or look-alike copies that deliberately give NO hunt credit: the 12 Bluespire ladder D1 warden copies (dungeon spawns, not
        /// the open zone) and the six Greenspire Aun copies 1003256-1003261 (different Name, spawn only on headland landblock 0x2BB5, tags
        /// deliberately headland-only for the C4 quest).
        /// </summary>
        private static readonly HashSet<int> ExcludedCopies = new HashSet<int>
        {
            1006425, 1006427, 1006470, 1006472, 1006474, 1006476, 1006478, 1006480, 1006482, 1006484, 1006486, 1006488,
            1003256, 1003257, 1003258, 1003259, 1003260, 1003261,
        };

        private static readonly Dictionary<string, int> ZoneLevel = new Dictionary<string, int> { { "South", 180 }, { "North", 225 }, { "Plateau", 275 } };

        private static readonly Dictionary<string, (long Xp, long Lum)> ZoneReward = new Dictionary<string, (long, long)>
        {
            { "South", (400000000L, 100000L) }, { "North", (800000000L, 200000L) }, { "Plateau", (1200000000L, 300000L) },
        };

        private static readonly string[] OldBountyRows =
        {
            "AunTumerokBountyCount", "CarenziBountyCount", "CrystalBountyCount", "GolemBountyCount", "GromnieBountyCount", "MattekarBountyCount",
            "OlthoiBountyCount", "SiraluunBountyCount", "TuskerBountyCount", "UrsuinBountyCount", "VirindiBountyCount", "ZefirBountyCount",
        };

        private static readonly Regex TagRx = new Regex(
            @"\((?<wcid>\d+),\s+(?<slot>45|49|50),\s+'(?<q>HuntCount_[A-Za-z0-9]+)'\)", RegexOptions.Compiled);

        private static readonly Regex CounterRx = new Regex(@"'(?<q>HuntCount_[A-Za-z0-9]+)(?:@(?<tag>[A-Za-z0-9]+))?'", RegexOptions.Compiled);

        private static readonly Regex NameRx = new Regex(@"\((?<wcid>\d+),\s+1,\s+'(?<n>(?:[^']|'')*)'\)\s*/\* Name \*/", RegexOptions.Compiled);

        private const string EmoteInsert = "INSERT INTO `weenie_properties_emote` (";

        private sealed class Hunter
        {
            public int Wcid;
            public string File;
            public string Text;
            public string Name;
            public string Zone;
            public string Category;
            public string Wait;
            public string Cleared;
            public string Task;
            public List<string> AssignSpecies = new List<string>();
            public List<int> AssignKills = new List<int>();
            public Dictionary<string, int> DoneMin = new Dictionary<string, int>();
            public List<double> GotoSetProbabilities = new List<double>();
        }

        private static string ContentRoot() => Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Content");

        private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

        private static IEnumerable<string> WeenieFiles() => Directory.GetFiles(Path.Combine(ContentRoot(), "sql", "weenies"), "*.sql");

        private static int LeadingWcid(string path)
        {
            // a few committed weenie files have no wcid-then-space name; they are not hunters
            var m = Regex.Match(Path.GetFileName(path), @"^(?<w>\d+)\b");
            return m.Success ? int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture) : -1;
        }

        /// <summary>Every HuntCount_ tag found in PropertyString 45/49/50 of any committed weenie: quest name -> (wcid, slot, file).</summary>
        private static Dictionary<string, List<(int Wcid, int Slot, string File)>> Tags()
        {
            var tags = new Dictionary<string, List<(int, int, string)>>();
            foreach (var f in WeenieFiles())
            {
                var text = Read(f);
                if (!text.Contains("HuntCount_"))
                    continue;
                foreach (Match m in TagRx.Matches(text))
                {
                    var q = m.Groups["q"].Value;
                    if (!tags.TryGetValue(q, out var list))
                        tags[q] = list = new List<(int, int, string)>();
                    list.Add((int.Parse(m.Groups["wcid"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture), Path.GetFileName(f)));
                }
            }
            return tags;
        }

        private static Dictionary<string, (int MinDelta, int MaxSolves)> QuestUnits()
        {
            var rx = new Regex(@"VALUES \('(?<n>[^']+)', (?<d>-?\d+), (?<m>-?\d+),", RegexOptions.Compiled);
            var res = new Dictionary<string, (int, int)>();
            foreach (var f in Directory.GetFiles(Path.Combine(ContentRoot(), "sql", "quests"), "*.sql"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!name.StartsWith("HuntCount_", StringComparison.Ordinal) && !(name.StartsWith("MaraeHunt", StringComparison.Ordinal)))
                    continue;
                var m = rx.Match(Read(f));
                Assert.IsTrue(m.Success, "no quest INSERT parsed in " + f);
                Assert.AreEqual(name, m.Groups["n"].Value, "quest unit name must match its file name: " + f);
                res[name] = (int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture));
            }
            return res;
        }

        private static List<string> EmoteChunks(string text) => text.Split(new[] { EmoteInsert }, StringSplitOptions.None).Skip(1).ToList();

        private static List<Hunter> Hunters()
        {
            var list = new List<Hunter>();
            foreach (var f in WeenieFiles())
            {
                var w = LeadingWcid(f);
                if (w < FirstHunter || w > LastHunter)
                    continue;
                var h = new Hunter { Wcid = w, File = Path.GetFileName(f), Text = Read(f) };
                var nm = NameRx.Match(h.Text);
                Assert.IsTrue(nm.Success, h.File + ": no Name row");
                h.Name = nm.Groups["n"].Value;
                var wm = Regex.Match(h.Text, @"'(?<w>HuntCount_(?<z>South|North|Plateau)(?<c>[A-Za-z]+)Wait)'");
                Assert.IsTrue(wm.Success, h.File + ": no HuntCount_<Zone><Cat>Wait cooldown row referenced");
                h.Wait = wm.Groups["w"].Value;
                h.Zone = wm.Groups["z"].Value;
                h.Category = wm.Groups["c"].Value;
                h.Cleared = "MaraeHunt" + h.Zone + h.Category + "Cleared";
                h.Task = "HuntCount_" + h.Zone + h.Category + "Task";

                // Emote sets are separated by the emote INSERT header; each chunk is one set with its actions.
                foreach (var c in EmoteChunks(h.Text))
                {
                    var head = Regex.Match(c, @"VALUES \(\d+,\s+(?<cat>\d+) /\* \w+ \*/,\s+(?<p>[0-9.]+),");
                    Assert.IsTrue(head.Success, h.File + ": unparseable emote set header");
                    if (head.Groups["cat"].Value == "32")
                    {
                        h.GotoSetProbabilities.Add(double.Parse(head.Groups["p"].Value, CultureInfo.InvariantCulture));
                        var all = Regex.Matches(c, @"/\* SetQuestCompletions \*/, 0, 1, NULL, '(?<q>HuntCount_[A-Za-z0-9]+)'");
                        foreach (Match m in all)
                        {
                            if (m.Groups["q"].Value != h.Task)
                                h.AssignSpecies.Add(m.Groups["q"].Value);
                        }
                        var slay = Regex.Match(c, @"'Slay (?<n>\d+) ");
                        Assert.IsTrue(slay.Success, h.File + ": assign set has no Slay N line");
                        h.AssignKills.Add(int.Parse(slay.Groups["n"].Value, CultureInfo.InvariantCulture));
                    }
                }
                foreach (Match m in Regex.Matches(h.Text, @"/\* InqQuestSolves \*/, 0, 1, NULL, '(?<q>HuntCount_[A-Za-z0-9]+)@Done', NULL, (?<min>\d+)"))
                    h.DoneMin[m.Groups["q"].Value] = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
                list.Add(h);
            }
            return list;
        }

        private static bool IsSpeciesCounter(string quest) =>
            quest.StartsWith("HuntCount_", StringComparison.Ordinal) && !quest.EndsWith("Task", StringComparison.Ordinal) && !quest.EndsWith("Wait", StringComparison.Ordinal);

        [TestMethod]
        public void TwentySevenHuntersAndSeventyThreeSpecies()
        {
            var hunters = Hunters();
            Assert.AreEqual(LastHunter - FirstHunter + 1, hunters.Count, "hunter weenies found in " + FirstHunter + "-" + LastHunter);
            Assert.AreEqual(ExpectedSpecies, hunters.Sum(h => h.AssignSpecies.Count), "species across all hunters");
            Assert.AreEqual(ExpectedSpecies, hunters.SelectMany(h => h.AssignSpecies).Distinct().Count(), "a species counter must belong to exactly one hunter");
        }

        [TestMethod]
        public void EveryCounterQuestUnitIsCarriedByItsOriginalAndExactlyItsListedCopies()
        {
            var tags = Tags();
            var units = QuestUnits();
            var counters = units.Keys.Where(IsSpeciesCounter).ToList();
            Assert.AreEqual(ExpectedSpecies, counters.Count, "HuntCount_ species quest units");

            foreach (var q in counters)
            {
                Assert.IsTrue(tags.TryGetValue(q, out var carriers), q + " is a quest unit that no creature carries in PropertyString 45/49/50, so the hunt could never advance");
                var originals = carriers.Where(c => !CreditedCopies.ContainsKey(c.Wcid)).ToList();
                Assert.AreEqual(1, originals.Count, q + " must be carried by exactly one original weenie, found: " + string.Join(", ", originals.Select(c => c.File)));
                var orig = originals[0];
                var expected = new HashSet<int>(CreditedCopies.Where(kv => kv.Value == orig.Wcid).Select(kv => kv.Key)) { orig.Wcid };
                CollectionAssert.AreEquivalent(expected.OrderBy(x => x).ToList(), carriers.Select(c => c.Wcid).OrderBy(x => x).ToList(),
                    q + ": carriers must be the original plus exactly its listed copies");

                foreach (var c in carriers)
                {
                    var want = SlotThreeWcids.Contains(orig.Wcid) ? 50 : 49;
                    Assert.AreEqual(want, c.Slot, q + " on " + c.File + " must sit in PropertyString " + want);
                }
            }

            // and the reverse: a HuntCount_ tag with no quest unit is a counter the kill path would never find a row for
            foreach (var q in tags.Keys)
                Assert.IsTrue(units.ContainsKey(q), "tag " + q + " has no Content/sql/quests unit");
            Assert.AreEqual(ExpectedSpecies, tags.Count, "distinct HuntCount_ tags on weenies");

            // every credited copy is accounted for: it has a tag, and it names an original that is itself tagged
            Assert.AreEqual(CreditedCopies.Count, tags.Values.SelectMany(v => v).Count(c => CreditedCopies.ContainsKey(c.Wcid)), "credited copies carrying a tag");
        }

        [TestMethod]
        public void EverySameNameCopyOfAHuntTargetIsEitherCreditedOrExplicitlyExcluded()
        {
            var tags = Tags();
            var targetWcids = new HashSet<int>(tags.Values.SelectMany(v => v).Select(c => c.Wcid).Where(w => !CreditedCopies.ContainsKey(w)));
            var nameOf = new Dictionary<int, string>();
            foreach (var f in WeenieFiles())
            {
                var w = LeadingWcid(f);
                if (w < 0)
                    continue;
                var m = NameRx.Match(Read(f));
                if (m.Success && int.Parse(m.Groups["wcid"].Value, CultureInfo.InvariantCulture) == w)
                    nameOf[w] = m.Groups["n"].Value;
            }

            var targetNames = new HashSet<string>(targetWcids.Where(nameOf.ContainsKey).Select(w => nameOf[w]));
            Assert.AreEqual(ExpectedSpecies, targetWcids.Count, "original targets");
            foreach (var kv in nameOf)
            {
                if (!targetNames.Contains(kv.Value) || targetWcids.Contains(kv.Key))
                    continue;
                Assert.IsTrue(CreditedCopies.ContainsKey(kv.Key) || ExcludedCopies.Contains(kv.Key),
                    kv.Key + " '" + kv.Value + "' shares a hunt target's Name but is in neither CreditedCopies nor ExcludedCopies: decide whether kills of it count");
            }

            foreach (var w in ExcludedCopies)
                Assert.IsFalse(tags.Values.SelectMany(v => v).Any(c => c.Wcid == w), w + " is an excluded copy and must not carry a hunt tag");
            foreach (var kv in CreditedCopies)
            {
                Assert.IsTrue(nameOf.TryGetValue(kv.Key, out var cn) && nameOf.TryGetValue(kv.Value, out var on) && cn == on, kv.Key + " must share its original's Name");
            }
        }

        [TestMethod]
        public void HuntTaggedWeeniesHaveNoDuplicateStringProperty()
        {
            var checkedFiles = 0;
            foreach (var f in WeenieFiles())
            {
                var text = Read(f);
                if (!text.Contains("HuntCount_"))
                    continue;
                var block = Regex.Match(text, @"INSERT INTO `weenie_properties_string`[^;]*;", RegexOptions.Singleline);
                Assert.IsTrue(block.Success, Path.GetFileName(f) + ": no weenie_properties_string block");
                var dup = Regex.Matches(block.Value, @"\(\s*(?<o>\d+),\s+(?<t>\d+),\s+'").Cast<Match>()
                    .GroupBy(m => m.Groups["o"].Value + "/" + m.Groups["t"].Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Assert.AreEqual(0, dup.Count, Path.GetFileName(f) + " repeats (object_Id/type): " + string.Join(", ", dup));
                checkedFiles++;
            }
            Assert.IsTrue(checkedFiles >= 121, "control: expected the 73 targets, 21 credited copies and 27 hunters, scanned " + checkedFiles);
        }

        [TestMethod]
        public void EveryHunterAssignsOnlyTaggedSpeciesWithConsistentKillCounts()
        {
            var tags = Tags();
            var units = QuestUnits();
            foreach (var h in Hunters())
            {
                Assert.IsTrue(units.TryGetValue(h.Wait, out var wait), h.File + ": no quest unit for " + h.Wait);
                Assert.AreEqual(WaitMinDelta, wait.MinDelta, h.Wait + " cooldown must be 20 hours");
                Assert.IsTrue(units.TryGetValue(h.Cleared, out var cleared), h.File + ": no quest unit for " + h.Cleared);
                Assert.AreEqual(0, cleared.MinDelta, h.Cleared);
                Assert.AreEqual(-1, cleared.MaxSolves, h.Cleared);
                Assert.IsTrue(units.ContainsKey(h.Task), h.File + ": no quest unit for " + h.Task);
                Assert.AreEqual(0, units[h.Task].MinDelta, h.Task);

                Assert.IsTrue(h.AssignSpecies.Count > 0, h.File + " assigns no species");
                Assert.AreEqual(h.AssignSpecies.Count, h.GotoSetProbabilities.Count, h.File + ": one GotoSet per species");
                Assert.AreEqual(h.AssignSpecies.Count, h.AssignSpecies.Distinct().Count(), h.File + ": a species is assigned twice");
                CollectionAssert.AreEquivalent(h.AssignSpecies, h.DoneMin.Keys.ToList(), h.File + ": the Done-check chain must cover exactly the assigned species");

                for (var i = 0; i < h.AssignSpecies.Count; i++)
                {
                    var q = h.AssignSpecies[i];
                    Assert.IsTrue(q.StartsWith("HuntCount_" + h.Zone + h.Category, StringComparison.Ordinal), h.File + ": " + q + " is outside this hunter's zone/category");
                    Assert.IsTrue(tags.ContainsKey(q), h.File + " assigns " + q + " but no weenie carries that kill tag");
                    Assert.IsTrue(units.ContainsKey(q), h.File + " assigns " + q + " but there is no quest unit");
                    Assert.AreEqual(units[q].MaxSolves, h.DoneMin[q], q + ": the Done minimum must equal the quest's max_Solves");
                    Assert.AreEqual(units[q].MaxSolves, h.AssignKills[i], q + ": the 'Slay N' text must equal the quest's max_Solves");
                    Assert.IsTrue(units[q].MaxSolves == 50 || units[q].MaxSolves == 20, q + " kill count");

                    // cumulative uniform probabilities, last exactly 1
                    var expect = i == h.AssignSpecies.Count - 1 ? 1.0 : Math.Round((i + 1) / (double)h.AssignSpecies.Count, 6);
                    Assert.AreEqual(expect, h.GotoSetProbabilities[i], 1e-9, h.File + ": GotoSet probability #" + (i + 1));
                }

                // every HuntCount_ name the file mentions at all is its Task row, its Wait row or one of its own species
                foreach (Match m in CounterRx.Matches(h.Text))
                {
                    var q = m.Groups["q"].Value;
                    Assert.IsTrue(q == h.Task || q == h.Wait || h.AssignSpecies.Contains(q), h.File + " references the foreign or misspelled counter " + q);
                }
            }
        }

        [TestMethod]
        public void SparseSpeciesNeedTwentyKillsAndEveryoneElseFifty()
        {
            var tags = Tags();
            var units = QuestUnits();
            var sparse = new HashSet<int> { 1002890, 1003668, 1002900, 1002899 };
            foreach (var kv in tags)
            {
                var wcid = kv.Value.Select(c => c.Wcid).First(w => !CreditedCopies.ContainsKey(w));
                Assert.AreEqual(sparse.Contains(wcid) ? 20 : 50, units[kv.Key].MaxSolves, kv.Key + " (wcid " + wcid + ")");
            }
        }

        [TestMethod]
        public void HuntersHaveNoLevelGateOneUseSetAndAnAbandonPath()
        {
            foreach (var h in Hunters())
            {
                var cats = Regex.Matches(h.Text, @"VALUES \(\d+,\s+(?<cat>\d+) /\* (?<n>\w+) \*/,").Cast<Match>().Select(m => m.Groups["n"].Value).ToList();
                Assert.AreEqual(1, cats.Count(c => c == "Use"), h.File + ": exactly one Use emote set");
                Assert.AreEqual(0, cats.Count(c => c == "TestSuccess" || c == "TestFailure"), h.File + ": the level gate's Test sets must be gone");
                Assert.IsFalse(h.Text.Contains("InqIntStat"), h.File + ": the level gate must be gone");
                Assert.IsTrue(h.Text.Contains("My hunts suit a hunter of about level " + ZoneLevel[h.Zone] + "."), h.File + ": suggested-level line");
                Assert.IsTrue(h.Text.Contains("Return in 20 hours for another hunt."), h.File);

                // the hunter's own Level matches the level it advertises
                var lvl = Regex.Match(h.Text, @"\(" + h.Wcid + @",\s+25,\s+(?<v>\d+)\) /\* Level \*/");
                Assert.IsTrue(lvl.Success, h.File + ": no Level row");
                Assert.AreEqual(ZoneLevel[h.Zone], int.Parse(lvl.Groups["v"].Value, CultureInfo.InvariantCulture), h.File + ": PropertyInt 25 (Level)");

                // class_Name carries the hunter's OWN wcid
                Assert.IsTrue(h.Text.Contains("VALUES (" + h.Wcid + ", 'ace" + h.Wcid + "-"), h.File + ": class_Name must start ace" + h.Wcid + "-");

                // the progress hint names the exact /tell command
                Assert.IsTrue(h.Text.Contains("'To give up this hunt, send me a tell: /tell " + h.Name + ", abandon'"), h.File + ": abandon hint");
                Assert.IsFalse(h.Text.Contains("tell me abandon"), h.File + ": the old abandon hint must be gone");

                // abandon: a ReceiveTalkDirect set keyed 'abandon' that routes through Task@Abandon
                Assert.IsTrue(Regex.IsMatch(h.Text, @"38 /\* ReceiveTalkDirect \*/,\s+1, NULL, NULL, NULL, 'abandon',"), h.File + ": no 'abandon' ReceiveTalkDirect set");
                var chunks = EmoteChunks(h.Text);
                var abandonOk = chunks.First(c => c.Contains("'" + h.Task + "@Abandon'") && c.Contains("12 /* QuestSuccess */"));
                foreach (var s in h.AssignSpecies)
                    Assert.IsTrue(abandonOk.Contains("'" + s + "'"), h.File + ": abandon must erase " + s);
                Assert.IsTrue(abandonOk.Contains("/* EraseQuest */, 0, 1, NULL, '" + h.Task + "'"), h.File + ": abandon must erase the Task row");
                Assert.IsTrue(abandonOk.Contains("/* StampQuest */, 0, 1, NULL, '" + h.Wait + "'"), h.File + ": abandon must stamp the cooldown");
                Assert.IsTrue(abandonOk.Contains("So be it. The hunt is called off. Come back in 20 hours."), h.File);

                // B: abandon pays no stamp - everything it stamps is a HuntCount_ row (an excluded quest-stamp prefix)
                var abandonStamps = Regex.Matches(abandonOk, @"/\* StampQuest \*/, 0, 1, NULL, '(?<q>[^']+)'").Cast<Match>().Select(m => m.Groups["q"].Value).ToList();
                Assert.IsTrue(abandonStamps.Count > 0 && abandonStamps.All(q => q.StartsWith("HuntCount_", StringComparison.Ordinal)),
                    h.File + ": the abandon set stamps a name outside HuntCount_: " + string.Join(", ", abandonStamps));
                Assert.IsFalse(abandonOk.Contains(h.Cleared), h.File + ": abandon must never touch the Cleared row");

                // B: the reward set stamps exactly the Cleared row plus the Wait row
                var rewards = chunks.Where(c => c.Contains("/* AwardNoShareXP */")).ToList();
                Assert.AreEqual(h.AssignSpecies.Count, rewards.Count, h.File + ": one reward set per species");
                foreach (var r in rewards)
                {
                    var stamps = Regex.Matches(r, @"/\* StampQuest \*/, 0, 1, NULL, '(?<q>[^']+)'").Cast<Match>().Select(m => m.Groups["q"].Value).OrderBy(x => x).ToList();
                    CollectionAssert.AreEqual(new[] { h.Cleared, h.Wait }.OrderBy(x => x).ToList(), stamps, h.File + ": reward set must stamp exactly the Cleared and Wait rows");

                    // H(b): the reward amounts per zone
                    var xp = Regex.Match(r, @"/\* AwardNoShareXP \*/, 0, 1,(?: NULL,){12} (?<v>\d+),");
                    var lum = Regex.Match(r, @"/\* AwardLuminance \*/, 0, 1,(?: NULL,){12} (?<v>\d+),");
                    Assert.IsTrue(xp.Success && lum.Success, h.File + ": unparseable reward amounts");
                    Assert.AreEqual(ZoneReward[h.Zone].Xp, long.Parse(xp.Groups["v"].Value, CultureInfo.InvariantCulture), h.File + ": XP");
                    Assert.AreEqual(ZoneReward[h.Zone].Lum, long.Parse(lum.Groups["v"].Value, CultureInfo.InvariantCulture), h.File + ": Luminance");
                }

                // E: the cooldown reply is ONE line
                var cooldown = chunks.Single(c => Regex.IsMatch(c, @"VALUES \(\d+,\s+12 /\* QuestSuccess \*/,\s+1, NULL, NULL, NULL, '" + h.Wait + "',"));
                Assert.AreEqual(1, Regex.Matches(cooldown, @"\(@parent_id,").Count, h.File + ": the cooldown reply must be a single action");
                Assert.IsTrue(cooldown.Contains("'Your hunt for today is done. You may take another hunt in %tqt.'"), h.File + ": cooldown text");

                // C: EVERY hunter clears the retired hub's rows at the start of Use
                var use = chunks.Single(c => Regex.IsMatch(c, @"VALUES \(\d+,\s+7 /\* Use \*/"));
                foreach (var row in OldBountyRows)
                    Assert.IsTrue(use.Contains("/* EraseQuest */, 0, 1, NULL, '" + row + "'"), h.File + ": Use must erase " + row);
            }
        }

        [TestMethod]
        public void PortalsRouteWithoutLevelOrQuestGates()
        {
            var seen = 0;
            foreach (var f in WeenieFiles())
            {
                var w = LeadingWcid(f);
                if (w < 1008177 || w > 1008179)
                    continue;
                seen++;
                var text = Read(f);
                var name = Path.GetFileName(f);
                Assert.IsTrue(Regex.IsMatch(text, @"\(" + w + @",\s+9022,\s+1\)"), name + ": PortalRealm (PropertyInt 9022) must be 1");
                Assert.IsFalse(Regex.IsMatch(text, @"\(" + w + @",\s+37,\s+'"), name + ": no PropertyString 37 (QuestRestriction)");
                Assert.IsFalse(Regex.IsMatch(text, @"\(" + w + @",\s+86,\s+-?\d+\)"), name + ": no PropertyInt 86 (MinLevel)");
            }
            Assert.AreEqual(3, seen, "portal weenies 1008177-1008179");
        }

        [TestMethod]
        public void EveryPlacementRowInTheHuntUnitsHasAUnitQuaternion()
        {
            // an all-zero rotation is not a rotation: spawncheck gives a different z for (0,0,0,0) than for (1,0,0,0), and the server would normalise garbage
            var units = new Dictionary<string, int>
            {
                { "marae_hunt_hamlet", 11 }, { "marae_hunt_bluespire_portals", 3 }, { "marae_hunt_north_camp", 25 }, { "marae_hunt_plateau_camp", 20 },
            };
            var rx = new Regex(
                @"\(1, 0x[0-9A-F]{8}, \d+, 0x[0-9A-F]{8}, -?[\d.]+, -?[\d.]+, -?[\d.]+, (?<w>-?[\d.]+), (?<x>-?[\d.]+), (?<y>-?[\d.]+), (?<z>-?[\d.]+), 0\)",
                RegexOptions.Compiled);
            foreach (var kv in units)
            {
                var matches = rx.Matches(Read(Path.Combine(ContentRoot(), "placements", kv.Key + ".sql")));
                Assert.AreEqual(kv.Value, matches.Count, kv.Key + ": placement rows parsed");
                foreach (Match m in matches)
                {
                    double q(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
                    var norm = Math.Sqrt(q("w") * q("w") + q("x") * q("x") + q("y") * q("y") + q("z") * q("z"));
                    Assert.IsTrue(Math.Abs(norm - 1.0) <= 0.01, kv.Key + ": row " + m.Value + " has |q| = " + norm.ToString("0.0000", CultureInfo.InvariantCulture));
                }
            }
        }

        [TestMethod]
        public void GeneratorSpecsCarryTheSameTagsAsTheCommittedWeenies()
        {
            var root = ClassAbilityAffinityDeclarationTests.RepoRoot();
            var fromSpec = new Dictionary<int, string>();
            foreach (var g in new[] { "generate_weenies.py", "generate_aun.py" })
            {
                var text = Read(Path.Combine(root, "tools", "marae-bestiary", g));
                foreach (Match m in Regex.Matches(text, @"^\s+(?<w>100\d{4}): ""(?<q>HuntCount_[A-Za-z0-9]+)"",", RegexOptions.Multiline))
                    fromSpec[int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture)] = m.Groups["q"].Value;
            }

            // the credited copies are clones derived from their tagged originals (generate_generators.py emit_creature_clone_sql), so they are
            // not in these specs; only the originals are
            var fromFiles = new Dictionary<int, string>();
            foreach (var kv in Tags())
                foreach (var c in kv.Value.Where(c => !CreditedCopies.ContainsKey(c.Wcid)))
                    fromFiles[c.Wcid] = kv.Key;

            CollectionAssert.AreEquivalent(fromFiles.OrderBy(k => k.Key).ToList(), fromSpec.OrderBy(k => k.Key).ToList(),
                "the HUNT_KILL_QUEST tables in tools/marae-bestiary must equal the tags in the committed weenie SQL, or a regeneration drops them");
        }
    }
}
