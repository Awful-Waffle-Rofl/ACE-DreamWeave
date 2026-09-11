using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers the band-standard half of the adaptive-band feature: DungeonStatProfile, DungeonBandStandard's
    /// cross-family medians and sample widening, and ThreadDungeonSpawner's pure uplift arithmetic
    /// (UpliftRatio, ScaleBodyValue, ApplyBodyPartUplift, SanitizeBandFloorDial, ProfileOfWeenie).
    ///
    /// Nothing here touches a database, a landblock or PropertyManager.
    /// </summary>
    [TestClass]
    public class DungeonBandStandardTests
    {
        // ---- fixtures ------------------------------------------------------------------------------------

        private static DungeonStatProfile Profile(int level, uint health = 0, uint damage = 0, uint armor = 0, int spellTier = 0,
            params (Skill, uint)[] skills)
            => new DungeonStatProfile(level, health, skills.ToDictionary(s => s.Item1, s => s.Item2), damage, armor, spellTier);

        private static Dictionary<string, SpeciesTableDef> TablesOf(params (string Id, uint[] Wcids)[] families)
            => families.ToDictionary(f => f.Id, f => new SpeciesTableDef
            {
                Id = f.Id,
                Members = f.Wcids.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList()
            });

        // ---- DungeonStatProfile ---------------------------------------------------------------------------

        [TestMethod]
        public void An_empty_profile_reports_no_data_on_every_axis()
        {
            Assert.AreEqual(0, DungeonStatProfile.Empty.Level);
            Assert.AreEqual(0u, DungeonStatProfile.Empty.Health);
            Assert.AreEqual(0u, DungeonStatProfile.Empty.MaxBodyDamage);
            Assert.AreEqual(0u, DungeonStatProfile.Empty.MaxBaseArmor);
            Assert.AreEqual(0, DungeonStatProfile.Empty.Skills.Count);
            Assert.IsFalse(DungeonStatProfile.Empty.HasData);
        }

        [TestMethod]
        public void HasData_is_true_for_any_single_nonzero_axis_and_false_for_a_zero_valued_skill_row()
        {
            Assert.IsTrue(Profile(200, damage: 50).HasData, "body damage alone is data");
            Assert.IsTrue(Profile(200, armor: 50).HasData, "body armour alone is data");
            Assert.IsTrue(Profile(200, skills: (Skill.MeleeDefense, 300u)).HasData, "a skill alone is data");

            // A skill ROW that carries InitLevel 0 is "no authored data" by the same convention health uses,
            // so a profile whose only content is such a row must not count toward MinSample.
            Assert.IsFalse(Profile(200, skills: (Skill.MeleeDefense, 0u)).HasData);
        }

        // ---- ProfileOfWeenie ------------------------------------------------------------------------------

        private static Weenie CreatureWeenie(int level, uint maxHealthInit, uint maxHealthCp, uint endurance,
            (Skill, uint)[] skills, (CombatBodyPart, int Damage, int Armor)[] parts)
        {
            var w = new Weenie
            {
                WeenieClassId = 12345,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = maxHealthInit, LevelFromCP = maxHealthCp }
                },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = endurance }
                },
            };

            if (skills != null)
                w.PropertiesSkill = skills.ToDictionary(s => s.Item1, s => new PropertiesSkill { InitLevel = s.Item2 });

            if (parts != null)
                w.PropertiesBodyPart = parts.ToDictionary(p => p.Item1, p => new PropertiesBodyPart { DVal = p.Damage, BaseArmor = p.Armor });

            return w;
        }

        [TestMethod]
        public void ProfileOfWeenie_reads_every_axis_and_takes_the_maximum_body_part()
        {
            var weenie = CreatureWeenie(220, 4000, 1000, 300,
                new[] { (Skill.MeleeDefense, 400u), (Skill.WarMagic, 350u) },
                new[] { (CombatBodyPart.Head, 90, 200), (CombatBodyPart.Chest, 140, 180), (CombatBodyPart.Tail, 60, 260) });

            var profile = ThreadDungeonSpawner.ProfileOfWeenie(weenie);

            Assert.AreEqual(220, profile.Level);
            // The one health formula: InitLevel + LevelFromCP + Endurance/2 = 4000 + 1000 + 150.
            Assert.AreEqual(5150u, profile.Health);
            Assert.AreEqual(5150u, ThreadDungeonSpawner.HealthOfWeenie(weenie), "one copy of the formula, shared");
            Assert.AreEqual(140u, profile.MaxBodyDamage, "the largest DVal, not a mean");
            Assert.AreEqual(260u, profile.MaxBaseArmor, "the largest BaseArmor, from a different part than the damage");
            Assert.AreEqual(400u, profile.Skills[Skill.MeleeDefense]);
            Assert.AreEqual(350u, profile.Skills[Skill.WarMagic]);
        }

        [TestMethod]
        public void ProfileOfWeenie_tolerates_a_null_weenie_and_null_collections()
        {
            Assert.IsFalse(ThreadDungeonSpawner.ProfileOfWeenie(null).HasData);

            // Weenie's own class doc comment: only POPULATED collections are initialized, so every one of
            // these can arrive null on a real cached weenie.
            var bare = new Weenie { WeenieClassId = 1 };
            var profile = ThreadDungeonSpawner.ProfileOfWeenie(bare);

            Assert.AreEqual(0, profile.Level);
            Assert.AreEqual(0u, profile.Health);
            Assert.AreEqual(0u, profile.MaxBodyDamage);
            Assert.AreEqual(0u, profile.MaxBaseArmor);
            Assert.AreEqual(0, profile.Skills.Count);
        }

        // ---- DungeonBandStandard --------------------------------------------------------------------------

        [TestMethod]
        public void The_standard_is_cross_family_distinct_by_wcid_and_takes_the_lower_middle()
        {
            // Six members at level 205, all inside a level-200 gem's natural band [200, 230], spread over
            // three families so a family-scoped statistic would give a different answer on every one of them.
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [800] = Profile(205, damage: 10, armor: 100, skills: (Skill.MeleeDefense, 100u)),
                [801] = Profile(205, damage: 20, armor: 200, skills: (Skill.MeleeDefense, 200u)),
                [802] = Profile(205, damage: 30, armor: 300, skills: (Skill.MeleeDefense, 300u)),
                [803] = Profile(205, damage: 40, armor: 400, skills: (Skill.MeleeDefense, 400u)),
                [804] = Profile(205, damage: 50, armor: 500, skills: (Skill.MeleeDefense, 500u)),
                [805] = Profile(205, damage: 60, armor: 600, skills: (Skill.MeleeDefense, 600u)),
            };

            var tables = TablesOf(("a", new uint[] { 800, 801 }), ("b", new uint[] { 802, 803 }), ("c", new uint[] { 804, 805 }));

            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);

            Assert.AreEqual(6, standard.SampleCount);
            // Even count: the LOWER of the two middles, never their average (which would be 35 / 350 / 350).
            Assert.AreEqual(30u, standard.MaxBodyDamage);
            Assert.AreEqual(300u, standard.MaxBaseArmor);
            Assert.AreEqual(300u, standard.MedianFor(Skill.MeleeDefense));
            Assert.AreEqual(1.0, standard.SampleLowRatio, 1e-9, "the natural sample was big enough; no widening");
        }

        [TestMethod]
        public void A_zero_on_an_axis_is_excluded_from_that_axis_rather_than_counted_as_a_zero()
        {
            // Three members carry real damage; two report 0, which means "no authored data" and must not drag
            // the median down. Counting the zeroes would sort to {0, 0, 10, 20, 30} and give 10; excluding
            // them gives {10, 20, 30} and 20.
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [810] = Profile(205, damage: 10, armor: 100),
                [811] = Profile(205, damage: 0, armor: 200),
                [812] = Profile(205, damage: 20, armor: 300),
                [813] = Profile(205, damage: 0, armor: 400),
                [814] = Profile(205, damage: 30, armor: 500),
            };

            var tables = TablesOf(("a", new uint[] { 810, 811, 812, 813, 814 }));
            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);

            Assert.AreEqual(5, standard.SampleCount, "all five have SOME data, so all five are in the sample");
            Assert.AreEqual(20u, standard.MaxBodyDamage, "the two zero-damage members are excluded from the damage axis only");
            Assert.AreEqual(300u, standard.MaxBaseArmor, "all five carry armour, so the armour axis keeps all five");
        }

        [TestMethod]
        public void A_skill_median_is_taken_over_only_the_members_that_carry_that_skill()
        {
            // Two casters among five members. Averaging a zero in for the three non-casters would put the War
            // Magic standard at 0 and leave an uplifted caster's skill untouched.
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [820] = Profile(205, damage: 10, skills: (Skill.MeleeDefense, 100u)),
                [821] = Profile(205, damage: 10, skills: (Skill.MeleeDefense, 200u)),
                [822] = Profile(205, damage: 10, skills: (Skill.MeleeDefense, 300u)),
                [823] = Profile(205, damage: 10, skills: (Skill.WarMagic, 400u)),
                [824] = Profile(205, damage: 10, skills: (Skill.WarMagic, 600u)),
            };

            var tables = TablesOf(("a", new uint[] { 820, 821, 822, 823, 824 }));
            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);

            Assert.AreEqual(200u, standard.MedianFor(Skill.MeleeDefense), "three carriers, odd count, true median");
            Assert.AreEqual(400u, standard.MedianFor(Skill.WarMagic), "two carriers, lower middle");
            Assert.AreEqual(0u, standard.MedianFor(Skill.Lockpick), "a skill nobody in the band carries has no standard");
        }

        [TestMethod]
        public void SpellTier_is_the_lower_median_of_positive_MaxSpellTier_values_and_zero_when_none_carry_one()
        {
            // Three casters carry a tierable spell (tiers 2, 4, 6 -> lower median 4); two more carry none
            // (spellTier 0) and must be EXCLUDED from the axis rather than dragging the median toward 0 - the
            // same "0 means no data" convention MaxBodyDamage and MaxBaseArmor already use.
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [880] = Profile(205, damage: 10, spellTier: 2),
                [881] = Profile(205, damage: 10, spellTier: 4),
                [882] = Profile(205, damage: 10, spellTier: 6),
                [883] = Profile(205, damage: 10, spellTier: 0),
                [884] = Profile(205, damage: 10, spellTier: 0),
            };

            var tables = TablesOf(("a", new uint[] { 880, 881, 882, 883, 884 }));
            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);

            Assert.AreEqual(4, standard.SpellTier, "lower median of {2, 4, 6}, the two spell-tier-0 members excluded");
            // The other axes must be untouched by the new axis existing at all.
            Assert.AreEqual(10u, standard.MaxBodyDamage);
        }

        [TestMethod]
        public void SpellTier_is_zero_when_no_sample_member_carries_a_tierable_spell()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [885] = Profile(205, damage: 10, spellTier: 0),
                [886] = Profile(205, damage: 20, spellTier: 0),
            };

            var tables = TablesOf(("a", new uint[] { 885, 886 }));
            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60, minSample: 2);

            Assert.AreEqual(0, standard.SpellTier);
        }

        [TestMethod]
        public void A_thin_natural_sample_widens_the_sample_bands_low_edge_and_never_its_high_edge()
        {
            // Level-200 gem, natural band [200, 230]. Only two members sit there; six more sit at 170, which
            // a low edge of 0.85 reaches ([170, 230]) - the first ratio at which the sample reaches MinSample.
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [830] = Profile(205, damage: 1000),
                [831] = Profile(210, damage: 1000),
                [832] = Profile(170, damage: 10),
                [833] = Profile(170, damage: 20),
                [834] = Profile(170, damage: 30),
                [835] = Profile(170, damage: 40),
                // Above the high edge: must NEVER be reached, at any widening, because only the low edge moves.
                [836] = Profile(400, damage: 99999),
            };

            var tables = TablesOf(("a", profiles.Keys.ToArray()));
            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);

            Assert.AreEqual(6, standard.SampleCount, "widened until MinSample (5) was met, then stopped");
            Assert.AreEqual(0.85, standard.SampleLowRatio, 1e-9, "the FIRST ratio that met the threshold, not the floor");
            Assert.IsTrue(standard.MaxBodyDamage < 99999u, "the level-400 member is above the high edge and can never enter the sample");
        }

        [TestMethod]
        public void A_sample_that_never_reaches_the_threshold_stops_at_the_floor_rather_than_returning_nothing()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [840] = Profile(205, damage: 100),
                [841] = Profile(130, damage: 200), // reachable only at the 0.60 floor: [120, 230]
            };

            var tables = TablesOf(("a", new uint[] { 840, 841 }));
            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);

            Assert.AreEqual(2, standard.SampleCount);
            Assert.AreEqual(0.60, standard.SampleLowRatio, 1e-9);
            Assert.AreEqual(100u, standard.MaxBodyDamage, "lower middle of {100, 200}");
        }

        [TestMethod]
        public void An_empty_or_dataless_band_yields_the_empty_standard()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile> { [850] = Profile(205), [851] = Profile(205) };
            var tables = TablesOf(("a", new uint[] { 850, 851 }));

            // Members are in band but carry no usable data on any axis.
            var dataless = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60);
            Assert.IsTrue(dataless.IsEmpty);
            Assert.AreEqual(0u, dataless.MaxBodyDamage);
            Assert.AreEqual(0u, dataless.MedianFor(Skill.MeleeDefense));
            Assert.AreEqual(0, dataless.SpellTier);
            Assert.AreEqual(0, DungeonBandStandard.Empty.SpellTier);

            // No member anywhere near the band at any width.
            var farProfiles = new Dictionary<uint, DungeonStatProfile> { [860] = Profile(20, damage: 5) };
            var farTables = TablesOf(("a", new uint[] { 860 }));
            Assert.IsTrue(DungeonBandStandard.Compute(farTables, 200, w => farProfiles[w], DungeonRosterBand.Default, 0.60).IsEmpty);

            // A null delegate is the "no profile source" case the builder's optional parameter allows.
            Assert.IsTrue(DungeonBandStandard.Compute(tables, 200, null, DungeonRosterBand.Default, 0.60).IsEmpty);
        }

        [TestMethod]
        public void Role_1_and_above_members_are_never_in_the_sample()
        {
            // Same construction as BandMedianHealth: role 0 ONLY. An elite or a named boss is not what a
            // trash slot is normalized against.
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [870] = Profile(205, damage: 10),
                [871] = Profile(205, damage: 20),
                [872] = Profile(205, damage: 99999),
            };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["a"] = new SpeciesTableDef
                {
                    Id = "a",
                    Members = new List<SpeciesMemberDef>
                    {
                        new SpeciesMemberDef { Wcid = 870, Role = 0 },
                        new SpeciesMemberDef { Wcid = 871, Role = 0 },
                        new SpeciesMemberDef { Wcid = 872, Role = 1 },
                    }
                }
            };

            var standard = DungeonBandStandard.Compute(tables, 200, w => profiles[w], DungeonRosterBand.Default, 0.60, minSample: 2);

            Assert.AreEqual(2, standard.SampleCount);
            Assert.AreEqual(10u, standard.MaxBodyDamage, "lower middle of {10, 20}; the role-1 member is not in the sample");
        }

        // ---- uplift arithmetic (invariant I-C) -------------------------------------------------------------

        /// <summary>
        /// I-C: no uplift axis ever lowers a value. Every case here is one where a naive implementation would
        /// have - a standard below the creature's own value, a zero standard, a zero own value, a garbled
        /// ratio - and every one of them must come back a no-op rather than a reduction.
        /// </summary>
        [TestMethod]
        public void IC_no_uplift_axis_ever_lowers_a_value()
        {
            // UpliftRatio never returns below 1.
            Assert.AreEqual(1.0, ThreadDungeonSpawner.UpliftRatio(400, 100), 1e-9, "a standard BELOW the creature's own is a no-op");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.UpliftRatio(400, 400), 1e-9, "equal is a no-op");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.UpliftRatio(400, 0), 1e-9, "no standard is a no-op");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.UpliftRatio(0, 400), 1e-9, "a ratio cannot lift a zero");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.UpliftRatio(-5, 400), 1e-9, "nonsense authored data is a no-op, never a sign flip");
            Assert.AreEqual(2.0, ThreadDungeonSpawner.UpliftRatio(100, 200), 1e-9);

            // ScaleBodyValue clamps its LOWER bound at the original value, so it is monotone for any ratio.
            Assert.AreEqual(100, ThreadDungeonSpawner.ScaleBodyValue(100, 0.5), "a sub-1 ratio cannot reach this function, and is inert if it does");
            Assert.AreEqual(100, ThreadDungeonSpawner.ScaleBodyValue(100, 1.0));
            Assert.AreEqual(100, ThreadDungeonSpawner.ScaleBodyValue(100, double.NaN));
            Assert.AreEqual(0, ThreadDungeonSpawner.ScaleBodyValue(0, 5.0), "a zero stays zero rather than being invented");
            Assert.AreEqual(250, ThreadDungeonSpawner.ScaleBodyValue(100, 2.5));

            foreach (var value in new[] { 1, 7, 99, 1000, 123456 })
            {
                foreach (var ratio in new[] { 0.0, 0.5, 1.0, 1.0000001, 1.5, 3.7, 100.0 })
                    Assert.IsTrue(ThreadDungeonSpawner.ScaleBodyValue(value, ratio) >= value, $"value={value} ratio={ratio}");
            }
        }

        // ---- ApplyBodyPartUplift -------------------------------------------------------------------------

        private static Biota BiotaWithParts(params (CombatBodyPart Part, int Damage, int Armor)[] parts)
        {
            var biota = new Biota
            {
                Id = 0x70000001,
                WeenieClassId = 12345,
                PropertiesBodyPart = parts.ToDictionary(p => p.Part, p => new PropertiesBodyPart { DVal = p.Damage, BaseArmor = p.Armor })
            };

            return biota;
        }

        [TestMethod]
        public void Body_parts_are_scaled_by_a_ratio_against_the_creatures_own_maximum()
        {
            var biota = BiotaWithParts((CombatBodyPart.Head, 50, 100), (CombatBodyPart.Chest, 100, 400), (CombatBodyPart.Tail, 25, 50));

            // Own maxima: damage 100, armour 400. Standard: damage 300 (3x), armour 800 (2x).
            var scaled = ThreadDungeonSpawner.ApplyBodyPartUplift(biota, 300, 800);

            Assert.AreEqual(3, scaled);
            Assert.AreEqual(150, biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
            Assert.AreEqual(300, biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal);
            Assert.AreEqual(75, biota.PropertiesBodyPart[CombatBodyPart.Tail].DVal);
            Assert.AreEqual(200, biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
            Assert.AreEqual(800, biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);
            Assert.AreEqual(100, biota.PropertiesBodyPart[CombatBodyPart.Tail].BaseArmor);
        }

        [TestMethod]
        public void A_creature_already_at_or_above_the_standard_is_left_completely_untouched()
        {
            var biota = BiotaWithParts((CombatBodyPart.Head, 500, 900));
            var before = biota.PropertiesBodyPart;

            Assert.AreEqual(0, ThreadDungeonSpawner.ApplyBodyPartUplift(biota, 300, 800));
            Assert.AreEqual(500, biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
            Assert.AreEqual(900, biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
            Assert.AreSame(before, biota.PropertiesBodyPart, "a no-op uplift must not even clone");
        }

        /// <summary>
        /// The production trap this whole clone exists for. PropertiesBodyPart is reference-shared with the
        /// CACHED WEENIE (ACE.Entity/Adapter/WeenieConverter.cs, the
        /// referenceWeenieCollectionsForCommonProperties branch), so an in-place edit would raise the damage
        /// and armour of every instance of that creature server-wide, retail landblocks included, until the
        /// world cache was invalidated. Both the dictionary AND each value object have to be copied - a fresh
        /// dictionary holding the same value objects still writes straight through.
        /// </summary>
        [TestMethod]
        public void The_uplift_never_writes_through_into_the_shared_cached_weenie_collection()
        {
            var shared = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = new PropertiesBodyPart { DVal = 50, BaseArmor = 100 },
                [CombatBodyPart.Chest] = new PropertiesBodyPart { DVal = 100, BaseArmor = 400 },
            };

            var headPart = shared[CombatBodyPart.Head];
            var chestPart = shared[CombatBodyPart.Chest];

            // Exactly what ConvertToBiota does in the reference-sharing branch: a bare assignment.
            var biota = new Biota { Id = 0x70000002, WeenieClassId = 12345, PropertiesBodyPart = shared };

            ThreadDungeonSpawner.ApplyBodyPartUplift(biota, 300, 800);

            Assert.AreNotSame(shared, biota.PropertiesBodyPart, "the dictionary itself must be replaced");
            Assert.AreNotSame(headPart, biota.PropertiesBodyPart[CombatBodyPart.Head], "each value must be cloned too");

            Assert.AreEqual(50, headPart.DVal, "the cached weenie's own part is untouched");
            Assert.AreEqual(100, headPart.BaseArmor);
            Assert.AreEqual(100, chestPart.DVal);
            Assert.AreEqual(400, chestPart.BaseArmor);

            Assert.AreEqual(150, biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, "the instance did get the uplift");
        }

        [TestMethod]
        public void A_biota_with_no_body_parts_is_a_no_op()
        {
            Assert.AreEqual(0, ThreadDungeonSpawner.ApplyBodyPartUplift(null, 300, 800));
            Assert.AreEqual(0, ThreadDungeonSpawner.ApplyBodyPartUplift(new Biota { Id = 1, WeenieClassId = 1 }, 300, 800));
            Assert.AreEqual(0, ThreadDungeonSpawner.ApplyBodyPartUplift(BiotaWithParts(), 300, 800));
        }

        // ---- SanitizeBandFloorDial -------------------------------------------------------------------------

        [TestMethod]
        public void SanitizeBandFloorDial_rejects_non_positive_and_non_finite_values_and_clamps_above_one()
        {
            const double fallback = DungeonPopulationLimits.DefaultBandLowFloorRatio;

            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandFloorDial(double.NaN, fallback), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandFloorDial(double.PositiveInfinity, fallback), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandFloorDial(double.NegativeInfinity, fallback), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandFloorDial(0, fallback), 1e-9,
                "0 is DEGENERATE here, not 'disabled' - it would admit every creature in the game");
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandFloorDial(-1, fallback), 1e-9);

            Assert.AreEqual(0.75, ThreadDungeonSpawner.SanitizeBandFloorDial(0.75, fallback), 1e-9, "an in-range value is kept");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.SanitizeBandFloorDial(1.0, fallback), 1e-9, "1.0 is the documented DISABLE value and must survive");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.SanitizeBandFloorDial(2.0, fallback), 1e-9,
                "above 1.0 reads as 1.0 (disabled), NOT as the shipped default - the high edge never moves");
        }
    }
}
