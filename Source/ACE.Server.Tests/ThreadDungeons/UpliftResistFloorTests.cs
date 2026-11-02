using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The uplift resist floor (dynamic_dungeons_uplift_resist_floor, 2026-10-27): the per-element rule (never softens: a
    /// ResistX never rises, an ArmorModVs never falls; lands on the median; an authored weakness above 1.0 is untouched;
    /// rho_def 0 and a 0 median are identity), the guard re-run over the floored values, the softer-middle medians and
    /// their carriage through With / Extrapolate / TransferRatio, the weenie facts, and the creature glue. No database, no
    /// dat: TestGameTables supplies the formulas; no PropertyManager key is read by anything driven here.
    /// </summary>
    [TestClass]
    public class UpliftResistFloorTests
    {
        private static uint nextGuid = 0x7E3A0000;  // static guid range, clear of GuidManager and the other creature tests
        private static uint nextWcid = 997600;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static double[] Eight(double v) => Enumerable.Repeat(v, 8).ToArray();

        private static ArchetypeUpliftInfo Info(double[] resistMedians, double[] armorMedians, double defense = 1.0)
            => new ArchetypeUpliftInfo
            {
                Wcid = 1,
                Archetype = CreatureArchetype.MeleeTank,
                Source = CreatureArchetype.MeleeTank,
                Run = new ArchetypeAxes(DungeonBandStandard.Empty, 0, 0, 0, 0, 0, 0, 0, 8, 1.0, null, resistMedians, armorMedians),
                Own = ArchetypeAxes.Empty,
                DefenseRatio = defense,
            };

        private static Func<PropertyFloat, double?> Getter(Dictionary<PropertyFloat, double> values)
            => p => values.TryGetValue(p, out var v) ? v : (double?)null;

        // ---- the per-element rule ---------------------------------------------------------------------------

        [TestMethod]
        public void ResistTarget_lowers_to_the_median_and_never_raises()
        {
            Assert.AreEqual(0.6, ArchetypeUplift.ResistTarget(1.0, 0.6), 1e-12, "lands on the median");
            Assert.AreEqual(0.4, ArchetypeUplift.ResistTarget(0.4, 0.6), 1e-12, "already tougher than the median: kept, never raised");
            Assert.AreEqual(0.9, ArchetypeUplift.ResistTarget(0.9, 1.2), 1e-12, "a median above own never raises it");
        }

        [TestMethod]
        public void ResistTarget_leaves_an_authored_weakness_and_no_data_untouched()
        {
            Assert.AreEqual(1.5, ArchetypeUplift.ResistTarget(1.5, 0.6), 1e-12, "Q1: own above 1.0 (a weakness) is left as authored");
            Assert.AreEqual(0.6, ArchetypeUplift.ResistTarget(1.0, 0.6), 1e-12, "exactly 1.0 is not a weakness");
            Assert.AreEqual(1.0, ArchetypeUplift.ResistTarget(1.0, 0.0), 1e-12, "median 0 = no data");
            Assert.AreEqual(1.0, ArchetypeUplift.ResistTarget(1.0, double.NaN), 1e-12);
            Assert.AreEqual(1.0, ArchetypeUplift.ResistTarget(1.0, double.PositiveInfinity), 1e-12);
        }

        [TestMethod]
        public void ArmorModTarget_raises_to_the_median_only_with_body_armour_and_never_lowers()
        {
            Assert.AreEqual(1.3, ArchetypeUplift.ArmorModTarget(0.5, 1.3, 200), 1e-12, "lands on the median");
            Assert.AreEqual(1.8, ArchetypeUplift.ArmorModTarget(1.8, 1.3, 200), 1e-12, "never lowers");
            Assert.AreEqual(0.5, ArchetypeUplift.ArmorModTarget(0.5, 1.3, 0), 1e-12, "no body armour to multiply: untouched");
            Assert.AreEqual(0.5, ArchetypeUplift.ArmorModTarget(0.5, 0.0, 200), 1e-12, "median 0 = no data");
        }

        // ---- the writes -------------------------------------------------------------------------------------

        [TestMethod]
        public void ResistFloorWrites_never_softens_and_lands_every_element_on_its_median()
        {
            // An absent resist reads 1.0; slash is authored tough, fire is an authored weakness.
            var values = new Dictionary<PropertyFloat, double>
            {
                [PropertyFloat.ResistSlash] = 0.3,
                [PropertyFloat.ResistFire] = 1.4,
                [PropertyFloat.ArmorModVsPierce] = 2.0,
                [PropertyFloat.ArmorModVsCold] = 0.2,
            };

            var writes = ArchetypeUplift.ResistFloorWrites(Info(Eight(0.7), Eight(0.9)), Getter(values), 300, guard: false, 0.5, 2.0);
            var byProperty = writes.ToDictionary(w => w.Property);

            foreach (var w in writes)
            {
                if (Array.IndexOf(ArchetypeFacts.ResistProperties, w.Property) >= 0)
                    Assert.IsTrue(w.To <= w.From, $"{w.Property} rose");
                else
                    Assert.IsTrue(w.To >= w.From, $"{w.Property} fell");
            }

            Assert.IsFalse(byProperty.ContainsKey(PropertyFloat.ResistSlash), "0.3 is already tougher than 0.7");
            Assert.IsFalse(byProperty.ContainsKey(PropertyFloat.ResistFire), "an authored weakness is untouched");
            Assert.AreEqual(0.7, byProperty[PropertyFloat.ResistPierce].To, 1e-12, "absent 1.0 lands on the median");
            Assert.AreEqual(1.0, byProperty[PropertyFloat.ResistPierce].From, 1e-12);
            Assert.AreEqual(0.7, byProperty[PropertyFloat.ResistNether].To, 1e-12);

            Assert.IsFalse(byProperty.ContainsKey(PropertyFloat.ArmorModVsPierce), "2.0 is above the 0.9 median");
            Assert.AreEqual(0.9, byProperty[PropertyFloat.ArmorModVsCold].To, 1e-12);
            Assert.IsFalse(byProperty.ContainsKey(PropertyFloat.ArmorModVsSlash), "absent 1.0 is above the 0.9 median");

            Assert.AreEqual(6, writes.Count(w => Array.IndexOf(ArchetypeFacts.ResistProperties, w.Property) >= 0), "8 resists minus slash and fire");
        }

        [TestMethod]
        public void ResistFloorWrites_is_identity_for_rho_def_zero_no_info_and_no_data()
        {
            var values = new Dictionary<PropertyFloat, double>();

            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(Info(Eight(0.5), Eight(1.5), defense: 0.0), Getter(values), 300, true, 0.5, 2.0).Count, "rho_def 0");
            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(Info(Eight(0.5), Eight(1.5), defense: double.NaN), Getter(values), 300, true, 0.5, 2.0).Count);
            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(null, Getter(values), 300, true, 0.5, 2.0).Count, "no info: left authored");
            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(Info(null, null), Getter(values), 300, true, 0.5, 2.0).Count, "no medians");
            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(Info(Eight(0.0), Eight(0.0)), Getter(values), 300, true, 0.5, 2.0).Count, "every median 0");
            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(Info(null, Eight(1.5)), Getter(values), 0, true, 0.5, 2.0).Count, "no body armour: no armour-mod write");
        }

        [TestMethod]
        public void The_guard_re_run_restores_a_group_the_floor_made_whole_immune()
        {
            // Medians of 0.2 on every physical resist would leave the whole physical group immune; the category floor 0.5
            // (the trait strip's guard) raises the group back to 0.5. The magic group's median 0.6 is above the floor.
            var medians = new[] { 0.2, 0.2, 0.2, 0.6, 0.6, 0.6, 0.6, 0.6 };
            var values = new Dictionary<PropertyFloat, double>();

            var unguarded = ArchetypeUplift.ResistFloorWrites(Info(medians, null), Getter(values), 300, guard: false, 0.5, 2.0).ToDictionary(w => w.Property);
            Assert.AreEqual(0.2, unguarded[PropertyFloat.ResistSlash].To, 1e-12, "control: without the guard the floor makes slash 0.2");

            var guarded = ArchetypeUplift.ResistFloorWrites(Info(medians, null), Getter(values), 300, guard: true, 0.5, 2.0).ToDictionary(w => w.Property);

            foreach (var p in DungeonCombatNormalizer.PhysicalResists)
                Assert.AreEqual(0.5, guarded[p].To, 1e-12, $"{p} restored to the category floor");

            Assert.AreEqual(0.6, guarded[PropertyFloat.ResistFire].To, 1e-12, "the magic group is not whole-immune: the floor stands");
        }

        [TestMethod]
        public void The_guard_re_run_scales_a_physical_armour_group_the_floor_lifted_above_the_ceiling()
        {
            var values = new Dictionary<PropertyFloat, double>();
            var writes = ArchetypeUplift.ResistFloorWrites(Info(null, Eight(3.0)), Getter(values), 300, guard: true, 0.5, 2.0).ToDictionary(w => w.Property);

            foreach (var p in DungeonCombatNormalizer.PhysicalArmorMods)
                Assert.AreEqual(2.0, writes[p].To, 1e-12, $"{p} held at the category ceiling");

            Assert.AreEqual(3.0, writes[PropertyFloat.ArmorModVsFire].To, 1e-12, "the ceiling is physical only");
        }

        [TestMethod]
        public void The_guard_re_run_never_raises_an_authored_immunity_the_floor_did_not_write()
        {
            // Review case F1a: slash authored 0.2, pierce/bludgeon 0.6, physical medians 0.4. The floor lowers pierce and
            // bludgeon to 0.4 (whole group now below 0.5), the guard raises the group back to the category floor - but
            // slash was never floor-written and must keep its authored 0.2 (raising it would soften the creature).
            var medians = new[] { 0.4, 0.4, 0.4, 1.0, 1.0, 1.0, 1.0, 1.0 };
            var values = new Dictionary<PropertyFloat, double>
            {
                [PropertyFloat.ResistSlash] = 0.2, [PropertyFloat.ResistPierce] = 0.6, [PropertyFloat.ResistBludgeon] = 0.6,
            };

            var writes = ArchetypeUplift.ResistFloorWrites(Info(medians, null), Getter(values), 300, guard: true, 0.5, 2.0).ToDictionary(w => w.Property);

            Assert.IsFalse(writes.ContainsKey(PropertyFloat.ResistSlash), "slash stays at its authored 0.2");
            Assert.AreEqual(0.5, writes[PropertyFloat.ResistPierce].To, 1e-12, "pierce: the guard takes back part of the floor");
            Assert.AreEqual(0.5, writes[PropertyFloat.ResistBludgeon].To, 1e-12);

            foreach (var (_, from, to) in writes.Values)
                Assert.IsTrue(to <= from + 1e-12, "no resist rises");

            var best = DungeonCombatNormalizer.PhysicalResists.Max(p => writes.TryGetValue(p, out var w) ? w.To : values[p]);
            Assert.IsTrue(best >= 0.5 - 1e-12, "the group is not left whole-immune");
        }

        [TestMethod]
        public void The_guard_re_run_never_scales_down_an_authored_armour_mod_the_floor_did_not_write()
        {
            // Review case F1b: slash 1.0, pierce 3.0, bludgeon 1.0, medians 2.5, ceiling 2.0. The floor may lift slash and
            // bludgeon only up to the ceiling, and the authored pierce 3.0 must not be scaled down to 2.4.
            var values = new Dictionary<PropertyFloat, double>
            {
                [PropertyFloat.ArmorModVsSlash] = 1.0, [PropertyFloat.ArmorModVsPierce] = 3.0, [PropertyFloat.ArmorModVsBludgeon] = 1.0,
            };

            var writes = ArchetypeUplift.ResistFloorWrites(Info(null, Eight(2.5)), Getter(values), 300, guard: true, 0.5, 2.0).ToDictionary(w => w.Property);

            Assert.IsFalse(writes.ContainsKey(PropertyFloat.ArmorModVsPierce), "pierce stays at its authored 3.0");
            Assert.AreEqual(2.0, writes[PropertyFloat.ArmorModVsSlash].To, 1e-12);
            Assert.AreEqual(2.0, writes[PropertyFloat.ArmorModVsBludgeon].To, 1e-12);
            Assert.AreEqual(2.5, writes[PropertyFloat.ArmorModVsFire].To, 1e-12, "the ceiling is physical only");

            foreach (var (_, from, to) in writes.Values)
                Assert.IsTrue(to >= from - 1e-12, "no armour mod falls");
        }

        [TestMethod]
        public void The_floor_never_lifts_the_smallest_physical_armour_mod_above_the_ceiling()
        {
            // slash 1.0 (median 3.0), pierce 2.6, bludgeon 3.0: a scale-then-clamp would leave slash at 2.31 and the group's
            // smallest above the ceiling; the per-property cap keeps the smallest at the ceiling.
            var values = new Dictionary<PropertyFloat, double>
            {
                [PropertyFloat.ArmorModVsSlash] = 1.0, [PropertyFloat.ArmorModVsPierce] = 2.6, [PropertyFloat.ArmorModVsBludgeon] = 3.0,
            };

            var writes = ArchetypeUplift.ResistFloorWrites(Info(null, Eight(3.0)), Getter(values), 300, guard: true, 0.5, 2.0).ToDictionary(w => w.Property);
            var smallest = DungeonCombatNormalizer.PhysicalArmorMods.Min(p => writes.TryGetValue(p, out var w) ? w.To : values[p]);

            Assert.AreEqual(2.0, smallest, 1e-12);
            Assert.IsFalse(writes.ContainsKey(PropertyFloat.ArmorModVsPierce));
            Assert.IsFalse(writes.ContainsKey(PropertyFloat.ArmorModVsBludgeon));
        }

        [TestMethod]
        public void The_guard_does_not_run_when_the_floor_wrote_nothing()
        {
            // An authored whole-immune physical group (the strip's guard would have fixed it at step 4b; here it is left) is
            // not touched by a floor that has nothing to floor.
            var values = new Dictionary<PropertyFloat, double>
            {
                [PropertyFloat.ResistSlash] = 0.1, [PropertyFloat.ResistPierce] = 0.1, [PropertyFloat.ResistBludgeon] = 0.1,
            };

            Assert.AreEqual(0, ArchetypeUplift.ResistFloorWrites(Info(Eight(1.0), null), Getter(values), 300, guard: true, 0.5, 2.0).Count);
        }

        // ---- the medians ------------------------------------------------------------------------------------

        private static DungeonStatProfile Profile(uint armor, double resist, double armorMod, bool facts = true)
            => new DungeonStatProfile(100, 1000, new Dictionary<Skill, uint> { [Skill.Sword] = 100 }, 10, armor, 0, 0.0, null,
                facts ? new ArchetypeFacts(null, false, false, null, resists: Eight(resist), armorMods: Eight(armorMod)) : null);

        [TestMethod]
        public void Resist_medians_take_the_upper_middle_and_armour_mod_medians_the_lower_middle_over_armoured_members()
        {
            var sample = new List<DungeonStatProfile>
            {
                Profile(100, 0.2, 0.5), Profile(100, 0.4, 0.7), Profile(100, 0.6, 0.9), Profile(100, 0.8, 1.1),
                Profile(0, 0.1, 5.0),             // no body armour: its armour mods are not sampled, its resist is
                Profile(100, 0.0, 0.0, facts: false),   // no facts: sampled by neither
            };

            var resist = DungeonArchetypeStandards.ResistMediansOf(sample);
            var armor = DungeonArchetypeStandards.ArmorModMediansOf(sample);

            // resists 0.1, 0.2, 0.4, 0.6, 0.8: upper middle of five = 0.4 (index 2)
            Assert.AreEqual(0.4, resist[0], 1e-12);

            // armour mods (armoured only) 0.5, 0.7, 0.9, 1.1: lower middle of four = 0.7 (index 1)
            Assert.AreEqual(0.7, armor[0], 1e-12);

            // Even count, resists only: 0.2, 0.4, 0.6, 0.8 -> upper middle 0.6, the softer one.
            Assert.AreEqual(0.6, DungeonArchetypeStandards.ResistMediansOf(sample.Take(4).ToList())[3], 1e-12);

            Assert.IsNull(DungeonArchetypeStandards.ResistMediansOf(new List<DungeonStatProfile> { Profile(100, 0, 0, facts: false) }));
            Assert.IsNull(DungeonArchetypeStandards.ArmorModMediansOf(new List<DungeonStatProfile> { Profile(0, 0.5, 0.5) }));
        }

        [TestMethod]
        public void AxesOf_carries_the_medians_and_With_Extrapolate_and_TransferRatio_preserve_them()
        {
            var sample = new List<DungeonStatProfile> { Profile(100, 0.5, 1.2), Profile(100, 0.5, 1.2), Profile(100, 0.5, 1.2) };
            var axes = DungeonArchetypeStandards.AxesOf(sample, 1.0, null);

            Assert.AreEqual(0.5, axes.ResistMedians[4], 1e-12);
            Assert.AreEqual(1.2, axes.ArmorModMedians[2], 1e-12);

            var with = axes.With(health: 12345, effectiveDamage: 77);
            CollectionAssert.AreEqual(axes.ResistMedians.ToList(), with.ResistMedians.ToList(), "With keeps them");
            CollectionAssert.AreEqual(axes.ArmorModMedians.ToList(), with.ArmorModMedians.ToList());

            var extrapolated = DungeonArchetypeStandards.Extrapolate(axes, 326, 400, 0.5, 0.5, 1.0);
            CollectionAssert.AreEqual(axes.ResistMedians.ToList(), extrapolated.ResistMedians.ToList(), "carried flat up the stat curve");
            CollectionAssert.AreEqual(axes.ArmorModMedians.ToList(), extrapolated.ArmorModMedians.ToList());

            // Ratio transfer copies the REFERENCE archetype's medians (dimensionless: never scaled by archetype / all).
            var all = new ArchetypeAxes(axes.Core, 10, 0, 0, 0, 0, 0, 100, 8, 1.0, null, Eight(0.9), Eight(0.8));
            var allRef = new ArchetypeAxes(axes.Core, 10, 0, 0, 0, 0, 0, 100, 8, 1.0, null, Eight(0.95), Eight(0.85));
            var transferred = DungeonArchetypeStandards.TransferRatio(all, axes, allRef);

            CollectionAssert.AreEqual(axes.ResistMedians.ToList(), transferred.ResistMedians.ToList());
            CollectionAssert.AreEqual(axes.ArmorModMedians.ToList(), transferred.ArmorModMedians.ToList());
        }

        // ---- weenie facts and the creature glue ------------------------------------------------------------

        private static Weenie MakeWeenie(int baseArmor)
            => new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 200 },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    [PropertyFloat.ResistSlash] = 0.4,
                    [PropertyFloat.ResistFire] = 1.5,
                    [PropertyFloat.ArmorModVsCold] = 0.3,
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 500 },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Chest] = new PropertiesBodyPart { DType = DamageType.Bludgeon, DVal = 10, DVar = 0.5f, BaseArmor = baseArmor, MLF = 1, MRF = 1, MLB = 1, MRB = 1 },
                },
            };

        [TestMethod]
        public void FromWeenie_reads_all_eight_resists_and_armour_mods_with_absent_as_one()
        {
            var facts = ArchetypeFacts.FromWeenie(MakeWeenie(100), new ArchetypeFactsResolver(id => null, id => null, null, null, null));

            Assert.AreEqual(8, facts.Resists.Count);
            Assert.AreEqual(0.4, facts.Resists[0], 1e-12, "slash");
            Assert.AreEqual(1.0, facts.Resists[1], 1e-12, "pierce absent");
            Assert.AreEqual(1.5, facts.Resists[3], 1e-12, "fire");
            Assert.AreEqual(0.3, facts.ArmorMods[4], 1e-12, "cold");
            Assert.AreEqual(1.0, facts.ArmorMods[0], 1e-12, "slash absent");
            Assert.IsNull(ArchetypeFacts.Empty.Resists, "unread facts carry no resist data");
        }

        [TestMethod]
        public void FloorResists_writes_the_floor_onto_a_real_creature_reading_its_final_armour()
        {
            var creature = new Creature(MakeWeenie(150), new ObjectGuid(nextGuid++));
            var session = ArchetypeUplift.Begin(creature, Info(Eight(0.6), Eight(0.8)), 1.0);
            var plan = new DungeonSpawnPlan { StripCombatTraits = true, CategoryResistFloor = 0.5, CategoryArmorModCeiling = 2.0, UpliftResistFloor = true };

            var writes = ArchetypeUplift.FloorResists(creature, session, plan);

            Assert.AreEqual(0.4, creature.GetProperty(PropertyFloat.ResistSlash).Value, 1e-12, "tougher than the median: kept");
            Assert.AreEqual(0.6, creature.GetProperty(PropertyFloat.ResistPierce).Value, 1e-12, "absent -> the median");
            Assert.AreEqual(1.5, creature.GetProperty(PropertyFloat.ResistFire).Value, 1e-12, "the weakness stands");
            Assert.AreEqual(0.8, creature.GetProperty(PropertyFloat.ArmorModVsCold).Value, 1e-12, "raised to the median");
            Assert.IsNull(creature.GetProperty(PropertyFloat.ArmorModVsSlash), "absent 1.0 is above 0.8: nothing written");
            Assert.AreEqual(writes.Count, writes.Select(w => w.Property).Distinct().Count(), "one write per property");

            var line = ArchetypeUplift.LogLine(1, 400, 200, session, new ArchetypeUplift.Result(), default, 0, 500, writes);
            StringAssert.Contains(line, "resist floor: ResistPierce:1->0.6");

            // No body armour: the armour mods stay authored.
            var bare = new Creature(MakeWeenie(0), new ObjectGuid(nextGuid++));
            ArchetypeUplift.FloorResists(bare, ArchetypeUplift.Begin(bare, Info(Eight(0.6), Eight(0.8)), 1.0), plan);
            Assert.AreEqual(0.3, bare.GetProperty(PropertyFloat.ArmorModVsCold).Value, 1e-12);
        }
    }
}
