using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// <see cref="DungeonWeakPoint"/>, the pure core of the Threads weak point. No PropertyManager, no Creature.
    /// </summary>
    [TestClass]
    public class DungeonWeakPointTests
    {
        private static PropertiesBodyPart HighPart(float armor = 100) => new PropertiesBodyPart { BaseArmor = (int)armor, HLF = 0.1f, HRF = 0.1f, HLB = 0.1f, HRB = 0.1f };
        private static PropertiesBodyPart MediumPart(float armor = 100) => new PropertiesBodyPart { BaseArmor = (int)armor, MLF = 0.1f, MRF = 0.1f, MLB = 0.1f, MRB = 0.1f };
        private static PropertiesBodyPart LowPart(float armor = 100) => new PropertiesBodyPart { BaseArmor = (int)armor, LLF = 0.1f, LRF = 0.1f, LLB = 0.1f, LRB = 0.1f };

        /// <summary>Equal weight at every height: hittable, dominant at none.</summary>
        private static PropertiesBodyPart TiedPart(int armor = 100) => new PropertiesBodyPart
        {
            BaseArmor = armor, HLF = 0.1f, MLF = 0.1f, LLF = 0.1f,
        };

        private static Dictionary<CombatBodyPart, PropertiesBodyPart> Humanoid() => new Dictionary<CombatBodyPart, PropertiesBodyPart>
        {
            [CombatBodyPart.Head] = HighPart(300),
            [CombatBodyPart.Chest] = new PropertiesBodyPart { BaseArmor = 400, HLF = 0.1f, HRF = 0.1f, HLB = 0.1f, HRB = 0.1f, MLF = 0.025f, MRF = 0.025f },
            [CombatBodyPart.Abdomen] = MediumPart(200),
            [CombatBodyPart.UpperLeg] = new PropertiesBodyPart { BaseArmor = 250, LLF = 0.1f, LRF = 0.1f, LLB = 0.1f, LRB = 0.1f, MLF = 0.05f },
        };

        private static List<CombatBodyPart> Sel(IDictionary<CombatBodyPart, PropertiesBodyPart> parts, AttackHeight? h, string family = "drudge", uint wcid = 1234)
            => DungeonWeakPoint.Select(parts, h, family, wcid).Parts.ToList();

        [TestMethod]
        public void Humanoid_shape_puts_head_and_chest_high_abdomen_medium_and_upper_leg_low()
        {
            var parts = Humanoid();

            CollectionAssert.AreEqual(new[] { CombatBodyPart.Head, CombatBodyPart.Chest }, Sel(parts, AttackHeight.High));
            CollectionAssert.AreEqual(new[] { CombatBodyPart.Abdomen }, Sel(parts, AttackHeight.Medium));
            CollectionAssert.AreEqual(new[] { CombatBodyPart.UpperLeg }, Sel(parts, AttackHeight.Low));
            Assert.AreEqual(DungeonWeakPoint.SelectionMode.Height, DungeonWeakPoint.Select(parts, AttackHeight.High, "drudge", 1).Mode);
        }

        [TestMethod]
        public void A_part_hit_equally_at_all_heights_has_no_dominant_height()
            => Assert.IsNull(DungeonWeakPoint.DominantHeight(TiedPart()));

        [TestMethod]
        public void A_two_way_tie_has_no_dominant_height()
        {
            var part = new PropertiesBodyPart { HLF = 0.4f, MLF = 0.4f };

            Assert.IsNull(DungeonWeakPoint.DominantHeight(part));
        }

        [TestMethod]
        public void A_lead_inside_the_tie_epsilon_is_a_tie_and_one_outside_it_is_not()
        {
            Assert.IsNull(DungeonWeakPoint.DominantHeight(new PropertiesBodyPart { HLF = 0.4f, MLF = 0.40005f }), "5e-5 apart is inside 1e-4");
            Assert.AreEqual(AttackHeight.Medium, DungeonWeakPoint.DominantHeight(new PropertiesBodyPart { HLF = 0.4f, MLF = 0.4002f }), "2e-4 apart is outside 1e-4");
        }

        [TestMethod]
        public void The_BH_field_is_never_read()
        {
            var part = new PropertiesBodyPart { BH = 3, LLF = 0.5f };

            Assert.AreEqual(AttackHeight.Low, DungeonWeakPoint.DominantHeight(part));
        }

        [TestMethod]
        public void A_zero_weight_part_is_never_selected_even_by_the_fallback()
        {
            var parts = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = new PropertiesBodyPart { BaseArmor = 100 },
                [CombatBodyPart.Chest] = TiedPart(),
                [CombatBodyPart.Abdomen] = TiedPart(),
                [CombatBodyPart.UpperLeg] = TiedPart(),
            };

            Assert.IsFalse(DungeonWeakPoint.IsHittable(parts[CombatBodyPart.Head]));
            Assert.IsNull(DungeonWeakPoint.DominantHeight(parts[CombatBodyPart.Head]));

            for (uint wcid = 1; wcid <= 40; wcid++)
            {
                Assert.IsFalse(Sel(parts, null, "x", wcid).Contains(CombatBodyPart.Head), $"wcid {wcid}");
                Assert.IsFalse(Sel(parts, AttackHeight.High, "x", wcid).Contains(CombatBodyPart.Head), $"wcid {wcid} (fallback)");
            }
        }

        [TestMethod]
        public void RandomThirdCount_rounds_a_third_away_from_zero_with_a_floor_of_one()
        {
            Assert.AreEqual(1, DungeonWeakPoint.RandomThirdCount(1));
            Assert.AreEqual(1, DungeonWeakPoint.RandomThirdCount(2));
            Assert.AreEqual(1, DungeonWeakPoint.RandomThirdCount(3));
            Assert.AreEqual(1, DungeonWeakPoint.RandomThirdCount(4));
            Assert.AreEqual(2, DungeonWeakPoint.RandomThirdCount(5));
            Assert.AreEqual(2, DungeonWeakPoint.RandomThirdCount(6));
            Assert.AreEqual(1, DungeonWeakPoint.RandomThirdCount(0), "never fewer than one");
        }

        [TestMethod]
        public void Hash_and_mixer_match_independently_computed_vectors()
        {
            // FNV-1a 64 over the UTF-8 bytes of "drudge|1234", computed outside C# with big-integer arithmetic.
            Assert.AreEqual(0x5473B451F7380C72UL, DungeonWeakPoint.StableSeed("drudge", 1234));
            // The published SplitMix64 first output for state 0.
            Assert.AreEqual(0xE220A8397B1DCDAFUL, DungeonWeakPoint.SplitMix64(0));
        }

        private static Dictionary<CombatBodyPart, PropertiesBodyPart> NineTied(IList<CombatBodyPart> order)
        {
            var d = new Dictionary<CombatBodyPart, PropertiesBodyPart>();

            foreach (var key in order)
                d[key] = TiedPart();

            return d;
        }

        private static readonly CombatBodyPart[] Nine =
        {
            CombatBodyPart.Head, CombatBodyPart.Chest, CombatBodyPart.Abdomen, CombatBodyPart.UpperArm, CombatBodyPart.LowerArm,
            CombatBodyPart.Hand, CombatBodyPart.UpperLeg, CombatBodyPart.LowerLeg, CombatBodyPart.Foot,
        };

        [TestMethod]
        public void The_random_third_is_stable_across_calls_and_across_dictionary_insertion_order()
        {
            var first = Sel(NineTied(Nine), null, "drudge", 1234);

            Assert.AreEqual(3, first.Count, "a third of nine hittable parts");
            CollectionAssert.AreEqual(first.OrderBy(k => (int)k).ToList(), first, "sorted by part key");

            var rng = new Random(7);

            for (var i = 0; i < 100; i++)
            {
                var shuffled = Nine.OrderBy(_ => rng.Next()).ToList();

                CollectionAssert.AreEqual(first, Sel(NineTied(shuffled), null, "drudge", 1234), $"call {i}");
            }
        }

        [TestMethod]
        public void Different_wcids_of_one_family_can_pick_different_parts()
        {
            var distinct = Enumerable.Range(1, 60).Select(w => string.Join(",", Sel(NineTied(Nine), null, "drudge", (uint)w))).Distinct().Count();

            Assert.IsTrue(distinct > 1, "sixty wcids all picked the same third");
        }

        [TestMethod]
        public void A_creature_with_no_part_dominant_at_the_height_falls_back_to_a_random_third()
        {
            var parts = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = MediumPart(), [CombatBodyPart.Chest] = MediumPart(), [CombatBodyPart.Abdomen] = MediumPart(),
                [CombatBodyPart.UpperArm] = MediumPart(), [CombatBodyPart.LowerArm] = MediumPart(), [CombatBodyPart.Hand] = MediumPart(),
            };

            var selection = DungeonWeakPoint.Select(parts, AttackHeight.High, "drudge", 99);

            Assert.AreEqual(DungeonWeakPoint.SelectionMode.RandomThirdFallback, selection.Mode);
            Assert.AreEqual(AttackHeight.High, selection.Height);
            Assert.AreEqual(2, selection.Parts.Count, "round(6 / 3)");
            Assert.AreEqual(DungeonWeakPoint.SelectionMode.Height, DungeonWeakPoint.Select(parts, AttackHeight.Medium, "drudge", 99).Mode, "positive control: the same creature has a Medium selection");
        }

        [TestMethod]
        public void A_family_with_no_height_is_marked_random_third_family()
        {
            var selection = DungeonWeakPoint.Select(Humanoid(), null, "wisp", 5);

            Assert.AreEqual(DungeonWeakPoint.SelectionMode.RandomThirdFamily, selection.Mode);
            Assert.IsNull(selection.Height);
            Assert.AreEqual(DungeonWeakPoint.RandomThirdCount(4), selection.Parts.Count);
        }

        [TestMethod]
        public void CutArmor_cases()
        {
            Assert.AreEqual(0, DungeonWeakPoint.CutArmor(0, 0.4));
            Assert.AreEqual(-100, DungeonWeakPoint.CutArmor(-100, 0.4), "negative armour is unchanged, never made stronger");
            Assert.AreEqual(300, DungeonWeakPoint.CutArmor(300, -0.5), "a negative cut leaves the value");
            Assert.AreEqual(300, DungeonWeakPoint.CutArmor(300, 0), "a zero cut leaves the value");
            Assert.AreEqual(300, DungeonWeakPoint.CutArmor(300, double.NaN), "NaN leaves the value");
            Assert.AreEqual(0, DungeonWeakPoint.CutArmor(300, 1));
            Assert.AreEqual(0, DungeonWeakPoint.CutArmor(300, 7), "a cut above one reads as one");
            Assert.AreEqual(180, DungeonWeakPoint.CutArmor(300, 0.4));
            Assert.AreEqual(3, DungeonWeakPoint.CutArmor(5, 0.5), "2.5 rounds away from zero");
        }

        [TestMethod]
        public void Apply_that_changes_nothing_allocates_and_assigns_nothing()
        {
            var parts = new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Head] = HighPart(0), [CombatBodyPart.Abdomen] = MediumPart(50) };
            var biota = new ACE.Entity.Models.Biota { PropertiesBodyPart = parts };
            var head = parts[CombatBodyPart.Head];
            var selection = DungeonWeakPoint.Select(parts, AttackHeight.High, "x", 1);

            Assert.AreEqual(1, selection.Parts.Count, "guard: the head is selected");
            Assert.AreEqual(0, DungeonWeakPoint.Apply(biota, selection, 0.4).Count, "armour 0 cuts to 0: nothing changes");
            Assert.AreSame(parts, biota.PropertiesBodyPart);
            Assert.AreSame(head, biota.PropertiesBodyPart[CombatBodyPart.Head]);

            var selectionB = DungeonWeakPoint.Select(parts, AttackHeight.Medium, "x", 1);

            Assert.AreEqual(0, DungeonWeakPoint.Apply(biota, selectionB, 0).Count, "a zero cut changes nothing");
            Assert.AreSame(parts, biota.PropertiesBodyPart);
        }

        [TestMethod]
        public void Apply_builds_a_fresh_dictionary_of_cloned_parts_and_never_writes_the_original()
        {
            var parts = Humanoid();
            var originals = parts.ToDictionary(kv => kv.Key, kv => kv.Value);
            var armorBefore = parts.ToDictionary(kv => kv.Key, kv => kv.Value.BaseArmor);
            var biota = new ACE.Entity.Models.Biota { PropertiesBodyPart = parts };

            var changes = DungeonWeakPoint.Apply(biota, DungeonWeakPoint.Select(parts, AttackHeight.High, "x", 1), 0.4);

            Assert.AreEqual(2, changes.Count);
            CollectionAssert.AreEqual(new[] { (CombatBodyPart.Head, 300, 180), (CombatBodyPart.Chest, 400, 240) }, changes);
            Assert.AreNotSame(parts, biota.PropertiesBodyPart);

            foreach (var kv in originals)
            {
                Assert.AreNotSame(kv.Value, biota.PropertiesBodyPart[kv.Key], $"{kv.Key} must be a clone, cut or not");
                Assert.AreEqual(armorBefore[kv.Key], kv.Value.BaseArmor, $"{kv.Key}: the original part must not be written");
            }

            Assert.AreEqual(180, biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
            Assert.AreEqual(240, biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);
            Assert.AreEqual(200, biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor, "an unselected part keeps its value");
            Assert.AreEqual(250, biota.PropertiesBodyPart[CombatBodyPart.UpperLeg].BaseArmor);
        }

        [TestMethod]
        public void TryParseHeight_accepts_the_four_tokens_and_nothing_else()
        {
            Assert.IsTrue(DungeonWeakPoint.TryParseHeight("high", out var h) && h == AttackHeight.High);
            Assert.IsTrue(DungeonWeakPoint.TryParseHeight("medium", out h) && h == AttackHeight.Medium);
            Assert.IsTrue(DungeonWeakPoint.TryParseHeight("low", out h) && h == AttackHeight.Low);
            Assert.IsTrue(DungeonWeakPoint.TryParseHeight("random", out h) && h == null);

            foreach (var bad in new[] { null, "", "High", "mid", "med", "random " })
                Assert.IsFalse(DungeonWeakPoint.TryParseHeight(bad, out _), $"'{bad}'");
        }
    }
}
