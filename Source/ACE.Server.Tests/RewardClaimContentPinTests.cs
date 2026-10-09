using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Entity.RewardClaims;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pin for the ClaimRewardOnce (EmoteType 9003) gate: RewardClaimAllowlist.cs is the single
    /// source of which NPCs may run it (the five Assay Row bays and the four Proving Grounds heralds) and
    /// which reward each key guards, but nothing mechanically checked that the SQL in Content/sql/weenies
    /// actually matches that list, or that no other file picked up the emote by copy-paste. This suite
    /// reads the allowlist (never re-hardcodes its keys) and checks, for each entry's NPC file:
    ///   1. exactly one type-9003 action whose message is ordinally the claim key, as many 9003 rows in
    ///      the file as the NPC has keys, and the claim alone in its set (nothing pays before the gate);
    ///      a '&lt;stamp&gt;@claim' herald key is run from TestSuccess '&lt;stamp&gt;@pack', and a SpeedSeason
    ///      key's stamp is one Player.SpeedRewardClaimQuests erases at season rollover;
    ///   2. exactly one TestSuccess(22) set and one TestFailure(23) set, both keyed on that claim key;
    ///   3. the TestSuccess set Gives the allowlisted reward wcid and StampQuests (its own stamp, for a
    ///      herald key);
    ///   4. the TestFailure set never TakeItems/Gives, but still Tells and StampQuests;
    ///   5. the TestFailure set stamps the same quest(s) and Gotos the same target(s) as TestSuccess.
    /// A second test then scans every Content/**/*.sql file and fails if type-9003 shows up anywhere
    /// outside the allowlisted files, so another NPC cannot silently start running this gate. A third
    /// covers the one payout that is deliberately NOT on the allowlist - the Proving Grounds tier-4
    /// commendation, which grants a class ability point and is per character - and pins the stamp-based
    /// bound that stands in place of a claim gate there.
    ///
    /// Pure file + regex parsing - no database, no world, same idiom as PropertyRegistryTests and
    /// WorldContentPlannerTests' live-tree walk.
    /// </summary>
    [TestClass]
    public class RewardClaimContentPinTests
    {
        // EmoteType ids (Source/ACE.Entity/Enum/EmoteType.cs), verified against source, not recalled.
        private const int GiveType = 3;
        private const int TellType = 10;
        private const int StampQuestType = 22;
        private const int GotoType = 67;
        private const int TakeItemsType = 74;
        private const int InqQuestType = 21;
        private const int ClaimRewardOnceType = 9003;

        /// <summary>Herald claim keys are the tier's per-character stamp plus this suffix.</summary>
        private const string ClaimKeySuffix = "@claim";

        // EmoteCategory ids (Source/ACE.Entity/Enum/EmoteCategory.cs).
        private const int TestSuccessCategory = 22;
        private const int TestFailureCategory = 23;

        [TestMethod]
        public void ClaimBlocks_MatchAllowlist()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var weeniesDir = Path.Combine(contentDir, "sql", "weenies");
            Assert.IsTrue(Directory.Exists(weeniesDir), $"Expected {weeniesDir} to exist.");

            var problems = new List<string>();

            foreach (var entry in RewardClaimAllowlist.Entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var file = FindNpcWeenieFile(weeniesDir, entry.NpcWcid);

                if (file == null)
                {
                    problems.Add($"{entry.Key}: no file under {weeniesDir} starts with '{entry.NpcWcid} '.");
                    continue;
                }

                var relative = Path.GetRelativePath(contentDir, file).Replace('\\', '/');
                var sets = ParseEmoteSets(file);
                var allActions = sets.SelectMany(s => s.Actions).ToList();

                // 1. exactly one ClaimRewardOnce (9003) action whose message is this key, and the file holds
                //    exactly as many 9003 rows as this NPC has allowlist entries (no stray or mistyped key).
                var claimRows = allActions.Where(a => a.Type == ClaimRewardOnceType && string.Equals(a.Message, entry.Key, StringComparison.Ordinal)).ToList();
                var npcEntryCount = RewardClaimAllowlist.Entries.Values.Count(e => e.NpcWcid == entry.NpcWcid);
                var fileClaimCount = allActions.Count(a => a.Type == ClaimRewardOnceType);

                if (claimRows.Count != 1)
                    problems.Add($"{entry.Key} ({relative}): expected exactly one type-9003 (ClaimRewardOnce) action row with message '{entry.Key}', found {claimRows.Count}.");

                if (fileClaimCount != npcEntryCount)
                    problems.Add($"{entry.Key} ({relative}): the file has {fileClaimCount} type-9003 rows but the allowlist lists {npcEntryCount} key(s) for npc {entry.NpcWcid}.");

                // 1b. the set holding the claim runs NOTHING else, so nothing can pay before the gate decides.
                var claimSets = sets.Where(s => s.Actions.Any(a => a.Type == ClaimRewardOnceType && string.Equals(a.Message, entry.Key, StringComparison.Ordinal))).ToList();

                foreach (var claimSet in claimSets)
                {
                    if (claimSet.Actions.Count != 1)
                        problems.Add($"{entry.Key} ({relative}): the set running ClaimRewardOnce ('{claimSet.Quest}', category {claimSet.Category}) has {claimSet.Actions.Count} actions; the claim must be its only action.");
                }

                // 1c. herald keys are '<stamp>@claim', run from TestSuccess '<stamp>@pack'.
                string stamp = null;

                if (entry.Key.EndsWith(ClaimKeySuffix, StringComparison.Ordinal))
                {
                    stamp = entry.Key.Substring(0, entry.Key.Length - ClaimKeySuffix.Length);

                    if (claimSets.Count != 1 || claimSets[0].Category != TestSuccessCategory || !string.Equals(claimSets[0].Quest, stamp + "@pack", StringComparison.Ordinal))
                        problems.Add($"{entry.Key} ({relative}): ClaimRewardOnce must be run from exactly one TestSuccess(22) set with quest '{stamp}@pack'.");
                }

                // 1d. a SpeedSeason key's stamp must be one the season rollover erases, or the per-character
                //     tier would never re-open while the IP gate does (or the reverse).
                if (entry.Period == ClaimPeriod.SpeedSeason && (stamp == null || !ReadSpeedRewardClaimQuests().Contains(stamp, StringComparer.Ordinal)))
                    problems.Add($"{entry.Key} ({relative}): a SpeedSeason key must be '<stamp>@claim' with <stamp> in Player.SpeedRewardClaimQuests.");

                // 2. exactly one TestSuccess(22) set and one TestFailure(23) set keyed on the claim key
                var successSets = sets.Where(s => s.Category == TestSuccessCategory && string.Equals(s.Quest, entry.Key, StringComparison.Ordinal)).ToList();
                var failureSets = sets.Where(s => s.Category == TestFailureCategory && string.Equals(s.Quest, entry.Key, StringComparison.Ordinal)).ToList();

                if (successSets.Count != 1)
                    problems.Add($"{entry.Key} ({relative}): expected exactly one TestSuccess(22) set with quest '{entry.Key}', found {successSets.Count}.");

                if (failureSets.Count != 1)
                    problems.Add($"{entry.Key} ({relative}): expected exactly one TestFailure(23) set with quest '{entry.Key}', found {failureSets.Count}.");

                // 3. TestSuccess: Gives the allowlisted reward wcid, and StampQuests
                if (successSets.Count == 1)
                {
                    var success = successSets[0];

                    if (!success.Actions.Any(a => a.Type == GiveType && a.WeenieClassId == entry.RewardWcid))
                        problems.Add($"{entry.Key} ({relative}): the TestSuccess set does not Give wcid {entry.RewardWcid}.");

                    if (!success.Actions.Any(a => a.Type == StampQuestType))
                        problems.Add($"{entry.Key} ({relative}): the TestSuccess set does not StampQuest (22).");

                    if (stamp != null && !success.Actions.Any(a => a.Type == StampQuestType && string.Equals(a.Message, stamp, StringComparison.Ordinal)))
                        problems.Add($"{entry.Key} ({relative}): the TestSuccess set does not StampQuest '{stamp}'.");
                }

                // 5. TestFailure closes the SAME stamp(s) the paying branch closes and continues to the SAME
                //    Goto target(s), so a denied tier neither loops nor strands a multi-tier visit.
                if (successSets.Count == 1 && failureSets.Count == 1)
                {
                    var successStamps = successSets[0].Actions.Where(a => a.Type == StampQuestType).Select(a => a.Message).ToList();
                    var failureStamps = failureSets[0].Actions.Where(a => a.Type == StampQuestType).Select(a => a.Message).ToList();
                    var successGotos = successSets[0].Actions.Where(a => a.Type == GotoType).Select(a => a.Message).ToList();
                    var failureGotos = failureSets[0].Actions.Where(a => a.Type == GotoType).Select(a => a.Message).ToList();

                    if (!successStamps.SequenceEqual(failureStamps, StringComparer.Ordinal))
                        problems.Add($"{entry.Key} ({relative}): TestFailure stamps [{string.Join(", ", failureStamps)}] but TestSuccess stamps [{string.Join(", ", successStamps)}].");

                    if (!successGotos.SequenceEqual(failureGotos, StringComparer.Ordinal))
                        problems.Add($"{entry.Key} ({relative}): TestFailure Gotos [{string.Join(", ", failureGotos)}] but TestSuccess Gotos [{string.Join(", ", successGotos)}].");
                }

                // 4. TestFailure: no TakeItems, no Give, but Tells and StampQuests
                if (failureSets.Count == 1)
                {
                    var failure = failureSets[0];

                    if (failure.Actions.Any(a => a.Type == TakeItemsType))
                        problems.Add($"{entry.Key} ({relative}): the TestFailure set must not TakeItems (74), it does.");

                    if (failure.Actions.Any(a => a.Type == GiveType))
                        problems.Add($"{entry.Key} ({relative}): the TestFailure set must not Give (3), it does.");

                    if (!failure.Actions.Any(a => a.Type == TellType))
                        problems.Add($"{entry.Key} ({relative}): the TestFailure set does not Tell (10).");

                    if (!failure.Actions.Any(a => a.Type == StampQuestType))
                        problems.Add($"{entry.Key} ({relative}): the TestFailure set does not StampQuest (22).");
                }
            }

            Assert.IsTrue(problems.Count == 0,
                "ClaimRewardOnce claim blocks do not match RewardClaimAllowlist:\n  " + string.Join("\n  ", problems));
        }

        [TestMethod]
        public void ClaimRewardOnce_UsedOnlyByAllowlistedNpcs()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var weeniesDir = Path.Combine(contentDir, "sql", "weenies");
            var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in RewardClaimAllowlist.Entries.Values)
            {
                var file = FindNpcWeenieFile(weeniesDir, entry.NpcWcid);

                if (file != null)
                    allowedFiles.Add(Path.GetFullPath(file));
            }

            Assert.AreEqual(RewardClaimAllowlist.Entries.Values.Select(e => e.NpcWcid).Distinct().Count(), allowedFiles.Count,
                "Could not resolve one file per allowlisted NPC - see ClaimBlocks_MatchAllowlist for detail.");

            var violations = new List<string>();

            foreach (var file in Directory.EnumerateFiles(contentDir, "*.sql", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);

                // Cheap pre-filter: a file with no "9003" substring anywhere cannot contain a type-9003 row.
                if (text.IndexOf("9003", StringComparison.Ordinal) < 0)
                    continue;

                var sets = ParseEmoteSetsFromText(text);
                var hasClaim = sets.SelectMany(s => s.Actions).Any(a => a.Type == ClaimRewardOnceType);

                if (!hasClaim)
                    continue;

                if (!allowedFiles.Contains(Path.GetFullPath(file)))
                    violations.Add(Path.GetRelativePath(contentDir, file).Replace('\\', '/'));
            }

            Assert.IsTrue(violations.Count == 0,
                "ClaimRewardOnce (EmoteType 9003) must be used ONLY by the NPC files RewardClaimAllowlist names. Found it also in:\n  " +
                string.Join("\n  ", violations));
        }

        /// <summary>
        /// The Proving Grounds tier-4 payouts hand over the only NPC-given item in this content that
        /// carries a class ability point (1000304, ClassAbilityPointValue 1), and the owner's 2026-10-02
        /// ruling is that the point is earned PER CHARACTER: the MMD tiers 1-3 keep their once-per-account,
        /// once-per-IP claim gate, and tier 4 has none. That removed the only account-scoped bound on a CAP
        /// grant, so this pins the per-character bound that is now the whole of it. Four parts, and all
        /// four have to hold:
        ///   1. nothing on RewardClaimAllowlist guards the commendation and no tier-4 key is listed, so
        ///      the account/IP cap the ruling removed has not come back;
        ///   2. the herald's tier-4 '@pack' TestSuccess set is itself the paying body - it Gives 1000304
        ///      and StampQuests the tier stamp in the SAME set, and runs no ClaimRewardOnce;
        ///   3. the emote chain asks InqQuest on that same stamp, which is what makes the stamp a gate at
        ///      all (EmoteManager's InqQuest case sets success = hasQuest &amp;&amp; !canSolve, and only the
        ///      QuestFailure branch walks on toward the score and pack checks and the payout);
        ///   4. the quest row carries max_Solves 1, so once the stamp is on the character CanSolve is
        ///      false forever (QuestManager.GetNextSolveTime returns TimeSpan.MaxValue as soon as
        ///      NumTimesCompleted reaches MaxSolves).
        /// Break 2, 3 or 4 and tier-4 CAP is unbounded - the tier pays another point on every visit.
        /// </summary>
        [TestMethod]
        public void ProvingGroundsTier4_PaysPerCharacter_AndIsBoundedByItsOwnQuestStamp()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var weeniesDir = Path.Combine(contentDir, "sql", "weenies");
            var questsDir = Path.Combine(contentDir, "sql", "quests");
            Assert.IsTrue(Directory.Exists(weeniesDir), $"Expected {weeniesDir} to exist.");
            Assert.IsTrue(Directory.Exists(questsDir), $"Expected {questsDir} to exist.");

            var tiers = new (uint Npc, string Stamp)[]
            {
                (RewardClaimAllowlist.MasterAtArmsOrdwinCray, "ProvingGroundsAttackTier4"),
                (RewardClaimAllowlist.WardMasterOsricCray, "ProvingGroundsDefenseTier4"),
                (RewardClaimAllowlist.FieldMarshalOswynCray, "ProvingGroundsWaveTier4"),
            };

            var problems = new List<string>();

            // 1. the ruling itself: no claim entry may guard a CAP-granting reward.
            foreach (var entry in RewardClaimAllowlist.Entries.Values)
            {
                if (entry.RewardWcid == RewardClaimAllowlist.ProvingGroundsCommendation)
                    problems.Add($"allowlist entry '{entry.Key}' guards wcid {entry.RewardWcid}, the Proving Grounds Commendation. That reward grants a class ability point, which is earned per character, so it must not carry an account/IP cap.");
            }

            foreach (var tier in tiers)
            {
                var claimKey = tier.Stamp + ClaimKeySuffix;

                if (RewardClaimAllowlist.Entries.ContainsKey(claimKey))
                    problems.Add($"'{claimKey}' is on RewardClaimAllowlist. Only the MMD tiers 1-3 are claim-gated; tier 4 pays per character.");

                var file = FindNpcWeenieFile(weeniesDir, tier.Npc);

                if (file == null)
                {
                    problems.Add($"{tier.Stamp}: no file under {weeniesDir} starts with '{tier.Npc} '.");
                    continue;
                }

                var relative = Path.GetRelativePath(contentDir, file).Replace('\\', '/');
                var sets = ParseEmoteSets(file);
                var allActions = sets.SelectMany(s => s.Actions).ToList();

                // 2. the '@pack' TestSuccess set pays: Give and StampQuest together, no claim step.
                var paySets = sets
                    .Where(s => s.Category == TestSuccessCategory && string.Equals(s.Quest, tier.Stamp + "@pack", StringComparison.Ordinal))
                    .ToList();

                if (paySets.Count != 1)
                {
                    problems.Add($"{tier.Stamp} ({relative}): expected exactly one TestSuccess set keyed '{tier.Stamp}@pack', found {paySets.Count}.");
                }
                else
                {
                    var pay = paySets[0];

                    if (!pay.Actions.Any(a => a.Type == GiveType && a.WeenieClassId == RewardClaimAllowlist.ProvingGroundsCommendation))
                        problems.Add($"{tier.Stamp} ({relative}): TestSuccess '{tier.Stamp}@pack' must Give wcid {RewardClaimAllowlist.ProvingGroundsCommendation} itself, since no claim set pays for it any more.");

                    if (!pay.Actions.Any(a => a.Type == StampQuestType && string.Equals(a.Message, tier.Stamp, StringComparison.Ordinal)))
                        problems.Add($"{tier.Stamp} ({relative}): TestSuccess '{tier.Stamp}@pack' must StampQuest '{tier.Stamp}' in the same set as the Give. With no stamp the InqQuest gate never closes and the tier pays another class ability point on every visit.");

                    if (pay.Actions.Any(a => a.Type == ClaimRewardOnceType))
                        problems.Add($"{tier.Stamp} ({relative}): TestSuccess '{tier.Stamp}@pack' runs ClaimRewardOnce. Tier 4 must not be claim-gated.");
                }

                if (allActions.Any(a => a.Type == ClaimRewardOnceType && string.Equals(a.Message, claimKey, StringComparison.Ordinal)))
                    problems.Add($"{tier.Stamp} ({relative}): a type-9003 action still names '{claimKey}', which is no longer allowlisted. An unlisted key passes straight through to TestSuccess and is logged at error level as a content bug.");

                // 3. the chain asks InqQuest on the stamp.
                if (!allActions.Any(a => a.Type == InqQuestType && string.Equals(a.Message, tier.Stamp, StringComparison.Ordinal)))
                    problems.Add($"{tier.Stamp} ({relative}): no InqQuest action asks '{tier.Stamp}'. That question is the only thing standing between a stamped character and a second payout.");

                // 4. max_Solves 1 on the quest row.
                var questFile = Path.Combine(questsDir, tier.Stamp + ".sql");

                if (!File.Exists(questFile))
                {
                    problems.Add($"{tier.Stamp}: expected the quest definition at {questFile}.");
                    continue;
                }

                var maxSolves = ReadQuestMaxSolves(questFile, tier.Stamp);
                var shown = maxSolves.HasValue ? maxSolves.Value.ToString(CultureInfo.InvariantCulture) : "absent";

                if (maxSolves != 1)
                    problems.Add($"{tier.Stamp} (sql/quests/{tier.Stamp}.sql): max_Solves is {shown}, expected 1. Anything else lets a stamped character solve the tier again and collect another class ability point.");
            }

            Assert.IsTrue(problems.Count == 0,
                "Proving Grounds tier 4 pays per character and its own quest stamp is the whole bound:\n  " +
                string.Join("\n  ", problems));
        }

        // ---- Player.SpeedRewardClaimQuests, read from source ---------------------------
        //
        // Touching Player in this host runs its static initializer, which reaches WorldDbContext and throws,
        // so the array is read from Player_SpeedChallenge.cs instead: the initializer bound to that exact
        // field name, every string literal inside its braces.

        private static List<string> speedRewardClaimQuests;

        private static List<string> ReadSpeedRewardClaimQuests()
        {
            if (speedRewardClaimQuests != null)
                return speedRewardClaimQuests;

            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, "Could not locate the repo's Content/ directory.");

            var source = Path.Combine(Path.GetDirectoryName(contentDir), "Source", "ACE.Server", "WorldObjects", "Player_SpeedChallenge.cs");
            Assert.IsTrue(File.Exists(source), $"Expected {source} to exist.");

            var match = Regex.Match(File.ReadAllText(source), @"\bstring\[\]\s+SpeedRewardClaimQuests\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
            Assert.IsTrue(match.Success, $"Could not find the SpeedRewardClaimQuests initializer in {source}.");

            speedRewardClaimQuests = Regex.Matches(match.Groups["body"].Value, "\"(?<name>[^\"]+)\"").Select(m => m.Groups["name"].Value).ToList();
            Assert.IsTrue(speedRewardClaimQuests.Count > 0, "SpeedRewardClaimQuests parsed as empty.");

            return speedRewardClaimQuests;
        }

        // ---- file lookup -------------------------------------------------------------

        private static string FindNpcWeenieFile(string weeniesDir, uint npcWcid)
        {
            var prefix = npcWcid.ToString(CultureInfo.InvariantCulture) + " ";

            var matches = Directory.EnumerateFiles(weeniesDir, "*.sql")
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        /// <summary>
        /// Walks up from the test assembly location looking for a sibling "Content" directory that actually
        /// holds .sql files. Same idiom as WorldContentPlannerTests.FindRepoContentDir and
        /// PropertyRegistryTests.FindInSourceTree - robust to the bin/x64/Debug/netX nesting and to running
        /// inside a git worktree.
        /// </summary>
        private static string FindContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content");

                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.sql", SearchOption.AllDirectories).Any())
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        // ---- minimal SQL parsing -------------------------------------------------------
        //
        // Not a general SQL parser - just enough to read this repo's weenie emote INSERT statements:
        // strip comments, split the file into top-level (outside string literals / parens) ';'
        // statements, and for weenie_properties_emote(_action) statements read the column list from
        // the INSERT header so field positions are looked up BY NAME rather than assumed.

        private sealed class EmoteAction
        {
            public int Type;
            public string Message;
            public uint? WeenieClassId;
        }

        private sealed class EmoteSet
        {
            public int Category;
            public string Quest;
            public List<EmoteAction> Actions = new List<EmoteAction>();
        }

        private static List<EmoteSet> ParseEmoteSets(string filePath) => ParseEmoteSetsFromText(File.ReadAllText(filePath));

        private static List<EmoteSet> ParseEmoteSetsFromText(string rawText)
        {
            // Strip comments first so neither a semicolon nor a stray quote inside a header prose
            // comment can throw off statement splitting below.
            var text = Regex.Replace(rawText, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            text = Regex.Replace(text, @"--[^\n]*", string.Empty);

            var sets = new List<EmoteSet>();
            EmoteSet currentSet = null;

            foreach (var rawStatement in SplitTopLevel(text, ';'))
            {
                var statement = rawStatement.Trim();

                var header = Regex.Match(statement, @"^INSERT\s+INTO\s+`(?<table>[a-zA-Z_]+)`\s*\(", RegexOptions.IgnoreCase);

                if (!header.Success)
                    continue;

                var table = header.Groups["table"].Value;

                if (table != "weenie_properties_emote" && table != "weenie_properties_emote_action")
                    continue;

                var openIndex = header.Index + header.Length - 1;
                var columnsText = ReadParenGroup(statement, openIndex, out var afterColumns);
                var columns = SplitTopLevel(columnsText, ',').Select(c => c.Trim().Trim('`')).ToList();
                var colIndex = new Dictionary<string, int>(StringComparer.Ordinal);

                for (var i = 0; i < columns.Count; i++)
                    colIndex[columns[i]] = i;

                var valuesIndex = statement.IndexOf("VALUES", afterColumns, StringComparison.OrdinalIgnoreCase);

                if (valuesIndex < 0)
                    continue;

                var pos = valuesIndex + "VALUES".Length;

                while (true)
                {
                    var openTuple = statement.IndexOf('(', pos);

                    if (openTuple < 0)
                        break;

                    var tupleText = ReadParenGroup(statement, openTuple, out var afterTuple);
                    var fields = SplitTopLevel(tupleText, ',').Select(f => f.Trim()).ToList();

                    if (table == "weenie_properties_emote")
                    {
                        // Not every emote insert in this repo carries a 'quest' column - short archetypes
                        // (e.g. bare Use/Vendor sets) omit it entirely, which is not NULL, it is absent.
                        currentSet = new EmoteSet
                        {
                            Category = ParseIntNonNull(fields[colIndex["category"]]),
                            Quest = colIndex.TryGetValue("quest", out var questIdx) ? ParseStringLiteral(fields[questIdx]) : null,
                        };

                        sets.Add(currentSet);
                    }
                    else
                    {
                        // Same reasoning: 'message' and 'weenie_Class_Id' are both absent (not NULL) from
                        // several shorter emote_action column lists used elsewhere in the content tree.
                        var action = new EmoteAction
                        {
                            Type = ParseIntNonNull(fields[colIndex["type"]]),
                            Message = colIndex.TryGetValue("message", out var messageIdx) ? ParseStringLiteral(fields[messageIdx]) : null,
                            WeenieClassId = colIndex.TryGetValue("weenie_Class_Id", out var wcidIdx) ? ParseNullableUInt(fields[wcidIdx]) : null,
                        };

                        currentSet?.Actions.Add(action);
                    }

                    pos = afterTuple;
                }
            }

            return sets;
        }

        /// <summary>
        /// Reads the balanced-paren group starting at text[openIndex] (must be '('), respecting single-quoted
        /// string literals (with '' as an escaped quote) so a literal parenthesis or comma inside a message
        /// never breaks the split. Returns the inner content (parens stripped) and the index just past the
        /// closing paren.
        /// </summary>
        private static string ReadParenGroup(string text, int openIndex, out int endIndex)
        {
            var depth = 0;
            var inQuotes = false;

            for (var i = openIndex; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuotes)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inQuotes = false;
                    }

                    continue;
                }

                if (c == '\'')
                {
                    inQuotes = true;
                    continue;
                }

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;

                    if (depth == 0)
                    {
                        endIndex = i + 1;
                        return text.Substring(openIndex + 1, i - openIndex - 1);
                    }
                }
            }

            throw new FormatException($"Unbalanced parens starting at index {openIndex}.");
        }

        /// <summary>
        /// Splits on delimiter at depth 0, outside single-quoted string literals ('' escapes a quote).
        /// Used both to split a statement into top-level ';'-terminated pieces and to split a paren
        /// group's contents into fields/columns on ','.
        /// </summary>
        private static List<string> SplitTopLevel(string content, char delimiter)
        {
            var parts = new List<string>();
            var depth = 0;
            var inQuotes = false;
            var start = 0;

            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];

                if (inQuotes)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inQuotes = false;
                    }

                    continue;
                }

                if (c == '\'')
                {
                    inQuotes = true;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (c == delimiter && depth == 0)
                {
                    parts.Add(content.Substring(start, i - start));
                    start = i + 1;
                }
            }

            parts.Add(content.Substring(start));

            return parts;
        }

        /// <summary>
        /// Reads max_Solves for one quest name out of a Content/sql/quests file. The column is looked up BY
        /// NAME from the INSERT header, like the emote parse above, so a column reorder cannot turn this
        /// into a silent pass. Null when the file holds no row for that name.
        /// </summary>
        private static int? ReadQuestMaxSolves(string questFile, string questName)
        {
            var text = Regex.Replace(File.ReadAllText(questFile), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            text = Regex.Replace(text, @"--[^\n]*", string.Empty);

            foreach (var rawStatement in SplitTopLevel(text, ';'))
            {
                var statement = rawStatement.Trim();
                var header = Regex.Match(statement, @"^INSERT\s+INTO\s+`quest`\s*\(", RegexOptions.IgnoreCase);

                if (!header.Success)
                    continue;

                var openIndex = header.Index + header.Length - 1;
                var columnsText = ReadParenGroup(statement, openIndex, out var afterColumns);
                var columns = SplitTopLevel(columnsText, ',').Select(c => c.Trim().Trim('`')).ToList();
                var nameIndex = columns.IndexOf("name");
                var maxSolvesIndex = columns.IndexOf("max_Solves");

                if (nameIndex < 0 || maxSolvesIndex < 0)
                    continue;

                var valuesIndex = statement.IndexOf("VALUES", afterColumns, StringComparison.OrdinalIgnoreCase);

                if (valuesIndex < 0)
                    continue;

                var pos = valuesIndex + "VALUES".Length;

                while (true)
                {
                    var openTuple = statement.IndexOf('(', pos);

                    if (openTuple < 0)
                        break;

                    var tupleText = ReadParenGroup(statement, openTuple, out var afterTuple);
                    var fields = SplitTopLevel(tupleText, ',').Select(f => f.Trim()).ToList();

                    if (maxSolvesIndex < fields.Count && string.Equals(ParseStringLiteral(fields[nameIndex]), questName, StringComparison.Ordinal))
                        return ParseIntNonNull(fields[maxSolvesIndex]);

                    pos = afterTuple;
                }
            }

            return null;
        }

        private static int ParseIntNonNull(string field)
        {
            var trimmed = field.Trim();

            if (!int.TryParse(trimmed, out var value))
                throw new FormatException($"Expected an integer, got: '{field}'.");

            return value;
        }

        private static uint? ParseNullableUInt(string field)
        {
            var trimmed = field.Trim();

            if (trimmed == "NULL")
                return null;

            if (!uint.TryParse(trimmed, out var value))
                throw new FormatException($"Expected a uint or NULL, got: '{field}'.");

            return value;
        }

        private static string ParseStringLiteral(string field)
        {
            var trimmed = field.Trim();

            if (trimmed == "NULL")
                return null;

            if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')
                return trimmed.Substring(1, trimmed.Length - 2).Replace("''", "'");

            throw new FormatException($"Expected a string literal or NULL, got: '{field}'.");
        }
    }
}
