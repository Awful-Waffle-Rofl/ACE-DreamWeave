using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The chain's pacing and story (2026-10-19): a stage change goes out in order (outcome, thank-you with the cache,
    /// story beat, next stage's announcement), the lines that link the stages, Asheron's closing line only at the
    /// chain's end, and "1 defender".
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private static ChainStoryDef SampleStory() => new ChainStoryDef
        {
            Thanks = new List<string> { "Well fought! Take your reward from the cache." },
            ThanksNarrator = new List<string> { "The field is held. Your reward waits in the cache." },
            Reveal = new List<string> { "{Family} were sent by {champion}, who now moves on {nextTown}!" },
            RevealNarrator = new List<string> { "An order from {champion} lies among the fallen. It points to {nextTown}." },
            TieBack = new List<string> { "{champion} sent {family} against us at {town}!" },
            TieBackNarrator = new List<string> { "This is {champion}, who sent {family} to {town}." },
            ClosingSuccess = new List<string> { "{champion} is fallen. From {town} to {nextTown}, you stood with us." },
            ClosingSuccessNarrator = new List<string> { "With {champion} slain, the envoys are safe." },
            ClosingFailure = new List<string> { "Our message did not get through." },
            ClosingFailureNarrator = new List<string> { "The envoys have fallen." }
        };

        private static WorldEventChain.StoryTokens SampleTokens(string envoy = "Sir Adarl") => new WorldEventChain.StoryTokens
        {
            Envoy = envoy,
            Family = "the Ruschks and the Mukkirs",
            Town = "Eastham",
            NextTown = "Rithwic",
            Champion = "Ssethaun"
        };

        [TestMethod]
        public void ChainStory_EnvoySpeaks_OrANarratorWhenNoneSurvived()
        {
            var rng = new Random(1);

            Assert.AreEqual("[World Event] Sir Adarl: \"The Ruschks and the Mukkirs were sent by Ssethaun, who now moves on Rithwic!\"",
                WorldEventChain.StoryLine(SampleStory(), WorldEventChain.StoryBeat.Reveal, SampleTokens(), rng));

            Assert.AreEqual("[World Event] An order from Ssethaun lies among the fallen. It points to Rithwic.",
                WorldEventChain.StoryLine(SampleStory(), WorldEventChain.StoryBeat.Reveal, SampleTokens(envoy: null), rng), "no envoy: the narrator");

            Assert.AreEqual("[World Event] Sir Adarl: \"Ssethaun sent the Ruschks and the Mukkirs against us at Eastham!\"",
                WorldEventChain.StoryLine(SampleStory(), WorldEventChain.StoryBeat.TieBack, SampleTokens(), rng));
        }

        [TestMethod]
        public void ChainStory_Thanks_AlwaysPointsToTheCache_OtherBeatsSkipWhenEmpty()
        {
            var empty = new ChainStoryDef();
            var rng = new Random(1);

            Assert.AreEqual($"[World Event] Sir Adarl: \"{WorldEventChain.DefaultThanks}\"",
                WorldEventChain.StoryLine(empty, WorldEventChain.StoryBeat.Thanks, SampleTokens(), rng));
            StringAssert.Contains(WorldEventChain.StoryLine(empty, WorldEventChain.StoryBeat.Thanks, SampleTokens(null), rng), "cache");

            foreach (var beat in new[] { WorldEventChain.StoryBeat.Reveal, WorldEventChain.StoryBeat.TieBack,
                         WorldEventChain.StoryBeat.ClosingSuccess, WorldEventChain.StoryBeat.ClosingFailure })
                Assert.IsNull(WorldEventChain.StoryLine(empty, beat, SampleTokens(), rng), beat.ToString());
        }

        [TestMethod]
        public void ChainStory_Tokens_FallBack_AndTheLineStartsCapitalised()
        {
            Assert.AreEqual("Their champion moves on the next town, sent from this place.",
                WorldEventChain.FillStory("{champion} moves on {nextTown}, sent from {town}.", new WorldEventChain.StoryTokens()));
            Assert.AreEqual("The attackers fled.", WorldEventChain.FillStory("{family} fled.", null));
            Assert.AreEqual("Sir Adarl speaks.", WorldEventChain.FillStory("{envoy} speaks.", SampleTokens()));
        }

        [TestMethod]
        public void ChainPacing_StageChange_ThanksThenStoryThenAnnouncement()
        {
            var chain = new ChainDef { Story = SampleStory() };
            var plan = WorldEventChain.StageChangePlan(chain, SampleTokens(), "A rift tears open over Rithwic! Ssethaun descends in 120 seconds.", new Random(1));

            CollectionAssert.AreEqual(new[] { 2.0, 5.0, 8.0 }, plan.Select(l => l.Delay).ToArray(), "the default pacing");
            StringAssert.Contains(plan[0].Text, "cache", "2 s: the thank-you points to the cache");
            StringAssert.Contains(plan[1].Text, "Rithwic", "5 s: the story beat names the next town");
            Assert.AreEqual("[World Event] A rift tears open over Rithwic! Ssethaun descends in 120 seconds.", plan[2].Text, "8 s: one announcement");
            CollectionAssert.AreEqual(new[] { false, true, true }, plan.Select(l => l.AboutNextStage).ToArray(), "the thank-you is about this stage");

            Assert.IsFalse(plan.Any(l => l.Text.Contains("humming") || l.Text.Contains("Then make for")), "no teaser, no separate reminder");
            Assert.IsFalse(plan.Any(l => l.Text == WorldEventAnnouncer.DeathProtectionEndLine), "Asheron waits for the chain's end");

            chain.Pacing = new ChainPacingDef { ThanksSeconds = 1, StorySeconds = 10, AnnounceSeconds = 20 };
            CollectionAssert.AreEqual(new[] { 1.0, 10.0, 20.0 }, WorldEventChain.StageChangePlan(chain, SampleTokens(), "x", new Random(1)).Select(l => l.Delay).ToArray(), "configurable");
        }

        [TestMethod]
        public void ChainPacing_ChainEnd_ClosingLineThenAsheron_AbortOnlyAsheron()
        {
            var chain = new ChainDef { Story = SampleStory() };
            var rng = new Random(1);

            var won = WorldEventChain.ChainEndPlan(chain, WorldEventOutcome.Success, SampleTokens(), asheronSpoke: true, thanksFirst: false, rng);
            CollectionAssert.AreEqual(new[] { 3.0, 6.0 }, won.Select(l => l.Delay).ToArray());
            Assert.AreEqual("[World Event] Sir Adarl: \"Ssethaun is fallen. From Eastham to Rithwic, you stood with us.\"", won[0].Text);
            Assert.AreEqual(WorldEventAnnouncer.DeathProtectionEndLine, won[1].Text);

            var lost = WorldEventChain.ChainEndPlan(chain, WorldEventOutcome.FailedObjectiveLost, SampleTokens(null), true, false, rng);
            Assert.AreEqual("[World Event] The envoys have fallen.", lost[0].Text, "a loss with no envoy left: the narrator's somber line");

            var aborted = WorldEventChain.ChainEndPlan(chain, WorldEventOutcome.AbortedAdmin, SampleTokens(), true, false, rng);
            Assert.AreEqual(1, aborted.Count);
            Assert.AreEqual(0.0, aborted[0].Delay, "an abort: Asheron at once, no story");

            Assert.IsFalse(WorldEventChain.ChainEndPlan(chain, WorldEventOutcome.Success, SampleTokens(), asheronSpoke: false, false, rng)
                .Any(l => l.Text == WorldEventAnnouncer.DeathProtectionEndLine), "never an end line without the opening line");

            var stalled = WorldEventChain.ChainEndPlan(chain, WorldEventOutcome.Success, SampleTokens(), true, thanksFirst: true, rng);
            CollectionAssert.AreEqual(new[] { 2.0, 5.0, 8.0 }, stalled.Select(l => l.Delay).ToArray(), "won but the next stage could not start: thanks, closing, Asheron");
            StringAssert.Contains(stalled[0].Text, "cache");
        }

        [TestMethod]
        public void ChainPacing_Validate_EveryValue0To60()
        {
            Assert.IsNull(WorldEventChain.Validate(new ChainDef()));
            StringAssert.Contains(WorldEventChain.Validate(new ChainDef { Pacing = new ChainPacingDef { StorySeconds = 61 } }), "chain.pacing");
            StringAssert.Contains(WorldEventChain.Validate(new ChainDef { Pacing = new ChainPacingDef { ThanksSeconds = -1 } }), "chain.pacing");
            StringAssert.Contains(WorldEventChain.Validate(new ChainDef { Pacing = new ChainPacingDef { AsheronSeconds = double.NaN } }), "chain.pacing");
        }

        [TestMethod]
        public void LineSchedule_InTimeOrder_NeverEarly_DropsLinesNoLongerWanted()
        {
            var schedule = new WorldEventLineSchedule();
            var nextStageOn = true;

            schedule.AddPlan(100, new[]
            {
                new WorldEventChain.PacedLine(8, "announce", true),
                new WorldEventChain.PacedLine(2, "thanks"),
                new WorldEventChain.PacedLine(5, "story", true)
            }, () => nextStageOn);

            Assert.AreEqual(0, schedule.TakeDue(101.9).Count, "nothing before its time");
            CollectionAssert.AreEqual(new[] { "thanks" }, schedule.TakeDue(102).ToArray());
            CollectionAssert.AreEqual(new[] { "story" }, schedule.TakeDue(106).ToArray());

            nextStageOn = false;
            Assert.AreEqual(0, schedule.TakeDue(200).Count, "the next stage was stopped: its announcement is dropped");
            Assert.AreEqual(0, schedule.Count);

            schedule.Add(10, "b");
            schedule.Add(5, "a");
            schedule.Add(10, "c");
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, schedule.TakeDue(10).ToArray(), "time order, ties as added");
        }

        [TestMethod]
        public void ChainNextRequest_CountdownStartsAtTheAnnouncement()
        {
            var anchor = new ACE.Entity.Position(0xCE950022, 100, 100, 20, 0, 0, 0, 1, 0);
            var next = WorldEventChain.NextRequest(new WorldEventRequest(), new ChainDef { CountdownSeconds = 120 }, 2, 2, 1, 1, anchor, "Rithwic");

            Assert.AreEqual(128, next.TeaserLeadSeconds + next.AnnounceLeadSeconds, "live 120 s after the announcement, which goes out 8 s after the outcome");
        }

        [TestMethod]
        public void OutcomeLine_OneDefender_IsSingular()
        {
            Assert.AreEqual("1 defender took part.", WorldEventAnnouncer.DefendersTookPart(1));
            Assert.AreEqual("2 defenders took part.", WorldEventAnnouncer.DefendersTookPart(2));
            Assert.AreEqual("0 defenders took part.", WorldEventAnnouncer.DefendersTookPart(0));

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, new GoalDef { DisplayName = "Defend" }, "Eastham",
                WorldEventMvp.None, null, 1);
            StringAssert.EndsWith(line, "Most kills: none (0). 1 defender took part.");
        }

        [TestMethod]
        public void ShippedEnvoys_StoryLines_FollowTheWritingRules_WhenPresent()
        {
            var goalsPath = ContentFile("ace-content", "events", "axes", "goals.json");
            var copyPath = ContentFile("dreamweave-content", "events", "defend_envoys.goal.json");

            if (goalsPath == null || copyPath == null)
                Assert.Inconclusive("goals.json or defend_envoys.goal.json not found next to the repo");

            foreach (var path in new[] { goalsPath, copyPath })
            {
                var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null);
                var chain = store.Goals["defend_envoys"].Chain;
                var story = chain.Story;

                Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("chain")), string.Join(" | ", store.Diagnostics));
                Assert.IsTrue(story.Reveal.Count >= 4 && story.Reveal.Count <= 5, "4 to 5 story beats for the stage change");

                var pools = new Dictionary<string, List<string>>
                {
                    ["thanks"] = story.Thanks, ["thanksNarrator"] = story.ThanksNarrator,
                    ["reveal"] = story.Reveal, ["revealNarrator"] = story.RevealNarrator,
                    ["tieBack"] = story.TieBack, ["tieBackNarrator"] = story.TieBackNarrator,
                    ["closingSuccess"] = story.ClosingSuccess, ["closingSuccessNarrator"] = story.ClosingSuccessNarrator,
                    ["closingFailure"] = story.ClosingFailure, ["closingFailureNarrator"] = story.ClosingFailureNarrator
                };

                foreach (var (name, pool) in pools)
                {
                    Assert.IsTrue(pool.Count >= 2, $"{name}: at least two lines");

                    foreach (var template in pool)
                    {
                        Assert.IsFalse(template.Contains('—') || template.Contains('–'), $"{name}: no em or en dash: {template}");
                        Assert.IsFalse(Regex.IsMatch(template, @"\b(weaves?|folds?|tapestry|warp)\b", RegexOptions.IgnoreCase), $"{name}: Warp-blind: {template}");

                        foreach (var envoy in new[] { "Sir Adarl", null })
                        {
                            var line = WorldEventChain.FillStory(template, SampleTokens(envoy));
                            AssertAscii(WorldEventAnnouncer.Global(line));
                            Assert.IsFalse(line.Contains('{') || line.Contains('}'), $"{name}: unknown token: {line}");
                            Assert.IsTrue(Regex.Matches(line.Trim(), @"[.!?]+(\s|$)").Count <= 3, $"{name}: three sentences at most: {line}");
                        }
                    }
                }

                foreach (var template in story.Thanks.Concat(story.ThanksNarrator))
                    StringAssert.Contains(template, "cache", "the thank-you points players to the cache");

                foreach (var template in story.Reveal.Concat(story.RevealNarrator))
                {
                    StringAssert.Contains(template, "{nextTown}", "the story beat says where the fight moves");
                    StringAssert.Contains(template, "{champion}", "and who leads it");
                }

                foreach (var template in story.TieBack.Concat(story.TieBackNarrator))
                    StringAssert.Contains(template, "{champion}", "the second stage names the champion who sent the raiders");

                foreach (var template in chain.Announce)
                {
                    var line = WorldEventChain.AnnounceLine(template, "the Pale Correction", "Rithwic (11.2N, 59.7E)", 120, "Rithwic", "Ssethaun");
                    AssertPlayerLine(line, "Rithwic");
                    StringAssert.Contains(line, "120 seconds");
                    StringAssert.Contains(line, "Ssethaun");
                }

                Assert.AreEqual(2, chain.Pacing.ThanksSeconds);
                Assert.AreEqual(5, chain.Pacing.StorySeconds);
                Assert.AreEqual(8, chain.Pacing.AnnounceSeconds);
            }
        }
    }
}
