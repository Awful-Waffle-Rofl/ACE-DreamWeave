using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.SalvageRoller;
using ACE.Server.SpellReroll;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SalvageRollerEngine (Docs/SalvageRoller/DESIGN.md 5.5, plan step 5):
    ///
    ///   - for every action, the original plus a candidate's diff equals the rolled copy;
    ///   - Amethyst candidate k is the weapon after k swaps in a row on ONE working copy;
    ///   - the target itself is never changed;
    ///   - the payload shape is DESIGN.md 5.2's and refuses an unknown version.
    ///
    /// "Equals" is a canonical dump of every property table, sorted, so dictionary order cannot hide or fake a match.
    /// The rollers draw from the global ThreadSafeRandom, which has no seed, so these tests prove the relation
    /// between each candidate and its rolled copy rather than any particular roll.
    /// </summary>
    [TestClass]
    public class SalvageRollerEngineTests
    {
        private static uint nextGuid = 0x7D4C0000;   // static guid range, clear of GuidManager and the other test classes
        private static uint nextWcid = 993600;

        private Func<WorldObject, (SpellRerollPlan Plan, RerollRefusal Refusal)> savedPlanner;
        private Func<SpellRerollSwap, string> savedDescriber;
        private Func<int, int> savedGreenJadeNext;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Every double the engine's roll paths read through PropertyManager. Seeded to the shipped default before each
        /// test and put back after, because PropertyManager's cache is process-wide and another class may have left a
        /// different value (step 5 review F2: poisoning special_chance to 0 failed two tests here).
        /// </summary>
        private static readonly string[] EngineDoubleKeys =
        {
            "weapon_mod_special_chance_1",
            "weapon_mod_special_chance_2",
            "weapon_mod_special_chance_3",
            "weapon_mod_special_chance_4",
            "weapon_mod_magnitude_scale",
            "equipment_mod_potency_scale",
        };

        /// <summary>Every bool the engine's roll paths read, beside the two feature switches set to true below.</summary>
        private static readonly string[] EngineBoolKeys =
        {
            "weapon_mod_guarantee_damage_special",
            "allow_combat_mode_crafting",
            "serpentine_reroll_enabled",
        };

        [TestInitialize]
        public void Setup()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("equipment_mods_enabled", true));

            foreach (var key in EngineDoubleKeys)
                Assert.IsTrue(PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item), key);

            foreach (var key in EngineBoolKeys)
                Assert.IsTrue(PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item), key);

            savedPlanner = SalvageRollerEngine.SerpentinePlanner;
            savedDescriber = SalvageRollerEngine.SerpentineSwapDescriber;
            savedGreenJadeNext = SalvageRollerEngine.GreenJadeNext;
        }

        [TestCleanup]
        public void Cleanup()
        {
            SalvageRollerEngine.SerpentinePlanner = savedPlanner;
            SalvageRollerEngine.SerpentineSwapDescriber = savedDescriber;
            SalvageRollerEngine.GreenJadeNext = savedGreenJadeNext;

            foreach (var key in new[] { "weapon_mods_enabled", "equipment_mods_enabled" }.Concat(EngineBoolKeys))
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            foreach (var key in EngineDoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        // ---------------- fixtures ----------------

        private static WorldObject Weapon(string name = "Test Sword")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Armor(Dictionary<PropertyInt, int> ints)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)EquipMask.ChestArmor },
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Hauberk" } },
            };

            foreach (var kvp in ints)
                weenie.PropertiesInt[kvp.Key] = kvp.Value;

            var wo = new Clothing(weenie, new ObjectGuid(nextGuid++));

            EquipmentModManager.StampOriginalGearRatings(wo);

            return wo;
        }

        private static WorldObject Robe(params int[] spells)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Clothing } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Robe" } },
                PropertiesSpellBook = spells.ToDictionary(s => s, s => 2.0f),
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>Every property table of an item, sorted, as one string. Equal strings = equal item state.</summary>
        private static string Canon(WorldObject wo)
        {
            var item = DuoGemSnapshot.Capture(wo).Item;
            var sb = new StringBuilder();

            void Table<T>(string label, Dictionary<int, T> table)
            {
                sb.Append(label).Append(':');
                foreach (var kvp in table.OrderBy(k => k.Key))
                    sb.Append(kvp.Key).Append('=').Append(JsonSerializer.Serialize(kvp.Value)).Append(';');
                sb.Append('\n');
            }

            sb.Append("w=").Append(item.WeenieClassId).Append(" wt=").Append(item.WeenieType).Append('\n');
            Table("i", item.Ints);
            Table("i64", item.Int64s);
            Table("b", item.Bools);
            Table("f", item.Floats);
            Table("s", item.Strings);
            Table("did", item.DataIds);
            Table("sb", item.SpellBook);
            sb.Append("rest=").Append(JsonSerializer.Serialize(new object[] { item.AnimParts, item.Palettes, item.TextureMaps, item.Enchantments }));

            return sb.ToString();
        }

        /// <summary>A fresh copy of <paramref name="original"/> with <paramref name="candidate"/>'s diff applied.</summary>
        private static WorldObject OriginalPlus(WorldObject original, SalvageRollerCandidate candidate)
        {
            var copy = SalvageRollerEngine.NewScratchCopy(DuoGemSnapshot.Capture(original));
            SalvageRollerEngine.ApplyDiff(copy, candidate.Diff);
            return copy;
        }

        /// <summary>Rolls, recording the canonical state of each rolled copy AT THE MOMENT its candidate was taken.</summary>
        private static List<SalvageRollerCandidate> RollRecording(WorldObject target, SalvageRollerAction action, int rolls,
            out Dictionary<int, string> rolledStates, out List<WorldObject> rolledCopies)
        {
            var states = new Dictionary<int, string>();
            var copies = new List<WorldObject>();

            var candidates = SalvageRollerEngine.Roll(target, action, rolls, (seq, copy) =>
            {
                states[seq] = Canon(copy);
                copies.Add(copy);
            });

            rolledStates = states;
            rolledCopies = copies;
            return candidates;
        }

        private static void AssertOriginalPlusDiffIsTheRolledCopy(WorldObject original, List<SalvageRollerCandidate> candidates, Dictionary<int, string> rolledStates)
        {
            foreach (var candidate in candidates)
                Assert.AreEqual(rolledStates[candidate.Seq], Canon(OriginalPlus(original, candidate)), $"candidate {candidate.Seq}: original + diff != rolled copy");
        }

        // ---------------- original + result == rolled copy, every action ----------------

        [TestMethod]
        public void Tourmaline_OriginalPlusDiff_IsTheRolledCopy_AndTheTargetIsUntouched()
        {
            var sword = Weapon();
            var before = Canon(sword);

            var candidates = RollRecording(sword, SalvageRollerAction.TourmalineReroll, 12, out var states, out var copies);

            CollectionAssert.AreEqual(Enumerable.Range(1, 12).ToList(), candidates.Select(c => c.Seq).ToList(), "roll order, 1-based");
            AssertOriginalPlusDiffIsTheRolledCopy(sword, candidates, states);
            Assert.AreEqual(before, Canon(sword), "the target is read, never changed");

            Assert.AreEqual(12, copies.Distinct().Count(), "every Tourmaline roll is a FRESH copy");
            Assert.IsTrue(copies.All(c => c.Guid == ObjectGuid.Invalid), "scratch copies take no dynamic guid");
            Assert.IsTrue(candidates.Any(c => !c.Diff.IsEmpty), "the rolls changed something");

            // the summary reads the rolled copy: total intensity, its tier, one line per special
            for (var i = 0; i < candidates.Count; i++)
            {
                var copy = copies[i];
                var summary = candidates[i].Summary;

                Assert.AreEqual(WeaponQualityTiers.TotalIntensity(copy), (int)summary.Total);
                Assert.AreEqual((int)WeaponQualityTiers.FromTotal((int)summary.Total), summary.Tier);
                Assert.AreEqual(Math.Max(1, WeaponModTinkerSet.SpecialCount(copy)), summary.Lines.Count);
            }
        }

        [TestMethod]
        public void Amethyst_IsAChain_CandidateKIsTheWeaponAfterKSwaps()
        {
            var sword = Weapon();
            var before = Canon(sword);

            var candidates = RollRecording(sword, SalvageRollerAction.AmethystSwap, 20, out var states, out var copies);

            Assert.AreEqual(20, candidates.Count);

            // ONE working copy, observed after every swap: each state is built on top of the previous one
            Assert.AreEqual(1, copies.Distinct().Count(), "the chain uses a single working copy");

            // and each candidate, applied to the ORIGINAL, reproduces the state after that many swaps
            AssertOriginalPlusDiffIsTheRolledCopy(sword, candidates, states);

            // a weapon with no specials gains at most one on the first swap, and the count never grows after that
            // (ApplySwap removes one and draws one)
            var counts = candidates.Select(c => OriginalPlus(sword, c)).Select(WeaponModTinkerSet.SpecialCount).ToList();
            Assert.IsTrue(counts.All(n => n <= 1), $"special counts after each swap: {string.Join(",", counts)}");

            Assert.AreEqual(before, Canon(sword), "the target is read, never changed");
        }

        [TestMethod]
        public void Amethyst_EachCandidateIsOneSwapOnTopOfTheLast()
        {
            // The draws cannot be replayed (ThreadSafeRandom has no seed), so this asserts the structural relation
            // instead: the state after swap k differs from the state after swap k-1 in at most one special identity
            // lost and one gained, which is what ONE ApplySwap can do. Starting from a weapon that already holds
            // several specials is what makes this discriminate: k INDEPENDENT swaps of the original would let two
            // neighbouring candidates differ in two specials each way.
            var sword = Weapon();

            for (var tries = 0; WeaponModTinkerSet.SpecialCount(sword) < 2 && tries < 500; tries++)
                WeaponModManager.ApplyReroll(sword, WeaponClassifier.Classify(sword), 10);

            Assert.IsTrue(WeaponModTinkerSet.SpecialCount(sword) >= 2, "precondition: a weapon holding at least two specials");

            var candidates = SalvageRollerEngine.Roll(sword, SalvageRollerAction.AmethystSwap, 30);

            var previous = WeaponModTinkerSet.ReadSpecials(sword).Select(s => s.Definition.Id).ToHashSet();

            foreach (var candidate in candidates)
            {
                var state = OriginalPlus(sword, candidate);
                var held = WeaponModTinkerSet.ReadSpecials(state).Select(s => s.Definition.Id).ToHashSet();

                var lost = previous.Except(held).Count();
                var gained = held.Except(previous).Count();

                Assert.IsTrue(lost <= 1 && gained <= 1, $"candidate {candidate.Seq}: lost {lost}, gained {gained} - more than one swap's worth");

                previous = held;
            }
        }

        // ---------------- Green Jade: a chain of add-or-swap steps ----------------

        private static WorldObject Ring(int attack, int defense, int utility) =>
            JewelryAspectApplicatorTests.Jewelry("Test Ring", EquipMask.FingerWearLeft | EquipMask.FingerWearRight, attack, defense, utility);

        /// <summary>A deterministic draw: the same seed always yields the same sequence, so two runs can be replayed against each other.</summary>
        private static Func<int, int> Draws(int seed)
        {
            var state = (uint)seed;

            return n =>
            {
                state = unchecked(state * 1664525u + 1013904223u);
                return (int)((state >> 16) % (uint)n);
            };
        }

        private static AspectCounts CountsOf(WorldObject wo) => JewelryAspectMath.ReadCounts(wo);

        [TestMethod]
        public void GreenJade_ChainParity_CandidateKIsKHandAppliedSteps_FromAnyStartingFill()
        {
            // 9 aspects crosses the add -> swap boundary on step 2; 1 aspect fills for nine steps; 10 is swaps throughout
            foreach (var start in new[] { (4, 3, 2), (1, 0, 0), (3, 4, 3) })
            {
                var ring = Ring(start.Item1, start.Item2, start.Item3);
                var before = Canon(ring);

                SalvageRollerEngine.GreenJadeNext = Draws(77);
                var candidates = RollRecording(ring, SalvageRollerAction.GreenJadeSwap, 14, out var states, out var copies);

                Assert.AreEqual(14, candidates.Count);
                Assert.AreEqual(1, copies.Distinct().Count(), "the chain uses a single working copy");
                AssertOriginalPlusDiffIsTheRolledCopy(ring, candidates, states);
                Assert.AreEqual(before, Canon(ring), "the target is read, never changed");

                // the same draw sequence applied by hand, one ApplyStep at a time
                var hand = Ring(start.Item1, start.Item2, start.Item3);
                var next = Draws(77);

                foreach (var candidate in candidates)
                {
                    var expected = GreenJadeManager.ApplyStep(hand, next, out _, out _, out _);
                    var actual = CountsOf(OriginalPlus(ring, candidate));

                    Assert.AreEqual(expected, actual, $"start {start}, candidate {candidate.Seq}: roller != {candidate.Seq} hand-applied steps");
                }
            }
        }

        [TestMethod]
        public void GreenJade_MidFillChain_AddsOnePerStepUntilTenThenSwapsKeepingTen()
        {
            var ring = Ring(3, 3, 2);   // 8 of 10

            SalvageRollerEngine.GreenJadeNext = Draws(5);
            var candidates = SalvageRollerEngine.Roll(ring, SalvageRollerAction.GreenJadeSwap, 6);

            var totals = candidates.Select(c => CountsOf(OriginalPlus(ring, c)).Total).ToArray();
            CollectionAssert.AreEqual(new[] { 9, 10, 10, 10, 10, 10 }, totals, "two adds, then swaps that keep ten");

            // a swap moves exactly one slot to a different type
            var previous = CountsOf(OriginalPlus(ring, candidates[1]));

            foreach (var candidate in candidates.Skip(2))
            {
                var now = CountsOf(OriginalPlus(ring, candidate));
                var moved = Math.Abs(now.Attack - previous.Attack) + Math.Abs(now.Defense - previous.Defense) + Math.Abs(now.Utility - previous.Utility);

                Assert.AreEqual(2, moved, $"candidate {candidate.Seq}: one slot out, one in");
                previous = now;
            }
        }

        [TestMethod]
        public void GreenJade_DrawsGoThroughTheSeam_NotAFixedSource()
        {
            var ring = Ring(2, 2, 2);
            var seen = 0;

            SalvageRollerEngine.GreenJadeNext = n => { seen++; return 0; };
            var candidates = SalvageRollerEngine.Roll(ring, SalvageRollerAction.GreenJadeSwap, 3);

            Assert.IsTrue(seen >= 3, "at least one draw per charge");

            // draw 0 always adds Attack: 3/2/2, 4/2/2, 5/2/2
            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, candidates.Select(c => CountsOf(OriginalPlus(ring, c)).Attack).ToArray());
        }

        [TestMethod]
        public void GreenJade_Summary_TierIsTheLeadingCount_TotalIsTheRunnerUp_OneLine()
        {
            var s = SalvageRollerEngine.GreenJadeSummary(new AspectCounts(2, 6, 1));
            Assert.AreEqual(6, s.Tier);
            Assert.AreEqual(2, s.Total);
            CollectionAssert.AreEqual(new[] { "Attack 2 / Defense 6 / Utility 1" }, s.Lines);

            var tie = SalvageRollerEngine.GreenJadeSummary(new AspectCounts(5, 5, 0));
            Assert.AreEqual(5, tie.Tier);
            Assert.AreEqual(5, tie.Total, "a tie for the lead makes the runner-up as large as the leader");

            var even = SalvageRollerEngine.GreenJadeSummary(new AspectCounts(3, 3, 3));
            Assert.AreEqual(3, even.Tier);
            Assert.AreEqual(3, even.Total);

            // and the rolled candidates carry the summary of THEIR state
            var ring = Ring(4, 3, 2);
            SalvageRollerEngine.GreenJadeNext = Draws(9);
            var candidates = RollRecording(ring, SalvageRollerAction.GreenJadeSwap, 5, out _, out _);

            foreach (var c in candidates)
            {
                var counts = CountsOf(OriginalPlus(ring, c));
                var expected = SalvageRollerEngine.GreenJadeSummary(counts);

                Assert.AreEqual(expected.Tier, c.Summary.Tier);
                Assert.AreEqual(expected.Total, c.Summary.Total);
                CollectionAssert.AreEqual(expected.Lines, c.Summary.Lines);
            }
        }

        [TestMethod]
        public void GreenJade_AnUnaspectedPiece_ThrowsRatherThanReturningAPartialList()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => SalvageRollerEngine.Roll(Ring(0, 0, 0), SalvageRollerAction.GreenJadeSwap, 3));
        }

        [TestMethod]
        public void GreenJadeNext_DefaultsToTheManagersProductionDraw()
        {
            Assert.AreSame(GreenJadeManager.DefaultNext, savedGreenJadeNext);
        }

        [TestMethod]
        public void ObsidianCandidateLines_CarryRequirementSuffix()
        {
            var plate = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 2 }, { PropertyInt.GearDamage, 1 } });

            var candidates = RollRecording(plate, SalvageRollerAction.ObsidianConvert, 5, out _, out var copies);

            Assert.IsTrue(candidates.Count > 0);

            for (var i = 0; i < candidates.Count; i++)
            {
                var expected = EquipmentModDisplay.GetMods(copies[i])
                    .Select(m => $"{EquipmentModDisplay.Describe(m.Definition, m.Potency)} ({EquipmentModDisplay.AbilityRequirement(m.Definition)})").ToList();

                CollectionAssert.AreEqual(expected, candidates[i].Summary.Lines);
                Assert.IsTrue(candidates[i].Summary.Lines.All(l => l.EndsWith("Works without the ability)") || (l.Contains(" (Requires ") && l.EndsWith(" trained)"))),
                    "every Obsidian candidate line ends in a requirement suffix");
            }
        }

        [TestMethod]
        public void ObsidianConvert_OriginalPlusDiff_IsTheRolledCopy()
        {
            var plate = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 2 }, { PropertyInt.GearDamage, 1 } });
            var before = Canon(plate);

            var candidates = RollRecording(plate, SalvageRollerAction.ObsidianConvert, 10, out var states, out var copies);

            AssertOriginalPlusDiffIsTheRolledCopy(plate, candidates, states);
            Assert.AreEqual(before, Canon(plate));

            foreach (var candidate in candidates)
            {
                // the natural ratings are spent and three mods minted, with capacity grown to match
                Assert.IsTrue(candidate.Diff.Ints.ContainsKey((int)PropertyInt.GearCrit), "the rating is cleared");
                Assert.IsNull(candidate.Diff.Ints[(int)PropertyInt.GearCrit]);
                Assert.AreEqual(3, candidate.Diff.Ints[(int)PropertyInt.GearModCapacity]);
                Assert.AreEqual(3, candidate.Summary.Lines.Count);
                Assert.AreEqual(0, candidate.Summary.Tier);
            }

            for (var i = 0; i < copies.Count; i++)
                Assert.AreEqual(EquipmentModDisplay.GetMods(copies[i]).Sum(m => m.Potency), candidates[i].Summary.Total, 1e-12, "total = summed mod fractions");
        }

        [TestMethod]
        public void ObsidianReroll_OriginalPlusDiff_IsTheRolledCopy()
        {
            var plate = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 2 } });
            Assert.IsNotNull(EquipmentModManager.ApplyConvertOrReroll(plate, EquipmentModManager.ModAction.Convert, 2,
                (p, v) => EquipmentModManager.WriteRatingToItem(plate, p, v)));

            var before = Canon(plate);

            var candidates = RollRecording(plate, SalvageRollerAction.ObsidianReroll, 10, out var states, out _);

            AssertOriginalPlusDiffIsTheRolledCopy(plate, candidates, states);
            Assert.AreEqual(before, Canon(plate));
            Assert.IsTrue(candidates.All(c => c.Summary.Lines.Count == 2), "a reroll keeps the mod count");
        }

        [TestMethod]
        public void Serpentine_OriginalPlusDiff_IsTheRolledCopy_AndTheProbabilityIsKept()
        {
            var robe = Robe(1001, 1002);
            robe.Biota.TryRemoveKnownSpell(1002, robe.BiotaDatabaseLock);
            robe.Biota.GetOrAddKnownSpell(1002, robe.BiotaDatabaseLock, out _, 1.5f);

            var before = Canon(robe);
            var roll = 0;

            SalvageRollerEngine.SerpentinePlanner = copy =>
            {
                roll++;
                var plan = new SpellRerollPlan();
                plan.Swaps.Add(new SpellRerollSwap(1002, 2000 + roll, 7));
                return (plan, RerollRefusal.None);
            };
            SalvageRollerEngine.SerpentineSwapDescriber = s => $"{s.OldSpell} becomes {s.NewSpell}.";

            var candidates = RollRecording(robe, SalvageRollerAction.SerpentineReroll, 3, out var states, out _);

            AssertOriginalPlusDiffIsTheRolledCopy(robe, candidates, states);
            Assert.AreEqual(before, Canon(robe));

            var second = candidates[1];
            CollectionAssert.AreEqual(new[] { 1002 }, second.Diff.Spells.Remove);
            Assert.AreEqual(1, second.Diff.Spells.Add.Count);
            Assert.AreEqual(1.5f, second.Diff.Spells.Add[2002], "the swapped-in spell keeps the old one's cast probability");
            CollectionAssert.AreEqual(new[] { "1002 becomes 2002." }, second.Summary.Lines);
        }

        // ---------------- refusals ----------------

        [TestMethod]
        public void Roll_RefusesOutOfRangeCountsAndNoAction()
        {
            var sword = Weapon();

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SalvageRollerEngine.Roll(sword, SalvageRollerAction.TourmalineReroll, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SalvageRollerEngine.Roll(sword, SalvageRollerAction.TourmalineReroll, 101));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SalvageRollerEngine.Roll(sword, SalvageRollerAction.None, 1));
            Assert.ThrowsExactly<ArgumentNullException>(() => SalvageRollerEngine.Roll(null, SalvageRollerAction.TourmalineReroll, 1));

            Assert.AreEqual(100, SalvageRollerEngine.Roll(sword, SalvageRollerAction.TourmalineReroll, 100).Count, "100 is allowed");
        }

        [TestMethod]
        public void Roll_ARollThatCannotBeMade_Throws_RatherThanReturningAPartialList()
        {
            var plate = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 1 } });

            // the item resolves to Convert, so a Reroll batch must not be rolled on it
            Assert.ThrowsExactly<InvalidOperationException>(() => SalvageRollerEngine.Roll(plate, SalvageRollerAction.ObsidianReroll, 3));

            // a weapon roll on something that is not a weapon
            Assert.ThrowsExactly<InvalidOperationException>(() => SalvageRollerEngine.Roll(plate, SalvageRollerAction.TourmalineReroll, 3));

            SalvageRollerEngine.SerpentinePlanner = _ => (new SpellRerollPlan(), RerollRefusal.EmptyPool);
            Assert.ThrowsExactly<InvalidOperationException>(() => SalvageRollerEngine.Roll(Robe(1001), SalvageRollerAction.SerpentineReroll, 2));
        }

        [TestMethod]
        public void ComputeDiff_RefusesATableItCannotCarry()
        {
            var robe = Robe(1001);
            var before = DuoGemSnapshot.Capture(robe);
            var after = DuoGemSnapshot.Capture(robe);

            after.Item.Palettes.Add(new ACE.Server.Pvp.Templates.PvpTemplatePalette { SubPaletteId = 1, Offset = 0, Length = 1 });

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => SalvageRollerEngine.ComputeDiff(before, after));
            StringAssert.Contains(ex.Message, "palettes");
        }

        [TestMethod]
        public void ApplyDiff_SetsAndRemoves_EveryTable()
        {
            var robe = Robe(1001);
            robe.SetProperty(PropertyInt.GearCrit, 3);
            robe.SetProperty(PropertyFloat.ElementalDamageMod, 1.5);

            var diff = new SalvageRollerDiff();
            diff.Ints[(int)PropertyInt.GearCrit] = null;
            diff.Ints[(int)PropertyInt.GearModCapacity] = 2;
            diff.Floats[(int)PropertyFloat.ElementalDamageMod] = null;
            diff.Int64s[(int)PropertyInt64.ItemTotalXp] = 77;
            diff.Strings[(int)PropertyString.LongDesc] = "rolled";
            diff.DataIds[(int)PropertyDataId.Icon] = 0x06001234;
            diff.Bools[(int)PropertyBool.Retained] = true;
            diff.Spells.Remove.Add(1001);
            diff.Spells.Add[1002] = 1.25f;

            SalvageRollerEngine.ApplyDiff(robe, diff);

            Assert.IsNull(robe.GetProperty(PropertyInt.GearCrit));
            Assert.AreEqual(2, robe.GetProperty(PropertyInt.GearModCapacity));
            Assert.IsNull(robe.GetProperty(PropertyFloat.ElementalDamageMod));
            Assert.AreEqual(77L, robe.GetProperty(PropertyInt64.ItemTotalXp));
            Assert.AreEqual("rolled", robe.GetProperty(PropertyString.LongDesc));
            Assert.AreEqual(0x06001234u, robe.GetProperty(PropertyDataId.Icon));
            Assert.AreEqual(true, robe.GetProperty(PropertyBool.Retained));

            var spells = robe.Biota.PropertiesSpellBook;
            Assert.IsFalse(spells.ContainsKey(1001));
            Assert.AreEqual(1.25f, spells[1002]);
        }

        // ---------------- payload ----------------

        [TestMethod]
        public void Payload_HasTheDesignsShape_AndRoundTrips()
        {
            var plate = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 1 } });
            var payload = new SalvageRollerPayload(SalvageRollerEngine.Roll(plate, SalvageRollerAction.ObsidianConvert, 3));

            var json = payload.Serialize();

            foreach (var key in new[] { "\"v\":1", "\"cands\":", "\"seq\":1", "\"diff\":", "\"ints\":", "\"int64s\":", "\"floats\":", "\"strings\":",
                "\"dids\":", "\"spells\":", "\"add\":", "\"remove\":", "\"summary\":", "\"tier\":", "\"total\":", "\"lines\":" })
                StringAssert.Contains(json, key);

            // a removal is carried as an explicit null, not dropped
            StringAssert.Contains(json, $"\"{(int)PropertyInt.GearCrit}\":null");

            Assert.IsTrue(SalvageRollerPayload.TryRead(json, out var read, out var error), error);
            Assert.AreEqual(json, read.Serialize(), "a read payload serializes back to the same text");
            Assert.AreEqual(3, read.Candidates.Count);
            Assert.IsNotNull(read.Find(2));
            Assert.IsNull(read.Find(4));

            // and the read diff still reproduces the result
            var copy = SalvageRollerEngine.NewScratchCopy(DuoGemSnapshot.Capture(plate));
            SalvageRollerEngine.ApplyDiff(copy, read.Find(1).Diff);
            Assert.IsNull(copy.GetProperty(PropertyInt.GearCrit));
        }

        [TestMethod]
        public void Payload_TryRead_RefusesWhatItCannotTrust()
        {
            Assert.IsFalse(SalvageRollerPayload.TryRead(null, out _, out _));
            Assert.IsFalse(SalvageRollerPayload.TryRead("   ", out _, out _));
            Assert.IsFalse(SalvageRollerPayload.TryRead("{not json", out _, out _));

            Assert.IsFalse(SalvageRollerPayload.TryRead("{\"v\":2,\"cands\":[]}", out _, out var error));
            StringAssert.Contains(error, "version 2");

            Assert.IsFalse(SalvageRollerPayload.TryRead("{\"v\":1}", out _, out _), "no candidate list");
            Assert.IsFalse(SalvageRollerPayload.TryRead("{\"v\":1,\"cands\":[]}", out _, out _), "an empty batch is never valid");
            Assert.IsFalse(SalvageRollerPayload.TryRead("{\"cands\":[{\"seq\":1,\"diff\":{},\"summary\":{}}]}", out _, out _), "a missing version is not version 1");
            Assert.IsFalse(SalvageRollerPayload.TryRead("{\"v\":1,\"cands\":[{\"seq\":1}]}", out _, out _), "no diff or summary");

            const string twice = "{\"v\":1,\"cands\":[{\"seq\":1,\"diff\":{},\"summary\":{}},{\"seq\":1,\"diff\":{},\"summary\":{}}]}";
            Assert.IsFalse(SalvageRollerPayload.TryRead(twice, out _, out _), "a repeated sequence number");

            const string zero = "{\"v\":1,\"cands\":[{\"seq\":0,\"diff\":{},\"summary\":{}}]}";
            Assert.IsFalse(SalvageRollerPayload.TryRead(zero, out _, out _), "sequence numbers start at 1");

            // an empty diff object is filled in, not left null
            Assert.IsTrue(SalvageRollerPayload.TryRead("{\"v\":1,\"cands\":[{\"seq\":1,\"diff\":{},\"summary\":{}}]}", out var read, out error), error);
            Assert.IsTrue(read.Candidates[0].Diff.IsEmpty);
        }

        [TestMethod]
        public void Payload_TryRead_RequiresSequenceNumbersExactlyOneToN()
        {
            // step 5 review F6: {1,3} is positive and unique, but candidate 2 is missing, so a take of row 2 would
            // find nothing. The take compares N with the batch's roll_count as well.
            const string gap = "{\"v\":1,\"cands\":[{\"seq\":1,\"diff\":{},\"summary\":{}},{\"seq\":3,\"diff\":{},\"summary\":{}}]}";
            Assert.IsFalse(SalvageRollerPayload.TryRead(gap, out _, out var error), "{1,3} is not 1..2");
            StringAssert.Contains(error, "1..2");

            const string offByOne = "{\"v\":1,\"cands\":[{\"seq\":2,\"diff\":{},\"summary\":{}}]}";
            Assert.IsFalse(SalvageRollerPayload.TryRead(offByOne, out _, out _), "{2} is not 1..1");

            // order in the array does not matter; the set does
            const string shuffled = "{\"v\":1,\"cands\":[{\"seq\":2,\"diff\":{},\"summary\":{}},{\"seq\":1,\"diff\":{},\"summary\":{}}]}";
            Assert.IsTrue(SalvageRollerPayload.TryRead(shuffled, out var read, out error), error);
            Assert.AreEqual(2, read.Candidates.Count);
        }

        // ---------------- the effect slot never rides a candidate (delivery bug, 2026-11-03) ----------------

        /// <summary>
        /// PIN, not a discriminator: the take's tier transition owns PropertyDataId 9000 (VisualEffectScript), so no
        /// candidate diff may carry it. The rollers work on scratch copies and never run ApplyTierScript, so both
        /// snapshots of a copy hold the same 9000 value and the diff is clean. If a roller ever started writing the
        /// slot, a take would overwrite the real weapon's base script or aura with the scratch copy's.
        /// </summary>
        [TestMethod]
        public void WeaponRoll_CandidateDiff_NeverCarriesTheVisualEffectScript()
        {
            const uint baseScript = 0x33001154;

            foreach (var action in new[] { SalvageRollerAction.TourmalineReroll, SalvageRollerAction.AmethystSwap })
            {
                var sword = Weapon($"Smoking Sword {action}");
                sword.VisualEffectScript = baseScript;

                var candidates = SalvageRollerEngine.Roll(sword, action, 20);

                Assert.AreEqual(20, candidates.Count);

                foreach (var candidate in candidates)
                {
                    var tag = $"{action} candidate {candidate.Seq}";

                    Assert.IsFalse(candidate.Diff.DataIds.ContainsKey((int)PropertyDataId.VisualEffectScript), $"{tag}: VisualEffectScript in the diff");
                    Assert.AreEqual(baseScript, OriginalPlus(sword, candidate).VisualEffectScript, $"{tag}: the script survives original plus diff");
                }

                Assert.AreEqual(baseScript, sword.VisualEffectScript, $"{action}: the target itself is untouched");
            }
        }

        // ---------------- tinker bookkeeping never rides a candidate (step 5 review F3) ----------------

        [TestMethod]
        public void TinkerBookkeeping_NeverAppearsInADiff_AndSurvivesOriginalPlus()
        {
            foreach (var action in new[] { SalvageRollerAction.TourmalineReroll, SalvageRollerAction.AmethystSwap })
            {
                var sword = Weapon($"Tinkered Sword {action}");

                // a managed weapon with a full tinker composition: count, both logs and NumTimesTinkered all set
                WeaponModTinkerSet.WriteComposition(sword, null, new[] { MaterialType.Iron, MaterialType.Iron, MaterialType.Granite });

                var tinkered = sword.GetProperty(PropertyInt.NumTimesTinkered);
                var modCount = sword.GetProperty(PropertyInt.WeaponModTinkerCount);
                var log = sword.GetProperty(PropertyString.TinkerLog);
                var modLog = sword.GetProperty(PropertyString.WeaponModTinkerLog);

                Assert.IsNotNull(tinkered, "precondition: NumTimesTinkered set");
                Assert.IsNotNull(modCount, "precondition: WeaponModTinkerCount set");
                Assert.IsFalse(string.IsNullOrEmpty(log), "precondition: TinkerLog set");
                Assert.IsFalse(string.IsNullOrEmpty(modLog), "precondition: WeaponModTinkerLog set");

                var candidates = SalvageRollerEngine.Roll(sword, action, 5);

                Assert.AreEqual(5, candidates.Count);

                foreach (var candidate in candidates)
                {
                    var tag = $"{action} candidate {candidate.Seq}";

                    Assert.IsFalse(candidate.Diff.Ints.ContainsKey((int)PropertyInt.NumTimesTinkered), $"{tag}: NumTimesTinkered in the diff");
                    Assert.IsFalse(candidate.Diff.Ints.ContainsKey((int)PropertyInt.WeaponModTinkerCount), $"{tag}: WeaponModTinkerCount in the diff");
                    Assert.IsFalse(candidate.Diff.Strings.ContainsKey((int)PropertyString.TinkerLog), $"{tag}: TinkerLog in the diff");
                    Assert.IsFalse(candidate.Diff.Strings.ContainsKey((int)PropertyString.WeaponModTinkerLog), $"{tag}: WeaponModTinkerLog in the diff");

                    var result = OriginalPlus(sword, candidate);

                    Assert.AreEqual(tinkered, result.GetProperty(PropertyInt.NumTimesTinkered), $"{tag}: NumTimesTinkered");
                    Assert.AreEqual(modCount, result.GetProperty(PropertyInt.WeaponModTinkerCount), $"{tag}: WeaponModTinkerCount");
                    Assert.AreEqual(log, result.GetProperty(PropertyString.TinkerLog), $"{tag}: TinkerLog");
                    Assert.AreEqual(modLog, result.GetProperty(PropertyString.WeaponModTinkerLog), $"{tag}: WeaponModTinkerLog");
                }
            }
        }

        // ---------------- production seams (step 5 review F4) ----------------

        private const string EngineSource = "Source/ACE.Server/SalvageRoller/SalvageRollerEngine.cs";

        /// <summary>The ThreadSafeRandom picker both Serpentine paths must share. Next's max is inclusive, hence n - 1.</summary>
        private const string SerpentinePicker = "n => ThreadSafeRandom.Next(0, n - 1)";

        [TestMethod]
        public void SerpentinePlanner_DefaultsToBuildPlan_WithTheBagPathsPicker()
        {
            var engine = PooledLootSourceText.Read(EngineSource);

            StringAssert.Contains(engine,
                "internal static Func<WorldObject, (SpellRerollPlan Plan, RerollRefusal Refusal)> SerpentinePlanner = target =>",
                "the production planner is the seam's initializer");
            StringAssert.Contains(engine, $"SpellRerollManager.BuildPlan(target, {SerpentinePicker}, out var refusal)",
                "the production planner draws with ThreadSafeRandom over 0..n-1");

            // the bag path draws the same way, so a roller result is distributed exactly like a bag Serpentine
            var bag = PooledLootSourceText.Read("Source/ACE.Server/SpellReroll/SpellRerollManager.cs");
            StringAssert.Contains(bag, $"BuildPlan(target, {SerpentinePicker}, out var refusal)", "the bag path's picker moved; re-check the engine's");
        }

        [TestMethod]
        public void SerpentineSwapDescriber_DefaultsToSpellRerollManagerDescribeSwap()
        {
            var engine = PooledLootSourceText.Read(EngineSource);

            StringAssert.Contains(engine,
                "internal static Func<SpellRerollSwap, string> SerpentineSwapDescriber = SpellRerollManager.DescribeSwap;");
        }
    }
}

