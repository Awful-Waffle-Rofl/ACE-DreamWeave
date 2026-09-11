using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards tools/ca-planner/cap-quests.json - the planner's list of quests that hand out a class ability
    /// point (CAP) - against the content that actually hands them out.
    ///
    /// THE GRANT IS DATA-DRIVEN AND THEREFORE INVISIBLE. Gem.ActOnUse reads
    /// PropertyInt.ClassAbilityPointValue off ANY Gem weenie and grants that many points; there is no
    /// per-item code and no registry to add to, so a new CAP-granting item is a single row in a .sql file and
    /// nothing else in the repo mentions it. Without this test, a future CAP source is simply invisible to
    /// the planner. With it, adding one is a red build until cap-quests.json is updated.
    ///
    /// THE SCAN IS SCOPED TO weenie_properties_int ON PURPOSE. The numeric id 9020 is reused across property
    /// TABLES - it is also PropertyBool.WaveChallengeCreature (on the Proving Grounds wave monsters) and
    /// PropertyInt64.BestSurvivalScore (on the Proving Grounds heralds' emote tests). A grep for "9020" over
    /// Content/ matches dozens of rows that have nothing to do with class ability points, so the scan walks
    /// weenie_properties_int INSERT blocks specifically.
    /// </summary>
    [TestClass]
    public class ClassAbilityCapQuestTests
    {
        private const string CapQuestsRelativePath = "tools/ca-planner/cap-quests.json";

        private static readonly int ClassAbilityPointValueId = (int)PropertyInt.ClassAbilityPointValue;

        /// <summary>Matches one "(objectId, type, value)" tuple from a weenie_properties_int VALUES list.</summary>
        private static readonly Regex IntPropertyRow = new Regex(
            @"\(\s*(?<wcid>\d+)\s*,\s*(?<type>\d+)\s*,\s*(?<value>-?\d+)\s*\)", RegexOptions.Compiled);

        private static readonly Regex BlockComment = new Regex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

        private sealed class CapQuest
        {
            public string QuestName;
            public uint Wcid;
            public int Cap;
            public string Label;
        }

        /// <summary>
        /// The set of wcids that carry PropertyInt.ClassAbilityPointValue in Content must be exactly the set
        /// cap-quests.json declares. This is the test that turns "someone shipped a new CAP source" into a
        /// build failure instead of a silent planner gap.
        /// </summary>
        [TestMethod]
        public void CapQuests_DeclareEveryWcidThatGrantsAClassAbilityPoint()
        {
            var declared = LoadCapQuests();
            var found = ScanContentForCapGrantingWcids();

            var declaredWcids = declared.Select(q => q.Wcid).Distinct().OrderBy(w => w).ToArray();
            var foundWcids = found.Keys.OrderBy(w => w).ToArray();

            var missing = foundWcids.Except(declaredWcids).ToArray();
            var stale = declaredWcids.Except(foundWcids).ToArray();

            Assert.AreEqual(0, missing.Length,
                $"Content carries PropertyInt.ClassAbilityPointValue ({ClassAbilityPointValueId}) on wcid(s) " +
                $"{string.Join(", ", missing)}, which {CapQuestsRelativePath} does not declare. " +
                "Add the quest(s) that hand them out, or the planner cannot account for those points.");

            Assert.AreEqual(0, stale.Length,
                $"{CapQuestsRelativePath} declares wcid(s) {string.Join(", ", stale)} that no longer carry " +
                $"PropertyInt.ClassAbilityPointValue in Content.");

            CollectionAssert.AreEqual(foundWcids, declaredWcids);
        }

        /// <summary>
        /// Each declared cap amount matches the value on the weenie, so the planner never prices a grant from
        /// a stale number.
        /// </summary>
        [TestMethod]
        public void CapQuests_DeclareTheAmountTheWeenieActuallyGrants()
        {
            var found = ScanContentForCapGrantingWcids();

            foreach (var quest in LoadCapQuests())
            {
                Assert.IsTrue(found.TryGetValue(quest.Wcid, out var value),
                    $"{quest.QuestName}: wcid {quest.Wcid} carries no ClassAbilityPointValue row in Content.");

                Assert.AreEqual(value, quest.Cap,
                    $"{quest.QuestName}: declares cap {quest.Cap} but wcid {quest.Wcid} grants {value}.");

                Assert.IsTrue(quest.Cap > 0, $"{quest.QuestName}: a cap of {quest.Cap} would never grant anything.");
            }
        }

        /// <summary>
        /// Every declared quest name is a real quest row in Content/sql/quests. A typo here would leave the
        /// planner describing a gate that does not exist.
        /// </summary>
        [TestMethod]
        public void CapQuests_NameRealQuestRows()
        {
            var questsDir = Path.Combine(ContentRoot(), "sql", "quests");

            Assert.IsTrue(Directory.Exists(questsDir), $"{questsDir} does not exist.");

            foreach (var quest in LoadCapQuests())
            {
                var path = Path.Combine(questsDir, quest.QuestName + ".sql");

                Assert.IsTrue(File.Exists(path), $"{quest.QuestName}: no quest unit at Content/sql/quests/{quest.QuestName}.sql.");

                var text = File.ReadAllText(path);

                StringAssert.Contains(text, $"'{quest.QuestName}'",
                    $"Content/sql/quests/{quest.QuestName}.sql does not insert a quest row named {quest.QuestName}.");
            }
        }

        /// <summary>Each (quest, wcid) pair appears once, and every row is fully populated.</summary>
        [TestMethod]
        public void CapQuests_AreWellFormedAndUnique()
        {
            var quests = LoadCapQuests();

            Assert.IsTrue(quests.Count > 0, $"{CapQuestsRelativePath} declares no quests.");

            var keys = quests.Select(q => $"{q.QuestName}|{q.Wcid}").ToArray();

            CollectionAssert.AllItemsAreUnique(keys, "duplicate (questName, wcid) row in cap-quests.json");

            foreach (var quest in quests)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(quest.QuestName), "a row is missing questName");
                Assert.IsFalse(string.IsNullOrWhiteSpace(quest.Label), $"{quest.QuestName} is missing a player-facing label");
                Assert.AreNotEqual(0u, quest.Wcid, $"{quest.QuestName} is missing a wcid");
            }
        }

        // ---------------------------------------------------------------------------------------------

        private static List<CapQuest> LoadCapQuests()
        {
            var path = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "tools", "ca-planner", "cap-quests.json");

            Assert.IsTrue(File.Exists(path), $"{CapQuestsRelativePath} does not exist.");

            JsonNode root;

            try
            {
                root = JsonNode.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                Assert.Fail($"{CapQuestsRelativePath} is not valid JSON: {ex.Message}");
                return null;
            }

            var rows = root["capQuests"];

            Assert.IsNotNull(rows, $"{CapQuestsRelativePath} has no \"capQuests\" array.");

            return rows.AsArray()
                .Select(node => new CapQuest
                {
                    QuestName = (string)node["questName"],
                    Wcid = (uint)node["wcid"],
                    Cap = (int)node["cap"],
                    Label = (string)node["label"],
                })
                .ToList();
        }

        /// <summary>
        /// Every wcid in Content/sql carrying PropertyInt.ClassAbilityPointValue, mapped to the granted
        /// amount. Walks weenie_properties_int INSERT blocks line by line rather than grepping for the bare
        /// id, because 9020 is also a PropertyBool and a PropertyInt64 id (see the class comment).
        /// </summary>
        private static Dictionary<uint, int> ScanContentForCapGrantingWcids()
        {
            var found = new Dictionary<uint, int>();

            var sqlRoot = Path.Combine(ContentRoot(), "sql");

            Assert.IsTrue(Directory.Exists(sqlRoot), $"{sqlRoot} does not exist.");

            foreach (var file in Directory.EnumerateFiles(sqlRoot, "*.sql", SearchOption.AllDirectories))
            {
                var inIntBlock = false;

                foreach (var raw in File.ReadLines(file))
                {
                    if (!inIntBlock)
                    {
                        if (raw.IndexOf("INSERT INTO `weenie_properties_int`", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        inIntBlock = true;

                        // fall through rather than skipping the line: the column list on it cannot match the
                        // all-digits tuple pattern, and a single-line "INSERT ... VALUES (...)" would
                        // otherwise be missed entirely.
                    }

                    var line = BlockComment.Replace(raw, string.Empty);

                    foreach (Match match in IntPropertyRow.Matches(line))
                    {
                        if (int.Parse(match.Groups["type"].Value) != ClassAbilityPointValueId)
                            continue;

                        var wcid = uint.Parse(match.Groups["wcid"].Value);
                        var value = int.Parse(match.Groups["value"].Value);

                        Assert.IsFalse(found.ContainsKey(wcid) && found[wcid] != value,
                            $"wcid {wcid} carries two different ClassAbilityPointValue values in Content ({found.GetValueOrDefault(wcid)} and {value}).");

                        found[wcid] = value;
                    }

                    if (line.TrimEnd().EndsWith(";", StringComparison.Ordinal))
                        inIntBlock = false;
                }
            }

            return found;
        }

        private static string ContentRoot() => Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Content");
    }
}
