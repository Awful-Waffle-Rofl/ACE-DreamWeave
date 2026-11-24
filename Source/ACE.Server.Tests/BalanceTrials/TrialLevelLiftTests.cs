using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The offline level lift on synthetic charts (dat-free). Every expected value is computed in the test from the live
    /// function the server uses (the CheckForLevelup loop criterion over the chart, EnlightenmentXpCurve.GetSkillCreditsForLevel,
    /// ClassAbilityMilestones.EntitledCount, Player.XpCostForClassAbilityPoints, EnlightenmentRetirement.EquivalentLevel),
    /// never from TrialLevelLift itself.
    /// </summary>
    [TestClass]
    public class TrialLevelLiftTests
    {
        private static readonly uint[] TrainedChart = { 0, 10, 30, 60, 100 };
        private static readonly uint[] SpecChart = { 0, 20, 60, 120, 200, 300 };
        private static readonly uint[] AttrChart = { 0, 50, 150, 400 };
        private static readonly uint[] VitalChart = { 0, 40, 100, 250 };

        private static readonly Dictionary<Skill, int> Costs = new Dictionary<Skill, int>
        {
            [Skill.WarMagic] = 8, [Skill.VoidMagic] = 8, [Skill.LifeMagic] = 6, [Skill.ItemTinkering] = 4, [Skill.Salvaging] = 0, [Skill.Alchemy] = 12,
        };

        private static readonly XpLaneTunables NoLane = new XpLaneTunables(long.MaxValue / 4, 1.4, 1.4, 1.4, 1, 1, 275);
        private static readonly CohortTargets SpendNothing = new CohortTargets(null, null, Array.Empty<Skill>());

        /// <summary>A retail-shaped 276-entry base (level 0..275), extended by the live Extend; credits on every 10th level up to 275.</summary>
        private static XpCharts Charts(uint[] trained = null, uint[] spec = null, uint[] attr = null, uint[] vital = null)
        {
            var baseTotals = Enumerable.Range(0, 276).Select(l => (ulong)l * (ulong)l * 1_000UL).ToList();
            var credits = Enumerable.Range(0, 276).Select(l => l > 0 && l % 10 == 0 ? 1u : 0u).ToList();
            return XpCharts.FromBase(baseTotals, credits, trained ?? TrainedChart, spec ?? SpecChart, attr ?? AttrChart, vital ?? VitalChart, Costs);
        }

        /// <summary>The CheckForLevelup criterion, walked from level 0: raise while total >= totals[level + 1].</summary>
        private static int LiveLoopLevel(IReadOnlyList<ulong> totals, long total)
        {
            var level = 0;
            while (level + 1 < totals.Count && (ulong)total >= totals[level + 1])
                level++;
            return level;
        }

        private static TrialLiftInput Input(XpCharts c, int level, long progress, int target, IReadOnlyList<TraitState> traits = null,
            long unspentXp = 0, int unspentCredits = 0, int totalCredits = 50, int milestone = 12, int capXp = 0, int enlightenment = 0, IReadOnlyCollection<Skill> augmented = null) =>
            new TrialLiftInput(level, (long)c.LevelTotals[level] + progress, unspentXp, unspentCredits, totalCredits, milestone, capXp,
                traits ?? Array.Empty<TraitState>(), target, enlightenment, augmented);

        private static TrialLiftResult LiftOk(TrialLiftInput i, XpCharts c, XpLaneTunables t, CohortTargets k)
        {
            var r = TrialLevelLift.Lift(i, c, t, k, out var refusal);
            Assert.IsNotNull(r, $"refused: {refusal}");
            Assert.IsNull(refusal);
            return r;
        }

        [TestMethod]
        public void Level_and_total_match_the_live_loop_criterion()
        {
            var c = Charts();
            var i = Input(c, 375, 12345, 400, unspentXp: 777);
            var r = LiftOk(i, c, NoLane, SpendNothing);

            Assert.AreEqual(400, r.Level);
            Assert.AreEqual(400, LiveLoopLevel(c.LevelTotals, r.TotalXp), "the live loop puts the lifted total on the target level");
            Assert.IsTrue((ulong)r.TotalXp >= c.LevelTotals[400] && (ulong)r.TotalXp < c.LevelTotals[401]);
            Assert.AreEqual(r.TotalXp - i.TotalXp, r.XpGranted);
            Assert.AreEqual(777 + r.XpGranted, r.UnspentXp, "everything the lift grants is unassigned");

            // The precondition reads the same criterion: the anchor's stored level is the loop level of its total.
            Assert.AreEqual(375, LiveLoopLevel(c.LevelTotals, i.TotalXp));
        }

        [TestMethod]
        public void Progress_within_the_level_is_preserved_and_clamped()
        {
            var c = Charts();

            // Preserved: progress inside 375 carries over inside 400.
            var r = LiftOk(Input(c, 375, 12345, 400), c, NoLane, SpendNothing);
            Assert.AreEqual((long)c.LevelTotals[400] + 12345, r.TotalXp);

            // Clamped: an edge chart whose level 400 is narrower than the anchor's progress inside 375. Real charts have
            // non-decreasing deltas past 275, so this is the defensive edge: the total must stay below level 401.
            var totals = c.LevelTotals.ToList();
            var progress = (long)(totals[376] - totals[375]) - 1;      // the top of level 375
            var narrow = 1000UL;
            for (var l = 401; l < totals.Count; l++)
                totals[l] = totals[l] - (totals[401] - totals[400]) + narrow;
            var edge = c with { LevelTotals = totals };
            Assert.IsTrue((ulong)progress > narrow, "premise: the anchor's progress is wider than level 400");

            var clamped = LiftOk(Input(edge, 375, progress, 400), edge, NoLane, SpendNothing);
            Assert.AreEqual((long)(totals[400] + narrow - 1), clamped.TotalXp, "clamped to the last XP of level 400");
            Assert.AreEqual(400, LiveLoopLevel(totals, clamped.TotalXp));
        }

        [TestMethod]
        public void Credits_are_the_live_sum_over_the_half_open_range()
        {
            var c = Charts();
            var live = 0L;
            for (var l = 376; l <= 400; l++)
                live += EnlightenmentXpCurve.GetSkillCreditsForLevel(l, c.LevelCredits);
            var inclusive = live + EnlightenmentXpCurve.GetSkillCreditsForLevel(375, c.LevelCredits);
            Assert.AreEqual(1L, live, "premise: 376..400 grants one credit (at 400)");
            Assert.AreEqual(2L, inclusive, "premise: counting 375 too would give two");

            var r = LiftOk(Input(c, 375, 0, 400, unspentCredits: 3, totalCredits: 70), c, NoLane, SpendNothing);
            Assert.AreEqual((int)live, r.CreditsGranted);
            Assert.AreEqual(3 + (int)live, r.UnspentCredits);
            Assert.AreEqual(70 + (int)live, r.TotalCredits);
        }

        [TestMethod]
        public void Milestone_cap_equals_EntitledCount_delta()
        {
            var expected = ClassAbilityMilestones.EntitledCount(280) - ClassAbilityMilestones.EntitledCount(270);
            Assert.AreEqual(1, expected, "premise: 275 is the one milestone between 270 and 280");
            Assert.AreEqual(expected, TrialLevelLift.MilestoneCapOwed(270, 280, ClassAbilityMilestones.EntitledCount(270)));

            // A counter behind the level is not paid by the lift (the login catch-up owns that), one ahead absorbs the delta.
            Assert.AreEqual(expected, TrialLevelLift.MilestoneCapOwed(270, 280, 5));
            Assert.AreEqual(0, TrialLevelLift.MilestoneCapOwed(270, 280, ClassAbilityMilestones.EntitledCount(280)));

            // Every legal lift starts at 275 or above, so it owes nothing; computed, not assumed.
            var c = Charts();
            var r = LiftOk(Input(c, 375, 0, 400, milestone: ClassAbilityMilestones.EntitledCount(375)), c, NoLane, SpendNothing);
            Assert.AreEqual(ClassAbilityMilestones.EntitledCount(400) - ClassAbilityMilestones.EntitledCount(375), r.MilestoneCapAdded);
            Assert.AreEqual(0, r.CapAdded);
            Assert.AreEqual(ClassAbilityMilestones.EntitledCount(375), r.MilestoneCapGranted);
        }

        [TestMethod]
        public void Credits_train_the_cohort_order_down_to_the_cohort_median_with_TrainSkill_effects()
        {
            // A trained chart no lift can top, so the TrainSkill row is visible as written: Trained, 0 XP, 0 ranks.
            var c = Charts(trained: new uint[] { 0, 10, 4_000_000_000 });
            var traits = new[] { new TraitState(TraitKind.Skill, (int)Skill.VoidMagic, (int)SkillAdvancementClass.Trained, 0) };
            var order = new[] { Skill.Alchemy, Skill.VoidMagic, Skill.WarMagic, Skill.LifeMagic, Skill.ItemTinkering, Skill.Salvaging };
            var i = Input(c, 375, 0, 400, traits, unspentCredits: 20);
            var credits = i.UnspentCredits + 1;                        // the lift's one credit (asserted in the credits test)

            var r = LiftOk(i, c, NoLane, new CohortTargets(2, null, order));

            // 21: Alchemy (12) fits -> 9; VoidMagic already trained; WarMagic (8) -> 1; 1 <= 2 stops.
            Assert.AreEqual(credits - Costs[Skill.Alchemy] - Costs[Skill.WarMagic], r.UnspentCredits);
            CollectionAssert.AreEqual(new[] { Skill.Alchemy, Skill.WarMagic }, r.TrainedSkills.ToArray());
            foreach (var s in r.TrainedSkills)
            {
                var change = r.Changes.Single(x => x.Kind == TraitKind.Skill && x.Id == (int)s);
                Assert.AreEqual((int)SkillAdvancementClass.Trained, change.Sac);
                Assert.AreEqual(0u, change.Spent);
                Assert.AreEqual(0, change.Ranks);
                Assert.IsTrue(change.NewlyTrained);
            }
            Assert.AreEqual(i.TotalCredits + 1, r.TotalCredits, "training spends available credits only");

            // Stops when nothing is affordable even above the median: 9 credits, order of 12-cost skills only.
            var r2 = LiftOk(Input(c, 375, 0, 400, unspentCredits: 8), c, NoLane, new CohortTargets(0, null, new[] { Skill.Alchemy }));
            Assert.AreEqual(9, r2.UnspentCredits);
            Assert.AreEqual(0, r2.TrainedSkills.Count);

            // A null cohort amount trains nothing.
            var r3 = LiftOk(Input(c, 375, 0, 400, unspentCredits: 20), c, NoLane, new CohortTargets(null, null, order));
            Assert.AreEqual(21, r3.UnspentCredits);
            Assert.AreEqual(0, r3.TrainedSkills.Count);
        }

        /// <summary>The live level-up loop condition, matched exactly: the hard-ceiling bound and the total-XP comparison.</summary>
        internal static readonly Regex LiveLevelLoop = new Regex(
            @"while \(\(Level \?\? 0\) \+ 1 <= EnlightenmentXpCurve\.HardCeilingLevel\s*&&\s*\(ulong\)\(TotalExperience \?\? 0\) >= EnlightenmentXpCurve\.ExtendedTotals\[\(Level \?\? 0\) \+ 1\]\)");

        [TestMethod]
        public void LoopLevel_mirrors_the_live_CheckForLevelup_loop_criterion()
        {
            // TrialLevelLift.LoopLevel re-states the while condition in Player.CheckForLevelup (next level reached when
            // total XP >= totals[level + 1], bounded by the hard ceiling). A test against its own copy cannot fail, so pin
            // the live text. Read from source for the same reason as the augmentation pairs above (Player static init).
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Player_Xp.cs")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldObjects/Player_Xp.cs by walking up from {AppContext.BaseDirectory}");
            var source = File.ReadAllText(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Player_Xp.cs"));

            Assert.AreEqual(1, LiveLevelLoop.Matches(source).Count, "the live CheckForLevelup while condition is not in its pinned shape");

            // Mutation proof on a scratch copy of the text: a strict comparison must no longer match.
            Assert.IsFalse(LiveLevelLoop.IsMatch(source.Replace("(ulong)(TotalExperience ?? 0) >= ", "(ulong)(TotalExperience ?? 0) > ")), "the pin must reject > in place of >=");
            Assert.IsFalse(LiveLevelLoop.IsMatch(source.Replace("+ 1 <= EnlightenmentXpCurve.HardCeilingLevel", "+ 1 < EnlightenmentXpCurve.HardCeilingLevel")), "the pin must reject a tighter ceiling bound");

            // The mirror's boundary agrees with the pinned operator: exactly equal to a threshold reaches that level.
            var totals = new ulong[] { 0, 10, 20, 30 };
            Assert.AreEqual(2, TrialLevelLift.LoopLevel(totals, 20));
            Assert.AreEqual(1, TrialLevelLift.LoopLevel(totals, 19));
            Assert.AreEqual(3, TrialLevelLift.LoopLevel(totals, 1000), "bounded by the table's last level");
        }

        [TestMethod]
        public void Augmentation_specialized_skills_are_never_trained()
        {
            // The lift's augmentation table is the live switch in Player.IsSkillSpecializedViaAugmentation, pair for pair.
            // Read from source: touching a Player static field runs Player's type initializer, which needs a database
            // and would poison every later Player use in the test process.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Player_Skills.cs")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldObjects/Player_Skills.cs by walking up from {AppContext.BaseDirectory}");
            var source = File.ReadAllText(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Player_Skills.cs"));
            var body = Regex.Match(source, @"public bool IsSkillSpecializedViaAugmentation\(Skill skill, out bool playerHasAugmentation\)(.*?)return AugSpecSkills\.Contains\(skill\);", RegexOptions.Singleline);
            Assert.IsTrue(body.Success, "IsSkillSpecializedViaAugmentation not found in its expected shape");
            var pairs = Regex.Matches(body.Groups[1].Value, @"case Skill\.(\w+):\s*playerHasAugmentation = (\w+) > 0;")
                .Select(m => (Enum.Parse<Skill>(m.Groups[1].Value), Enum.Parse<PropertyInt>(m.Groups[2].Value)))
                .ToList();
            Assert.AreEqual(5, pairs.Count, "premise: five augmentation-specialized skills");
            CollectionAssert.AreEquivalent(pairs, TrialLevelLift.SkillSpecializingAugmentations.Select(kv => (kv.Key, kv.Value)).ToList());
            var list = Regex.Match(source, @"public static List<Skill> AugSpecSkills = new List<Skill>\(\)\s*\{(.*?)\};", RegexOptions.Singleline);
            Assert.IsTrue(list.Success, "AugSpecSkills not found in its expected shape");
            CollectionAssert.AreEquivalent(
                Regex.Matches(list.Groups[1].Value, @"Skill\.(\w+)").Select(m => Enum.Parse<Skill>(m.Groups[1].Value)).ToList(),
                TrialLevelLift.SkillSpecializingAugmentations.Keys.ToList());

            var c = Charts();
            var order = new[] { Skill.ItemTinkering, Skill.Salvaging, Skill.WarMagic };
            var held = new[] { Skill.ItemTinkering, Skill.Salvaging };

            var r = LiftOk(Input(c, 375, 0, 400, unspentCredits: 20, augmented: held), c, NoLane, new CohortTargets(0, null, order));
            CollectionAssert.AreEqual(new[] { Skill.WarMagic }, r.TrainedSkills.ToArray());

            // Control: without the augmentations the same order trains them.
            var control = LiftOk(Input(c, 375, 0, 400, unspentCredits: 20), c, NoLane, new CohortTargets(0, null, order));
            CollectionAssert.AreEqual(order, control.TrainedSkills.ToArray());
        }

        [TestMethod]
        public void Traits_top_up_to_the_chart_in_order_and_report_unmaxed_when_xp_runs_out()
        {
            var big = 4_000_000_000u;
            var c = Charts(trained: new uint[] { 0, 10, big }, spec: new uint[] { 0, 20, big }, attr: new uint[] { 0, 50, big }, vital: new uint[] { 0, 40, big });
            var probe = LiftOk(Input(c, 375, 0, 400), c, NoLane, SpendNothing);
            var budget = probe.UnspentXp;                                // the XP the lift leaves to spend
            Assert.IsTrue(budget > 300 && budget < big, "premise: the budget is above the small costs and below a whole chart");

            // Ids out of order on purpose; the spend goes attributes, vitals, skills, each by id.
            var traits = new[]
            {
                new TraitState(TraitKind.Skill, (int)Skill.WarMagic, (int)SkillAdvancementClass.Specialized, big - 1),          // cost 1, after the XP is gone: unmaxed
                new TraitState(TraitKind.Vital, (int)PropertyAttribute2nd.MaxHealth, 0, big - 100),                            // cost 100
                new TraitState(TraitKind.Attribute, (int)PropertyAttribute.Endurance, 0, big - 50),                           // cost 50
                new TraitState(TraitKind.Attribute, (int)PropertyAttribute.Strength, 0, 0),                                   // cost big: unmaxed, continue
                new TraitState(TraitKind.Skill, (int)Skill.LifeMagic, (int)SkillAdvancementClass.Trained, (uint)(big - (budget - 150))), // exactly the rest
                new TraitState(TraitKind.Skill, (int)Skill.Alchemy, (int)SkillAdvancementClass.Untrained, 0),                  // untrained: no chart
                new TraitState(TraitKind.Attribute, (int)PropertyAttribute.Focus, 0, big),                                    // already at the top
            };

            var r = LiftOk(Input(c, 375, 0, 400, traits), c, NoLane, SpendNothing);

            Assert.AreEqual(0, r.UnspentXp);
            CollectionAssert.AreEqual(
                new[] { (TraitKind.Attribute, (int)PropertyAttribute.Endurance), (TraitKind.Vital, (int)PropertyAttribute2nd.MaxHealth), (TraitKind.Skill, (int)Skill.LifeMagic) },
                r.Changes.Select(x => (x.Kind, x.Id)).ToArray());
            foreach (var ch in r.Changes)
            {
                Assert.AreEqual(big, ch.Spent);
                Assert.AreEqual(2, ch.Ranks, "ranks = chart count - 1");
                Assert.IsFalse(ch.NewlyTrained);
            }
            Assert.AreEqual((int)SkillAdvancementClass.Trained, r.Changes.Single(x => x.Id == (int)Skill.LifeMagic).Sac);
            CollectionAssert.AreEqual(
                new[] { (TraitKind.Attribute, (int)PropertyAttribute.Strength), (TraitKind.Skill, (int)Skill.WarMagic) },
                r.Unmaxed.Select(x => (x.Kind, x.Id)).ToArray());
        }

        [TestMethod]
        public void Xp_lane_buys_toward_the_cohort_median_at_the_live_price()
        {
            var t = new XpLaneTunables(1_000_000, 2.0, 2.0, 2.0, 1, 1, 275);
            var c = Charts();
            const int already = 3, cohort = 10;

            var budget = LiftOk(Input(c, 375, 0, 400, capXp: already), c, t, SpendNothing).UnspentXp;

            var longs = new (string Key, long Value)[] { (XpLaneTunables.BaseCostKey, t.BaseCost), (XpLaneTunables.Breakpoint1Key, t.Breakpoint1), (XpLaneTunables.Breakpoint2Key, t.Breakpoint2), (XpLaneTunables.MinLevelKey, t.MinLevel) };
            var doubles = new (string Key, double Value)[] { (XpLaneTunables.Ratio1Key, t.Ratio1), (XpLaneTunables.Ratio2Key, t.Ratio2), (XpLaneTunables.Ratio3Key, t.Ratio3) };
            int expectedN = 0;
            long expectedCost = 0;
            try
            {
                foreach (var (k, v) in longs) Assert.IsTrue(PropertyManager.ModifyLong(k, v), k);
                foreach (var (k, v) in doubles) Assert.IsTrue(PropertyManager.ModifyDouble(k, v), k);

                for (var n = 1; n <= cohort - already; n++)
                {
                    var cost = Player.XpCostForClassAbilityPoints(already, n);
                    if (cost > budget) break;
                    expectedN = n;
                    expectedCost = cost;
                }
            }
            finally
            {
                foreach (var (k, _) in longs) PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item);
                foreach (var (k, _) in doubles) PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item);
            }
            Assert.IsTrue(expectedN > 0 && expectedN < cohort - already, $"premise: the budget binds the purchase (n={expectedN})");

            var i = Input(c, 375, 0, 400, capXp: already);
            var r = LiftOk(i, c, t, new CohortTargets(null, cohort, Array.Empty<Skill>()));

            Assert.AreEqual(expectedN, r.CapBoughtThisLift);
            Assert.AreEqual(already + expectedN, r.CapBoughtWithXp);
            Assert.AreEqual(expectedN, r.CapAdded, "CAP earned and available grow by the points bought (no milestone owed)");
            Assert.AreEqual(expectedCost, r.CapXpSpent);
            Assert.AreEqual(budget - expectedCost, r.UnspentXp, "the purchase spends unassigned XP");
            Assert.AreEqual((long)c.LevelTotals[400], r.TotalXp, "and leaves total XP alone");

            // Below the lane's minimum level nothing is bought.
            var closed = LiftOk(i, c, t with { MinLevel = 401 }, new CohortTargets(null, cohort, Array.Empty<Skill>()));
            Assert.AreEqual(0, closed.CapBoughtThisLift);
            Assert.AreEqual(budget, closed.UnspentXp);
        }

        [TestMethod]
        public void Refuses_a_down_level_a_sub_275_anchor_and_an_inconsistent_stored_level()
        {
            var c = Charts();
            string refusal;

            Assert.IsNull(TrialLevelLift.Lift(Input(c, 380, 0, 375), c, NoLane, SpendNothing, out refusal));
            StringAssert.Contains(refusal, "down-level");

            Assert.IsNull(TrialLevelLift.Lift(Input(c, 270, 0, 300), c, NoLane, SpendNothing, out refusal));
            StringAssert.Contains(refusal, "below 275");

            var inconsistent = Input(c, 380, 0, 400) with { TotalXp = (long)c.LevelTotals[381] };
            Assert.IsNull(TrialLevelLift.Lift(inconsistent, c, NoLane, SpendNothing, out refusal));
            StringAssert.Contains(refusal, "stored level 380");

            Assert.IsNull(TrialLevelLift.Lift(Input(c, 380, 0, c.LevelTotals.Count), c, NoLane, SpendNothing, out refusal));
            StringAssert.Contains(refusal, "past the chart");
        }

        [TestMethod]
        public void At_target_is_a_no_op()
        {
            var c = Charts();
            var traits = new[] { new TraitState(TraitKind.Attribute, (int)PropertyAttribute.Strength, 0, 0) };
            var i = Input(c, 400, 55, 400, traits, unspentXp: 9_000_000_000, unspentCredits: 30, totalCredits: 90, milestone: 12, capXp: 2);

            var r = LiftOk(i, c, NoLane with { BaseCost = 1 }, new CohortTargets(0, 50, new[] { Skill.WarMagic }));

            Assert.AreEqual(i.Level, r.Level);
            Assert.AreEqual(i.TotalXp, r.TotalXp);
            Assert.AreEqual(i.UnspentXp, r.UnspentXp);
            Assert.AreEqual(i.UnspentCredits, r.UnspentCredits);
            Assert.AreEqual(i.TotalCredits, r.TotalCredits);
            Assert.AreEqual(i.MilestoneCapGranted, r.MilestoneCapGranted);
            Assert.AreEqual(i.CapBoughtWithXp, r.CapBoughtWithXp);
            Assert.AreEqual(0, r.CapAdded);
            Assert.AreEqual(0, r.Changes.Count);
            Assert.AreEqual(0, r.TrainedSkills.Count);
            Assert.IsFalse(r.Lifted);
        }

        [TestMethod]
        public void Lifted_total_clears_the_retirement_floor()
        {
            var c = Charts();
            var totals = c.LevelTotals;

            // An enlightenment count whose floor lies inside the lift, and one whose floor lies above the target.
            var inside = Enumerable.Range(1, 50).First(e => EnlightenmentRetirement.EquivalentLevel(e, totals) > 375);
            var above = Enumerable.Range(1, 200).First(e => EnlightenmentRetirement.EquivalentLevel(e, totals) > 400);
            var insideFloor = EnlightenmentRetirement.EquivalentLevel(inside, totals);
            Assert.IsTrue(insideFloor <= 400, $"premise: enlightenment {inside} floors at {insideFloor}, inside 375..400");

            var r = LiftOk(Input(c, 375, 0, 400, enlightenment: inside), c, NoLane, SpendNothing);
            Assert.IsTrue((ulong)r.TotalXp >= totals[insideFloor], "GrantEnlightenmentRetirementCredit's target is already met");

            Assert.IsNull(TrialLevelLift.Lift(Input(c, 375, 0, 400, enlightenment: above), c, NoLane, SpendNothing, out var refusal));
            StringAssert.Contains(refusal, "retirement floor");
        }
    }
}
