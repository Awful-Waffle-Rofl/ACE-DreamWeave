using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins ArchetypeUpliftContext.ForAt: the uplift info carried from the run level L to a stretched pick's mapped level M
    /// along the stat curve (Run, AllRun, the attack-skill ceiling and health), as a clone that never touches the shared
    /// For(wcid) instance, and For itself whenever there is nothing to carry.
    /// </summary>
    [TestClass]
    public class ArchetypeForAtTests
    {
        private const int L = 400;
        private const int M = 460;

        private static DungeonPopulationLimits Limits(bool statCurve = true)
            => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, archetypeUplift: true, statCurve: statCurve);

        /// <summary>
        /// FamilyStretchFixture.LevelProfile with the shipped roster's authored health filled in. LevelProfile carries health
        /// 0, so every measured health median is 0 and the run standard's health stays 0 (HealthOnCurve needs a measured
        /// median); the health test needs a real one.
        /// </summary>
        private static DungeonStatProfile HealthProfile(uint wcid)
        {
            var p = FamilyStretchFixture.LevelProfile(wcid);

            return p.Level <= 0 ? p : new DungeonStatProfile(p.Level, ShippedRosterFixture.HealthOf(wcid), p.Skills, p.MaxBodyDamage, p.MaxBaseArmor,
                p.MaxSpellTier, p.HealthRate, p.Attributes);
        }

        private static ArchetypeUpliftContext Context(DungeonPopulationLimits limits, Func<uint, DungeonStatProfile> profileOf = null)
        {
            // The stat-curve carry reads skill formulas from the game tables.
            TestGameTables.EnsureInitialized();

            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "Content/events/axes/species not found from the test output directory");

            return DungeonPopulationBuilder.BuildArchetypeContext(FamilyStretchFixture.Spec(L), tables, limits, null, profileOf ?? FamilyStretchFixture.LevelProfile);
        }

        /// <summary>The highest-level drudge trash wcid the context resolves (non-null For), wcid ascending on ties.</summary>
        private static uint Drudge(ArchetypeUpliftContext ctx)
        {
            var w = ShippedRosterFixture.Tables["drudge"].Members
                .Where(m => m.Role == 0)
                .OrderByDescending(m => m.Level).ThenBy(m => m.Wcid)
                .Select(m => m.Wcid)
                .FirstOrDefault(x => ctx.For(x) != null);

            Assert.AreNotEqual(0u, w, "no drudge wcid resolves an archetype uplift info");
            return w;
        }

        [TestMethod]
        public void BuildArchetypeContext_passes_the_pivot_only_with_the_stat_curve_on()
        {
            Assert.IsTrue(Context(Limits()).CurvePivot > 0, "stat curve on: the context carries the pivot");
            Assert.AreEqual(0, Context(Limits(statCurve: false)).CurvePivot, "stat curve off: 0 = do not carry");
        }

        [TestMethod]
        public void ForAt_run_level_is_For()
        {
            var ctx = Context(Limits());
            var w = Drudge(ctx);

            Assert.AreSame(ctx.For(w), ctx.ForAt(w, L));
        }

        [TestMethod]
        public void ForAt_carries_run_axes_up()
        {
            var ctx = Context(Limits());
            var w = Drudge(ctx);
            var at = ctx.For(w);
            var up = ctx.ForAt(w, M);

            Assert.IsNotNull(up);
            Assert.AreNotSame(at, up, "ForAt returns a clone, never the shared For instance");
            Assert.IsTrue(up.Run.Core.MaxBodyDamage > at.Run.Core.MaxBodyDamage,
                $"carried MaxBodyDamage {up.Run.Core.MaxBodyDamage} vs {at.Run.Core.MaxBodyDamage} at L");
            Assert.AreEqual(M, up.RunLevel);
            Assert.AreSame(at.Own, up.Own, "Own is the creature's own authored axes: shared, not carried");
            // The shipped-roster sample is EMPTY here (TestGameTables carries no attack-skill formulas, so
            // AttackSkillSampleOf drops every value and the ceiling is 0 at every level); the ceiling carry is pinned
            // strictly on a hand-built sample below (HandBuilt*).
            Assert.AreEqual(ctx.AttackSkillCeilingAt(M), up.AttackSkillCeiling);
            Assert.IsTrue(up.AllRun.MaxBodyDamage > at.AllRun.MaxBodyDamage,
                $"carried AllRun MaxBodyDamage {up.AllRun.MaxBodyDamage} vs {at.AllRun.MaxBodyDamage}");

            // Everything that is not a run-level standard is copied unchanged.
            Assert.AreEqual(at.Wcid, up.Wcid);
            Assert.AreEqual(at.Source, up.Source);
            Assert.AreEqual(at.OwnLevel, up.OwnLevel);
            Assert.AreSame(at.AllOwn, up.AllOwn);
            Assert.AreEqual(at.OffenseRatio, up.OffenseRatio);
            Assert.AreEqual(at.DefenseRatio, up.DefenseRatio);
            Assert.AreEqual(at.CapRatio, up.CapRatio);
            Assert.AreEqual(at.DefenseCapRatio, up.DefenseCapRatio);
        }

        [TestMethod]
        public void ForAt_never_mutates_the_shared_For_instance()
        {
            var ctx = Context(Limits());
            var w = Drudge(ctx);
            var at = ctx.For(w);
            var run = at.Run;
            var allRun = at.AllRun;
            var ceiling = at.AttackSkillCeiling;

            ctx.ForAt(w, M);

            Assert.AreSame(at, ctx.For(w));
            Assert.AreEqual(L, at.RunLevel);
            Assert.AreSame(run, at.Run);
            Assert.AreSame(allRun, at.AllRun);
            Assert.AreEqual(ceiling, at.AttackSkillCeiling);
        }

        [TestMethod]
        public void ForAt_is_memoized_per_wcid_and_level()
        {
            var ctx = Context(Limits());
            var w = Drudge(ctx);

            var results = new ArchetypeUpliftInfo[16];
            Parallel.For(0, results.Length, i => results[i] = ctx.ForAt(w, M));

            foreach (var r in results)
                Assert.AreSame(results[0], r);

            Assert.AreNotSame(ctx.ForAt(w, M), ctx.ForAt(w, M - 10));
            Assert.AreEqual(M - 10, ctx.ForAt(w, M - 10).RunLevel);
        }

        [TestMethod]
        public void ForAt_with_curve_off_is_For()
        {
            var ctx = Context(Limits(statCurve: false));
            var w = Drudge(ctx);

            Assert.AreSame(ctx.For(w), ctx.ForAt(w, M));
        }

        [TestMethod]
        public void ForAt_unresolved_wcid_stays_null()
        {
            var ctx = Context(Limits());

            Assert.IsNull(ctx.For(0));
            Assert.IsNull(ctx.ForAt(0, M));
        }

        [TestMethod]
        public void ForAt_health_follows_the_curve()
        {
            var limits = Limits();
            var ctx = Context(limits, HealthProfile);
            var w = Drudge(ctx);
            var at = ctx.For(w);
            var up = ctx.ForAt(w, M);

            Assert.IsTrue(at.Run.Health > 0, "fixture: the run standard carries health");

            var expected = DungeonHealthCurve.Target(M, limits) / DungeonHealthCurve.Target(L, limits);
            Assert.AreEqual(expected, up.Run.Health / at.Run.Health, 1e-9);
        }

        // ---- Hand-built sample: the shipped fixture's attack-skill sample is empty under TestGameTables. ----

        private const int HandPivot = 326;

        private static SkillFormula HandFormulaOf(Skill skill) => new SkillFormula(PropertyAttribute.Strength, PropertyAttribute.Coordination, 3);

        private static readonly Dictionary<PropertyAttribute, uint> HandAttrs = new Dictionary<PropertyAttribute, uint>
        {
            [PropertyAttribute.Strength] = 300,
            [PropertyAttribute.Coordination] = 300,
        };

        /// <summary>
        /// Level-400 run, not extrapolated, curve pivot <see cref="HandPivot"/> (or 0): five melee (Sword 400) and two
        /// casters (WarMagic 400) in the natural band, every effective attack value (300 + 300) / 3 + 400 = 600.
        /// </summary>
        private static ArchetypeUpliftContext HandBuilt(int curvePivot)
        {
            DungeonStatProfile Melee(int level) => new DungeonStatProfile(level, 6000,
                new Dictionary<Skill, uint> { [Skill.Sword] = 400, [Skill.MeleeDefense] = 500 }, 50, 1000, 0, 0.0, HandAttrs,
                new ArchetypeFacts(null, false, false, null, effectiveMaxDamage: 50));
            DungeonStatProfile Caster(int level) => new DungeonStatProfile(level, 2000,
                new Dictionary<Skill, uint> { [Skill.WarMagic] = 400, [Skill.MeleeDefense] = 200 }, 10, 100, 6, 0.0, HandAttrs,
                new ArchetypeFacts(null, false, false, new Dictionary<Skill, int> { [Skill.WarMagic] = 1 },
                    new[] { new SpellRoll(0.6, Skill.WarMagic) }, effectiveMaxDamage: 10, bestProjectileMean: 200));

            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [1] = Melee(400), [2] = Melee(401), [3] = Melee(402), [4] = Melee(403), [5] = Melee(404),
                [11] = Caster(400), [12] = Caster(405),
            };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["t"] = new SpeciesTableDef { Id = "t", Members = profiles.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Level = profiles[w].Level, Role = 0 }).ToList() },
            };

            var band = new DungeonRosterBand(1.0, 1.15);
            Func<uint, DungeonStatProfile> profileOf = w => profiles.TryGetValue(w, out var p) ? p : null;
            var roster = new RosterArchetypes(tables, band, 0.6, profileOf, HandFormulaOf);
            Func<int, DungeonArchetypeStandards> measure = level => DungeonArchetypeStandards.Measure(tables, level, profileOf, roster, band, 0.6, formulaOf: HandFormulaOf);

            return new ArchetypeUpliftContext(L, L, false, 0.5, 0.0, 0.75, DungeonStatCurve.DefaultWeaponAttackRateAbove400,
                DungeonStatCurve.DefaultMeleeDefenseRateAbove400, false, measure(L), measure, roster, l => 0.0, 1.0, 1.0, 1.25, HandFormulaOf,
                curvePivot: curvePivot);
        }

        [TestMethod]
        public void HandBuilt_ForAt_attack_skill_ceiling_is_the_sample_carried_per_axis_to_M()
        {
            var ctx = HandBuilt(HandPivot);
            var at = ctx.For(1);
            var up = ctx.ForAt(1, M);

            Assert.IsNotNull(at, "fixture: wcid 1 resolves");
            Assert.AreEqual(7, ctx.RunMeasured.All.AttackSkillSample.Count, "fixture: five Sword and two WarMagic values");
            Assert.AreEqual(600u, at.AttackSkillCeiling, "fixture: every effective value is 600 at L");

            // Rebuilt independently: each value carried from L (not extrapolated) to M on its own axis with the damage
            // curve splitting at the context's pivot, through the same P75 reducer.
            double RatioOf(Skill skill)
            {
                var axis = DungeonStatCurve.AxisOf(skill);
                return axis.HasValue
                    ? DungeonStatCurve.Ratio(axis.Value, ctx.RunLevel, M, ctx.DefenseRate, ctx.MagicDefenseRate, ctx.DamageRate, ctx.CurvePivot,
                        ctx.WeaponAttackRateAbove400, ctx.MeleeDefenseRateAbove400)
                    : 1.0;
            }

            var expected = DungeonArchetypeStandards.AttackSkillCeiling(ctx.RunMeasured.All.AttackSkillSample, RatioOf);

            Assert.IsTrue(up.AttackSkillCeiling > at.AttackSkillCeiling, $"ceiling {up.AttackSkillCeiling} vs {at.AttackSkillCeiling}");
            Assert.AreEqual(expected, up.AttackSkillCeiling);
            Assert.AreEqual(expected, ctx.AttackSkillCeilingAt(M));
        }

        [TestMethod]
        public void HandBuilt_AttackSkillCeilingAt_with_curve_off_is_AttackSkillCeiling()
        {
            var ctx = HandBuilt(0);

            Assert.AreEqual(600u, ctx.AttackSkillCeiling, "fixture");
            Assert.AreEqual(ctx.AttackSkillCeiling, ctx.AttackSkillCeilingAt(M), "pivot 0: nothing to carry along");
            Assert.AreEqual(ctx.AttackSkillCeiling, ctx.AttackSkillCeilingAt(L));
            Assert.IsTrue(HandBuilt(HandPivot).AttackSkillCeilingAt(M) > 600u, "control: with a pivot the same call carries");
        }

        [TestMethod]
        public void ForAt_max_body_damage_is_carried_on_the_damage_axis()
        {
            var ctx = Context(Limits());
            var w = Drudge(ctx);
            var at = ctx.For(w);

            var ratio = DungeonStatCurve.Ratio(DungeonStatAxis.Damage, L, M, ctx.DefenseRate, ctx.MagicDefenseRate, ctx.DamageRate, ctx.CurvePivot,
                ctx.WeaponAttackRateAbove400, ctx.MeleeDefenseRateAbove400);
            var expected = at.Run.Core.MaxBodyDamage * ratio;

            Assert.IsTrue(ratio > 1.0, $"damage ratio {ratio}");
            Assert.AreEqual(expected, ctx.ForAt(w, M).Run.Core.MaxBodyDamage, 1.0);
        }

        [TestMethod]
        public void AttackSkillCeilingAt_run_level_matches_AttackSkillCeiling()
        {
            var ctx = Context(Limits());

            Assert.AreEqual(ctx.AttackSkillCeiling, ctx.AttackSkillCeilingAt(L));
            Assert.IsTrue(ctx.AttackSkillCeilingAt(M) >= ctx.AttackSkillCeiling);
        }
    }
}
