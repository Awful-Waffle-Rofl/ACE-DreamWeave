using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events stat curve and Threads-style uplift (world_events_stat_curve, world_events_threads_uplift,
    /// world_events_magic_attack_skill_cap): the curve-following standard and health in WorldEventBandMemo, the pure
    /// health/reachability decisions in WorldEventSpawner, and source-order pins for the per-creature sequence (a live
    /// Creature cannot be built in this harness). Reads no PropertyManager key: every memo entry point here takes the
    /// dial as an argument.
    /// </summary>
    [TestClass]
    public class WorldEventStatCurveTests
    {
        private const double Low = WorldEventRosterSelector.DefaultTrashBandLow;
        private const double High = WorldEventRosterSelector.DefaultTrashBandHigh;
        private const double Floor = WorldEventRosterSelector.DefaultBandLowFloor;

        [TestInitialize]
        public void Pin()
        {
            // The effective-skill medians read the skill formula table (DungeonBandStandard.DefaultFormulaOf).
            TestGameTables.EnsureInitialized();
            WorldEventBandMemo.Clear();
        }

        [TestCleanup]
        public void Restore() => WorldEventBandMemo.Clear();

        // ---- synthetic roster: one role-0 member per level 200..375, stats rising with level ------------------

        private static Dictionary<string, SpeciesTableDef> Tables(int top = 375)
            => new Dictionary<string, SpeciesTableDef>
            {
                ["t"] = new SpeciesTableDef
                {
                    Id = "t",
                    Members = Enumerable.Range(200, top - 200 + 1).Select(l => new SpeciesMemberDef { Wcid = (uint)l, Level = l, Role = 0 }).ToList(),
                },
            };

        private static DungeonStatProfile Resolve(uint wcid)
        {
            var level = (int)wcid;
            var skills = new Dictionary<Skill, uint>
            {
                [Skill.MeleeDefense] = 200u + (uint)level,
                [Skill.MissileDefense] = 180u + (uint)level,
                [Skill.MagicDefense] = 40u + (uint)level / 4,
                [Skill.HeavyWeapons] = 220u + (uint)level,
                [Skill.WarMagic] = 160u + (uint)level,
            };
            var attributes = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = 200u + (uint)level / 2,
                [PropertyAttribute.Endurance] = 210u + (uint)level / 2,
                [PropertyAttribute.Coordination] = 190u + (uint)level / 2,
                [PropertyAttribute.Quickness] = 180u + (uint)level / 2,
                [PropertyAttribute.Focus] = 170u + (uint)level / 2,
                [PropertyAttribute.Self] = 175u + (uint)level / 2,
            };

            return new DungeonStatProfile(level, 4000u + 20u * (uint)level, skills, 100u + (uint)level / 2, 300u + (uint)level,
                maxSpellTier: level >= 300 ? 8 : 7, healthRate: 5.0, attributes: attributes);
        }

        /// <summary>The Threads construction (DungeonPopulationBuilder's curveApplies branch) at its compiled defaults.</summary>
        private static DungeonStatCurve.Anchored ThreadsCurve(Dictionary<string, SpeciesTableDef> tables, int pivot)
            => new DungeonStatCurve.Anchored(
                DungeonBandStandard.Compute(tables, pivot, Resolve,
                    new DungeonRosterBand(DungeonPopulationLimits.DefaultRosterBandLow, DungeonPopulationLimits.DefaultRosterBandHigh),
                    DungeonPopulationLimits.DefaultBandLowFloorRatio),
                pivot, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375, DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300,
                DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot, DungeonStatCurve.DefaultWeaponAttackRateAbove400, DungeonStatCurve.DefaultMeleeDefenseRateAbove400);

        private static void AssertSameStandard(DungeonBandStandard expected, DungeonBandStandard actual, string context)
        {
            Assert.AreEqual(expected.SampleCount, actual.SampleCount, context + " SampleCount");
            Assert.AreEqual(expected.SampleLowRatio, actual.SampleLowRatio, context + " SampleLowRatio");
            Assert.AreEqual(expected.MaxBodyDamage, actual.MaxBodyDamage, context + " MaxBodyDamage");
            Assert.AreEqual(expected.MaxBaseArmor, actual.MaxBaseArmor, context + " MaxBaseArmor");
            Assert.AreEqual(expected.SpellTier, actual.SpellTier, context + " SpellTier");
            Assert.AreEqual(expected.HealthRate, actual.HealthRate, context + " HealthRate");
            CollectionAssert.AreEquivalent(expected.SkillMedians.ToList(), actual.SkillMedians.ToList(), context + " SkillMedians");
            CollectionAssert.AreEquivalent(expected.EffectiveSkillMedians.ToList(), actual.EffectiveSkillMedians.ToList(), context + " EffectiveSkillMedians");
            CollectionAssert.AreEquivalent(expected.AttributeMedians.ToList(), actual.AttributeMedians.ToList(), context + " AttributeMedians");
        }

        // ---- the pivot ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Pivot_IsTheThreadsPivot_OverTheWorldEventTables()
        {
            var anchor = WorldEventBandMemo.CurveAnchorFor(Tables(), Resolve);

            Assert.AreEqual(DungeonPopulationBuilder.ReachUpPivot(375, DungeonRosterSelector.BandHigh), anchor.Pivot);
            Assert.AreEqual(326, anchor.Pivot, "floor(375 / 1.15)");
            Assert.IsNotNull(anchor.Curve);
        }

        [TestMethod]
        public void Pivot_IgnoresNonTrashMembers_AndAnEmptyRosterHasNone()
        {
            var tables = Tables(300);
            tables["t"].Members.Add(new SpeciesMemberDef { Wcid = 999, Level = 500, Role = 1 });

            Assert.AreEqual(260, WorldEventBandMemo.CurveAnchorFor(tables, w => w == 999 ? Resolve(500) : Resolve(w)).Pivot, "floor(300 / 1.15); the role-1 500 is not trash");

            var empty = new Dictionary<string, SpeciesTableDef> { ["e"] = new SpeciesTableDef { Id = "e" } };
            var none = WorldEventBandMemo.CurveAnchorFor(empty, Resolve);

            Assert.AreEqual(0, none.Pivot);
            Assert.IsNull(none.Curve);
        }

        // ---- the regime -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Regime_Boundaries()
        {
            Assert.AreEqual(WorldEventBandMemo.CurveRegime.Measured, WorldEventBandMemo.RegimeFor(false, 400, 326), "dial off");
            Assert.AreEqual(WorldEventBandMemo.CurveRegime.Measured, WorldEventBandMemo.RegimeFor(true, 300, 326));
            Assert.AreEqual(WorldEventBandMemo.CurveRegime.RetailMagicDefense, WorldEventBandMemo.RegimeFor(true, 301, 326));
            Assert.AreEqual(WorldEventBandMemo.CurveRegime.RetailMagicDefense, WorldEventBandMemo.RegimeFor(true, 326, 326), "AT the pivot is not above it");
            Assert.AreEqual(WorldEventBandMemo.CurveRegime.Curve, WorldEventBandMemo.RegimeFor(true, 327, 326));
            Assert.AreEqual(WorldEventBandMemo.CurveRegime.RetailMagicDefense, WorldEventBandMemo.RegimeFor(true, 390, 0), "no pivot: no curve");
        }

        // ---- the standard ---------------------------------------------------------------------------------------

        [TestMethod]
        public void AbovePivot_EveryFieldEqualsTheThreadsConstruction()
        {
            var tables = Tables();
            var threads = ThreadsCurve(tables, 326);

            foreach (var level in new[] { 327, 350, 375, 390, 400 })
            {
                var we = WorldEventBandMemo.CurveStandardFor(tables, level, Low, High, Floor, Resolve, statCurve: true);

                AssertSameStandard(threads.StandardAt(level), we, $"level {level}");
            }
        }

        [TestMethod]
        public void AbovePivot_RisesWithLevel()
        {
            var tables = Tables();
            var at330 = WorldEventBandMemo.CurveStandardFor(tables, 330, Low, High, Floor, Resolve, statCurve: true);
            var at400 = WorldEventBandMemo.CurveStandardFor(tables, 400, Low, High, Floor, Resolve, statCurve: true);

            Assert.IsTrue(at400.MaxBodyDamage > at330.MaxBodyDamage);
            Assert.IsTrue(at400.EffectiveSkillMedianFor(Skill.MeleeDefense) > at330.EffectiveSkillMedianFor(Skill.MeleeDefense));
            Assert.AreEqual(at330.EffectiveSkillMedianFor(Skill.MagicDefense), at400.EffectiveSkillMedianFor(Skill.MagicDefense),
                "magic defense is frozen above 300 at the compiled default rate 0");
        }

        [TestMethod]
        public void RetailBand_IsStandardForWithRetailMagicDefense()
        {
            var tables = Tables();

            foreach (var level in new[] { 301, 310, 326 })
            {
                var measured = WorldEventBandMemo.StandardFor(tables, level, Low, High, Floor, Resolve);
                var expected = DungeonStatCurve.WithRetailMagicDefense(measured, level, DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300);
                var we = WorldEventBandMemo.CurveStandardFor(tables, level, Low, High, Floor, Resolve, statCurve: true);

                AssertSameStandard(expected, we, $"level {level}");
                Assert.AreNotEqual(measured.MedianFor(Skill.MagicDefense), we.MedianFor(Skill.MagicDefense),
                    $"the synthetic roster's measured magic defense differs from the retail curve, so the replacement is visible " +
                    $"(level {level}: measured eff {measured.EffectiveSkillMedianFor(Skill.MagicDefense)} init {measured.MedianFor(Skill.MagicDefense)}, " +
                    $"we eff {we.EffectiveSkillMedianFor(Skill.MagicDefense)} init {we.MedianFor(Skill.MagicDefense)}, n {measured.SampleCount})");
                Assert.AreEqual(measured.MaxBodyDamage, we.MaxBodyDamage, "only magic defense is replaced");
            }
        }

        [TestMethod]
        public void DialOff_OrAtOrBelow300_IsStandardForItself()
        {
            var tables = Tables();

            foreach (var level in new[] { 150, 250, 300, 310, 326, 390, 400 })
            {
                var plain = WorldEventBandMemo.StandardFor(tables, level, Low, High, Floor, Resolve);

                Assert.AreSame(plain, WorldEventBandMemo.CurveStandardFor(tables, level, Low, High, Floor, Resolve, statCurve: false), $"dial off at {level}");
                Assert.AreEqual(WorldEventBandMemo.MedianHealthFor(tables, level, Low, High, Resolve),
                    WorldEventBandMemo.CurveMedianHealthFor(tables, level, Low, High, Resolve, statCurve: false), $"dial off health at {level}");
            }

            foreach (var level in new[] { 150, 250, 300 })
                Assert.AreSame(WorldEventBandMemo.StandardFor(tables, level, Low, High, Floor, Resolve),
                    WorldEventBandMemo.CurveStandardFor(tables, level, Low, High, Floor, Resolve, statCurve: true), $"dial on at {level}");

            Assert.AreEqual(0, WorldEventBandMemo.CurveCounts.Standards, "nothing reached the curve maps");
        }

        [TestMethod]
        public void PinnacleStandard_IsUntouchedByTheCurve()
        {
            // Pinnacles call StandardFor with maxHighEdge 275 directly; the curve methods are a separate entry point.
            var tables = Tables();
            var before = WorldEventBandMemo.StandardFor(tables, 275, Low, High, Floor, Resolve, maxHighEdge: 275);

            WorldEventBandMemo.CurveStandardFor(tables, 390, Low, High, Floor, Resolve, statCurve: true);

            Assert.AreSame(before, WorldEventBandMemo.StandardFor(tables, 275, Low, High, Floor, Resolve, maxHighEdge: 275));
        }

        // ---- health ---------------------------------------------------------------------------------------------

        private static uint Curve(int level)
            => (uint)Math.Round(DungeonHealthCurve.Target(level, DungeonPopulationLimits.DefaultHealthCurveAnchorLow,
                DungeonPopulationLimits.DefaultHealthCurveAnchorHigh, DungeonPopulationLimits.DefaultHealthCurveTopLevel));

        [TestMethod]
        public void Health_AbovePivot_IsMaxOfTheCurveAndTheBandMedian()
        {
            var tables = Tables();

            foreach (var level in new[] { 327, 350, 375, 390, 400 })
            {
                var median = WorldEventBandMemo.MedianHealthFor(tables, level, Low, High, Resolve);

                Assert.AreEqual(Math.Max(Curve(level), median), WorldEventBandMemo.CurveMedianHealthFor(tables, level, Low, High, Resolve, statCurve: true), $"level {level}");
                Assert.AreEqual(Curve(level), WorldEventBandMemo.CurveHealthTarget(level));
            }

            Assert.AreEqual(13100u, WorldEventBandMemo.CurveHealthTarget(375), "the high anchor exactly");
        }

        [TestMethod]
        public void Health_JustAbovePivot_KeepsAHigherBandMedian()
        {
            // The synthetic roster's band [327, 400] median (4000 + 20 x level) is far above the curve's 6641 at 327.
            var tables = Tables();
            var median = WorldEventBandMemo.MedianHealthFor(tables, 327, Low, High, Resolve);

            Assert.IsTrue(median > Curve(327), $"precondition: median {median} above curve {Curve(327)}");
            Assert.AreEqual(median, WorldEventBandMemo.CurveMedianHealthFor(tables, 327, Low, High, Resolve, statCurve: true), "upward-only: the median stands");
        }

        [TestMethod]
        public void Health_CurveWins_WhereItExceedsTheMedian_AndWhereThereIsNoMedian()
        {
            // A flat low-health roster: median 2000 at 327, below the curve.
            // The memo caches profiles by wcid, so it starts empty for this resolver (TestInitialize cleared it).
            var tables = Tables();

            DungeonStatProfile Weak(uint wcid)
            {
                var p = Resolve(wcid);
                return new DungeonStatProfile(p.Level, 2000, p.Skills, p.MaxBodyDamage, p.MaxBaseArmor, p.MaxSpellTier, p.HealthRate, p.Attributes);
            }

            var median = WorldEventBandMemo.MedianHealthFor(tables, 327, Low, High, Weak);

            Assert.AreEqual(2000u, median, "precondition");
            Assert.AreEqual(Curve(327), WorldEventBandMemo.CurveMedianHealthFor(tables, 327, Low, High, Weak, statCurve: true));

            // Above the authored top (375) the band holds nobody: median 0, the curve stands.
            WorldEventBandMemo.Clear();
            Assert.AreEqual(0u, WorldEventBandMemo.MedianHealthFor(tables, 390, Low, High, Resolve), "precondition");
            Assert.AreEqual(Curve(390), WorldEventBandMemo.CurveMedianHealthFor(tables, 390, Low, High, Resolve, statCurve: true));
        }

        [TestMethod]
        public void Health_BelowPivotAndDialOff_IsTheBandMedian()
        {
            var tables = Tables();

            foreach (var level in new[] { 250, 301, 326 })
                Assert.AreEqual(WorldEventBandMemo.MedianHealthFor(tables, level, Low, High, Resolve),
                    WorldEventBandMemo.CurveMedianHealthFor(tables, level, Low, High, Resolve, statCurve: true), $"level {level}");

            foreach (var level in new[] { 250, 327, 350, 390, 400 })
                Assert.AreEqual(WorldEventBandMemo.MedianHealthFor(tables, level, Low, High, Resolve),
                    WorldEventBandMemo.CurveMedianHealthFor(tables, level, Low, High, Resolve, statCurve: false), $"dial off at {level}");
        }

        // ---- memo -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Memo_ReturnsTheSameObject_AndStaysBounded()
        {
            var tables = Tables();
            var a = WorldEventBandMemo.CurveStandardFor(tables, 390, Low, High, Floor, Resolve, statCurve: true);

            Assert.AreSame(a, WorldEventBandMemo.CurveStandardFor(tables, 390, Low, High, Floor, Resolve, statCurve: true));
            Assert.AreSame(WorldEventBandMemo.CurveAnchorFor(tables, Resolve), WorldEventBandMemo.CurveAnchorFor(tables, Resolve));

            for (var level = 301; level <= 301 + WorldEventBandMemo.MaxEntries + 20; level++)
            {
                WorldEventBandMemo.CurveStandardFor(tables, level, Low, High, Floor, Resolve, statCurve: true);
                WorldEventBandMemo.CurveMedianHealthFor(tables, level, Low, High, Resolve, statCurve: true);
            }

            Assert.IsTrue(WorldEventBandMemo.CurveCounts.Standards <= WorldEventBandMemo.MaxEntries);
            Assert.IsTrue(WorldEventBandMemo.CurveCounts.Health <= WorldEventBandMemo.MaxEntries);
            Assert.IsTrue(WorldEventBandMemo.Counts.Standards <= WorldEventBandMemo.MaxEntries);

            for (var i = 0; i < WorldEventBandMemo.MaxCurves + 5; i++)
                WorldEventBandMemo.CurveAnchorFor(Tables(300 + i), Resolve);

            Assert.IsTrue(WorldEventBandMemo.CurveCounts.Curves <= WorldEventBandMemo.MaxCurves);

            WorldEventBandMemo.Clear();
            Assert.AreEqual((0, 0, 0), WorldEventBandMemo.CurveCounts);
        }

        [TestMethod]
        public void Memo_KeysTheCurveByTableReference()
        {
            var first = WorldEventBandMemo.CurveAnchorFor(Tables(), Resolve);
            var second = WorldEventBandMemo.CurveAnchorFor(Tables(), Resolve);

            Assert.AreNotSame(first, second, "a reload publishes a new dictionary, which must not match the old one");
        }

        // ---- the pure per-creature decisions ---------------------------------------------------------------------

        [TestMethod]
        public void HealthWrite_IsMaxOfAuthoredAndFloor_AgainstTheLiveMax()
        {
            Assert.AreEqual(5000, WorldEventSpawner.UpliftHealthWrite(1000, 1000, 5000), "floored");
            Assert.IsNull(WorldEventSpawner.UpliftHealthWrite(8000, 8000, 5000), "already above the floor and unchanged");
            Assert.AreEqual(8000, WorldEventSpawner.UpliftHealthWrite(8000, 8150, 5000), "an Endurance raise moved the live max: back to authored");
            Assert.AreEqual(5000, WorldEventSpawner.UpliftHealthWrite(1000, 1150, 5000), "target from the AUTHORED max, not the raised one");
            Assert.IsNull(WorldEventSpawner.UpliftHealthWrite(1000, 1000, 0), "no floor and no raise: nothing to write");
        }

        [TestMethod]
        public void AttributeRaise_IsSkipped_WhenTheHealthTargetWouldBeUnreachable()
        {
            // Authored 100, no floor, Endurance raised 100 -> 400: the raised Endurance alone contributes 200 > 100.
            Assert.IsFalse(WorldEventSpawner.UpliftAttributeRaiseKeepsHealthReachable(100, 0, 100, 400));

            // The same creature with a 5000 floor: reachable.
            Assert.IsTrue(WorldEventSpawner.UpliftAttributeRaiseKeepsHealthReachable(100, 5000, 100, 400));

            // An Endurance already above the median is not raised, so the term is its own.
            Assert.IsTrue(WorldEventSpawner.UpliftAttributeRaiseKeepsHealthReachable(1000, 0, 500, 400));

            // Matches the Threads rule exactly.
            Assert.AreEqual(ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(3000, 400),
                WorldEventSpawner.UpliftAttributeRaiseKeepsHealthReachable(2000, 3000, 300, 400));
        }

        /// <summary>
        /// A fake creature's max health: Endurance contributes End / 2 (the CreatureVital formula), and the raise
        /// delegate moves Endurance, so the live max changes exactly as it does on a real creature.
        /// </summary>
        private sealed class FakeVitals
        {
            public uint StartingValue;
            public uint Endurance;
            public int MaxReads;
            public uint MaxValue { get { MaxReads++; return StartingValue + Endurance / 2; } }
            public uint MaxValueSilent => StartingValue + Endurance / 2;

            /// <summary>ApplyHealth's arithmetic: StartingValue moves by the delta, clamped at 1.</summary>
            public void ApplyHealth(int hp) => StartingValue = (uint)Math.Max(1, (int)StartingValue + hp - (int)MaxValueSilent);
        }

        [TestMethod]
        public void UpliftHealth_CapturesAuthoredMaxBeforeTheRaise_AndLandsOnTheFloorUpward()
        {
            var fake = new FakeVitals { StartingValue = 900, Endurance = 200 };   // authored max 1000
            var raised = false;

            var sequence = WorldEventSpawner.BeginUpliftHealth(() => fake.MaxValue, fake.Endurance, 500, 5000,
                () => { Assert.AreEqual(1, fake.MaxReads, "authored max read before the raise"); fake.Endurance = 500; raised = true; return 1; });

            Assert.IsTrue(raised && sequence.AttributesRan);
            Assert.AreEqual(1000u, sequence.AuthoredMax, "the PRE-raise max");
            Assert.AreEqual(1150u, fake.MaxValueSilent, "the raise moved the live max");

            var write = sequence.FinalWrite(fake.MaxValueSilent);
            Assert.AreEqual(5000, write);

            fake.ApplyHealth(write.Value);
            Assert.AreEqual(5000u, fake.MaxValueSilent, "max(authored 1000, floor 5000)");
        }

        [TestMethod]
        public void UpliftHealth_WritesBackDownToTheAuthoredMax_WhenTheRaiseOvershoots()
        {
            var fake = new FakeVitals { StartingValue = 7900, Endurance = 200 };  // authored max 8000, above the 5000 floor

            var sequence = WorldEventSpawner.BeginUpliftHealth(() => fake.MaxValue, fake.Endurance, 600, 5000,
                () => { fake.Endurance = 600; return 1; });

            Assert.AreEqual(8000u, sequence.AuthoredMax);
            Assert.AreEqual(8200u, fake.MaxValueSilent, "Endurance raise pushed the live max above authored");

            var write = sequence.FinalWrite(fake.MaxValueSilent);
            Assert.AreEqual(8000, write, "target is max(authored, floor) from the AUTHORED max, so health comes DOWN to it");

            fake.ApplyHealth(write.Value);
            Assert.AreEqual(8000u, fake.MaxValueSilent);
            Assert.IsNull(sequence.FinalWrite(fake.MaxValueSilent), "already exact: nothing more to write");
        }

        [TestMethod]
        public void UpliftHealth_SkipsTheRaise_WhenTheTargetWouldBeUnreachable()
        {
            var fake = new FakeVitals { StartingValue = 50, Endurance = 100 };    // authored max 100, no floor
            var called = false;

            var sequence = WorldEventSpawner.BeginUpliftHealth(() => fake.MaxValue, fake.Endurance, 400, 0, () => { called = true; return 6; });

            Assert.IsFalse(called, "End 400 alone contributes 200 > the 100 target");
            Assert.IsFalse(sequence.AttributesRan);
            Assert.AreEqual(0, sequence.AttributesRaised);
            Assert.AreEqual(100u, sequence.AuthoredMax);
            Assert.IsNull(sequence.FinalWrite(fake.MaxValueSilent), "nothing raised, no floor: health untouched");
        }

        [TestMethod]
        public void StandardLevel_IsTheUpliftLevel_ElseTheSlotLevel()
        {
            Assert.AreEqual(390, WorldEventSpawner.StandardLevelFor(390, 390));
            Assert.AreEqual(250, WorldEventSpawner.StandardLevelFor(0, 250));
            Assert.AreEqual(WorldEventSpawner.NoSlotLevel, WorldEventSpawner.StandardLevelFor(0, WorldEventSpawner.NoSlotLevel));
        }

        // ---- source pins ----------------------------------------------------------------------------------------

        private static string[] SpawnerLines() => File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEventSpawner.cs"));

        private static string Body(string[] lines, string signaturePrefix)
        {
            var start = Array.FindIndex(lines, l => l.Trim().StartsWith(signaturePrefix, StringComparison.Ordinal));
            Assert.IsTrue(start >= 0, $"missing {signaturePrefix}");

            var depth = 0;
            var opened = false;
            var body = new List<string>();

            for (var i = start; i < lines.Length; i++)
            {
                body.Add(lines[i]);
                depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
                opened |= lines[i].Contains('{');

                if (opened && depth == 0)
                    break;
            }

            return string.Join("\n", body);
        }

        [TestMethod]
        public void ApplyBandUplift_RunsTheThreadsSequenceInOrder()
        {
            var body = Body(SpawnerLines(), "private void ApplyBandUplift(");

            int At(string code)
            {
                var i = body.IndexOf(code, StringComparison.Ordinal);
                Assert.IsTrue(i >= 0, $"missing: {code}");
                Assert.AreEqual(i, body.LastIndexOf(code, StringComparison.Ordinal), $"must appear once: {code}");
                return i;
            }

            var spell = At("DungeonSpellTier.Raise(creature.Biota.PropertiesSpellBook, standard.SpellTier)");
            var authored = At("var authoredMax = creature.Health.MaxValue;");
            var reachable = At("healthSequence = BeginUpliftHealth(() => creature.Health.MaxValue, endurance, standard.AttributeMedianFor(PropertyAttribute.Endurance), median,");
            var raise = At("() => BandUplift.RaiseAttributes(creature, standard));");
            var gate = At("creature.LootGateLevel = authoredLevel;");
            var apply = At("var (skillsRaised, partsScaled) = BandUplift.Apply(creature, upliftLevel, standard);");
            var topUp = At("(toppedUp, ceilingsSet) = BandUplift.TopUpEffectiveSkillsAndCeil(creature, standard, guard.DefenseSkillCapOffset, guard.Enabled);");
            var health = At("var write = healthSequence.FinalWrite(creature.Health.MaxValue);");
            var legacy = At("var floorTarget = HealthFloorTarget(creature.Health.MaxValue, median);");

            Assert.IsTrue(spell < authored, "spell tier first");
            Assert.IsTrue(authored < reachable && reachable < raise, "authored max captured before the attribute raise, which is gated on reachability");
            Assert.IsTrue(raise < gate && gate < apply, "the loot gate is recorded before Apply raises Level");
            Assert.IsTrue(apply < topUp, "top-up after Apply");
            Assert.IsTrue(topUp < health && topUp < legacy, "health last");
            Assert.IsTrue(body.Contains("if (attributesRan)"), "the top-up is gated on the attribute raise having run, as Threads gates it");
            Assert.IsTrue(body.Contains("var threads = upliftDials.ThreadsUplift && !standard.IsEmpty;"), "world_events_threads_uplift gates the Threads steps");
        }

        [TestMethod]
        public void SpawnSite_ComputesOneStandard_AndHandsItToTheUpliftAndTheGuard()
        {
            var lines = SpawnerLines();

            int Find(string code)
            {
                var i = Array.FindIndex(lines, l => l.Trim() == code);
                Assert.IsTrue(i > 0, $"missing: {code}");
                Assert.AreEqual(i, Array.FindLastIndex(lines, l => l.Trim() == code), $"must appear once: {code}");
                return i;
            }

            var dials = Find("var upliftDials = WorldEventUplift.Current();");
            var standard = Find("var slotStandard = SlotStandardFor(evt, entry, kind, upliftDials);");
            var uplift = Find("ApplyBandUplift(evt, creature, entry.UpliftLevel, kind, slotStandard, upliftDials);");
            var guard = Find("ApplyCombatGuard(evt, creature, entry.SlotLevel, kind, slotStandard);");
            var magic = Find("ApplyWorldEventMagicAttackCeiling(evt, creature, entry.SlotLevel, kind, upliftDials);");
            var multiplier = Find("ApplyHealthMultiplier(evt, creature, healthMult);");

            Assert.IsTrue(dials < standard && standard < uplift && uplift < guard && guard < magic && magic < multiplier);

            var text = string.Join("\n", lines);

            Assert.IsFalse(text.Contains("WorldEventBandMemo.StandardFor("), "the spawner reads the standard ONLY through CurveStandardFor, once per creature");
            Assert.AreEqual(1, CountOf(text, "WorldEventBandMemo.CurveStandardFor("));
            Assert.IsTrue(Body(lines, "private void ApplyCombatGuard(").Contains("? slotStandard ?? DungeonBandStandard.Empty"), "the guard reads the shared standard");
        }

        [TestMethod]
        public void MagicAttackCeiling_CoversExactlyTheGuardedCreatures()
        {
            var body = Body(SpawnerLines(), "private uint? ApplyWorldEventMagicAttackCeiling(");

            Assert.IsTrue(body.Contains("if (!WorldEventCombatGuard.AppliesTo(kind, named))"));
            Assert.IsTrue(body.Contains("slotLevel < 0"), "an entry with no slot level is skipped, as the guard skips it");
            Assert.IsFalse(body.Contains("dials.Enabled"), "independent of world_events_strip_combat_traits");
        }

        [TestMethod]
        public void Simulate_ReportsThePivotAndTheDials()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), "Command", "Handlers", "WorldEventCommands.cs"));

            Assert.IsTrue(text.Contains("DungeonPopulationBuilder.AuthoredTopLevel(WorldEventManager.Store?.SpeciesTables, w => WorldEventSpawner.LiveProfileOf(w)?.Level ?? 0)"));
            Assert.IsTrue(text.Contains("WorldEventBandMemo.CurveBandHigh);"));
            Assert.IsTrue(text.Contains("lines.Add($\"stat uplift: curve {(upliftDials.StatCurve ? \"on\" : \"off\")} (pivot {(curvePivot > 0"));

            // The memo's maps are world-thread-only; a command must touch no memo state (constants are fine).
            var simulate = Body(File.ReadAllLines(Path.Combine(FindSourceRoot(), "Command", "Handlers", "WorldEventCommands.cs")), "private static void HandleSimulate(");
            var memoUses = System.Text.RegularExpressions.Regex.Matches(simulate, @"WorldEventBandMemo\.(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList();

            CollectionAssert.AreEqual(new[] { "CurveBandHigh" }, memoUses, "simulate reads only the CurveBandHigh constant from the memo class");
        }

        [TestMethod]
        public void TheCurveCode_ReadsNoThreadsTunable()
        {
            foreach (var file in new[] { "WorldEventBandMemo.cs", "WorldEventUplift.cs" })
            {
                var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", file));

                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i];
                    var commentAt = code.IndexOf("//", StringComparison.Ordinal);

                    if (commentAt >= 0)
                        code = code.Substring(0, commentAt);

                    Assert.IsFalse(code.Contains("\"dynamic_dungeons_"), $"{file}:{i + 1} reads a Threads tunable");
                    if (file == "WorldEventBandMemo.cs")
                        Assert.IsFalse(code.Contains("PropertyManager"), $"{file}:{i + 1}: the curve uses compiled defaults only");
                }
            }
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldEvents", "WorldEventBandMemo.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldEvents/WorldEventBandMemo.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "ACE.Server");
        }
    }
}
