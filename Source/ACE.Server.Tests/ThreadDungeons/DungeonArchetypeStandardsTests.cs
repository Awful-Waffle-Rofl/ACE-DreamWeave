using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// DungeonArchetypeStandards and ArchetypeUpliftContext over hand-built profiles: the one sample walk partitioned by
    /// archetype, each archetype widening its own low edge, the All standard equal to DungeonBandStandard.Compute over the
    /// same inputs, the ratings as means including zeros, the fallback chain and the common source, extrapolation above
    /// the pivot, and the health standard on the curve. No database, no dat, no PropertyManager.
    /// </summary>
    [TestClass]
    public class DungeonArchetypeStandardsTests
    {
        private static SkillFormula FormulaOf(Skill skill) => new SkillFormula(PropertyAttribute.Strength, PropertyAttribute.Coordination, 3);

        private static readonly Dictionary<PropertyAttribute, uint> Attrs = new Dictionary<PropertyAttribute, uint>
        {
            [PropertyAttribute.Strength] = 300,
            [PropertyAttribute.Coordination] = 300,
        };

        private static readonly DungeonRosterBand Natural = new DungeonRosterBand(1.0, 1.15);

        private static DungeonStatProfile Caster(int level, uint dval, int damageRating, double mean)
            => new DungeonStatProfile(level, 2000, new Dictionary<Skill, uint> { [Skill.WarMagic] = 400, [Skill.MeleeDefense] = 200 }, dval, 100, 6, 0.0, Attrs,
                new ArchetypeFacts(null, false, false, new Dictionary<Skill, int> { [Skill.WarMagic] = 1 },
                    new[] { new SpellRoll(0.6, Skill.WarMagic) }, effectiveMaxDamage: dval, bestProjectileMean: mean, damageRating: damageRating));

        private static DungeonStatProfile Melee(int level, uint dval)
            => new DungeonStatProfile(level, 6000, new Dictionary<Skill, uint> { [Skill.Sword] = 400, [Skill.MeleeDefense] = 500 }, dval, 1000, 0, 0.0, Attrs,
                new ArchetypeFacts(null, false, false, null, effectiveMaxDamage: dval, critRating: 6));

        /// <summary>
        /// Level-100 run. Melee: five in the natural band [100, 115]. Casters: two in band, three more at level 90, so the
        /// caster sample has to widen to x0.9 while the melee and the All samples do not. Ranged: one member only. A low
        /// population at levels 40-49 (five casters with HALF the melee damage of the five melee beside them) gives the
        /// ratio-transfer walk an adequate band below a thin one; no level-90-or-above ladder reaches it (x0.6 of 90 is 54).
        /// </summary>
        private static (Dictionary<string, SpeciesTableDef> Tables, Dictionary<uint, DungeonStatProfile> Profiles) Fixture()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [1] = Melee(100, 50), [2] = Melee(101, 50), [3] = Melee(102, 50), [4] = Melee(103, 50), [5] = Melee(104, 50),
                [11] = Caster(100, 10, 0, 200), [12] = Caster(105, 10, 0, 220),
                [13] = Caster(90, 10, 0, 180), [14] = Caster(91, 10, 10, 240), [15] = Caster(92, 10, 40, 260),
                [21] = new DungeonStatProfile(100, 3000, new Dictionary<Skill, uint> { [Skill.Bow] = 400 }, 20, 300, 0, 0.0, Attrs,
                    new ArchetypeFacts(null, true, true, null)),
                [31] = Caster(40, 5, 0, 100), [32] = Caster(41, 5, 0, 100), [33] = Caster(42, 5, 0, 100), [34] = Caster(43, 5, 0, 100), [35] = Caster(44, 5, 0, 100),
                [41] = Melee(45, 20), [42] = Melee(46, 20), [43] = Melee(47, 20), [44] = Melee(48, 20), [45] = Melee(49, 20),
            };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["t"] = new SpeciesTableDef
                {
                    Id = "t",
                    Members = profiles.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 })
                        .Append(new SpeciesMemberDef { Wcid = 99, Role = 3 })   // a named boss: never sampled
                        .ToList(),
                },
            };

            profiles[99] = Melee(100, 9999);

            return (tables, profiles);
        }

        private static (DungeonArchetypeStandards Standards, RosterArchetypes Roster, Dictionary<string, SpeciesTableDef> Tables, Dictionary<uint, DungeonStatProfile> Profiles) MeasureFixture(int level = 100)
        {
            var (tables, profiles) = Fixture();
            var roster = new RosterArchetypes(tables, Natural, 0.6, w => profiles.TryGetValue(w, out var p) ? p : null, FormulaOf);

            return (DungeonArchetypeStandards.Measure(tables, level, w => profiles.TryGetValue(w, out var p) ? p : null, roster, Natural, 0.6, formulaOf: FormulaOf),
                roster, tables, profiles);
        }

        [TestMethod]
        public void The_fixture_classifies_as_intended()
        {
            var (_, roster, _, _) = MeasureFixture();

            Assert.AreEqual(CreatureArchetype.Caster, roster.Classify(11).Archetype);
            Assert.AreEqual(CreatureArchetype.Caster, roster.Classify(13).Archetype);
            Assert.AreEqual(CreatureArchetype.Ranged, roster.Classify(21).Archetype);

            var melee = roster.Classify(1).Archetype;
            Assert.IsTrue(melee == CreatureArchetype.MeleeTank || melee == CreatureArchetype.MeleeDps, melee.ToString());
            Assert.IsTrue(new uint[] { 2, 3, 4, 5 }.All(w => roster.Classify(w).Archetype == melee), "identical melee members share one split");
        }

        [TestMethod]
        public void Each_archetype_is_reduced_over_its_own_members_and_widens_its_own_low_edge()
        {
            var (s, roster, _, _) = MeasureFixture();
            var meleeKind = roster.Classify(1).Archetype;

            var caster = s.Get(CreatureArchetype.Caster);
            Assert.AreEqual(5, caster.SampleCount);
            Assert.AreEqual(0.9, caster.SampleLowRatio, 1e-9, "two casters in band; the ladder widens to x0.9 to reach MinSample");
            Assert.AreEqual(10.0, caster.EffectiveDamage, 1e-9);
            Assert.AreEqual(6, caster.Core.SpellTier);

            var melee = s.Get(meleeKind);
            Assert.AreEqual(5, melee.SampleCount);
            Assert.AreEqual(1.0, melee.SampleLowRatio, 1e-9, "the melee sample is already full in the natural band");
            Assert.AreEqual(50.0, melee.EffectiveDamage, 1e-9);
            Assert.AreEqual(1000u, melee.Core.MaxBaseArmor);
            Assert.AreEqual(6000.0, melee.Health, 1e-9);

            Assert.AreEqual(1, s.Get(CreatureArchetype.Ranged).SampleCount, "the ladder bottoms out with one archer");
            Assert.IsFalse(s.IsAdequate(CreatureArchetype.Ranged));
            Assert.IsTrue(s.IsAdequate(CreatureArchetype.Caster));
            Assert.IsTrue(s.IsAdequate(CreatureArchetype.Unclassified), "everyone (All) has data");
            Assert.AreEqual(0, s.Get(CreatureArchetype.Hybrid).SampleCount);
        }

        [TestMethod]
        public void The_All_standard_is_DungeonBandStandard_Compute_over_the_same_inputs()
        {
            var (s, _, tables, profiles) = MeasureFixture();
            var compute = DungeonBandStandard.Compute(tables, 100, w => profiles.TryGetValue(w, out var p) ? p : null, Natural, 0.6, formulaOf: FormulaOf);

            Assert.AreEqual(compute.SampleCount, s.All.SampleCount);
            Assert.AreEqual(compute.SampleLowRatio, s.All.Core.SampleLowRatio, 1e-12);
            Assert.AreEqual(compute.MaxBodyDamage, s.All.Core.MaxBodyDamage);
            Assert.AreEqual(compute.MaxBaseArmor, s.All.Core.MaxBaseArmor);
            Assert.AreEqual(compute.SpellTier, s.All.Core.SpellTier);
            Assert.AreEqual(compute.HealthRate, s.All.Core.HealthRate, 1e-12);
            CollectionAssert.AreEquivalent(compute.SkillMedians.ToList(), s.All.Core.SkillMedians.ToList());
            CollectionAssert.AreEquivalent(compute.EffectiveSkillMedians.ToList(), s.All.Core.EffectiveSkillMedians.ToList());
            CollectionAssert.AreEquivalent(compute.AttributeMedians.ToList(), s.All.Core.AttributeMedians.ToList());
        }

        [TestMethod]
        public void Ratings_are_means_including_zeros_and_spell_output_is_the_median_over_projectile_carriers()
        {
            var (s, roster, _, _) = MeasureFixture();

            var caster = s.Get(CreatureArchetype.Caster);
            Assert.AreEqual(10.0, caster.DamageRating, 1e-9, "(0 + 0 + 0 + 10 + 40) / 5");
            Assert.AreEqual(0.0, caster.CritRating, 1e-9);

            // Outputs: 200, 220, 180, 240 x 1.1 = 264, 260 x 1.4 = 364 -> lower median 220.
            Assert.AreEqual(220.0, caster.SpellOutput, 1e-9);

            var melee = s.Get(roster.Classify(1).Archetype);
            Assert.AreEqual(6.0, melee.CritRating, 1e-9);
            Assert.AreEqual(0.0, melee.SpellOutput, 1e-9, "no member carries a projectile: no data");
        }

        [TestMethod]
        public void A_Hybrid_falls_back_to_its_primary_then_everyone_and_the_source_must_hold_at_both_ends()
        {
            CollectionAssert.AreEqual(new[] { CreatureArchetype.Hybrid, CreatureArchetype.Caster, CreatureArchetype.Unclassified },
                DungeonArchetypeStandards.FallbackChain(CreatureArchetype.Hybrid, CreatureArchetype.Caster).ToArray());
            CollectionAssert.AreEqual(new[] { CreatureArchetype.MeleeTank, CreatureArchetype.Unclassified },
                DungeonArchetypeStandards.FallbackChain(CreatureArchetype.MeleeTank, CreatureArchetype.MeleeTank).ToArray());
            CollectionAssert.AreEqual(new[] { CreatureArchetype.Unclassified },
                DungeonArchetypeStandards.FallbackChain(CreatureArchetype.Unclassified, CreatureArchetype.Unclassified).ToArray());

            var (run, _, _, _) = MeasureFixture(100);

            // Hybrid has no members at either end: its primary (Caster) is adequate at both, so Caster is the source.
            var chain = DungeonArchetypeStandards.FallbackChain(CreatureArchetype.Hybrid, CreatureArchetype.Caster);
            Assert.AreEqual(CreatureArchetype.Caster, DungeonArchetypeStandards.CommonSource(chain, run, run));

            // A source adequate at L but not at l is skipped for one that holds at both.
            Assert.AreEqual(CreatureArchetype.Unclassified, DungeonArchetypeStandards.CommonSource(chain, run, DungeonArchetypeStandardsEmptyButAll(run)));

            Assert.IsNull(DungeonArchetypeStandards.CommonSource(chain, run, DungeonArchetypeStandards.Empty), "not even All at the own level");
        }

        /// <summary>A standards set whose only adequate source is All: measured at a level where no caster lands in any rung.</summary>
        private static DungeonArchetypeStandards DungeonArchetypeStandardsEmptyButAll(DungeonArchetypeStandards unused)
        {
            var (s, _, _, _) = MeasureFixture(160);  // band [160, 184], ladder to x0.6 = 96: melee 100..104 and casters 100/105 only
            Assert.IsFalse(s.IsAdequate(CreatureArchetype.Caster), "fixture: two casters reach level 160's widest rung");
            Assert.IsTrue(s.IsAdequate(CreatureArchetype.Unclassified));
            return s;
        }

        [TestMethod]
        public void Extrapolate_carries_damage_and_output_on_the_damage_ratio_and_keeps_ratings_flat()
        {
            var (s, _, _, _) = MeasureFixture();
            var caster = s.Get(CreatureArchetype.Caster);

            Assert.AreSame(caster, DungeonArchetypeStandards.Extrapolate(caster, 326, 300, 0.02, 0.02, 1.0, FormulaOf), "at or below the pivot: as measured");

            var up = DungeonArchetypeStandards.Extrapolate(caster, 100, 200, 0.02, 0.02, 1.0, FormulaOf);
            var ratio = DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 100, 200, 0.02, 0.02, 1.0, 326);

            Assert.IsTrue(ratio > 1.0, "fixture: the curve rises");
            Assert.IsTrue(caster.SpellOutput > 0, "fixture: the casters carry projectiles");
            Assert.AreEqual(caster.EffectiveDamage * ratio, up.EffectiveDamage, 1e-9);
            Assert.AreEqual(caster.SpellOutput, up.SpellOutput, 1e-12, "spell output is FLAT at the pivot standard (natural stamped picks never get spell scaling)");
            Assert.AreEqual(caster.DamageRating, up.DamageRating, 1e-12, "ratings ride flat");
            Assert.AreEqual(caster.CritDamageRating, up.CritDamageRating, 1e-12);
            Assert.AreEqual(caster.Health, up.Health, 1e-12, "health is priced by the context, not here");
            Assert.AreEqual(caster.SampleCount, up.SampleCount);
        }

        [TestMethod]
        public void TransferRatio_scales_everyones_standard_by_the_archetypes_own_ratio_per_axis()
        {
            ArchetypeAxes Axes(uint damage, uint armor, uint sword, uint attr, int tier, double eff, double output, double dr, double health, int n)
                => new ArchetypeAxes(DungeonBandStandard.ForTest(new Dictionary<Skill, uint> { [Skill.Sword] = sword }, damage, armor, tier, 0.5, n,
                        new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = attr },
                        effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.Sword] = sword + 100, [Skill.MeleeDefense] = 500 }),
                    eff, output, dr, 0, 0, 0, health, n, 0.9);

            var allTarget = Axes(1000, 2000, 800, 400, 8, 1000, 300, 4, 20000, 20);
            var allRef = Axes(100, 400, 200, 100, 6, 100, 150, 2, 2000, 40);
            var archer = new ArchetypeAxes(DungeonBandStandard.ForTest(new Dictionary<Skill, uint> { [Skill.Bow] = 300 }, 50, 200, 0, 0.5, 7,
                    new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 50 },
                    effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 600 }),
                150, 0, 3, 0, 0, 0, 3000, 7, 0.8);

            var t = DungeonArchetypeStandards.TransferRatio(allTarget, archer, allRef);

            Assert.AreEqual(500u, t.Core.MaxBodyDamage, "1000 x 50/100");
            Assert.AreEqual(1000u, t.Core.MaxBaseArmor, "2000 x 200/400");
            Assert.AreEqual(200u, t.Core.AttributeMedianFor(PropertyAttribute.Strength), "400 x 50/100");
            Assert.AreEqual(600u, t.Core.EffectiveDefenseMedianFor(Skill.MeleeDefense), "500 x 600/500");
            Assert.AreEqual(0u, t.Core.MedianFor(Skill.Sword), "the archers carry no Sword: dropped, not borrowed");
            Assert.AreEqual(0u, t.Core.MedianFor(Skill.Bow), "everyone carries no Bow median at the target: nothing to scale");
            Assert.AreEqual(0, t.Core.SpellTier, "the archetype casts no tierable spell");
            Assert.AreEqual(1500.0, t.EffectiveDamage, 1e-9, "1000 x 150/100");
            Assert.AreEqual(0.0, t.SpellOutput, 1e-12, "no projectile carrier among the archers");
            Assert.AreEqual(6.0, t.DamageRating, 1e-9, "4 x 3/2");
            Assert.AreEqual(30000.0, t.Health, 1e-9, "20000 x 3000/2000");
            Assert.AreEqual(7, t.SampleCount, "the reference's sample size, so the transferred standard reads adequate");
            Assert.AreEqual(0.8, t.SampleLowRatio, 1e-12);

            var caster = Axes(50, 200, 100, 50, 7, 50, 300, 0, 1000, 6);
            Assert.AreEqual(8, DungeonArchetypeStandards.TransferRatio(allTarget, caster, allRef).Core.SpellTier, "a casting archetype takes the target's tier");
            Assert.AreEqual(600.0, DungeonArchetypeStandards.TransferRatio(allTarget, caster, allRef).SpellOutput, 1e-9, "300 x 300/150");
        }

        [TestMethod]
        public void MinLevelOf_is_the_lowest_authored_level_of_the_archetypes_role0_members()
        {
            var (_, roster, _, _) = MeasureFixture();

            Assert.AreEqual(40, roster.MinLevelOf(CreatureArchetype.Caster));
            Assert.AreEqual(100, roster.MinLevelOf(CreatureArchetype.Ranged));
            Assert.AreEqual(0, roster.MinLevelOf(CreatureArchetype.Hybrid), "no members");
        }

        // ---- the attack-skill ceiling (round 3, rule 3) --------------------------------------------------------

        [TestMethod]
        public void AttackSkillCeiling_is_the_nearest_rank_P75_of_pooled_values_carried_by_their_own_ratio()
        {
            var sample = new List<(Skill, uint)> { (Skill.UnarmedCombat, 100), (Skill.Sword, 200), (Skill.WarMagic, 300), (Skill.Bow, 400) };

            Assert.AreEqual(300u, DungeonArchetypeStandards.AttackSkillCeiling(sample, s => 1.0), "ceil(0.75 x 4) = rank 3 of 100, 200, 300, 400");
            Assert.AreEqual(400u, DungeonArchetypeStandards.AttackSkillCeiling(sample, s => s == Skill.WarMagic ? 2.0 : 1.0),
                "each value rides its own axis BEFORE pooling: 100, 200, 400, 600");
            Assert.AreEqual(300u, DungeonArchetypeStandards.AttackSkillCeiling(sample, s => double.NaN), "a garbled ratio reads 1");
            Assert.AreEqual(300u, DungeonArchetypeStandards.AttackSkillCeiling(sample, null));
            Assert.AreEqual(100u, DungeonArchetypeStandards.AttackSkillCeiling(sample, s => 1.0, 0.0));
            Assert.AreEqual(400u, DungeonArchetypeStandards.AttackSkillCeiling(sample, s => 1.0, 1.0));
            Assert.AreEqual(0u, DungeonArchetypeStandards.AttackSkillCeiling(new List<(Skill, uint)>(), s => 1.0), "no sample: no ceiling");
            Assert.AreEqual(0u, DungeonArchetypeStandards.AttackSkillCeiling(null, s => 1.0));
        }

        [TestMethod]
        public void The_attack_skill_sample_pools_every_schools_effective_value_and_the_context_hands_its_P75_to_every_info()
        {
            var (s, _, _, _) = MeasureFixture();

            // All at level 100: five melee (Sword 400), two casters (WarMagic 400), one archer (Bow 400); attribute term
            // (300 + 300) / 3 = 200, so every effective value is 600. Defense skills are not attack skills.
            Assert.AreEqual(8, s.All.AttackSkillSample.Count);
            Assert.IsTrue(s.All.AttackSkillSample.All(x => x.Effective == 600u));
            CollectionAssert.AreEquivalent(new[] { Skill.Sword, Skill.WarMagic, Skill.Bow }, s.All.AttackSkillSample.Select(x => x.Skill).Distinct().ToArray());
            Assert.AreEqual(5, s.Get(CreatureArchetype.Caster).AttackSkillSample.Count, "each archetype carries its own sample too");

            var (tables, profiles) = Fixture();
            Func<uint, DungeonStatProfile> profileOf = w => profiles.TryGetValue(w, out var p) ? p : null;
            var roster = new RosterArchetypes(tables, Natural, 0.6, profileOf, FormulaOf);
            var run = DungeonArchetypeStandards.Measure(tables, 100, profileOf, roster, Natural, 0.6, formulaOf: FormulaOf);
            Func<int, DungeonArchetypeStandards> measure = level => DungeonArchetypeStandards.Measure(tables, level, profileOf, roster, Natural, 0.6, formulaOf: FormulaOf);

            var flat = new ArchetypeUpliftContext(100, 100, false, 0.02, 0.02, 1.0, false, run, measure, roster, level => 0.0, 1.0, 1.0, 1.25, FormulaOf);
            Assert.AreEqual(600u, flat.AttackSkillCeiling);
            Assert.AreEqual(600u, flat.For(11).AttackSkillCeiling, "every info carries the run's ceiling");

            // Extrapolated from pivot 100 to 200: the six weapon values ride the weapon-attack ratio and the two war values
            // the magic-attack ratio; the P75 of the eight (rank 6) is the larger weapon value unless magic outruns it.
            var up = new ArchetypeUpliftContext(200, 100, true, 0.02, 0.02, 1.0, false, run, measure, roster, level => 0.0, 1.0, 1.0, 1.25, FormulaOf);
            var weapon = DungeonStatCurve.Scale(600, DungeonStatCurve.Ratio(DungeonStatAxis.WeaponAttack, 100, 200, 0.02, 0.02, 1.0, 326));
            var magic = DungeonStatCurve.Scale(600, DungeonStatCurve.Ratio(DungeonStatAxis.MagicAttack, 100, 200, 0.02, 0.02, 1.0, 326));
            var pooled = Enumerable.Repeat(weapon, 6).Concat(Enumerable.Repeat(magic, 2)).OrderBy(v => v).ToList();

            Assert.AreEqual(pooled[5], up.AttackSkillCeiling);
            Assert.AreNotEqual(600u, up.AttackSkillCeiling, "fixture: the curve moves the ceiling");
        }

        [TestMethod]
        public void HealthOnCurve_keeps_an_archetypes_relative_toughness_on_the_curve()
        {
            Assert.AreEqual(9000.0, ArchetypeUpliftContext.HealthOnCurve(6000, 3000, 2000), 1e-9, "1.5x the cross-archetype median");
            Assert.AreEqual(3000.0, ArchetypeUpliftContext.HealthOnCurve(0, 3000, 2000), 1e-9, "curve off: the measured median");
            Assert.AreEqual(3000.0, ArchetypeUpliftContext.HealthOnCurve(6000, 3000, 0), 1e-9);
        }

        /// <summary>Five melee creatures carrying all three defense skills (InitLevel; the formula adds (300 + 300) / 3 = 200).</summary>
        private static (DungeonArchetypeStandards Standards, RosterArchetypes Roster) DefenseFixture()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile>();

            for (uint w = 1; w <= 5; w++)
                profiles[w] = new DungeonStatProfile(100 + (int)w, 6000,
                    new Dictionary<Skill, uint> { [Skill.Sword] = 400, [Skill.MeleeDefense] = 600, [Skill.MissileDefense] = 540, [Skill.MagicDefense] = 330 },
                    50, 1000, 0, 0.0, Attrs, new ArchetypeFacts(null, false, false, null, effectiveMaxDamage: 50));

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["t"] = new SpeciesTableDef { Id = "t", Members = profiles.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList() },
            };

            DungeonStatProfile ProfileOf(uint w) => profiles.TryGetValue(w, out var p) ? p : null;
            var roster = new RosterArchetypes(tables, Natural, 0.6, ProfileOf, FormulaOf);

            return (DungeonArchetypeStandards.Measure(tables, 100, ProfileOf, roster, Natural, 0.6, formulaOf: FormulaOf), roster);
        }

        [TestMethod]
        public void The_context_routes_the_magic_rate_to_magic_defense_and_the_defense_rate_to_melee_and_missile()
        {
            // Owner ruling 2026-10-28. The two rates are adjacent doubles in the constructor; each is asserted on the axes it
            // owns, so a swap fails here.
            var (run, roster) = DefenseFixture();
            var measured = run.All.Core;

            Assert.AreEqual(530u, measured.EffectiveSkillMedianFor(Skill.MagicDefense), "positive control: the sample carries magic defense");
            Assert.AreEqual(800u, measured.EffectiveSkillMedianFor(Skill.MeleeDefense));
            Assert.AreEqual(740u, measured.EffectiveSkillMedianFor(Skill.MissileDefense));

            // Extrapolated: run 410 carried up from reference 326.
            var up = new ArchetypeUpliftContext(410, 326, true, 0.5, 0.0, 0.75, true, run, l => run, roster, l => 0.0, 1.0, 1.0, 1.25, FormulaOf);
            var upCore = up.RunAxes(CreatureArchetype.Unclassified).Core;

            Assert.AreEqual(520u, upCore.EffectiveSkillMedianFor(Skill.MagicDefense), "magic frozen at its level-300 value");
            Assert.AreEqual(DungeonStatCurve.Scale(800, DungeonStatCurve.Ratio(DungeonStatAxis.MeleeDefense, 326, 410, 0.5, 0.0, 1.0, 326)),
                upCore.EffectiveSkillMedianFor(Skill.MeleeDefense), "melee at the 0.5-softened rate");
            Assert.AreEqual(DungeonStatCurve.Scale(740, DungeonStatCurve.Ratio(DungeonStatAxis.MissileDefense, 326, 410, 0.5, 0.0, 1.0, 326)),
                upCore.EffectiveSkillMedianFor(Skill.MissileDefense), "missile at the 0.5-softened rate");
            Assert.IsTrue(upCore.EffectiveSkillMedianFor(Skill.MeleeDefense) > 800u, "discriminating: melee grows (a swapped rate 0 would freeze it)");
            Assert.AreEqual(run.All.EffectiveDamage * Math.Exp(0.75 * DungeonStatCurve.DamageSlope * 84), up.RunAxes(CreatureArchetype.Unclassified).EffectiveDamage, 1e-9,
                "damage at the damage rate 0.75 (owner ruling 2026-10-29)");
            Assert.AreEqual(DungeonStatCurve.Scale(run.All.Core.MaxBodyDamage, Math.Exp(0.75 * DungeonStatCurve.DamageSlope * 84)), upCore.MaxBodyDamage);

            // Not extrapolated, retail magic on: run 410 measured directly, only magic defense replaced.
            var flat = new ArchetypeUpliftContext(410, 410, false, 0.5, 0.0, 1.0, true, run, l => run, roster, l => 0.0, 1.0, 1.0, 1.25, FormulaOf);
            var flatCore = flat.RunAxes(CreatureArchetype.Unclassified).Core;

            Assert.AreEqual(520u, flatCore.EffectiveSkillMedianFor(Skill.MagicDefense), "retail magic at 410, frozen (a swapped 0.5 gives 569)");
            Assert.AreEqual(800u, flatCore.EffectiveSkillMedianFor(Skill.MeleeDefense), "melee is the measured standard");
            Assert.AreEqual(740u, flatCore.EffectiveSkillMedianFor(Skill.MissileDefense));
        }

        [TestMethod]
        public void The_context_resolves_a_source_common_to_both_ends_and_otherwise_returns_null()
        {
            var (tables, profiles) = Fixture();
            Func<uint, DungeonStatProfile> profileOf = w => profiles.TryGetValue(w, out var p) ? p : null;
            var roster = new RosterArchetypes(tables, Natural, 0.6, profileOf, FormulaOf);
            var run = DungeonArchetypeStandards.Measure(tables, 100, profileOf, roster, Natural, 0.6, formulaOf: FormulaOf);

            var context = new ArchetypeUpliftContext(100, 100, false, 0.02, 0.02, 1.0, false, run,
                level => DungeonArchetypeStandards.Measure(tables, level, profileOf, roster, Natural, 0.6, formulaOf: FormulaOf),
                roster, level => 0.0, 1.0, 1.0, 0.5, FormulaOf);

            var caster = context.For(11);
            Assert.IsNotNull(caster);
            Assert.AreEqual(CreatureArchetype.Caster, caster.Archetype);
            Assert.AreEqual(CreatureArchetype.Caster, caster.Source, "five casters at level 100 once widened to x0.9");
            Assert.AreEqual(100, caster.OwnLevel);
            Assert.AreEqual(10u, caster.OwnEffectiveDamage);
            Assert.AreSame(caster, context.For(11), "memoised");

            Assert.AreEqual(0, caster.RunSourceLevel, "measured directly at both ends");
            Assert.AreEqual(0, caster.OwnSourceLevel);

            // At level 90 only four casters reach any rung (the level-105 one is above [54, 103.5]): thin at the own end.
            // The run end stays the directly measured caster standard; the own end is RATIO-TRANSFERRED from the highest
            // band below where casters are adequate - level 65, whose ladder reaches the level-40 casters.
            var thin = context.For(13);
            Assert.IsNotNull(thin);
            Assert.AreEqual(CreatureArchetype.Caster, thin.Archetype);
            Assert.AreEqual(CreatureArchetype.Caster, thin.Source, "ratio transfer keeps the caster's own shape");
            Assert.AreEqual(90, thin.OwnLevel);
            Assert.AreEqual(0, thin.RunSourceLevel);
            Assert.AreEqual(65, thin.OwnSourceLevel);
            Assert.AreEqual(run.Get(CreatureArchetype.Caster).SampleCount, thin.Run.SampleCount);
            Assert.IsNull(thin.RunTransferArchetype, "the run end was measured directly: no transfer inputs");
            Assert.IsNotNull(thin.OwnTransferArchetype, "the transferred end records its reference pair for tools");
            Assert.AreEqual(5.0, thin.OwnTransferArchetype.EffectiveDamage, 1e-9, "the level-40 casters at the l65 reference");

            // S_A(l) = S_A(65) / S_All(65) x S_All(90) on effective damage: casters 5 vs everyone's lower median at 65.
            var at65 = DungeonArchetypeStandards.Measure(tables, 65, profileOf, roster, Natural, 0.6, formulaOf: FormulaOf);
            var at90 = DungeonArchetypeStandards.Measure(tables, 90, profileOf, roster, Natural, 0.6, formulaOf: FormulaOf);
            Assert.AreEqual(at90.All.EffectiveDamage * at65.Get(CreatureArchetype.Caster).EffectiveDamage / at65.All.EffectiveDamage,
                thin.Own.EffectiveDamage, 1e-9);
            StringAssert.Contains(thin.SourceDescription, "via ratio at l65");

            var archer = context.For(21);
            Assert.IsNotNull(archer);
            Assert.AreEqual(CreatureArchetype.Ranged, archer.Archetype);
            Assert.AreEqual(CreatureArchetype.Unclassified, archer.Source, "one archer anywhere, so no band to transfer a ratio from: everyone");

            var none = new ArchetypeUpliftContext(100, 100, false, 0.02, 0.02, 1.0, false, run, level => DungeonArchetypeStandards.Empty, roster, null, 1, 1, 0.5);
            Assert.IsNull(none.For(13), "no own-level data at all: the creature takes the legacy path");
            Assert.IsNull(new ArchetypeUpliftContext(100, 100, false, 0.02, 0.02, 1.0, false, run, null, roster, null, 1, 1, 0.5).For(13));
        }
    }
}
