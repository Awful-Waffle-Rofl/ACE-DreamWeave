using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Raw Fragment attunement core under PRESS v2 (owner ruling, 2026-09-07): the nine-slot board, the
    /// band primitive, the per-category budgets and the op table.
    ///
    /// WHAT CHANGED, AND WHY THE OLD SEEDED EXPECTATIONS ARE GONE. v1 replayed the loaded doses and nothing
    /// else, so a press of one Red Taper produced exactly one modifier at that modifier's MINIMUM. v2 resolves
    /// nine slots every time and rolls a BANDED magnitude for each, so:
    ///   - a press of one Red Taper on the shipped file produces a full gem, not a one-modifier one;
    ///   - the magnitude is a roll inside the component's band, not a fixed minimum or a fixed raise step;
    ///   - the rng stream has a different shape again (an empty-slot component draw, then the op draw, then
    ///     the op's own draws including one NextDouble per banded roll).
    /// A given seed therefore reproduces neither v1's nor the pre-instability-removal build's result. Every
    /// expectation below was re-derived against the draw order documented on RawFragmentRules; the ONE
    /// exact-string assertion (Determinism_the_same_seed_and_load_produce_the_same_gem) exists to pin that
    /// order and is characterised, with the semantic assertions that carry the meaning sitting above it.
    ///
    /// MOST TESTS USE A SINGLE-TYPE ROSTER. A roster carrying only tapers resolves only the four taper slots
    /// (ResolveSlots skips a slot type with no components), which is what lets a test about tapers assert on a
    /// result the other five slots did not also write into. The shipped-file tests at the bottom are the ones
    /// that exercise the whole board.
    /// </summary>
    [TestClass]
    public class RawFragmentRulesTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";

        /// <summary>
        /// The shipped modifier set, trimmed to what these tests reach: one row per budget and per new v2
        /// effect kind. Values are verbatim from Content/dungeons/dynamic/modifiers.json.
        /// </summary>
        private const string ModifiersJson =
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"display\":\"Hardy\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\"}," +
            "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\"}," +
            "{\"id\":\"precise\",\"display\":\"Precise\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":30,\"monsterEffectKind\":\"crit_rating\"}," +
            "{\"id\":\"stalwart\",\"display\":\"Stalwart\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_resist_rating\"}," +
            "{\"id\":\"swift\",\"display\":\"Swift\",\"rarity\":\"rare\",\"target\":\"monster\",\"minMagnitude\":1.15,\"maxMagnitude\":1.35,\"monsterEffectKind\":\"run_speed_mult\"}," +
            "{\"id\":\"boss_enraged\",\"display\":\"Enraged\",\"rarity\":\"common\",\"target\":\"boss\",\"minMagnitude\":20,\"maxMagnitude\":60,\"monsterEffectKind\":\"damage_rating\"}," +
            "{\"id\":\"boss_guarded\",\"display\":\"Guarded\",\"rarity\":\"common\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}," +
            "{\"id\":\"hollow\",\"display\":\"Hollow\",\"rarity\":\"rare\",\"target\":\"monster\",\"minMagnitude\":1.0,\"maxMagnitude\":1.0,\"monsterEffectKind\":\"hollow\"}," +
            "{\"id\":\"shield_hollow\",\"display\":\"Shieldbreaker\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":0.25,\"maxMagnitude\":0.6,\"monsterEffectKind\":\"ignore_shield\"}," +
            "{\"id\":\"enlightened\",\"display\":\"Enlightened\",\"rarity\":\"rare\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.5,\"monsterEffectKind\":\"none\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":0.0,\"rewardXpSlope\":1.0}," +
            "{\"id\":\"radiant\",\"display\":\"Radiant\",\"rarity\":\"rare\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"none\",\"rewardLumBase\":0.0,\"rewardLumSlope\":1.0}," +
            "{\"id\":\"bounteous\",\"display\":\"Bounteous\",\"rarity\":\"uncommon\",\"target\":\"run\",\"minMagnitude\":1.15,\"maxMagnitude\":1.6,\"monsterEffectKind\":\"loot_quantity\"}," +
            "{\"id\":\"affinity_iron\",\"display\":\"Iron Affinity\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":61,\"salvageBaseWcid\":243}," +
            "{\"id\":\"affinity_obsidian\",\"display\":\"Obsidian Affinity\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":69,\"salvageBaseWcid\":243}" +
            "],\"xpLadder\":[]}";

        // ---- fixture builders ------------------------------------------------------------------------

        private static string Op(string op, string modifier = null, string scope = null, int amount = 0, int entries = 0,
            double bandMin = 0.0, double bandMax = 1.0, int weight = 100)
        {
            var sb = new StringBuilder("{\"op\":\"").Append(op).Append('"');
            if (modifier != null) sb.Append(",\"modifier\":\"").Append(modifier).Append('"');
            if (scope != null) sb.Append(",\"scope\":\"").Append(scope).Append('"');
            sb.Append(",\"amount\":").Append(amount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"entries\":").Append(entries.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"bandMin\":").Append(bandMin.ToString("0.####", CultureInfo.InvariantCulture));
            sb.Append(",\"bandMax\":").Append(bandMax.ToString("0.####", CultureInfo.InvariantCulture));
            sb.Append(",\"weight\":").Append(weight.ToString(CultureInfo.InvariantCulture));
            return sb.Append('}').ToString();
        }

        /// <summary>One component. The limit is taken from the press board, so no fixture can trip lint rule 22.</summary>
        private static string Comp(uint wcid, string name, string type, params string[] ops)
            => "{\"wcid\":" + wcid.ToString(CultureInfo.InvariantCulture) + ",\"name\":\"" + name + "\",\"type\":\"" + type
               + "\",\"dose\":1,\"limit\":" + RawFragmentRules.SlotCounts[type].ToString(CultureInfo.InvariantCulture)
               + ",\"ops\":[" + string.Join(",", ops) + "]}";

        private static string Roster(params string[] components)
            => "{\"version\":1,\"colors\":[],\"components\":[" + string.Join(",", components) + "]}";

        private static ThreadDungeonStore Store(string attunementJson, string modifiersJson = null)
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, modifiersJson ?? ModifiersJson, new Dictionary<string, string>(), attunementJson: attunementJson);

        /// <summary>
        /// The fixture modifier set with a drawWeight spliced onto the named rows and left ABSENT on every
        /// other one, so a draw-weight test is simultaneously the assertion that an absent field means 1.0.
        /// </summary>
        private static string ModifiersWithDrawWeights(params (string Id, double Weight)[] overrides)
        {
            var json = ModifiersJson;

            foreach (var (id, weight) in overrides)
            {
                var anchor = "{\"id\":\"" + id + "\",";
                Assert.IsTrue(json.Contains(anchor, StringComparison.Ordinal), $"the fixture has no modifier '{id}'");
                json = json.Replace(anchor,
                    anchor + "\"drawWeight\":" + weight.ToString("0.####", CultureInfo.InvariantCulture) + ",");
            }

            return json;
        }

        /// <summary>The four shipped taper colours these tests reach, plus the untargeted Turquoise.</summary>
        private static string TaperRoster()
            => Roster(
                Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                Comp(1643, "Blue Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "precise")),
                Comp(1645, "Green Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hardy")),
                Comp(1646, "Grey Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "stalwart")));

        /// <summary>
        /// AIM CHANCE DEFAULTS TO 1.0 HERE, NOT TO THE SHIPPED 0.5, and that is deliberate rather than an
        /// oversight (owner ruling, 2026-09-07 added the 50/50 aim). Nearly every test in this file asserts
        /// what a NAMED component does - a Red Taper produces savage, four Grey Tapers stack into one
        /// stalwart, a fully loaded fragment presses into exactly the nine things that were chosen - and all
        /// of those statements are about the aim LANDING. At the shipped 0.5 they would each be true about
        /// half the time, which is not a weaker test, it is a different one.
        ///
        /// So the aimed path is pinned here at 1.0, and the 50/50 behaviour has its own tests in the aim
        /// section at the bottom of this file, which press at the shipped default and at 0.0 and assert the
        /// properties that are actually true of a miss.
        /// </summary>
        private static PressLimits Limits(int maxMonsterMods = 4, int maxLocks = 2,
            int maxEntries = RawFragmentRules.MaxEntries, int maxLevel = DungeonGemSpec.MaxLevel,
            int maxBonusMods = PressLimits.DefaultMaxBonusMods, int maxSalvageMods = PressLimits.DefaultMaxSalvageMods,
            double aimChance = 1.0)
            => new PressLimits(maxMonsterMods, maxLocks, maxEntries, maxLevel, maxBonusMods, maxSalvageMods, aimChance);

        private static DungeonGemSpec Fragment(int level = 195,
            IEnumerable<(string Id, double Magnitude)> mods = null, IEnumerable<string> locks = null, int presses = 0)
            => new DungeonGemSpec("any", level, 5, "any", 0, mods, 0, 0, presses, locks, null);

        private static DungeonGemSpec LoadDose(DungeonGemSpec spec, ThreadDungeonStore store, uint wcid)
        {
            Assert.IsTrue(store.Attunement.TryGet(wcid, out var component), $"roster has no wcid {wcid}");
            return RawFragmentRules.Load(spec, component);
        }

        private static PressState Press(DungeonGemSpec spec, ThreadDungeonStore store, PressLimits limits, int seed, int entries = 0)
            => RawFragmentRules.Resolve(new PressState(spec, entries), store.Attunement, store.Modifiers, limits, new Random(seed), out _);

        private static PressState Press(DungeonGemSpec spec, ThreadDungeonStore store, PressLimits limits, Random rng, int entries = 0)
            => RawFragmentRules.Resolve(new PressState(spec, entries), store.Attunement, store.Modifiers, limits, rng, out _);

        /// <summary>
        /// A Random that counts NextDouble calls, for the draw-count-honesty pin. EVERY method delegates to a
        /// separate inner Random rather than to base, and consumes one stream, so this is stream-equivalent to
        /// the plain new Random(seed) every other test presses with.
        ///
        /// The reason is NOT that an overridden NextDouble could otherwise be re-entered by integer draws - it
        /// could not. A derived Random routes through a compat implementation whose Next(int) calls the virtual
        /// Sample(), never the virtual NextDouble(), so counting in NextDouble alone is already safe from
        /// integer draws (measured: 0 NextDouble calls after 30 integer draws). An earlier version of this
        /// comment claimed the opposite and was refuted by probe on 2026-09-07.
        ///
        /// What full delegation actually buys is the Sample() override below. Sample must not reach base (the
        /// stream would fork), and it must not call this.NextDouble() (that recurses through the compat impl
        /// until the process dies of a stack overflow - observed, not theorised). Routing it to inner.NextDouble
        /// is the one shape that is neither, and it deliberately does NOT increment the counter, so the number
        /// this reports means "banded rolls" and nothing else.
        /// </summary>
        private sealed class CountingRandom : Random
        {
            private readonly Random inner;

            public CountingRandom(int seed) => inner = new Random(seed);

            public int NextDoubleCalls { get; private set; }

            public override double NextDouble()
            {
                NextDoubleCalls++;
                return inner.NextDouble();
            }

            public override int Next() => inner.Next();

            public override int Next(int maxValue) => inner.Next(maxValue);

            public override int Next(int minValue, int maxValue) => inner.Next(minValue, maxValue);

            public override void NextBytes(byte[] buffer) => inner.NextBytes(buffer);

            protected override double Sample() => inner.NextDouble();
        }

        /// <summary>
        /// A Random that counts EVERY draw, whatever its shape - the counter the draw-weight work needs,
        /// because a weighted pick spends a NextDouble where the uniform pick it replaced spent a Next(int)
        /// and the invariant under test is that the TOTAL is unchanged.
        ///
        /// Built on the same full-delegation shape as <see cref="CountingRandom"/> and for the same reasons
        /// documented there: every method goes to an inner Random, so Sample() never reaches base (which
        /// would fork the stream) and never re-enters this.NextDouble() (which stack-overflows). Because our
        /// own overrides delegate to inner rather than to base, Sample() is not on the path of any of them,
        /// so counting in all four entry points cannot double-count a bounded integer draw.
        /// </summary>
        private sealed class TotalDrawRandom : Random
        {
            private readonly Random inner;

            public TotalDrawRandom(int seed) => inner = new Random(seed);

            public int Draws { get; private set; }

            public override double NextDouble()
            {
                Draws++;
                return inner.NextDouble();
            }

            public override int Next()
            {
                Draws++;
                return inner.Next();
            }

            public override int Next(int maxValue)
            {
                Draws++;
                return inner.Next(maxValue);
            }

            public override int Next(int minValue, int maxValue)
            {
                Draws++;
                return inner.Next(minValue, maxValue);
            }

            public override void NextBytes(byte[] buffer)
            {
                Draws++;
                inner.NextBytes(buffer);
            }

            protected override double Sample() => inner.NextDouble();
        }

        private static double MagnitudeOf(DungeonGemSpec spec, string id)
            => spec.Modifiers.First(m => m.Id == id).Magnitude;

        private static bool Has(DungeonGemSpec spec, string id) => spec.Modifiers.Any(m => m.Id == id);

        private static void AssertClean(ThreadDungeonStore store)
            => Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));

        // ---- load limits -----------------------------------------------------------------------------

        [TestMethod]
        public void Unknown_wcid_refuses_NotAComponent()
        {
            var store = Store(TaperRoster());
            var refusal = RawFragmentRules.CanLoad(Fragment(), store.Attunement, 99999, 10, out var component, out var text);
            Assert.AreEqual(LoadRefusal.NotAComponent, refusal);
            Assert.IsNull(component);
            Assert.AreEqual(RawFragmentRules.RefuseNotAComponent, text);
        }

        /// <summary>
        /// The check ORDER is binding: an unmapped item gets the informative "does not take that" rather than
        /// one of the later, more specific refusals. NotAComponent, then NotEnough, then OverLimit.
        /// </summary>
        [TestMethod]
        public void Check_order_puts_NotAComponent_before_the_more_specific_refusals()
        {
            var store = Store(Roster(
                Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                "{\"wcid\":1643,\"name\":\"Blue Taper\",\"type\":\"taper\",\"dose\":10,\"limit\":4,\"ops\":["
                    + Op(AttunementOps.AddOrRaise, modifier: "precise") + "]}"));

            // An unmapped wcid on a fragment that would ALSO fail the stack-size check reads as NotAComponent.
            Assert.AreEqual(LoadRefusal.NotAComponent, RawFragmentRules.CanLoad(Fragment(), store.Attunement, 765, 1, out _, out var text));
            Assert.AreEqual(RawFragmentRules.RefuseNotAComponent, text);

            // NotEnough beats OverLimit: the Blue Taper takes 10 at a time, and a fragment already holding
            // four tapers offered a short stack reports the stack problem the player can actually fix.
            var full = Fragment().WithLoad(new (uint, int)[] { (1650, 4) });
            Assert.AreEqual(LoadRefusal.NotEnough, RawFragmentRules.CanLoad(full, store.Attunement, 1643, 9, out _, out _));
            Assert.AreEqual(LoadRefusal.OverLimit, RawFragmentRules.CanLoad(full, store.Attunement, 1643, 10, out _, out _));
        }

        [TestMethod]
        public void A_fifth_taper_refuses_OverLimit()
        {
            var store = Store(TaperRoster());
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 1643, 1645, 1646 })
                spec = LoadDose(spec, store, wcid);

            Assert.AreEqual(4, RawFragmentRules.DosesOfType(spec, store.Attunement, "taper"), "four taper slots, four doses");
            Assert.AreEqual(LoadRefusal.OverLimit, RawFragmentRules.CanLoad(spec, store.Attunement, 1650, 1, out _, out var text));
            Assert.AreEqual(RawFragmentRules.RefuseOverLimit, text);
        }

        [TestMethod]
        public void A_second_scarab_refuses_OverLimit_but_a_taper_still_loads()
        {
            var store = Store(Roster(
                Comp(687, "Gold Scarab", "scarab", Op(AttunementOps.SetDifficulty, amount: 10, entries: 4)),
                Comp(689, "Iron Scarab", "scarab", Op(AttunementOps.SetDifficulty, entries: 3)),
                Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage"))));

            var spec = LoadDose(Fragment(), store, 687);
            Assert.AreEqual(LoadRefusal.OverLimit, RawFragmentRules.CanLoad(spec, store.Attunement, 689, 1, out _, out _));

            // The limit is per TYPE, not per wcid.
            Assert.AreEqual(LoadRefusal.None, RawFragmentRules.CanLoad(spec, store.Attunement, 1650, 1, out _, out _));
        }

        /// <summary>
        /// AT MOST ONE POWDER, on the SHIPPED file rather than on a fixture. ThreadDungeonGemHandler's
        /// "Possible effects:" block relies on this to carry a single named-affinity entry instead of a list
        /// (owner ruling, 2026-09-07), so the refusal is pinned here rather than assumed: if it ever stops
        /// holding, that block needs a list and this test is what says so.
        ///
        /// The mechanism is that CanLoad compares DosesOfType - a count over the whole TYPE - against the
        /// component's own limit, and lint rule 22 pins every powder's limit to the powder slot count of 1.
        /// So a second powder of a DIFFERENT wcid is refused exactly as a repeat of the same one is, which is
        /// the case a per-wcid reading of the rule would let through.
        /// </summary>
        [TestMethod]
        public void A_second_powder_of_a_different_wcid_is_refused()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            Assert.AreEqual(1, RawFragmentRules.SlotCounts["powder"], "guard: the board has one powder slot");

            var spec = LoadDose(Fragment(), store, 626);
            Assert.AreEqual(1, RawFragmentRules.DosesOfType(spec, store.Attunement, "powder"));

            Assert.AreEqual(LoadRefusal.OverLimit,
                RawFragmentRules.CanLoad(spec, store.Attunement, 782, 1, out _, out var text),
                "a second powder of a different wcid must be refused");
            Assert.AreEqual(RawFragmentRules.RefuseOverLimit, text);

            Assert.AreEqual(LoadRefusal.OverLimit,
                RawFragmentRules.CanLoad(spec, store.Attunement, 626, 1, out _, out _),
                "and so must a repeat of the same one");

            // Control: the refusal is about the powder slot, not about the fragment being full.
            Assert.AreEqual(LoadRefusal.None, RawFragmentRules.CanLoad(spec, store.Attunement, 1650, 1, out _, out _));
        }

        // ---- the slot board --------------------------------------------------------------------------

        /// <summary>
        /// THE CORE OF THE RULING: every slot resolves, always. A bare fragment pressed against the shipped
        /// file comes back as a complete gem rather than an empty one, and every one of its nine components
        /// is marked as drawn rather than chosen.
        /// </summary>
        [TestMethod]
        public void An_empty_load_fills_all_nine_slots_from_random_draws()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            RawFragmentRules.Resolve(new PressState(Fragment(), 0), store.Attunement, store.Modifiers,
                Limits(), new Random(1), out var log);

            Assert.AreEqual(9, log.Count);
            CollectionAssert.AreEqual(RawFragmentRules.SlotOrder.ToArray(), log.Select(d => d.Slot).ToArray());
            Assert.IsTrue(log.All(d => d.Drawn), "an empty load fills every slot by drawing");
        }

        /// <summary>
        /// A filled slot resolves THAT component and the rest still draw. Load order decides which taper slot
        /// each loaded taper takes, so Red, Red, Blue occupy slots 1, 2 and 3 and slot 4 draws.
        /// </summary>
        [TestMethod]
        public void Loaded_doses_fill_their_own_slots_in_load_order_and_the_rest_draw()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 1650, 1643, 687 })
                spec = LoadDose(spec, store, wcid);

            var slots = RawFragmentRules.ResolveSlots(spec.Load, store.Attunement, store.Modifiers, new Random(7));

            Assert.AreEqual(9, slots.Count);
            CollectionAssert.AreEqual(RawFragmentRules.SlotOrder.ToArray(), slots.Select(s => s.Type).ToArray());

            var tapers = slots.Where(s => s.Type == RawFragmentRules.TaperType).ToList();
            CollectionAssert.AreEqual(new[] { "Red Taper", "Red Taper", "Blue Taper" },
                tapers.Take(3).Select(s => s.Component.Name).ToArray());
            Assert.IsTrue(tapers.Take(3).All(s => s.Loaded));
            Assert.IsFalse(tapers[3].Loaded, "the fourth taper slot was empty, so it drew");

            var scarab = slots.Single(s => s.Type == RawFragmentRules.ScarabType);
            Assert.AreEqual("Gold Scarab", scarab.Component.Name);
            Assert.IsTrue(scarab.Loaded);

            foreach (var type in new[] { RawFragmentRules.HerbType, RawFragmentRules.PowderType, RawFragmentRules.PotionType, RawFragmentRules.TalismanType })
                Assert.IsFalse(slots.Single(s => s.Type == type).Loaded, $"{type} was not loaded, so it drew");
        }

        /// <summary>
        /// A slot type with no components at all is SKIPPED rather than throwing or drawing from another
        /// type's pool. This is what makes the single-type rosters the rest of this file uses meaningful.
        /// </summary>
        [TestMethod]
        public void A_slot_type_with_no_components_is_skipped()
        {
            var store = Store(TaperRoster());
            var slots = RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, store.Modifiers, new Random(3));

            Assert.AreEqual(4, slots.Count);
            Assert.IsTrue(slots.All(s => s.Type == RawFragmentRules.TaperType));
        }

        /// <summary>
        /// The empty-slot draw is over the type's components ordered by WCID, never dictionary enumeration
        /// order. Pinned by drawing many times and checking that every component of the type - and nothing
        /// else - can come out, which is the observable half; the ordering itself is what makes the seed
        /// reproducible and is covered by the determinism test below.
        /// </summary>
        [TestMethod]
        public void The_empty_slot_draw_only_ever_returns_a_component_of_that_slots_type()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var seen = new HashSet<string>();

            for (var seed = 0; seed < 200; seed++)
                foreach (var slot in RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, store.Modifiers, new Random(seed)))
                {
                    Assert.AreEqual(slot.Type, slot.Component.Type, $"seed {seed}: a slot drew a component of the wrong type");
                    seen.Add(slot.Component.Type);
                }

            CollectionAssert.AreEquivalent(RawFragmentRules.SlotCounts.Keys.ToArray(), seen.ToArray());
        }

        // ---- draw weights (owner ruling, 2026-09-08) -------------------------------------------------
        //
        // "Rarity weights everywhere, but make Hollow the only reduced one for now. We will scale the rest
        // based on data once the feature is used." A modifier row carries a drawWeight (default 1.0) that
        // scales how often a RANDOM draw lands on it, and never touches what a player deliberately loaded.
        //
        // Every test here splices the weight onto the fixture rather than reading the shipped file, so the
        // shipped tuning value can change without turning these into failures - the shipped 0.1 on hollow is
        // asserted once, in ThreadDungeonStoreTests, where the file itself is the subject.

        /// <summary>
        /// THE DERIVATION, pinned directly rather than inferred from a draw. A component's weight is the
        /// MINIMUM over the modifiers its ops NAME, and 1.0 when its ops name none.
        ///
        /// Minimum rather than product is the design call: a component that can produce hollow should be
        /// exactly as rare as hollow, and a two-op component naming two reduced modifiers must not compound
        /// into something effectively unreachable.
        /// </summary>
        [TestMethod]
        public void Component_draw_weight_is_the_minimum_over_the_modifiers_its_ops_name()
        {
            var store = Store(
                Roster(
                    Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                    Comp(1652, "White Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hollow")),
                    Comp(1654, "Turquoise Taper", "taper", Op(AttunementOps.AddRandom, scope: AttunementScopes.Difficulty)),
                    Comp(1643, "Two Op Taper", "taper",
                        Op(AttunementOps.AddOrRaise, modifier: "savage", weight: 50),
                        Op(AttunementOps.AddOrRaise, modifier: "hollow", weight: 50))),
                ModifiersWithDrawWeights(("hollow", 0.1)));

            AssertClean(store);

            double WeightOf(uint wcid)
            {
                Assert.IsTrue(store.Attunement.TryGet(wcid, out var component), $"roster has no wcid {wcid}");
                return RawFragmentRules.ComponentDrawWeight(component, store.Modifiers);
            }

            Assert.AreEqual(0.1, WeightOf(1652), 0.0001, "a component naming hollow is exactly as rare as hollow");
            Assert.AreEqual(1.0, WeightOf(1650), 0.0001, "a component naming a default-weight modifier is unreduced");
            Assert.AreEqual(1.0, WeightOf(1654), 0.0001, "a component whose op names NO modifier is unreduced");
            Assert.AreEqual(0.1, WeightOf(1643), 0.0001, "two named modifiers take the minimum, never the product (0.1, not 0.1 * 1.0 read as anything else)");
        }

        /// <summary>
        /// The empty-slot draw honours the weight. Asserted as a generous DIRECTION rather than an exact
        /// count, so it pins the behaviour without becoming a test of one .NET version's rng: at 0.1 against
        /// 1.0 the expected split over 4000 draws is roughly 364 to 3636, and the bound asserted is five
        /// times looser than that.
        /// </summary>
        [TestMethod]
        public void A_reduced_draw_weight_makes_its_component_far_rarer_in_the_empty_slot_draw()
        {
            var store = Store(
                Roster(
                    Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                    Comp(1652, "White Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hollow"))),
                ModifiersWithDrawWeights(("hollow", 0.1)));

            AssertClean(store);

            var red = 0;
            var white = 0;

            for (var seed = 0; seed < 1000; seed++)
                foreach (var slot in RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, store.Modifiers, new Random(seed)))
                    if (slot.Component.Wcid == 1650) red++;
                    else white++;

            Assert.AreEqual(4000, red + white, "1000 seeds times four taper slots");
            Assert.IsTrue(white > 0, "0.1 means rare, not impossible");
            Assert.IsTrue(white * 5 < red, $"the reduced component's share must be under a fifth of the unreduced one's: {white} white vs {red} red");
        }

        /// <summary>
        /// A weight of 0.0 removes a modifier from the random pools ENTIRELY while leaving it fully reachable
        /// by loading the component that names it. This is the shape the owner may want next for hollow, so
        /// it is pinned now rather than discovered later.
        /// </summary>
        [TestMethod]
        public void A_zero_draw_weight_is_never_drawn_but_is_still_reached_by_loading_its_component()
        {
            var store = Store(
                Roster(
                    Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                    Comp(1652, "White Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hollow"))),
                ModifiersWithDrawWeights(("hollow", 0.0)));

            AssertClean(store);

            for (var seed = 0; seed < 500; seed++)
                foreach (var slot in RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, store.Modifiers, new Random(seed)))
                    Assert.AreEqual(1650u, slot.Component.Wcid, $"seed {seed}: a zero-weight component is never drawn at random");

            var loaded = Fragment();
            for (var i = 0; i < RawFragmentRules.SlotCounts[RawFragmentRules.TaperType]; i++)
                loaded = LoadDose(loaded, store, 1652);

            for (var seed = 0; seed < 50; seed++)
                Assert.IsTrue(Has(Press(loaded, store, Limits(), seed).Spec, "hollow"),
                    $"seed {seed}: loading the White Taper still lands hollow - the weight is about DRAWS, not about the modifier");
        }

        /// <summary>The same for the modifier pools: add_random cannot reach a zero-weight row either.</summary>
        [TestMethod]
        public void A_zero_draw_weight_is_never_reached_by_add_random()
        {
            var store = Store(
                Roster(Comp(1654, "Turquoise Taper", "taper", Op(AttunementOps.AddRandom, scope: AttunementScopes.Difficulty))),
                ModifiersWithDrawWeights(("hollow", 0.0)));

            AssertClean(store);

            var reached = false;

            for (var seed = 0; seed < 300; seed++)
            {
                var result = Press(Fragment(), store, Limits(), seed);
                Assert.IsFalse(Has(result.Spec, "hollow"), $"seed {seed}: add_random must not reach a zero-weight modifier");
                reached |= Has(result.Spec, "savage") || Has(result.Spec, "precise");
            }

            Assert.IsTrue(reached, "control: add_random still reaches the unreduced rows, so the assertion above is not vacuous");
        }

        /// <summary>
        /// A DEGENERATE pool - every candidate at 0.0 - falls back to a uniform draw rather than leaving the
        /// slot unfilled. A tuning value must never be able to punch a hole in the board.
        /// </summary>
        [TestMethod]
        public void An_all_zero_pool_still_fills_the_slot_uniformly()
        {
            var store = Store(
                Roster(
                    Comp(1651, "Violet Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "shield_hollow")),
                    Comp(1652, "White Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hollow"))),
                ModifiersWithDrawWeights(("hollow", 0.0), ("shield_hollow", 0.0)));

            AssertClean(store);

            var seen = new HashSet<uint>();

            for (var seed = 0; seed < 200; seed++)
            {
                var slots = RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, store.Modifiers, new Random(seed));

                Assert.AreEqual(4, slots.Count, $"seed {seed}: every taper slot still resolves");

                foreach (var slot in slots)
                {
                    Assert.IsNotNull(slot.Component, $"seed {seed}: a slot came back empty");
                    seen.Add(slot.Component.Wcid);
                }
            }

            CollectionAssert.AreEquivalent(new uint[] { 1651, 1652 }, seen.ToArray(),
                "the degenerate fallback is uniform over the WHOLE pool, not a fixed pick");
        }

        /// <summary>
        /// THE DRAW-ORDER INVARIANT. A weighted pick spends exactly ONE Random call, the same as the uniform
        /// pick it replaced, so the number and position of every later draw in the stream is unchanged. Only
        /// the VALUES move, because NextDouble is a different draw from Next(n).
        ///
        /// Measured on the empty-slot draw specifically, because that is the one the class header names, and
        /// with a roster of ONE component so the pool is degenerate-free but the pick is still real.
        /// </summary>
        [TestMethod]
        public void The_empty_slot_draw_still_costs_exactly_one_random_call_per_slot()
        {
            var store = Store(
                Roster(
                    Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                    Comp(1652, "White Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hollow"))),
                ModifiersWithDrawWeights(("hollow", 0.1)));

            AssertClean(store);

            var rng = new TotalDrawRandom(11);
            var slots = RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, store.Modifiers, rng);

            Assert.AreEqual(4, slots.Count, "four taper slots, all empty");
            Assert.AreEqual(4, rng.Draws, "one draw per drawn slot, exactly as the uniform pick cost");

            // The complement: a LOADED slot takes no draw at all, so the count is about draws and not about slots.
            var loaded = LoadDose(Fragment(), store, 1650);
            var loadedRng = new TotalDrawRandom(11);
            RawFragmentRules.ResolveSlots(loaded.Load, store.Attunement, store.Modifiers, loadedRng);

            Assert.AreEqual(3, loadedRng.Draws, "one taper slot was filled by hand, so only three drew");
        }

        /// <summary>
        /// A null modifier dictionary makes every weight 1.0 - the uniform draw. Pinned because it is the
        /// contract ResolveSlots documents for a caller that has no modifier table to hand.
        /// </summary>
        [TestMethod]
        public void A_null_modifier_table_draws_uniformly()
        {
            var store = Store(
                Roster(
                    Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                    Comp(1652, "White Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hollow"))),
                ModifiersWithDrawWeights(("hollow", 0.0)));

            AssertClean(store);

            var seen = new HashSet<uint>();

            for (var seed = 0; seed < 200; seed++)
                foreach (var slot in RawFragmentRules.ResolveSlots(new List<(uint, int)>(), store.Attunement, null, new Random(seed)))
                    seen.Add(slot.Component.Wcid);

            CollectionAssert.AreEquivalent(new uint[] { 1650, 1652 }, seen.ToArray(),
                "with no modifier table the zero weight is not visible, so both components are drawn");
        }

        // ---- bands -----------------------------------------------------------------------------------

        /// <summary>
        /// THE BAND PRIMITIVE. A band is a FRACTION of the modifier's own declared range, so a 0.00-0.25 band
        /// on savage (10..40) can only ever land in [10, 17.5] and a 0.80-1.00 band only in [34, 40]. Run over
        /// 300 seeds so the assertion is about the formula rather than about one lucky roll, and asserted as
        /// the closed interval the formula defines rather than as a sampled minimum and maximum.
        /// </summary>
        [TestMethod]
        public void A_band_confines_the_roll_to_its_fraction_of_the_modifiers_range()
        {
            var low = Store(Roster(Comp(1650, "Low Taper", "taper",
                Op(AttunementOps.AddOrRaise, modifier: "savage", bandMin: 0.00, bandMax: 0.25))));
            var high = Store(Roster(Comp(1650, "High Taper", "taper",
                Op(AttunementOps.AddOrRaise, modifier: "savage", bandMin: 0.80, bandMax: 1.00))));

            AssertClean(low);
            AssertClean(high);

            var lowSeen = new List<double>();
            var highSeen = new List<double>();

            for (var seed = 0; seed < 300; seed++)
            {
                // Only the FIRST taper slot is loaded; the other three draw the same component, so the value
                // read back is the stacked total. The first slot alone is what the band bounds, so the check
                // is made against a one-slot roster instead: see the single-slot press below.
                var lowResult = RawFragmentRules.Resolve(new PressState(LoadDose(Fragment(), low, 1650), 0),
                    low.Attunement, low.Modifiers, Limits(), new Random(seed), out var lowLog);
                var highResult = RawFragmentRules.Resolve(new PressState(LoadDose(Fragment(), high, 1650), 0),
                    high.Attunement, high.Modifiers, Limits(), new Random(seed), out var highLog);

                Assert.AreEqual(4, lowLog.Count, "four taper slots, all filled by the one component in the roster");
                Assert.AreEqual(4, highLog.Count);

                // Four doses of the same band stack, so the total is bounded by four times the band's own
                // contribution above the floor - which is still strictly ordered between the two bands.
                lowSeen.Add(MagnitudeOf(lowResult.Spec, "savage"));
                highSeen.Add(MagnitudeOf(highResult.Spec, "savage"));
            }

            // savage floor 10, span 30. Four low doses: 10 + 4 * [0, 7.5] = [10, 40]; four high doses:
            // 10 + 4 * [24, 30] = clamped to 40 every time. The discriminating claim is that the LOW band
            // does not reliably reach the ceiling while the HIGH band always does.
            Assert.IsTrue(highSeen.All(m => Math.Abs(m - 40.0) < 0.001), "a 0.80-1.00 band stacked four times always maxes savage");
            Assert.IsTrue(lowSeen.Any(m => m < 39.9), "a 0.00-0.25 band must not reliably max savage");
            Assert.IsTrue(lowSeen.All(m => m >= 10.0 && m <= 40.0), "every roll stays inside the modifier's declared range");
        }

        /// <summary>
        /// The band bound on ONE roll, isolated: a roster with a single taper component and a load of one
        /// dose still fills all four taper slots, so the isolation is done by pressing against a limit of one
        /// monster modifier and reading the FIRST dose's After render instead.
        /// </summary>
        [TestMethod]
        public void A_single_banded_roll_lands_inside_its_band()
        {
            var store = Store(Roster(Comp(1650, "Low Taper", "taper",
                Op(AttunementOps.AddOrRaise, modifier: "savage", bandMin: 0.00, bandMax: 0.25))));

            for (var seed = 0; seed < 300; seed++)
            {
                RawFragmentRules.Resolve(new PressState(Fragment(), 0), store.Attunement, store.Modifiers,
                    Limits(), new Random(seed), out var log);

                // "mods=savage:<n>" after the first slot. savage floor 10, span 30, band 0..0.25 -> [10, 17.5].
                var first = log[0].After;
                var value = double.Parse(first.Substring("mods=savage:".Length), CultureInfo.InvariantCulture);

                Assert.IsTrue(value >= 10.0 && value <= 17.5, $"seed {seed}: {value} is outside the 0.00-0.25 band of savage");
            }
        }

        /// <summary>
        /// A band whose edges are not fractions is a content bug that would otherwise clamp to the modifier's
        /// maximum and silently make the component the strongest in the file. Lint rule 19 drops the ENTRY.
        /// </summary>
        [TestMethod]
        public void An_out_of_range_band_drops_the_component_with_a_diagnostic()
        {
            var store = Store(Roster(Comp(1650, "Broken Taper", "taper",
                Op(AttunementOps.AddOrRaise, modifier: "savage", bandMin: 10, bandMax: 40))));

            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("band")), string.Join("\n", store.Diagnostics));
        }

        // ---- stacking --------------------------------------------------------------------------------

        /// <summary>
        /// Same-modifier tapers STACK magnitude rather than adding an entry (owner ruling: "four Red Tapers
        /// gives one extremely savage dungeon"). One entry, at or near the ceiling, on every seed.
        /// </summary>
        [TestMethod]
        public void Four_tapers_of_one_colour_stack_into_one_modifier()
        {
            var store = Store(TaperRoster());

            for (var seed = 0; seed < 100; seed++)
            {
                var spec = Fragment();
                for (var i = 0; i < 4; i++)
                    spec = LoadDose(spec, store, 1650);

                var result = Press(spec, store, Limits(), seed);

                Assert.AreEqual(1, result.Spec.Modifiers.Count, $"seed {seed}: four red tapers are one modifier, not four");
                Assert.IsTrue(Has(result.Spec, "savage"), $"seed {seed}");
                Assert.IsTrue(MagnitudeOf(result.Spec, "savage") >= 10.0, $"seed {seed}");
                Assert.IsTrue(MagnitudeOf(result.Spec, "savage") <= 40.0, $"seed {seed}: the modifier's own ceiling still binds");
            }
        }

        [TestMethod]
        public void Four_tapers_of_different_colours_give_four_modifiers()
        {
            var store = Store(TaperRoster());
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 1643, 1645, 1646 })
                spec = LoadDose(spec, store, wcid);

            var result = Press(spec, store, Limits(), 11);

            CollectionAssert.AreEquivalent(new[] { "savage", "precise", "hardy", "stalwart" },
                result.Spec.Modifiers.Select(m => m.Id).ToArray());
        }

        // ---- per-category budgets --------------------------------------------------------------------

        /// <summary>
        /// The budgets are PER CATEGORY, which is the whole reason the flat MaxMods 4 had to go: four tapers
        /// plus a herb plus a powder name six things and all six must survive.
        /// </summary>
        [TestMethod]
        public void Four_tapers_a_herb_and_a_powder_all_land_together()
        {
            var store = Store(Roster(
                Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage")),
                Comp(1643, "Blue Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "precise")),
                Comp(1645, "Green Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hardy")),
                Comp(1646, "Grey Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "stalwart")),
                Comp(625, "Ginseng", "herb", Op(AttunementOps.AddOrRaise, modifier: "enlightened")),
                Comp(626, "Powdered Hematite", "powder", Op(AttunementOps.AddOrRaise, modifier: "affinity_iron"))));

            AssertClean(store);

            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 1643, 1645, 1646, 625, 626 })
                spec = LoadDose(spec, store, wcid);

            var result = Press(spec, store, Limits(), 21);

            CollectionAssert.AreEquivalent(
                new[] { "savage", "precise", "hardy", "stalwart", "enlightened", "affinity_iron" },
                result.Spec.Modifiers.Select(m => m.Id).ToArray());
        }

        /// <summary>
        /// The monster/boss budget still binds at 4. With five distinct taper colours loaded past the slot
        /// count (the load limit is enforced by CanLoad, the budget by Resolve) only four modifiers land, and
        /// the fifth dose is spent sharpening rather than wasted.
        /// </summary>
        [TestMethod]
        public void The_monster_budget_caps_at_four_and_the_overflow_sharpens()
        {
            var store = Store(Roster(
                Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage", bandMin: 0.0, bandMax: 0.0)),
                Comp(1643, "Blue Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "precise", bandMin: 0.0, bandMax: 0.0)),
                Comp(1645, "Green Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "hardy", bandMin: 0.0, bandMax: 0.0)),
                Comp(1646, "Grey Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "stalwart", bandMin: 0.0, bandMax: 0.0)),
                Comp(1653, "Yellow Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "swift", bandMin: 1.0, bandMax: 1.0))));

            // Five doses, past the four taper slots: the fifth is simply never resolved by the board.
            var spec = Fragment().WithLoad(new (uint, int)[] { (1650, 1), (1643, 1), (1645, 1), (1646, 1), (1653, 1) });
            var result = Press(spec, store, Limits(maxMonsterMods: 4), 23);

            Assert.AreEqual(4, result.Spec.Modifiers.Count);
            Assert.IsFalse(Has(result.Spec, "swift"), "the fifth dose has no slot to resolve in");

            // Now force the overflow through a slot: a board of four Yellow Tapers on a gem that already
            // carries four other monster modifiers must sharpen rather than drop the dose.
            var startMods = new[] { ("savage", 10.0), ("precise", 10.0), ("hardy", 1.3), ("stalwart", 10.0) };
            var full = Fragment(mods: startMods).WithLoad(new (uint, int)[] { (1653, 4) });
            var overflow = Press(full, store, Limits(maxMonsterMods: 4), 24);

            Assert.AreEqual(4, overflow.Spec.Modifiers.Count);
            Assert.IsFalse(Has(overflow.Spec, "swift"));
            Assert.IsTrue(overflow.Spec.Modifiers.Sum(m => m.Magnitude) > startMods.Sum(m => m.Item2),
                "an over-budget dose sharpens something in the same budget instead of being wasted");
        }

        /// <summary>
        /// The wildcard powder (owner ruling, 2026-09-07): add_random at the salvage scope draws an affinity
        /// and only an affinity. Asserted over the whole fixture roster rather than against a named id, and
        /// over every seed in a range, because the point of the scope is that the POOL is derived - a
        /// hard-coded material list would pass a single-seed test and then miss the next row added.
        /// </summary>
        [TestMethod]
        public void A_wildcard_powder_draws_only_salvage_affinities()
        {
            var store = Store(Roster(
                Comp(782, "Powdered Agate", "powder", Op(AttunementOps.AddRandom, scope: AttunementScopes.Salvage))));

            AssertClean(store);

            var drawn = new HashSet<string>(StringComparer.Ordinal);

            for (var seed = 1; seed <= 40; seed++)
            {
                var result = Press(Fragment().WithLoad(new (uint, int)[] { (782, 1) }), store, Limits(), seed);

                Assert.AreEqual(1, result.Spec.Modifiers.Count, $"seed {seed} must land exactly one modifier");

                var id = result.Spec.Modifiers[0].Id;
                Assert.IsTrue(store.Modifiers[id].MonsterEffectKind == DungeonRewardMath.SalvageAffinity,
                    $"seed {seed} drew {id}, which is not a salvage affinity");

                drawn.Add(id);
            }

            // Both fixture affinities really are in the pool, so the scope is proven to be a POOL and not a
            // filter that happens to admit one row.
            CollectionAssert.AreEquivalent(new[] { "affinity_iron", "affinity_obsidian" }, drawn.ToArray());
        }

        /// <summary>
        /// The salvage scope is bounded by the salvage BUDGET, not merely by the pool: a gem that already
        /// carries an affinity does not gain a second one from a wildcard powder. Pins that InScope and
        /// LimitFor are reading the same Budget rather than agreeing by coincidence.
        /// </summary>
        [TestMethod]
        public void A_wildcard_powder_respects_the_salvage_budget()
        {
            var store = Store(Roster(
                Comp(782, "Powdered Agate", "powder", Op(AttunementOps.AddRandom, scope: AttunementScopes.Salvage))));

            var spec = Fragment(mods: new[] { ("affinity_obsidian", 10.0) })
                .WithLoad(new (uint, int)[] { (782, 1) });

            var result = Press(spec, store, Limits(), 31);

            Assert.AreEqual(1, result.Spec.Modifiers.Count(m => m.Id.StartsWith("affinity_", StringComparison.Ordinal)),
                "the salvage budget is one");
            Assert.IsFalse(Has(result.Spec, "affinity_iron"), "the second affinity must not land");
        }

        /// <summary>
        /// REGRESSION (review finding 4, 2026-09-07). THE DRAW-COUNT-HONESTY INVARIANT, measured rather than
        /// documented: one banded op costs EXACTLY ONE rng.NextDouble(), on every path through AddOrStack.
        ///
        /// The original AddOrStack rolled up front and then discovered the budget was full, discarded that
        /// roll and let the sharpen path roll again - two NextDouble calls for one op. The review measured 8
        /// where 4 were promised, on exactly this board. That is not a gameplay bug (both rolls are uniform
        /// and the surviving one is unbiased) but it falsifies the contract the class header and RollBanded's
        /// own doc both state, and a contract that is only true sometimes is worse than none: the next reader
        /// to reason about seed reproducibility from the documented draw order would have been wrong.
        ///
        /// FOUR doses on a FULL monster budget is the path that regressed. The control below is the same
        /// board on an EMPTY gem, which never reaches the budget-full branch at all - without it, a fix that
        /// simply stopped rolling somewhere would pass this test while breaking the ordinary path.
        ///
        /// THE EXPECTED COUNT WENT FROM 4 TO 8 WITH THE AIM ROLL (owner ruling, 2026-09-07), and the test is
        /// no weaker for it. add_or_raise names a modifier, so each of the four doses now costs one aim
        /// NextDouble as well as its one banded NextDouble - 4 + 4 - whatever the aim chance is, because the
        /// aim roll is taken unconditionally (see the class header). The regression this pins would still be
        /// visible: a double-rolling AddOrStack would read 4 aim + 8 banded = 12, not 8. Both halves are
        /// asserted against a computed breakdown rather than a bare literal, so the next reader can see which
        /// number is which.
        /// </summary>
        [TestMethod]
        public void A_full_budget_dose_costs_exactly_one_banded_roll()
        {
            var store = Store(Roster(
                Comp(1653, "Yellow Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "swift"))));

            AssertClean(store);

            // The board is four taper slots; four doses fill all four, so no slot draws a random component.
            var load = new (uint, int)[] { (1653, 4) };

            var full = Fragment(mods: new[] { ("savage", 10.0), ("precise", 10.0), ("hardy", 1.3), ("stalwart", 10.0) })
                .WithLoad(load);

            // Four doses, each costing one aim roll plus one banded roll.
            const int doses = 4;
            const int expected = doses + doses;

            var fullRng = new CountingRandom(24);
            var overflow = Press(full, store, Limits(maxMonsterMods: 4), fullRng);

            Assert.AreEqual(4, overflow.Spec.Modifiers.Count, "the budget still binds at four");
            Assert.IsFalse(Has(overflow.Spec, "swift"),
                "the budget-full branch is the one under test; if swift landed, this measured the wrong path");
            Assert.AreEqual(expected, fullRng.NextDoubleCalls,
                "four doses on a full budget must cost four aim rolls and four banded rolls, not eight banded ones");

            var emptyRng = new CountingRandom(24);
            var plain = Press(Fragment().WithLoad(load), store, Limits(maxMonsterMods: 4), emptyRng);

            Assert.IsTrue(Has(plain.Spec, "swift"));
            Assert.AreEqual(expected, emptyRng.NextDoubleCalls,
                "the ordinary add-then-stack path costs one banded roll per dose too");
        }

        /// <summary>
        /// The bonus and salvage budgets are 1 each, and they are SEPARATE budgets - a gem carrying a bonus is
        /// not thereby barred from a salvage affinity, which is the whole point of splitting the flat cap.
        /// </summary>
        [TestMethod]
        public void The_bonus_and_salvage_budgets_are_one_each_and_independent()
        {
            var store = Store(Roster(
                Comp(625, "Ginseng", "herb", Op(AttunementOps.AddOrRaise, modifier: "enlightened")),
                Comp(626, "Powdered Hematite", "powder", Op(AttunementOps.AddOrRaise, modifier: "affinity_iron"))));

            // A gem already carrying a bonus AND an affinity: neither slot may add a second of its own kind.
            var spec = Fragment(mods: new[] { ("radiant", 1.5), ("affinity_obsidian", 10.0) })
                .WithLoad(new (uint, int)[] { (625, 1), (626, 1) });

            var result = Press(spec, store, Limits(), 25);

            Assert.AreEqual(1, result.Spec.Modifiers.Count(m => m.Id == "radiant" || m.Id == "enlightened"), "one bonus");
            Assert.AreEqual(1, result.Spec.Modifiers.Count(m => m.Id.StartsWith("affinity_", StringComparison.Ordinal)), "one affinity");
            Assert.IsFalse(Has(result.Spec, "enlightened"));
            Assert.IsFalse(Has(result.Spec, "affinity_iron"));
        }

        // ---- the scarab slot -------------------------------------------------------------------------

        [TestMethod]
        public void The_scarab_sets_the_level_offset_and_the_entry_count()
        {
            var store = Store(Roster(
                Comp(691, "Lead Scarab", "scarab", Op(AttunementOps.SetDifficulty, amount: -10, entries: 3)),
                Comp(37155, "Mana Scarab", "scarab", Op(AttunementOps.SetDifficulty, amount: 35, entries: 6))));

            AssertClean(store);

            var lead = Press(LoadDose(Fragment(level: 195), store, 691), store, Limits(), 31, entries: 5);
            Assert.AreEqual(185, lead.Spec.Level);
            Assert.AreEqual(3, lead.Entries, "the scarab SETS the entry count, it does not raise it");

            var mana = Press(LoadDose(Fragment(level: 195), store, 37155), store, Limits(), 32, entries: 1);
            Assert.AreEqual(230, mana.Spec.Level);
            Assert.AreEqual(6, mana.Entries);
        }

        [TestMethod]
        public void The_scarab_cannot_push_the_level_outside_its_bounds()
        {
            var store = Store(Roster(
                Comp(37155, "Mana Scarab", "scarab", Op(AttunementOps.SetDifficulty, amount: 35, entries: 6)),
                Comp(691, "Lead Scarab", "scarab", Op(AttunementOps.SetDifficulty, amount: -10, entries: 3))));

            // Expressed against DungeonGemSpec.MaxLevel rather than a literal, so the CLAMP is still the
            // thing under test after a ceiling raise. Written as a literal 270 -> 275 before the 2026-09-09
            // raise to 375, at which point a +35 scarab on a 270 fragment landed at 305 and cleared the
            // ceiling by 70 levels without touching the clamp at all.
            var justUnder = Fragment(level: DungeonGemSpec.MaxLevel - 5);
            Assert.AreEqual(DungeonGemSpec.MaxLevel, Press(LoadDose(justUnder, store, 37155), store, Limits(), 33).Spec.Level,
                "a +35 scarab five levels under the ceiling clamps AT the ceiling");

            Assert.AreEqual(1, Press(LoadDose(Fragment(level: 5), store, 691), store, Limits(), 34).Spec.Level);
        }

        /// <summary>
        /// MaxEntries went from 5 to 6 with the Mana Scarab. Pinned against the limit rather than the literal
        /// so the two cannot drift.
        /// </summary>
        [TestMethod]
        public void The_entry_count_is_clamped_to_the_press_limit()
        {
            Assert.AreEqual(6, RawFragmentRules.MaxEntries);

            var store = Store(Roster(Comp(37155, "Mana Scarab", "scarab", Op(AttunementOps.SetDifficulty, amount: 0, entries: 6))));
            var result = Press(LoadDose(Fragment(), store, 37155), store, Limits(maxEntries: 4), 35);

            Assert.AreEqual(4, result.Entries);
        }

        // ---- the talisman slot -----------------------------------------------------------------------

        [TestMethod]
        public void A_targeted_talisman_sharpens_its_own_modifier_when_it_is_present()
        {
            var store = Store(Roster(Comp(746, "Hazel Talisman", "talisman",
                Op(AttunementOps.Sharpen, modifier: "savage", bandMin: 0.3, bandMax: 0.7))));

            var spec = Fragment(mods: new[] { ("savage", 10.0), ("precise", 10.0) }).WithLoad(new (uint, int)[] { (746, 1) });
            var result = Press(spec, store, Limits(), 41);

            Assert.AreEqual(2, result.Spec.Modifiers.Count, "sharpen never ADDS");
            Assert.IsTrue(MagnitudeOf(result.Spec, "savage") > 10.0, "savage was sharpened");
            Assert.AreEqual(10.0, MagnitudeOf(result.Spec, "precise"), 0.001, "and nothing else moved");

            // Band 0.3-0.7 of savage's 30-point span is a bump of 9..21 from 10, so 19..31.
            Assert.IsTrue(MagnitudeOf(result.Spec, "savage") >= 19.0 && MagnitudeOf(result.Spec, "savage") <= 31.0);
        }

        [TestMethod]
        public void A_targeted_talisman_falls_back_to_a_present_modifier_when_its_own_is_absent()
        {
            var store = Store(Roster(Comp(746, "Hazel Talisman", "talisman",
                Op(AttunementOps.Sharpen, modifier: "savage", bandMin: 0.3, bandMax: 0.7))));

            var spec = Fragment(mods: new[] { ("precise", 10.0) }).WithLoad(new (uint, int)[] { (746, 1) });
            var result = Press(spec, store, Limits(), 42);

            Assert.AreEqual(1, result.Spec.Modifiers.Count);
            Assert.IsFalse(Has(result.Spec, "savage"), "sharpen never adds its own modifier either");
            Assert.IsTrue(MagnitudeOf(result.Spec, "precise") > 10.0);
        }

        /// <summary>
        /// Oak and Hemlock name no modifier and are scoped instead. Over many seeds an Oak Talisman only ever
        /// moves the monster-category modifier and never the boss one, and Hemlock the reverse.
        /// </summary>
        [TestMethod]
        public void A_scoped_talisman_only_sharpens_inside_its_scope()
        {
            var store = Store(Roster(
                Comp(748, "Oak Talisman", "talisman", Op(AttunementOps.Sharpen, scope: AttunementScopes.Monster, bandMin: 0.5, bandMax: 0.9)),
                Comp(747, "Hemlock Talisman", "talisman", Op(AttunementOps.Sharpen, scope: AttunementScopes.Boss, bandMin: 0.5, bandMax: 0.9))));

            AssertClean(store);

            for (var seed = 0; seed < 100; seed++)
            {
                var start = new[] { ("savage", 10.0), ("boss_enraged", 20.0) };

                var oak = Press(Fragment(mods: start).WithLoad(new (uint, int)[] { (748, 1) }), store, Limits(), seed);
                Assert.IsTrue(MagnitudeOf(oak.Spec, "savage") > 10.0, $"seed {seed}: Oak sharpens the monster modifier");
                Assert.AreEqual(20.0, MagnitudeOf(oak.Spec, "boss_enraged"), 0.001, $"seed {seed}: and never the boss one");

                var hemlock = Press(Fragment(mods: start).WithLoad(new (uint, int)[] { (747, 1) }), store, Limits(), seed);
                Assert.AreEqual(10.0, MagnitudeOf(hemlock.Spec, "savage"), 0.001, $"seed {seed}: Hemlock never sharpens the monster modifier");
                Assert.IsTrue(MagnitudeOf(hemlock.Spec, "boss_enraged") > 20.0, $"seed {seed}: it sharpens the boss one");
            }
        }

        // ---- the potion slot -------------------------------------------------------------------------

        /// <summary>
        /// A potion re-rolls the N WEAKEST contributions, measured as a fraction of each modifier's own range
        /// (raw magnitudes are not comparable: savage runs 10..40 and hardy runs 1.3..2.0). The id is never
        /// changed - a potion refines, it does not replace - and a band floor of 0.3 means the re-roll can
        /// only land at or above 30% of the range.
        /// </summary>
        [TestMethod]
        public void A_potion_rerolls_the_weakest_contribution_and_leaves_the_rest_alone()
        {
            var store = Store(Roster(Comp(753, "Brimstone", "potion",
                Op(AttunementOps.RerollWeakest, amount: 1, bandMin: 0.3, bandMax: 1.0))));

            for (var seed = 0; seed < 100; seed++)
            {
                // savage at 10 sits at fraction 0.0; precise at 30 sits at fraction 1.0.
                var spec = Fragment(mods: new[] { ("savage", 10.0), ("precise", 30.0) }).WithLoad(new (uint, int)[] { (753, 1) });
                var result = Press(spec, store, Limits(), seed);

                CollectionAssert.AreEqual(new[] { "savage", "precise" }, result.Spec.Modifiers.Select(m => m.Id).ToArray(),
                    $"seed {seed}: the ids are untouched");
                Assert.AreEqual(30.0, MagnitudeOf(result.Spec, "precise"), 0.001, $"seed {seed}: the strongest is left alone");
                Assert.IsTrue(MagnitudeOf(result.Spec, "savage") >= 19.0, $"seed {seed}: the band floor 0.3 of 10..40 is 19");
            }
        }

        [TestMethod]
        public void A_three_deep_potion_rerolls_three_contributions()
        {
            var store = Store(Roster(Comp(764, "Vitriol", "potion",
                Op(AttunementOps.RerollWeakest, amount: 3, bandMin: 0.1, bandMax: 1.0))));

            var spec = Fragment(mods: new[] { ("savage", 10.0), ("precise", 10.0), ("stalwart", 10.0), ("hardy", 2.0) })
                .WithLoad(new (uint, int)[] { (764, 1) });
            var result = Press(spec, store, Limits(), 51);

            Assert.AreEqual(2.0, MagnitudeOf(result.Spec, "hardy"), 0.001, "hardy is at fraction 1.0 and is never among the three weakest");
            Assert.IsTrue(new[] { "savage", "precise", "stalwart" }.All(id => MagnitudeOf(result.Spec, id) >= 13.0),
                "each of the three weakest was re-rolled at or above the 0.1 band floor");
        }

        /// <summary>
        /// A FIXED-MAGNITUDE modifier is not a re-roll candidate. "hollow" ships with minMagnitude ==
        /// maxMagnitude because the effect is binary, so it sits at fraction 0 of its own range and would
        /// otherwise sort as the weakest thing on every board it appears on - and re-rolling it cannot change
        /// it. Without the exclusion a Brimstone on a hollow gem would do nothing at all, every time.
        /// </summary>
        [TestMethod]
        public void A_potion_skips_a_fixed_magnitude_modifier_and_refines_something_that_can_move()
        {
            var store = Store(Roster(Comp(753, "Brimstone", "potion",
                Op(AttunementOps.RerollWeakest, amount: 1, bandMin: 0.3, bandMax: 1.0))));

            for (var seed = 0; seed < 100; seed++)
            {
                var spec = Fragment(mods: new[] { ("hollow", 1.0), ("savage", 10.0) }).WithLoad(new (uint, int)[] { (753, 1) });
                var result = Press(spec, store, Limits(), seed);

                Assert.AreEqual(1.0, MagnitudeOf(result.Spec, "hollow"), 0.001, $"seed {seed}: hollow cannot move");
                Assert.IsTrue(MagnitudeOf(result.Spec, "savage") >= 19.0,
                    $"seed {seed}: the potion spent itself on the modifier that CAN move, not on hollow");
            }
        }

        [TestMethod]
        public void A_potion_never_rerolls_a_locked_modifier()
        {
            var store = Store(Roster(Comp(753, "Brimstone", "potion",
                Op(AttunementOps.RerollWeakest, amount: 1, bandMin: 0.3, bandMax: 1.0))));

            for (var seed = 0; seed < 50; seed++)
            {
                var spec = Fragment(mods: new[] { ("savage", 10.0), ("precise", 30.0) }, locks: new[] { "savage" })
                    .WithLoad(new (uint, int)[] { (753, 1) });
                var result = Press(spec, store, Limits(), seed);

                Assert.AreEqual(10.0, MagnitudeOf(result.Spec, "savage"), 0.001, $"seed {seed}: a locked modifier is never re-rolled");
            }
        }

        // ---- the turquoise taper (add_random) --------------------------------------------------------

        /// <summary>
        /// The Turquoise Taper aims roughly: it draws from the DIFFICULTY scope - monster and boss modifiers,
        /// which is exactly the taper slots' own budget - and never reaches a bonus or a salvage affinity,
        /// because those belong to the herb and powder slots.
        /// </summary>
        [TestMethod]
        public void The_untargeted_taper_only_ever_draws_a_monster_or_boss_modifier()
        {
            var store = Store(Roster(Comp(1654, "Turquoise Taper", "taper",
                Op(AttunementOps.AddRandom, scope: AttunementScopes.Difficulty))));

            AssertClean(store);

            var drawn = new HashSet<string>();

            for (var seed = 0; seed < 300; seed++)
            {
                var result = Press(Fragment(), store, Limits(), seed);

                foreach (var (id, _) in result.Spec.Modifiers)
                {
                    drawn.Add(id);
                    var def = store.Modifiers[id];
                    Assert.AreNotEqual(DungeonModifierCategory.Bonus, DungeonModifierCategories.Of(def),
                        $"seed {seed}: '{id}' is a bonus and must not be reachable from a taper");
                }
            }

            CollectionAssert.IsSubsetOf(
                new[] { "savage", "precise", "hardy", "stalwart", "swift", "boss_enraged", "boss_guarded", "hollow", "shield_hollow" },
                drawn.ToArray());
        }

        // ---- determinism -----------------------------------------------------------------------------

        /// <summary>
        /// THE REPRODUCIBILITY CONTRACT. The same seed, the same load and the same loaded-component set must
        /// produce the same gem, every time. Asserted against a re-run rather than against a literal, so it
        /// pins the property rather than one build's arithmetic.
        /// </summary>
        [TestMethod]
        public void Determinism_the_same_seed_and_load_produce_the_same_gem()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 687, 746 })
                spec = LoadDose(spec, store, wcid);

            string Run()
            {
                var result = RawFragmentRules.Resolve(new PressState(spec, 3), store.Attunement, store.Modifiers,
                    Limits(), new Random(1234), out _);
                return result.Spec.Serialize() + "|entries=" + result.Entries.ToString(CultureInfo.InvariantCulture);
            }

            var first = Run();
            Assert.AreEqual(first, Run());
            Assert.AreEqual(first, Run());
        }

        /// <summary>
        /// The complement: the seed actually MATTERS. Without this, a press that ignored its rng would satisfy
        /// the determinism test above trivially.
        /// </summary>
        [TestMethod]
        public void Different_seeds_produce_different_gems()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var results = new HashSet<string>();

            for (var seed = 0; seed < 25; seed++)
                results.Add(Press(Fragment(), store, Limits(), seed).Spec.Serialize());

            Assert.IsTrue(results.Count > 5, $"25 seeds produced only {results.Count} distinct gems");
        }

        // ---- caps and invariants ---------------------------------------------------------------------

        [TestMethod]
        public void Never_more_than_two_locks()
        {
            var store = Store(Roster(Comp(746, "Lockbox", "talisman", Op(AttunementOps.LockOne, modifier: "savage"))));

            var spec = Fragment(mods: new[] { ("savage", 20.0), ("precise", 15.0), ("hardy", 1.5) },
                locks: new[] { "precise", "hardy" }).WithLoad(new (uint, int)[] { (746, 1) });
            var result = Press(spec, store, Limits(maxLocks: 2), 61);

            Assert.AreEqual(2, result.Spec.Locks.Count);
            Assert.IsFalse(result.Spec.Locks.Contains("savage"), "the lock budget was already spent");
        }

        [TestMethod]
        public void Tier_is_never_changed_by_pressing()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 689, 746, 753 })
                spec = LoadDose(spec, store, wcid);

            Assert.AreEqual(5, Press(spec, store, Limits(), 62).Spec.Tier);
        }

        [TestMethod]
        public void A_lock_on_a_removed_modifier_is_dropped()
        {
            var store = Store(Roster(Comp(753, "Brimstone", "potion", Op(AttunementOps.RemoveOne))));

            var spec = Fragment(mods: new[] { ("savage", 20.0), ("precise", 15.0) }, locks: new[] { "savage" })
                .WithLoad(new (uint, int)[] { (753, 1) });
            var result = Press(spec, store, Limits(), 63);

            Assert.IsFalse(Has(result.Spec, "precise"), "the potion removes the only unlocked modifier");
            foreach (var id in result.Spec.Locks)
                Assert.IsTrue(Has(result.Spec, id), $"lock '{id}' names an absent modifier");

            Assert.IsTrue(DungeonGemSpec.TryParse(result.Spec.Serialize(), out _, out var error), error);
        }

        [TestMethod]
        public void Resolve_clears_load_and_rolls_a_nonzero_seed()
        {
            var store = Store(TaperRoster());
            var result = Press(LoadDose(Fragment(), store, 1650), store, Limits(), 64);

            Assert.AreEqual(0, result.Spec.Load.Count);
            Assert.AreNotEqual(0, result.Spec.Seed);
        }

        [TestMethod]
        public void Resolve_increments_press_count_by_one_per_pressing()
        {
            var store = Store(TaperRoster());
            var first = Press(LoadDose(Fragment(), store, 1650), store, Limits(), 65);
            Assert.AreEqual(1, first.Spec.Presses);

            var second = Press(LoadDose(first.Spec.WithSeed(0), store, 1643), store, Limits(), 66);
            Assert.AreEqual(2, second.Spec.Presses);

            var third = Press(second.Spec.WithSeed(0), store, Limits(), 67);
            Assert.AreEqual(3, third.Spec.Presses);
        }

        /// <summary>
        /// A press draws ONLY from the resolved component's own op list - there is no second, global op source
        /// that can override what the board decided, and nothing destroys a modifier that no loaded or drawn
        /// component asked to destroy.
        /// </summary>
        [TestMethod]
        public void A_press_never_produces_anything_but_its_slots_own_ops()
        {
            var store = Store(TaperRoster());

            for (var seed = 0; seed < 200; seed++)
            {
                var spec = Fragment(mods: new[] { ("precise", 10.0), ("hardy", 1.3), ("stalwart", 10.0) });
                var result = Press(LoadDose(spec, store, 1650), store, Limits(), seed, entries: 3);

                Assert.AreEqual(4, result.Spec.Modifiers.Count, $"seed {seed}: nothing may be lost");
                Assert.IsTrue(Has(result.Spec, "savage"), $"seed {seed}: the taper's own op always lands");
                Assert.AreEqual(0, result.Spec.Locks.Count, $"seed {seed}: no op the board did not run may run");
                Assert.AreEqual(195, result.Spec.Level, $"seed {seed}: no scarab in the roster, so the level is untouched");
                Assert.AreEqual(3, result.Entries, $"seed {seed}: and so is the entry count");
            }
        }

        [TestMethod]
        public void The_dose_log_records_the_components_own_op_and_its_slot()
        {
            var store = Store(TaperRoster());

            for (var seed = 0; seed < 50; seed++)
            {
                RawFragmentRules.Resolve(new PressState(LoadDose(Fragment(), store, 1650), 0),
                    store.Attunement, store.Modifiers, Limits(), new Random(seed), out var log);

                Assert.AreEqual(4, log.Count, $"seed {seed}: four taper slots");
                Assert.IsTrue(log.All(d => d.Op == AttunementOps.AddOrRaise), $"seed {seed}");
                Assert.IsTrue(log.All(d => d.Slot == RawFragmentRules.TaperType), $"seed {seed}");
                Assert.IsFalse(log[0].Drawn, $"seed {seed}: the first taper slot was loaded");
                Assert.IsTrue(log.Skip(1).All(d => d.Drawn), $"seed {seed}: the other three drew");
            }
        }

        // ---- text ------------------------------------------------------------------------------------

        [TestMethod]
        public void Pressed_line_renders_counts()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 1650, 1643, 687 })
                spec = LoadDose(spec, store, wcid);

            Assert.AreEqual("Red Taper x2, Blue Taper, Gold Scarab", RawFragmentRules.ComposePressedLine(spec.Load, store.Attunement));
        }

        [TestMethod]
        public void Pressed_line_is_nothing_when_empty()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.AreEqual("nothing", RawFragmentRules.ComposePressedLine(Fragment().Load, store.Attunement));
        }

        // ---- the shipped file ------------------------------------------------------------------------

        /// <summary>
        /// The board's slot counts and the shipped file's per-type load limits are the same numbers, checked
        /// against the file rather than against the constant on its own - that pairing is what lint rule 22
        /// enforces and what stops a player loading a dose no slot will resolve.
        /// </summary>
        [TestMethod]
        public void Every_shipped_component_declares_its_slot_count_as_its_limit()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            foreach (var component in store.Attunement.Components.Values)
                Assert.AreEqual(RawFragmentRules.SlotCounts[component.Type], component.Limit,
                    $"{component.Name} ({component.Wcid}) is a '{component.Type}'");
        }

        /// <summary>
        /// Each of the eleven targeted tapers produces its own colour's modifier, at a magnitude inside that
        /// modifier's declared range. The Turquoise Taper is excluded because it names none by design.
        ///
        /// The load is a FULL four tapers of the same colour, so all four taper slots are occupied and no
        /// drawn taper can contribute a second modifier to the assertion.
        /// </summary>
        [TestMethod]
        public void Every_colour_taper_produces_its_own_modifier()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var tapers = store.Attunement.Components.Values
                .Where(c => c.Type == RawFragmentRules.TaperType && c.Color != null && store.Attunement.ColorModifier.ContainsKey(c.Color))
                .OrderBy(c => c.Wcid).ToList();

            Assert.AreEqual(11, tapers.Count, "eleven coloured tapers map to a modifier; turquoise maps to none");

            var seed = 100;
            foreach (var taper in tapers)
            {
                var expected = store.Attunement.ColorModifier[taper.Color];

                var spec = Fragment();
                for (var i = 0; i < RawFragmentRules.SlotCounts[RawFragmentRules.TaperType]; i++)
                    spec = RawFragmentRules.Load(spec, taper);

                var result = Press(spec, store, Limits(), seed++);
                var def = store.Modifiers[expected];

                Assert.IsTrue(Has(result.Spec, expected), $"{taper.Name} must produce {expected}");
                Assert.IsTrue(MagnitudeOf(result.Spec, expected) >= def.MinMagnitude - 0.001, taper.Name);
                Assert.IsTrue(MagnitudeOf(result.Spec, expected) <= def.MaxMagnitude + 0.001, taper.Name);
            }
        }

        /// <summary>
        /// The shipped board end to end: a fully loaded fragment presses into a gem carrying exactly what was
        /// chosen, with every budget respected. Four distinct tapers, a herb, a powder, a scarab, a potion and
        /// a talisman is all nine slots filled by hand.
        /// </summary>
        [TestMethod]
        public void A_fully_loaded_fragment_presses_into_a_completely_chosen_gem()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();

            //           red   blue  green grey  saffron powdered agate gold scarab brimstone hazel
            foreach (var wcid in new uint[] { 1650, 1643, 1645, 1646, 778, 626, 687, 753, 746 })
                spec = LoadDose(spec, store, wcid);

            var result = RawFragmentRules.Resolve(new PressState(spec, 1), store.Attunement, store.Modifiers,
                Limits(), new Random(77), out var log);

            Assert.AreEqual(9, log.Count);
            Assert.IsTrue(log.All(d => !d.Drawn), "nothing was drawn: every slot was filled by hand");

            CollectionAssert.AreEquivalent(
                new[] { "savage", "precise", "hardy", "stalwart", "enlightened", "affinity_iron" },
                result.Spec.Modifiers.Select(m => m.Id).ToArray());

            Assert.AreEqual(205, result.Spec.Level, "the Gold Scarab's +10 offset on a level-195 fragment");
            Assert.AreEqual(4, result.Entries, "and its four entries");
        }

        // ---- the aim roll (owner ruling, 2026-09-07) -------------------------------------------------
        //
        // "A component the player selects should land its associated effect only 50% of the time; the other
        // 50% draws from the pool of everything else in the same category." Every test above presses at
        // aimChance 1.0 (see the Limits helper) because each is a statement about the aim LANDING; these are
        // the statements about the aim MISSING, and they press at 0.0 and at the shipped 0.5 instead.
        //
        // The ids these assert against are read off the fixture's own budgets rather than listed by hand
        // wherever that is possible, because a hardcoded expectation is exactly what would survive someone
        // adding a modifier row and stop testing the pool.

        /// <summary>The nine fixture rows in the monster/boss budget - the fallback pool a taper misses into.</summary>
        private static readonly string[] MonsterBudget =
        {
            "savage", "precise", "hardy", "stalwart", "swift", "boss_enraged", "boss_guarded", "hollow", "shield_hollow",
        };

        /// <summary>
        /// THE CONTROL, and it is what every other test in this file relies on: at aimChance 1.0 the named
        /// modifier lands every single time. Without this the aim tests below could pass against a press that
        /// had simply stopped honouring op.Modifier at all.
        /// </summary>
        [TestMethod]
        public void An_aim_chance_of_one_always_lands_the_named_modifier()
        {
            var store = Store(Roster(Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage"))));

            for (var seed = 0; seed < 200; seed++)
            {
                var result = Press(Fragment(), store, Limits(aimChance: 1.0), seed);

                Assert.AreEqual(1, result.Spec.Modifiers.Count, $"seed {seed}: four Red Taper slots stack into one modifier");
                Assert.AreEqual("savage", result.Spec.Modifiers[0].Id, $"seed {seed}");
            }
        }

        /// <summary>
        /// The mirror: at aimChance 0.0 the named modifier NEVER lands, and what lands instead is always in
        /// the same budget - never a bonus and never a salvage affinity, which belong to the herb and powder
        /// slots. Over 200 seeds the whole rest of the pool is reached, so this is a pool rather than a
        /// redirect to one fixed sibling.
        /// </summary>
        [TestMethod]
        public void An_aim_chance_of_zero_never_lands_the_named_modifier_and_stays_in_its_budget()
        {
            var store = Store(Roster(Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage"))));
            var landed = new HashSet<string>(StringComparer.Ordinal);

            for (var seed = 0; seed < 200; seed++)
            {
                var result = Press(Fragment(), store, Limits(aimChance: 0.0), seed);

                Assert.IsFalse(Has(result.Spec, "savage"), $"seed {seed}: a total miss must never land the named modifier");

                foreach (var (id, _) in result.Spec.Modifiers)
                {
                    CollectionAssert.Contains(MonsterBudget, id, $"seed {seed}: '{id}' is outside the taper's own budget");
                    landed.Add(id);
                }
            }

            CollectionAssert.AreEquivalent(MonsterBudget.Where(id => id != "savage").ToArray(), landed.ToArray(),
                "every other modifier in the budget must be reachable, and nothing else");
        }

        /// <summary>
        /// A POWDER misses into the other SALVAGE AFFINITIES, not into the monster modifiers - the pool is the
        /// named modifier's own budget class, derived from BudgetOf rather than from any list. The fixture
        /// carries exactly two affinities, so a total miss on one is deterministically the other, which is the
        /// sharpest form this claim can take.
        /// </summary>
        [TestMethod]
        public void A_missed_powder_lands_another_salvage_affinity()
        {
            var store = Store(Roster(
                Comp(626, "Powdered Hematite", "powder", Op(AttunementOps.AddOrRaise, modifier: "affinity_iron"))));

            AssertClean(store);

            for (var seed = 0; seed < 50; seed++)
            {
                var result = Press(Fragment().WithLoad(new (uint, int)[] { (626, 1) }), store, Limits(aimChance: 0.0), seed);

                Assert.AreEqual(1, result.Spec.Modifiers.Count, $"seed {seed}");
                Assert.AreEqual("affinity_obsidian", result.Spec.Modifiers[0].Id,
                    $"seed {seed}: the only other affinity in the fixture");
            }
        }

        /// <summary>
        /// A HERB misses into the other bonuses. Same claim as the powder above, on the third budget, so the
        /// three cases together prove BudgetOf is what selects the pool rather than a salvage special case.
        /// </summary>
        [TestMethod]
        public void A_missed_herb_lands_another_bonus()
        {
            var store = Store(Roster(
                Comp(625, "Ginseng", "herb", Op(AttunementOps.AddOrRaise, modifier: "enlightened"))));

            AssertClean(store);

            var landed = new HashSet<string>(StringComparer.Ordinal);

            for (var seed = 0; seed < 100; seed++)
            {
                var result = Press(Fragment(), store, Limits(aimChance: 0.0), seed);

                Assert.AreEqual(1, result.Spec.Modifiers.Count, $"seed {seed}: the bonus budget is one");
                Assert.IsFalse(Has(result.Spec, "enlightened"), $"seed {seed}");
                landed.Add(result.Spec.Modifiers[0].Id);
            }

            CollectionAssert.AreEquivalent(new[] { "radiant", "bounteous" }, landed.ToArray(),
                "the fixture's other two bonus rows, and nothing from the monster or salvage budgets");
        }

        /// <summary>
        /// AN EMPTY FALLBACK POOL FALLS BACK TO THE NAMED MODIFIER rather than doing nothing - a miss must
        /// never be a wasted slot. Needs a modifier file whose budget class has exactly one member, so this
        /// builds its own two-row file rather than using the shared fixture.
        /// </summary>
        [TestMethod]
        public void A_miss_with_no_sibling_in_the_budget_lands_the_named_modifier_anyway()
        {
            const string oneAffinity =
                "{\"modifiers\":[" +
                "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\"}," +
                "{\"id\":\"affinity_iron\",\"display\":\"Iron Affinity\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":61,\"salvageBaseWcid\":243}" +
                "],\"xpLadder\":[]}";

            var store = ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, oneAffinity, new Dictionary<string, string>(),
                attunementJson: Roster(Comp(626, "Powdered Hematite", "powder", Op(AttunementOps.AddOrRaise, modifier: "affinity_iron"))));

            AssertClean(store);

            for (var seed = 0; seed < 50; seed++)
            {
                var result = Press(Fragment().WithLoad(new (uint, int)[] { (626, 1) }), store, Limits(aimChance: 0.0), seed);

                Assert.AreEqual(1, result.Spec.Modifiers.Count, $"seed {seed}: the slot still did something");
                Assert.AreEqual("affinity_iron", result.Spec.Modifiers[0].Id,
                    $"seed {seed}: the only affinity in the file, so the miss has nowhere to go");
            }
        }

        /// <summary>
        /// A TARGETED TALISMAN IS AIMED TOO, and this is the sharp form of that claim: a modifier file whose
        /// monster budget holds exactly TWO rows, so a miss on savage has precisely one place to go. At 1.0 the
        /// talisman sharpens its own wood's modifier and never the sibling; at 0.0 it sharpens the sibling and
        /// never its own. Both directions in one test, because either alone would also pass against a talisman
        /// that had simply stopped reading its modifier argument.
        /// </summary>
        [TestMethod]
        public void A_targeted_talisman_is_aimed_and_a_miss_sharpens_its_budget_sibling()
        {
            const string twoMonsterMods =
                "{\"modifiers\":[" +
                "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\"}," +
                "{\"id\":\"precise\",\"display\":\"Precise\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":30,\"monsterEffectKind\":\"crit_rating\"}" +
                "],\"xpLadder\":[]}";

            var store = ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, twoMonsterMods, new Dictionary<string, string>(),
                attunementJson: Roster(Comp(746, "Hazel Talisman", "talisman",
                    Op(AttunementOps.Sharpen, modifier: "savage", bandMin: 0.5, bandMax: 0.9))));

            AssertClean(store);

            for (var seed = 0; seed < 100; seed++)
            {
                var start = new[] { ("savage", 10.0), ("precise", 10.0) };
                var load = new (uint, int)[] { (746, 1) };

                var hit = Press(Fragment(mods: start).WithLoad(load), store, Limits(aimChance: 1.0), seed);
                Assert.IsTrue(MagnitudeOf(hit.Spec, "savage") > 10.0, $"seed {seed}: an aimed talisman sharpens its own modifier");
                Assert.AreEqual(10.0, MagnitudeOf(hit.Spec, "precise"), 0.001, $"seed {seed}: and nothing else");

                var miss = Press(Fragment(mods: start).WithLoad(load), store, Limits(aimChance: 0.0), seed);
                Assert.AreEqual(10.0, MagnitudeOf(miss.Spec, "savage"), 0.001,
                    $"seed {seed}: a total miss must not sharpen the named modifier");
                Assert.IsTrue(MagnitudeOf(miss.Spec, "precise") > 10.0, $"seed {seed}: it sharpens the sibling instead");
                Assert.AreEqual(2, miss.Spec.Modifiers.Count, $"seed {seed}: sharpen still never ADDS");
            }
        }

        /// <summary>
        /// WHAT A MISS ONTO AN ABSENT MODIFIER DOES, pinned because it surprised this build's own test first
        /// (the assertion above was originally written against the full fixture and failed). sharpen's
        /// pre-existing contract is that a named modifier which is not on the board falls back to a random
        /// PRESENT modifier inside the op's scope, and an untargeted talisman's scope is "any" - so when the
        /// miss lands on a modifier the gem does not carry, that fallback runs and it can legitimately choose
        /// the originally named modifier after all.
        ///
        /// This is deliberately NOT "fixed" into a rule that the named modifier can never be sharpened on a
        /// miss. The aim roll decides what the talisman REACHES for; what happens when the thing it reached
        /// for is not there is sharpen's own long-standing behaviour, and rewriting that from inside the aim
        /// feature would change an op's semantics on a board with no aim involved at all.
        /// </summary>
        [TestMethod]
        public void A_miss_onto_an_absent_modifier_falls_through_to_sharpens_own_scope_fallback()
        {
            var store = Store(Roster(Comp(746, "Hazel Talisman", "talisman",
                Op(AttunementOps.Sharpen, modifier: "savage", bandMin: 0.5, bandMax: 0.9))));

            var sharpenedTheNamed = 0;
            var sharpenedTheSibling = 0;

            for (var seed = 0; seed < 200; seed++)
            {
                // Only two of the nine monster-budget rows are on the board, so most misses name something
                // absent and reach the scope fallback.
                var spec = Fragment(mods: new[] { ("savage", 10.0), ("precise", 10.0) })
                    .WithLoad(new (uint, int)[] { (746, 1) });

                var result = Press(spec, store, Limits(aimChance: 0.0), seed);

                if (MagnitudeOf(result.Spec, "savage") > 10.0) sharpenedTheNamed++;
                if (MagnitudeOf(result.Spec, "precise") > 10.0) sharpenedTheSibling++;

                Assert.AreEqual(2, result.Spec.Modifiers.Count, $"seed {seed}: sharpen never adds");
            }

            Assert.IsTrue(sharpenedTheSibling > 0, "a miss must be able to reach the sibling");
            Assert.IsTrue(sharpenedTheNamed > 0,
                "and the scope fallback must still be able to land on the named modifier when the miss target is absent");
            Assert.AreEqual(200, sharpenedTheNamed + sharpenedTheSibling,
                "exactly one modifier is sharpened per pressing, never none and never both");
        }

        /// <summary>
        /// THE SHIPPED DEFAULT IS A COIN FLIP, measured rather than assumed. 400 seeds of a single loaded
        /// taper at PressLimits.DefaultAimChance: the named modifier lands on somewhere near half of them,
        /// asserted as a wide band because this is a sample and not the mean. The band still excludes both
        /// degenerate answers - always and never - which is what the test is for.
        ///
        /// The load is a full four tapers so all four slots are the loaded component and no drawn taper can
        /// contribute a savage of its own, which would make a miss invisible.
        /// </summary>
        [TestMethod]
        public void The_shipped_aim_chance_lands_the_named_modifier_about_half_the_time()
        {
            Assert.AreEqual(0.5, PressLimits.DefaultAimChance, 0.0001, "guard: the ruling's 50%");

            var store = Store(Roster(Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage"))));

            var landed = 0;

            for (var seed = 0; seed < 400; seed++)
            {
                // The FIRST taper slot's own outcome only, so the count below is a count of individual aim
                // rolls rather than of "did any of the four slots land".
                RawFragmentRules.Resolve(new PressState(Fragment(), 0), store.Attunement, store.Modifiers,
                    Limits(aimChance: PressLimits.DefaultAimChance), new Random(seed), out var log);

                if (log[0].After.StartsWith("mods=savage:", StringComparison.Ordinal)) landed++;
            }

            Assert.IsTrue(landed > 120 && landed < 280,
                $"400 aim rolls at 0.5 landed the named modifier {landed} times; expected roughly half");

            // And the same board at the two extremes, so the band above is not passing on a press that
            // ignores the dial entirely.
            Assert.IsTrue(Enumerable.Range(0, 25).All(s => Has(Press(Fragment(), store, Limits(aimChance: 1.0), s).Spec, "savage")));
            Assert.IsTrue(Enumerable.Range(0, 25).All(s => !Has(Press(Fragment(), store, Limits(aimChance: 0.0), s).Spec, "savage")));
        }

        /// <summary>
        /// THE ANTI-DOUBLING INVARIANT SURVIVES THE MISS. Whatever the aim finally chooses goes through the
        /// same add-or-stack path, so an id already on the gem is stacked rather than added a second time -
        /// the 50% miss is not a way around de-duplication. Asserted over 200 seeds at the shipped chance, on
        /// a board of four same-colour tapers whose misses land all over the monster budget.
        /// </summary>
        [TestMethod]
        public void A_missed_aim_stacks_onto_an_existing_modifier_rather_than_doubling_it()
        {
            var store = Store(Roster(Comp(1650, "Red Taper", "taper", Op(AttunementOps.AddOrRaise, modifier: "savage"))));

            for (var seed = 0; seed < 200; seed++)
            {
                var spec = Fragment(mods: new[] { ("savage", 10.0), ("precise", 10.0) })
                    .WithLoad(new (uint, int)[] { (1650, 4) });

                var result = Press(spec, store, Limits(aimChance: PressLimits.DefaultAimChance), seed);

                var ids = result.Spec.Modifiers.Select(m => m.Id).ToList();
                Assert.AreEqual(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(),
                    $"seed {seed}: a modifier appears twice: {string.Join(",", ids)}");
                Assert.IsTrue(result.Spec.Modifiers.Count <= 4, $"seed {seed}: the monster budget still binds");
            }
        }

        /// <summary>
        /// THE REPRODUCIBILITY CONTRACT, AT THE SHIPPED AIM CHANCE. The aim roll and the fallback pick both
        /// come off the same seeded Random as everything else and in a fixed order, so the same seed and the
        /// same load still produce the same gem - a probabilistic aim must not mean an unreproducible press.
        /// Asserted against a re-run rather than a literal, exactly as the deterministic-aim test above is.
        /// </summary>
        [TestMethod]
        public void Determinism_holds_at_the_shipped_aim_chance()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 1650, 626, 687, 746 })
                spec = LoadDose(spec, store, wcid);

            string Run()
            {
                var result = RawFragmentRules.Resolve(new PressState(spec, 3), store.Attunement, store.Modifiers,
                    Limits(aimChance: PressLimits.DefaultAimChance), new Random(4321), out _);
                return result.Spec.Serialize() + "|entries=" + result.Entries.ToString(CultureInfo.InvariantCulture);
            }

            var first = Run();
            Assert.AreEqual(first, Run());
            Assert.AreEqual(first, Run());

            // The complement, so this cannot pass on a press that ignores its rng: a different seed on the
            // same load produces a different gem.
            var other = RawFragmentRules.Resolve(new PressState(spec, 3), store.Attunement, store.Modifiers,
                Limits(aimChance: PressLimits.DefaultAimChance), new Random(4322), out _);
            Assert.AreNotEqual(first, other.Spec.Serialize() + "|entries=" + other.Entries.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The aim chance is sanitized where it is STORED, so no caller has to remember to clamp it and a
        /// NaN comparison can never silently mean "always miss".
        /// </summary>
        [TestMethod]
        public void The_aim_chance_is_clamped_on_construction()
        {
            Assert.AreEqual(1.0, Limits(aimChance: 5.0).AimChance, 0.0001);
            Assert.AreEqual(0.0, Limits(aimChance: -3.0).AimChance, 0.0001);
            Assert.AreEqual(PressLimits.DefaultAimChance, Limits(aimChance: double.NaN).AimChance, 0.0001);

            // The shipped default is what a call site that omits the argument gets, on the same contract the
            // two v2 budgets already use.
            Assert.AreEqual(PressLimits.DefaultAimChance,
                new PressLimits(4, 2, RawFragmentRules.MaxEntries, DungeonGemSpec.MaxLevel).AimChance, 0.0001);
        }

        /// <summary>
        /// The ops that name NO modifier are not aimed, because there is no aim to miss. Pinned on
        /// add_random, whose whole job is already to be a wildcard: a Turquoise Taper at aimChance 0.0 must
        /// behave exactly as it does at 1.0.
        /// </summary>
        [TestMethod]
        public void An_unaimed_op_is_unaffected_by_the_aim_chance()
        {
            var store = Store(Roster(Comp(1654, "Turquoise Taper", "taper",
                Op(AttunementOps.AddRandom, scope: AttunementScopes.Difficulty))));

            for (var seed = 0; seed < 100; seed++)
            {
                var aimed = Press(Fragment(), store, Limits(aimChance: 1.0), seed).Spec.Serialize();
                var unaimed = Press(Fragment(), store, Limits(aimChance: 0.0), seed).Spec.Serialize();

                Assert.AreEqual(aimed, unaimed, $"seed {seed}: add_random takes no aim roll, so the dial cannot move it");
            }
        }
    }
}
