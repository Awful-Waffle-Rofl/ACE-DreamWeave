using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The trimmed switch (/facet N trim) and the out-of-reach warning. DESIGN.md section 4.2.
    ///
    /// The XP tables are synthetic and injected, because ACE.Server.Tests has no client dat. Their SHAPE
    /// matches what Player.GetSkillXPTable returns (index i = cumulative experience to hold rank i, index 0
    /// = 0), and the two tables deliberately differ at every rank so a test cannot pass by reading the
    /// wrong one. Every trim test names the plausible wrong implementation it would fail against.
    /// </summary>
    [TestClass]
    public class FacetTrimTests
    {
        private static readonly IReadOnlyList<uint> Trained = new List<uint> { 0, 100, 300, 600, 1000, 1500 };
        private static readonly IReadOnlyList<uint> Specialized = new List<uint> { 0, 50, 150, 300, 500, 750 };

        private static IReadOnlyList<uint> Tables(SkillAdvancementClass sac) => sac switch
        {
            SkillAdvancementClass.Trained => Trained,
            SkillAdvancementClass.Specialized => Specialized,
            _ => null,
        };

        private static FacetSkillEntry Entry(Skill skill, SkillAdvancementClass sac, uint pp, ushort ranks, uint initLevel = 0) => new FacetSkillEntry
        {
            Skill = skill,
            Sac = sac,
            Pp = pp,
            Ranks = ranks,
            InitLevel = initLevel,
        };

        private static void AssertSameFields(FacetSkillEntry expected, FacetSkillEntry actual, string because)
        {
            Assert.AreEqual(expected.Skill, actual.Skill, because);
            Assert.AreEqual(expected.Sac, actual.Sac, because);
            Assert.AreEqual(expected.Ranks, actual.Ranks, because);
            Assert.AreEqual(expected.Pp, actual.Pp, because);
            Assert.AreEqual(expected.InitLevel, actual.InitLevel, because);
        }

        // ==================================================================================
        // FacetPools.TrimSkillsToCover
        // ==================================================================================

        [TestMethod]
        public void Trim_ZeroShortfall_ReturnsAnIdenticalCopy_AndRemovesNothing()
        {
            // Discriminates an implementation that always takes at least one step.
            var incoming = new List<FacetSkillEntry>
            {
                Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 600, 3),
                Entry(Skill.Healing, SkillAdvancementClass.Specialized, 300, 3, 10),
            };

            var result = FacetPools.TrimSkillsToCover(incoming, 0, Tables);

            Assert.IsTrue(result.Covered);
            Assert.AreEqual(0UL, result.Removed);
            Assert.AreEqual(0, result.Deltas.Count);
            Assert.AreEqual(incoming.Count, result.Skills.Count);

            for (var i = 0; i < incoming.Count; i++)
                AssertSameFields(incoming[i], result.Skills[i], "a zero shortfall must change nothing");
        }

        [TestMethod]
        public void Trim_ConservesExperience_AndTheSwitchIsThenFullyPayable()
        {
            // THE invariant. Discriminates any implementation whose reported Removed disagrees with what
            // actually left the build, or which stops short.
            var incoming = new List<FacetSkillEntry>
            {
                Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 1500, 5),
                Entry(Skill.Healing, SkillAdvancementClass.Specialized, 750, 5, 10),
                Entry(Skill.Run, SkillAdvancementClass.Trained, 300, 2),
            };

            const long current = 100;
            const ulong outgoingPp = 450;

            var incomingPp = FacetPools.TotalPp(incoming);
            FacetPools.AvailableExperienceAfterSwap(current, outgoingPp, incomingPp, out var shortfall);

            Assert.AreEqual(2000UL, shortfall, "fixture: 2550 incoming against 550 released");

            var result = FacetPools.TrimSkillsToCover(incoming, shortfall, Tables);
            var trimmedPp = FacetPools.TotalPp(result.Skills);

            Assert.IsTrue(result.Covered);
            Assert.AreEqual(incomingPp - trimmedPp, result.Removed, "Removed must be exactly what left the build");
            Assert.IsTrue(result.Removed >= shortfall);

            var after = FacetPools.AvailableExperienceAfterSwap(current, outgoingPp, trimmedPp, out var afterShortfall);

            Assert.AreEqual(0UL, afterShortfall, "the trimmed switch must be fully payable");
            Assert.AreEqual((long)(result.Removed - shortfall), after, "the only experience left over is the last step's overshoot");
        }

        [TestMethod]
        public void Trim_TakesTheMostExpensiveStepAcrossSkills_NotTheHighestRankedSkill_NorProportionally()
        {
            // Healing is the HIGHER-ranked skill (5) but its top step is only 250; Two Handed Combat is
            // rank 4 with a top step of 400. A 300 shortfall is covered by ONE Two Handed step.
            //   - "trim the highest-ranked skill first" takes Healing twice (250 + 200) and leaves Two
            //     Handed alone;
            //   - "trim proportionally" touches both.
            // Only largest-step greedy leaves Healing untouched.
            var incoming = new List<FacetSkillEntry>
            {
                Entry(Skill.Healing, SkillAdvancementClass.Specialized, 750, 5, 10),
                Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 1000, 4),
            };

            var result = FacetPools.TrimSkillsToCover(incoming, 300, Tables);

            AssertSameFields(incoming[0], result.Skills[0], "Healing's 250 step is smaller than Two Handed's 400 and must not be taken");
            Assert.AreEqual(600u, result.Skills[1].Pp);
            Assert.AreEqual((ushort)3, result.Skills[1].Ranks);
            Assert.AreEqual(400UL, result.Removed);
        }

        [TestMethod]
        public void Trim_ASpecializedEntry_UsesTheSpecializedTable()
        {
            // 300 PP is rank 3 on the specialized table (step down to 150), but rank 2 on the trained table
            // (step down to 100). An implementation that ignored Sac would land on 100 / rank 1.
            var incoming = new List<FacetSkillEntry> { Entry(Skill.WarMagic, SkillAdvancementClass.Specialized, 300, 3, 10) };

            var result = FacetPools.TrimSkillsToCover(incoming, 1, Tables);

            Assert.AreEqual(150u, result.Skills[0].Pp);
            Assert.AreEqual((ushort)2, result.Skills[0].Ranks);
            Assert.AreEqual(150UL, result.Removed);
        }

        [TestMethod]
        public void Trim_ATrainedEntry_UsesTheTrainedTable()
        {
            // The mirror of the specialized case: reading the specialized table here would land on 150 / rank 2.
            var incoming = new List<FacetSkillEntry> { Entry(Skill.WarMagic, SkillAdvancementClass.Trained, 300, 2) };

            var result = FacetPools.TrimSkillsToCover(incoming, 1, Tables);

            Assert.AreEqual(100u, result.Skills[0].Pp);
            Assert.AreEqual((ushort)1, result.Skills[0].Ranks);
            Assert.AreEqual(200UL, result.Removed);
        }

        [TestMethod]
        public void Trim_PartialProgressAboveTheTopRank_GoesWithThatRank()
        {
            // 450 PP is rank 2 (threshold 300) with 150 of partial progress. The step drops to rank 1's
            // threshold, 100, removing 350. Discriminates:
            //   - "remove one rank's cost" (table[2] - table[1] = 200), which would leave 250;
            //   - "sweep only the partial progress", which would leave 300 at rank 2.
            var incoming = new List<FacetSkillEntry> { Entry(Skill.Run, SkillAdvancementClass.Trained, 450, 2) };

            var result = FacetPools.TrimSkillsToCover(incoming, 10, Tables);

            Assert.AreEqual(100u, result.Skills[0].Pp);
            Assert.AreEqual((ushort)1, result.Skills[0].Ranks);
            Assert.AreEqual(350UL, result.Removed);
        }

        [TestMethod]
        public void Trim_PpBeyondTheLastTableEntry_ReachesTheLastRank_AndStepsFromThere()
        {
            // 1700 is past the table's last threshold (1500): rank 5, stepping to rank 4's 1000. An
            // implementation reading table[rank + 1] would index past the end.
            var incoming = new List<FacetSkillEntry> { Entry(Skill.Run, SkillAdvancementClass.Trained, 1700, 5) };

            var result = FacetPools.TrimSkillsToCover(incoming, 1, Tables);

            Assert.AreEqual(1000u, result.Skills[0].Pp);
            Assert.AreEqual((ushort)4, result.Skills[0].Ranks);
            Assert.AreEqual(700UL, result.Removed);
        }

        [TestMethod]
        public void Trim_StopsAtTheMinimalNumberOfSteps()
        {
            // 900 shortfall on a rank-5 trained skill: 1500 -> 1000 (500) is not enough, 1000 -> 600 (400)
            // makes 900 exactly. One fewer step leaves 400 short; an implementation that took one more
            // would land on 300.
            var incoming = new List<FacetSkillEntry> { Entry(Skill.Run, SkillAdvancementClass.Trained, 1500, 5) };

            var result = FacetPools.TrimSkillsToCover(incoming, 900, Tables);

            Assert.AreEqual(600u, result.Skills[0].Pp);
            Assert.AreEqual((ushort)3, result.Skills[0].Ranks);
            Assert.AreEqual(900UL, result.Removed);
            Assert.IsTrue(FacetPools.TryTopSkillStep(Trained, 1500, out var afterOneStep), "fixture: the skill must have a first step");
            Assert.IsTrue(1500u - afterOneStep < 900u, "fixture: the first step alone must not cover the shortfall, or this test proves nothing about stopping");
        }

        [TestMethod]
        public void Trim_LeavesUntouchedEntriesIdentical_AndNeverChangesSacOrInitLevel()
        {
            // Discriminates an implementation that recomputes Ranks for every entry (Run's stored 7 does not
            // match its PP on this table, so a blanket recompute would change it), and one that resets SAC or
            // InitLevel on a trimmed entry.
            var incoming = new List<FacetSkillEntry>
            {
                Entry(Skill.Healing, SkillAdvancementClass.Specialized, 750, 5, 10),
                Entry(Skill.Run, SkillAdvancementClass.Trained, 100, 7),
                Entry(Skill.Jump, SkillAdvancementClass.Untrained, 0, 0),
            };

            var result = FacetPools.TrimSkillsToCover(incoming, 1, Tables);

            Assert.AreEqual(500u, result.Skills[0].Pp, "fixture: Healing's 250 step beats Run's 100");
            Assert.AreEqual(Skill.Healing, result.Skills[0].Skill);
            Assert.AreEqual(SkillAdvancementClass.Specialized, result.Skills[0].Sac, "a trim never changes SAC");
            Assert.AreEqual(10u, result.Skills[0].InitLevel, "a trim never changes InitLevel");

            AssertSameFields(incoming[1], result.Skills[1], "an untouched entry is copied verbatim, including its stored Ranks");
            AssertSameFields(incoming[2], result.Skills[2], "an untouched entry is copied verbatim");
        }

        [TestMethod]
        public void Trim_DoesNotMutateTheInput_AndReturnsNewEntries()
        {
            // Discriminates an in-place trim, which would corrupt the preview's source build (and, in the
            // switch, the stored build it was deserialized from).
            var incoming = new List<FacetSkillEntry>
            {
                Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 1500, 5),
                Entry(Skill.Healing, SkillAdvancementClass.Specialized, 750, 5, 10),
            };

            var snapshot = incoming.Select(e => Entry(e.Skill, e.Sac, e.Pp, e.Ranks, e.InitLevel)).ToList();

            var result = FacetPools.TrimSkillsToCover(incoming, 1200, Tables);

            Assert.IsTrue(result.Removed > 0, "fixture: something must actually be trimmed");

            for (var i = 0; i < incoming.Count; i++)
            {
                AssertSameFields(snapshot[i], incoming[i], "the input entries must be untouched");
                Assert.AreNotSame(incoming[i], result.Skills[i], "every output entry must be a new object");
            }

            Assert.AreNotSame(incoming, result.Skills);
        }

        [TestMethod]
        public void Trim_PpTheTrimCannotReach_ReportsNotCovered()
        {
            // An Untrained entry has no XP table, so its PP is not a candidate. The trim must say it could
            // not cover the shortfall rather than claim success - TrySwitchFacet refuses on this.
            var incoming = new List<FacetSkillEntry> { Entry(Skill.Run, SkillAdvancementClass.Untrained, 500, 0) };

            var result = FacetPools.TrimSkillsToCover(incoming, 100, Tables);

            Assert.IsFalse(result.Covered);
            Assert.AreEqual(0UL, result.Removed);
            AssertSameFields(incoming[0], result.Skills[0], "nothing trimmable means nothing changes");
        }

        [TestMethod]
        public void Trim_Deltas_AreOrderedByExperienceRemoved_WithNegativeRankDeltas()
        {
            var incoming = new List<FacetSkillEntry>
            {
                Entry(Skill.Run, SkillAdvancementClass.Trained, 300, 2),
                Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 1500, 5),
            };

            // 1500->1000 (500), 1000->600 (400), 600->300 (300), then Run and Two Handed tie at 200 and the
            // earlier entry (Run) wins: 1400 >= 1300.
            var result = FacetPools.TrimSkillsToCover(incoming, 1300, Tables);

            Assert.AreEqual(2, result.Deltas.Count);
            Assert.AreEqual(Skill.TwoHandedCombat, result.Deltas[0].Skill, "most experience removed first");
            Assert.AreEqual(-3, result.Deltas[0].RankDelta);
            Assert.AreEqual(1200u, result.Deltas[0].PpRemoved);
            Assert.AreEqual(Skill.Run, result.Deltas[1].Skill);
            Assert.AreEqual(-1, result.Deltas[1].RankDelta);
            Assert.AreEqual(1400UL, result.Removed);
        }

        // ==================================================================================
        // Reachability and the out-of-reach transition
        // ==================================================================================

        private static Dictionary<int, FacetStoredSummary> Stored(params (int slot, ulong pp, string name)[] rows)
            => rows.ToDictionary(r => r.slot, r => new FacetStoredSummary(r.pp, r.name));

        [TestMethod]
        public void ReachBudget_ASkillRaiseLeavesItUnchanged()
        {
            // The reason skill raises never warn: the pool drops by exactly what the live PP rises by.
            Assert.AreEqual(FacetPools.ReachBudget(1_000, 500), FacetPools.ReachBudget(1_000 - 200, 500 + 200));
        }

        [TestMethod]
        public void OutOfReach_TheCrossingSpend_ReportsTheSlotNameAndGap()
        {
            var losses = FacetPools.FacetsPutOutOfReach(1_200, 900, Stored((2, 1_000, "Void")), activeSlot: 1);

            Assert.AreEqual(1, losses.Count);
            Assert.AreEqual(2, losses[0].Slot);
            Assert.AreEqual("Void", losses[0].Name);
            Assert.AreEqual(100UL, losses[0].Gap, "the gap is stored PP minus the budget AFTER the spend");
        }

        [TestMethod]
        public void OutOfReach_AlreadyUnreachableBeforeTheSpend_DoesNotReportAgain()
        {
            // Discriminates a level-triggered check ("is it unreachable now?"), which would spam every raise.
            Assert.AreEqual(0, FacetPools.FacetsPutOutOfReach(900, 800, Stored((2, 1_000, null)), 1).Count);
        }

        [TestMethod]
        public void OutOfReach_StillReachableAfterTheSpend_DoesNotReport()
        {
            // Exactly the stored PP is still reachable - the switch formula refuses only when strictly short.
            Assert.AreEqual(0, FacetPools.FacetsPutOutOfReach(1_500, 1_000, Stored((2, 1_000, null)), 1).Count);
        }

        [TestMethod]
        public void OutOfReach_ManySmallRaises_ReportEachFacetOnce()
        {
            // How a client actually spends: a stream of small increments. Only the one that crosses reports.
            var stored = Stored((2, 1_000, null), (3, 700, null));
            var reports = new List<int>();

            for (ulong budget = 1_300; budget > 400; budget -= 100)
                reports.AddRange(FacetPools.FacetsPutOutOfReach(budget, budget - 100, stored, 1).Select(l => l.Slot));

            CollectionAssert.AreEqual(new List<int> { 2, 3 }, reports);
        }

        [TestMethod]
        public void OutOfReach_SkipsTheActiveSlot_AndSlotsStoringNoPp()
        {
            var stored = Stored((1, 1_000, null), (3, 0, null));

            Assert.AreEqual(0, FacetPools.FacetsPutOutOfReach(1_200, 0, stored, activeSlot: 1).Count);
        }

        [TestMethod]
        public void OutOfReach_NullOrEmptyCache_ReportsNothing()
        {
            Assert.AreEqual(0, FacetPools.FacetsPutOutOfReach(1_200, 0, null, 1).Count);
            Assert.AreEqual(0, FacetPools.FacetsPutOutOfReach(1_200, 0, new Dictionary<int, FacetStoredSummary>(), 1).Count);
        }

        [TestMethod]
        public void OutOfReach_SeveralFacetsCrossedAtOnce_AreOrderedBySlot()
        {
            var losses = FacetPools.FacetsPutOutOfReach(2_000, 0, Stored((4, 900, null), (2, 1_500, null)), 1);

            CollectionAssert.AreEqual(new List<int> { 2, 4 }, losses.Select(l => l.Slot).ToList());
        }

        // ==================================================================================
        // Wording, cache summary and argument parsing (pure statics on Player / FacetCommands)
        // ==================================================================================

        [TestMethod]
        public void ShortfallRefusal_NamesTheShortfall_PreviewsTheTrim_AndDropsTheImpossibleAdvice()
        {
            var incoming = new List<FacetSkillEntry> { Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 1500, 5) };
            var preview = FacetPools.TrimSkillsToCover(incoming, 450, Tables);

            var text = Player.ComposeXpShortfallRefusal(2, 450, preview);

            StringAssert.Contains(text, $"short {450:N0} experience");
            StringAssert.Contains(text, $"{Skill.TwoHandedCombat.ToSentence()} -1");
            StringAssert.Contains(text, $"returning {50:N0} experience beyond the shortfall");
            StringAssert.Contains(text, "Type /facet 2 trim");
            Assert.IsFalse(text.Contains("free up experience"), "untraining on the current facet cannot close the gap, so the refusal must not suggest it");
        }

        [TestMethod]
        public void ShortfallRefusal_AnUncoverableTrim_DoesNotOfferIt()
        {
            var incoming = new List<FacetSkillEntry> { Entry(Skill.Run, SkillAdvancementClass.Untrained, 500, 0) };
            var preview = FacetPools.TrimSkillsToCover(incoming, 100, Tables);

            var text = Player.ComposeXpShortfallRefusal(3, 100, preview);

            Assert.IsFalse(text.Contains("trim would remove"));
            StringAssert.Contains(text, "contact staff");
        }

        [TestMethod]
        public void TrimList_CapsAtTheLimit_AndCountsTheRest()
        {
            var skills = new[] { Skill.Run, Skill.Jump, Skill.Healing, Skill.WarMagic, Skill.LifeMagic, Skill.VoidMagic, Skill.Loyalty, Skill.Leadership, Skill.Alchemy, Skill.Cooking };
            var deltas = skills.Select((s, i) => new FacetSkillTrimDelta(s, 10, 9, 1_000, 900, i)).ToList();

            var text = Player.ComposeFacetTrimList(deltas, Player.FacetTrimPreviewLimit);

            StringAssert.Contains(text, $"{Skill.Leadership.ToSentence()} -1", "the eighth entry is shown");
            Assert.IsFalse(text.Contains(Skill.Alchemy.ToSentence()), "the ninth entry is collapsed");
            StringAssert.Contains(text, "and 2 more");
        }

        [TestMethod]
        public void TrimList_PartialProgressOnly_IsNamedWithoutARankNumber()
        {
            var deltas = new List<FacetSkillTrimDelta> { new FacetSkillTrimDelta(Skill.Run, 0, 0, 40, 0, 0) };

            StringAssert.Contains(Player.ComposeFacetTrimList(deltas, 8), "(progress toward its next rank)");
        }

        [TestMethod]
        public void TrimSummary_NamesTheOvershootReturnedToThePool()
        {
            var incoming = new List<FacetSkillEntry> { Entry(Skill.TwoHandedCombat, SkillAdvancementClass.Trained, 1500, 5) };
            var trim = FacetPools.TrimSkillsToCover(incoming, 450, Tables);

            var text = Player.ComposeFacetTrimSummaryLine(450, trim);

            StringAssert.Contains(text, $"{Skill.TwoHandedCombat.ToSentence()} -1");
            StringAssert.Contains(text, $"{50:N0} experience past the shortfall");
        }

        [TestMethod]
        public void OutOfReachWarning_NamesTheFacetTheGapAndTheTrimCommand()
        {
            var text = Player.ComposeFacetOutOfReachWarning(new FacetReachLoss(3, "Archer", 12_345));

            StringAssert.Contains(text, "facet 3 (\"Archer\")");
            StringAssert.Contains(text, $"{12_345:N0} more experience");
            StringAssert.Contains(text, "/facet 3 trim");

            StringAssert.Contains(Player.ComposeFacetOutOfReachWarning(new FacetReachLoss(2, null, 1)), "put facet 2 out of reach");
        }

        [TestMethod]
        public void SummarizeStoredRows_SumsPp_CarriesNames_AndSkipsUnreadableRows()
        {
            var rows = new List<CharacterFacet>
            {
                new CharacterFacet
                {
                    Slot = 2,
                    Name = "Void",
                    SkillsJson = FacetSnapshot.SerializeSkills(new List<FacetSkillEntry>
                    {
                        Entry(Skill.VoidMagic, SkillAdvancementClass.Specialized, 4_000_000_000u, 200),
                        Entry(Skill.Run, SkillAdvancementClass.Trained, 1_000_000_000u, 150),
                    }),
                },
                new CharacterFacet { Slot = 3, Name = "Broken", SkillsJson = "   " },
            };

            var summaries = Player.SummarizeStoredFacetRows(rows);

            Assert.AreEqual(1, summaries.Count, "a row whose skills cannot be read is left out");
            Assert.AreEqual(5_000_000_000UL, summaries[2].Pp, "summed as ulong, not wrapped at uint");
            Assert.AreEqual("Void", summaries[2].Name);
        }

        [TestMethod]
        public void ParseSwitchArgs_AcceptsNAndNTrim_AndRefusesAnythingElse()
        {
            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "2" }, out var slot, out var trim));
            Assert.AreEqual(2, slot);
            Assert.IsFalse(trim);

            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "3", "TRIM" }, out slot, out trim));
            Assert.AreEqual(3, slot);
            Assert.IsTrue(trim, "trim is case-insensitive");

            Assert.IsFalse(FacetCommands.TryParseSwitchArgs(new[] { "2", "trm" }, out _, out _), "a mistyped trim must not silently run a plain switch");
            Assert.IsFalse(FacetCommands.TryParseSwitchArgs(new[] { "2", "trim", "now" }, out _, out _));
            Assert.IsFalse(FacetCommands.TryParseSwitchArgs(new[] { "two" }, out _, out _));
        }

        // ==================================================================================
        // Source-shape guards for the wiring no behavioural test here can reach (no live Player).
        // A textual check is a PROXY: it proves the call is written, not that it runs.
        // ==================================================================================

        private static string ReadServerFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", Path.Combine(relative)));
        }

        private static string MethodBody(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"could not find `{signature}` - this test's anchor has moved, it is not evidence about behaviour");

            var open = text.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after `{signature}`");
            return null;
        }

        [TestMethod]
        public void Wiring_SpendXpWarns_ButTheSkillRaisePathOptsOut()
        {
            // CATCHES: the warning being dropped from SpendXP (attributes and vitals would stop warning), and
            // the skill path losing its opt-out (every skill raise would warn falsely mid-raise).
            StringAssert.Contains(MethodBody(ReadServerFile("WorldObjects", "Player_Xp.cs"), "public bool SpendXP("), "WarnFacetsPutOutOfReach(availableBefore)");
            StringAssert.Contains(MethodBody(ReadServerFile("WorldObjects", "Player_Skills.cs"), "private bool SpendSkillXp("), "checkFacetReachability: false");

            Assert.IsFalse(MethodBody(ReadServerFile("WorldObjects", "Player_Attributes.cs"), "private bool SpendAttributeXp(").Contains("checkFacetReachability"),
                "attribute spends must keep the warning");
            Assert.IsFalse(MethodBody(ReadServerFile("WorldObjects", "Player_Vitals.cs"), "private bool SpendVitalXp(").Contains("checkFacetReachability"),
                "vital spends must keep the warning");
        }

        [TestMethod]
        public void Wiring_AugmentationDebitWarns_WithThePreDebitPool()
        {
            // AugmentationDevice.DoAugmentation debits AvailableExperience directly, bypassing SpendXP, so it
            // carries its own call. CATCHES: that call being dropped (augmentations, the largest XP sink,
            // would stop warning), and the pre-debit capture being moved after the debit (the check would
            // then see no drop and never warn). PROXY: proves the calls are written in this order, not that
            // they run.
            var body = MethodBody(ReadServerFile("WorldObjects", "AugmentationDevice.cs"), "public void DoAugmentation(");

            const string capture = "var availableBeforeAugmentation = player.AvailableExperience ?? 0;";
            const string debit = "player.AvailableExperience -= AugmentationCost;";
            const string warn = "player.WarnFacetsPutOutOfReach(availableBeforeAugmentation);";

            var captureAt = body.IndexOf(capture, StringComparison.Ordinal);
            var debitAt = body.IndexOf(debit, StringComparison.Ordinal);
            var warnAt = body.IndexOf(warn, StringComparison.Ordinal);

            Assert.IsTrue(debitAt >= 0, "fixture: the augmentation debit line has moved, so this test's anchor is stale");
            Assert.IsTrue(warnAt >= 0, "DoAugmentation must call WarnFacetsPutOutOfReach with the pre-debit pool");
            Assert.IsTrue(captureAt >= 0 && captureAt < debitAt, "the pool must be captured BEFORE the augmentation debit");
            Assert.IsTrue(warnAt > debitAt, "the warning must run AFTER the debit, or it sees no drop");
        }
    }
}
