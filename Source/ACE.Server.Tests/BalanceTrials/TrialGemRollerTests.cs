using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using ACE.Server.BalanceTrials;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Pins the pure half of the trial gem roller. The expected values for a rolled gem come from calling
    /// RawFragmentRules.Resolve directly in the test, never from the roller. Inline fixtures only; reads no PropertyManager key
    /// (limits come from FragmentPressStation.ComposeLimits, the pure half of BuildLimits).
    /// </summary>
    [TestClass]
    public class TrialGemRollerTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";

        private const string ModifiersJson =
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"display\":\"Hardy\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\"}," +
            "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\"}," +
            "{\"id\":\"precise\",\"display\":\"Precise\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":30,\"monsterEffectKind\":\"crit_rating\"}," +
            "{\"id\":\"stalwart\",\"display\":\"Stalwart\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_resist_rating\"}" +
            "],\"xpLadder\":[]}";

        private static string Comp(uint wcid, string name, string modifier)
            => "{\"wcid\":" + wcid.ToString(CultureInfo.InvariantCulture) + ",\"name\":\"" + name + "\",\"type\":\"taper\",\"dose\":1,\"limit\":"
               + RawFragmentRules.SlotCounts["taper"].ToString(CultureInfo.InvariantCulture)
               + ",\"ops\":[{\"op\":\"" + AttunementOps.AddOrRaise + "\",\"modifier\":\"" + modifier
               + "\",\"amount\":0,\"entries\":0,\"bandMin\":0,\"bandMax\":1,\"weight\":100}]}";

        private static ThreadDungeonStore Store()
        {
            var roster = "{\"version\":1,\"colors\":[],\"components\":[" + string.Join(",",
                Comp(1650, "Red Taper", "savage"), Comp(1643, "Blue Taper", "precise"),
                Comp(1645, "Green Taper", "hardy"), Comp(1646, "Grey Taper", "stalwart")) + "]}";
            return ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, ModifiersJson, new Dictionary<string, string>(), attunementJson: roster);
        }

        private static DungeonGemSpec Fragment(int level)
            => new DungeonGemSpec("any", level, 8, "any", 0, null, 0, 0);

        private static TrialGem Gem(int level = 400, IReadOnlyList<(string Id, double Magnitude)> pinned = null, int seed = 1234, TrialSeedMode mode = TrialSeedMode.Fixed)
            => new TrialGem(level, 8, "artifex_collegium", "any", pinned, seed, mode);

        private static string ModString(IEnumerable<(string Id, double Magnitude)> mods)
            => string.Join(",", mods.Select(m => m.Id + ":" + m.Magnitude.ToString("R", CultureInfo.InvariantCulture)));

        [TestMethod]
        public void Rolled_mods_equal_a_direct_Resolve_with_the_same_seed_and_limits()
        {
            var store = Store();
            var limits = FragmentPressStation.ComposeLimits(4, 0.5);
            var gem = Gem(seed: 4242);
            var fragment = Fragment(375);

            // The expected value is taken straight from the rules, not from the roller.
            var expected = RawFragmentRules.Resolve(new PressState(fragment, 0), store.Attunement, store.Modifiers, limits, new Random(4242), out _);
            Assert.IsTrue(expected.Spec.Modifiers.Count > 0, "fixture must roll at least one modifier");

            // Positive control: a different stream gives a different result, so a wrong seed is detectable.
            var wrong = RawFragmentRules.Resolve(new PressState(fragment, 0), store.Attunement, store.Modifiers, limits, new Random(0), out _);
            Assert.AreNotEqual(ModString(expected.Spec.Modifiers), ModString(wrong.Spec.Modifiers), "fixture is blind to the seed");

            var roll = TrialGemRoller.Roll(gem, 1, fragment, 0, store.Attunement, store.Modifiers, limits);

            Assert.AreEqual(ModString(expected.Spec.Modifiers), ModString(roll.Spec.Modifiers));
        }

        [TestMethod]
        public void Rolled_spec_runs_at_the_scenario_level_and_records_the_pressed_level()
        {
            var store = Store();
            var limits = FragmentPressStation.ComposeLimits(4, 0.5);
            var fragment = Fragment(375);

            var expected = RawFragmentRules.Resolve(new PressState(fragment, 0), store.Attunement, store.Modifiers, limits, new Random(77), out _);
            var roll = TrialGemRoller.Roll(Gem(400, seed: 77), 1, fragment, 0, store.Attunement, store.Modifiers, limits);

            Assert.AreEqual(400, roll.Spec.Level);
            Assert.AreEqual(expected.Spec.Level, roll.PressedLevel);
            Assert.AreNotEqual(roll.Spec.Level, roll.PressedLevel, "fixture must press to a level other than the scenario level");
            Assert.AreEqual(77, roll.RepSeed);
            Assert.AreEqual(77, roll.Spec.Seed);
            Assert.AreEqual("artifex_collegium", roll.Spec.DungeonId);
            Assert.AreEqual(8, roll.Spec.Tier);

            using var doc = JsonDocument.Parse(roll.ModsJson);
            Assert.AreEqual("rolled", doc.RootElement.GetProperty("mode").GetString());
            Assert.AreEqual(expected.Spec.Level, doc.RootElement.GetProperty("pressedLevel").GetInt32());
            var mods = doc.RootElement.GetProperty("mods").EnumerateArray().Select(e => (e.GetProperty("id").GetString(), e.GetProperty("magnitude").GetDouble())).ToList();
            CollectionAssert.AreEqual(expected.Spec.Modifiers.ToList(), mods);
        }

        [TestMethod]
        public void Pinned_mods_are_kept_at_the_scenario_level()
        {
            var store = Store();
            var pinned = new List<(string Id, double Magnitude)> { ("savage", 25), ("hardy", 1.5) };
            var roll = TrialGemRoller.Roll(Gem(400, pinned), 1, Fragment(375), 0, store.Attunement, store.Modifiers, FragmentPressStation.ComposeLimits(4, 0.5));

            CollectionAssert.AreEqual(pinned, roll.Spec.Modifiers.ToList());
            Assert.AreEqual(400, roll.PressedLevel);
            using var doc = JsonDocument.Parse(roll.ModsJson);
            Assert.AreEqual("pinned", doc.RootElement.GetProperty("mode").GetString());
        }

        [TestMethod]
        public void Pinned_mods_reject_an_unknown_id()
        {
            var store = Store();
            var pinned = new List<(string Id, double Magnitude)> { ("savage", 25), ("not_a_modifier", 1) };

            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() =>
                TrialGemRoller.Roll(Gem(400, pinned), 1, Fragment(375), 0, store.Attunement, store.Modifiers, FragmentPressStation.ComposeLimits(4, 0.5)));
            StringAssert.Contains(ex.Message, "not_a_modifier");
        }

        [TestMethod]
        public void Rolling_a_step_gem_at_rep_2_uses_the_next_seed()
        {
            var store = Store();
            var limits = FragmentPressStation.ComposeLimits(4, 0.5);
            var gem = Gem(seed: 1234, mode: TrialSeedMode.Step);

            var rep1 = TrialGemRoller.Roll(gem, 1, Fragment(375), 0, store.Attunement, store.Modifiers, limits);
            var rep2 = TrialGemRoller.Roll(gem, 2, Fragment(375), 0, store.Attunement, store.Modifiers, limits);

            Assert.AreEqual(1234, rep1.RepSeed);
            Assert.AreEqual(1234, rep1.Spec.Seed);
            Assert.AreEqual(1235, rep2.RepSeed);
            Assert.AreEqual(1235, rep2.Spec.Seed);
        }

        [TestMethod]
        public void Step_seeds_walk_and_fixed_seeds_repeat()
        {
            var fixedGem = Gem(seed: 1234, mode: TrialSeedMode.Fixed);
            var stepGem = Gem(seed: 1234, mode: TrialSeedMode.Step);

            Assert.AreEqual(1234, TrialGemRoller.RepSeed(fixedGem, 1));
            Assert.AreEqual(1234, TrialGemRoller.RepSeed(fixedGem, 9));
            Assert.AreEqual(1234, TrialGemRoller.RepSeed(stepGem, 1));
            Assert.AreEqual(1235, TrialGemRoller.RepSeed(stepGem, 2));
            Assert.AreEqual(1243, TrialGemRoller.RepSeed(stepGem, 10));

            var top = Gem(seed: int.MaxValue - 1, mode: TrialSeedMode.Step);
            Assert.AreEqual(int.MaxValue - 1, TrialGemRoller.RepSeed(top, 1));
            Assert.AreEqual(1, TrialGemRoller.RepSeed(top, 2), "wraps into 1..int.MaxValue-1");
            Assert.AreEqual(2, TrialGemRoller.RepSeed(top, 3));
        }
    }
}
