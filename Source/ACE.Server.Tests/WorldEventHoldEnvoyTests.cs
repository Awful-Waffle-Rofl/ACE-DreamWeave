using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Defend Asheron's Envoys (goal defend_envoys): the second Hold goal, three Royal Knights (WCIDs 2000005 to
    /// 2000007) in place of the caravan merchants. Content only, so these tests check the shipped lines and, when
    /// this machine has the content folders, the real goal file and the live goals.json / sources.json.
    /// Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private static readonly uint[] EnvoyWcids = { 2000005, 2000006, 2000007 };

        /// <summary>The Homecoming Pennant placed behind each knight, in place of the caravan's cart.</summary>
        private const uint EnvoyPennantWcid = 25952;

        /// <summary>The target lines shipped in dreamweave-content/events/defend_envoys.goal.json (lines block).</summary>
        private static readonly string[] EnvoyTargetLines =
        {
            "By Asheron's command, we hold this ground! Let {family} come.",
            "Knights, to your posts! {Family} will not stop Asheron's word.",
            "Stand with the Royal Knights, friends. Our message must get through.",
            "I am down to {pct} percent! To me, defenders!",
            "{Family} press me hard! I cannot hold them alone!",
            "My armor will not last much longer! Drive them back!",
            "I have failed my lord... carry Asheron's word for me!",
            "I fall! Guard the other envoys with your lives!",
            "Curse {family}! Do not let them take my brothers in arms!",
            "They falter! Bind our wounds before {family} return.",
            "A moment's respite. Tend to us quickly, friends!",
            "Well fought! Mend what you can, the next charge is coming.",
            "The field is ours! Asheron will hear of your courage.",
            "{Family} are broken. You have the thanks of the Royal Knights!",
            "Our message will reach its destination. Well fought, friends!"
        };

        /// <summary>The special wave lines (announce and breach lines) shipped in defend_envoys.goal.json.</summary>
        private static readonly string[] EnvoySpecialWaveLines =
        {
            "To arms! {Family} gather for a single charge!",
            "They mean to break one of us! Close ranks!",
            "They are massing on the {side} flank! To me, defenders!",
            "Every one of them is coming for the {side} post! Hold them back!",
            "Swift ones approach! Cut them down before they reach us!",
            "{Family} are charging in fast! Intercept them!",
            "Archers and casters hang back to strike at us! Go after them!",
            "{Family} strike from a distance! Close with them, quickly!",
            "Some of {family} hunt you, not us! Guard your healers!",
            "Healers, beware! {Family} seek you first!",
            "{champion} leads {family} in! Stand fast, this is their last assault!",
            "That is {champion}! Bring it down and our charge is safe!"
        };

        /// <summary>The goal's progress, success and fail templates.</summary>
        private static readonly string[] EnvoyTemplates =
        {
            "{waveProgress} - {alive} of {total} {nounPlural} still stand against {family}.",
            "Asheron's envoys at {anchor} held the field! {alive} of {total} {nounPlural} survived.",
            "The defense of Asheron's envoys at {anchor} failed - {reason}. {alive} of {total} {nounPlural} were still standing."
        };

        /// <summary>Words from the caravan goal that must not leak into the knights' lines.</summary>
        private static readonly string[] CaravanWords = { "caravan", "wagon", "merchant", "market", "goods" };

        [TestMethod]
        public void EnvoyLines_InCharacter_AsciiShortAndFilled()
        {
            foreach (var family in SampleFamilies)
            {
                foreach (var template in EnvoyTargetLines.Concat(EnvoySpecialWaveLines).Concat(EnvoyTemplates))
                {
                    var filled = WorldEventHoldRules.FillWaveTokens(template, "east", "Champion Drudge Ravener")
                        .Replace("{waveProgress}", WorldEventHoldRules.WaveProgress(2, 4))
                        .Replace("{reason}", "every envoy fell");

                    var line = WorldEventHoldRules.FillLine(filled, family, "envoy", "envoys", "Holtburg", 2, 3, pct: 50);

                    AssertAscii(line);
                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), $"leftover token: {line}");
                    Assert.IsTrue(SentenceCount(line) <= 3, $"more than three sentences: {line}");
                    Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, $"names the Warp: {line}");

                    foreach (var word in CaravanWords)
                        Assert.IsFalse(line.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0, $"caravan wording \"{word}\": {line}");
                }
            }
        }

        [TestMethod]
        public void EnvoyLines_NoDashesInTheSource()
        {
            foreach (var template in EnvoyTargetLines.Concat(EnvoySpecialWaveLines).Concat(EnvoyTemplates))
            {
                Assert.IsFalse(template.Contains((char)0x2013) || template.Contains((char)0x2014), $"en or em dash: {template}");
                AssertAscii(template);
            }
        }

        /// <summary>
        /// The real goal file, when this machine has it: it loads with no diagnostics, uses the three knights and
        /// the pennant, uses {family} in every line pool, matches the mirror above, and keeps the caravan's wave
        /// tuning (4 waves solo, 10 at most, paced over 180 s).
        /// </summary>
        [TestMethod]
        public void ShippedEnvoyGoalFile_LoadsClean_WhenPresent()
        {
            var path = FindContentFile(Path.Combine("dreamweave-content", "events", "defend_envoys.goal.json"));

            if (path == null)
                return;

            var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("defend_envoys"), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));

            var goal = store.Goals["defend_envoys"];
            var p = goal.Protect;

            Assert.AreEqual("Defend Asheron's Envoys", goal.DisplayName);
            Assert.AreEqual(GoalType.Hold, goal.TypeKind);
            CollectionAssert.AreEqual(EnvoyWcids, p.Wcids);
            Assert.AreEqual("envoy", p.Noun);
            Assert.AreEqual("envoys", p.NounPlural);

            Assert.IsTrue(p.TargetDecor.All(d => d.Wcid == EnvoyPennantWcid), "a pennant behind each knight, not the cart");
            Assert.IsFalse(p.TargetDecor.Any(d => d.Wcid == 28527), "no caravan cart");

            // Every pool names the run's monster family at least once.
            foreach (var pool in new[] { p.Lines.Spawn, p.Lines.Health, p.Lines.Destroyed, p.Lines.Heal, p.Lines.Success })
                Assert.IsTrue(pool.Any(l => l.IndexOf("{family}", StringComparison.OrdinalIgnoreCase) >= 0), string.Join(" / ", pool));

            var breach = p.SpecialWaves.Single(w => w.TypeKind == HoldWaveType.Breach);
            Assert.IsTrue(breach.Lines.All(l => l.Contains("{side}")), "breach warnings name the side");

            var shippedTargetLines = p.Lines.Spawn.Concat(p.Lines.Health).Concat(p.Lines.Destroyed).Concat(p.Lines.Heal).Concat(p.Lines.Success);
            CollectionAssert.AreEquivalent(EnvoyTargetLines, shippedTargetLines.ToList(), "the target-line mirror matches the file");

            var shippedWaveLines = p.SpecialWaves.SelectMany(w => w.Announce.Concat(w.Lines)).ToList();
            CollectionAssert.AreEquivalent(EnvoySpecialWaveLines, shippedWaveLines, "the special-wave mirror matches the file");

            CollectionAssert.AreEquivalent(EnvoyTemplates, new[] { goal.ProgressTemplate, goal.SuccessTemplate, goal.FailTemplate });

            AssertSoloWaveShape(goal);
        }

        /// <summary>
        /// The live content (ace-content next to the repo), when present: both Hold goals load, the caravan is
        /// still there unchanged in shape, and the ambush source offers both.
        /// </summary>
        [TestMethod]
        public void LiveContent_BothHoldGoalsLoad_AndAmbushOffersBoth_WhenPresent()
        {
            var goalsPath = FindContentFile(Path.Combine("ace-content", "events", "axes", "goals.json"));
            var sourcesPath = FindContentFile(Path.Combine("ace-content", "events", "axes", "sources.json"));

            if (goalsPath == null || sourcesPath == null)
                return;

            var store = WorldEventAxisStore.Parse(File.ReadAllText(sourcesPath), null, File.ReadAllText(goalsPath), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("defend_caravan"), string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.Goals.ContainsKey("defend_envoys"), string.Join(" | ", store.Diagnostics));
            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("defend_envoys") || d.Contains("defend_caravan")),
                string.Join(" | ", store.Diagnostics));

            CollectionAssert.AreEqual(new uint[] { 2000002, 2000003, 2000004 }, store.Goals["defend_caravan"].Protect.Wcids, "the caravan goal is kept as is");
            CollectionAssert.AreEqual(EnvoyWcids, store.Goals["defend_envoys"].Protect.Wcids);

            Assert.IsTrue(store.Sources.ContainsKey("ambush"), string.Join(" | ", store.Diagnostics));
            CollectionAssert.IsSubsetOf(new[] { "defend_caravan", "defend_envoys" }, store.Sources["ambush"].CompatibleGoals);

            AssertSoloWaveShape(store.Goals["defend_caravan"]);
            AssertSoloWaveShape(store.Goals["defend_envoys"]);
        }

        /// <summary>
        /// The wave-count fix against a real goal: 4 waves solo (a staff test counts as 0 players), never more
        /// than 10 however big the crowd, and a group that clears every wave instantly still gets exactly 4,
        /// paced 60 s apart over minSeconds 180, the last of them the finale.
        /// </summary>
        private static void AssertSoloWaveShape(GoalDef goal)
        {
            var p = goal.Protect;

            Assert.AreEqual(180, p.MinSeconds, 1e-9, $"{goal.Id}: minSeconds");
            Assert.IsTrue(goal.HoldSeconds >= p.MinSeconds, $"{goal.Id}: holdSeconds is the safety cap after the pacing");

            var solo = new HoldHarness(goal);
            Assert.AreEqual(4, solo.Objective.TargetWaves, $"{goal.Id}: solo waves");

            solo.Objective.OnAudienceResampled(1);
            Assert.AreEqual(5, solo.Objective.TargetWaves, $"{goal.Id}: one counted player gets 4 + ceil(0.2)");

            var crowd = new HoldHarness(goal);
            crowd.Objective.OnAudienceResampled(500);
            Assert.AreEqual(ProtectDef.MaxHoldWaves, crowd.Objective.TargetWaves, $"{goal.Id}: hard cap");

            // A wave asked for every second for the whole hold, every wave cleared at once.
            var started = new List<int>();

            for (var second = 0; second <= goal.HoldSeconds; second++)
            {
                if (HoldObjective.WantsAnotherWave(started.Count, 4, second, p.MinSeconds))
                    started.Add(second);
            }

            CollectionAssert.AreEqual(new[] { 0, 60, 120, 180 }, started, $"{goal.Id}: wave start times");
            Assert.AreEqual("Wave 4 of 4", WorldEventHoldRules.WaveProgress(started.Count + 3, 4), "never above the total");
        }
    }
}
