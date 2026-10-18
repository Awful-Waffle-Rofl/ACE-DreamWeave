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
    /// Defend Asheron's Envoys (goal defend_envoys): the shipped Hold goal, three Royal Knights (WCIDs 2000005 to
    /// 2000007). Content only, so these tests check the shipped lines and, when
    /// this machine has the content folders, the real goal file and the live goals.json / sources.json.
    /// Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private static readonly uint[] EnvoyWcids = { 2000005, 2000006, 2000007 };

        /// <summary>The Homecoming Pennant placed behind each knight.</summary>
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
            "Our message will reach its destination. Well fought, friends!",
            "The shrine's waters steady me. I can hold a while longer.",
            "Strength returns! My thanks, defender.",
            "Asheron's grace flows through the shrine. Back to the line!",
            "The corrupted one has fallen! I feel my strength return.",
            "Its foul hold is broken. Stand fast, the tide turns!",
            "Well struck! With that one gone we can breathe again."
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

        /// <summary>Traders' words that do not belong in a Royal Knight's mouth.</summary>
        private static readonly string[] OffThemeWords = { "caravan", "wagon", "merchant", "market", "goods" };

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

                    foreach (var word in OffThemeWords)
                        Assert.IsFalse(line.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0, $"off-theme wording \"{word}\": {line}");
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
        /// The real goal file, when this machine has it: it loads with no diagnostics, uses the three knights, the
        /// pennants and the shrines, uses {family} in every line pool, matches the mirrors above, and has the tuning
        /// the balance simulation (WorldEventHoldBalanceTests) settled on.
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

            Assert.IsTrue(p.TargetDecor.All(d => d.Wcid == EnvoyPennantWcid), "a pennant behind each knight");

            // Every pool names the run's monster family at least once.
            foreach (var pool in new[] { p.Lines.Spawn, p.Lines.Health, p.Lines.Destroyed, p.Lines.Heal, p.Lines.Success })
                Assert.IsTrue(pool.Any(l => l.IndexOf("{family}", StringComparison.OrdinalIgnoreCase) >= 0), string.Join(" / ", pool));

            var breach = p.SpecialWaves.Single(w => w.TypeKind == HoldWaveType.Breach);
            Assert.IsTrue(breach.Lines.All(l => l.Contains("{side}")), "breach warnings name the side");

            var shippedTargetLines = p.Lines.Spawn.Concat(p.Lines.Health).Concat(p.Lines.Destroyed).Concat(p.Lines.Heal).Concat(p.Lines.Success)
                .Concat(p.Lines.Shrine).Concat(p.Lines.CorruptedKill);
            CollectionAssert.AreEquivalent(EnvoyTargetLines, shippedTargetLines.ToList(), "the target-line mirror matches the file");

            var shippedWaveLines = p.SpecialWaves.SelectMany(w => w.Announce.Concat(w.Lines)).ToList();
            CollectionAssert.AreEquivalent(EnvoySpecialWaveLines, shippedWaveLines, "the special-wave mirror matches the file");

            CollectionAssert.AreEquivalent(EnvoyTemplates, new[] { goal.ProgressTemplate, goal.SuccessTemplate, goal.FailTemplate });

            AssertEnvoyTuning(goal);
        }

        /// <summary>
        /// The live content (ace-content next to the repo), when present: defend_envoys loads, and the ambush source
        /// offers it. It is the only Hold goal shipped.
        /// </summary>
        [TestMethod]
        public void LiveContent_EnvoysLoad_AndAmbushOffersThem_WhenPresent()
        {
            var goalsPath = FindContentFile(Path.Combine("ace-content", "events", "axes", "goals.json"));
            var sourcesPath = FindContentFile(Path.Combine("ace-content", "events", "axes", "sources.json"));

            if (goalsPath == null || sourcesPath == null)
                return;

            var store = WorldEventAxisStore.Parse(File.ReadAllText(sourcesPath), null, File.ReadAllText(goalsPath), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("defend_envoys"), string.Join(" | ", store.Diagnostics));
            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("defend_envoys")), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(1, store.Goals.Values.Count(g => g.TypeKind == GoalType.Hold), "defend_envoys is the only Hold goal");

            CollectionAssert.AreEqual(EnvoyWcids, store.Goals["defend_envoys"].Protect.Wcids);

            Assert.IsTrue(store.Sources.ContainsKey("ambush"), string.Join(" | ", store.Diagnostics));
            CollectionAssert.Contains(store.Sources["ambush"].CompatibleGoals, "defend_envoys");
            Assert.IsTrue(store.Sources["ambush"].CompatibleGoals.All(store.Goals.ContainsKey), "ambush offers only goals that exist");

            AssertEnvoyTuning(store.Goals["defend_envoys"]);
        }

        /// <summary>
        /// The tuning the balance simulation settled on (see WorldEventHoldBalanceTests and HOLD-SUMMARY.md):
        /// always 3 targets on the 16 m ring; 6 waves (7 with a counted player, never more than 10); waves of 2 + 2.5
        /// per player (5 for one, 7 for two, 15 for five); the next wave after 40 s AND once 80% of the field is
        /// dead, never later than 75 s; time-based damage of 150 s for one attacker, 15 s less per extra player,
        /// never under 100 s, and never faster than 60 s however many pile on; and the healing safeguards.
        /// </summary>
        private static void AssertEnvoyTuning(GoalDef goal)
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
            Assert.AreEqual(6, solo.Objective.TargetWaves, $"{goal.Id}: waves before anyone counts");

            solo.Objective.OnAudienceResampled(1);
            Assert.AreEqual(7, solo.Objective.TargetWaves, $"{goal.Id}: one counted player (or --level) gets 6 + ceil(0.25)");

            var crowd = new HoldHarness(goal);
            crowd.Objective.OnAudienceResampled(500);
            Assert.AreEqual(ProtectDef.MaxHoldWaves, crowd.Objective.TargetWaves, $"{goal.Id}: hard cap");

            Assert.AreEqual(4, p.WaveSize.Resolve(1), $"{goal.Id}: a solo (or --level) wave is 4 monsters (an assault wave adds 2 corrupted)");
            Assert.AreEqual(6, p.WaveSize.Resolve(2), $"{goal.Id}: two players, 6");
            Assert.AreEqual(14, p.WaveSize.Resolve(5), $"{goal.Id}: five players, 14");
            Assert.AreEqual(26, p.WaveSize.Resolve(10), $"{goal.Id}: ten players");
            Assert.IsTrue(p.MaxAlive >= p.WaveSize.Resolve(5) * 2, $"{goal.Id}: room for two overlapping waves of a group of 5");

            Assert.AreEqual(40, p.WaveIntervalSeconds, 1e-9);
            Assert.AreEqual(0.2, p.NextWaveAliveShare, 1e-9, "80% dead");
            Assert.IsTrue(p.NextWaveRequiresBoth, "whichever comes later");
            Assert.AreEqual(75, p.MaxWaveIntervalSeconds, 1e-9);
            Assert.AreEqual(8, p.MinWaveGapSeconds, 1e-9);

            // A group that wipes each wave the moment it lands still waits the 40 s interval.
            var fast = SimulateCadence(p, total: 7, killAllAtOnce: true, holdSeconds: goal.HoldSeconds);
            CollectionAssert.AreEqual(new[] { 0, 40, 80, 120, 160, 200, 240 }, fast, $"{goal.Id}: a breather even for a fast group");

            // A group that kills nothing gets the next wave at the 75 s ceiling.
            var slow = SimulateCadence(p, total: 7, killAllAtOnce: false, holdSeconds: goal.HoldSeconds);
            CollectionAssert.AreEqual(new[] { 0, 75, 150, 225, 300, 375 }, slow.Take(6).ToArray(), $"{goal.Id}: the ceiling keeps the run moving");

            Assert.AreEqual("Wave 7 of 7", WorldEventHoldRules.WaveProgress(9, 7), "never above the total");

            // Damage.
            Assert.AreEqual(200, p.EffectiveUndefendedSeconds(1), 1e-9, "solo (or --level): 200 s for one attacker");
            Assert.AreEqual(135, p.EffectiveUndefendedSeconds(2), 1e-9, "two players: 135 s");
            Assert.AreEqual(100, p.EffectiveUndefendedSeconds(3), 1e-9, "three players: the 100 s floor");
            Assert.AreEqual(100, p.EffectiveUndefendedSeconds(10), 1e-9, "never under the 100 s floor");
            Assert.AreEqual(60, p.MinDeathSeconds, 1e-9);

            // Keeping them alive, and the safeguards.
            Assert.AreEqual(0.10, p.HealBetweenWaves, 1e-9);
            Assert.AreEqual(0.10, p.CorruptedKillHeal, 1e-9);
            Assert.AreEqual(0.15, p.ShrineHeal, 1e-9);
            Assert.AreEqual(45, p.ShrineCooldownSeconds, 1e-9);
            Assert.AreEqual(0.01, p.RescueHeal, 1e-9);
            Assert.AreEqual(0.10, p.RescueCapPerWave, 1e-9);
            Assert.AreEqual(10, p.ClearRadius, 1e-9);
            Assert.AreEqual(0.005, p.ClearRegenPerSecond, 1e-9);
            Assert.AreEqual(0.35, p.HealBudgetPerWave, 1e-9);
            Assert.AreEqual(0.5, p.HealReductionAfterHit, 1e-9);
            Assert.AreEqual(5, p.HealReductionSeconds, 1e-9);

            Assert.IsNotNull(p.Shrine);
            Assert.AreEqual(855u, p.Shrine.Wcid, "the Altar of Asheron, not the Essence Font and its constant blue flame");
            Assert.AreEqual(1.0, p.Shrine.Scale, 1e-9, "normal size");
            Assert.AreEqual(20, p.Shrine.ReadyPulseSeconds, 1e-9, "a small cue every 20 s while ready, nothing constant");
            Assert.AreEqual("SkillUpGreen", p.Shrine.ReadyEffect);
            Assert.AreEqual("HealthUpRed", p.Shrine.UsedEffect);
        }

        /// <summary>
        /// The Hold cadence second by second: HoldObjective.WantsAnotherWave (the total) and
        /// WorldEventHoldRules.HoldNextWaveDue (the goal's rule), as WorldEvent.TickWaves runs them.
        /// </summary>
        private static List<int> SimulateCadence(ProtectDef p, int total, bool killAllAtOnce, int holdSeconds)
        {
            var started = new List<int> { 0 };
            var lastAt = 0.0;
            var size = p.WaveSize?.Resolve(1) ?? 11;
            var live = killAllAtOnce ? 0 : size;

            for (var second = 1; second <= holdSeconds; second++)
            {
                if (!HoldObjective.WantsAnotherWave(started.Count, total, second, p.MinSeconds))
                    continue;

                if (!WorldEventHoldRules.HoldNextWaveDue(second, lastAt, p.WaveIntervalSeconds, p.MinWaveGapSeconds, live, size,
                        p.NextWaveAliveShare, p.NextWaveRequiresBoth, p.MaxWaveIntervalSeconds))
                    continue;

                started.Add(second);
                lastAt = second;
                live = killAllAtOnce ? 0 : live + size;
            }

            return started;
        }
    }
}
