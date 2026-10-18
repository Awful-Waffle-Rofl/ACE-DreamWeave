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
            "That is {champion}! Bring it down and our charge is safe!",
            // What a knight says in local chat for each phase (2026-10-18).
            "They come at a run! Strike them down before they reach us!",
            "Quick ones, coming for our lines! Cut them off!",
            "They strike from afar! Close with their archers and casters!",
            "Shots from range! Run them down, defenders!",
            "Defenders, some of them hunt you! Guard each other!",
            "They seek our healers! Protect those who mend us!",
            "{champion} comes for us! Stand fast, Royal Knights!",
            "Bring down {champion} and Asheron's word is safe!"
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
        /// The 2026-10-18 tuning, checked against a real goal: always 3 targets on the 16 m ring; 6 waves solo
        /// (a staff test counts as 0 players), 7 with one counted player, never more than 10; waves of 11
        /// monsters solo growing by 2 per player; a wave every 25 s at most, sooner once most of the last wave is
        /// dead, never closer than 8 s; never a wave past the total; and an undefended target that lasts 180 s
        /// against one attacker and never less than 60 s against a crowd.
        /// </summary>
        private static void AssertSoloWaveShape(GoalDef goal)
        {
            var p = goal.Protect;

            for (var players = 0; players <= 40; players++)
            {
                var (count, radius) = p.ResolveLayout(players);
                Assert.AreEqual(3, count, $"{goal.Id}: always 3 targets ({players} players)");
                Assert.AreEqual(16.0, radius, 1e-9, $"{goal.Id}: 16 m ring ({players} players)");
            }

            Assert.AreEqual(0, p.MinSeconds, 1e-9, $"{goal.Id}: no minSeconds pacing, the interval paces the waves");

            var solo = new HoldHarness(goal);
            Assert.AreEqual(6, solo.Objective.TargetWaves, $"{goal.Id}: solo waves");

            solo.Objective.OnAudienceResampled(1);
            Assert.AreEqual(7, solo.Objective.TargetWaves, $"{goal.Id}: one counted player gets 6 + ceil(0.25)");

            var crowd = new HoldHarness(goal);
            crowd.Objective.OnAudienceResampled(500);
            Assert.AreEqual(ProtectDef.MaxHoldWaves, crowd.Objective.TargetWaves, $"{goal.Id}: hard cap");

            Assert.AreEqual(11, p.WaveSize.Resolve(0), $"{goal.Id}: a solo wave is 11 monsters");
            Assert.AreEqual(13, p.WaveSize.Resolve(1));
            Assert.AreEqual(19, p.WaveSize.Resolve(4), $"{goal.Id}: and grows with the crowd");
            Assert.IsTrue(p.MaxAlive >= p.WaveSize.Resolve(4) * 2, $"{goal.Id}: room for two overlapping waves of a group of 4");

            Assert.AreEqual(25, p.WaveIntervalSeconds, 1e-9);
            Assert.AreEqual(8, p.MinWaveGapSeconds, 1e-9);
            Assert.AreEqual(2, p.MinSpecialWaves);

            // Nobody kills anything: a wave every 25 s, never one past the total.
            var slow = SimulateCadence(p, total: 6, killAllAtOnce: false, holdSeconds: goal.HoldSeconds);
            CollectionAssert.AreEqual(new[] { 0, 25, 50, 75, 100, 125 }, slow, $"{goal.Id}: interval cadence");

            // A group that wipes each wave the moment it lands: every 8 s, still exactly the total.
            var fast = SimulateCadence(p, total: 6, killAllAtOnce: true, holdSeconds: goal.HoldSeconds);
            CollectionAssert.AreEqual(new[] { 0, 8, 16, 24, 32, 40 }, fast, $"{goal.Id}: most-dead cadence");

            // Ten waves still all start well inside the hold.
            var ten = SimulateCadence(p, total: 10, killAllAtOnce: false, holdSeconds: goal.HoldSeconds);
            Assert.AreEqual(10, ten.Count);
            Assert.IsTrue(ten[9] <= goal.HoldSeconds - 120, $"{goal.Id}: the finale starts at {ten[9]} s, leaving 2 minutes to beat it");

            Assert.AreEqual("Wave 6 of 6", WorldEventHoldRules.WaveProgress(9, 6), "never above the total");

            // Balance: one attacker alone takes undefendedSeconds; any crowd at least minDeathSeconds.
            Assert.AreEqual(180, p.UndefendedSeconds, 1e-9);
            Assert.AreEqual(60, p.MinDeathSeconds, 1e-9);
            Assert.AreEqual(0.10, p.HealBetweenWaves, 1e-9);
        }

        /// <summary>
        /// The Hold cadence second by second: HoldObjective.WantsAnotherWave (the total) and
        /// WorldEventHoldRules.HoldNextWaveDue (interval / most dead / min gap), as WorldEvent.TickWaves runs them.
        /// </summary>
        private static List<int> SimulateCadence(ProtectDef p, int total, bool killAllAtOnce, int holdSeconds)
        {
            var started = new List<int> { 0 };
            var lastAt = 0.0;
            var live = killAllAtOnce ? 0 : 11;

            for (var second = 1; second <= holdSeconds; second++)
            {
                if (!HoldObjective.WantsAnotherWave(started.Count, total, second, p.MinSeconds))
                    continue;

                if (!WorldEventHoldRules.HoldNextWaveDue(second, lastAt, p.WaveIntervalSeconds, p.MinWaveGapSeconds, live, 11, p.NextWaveAliveShare))
                    continue;

                started.Add(second);
                lastAt = second;
                live = killAllAtOnce ? 0 : live + 11;
            }

            return started;
        }
    }
}
