using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the PvP daily/weekly quest CONTENT (quest rows plus the three NPC emote tables) to the server-side
    /// progress engine's catalog, ACE.Server/Pvp/PvpDailyCatalog.cs (PR #1667).
    ///
    /// WHY: the engine advances a counter named PvpDaily&lt;Task&gt;Count only while the player HAS that quest row, and a quest
    /// name with no ace_world.quest row reads as MaxSolves 0 and can never be solved (QuestManager.GetNextSolveTime returns
    /// MaxValue for a missing world row), so a typo or a missing row in the content is a task that silently never moves.
    /// Nothing else in the build notices, because a quest name in an emote row is just a string.
    ///
    /// THE LIST BELOW MUST MATCH PvpDailyCatalog.Tasks (name, target, weekly). It is hardcoded because this content PR is
    /// content-only and branches from master, where the catalog does not exist yet. Once #1667 is on master, replace
    /// <see cref="Tasks"/> with a read of PvpDailyCatalog.Tasks (Task, Target, Weekly) and keep the NPC/reward columns here.
    ///
    /// It reads Content/ directly (no database), locating the repo by walking up from the test assembly, so it must run
    /// in-tree (never with --artifacts-path).
    /// </summary>
    [TestClass]
    public class PvpDailyQuestContentTests
    {
        private const int DailyMinDelta = 72000;
        private const int WeeklyMinDelta = 604800;
        private const int MarksWcid = 1006650;
        private const string PackFullLine = "Your main pack is full. Clear a space and speak to me again.";
        private const string IntroLine = "Take my trials, and every match you fight will count toward them.";
        private const uint VidorianWcid = 1007052;

        // Vidorian also gives the two-item Warmonger's Due reward; every pack check on him asks for 3 free main-pack slots.
        private const int VidorianPackSlots = 3;

        private sealed class TaskDef
        {
            public string Name;
            public string Display;
            public string Accept;
            public int Target;
            public bool Weekly;
            public uint Npc;
            public (uint Wcid, int Stack)[] Rewards;
            public int Slots => Npc == VidorianWcid ? VidorianPackSlots : Rewards.Length;
        }

        private sealed class NpcDef
        {
            public uint Wcid;
            public string File;
            public string Greeting;
            public string Rest;
            public string Giver;
        }

        private static readonly NpcDef[] Npcs =
        {
            new NpcDef { Wcid = 1007052, File = "1007052 Dark Master Vidorian.sql", Greeting = "The Hopeslayer hungers for war. Bring it to him.", Rest = "The war rests. Return when the Hopeslayer calls again.", Giver = ACE.Server.Pvp.PvpDailyCatalog.Vidorian },
            new NpcDef { Wcid = 1007058, File = "1007058 Black Ferah.sql", Greeting = "The sand drinks what we spill. Spill more.", Rest = "The sand is sated, for now. Come back tomorrow.", Giver = ACE.Server.Pvp.PvpDailyCatalog.Ferah },
            new NpcDef { Wcid = 1007057, File = "1007057 Ler Rhan.sql", Greeting = "A hill is only worth what it costs to hold.", Rest = "The hill will wait for you. Come back tomorrow.", Giver = ACE.Server.Pvp.PvpDailyCatalog.LerRhan },
        };

        private static TaskDef T(string name, string display, string accept, int target, bool weekly, uint npc, params (uint, int)[] rewards)
            => new TaskDef { Name = name, Display = display, Accept = accept, Target = target, Weekly = weekly, Npc = npc, Rewards = rewards };

        // Name/Target/Weekly: PvpDailyCatalog.Tasks (PR #1667). Everything else: owner spec 2026-10-15.
        private static readonly TaskDef[] Tasks =
        {
            T("WarmongersDue", "Warmonger's Due", "Warmonger's Due: fight five battlegrounds, King of the Hill or Attack/Defend.", 5, false, 1007052, (1002600, 1), (1004132, 1)),
            T("WeeklyBattlegrounds", "The Long War", "The Long War: fight twenty battlegrounds. I ask it once each week.", 20, true, 1007052, ((uint)MarksWcid, 20)),
            T("BloodInTheSand", "Blood in the Sand", "Blood in the Sand: fight three 1v1 duels.", 3, false, 1007058, ((uint)MarksWcid, 2)),
            T("FirstBlood", "First Blood", "First Blood: win a 1v1 duel.", 1, false, 1007058, ((uint)MarksWcid, 2)),
            T("ProvingGround", "Proving Ground", "Proving Ground: win 1v1 duels against three different opponents.", 3, false, 1007058, ((uint)MarksWcid, 3)),
            T("GladiatorsStreak", "Gladiator's Streak", "Gladiator's Streak: win two 1v1 duels in a row against different opponents.", 2, false, 1007058, ((uint)MarksWcid, 3)),
            T("IntoThePit", "Into the Pit", "Into the Pit: fight three Tugak Brawls.", 3, false, 1007058, ((uint)MarksWcid, 2)),
            T("PitVictor", "Pit Victor", "Pit Victor: win a Tugak Brawl.", 1, false, 1007058, ((uint)MarksWcid, 2)),
            T("Carnage", "Carnage", "Carnage: make five kills in Tugak Brawls.", 5, false, 1007058, ((uint)MarksWcid, 3)),
            T("Bloodbath", "Bloodbath", "Bloodbath: make three kills in a single Tugak Brawl.", 1, false, 1007058, ((uint)MarksWcid, 3)),
            T("HoldTheCrown", "Hold the Crown", "Hold the Crown: fight two King of the Hill matches.", 2, false, 1007057, ((uint)MarksWcid, 4)),
            T("KingsGround", "King's Ground", "King's Ground: stand on the hill for three minutes in total.", 180, false, 1007057, ((uint)MarksWcid, 4)),
            T("Usurper", "Usurper", "Usurper: slay five foes while they stand on the hill.", 5, false, 1007057, ((uint)MarksWcid, 6)),
            T("LastOneStanding", "Last One Standing", "Last One Standing: win a King of the Hill match.", 1, false, 1007057, ((uint)MarksWcid, 6)),
            T("SiegeDuty", "Siege Duty", "Siege Duty: fight two Attack/Defend matches.", 2, false, 1007052, ((uint)MarksWcid, 4)),
            T("CrystalBreaker", "Crystal Breaker", "Crystal Breaker: deal 5,000 damage to an enemy crystal.", 5000, false, 1007052, ((uint)MarksWcid, 4)),
            T("Bulwark", "Bulwark", "Bulwark: slay five foes near your own crystal while defending it.", 5, false, 1007052, ((uint)MarksWcid, 6)),
            T("Shatterpoint", "Shatterpoint", "Shatterpoint: win an Attack/Defend match as an attacker.", 1, false, 1007052, ((uint)MarksWcid, 6)),
            T("Holdfast", "Holdfast", "Holdfast: win an Attack/Defend match as a defender.", 1, false, 1007052, ((uint)MarksWcid, 6)),
        };

        // EmoteType / EmoteCategory ids, verified against ACE.Entity/Enum/EmoteType.cs and EmoteCategory.cs.
        private const int Give = 3, Tell = 10, InqQuest = 21, StampQuest = 22, InqQuestSolves = 30, EraseQuest = 31,
            Goto = 67, SetQuestCompletions = 70, InqPackSpace = 89, PvpDailyStatus = 9004;
        private const int CatUse = 7, CatQuestSuccess = 12, CatQuestFailure = 13, CatTestSuccess = 22, CatTestFailure = 23, CatGotoSet = 32;

        private sealed class EmoteAct
        {
            public int Type;
            public string Message;
            public int? Min, Max, Amount;
            public double Delay;
            public long? Wcid, Stack;
        }

        private sealed class EmoteSet
        {
            public int Category;
            public string Quest;
            public List<EmoteAct> Actions = new List<EmoteAct>();
        }

        [TestMethod]
        public void EveryCatalogCounterAndDoneFlagHasAQuestRow()
        {
            Assert.AreEqual(19, Tasks.Length, "the catalog defines 19 tasks");
            CollectionAssert.AllItemsAreUnique(Tasks.Select(t => t.Name).ToArray());

            foreach (var t in Tasks)
            {
                var count = ReadQuestRow($"PvpDaily{t.Name}Count");
                Assert.AreEqual(0, count.MinDelta, $"PvpDaily{t.Name}Count min_Delta");
                // The engine caps the counter at the target, so MaxSolves must be at least the target; it is exactly the target so
                // the progress line's %tqm token prints the target.
                Assert.AreEqual(t.Target, count.MaxSolves, $"PvpDaily{t.Name}Count max_Solves");
                Assert.IsFalse(string.IsNullOrWhiteSpace(count.Message), $"PvpDaily{t.Name}Count message is player-visible text");

                var done = ReadQuestRow($"PvpDaily{t.Name}Done");
                Assert.AreEqual(t.Weekly ? WeeklyMinDelta : DailyMinDelta, done.MinDelta, $"PvpDaily{t.Name}Done min_Delta");
                Assert.AreEqual(-1, done.MaxSolves, $"PvpDaily{t.Name}Done max_Solves");
                Assert.IsFalse(string.IsNullOrWhiteSpace(done.Message), $"PvpDaily{t.Name}Done message is player-visible text");
            }
        }

        [TestMethod]
        public void EveryTaskIsWiredOnExactlyItsOwnNpc()
        {
            foreach (var npc in Npcs)
            {
                var path = Path.Combine(ContentRoot(), "sql", "weenies", npc.File);
                Assert.IsTrue(File.Exists(path), $"{path} is missing");

                var text = File.ReadAllText(path);
                AssertNoNonAscii(text, npc.File);

                var sets = ParseEmoteSets(text, npc.Wcid);
                Assert.IsTrue(sets.Count > 0, $"{npc.File}: no emote sets parsed (a vacuous pass)");

                var mine = Tasks.Where(t => t.Npc == npc.Wcid).ToArray();
                var allQuestNames = Tasks.SelectMany(t => new[] { $"PvpDaily{t.Name}Count", $"PvpDaily{t.Name}Done" }).ToHashSet();

                // Use: face, greet, start the cooldown ladder at the first task, then the giver's status readout.
                var use = sets.Single(s => s.Category == CatUse);
                Assert.AreEqual(Tell, use.Actions[1].Type);
                Assert.AreEqual(npc.Greeting, use.Actions[1].Message, $"{npc.File}: greeting is verbatim");
                Assert.AreEqual(InqQuest, use.Actions[2].Type);
                Assert.AreEqual($"PvpDaily{mine[0].Name}Done@all", use.Actions[2].Message);
                // The intro line is NOT in Use: it would play before the rest line when every task is on cooldown.
                Assert.AreEqual(4, use.Actions.Count, $"{npc.File}: Use is face, greet, ladder, status");

                // The status readout is the LAST Use action, so it runs once per talk after the ladder/pass has run on every branch
                // (every branch of the ladder and the pass ends inside the nested InqQuest of action 2). It carries a pre-delay so that
                // a pay tail deferred by the Give action's turn-to-target delay has already stamped the Done flag when it reads.
                Assert.AreEqual(PvpDailyStatus, use.Actions[3].Type);
                Assert.AreEqual(npc.Giver, use.Actions[3].Message, $"{npc.File}: status names its own giver");
                Assert.IsTrue(use.Actions[3].Delay >= 1.0, $"{npc.File}: status waits at least a second after the pay tail");
                Assert.AreEqual(1, sets.SelectMany(s => s.Actions).Count(a => a.Type == PvpDailyStatus), $"{npc.File}: exactly one status action in the whole file");
                Assert.IsFalse(sets.Any(s => s.Category == CatQuestSuccess && s.Quest != null && s.Quest.EndsWith("@mid", StringComparison.Ordinal)),
                    $"{npc.File}: the @mid success progress Tells are replaced by the status readout");
                Assert.IsFalse(use.Actions.Any(a => a.Message == IntroLine), $"{npc.File}: intro line must live in the pass");

                // Ladder: success walks to the next task, the last success is the rest line, every failure goes to the pass.
                for (var i = 0; i < mine.Length; i++)
                {
                    var label = $"PvpDaily{mine[i].Name}Done@all";
                    var success = sets.Single(s => s.Category == CatQuestSuccess && s.Quest == label);
                    var failure = sets.Single(s => s.Category == CatQuestFailure && s.Quest == label);

                    if (i < mine.Length - 1)
                        Assert.AreEqual($"PvpDaily{mine[i + 1].Name}Done@all", success.Actions.Single(a => a.Type == InqQuest).Message);
                    else
                        Assert.AreEqual(npc.Rest, success.Actions.Single(a => a.Type == Tell).Message, $"{npc.File}: rest line is verbatim");

                    Assert.AreEqual("PvpDailyPass", failure.Actions.Single(a => a.Type == Goto).Message);
                }

                // The pass inquires every task's Done flag exactly once.
                var pass = sets.Single(s => s.Category == CatGotoSet && s.Quest == "PvpDailyPass");
                // The pass is reached only when at least one task is open, so its first action is the once-per-talk intro line.
                Assert.AreEqual(Tell, pass.Actions[0].Type);
                Assert.AreEqual(IntroLine, pass.Actions[0].Message, $"{npc.File}: intro line leads the pass");
                CollectionAssert.AreEqual(mine.Select(t => $"PvpDaily{t.Name}Done").ToArray(), pass.Actions.Skip(1).Select(a => a.Message).ToArray(), $"{npc.File}: pass order");

                foreach (var t in mine)
                    AssertTaskChain(npc.File, sets, t);

                // Every quest name an emote action references is a catalog name (ignoring the @label), and none is invented.
                foreach (var a in sets.SelectMany(s => s.Actions).Where(a => a.Type == InqQuest || a.Type == InqQuestSolves || a.Type == StampQuest || a.Type == EraseQuest || a.Type == SetQuestCompletions))
                {
                    var bare = a.Message.Split('@')[0];
                    Assert.IsTrue(allQuestNames.Contains(bare), $"{npc.File}: emote references unknown quest name '{a.Message}'");
                }

                // No task of another NPC leaks in.
                foreach (var other in Tasks.Where(t => t.Npc != npc.Wcid))
                    StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex($"PvpDaily{other.Name}(Count|Done)"), $"{npc.File} wires {other.Name}, which belongs to another NPC");
            }
        }

        [TestMethod]
        public void TaskOnCooldownIsSilent_SoAReArmedCounterNeverSpeaksDuringCooldown()
        {
            // QuestSuccess(Done) is InqQuest's "on cooldown" branch. With no set keyed on a bare Done name the NPC says nothing,
            // and the progress, accept and turn-in sets are reachable only through QuestFailure(Done) (eligible), so a counter
            // re-armed at turn-in cannot show progress or re-offer while its task is cooling down.
            foreach (var npc in Npcs)
            {
                var sets = ParseEmoteSets(File.ReadAllText(Path.Combine(ContentRoot(), "sql", "weenies", npc.File)), npc.Wcid);

                foreach (var t in Tasks.Where(x => x.Npc == npc.Wcid))
                {
                    Assert.AreEqual(0, sets.Count(s => s.Category == CatQuestSuccess && s.Quest == $"PvpDaily{t.Name}Done"), $"{npc.File} {t.Name}: a cooldown set would speak");

                    // The only dispatch into the Count@ sets is from QuestFailure(Done) or from another Count@ set.
                    var intoCount = sets.SelectMany(s => s.Actions.Select(a => (Set: s, Act: a)))
                        .Where(x => x.Act.Message != null && x.Act.Message.StartsWith($"PvpDaily{t.Name}Count@", StringComparison.Ordinal) && (x.Act.Type == InqQuestSolves || x.Act.Type == InqPackSpace))
                        .Select(x => x.Set.Category == CatQuestFailure && x.Set.Quest == $"PvpDaily{t.Name}Done" || x.Set.Quest.StartsWith($"PvpDaily{t.Name}Count@", StringComparison.Ordinal))
                        .ToArray();

                    Assert.IsTrue(intoCount.Length >= 2 && intoCount.All(ok => ok), $"{npc.File} {t.Name}: a Count@ set is reachable from outside the eligible branch");
                }
            }
        }

        [TestMethod]
        public void VidorianIsRetailScaleAndTakesTheNormalPlacement()
        {
            var text = File.ReadAllText(Path.Combine(ContentRoot(), "sql", "weenies", "1007052 Dark Master Vidorian.sql"));
            StringAssert.Contains(text, "'Dark Master Vidorian') /* Name */");
            // Owner ruling 2026-10-16: retail 1.1. At 1.65 the server forced him in with SpawnAtExactPosition (9069) but no
            // client drew him, because every scale from 1.15 up fails the room's placement insert (cells --spawncheck).
            StringAssert.Contains(text, "(1007052,  39,     1.1) /* DefaultScale");
            Assert.IsFalse(text.Contains("(1007052, 9069,"), "SpawnAtExactPosition must stay off: it hid him from clients");

            var placement = File.ReadAllText(Path.Combine(ContentRoot(), "placements", "marketplace_pk_room.sql"));
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(placement, @"\n\s+\(1, 0x701F5B\w\w, 1007052,").Count, "exactly one Vidorian placement row");
            StringAssert.Contains(placement, "(1, 0x701F5B07, 1007052, 0x01F50168, 109.983, -47.4815, -29.9945,");
        }

        [TestMethod]
        public void NoUnitOfThisContentIsHeldOffProdOnceThePvpReleaseShips()
        {
            // The #1667 engine ships with the PvP release (#1715), so the dailies, their NPCs and the PK room placement now
            // ride the prod manifest together. They must be released as a set: the PK room placement @depends-on
            // 1007052/57/58, and tools/generate-manifest.sh refuses an included placement whose weenie is excluded.
            var lines = File.ReadAllLines(Path.Combine(ContentRoot(), "prod-manifest-exclude.txt")).Select(l => l.Trim()).ToArray();

            foreach (var entry in new[]
            {
                "Content/sql/quests/PvpDaily*.sql",
                "Content/sql/weenies/1007052 Dark Master Vidorian.sql",
                "Content/sql/weenies/1007057 Ler Rhan.sql",
                "Content/sql/weenies/1007058 Black Ferah.sql",
                "Content/placements/marketplace_pk_room.sql",
            })
                CollectionAssert.DoesNotContain(lines, entry, $"prod-manifest-exclude.txt still holds back {entry}, but the PvP release ships it");
        }

        private static void AssertTaskChain(string file, List<EmoteSet> sets, TaskDef t)
        {
            var f = $"PvpDaily{t.Name}Done";
            var c = $"PvpDaily{t.Name}Count";
            string where = $"{file} {t.Name}";

            EmoteSet One(int cat, string quest)
            {
                var found = sets.Where(s => s.Category == cat && s.Quest == quest).ToArray();
                Assert.AreEqual(1, found.Length, $"{where}: expected exactly one set (category {cat}, quest {quest})");
                return found[0];
            }

            // Eligible (not on cooldown): branch on the counter being full.
            var eligible = One(CatQuestFailure, f).Actions.Single();
            Assert.AreEqual(InqQuestSolves, eligible.Type);
            Assert.AreEqual($"{c}@done", eligible.Message);
            Assert.AreEqual(t.Target, eligible.Min, $"{where}: full means solves >= target");
            Assert.AreEqual(int.MaxValue, eligible.Max);

            // Full: pack-space guard sized to the reward, then give -> stamp -> erase -> tell.
            var guard = One(CatQuestSuccess, $"{c}@done").Actions.Single();
            Assert.AreEqual(InqPackSpace, guard.Type);
            Assert.AreEqual($"{c}@pay", guard.Message);
            Assert.AreEqual(t.Slots, guard.Amount, $"{where}: free slots required");

            var pay = One(CatTestSuccess, $"{c}@pay").Actions;
            var gives = pay.Where(a => a.Type == Give).ToArray();
            Assert.AreEqual(t.Rewards.Length, gives.Length, $"{where}: give count");

            for (var i = 0; i < gives.Length; i++)
            {
                Assert.AreEqual((long)t.Rewards[i].Wcid, gives[i].Wcid, $"{where}: reward wcid #{i}");
                Assert.AreEqual((long)t.Rewards[i].Stack, gives[i].Stack, $"{where}: reward stack #{i}");
            }

            var tail = pay.Skip(gives.Length).ToArray();
            Assert.AreEqual(4, tail.Length, $"{where}: after the gives come exactly StampQuest, EraseQuest, SetQuestCompletions, Tell");
            Assert.AreEqual(StampQuest, tail[0].Type);
            Assert.AreEqual(f, tail[0].Message);
            Assert.AreEqual(EraseQuest, tail[1].Type);
            Assert.AreEqual(c, tail[1].Message);
            // Re-accept at turn-in: the counter is re-armed at an explicit 0 so progress keeps building during the cooldown
            // (the engine advances only HELD quests). It must come after EraseQuest, which deletes the row.
            Assert.AreEqual(SetQuestCompletions, tail[2].Type);
            Assert.AreEqual(c, tail[2].Message);
            Assert.AreEqual(0, tail[2].Amount, $"{where}: re-arm needs an explicit amount 0");
            Assert.AreEqual(Tell, tail[3].Type);
            Assert.AreEqual($"{t.Display} is done.", tail[3].Message);
            Assert.IsTrue(pay.Take(gives.Length).All(a => a.Type == Give), $"{where}: gives come first");

            // A full pack pays nothing and stamps nothing.
            var refuse = One(CatTestFailure, $"{c}@pay").Actions.Single();
            Assert.AreEqual(Tell, refuse.Type);
            Assert.AreEqual(PackFullLine, refuse.Message);

            // Not full: a multi-step counter shows progress when held (1..target-1), a one-step task assigns directly.
            EmoteSet assign;

            if (t.Target > 1)
            {
                var mid = One(CatQuestFailure, $"{c}@done").Actions.Single();
                Assert.AreEqual(InqQuestSolves, mid.Type);
                Assert.AreEqual($"{c}@mid", mid.Message);
                Assert.AreEqual(1, mid.Min);
                Assert.AreEqual(t.Target - 1, mid.Max);

                // In-progress is silent: the status readout at the end of Use carries the numbers, so no @mid success set exists.
                Assert.AreEqual(0, sets.Count(s => s.Category == CatQuestSuccess && s.Quest == $"{c}@mid"), $"{where}: no @mid success Tell");

                assign = One(CatQuestFailure, $"{c}@mid");
            }
            else
            {
                assign = One(CatQuestFailure, $"{c}@done");
            }

            Assert.AreEqual(2, assign.Actions.Count, $"{where}: assign is the accept line then the arming call");
            Assert.AreEqual(Tell, assign.Actions[0].Type);
            Assert.AreEqual(t.Accept, assign.Actions[0].Message, $"{where}: accept line is verbatim");
            Assert.AreEqual(SetQuestCompletions, assign.Actions[1].Type);
            Assert.AreEqual(c, assign.Actions[1].Message);
            // A NULL amount makes SetQuestCompletions a silent no-op (EmoteManager guards on Amount != null), so the counter row
            // would never be created and the engine, which advances only HELD quests, would never move it.
            Assert.AreEqual(0, assign.Actions[1].Amount, $"{where}: SetQuestCompletions needs an explicit amount 0");
        }

        // ---------------------------------------------------------------------------------------------

        private sealed class QuestRow { public int MinDelta, MaxSolves; public string Message; }

        private static QuestRow ReadQuestRow(string name)
        {
            var path = Path.Combine(ContentRoot(), "sql", "quests", name + ".sql");
            Assert.IsTrue(File.Exists(path), $"{name}: no quest unit at Content/sql/quests/{name}.sql");

            var statements = Statements(File.ReadAllText(path));
            var insert = statements.Single(s => s.StartsWith("INSERT INTO `quest`", StringComparison.Ordinal));
            var tuple = Tuples(insert).Single();

            Assert.AreEqual(name, Unquote(tuple[0]), $"{name}: the row inserted is named differently");

            return new QuestRow { MinDelta = int.Parse(tuple[1]), MaxSolves = int.Parse(tuple[2]), Message = Unquote(tuple[3]) };
        }

        private static List<EmoteSet> ParseEmoteSets(string sql, uint wcid)
        {
            var sets = new List<EmoteSet>();
            EmoteSet current = null;

            foreach (var st in Statements(sql))
            {
                if (st.StartsWith("INSERT INTO `weenie_properties_emote` ", StringComparison.Ordinal))
                {
                    var t = Tuples(st).Single();
                    Assert.AreEqual(wcid.ToString(), t[0], "emote header object_Id");
                    current = new EmoteSet { Category = int.Parse(t[1]), Quest = t[6] == "NULL" ? null : Unquote(t[6]) };
                    sets.Add(current);
                }
                else if (st.StartsWith("INSERT INTO `weenie_properties_emote_action` ", StringComparison.Ordinal))
                {
                    Assert.IsNotNull(current, "emote action rows with no preceding emote header");

                    foreach (var t in Tuples(st))
                    {
                        Assert.AreEqual(40, t.Count, "an emote action row has 40 values");
                        current.Actions.Add(new EmoteAct
                        {
                            Type = int.Parse(t[2]),
                            Delay = double.Parse(t[3], System.Globalization.CultureInfo.InvariantCulture),
                            Message = t[6] == "NULL" ? null : Unquote(t[6]),
                            Min = Int(t[8]),
                            Max = Int(t[9]),
                            Amount = Int(t[16]),
                            Wcid = Long(t[27]),
                            Stack = Long(t[28]),
                        });
                    }
                }
            }

            return sets;
        }

        private static int? Int(string v) => v == "NULL" ? (int?)null : int.Parse(v);

        private static long? Long(string v) => v == "NULL" ? (long?)null : long.Parse(v);

        private static string Unquote(string v)
        {
            Assert.IsTrue(v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'', $"expected a quoted string, got {v}");
            return v.Substring(1, v.Length - 2).Replace("''", "'");
        }

        /// <summary>
        /// Removes /* */ and -- comments (outside single-quoted strings) and splits the text into statements on ';' outside
        /// strings. A character scanner, not a regex split: dialogue strings contain commas and apostrophes.
        /// </summary>
        private static List<string> Statements(string sql)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var inString = false;

            for (var i = 0; i < sql.Length; i++)
            {
                var ch = sql[i];

                if (inString)
                {
                    sb.Append(ch);

                    if (ch == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append('\''); i++; }
                        else inString = false;
                    }

                    continue;
                }

                if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    Assert.IsTrue(end >= 0, "unterminated block comment");
                    i = end + 1;
                    continue;
                }

                if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    while (i < sql.Length && sql[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }

                if (ch == '\'') { inString = true; sb.Append(ch); continue; }

                if (ch == ';')
                {
                    var s = sb.ToString().Trim();
                    if (s.Length > 0) result.Add(s);
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);
            }

            Assert.IsFalse(inString, "unterminated string literal");
            var rest = sb.ToString().Trim();
            if (rest.Length > 0) result.Add(rest);

            return result;
        }

        /// <summary>Splits the VALUES clause of one INSERT statement into tuples of trimmed value tokens.</summary>
        private static List<List<string>> Tuples(string insert)
        {
            var at = insert.IndexOf("VALUES", StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, "INSERT without VALUES");

            var tuples = new List<List<string>>();
            List<string> cur = null;
            var sb = new StringBuilder();
            var inString = false;
            var depth = 0;

            for (var i = at + "VALUES".Length; i < insert.Length; i++)
            {
                var ch = insert[i];

                if (inString)
                {
                    sb.Append(ch);

                    if (ch == '\'')
                    {
                        if (i + 1 < insert.Length && insert[i + 1] == '\'') { sb.Append('\''); i++; }
                        else inString = false;
                    }

                    continue;
                }

                if (ch == '\'') { inString = true; sb.Append(ch); continue; }

                if (ch == '(')
                {
                    depth++;
                    if (depth == 1) { cur = new List<string>(); sb.Clear(); continue; }
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0) { cur.Add(sb.ToString().Trim()); tuples.Add(cur); sb.Clear(); cur = null; continue; }
                }
                else if (ch == ',' && depth == 1)
                {
                    cur.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }

                if (depth >= 1) sb.Append(ch);
            }

            Assert.AreEqual(0, depth, "unbalanced parentheses in VALUES");

            return tuples;
        }

        private static void AssertNoNonAscii(string text, string label)
        {
            for (var i = 0; i < text.Length; i++)
                Assert.IsTrue(text[i] < 128, $"{label}: non-ASCII character U+{(int)text[i]:X4} at offset {i}");
        }

        private static string ContentRoot() => Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Content");
    }
}
