using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// DungeonGemNarrator (press feedback D8): the load-time intent line, and the press-result breakdown
    /// (per-dose lines, finished-gem summary). Pure - every test builds its own AttunementDef through
    /// ThreadDungeonStore.Parse, the same pattern RawFragmentRulesTests uses, so nothing here touches
    /// PropertyManager.
    ///
    /// TWO OWNER RULINGS (2026-09-07) reshaped this file wholesale.
    ///
    /// 1. The load line is VAGUE, reversing PR #969. Every "phrase names the display" test was REWRITTEN to
    ///    assert the category-only wording; none was weakened, and several gained a negative assertion that
    ///    the modifier's name and magnitude are absent, which is the actual requirement. The three tests whose
    ///    entire subject was the specific wording - Single_op_load_line_names_the_effect_as_a_range,
    ///    Boss_target_modifier_keeps_the_boss_prefix_in_range_wording,
    ///    Equal_min_and_max_magnitude_renders_a_single_value_not_a_range - were DELETED, along with
    ///    Load_line_effect_text_is_literally_RenderEffectRanges_own_output and
    ///    Unrecognised_effect_kind_falls_back_to_the_bare_display_name: the load line no longer renders a
    ///    magnitude range at all, so there is nothing left for them to assert. The range formatter those
    ///    tests covered lost its last caller with them and was deleted rather than kept alive by its own
    ///    doc comment. The finished gem's "Modifiers:" list is a DIFFERENT method (RenderEffect) and is
    ///    pinned by DungeonGemLongDescTests, unaffected by any of this.
    ///
    /// 2. Instability is GONE. Calm_phrase, ZeroInstability_phrase,
    ///    Calm_with_no_modifier_change_names_the_settle, ZeroInstability_with_no_modifier_change_names_the_stilling,
    ///    Projection_sums_loaded_doses_plus_a_reopened_gems_existing_instability, Projection_of_zero_omits_the_sentence,
    ///    Nothing_op_dose_still_shows_an_instability_move, Dose_line_shows_both_a_modifier_change_and_an_instability_move
    ///    and Wild_dose_line_carries_the_WILD_marker_and_the_ops_named_effect were all DELETED - every one of
    ///    them asserts behaviour of an op, a projection or a draw that no longer exists.
    /// </summary>
    [TestClass]
    public class DungeonGemNarratorTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";

        /// <summary>
        /// savage/hardy/teeming carry no monsterEffectKind and no reward slope, so all three classify as
        /// MONSTER - the fall-through case. guarded is target "boss"; radiant is kind "none" with a
        /// non-neutral luminance pair, the shipped shape of a BONUS row. affinity_obsidian is a salvage
        /// affinity, the ONE kind the powder exception lets these strings name (owner ruling, 2026-09-07),
        /// and it classifies as bonus exactly as radiant does - which is what makes it a real test of the
        /// exception rather than of the category.
        /// </summary>
        private const string ModifiersJson =
            "{\"modifiers\":[" +
            "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40}," +
            "{\"id\":\"hardy\",\"display\":\"Hardy\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0}," +
            "{\"id\":\"teeming\",\"display\":\"Teeming\",\"rarity\":\"uncommon\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.75}," +
            "{\"id\":\"guarded\",\"display\":\"Guarded\",\"rarity\":\"common\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}," +
            "{\"id\":\"radiant\",\"display\":\"Radiant\",\"rarity\":\"rare\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"none\",\"rewardLumBase\":0.0,\"rewardLumSlope\":1.0}," +
            "{\"id\":\"affinity_obsidian\",\"display\":\"Obsidian Affinity\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":69,\"salvageBaseWcid\":243}" +
            "],\"xpLadder\":[]}";

        /// <summary>One component per op, so a single component's Ops list can be built to exactly one entry
        /// for the phrasing tests, and combined for the several-ops tests.</summary>
        private const string AttunementJson =
            "{\"version\":1,\"colors\":[{\"color\":\"red\",\"modifier\":\"savage\"}],\"components\":[" +
            "{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"color\":\"red\",\"dose\":10,\"limit\":4," +
            "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}," +
            "{\"wcid\":1645,\"name\":\"Green Taper\",\"type\":\"taper\",\"color\":\"green\",\"dose\":10,\"limit\":4," +
            "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"hardy\",\"weight\":100}]}," +
            ManaScarabJson + "," + HazelTalismanJson + "," +
            // A multi-op component, for the several-ops sentence shape. It sits in the potion slot only
            // because every component now has to sit in one of the six press v2 slots; the ops are what the
            // test is about.
            "{\"wcid\":900001,\"name\":\"Multi\",\"type\":\"potion\",\"color\":null,\"dose\":1,\"limit\":1," +
            "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":1},{\"op\":\"raise_random\",\"weight\":1},{\"op\":\"nothing\",\"weight\":1}]}" +
            "]}";

        // The three shipped rows the counter-pair tests drive through the real Resolve, each usable on its
        // own. Press v2 resolves the whole nine-slot board, so a test that wants exactly ONE dose has to
        // build a store carrying exactly one slot type - see SoloStore.
        private const string HazelTalismanJson =
            "{\"wcid\":746,\"name\":\"Hazel Talisman\",\"type\":\"talisman\",\"color\":null,\"dose\":1,\"limit\":1," +
            "\"ops\":[{\"op\":\"lock_one\",\"modifier\":\"savage\",\"weight\":100}]}";

        private const string IronScarabJson =
            "{\"wcid\":689,\"name\":\"Iron Scarab\",\"type\":\"scarab\",\"color\":null,\"dose\":1,\"limit\":1," +
            "\"ops\":[{\"op\":\"level_up\",\"amount\":1,\"weight\":100}]}";

        private const string ManaScarabJson =
            "{\"wcid\":37155,\"name\":\"Mana Scarab\",\"type\":\"scarab\",\"color\":null,\"dose\":1,\"limit\":1," +
            "\"ops\":[{\"op\":\"add_entry\",\"weight\":100}]}";

        private const string LeadScarabJson =
            "{\"wcid\":691,\"name\":\"Lead Scarab\",\"type\":\"scarab\",\"color\":null,\"dose\":1,\"limit\":1," +
            "\"ops\":[{\"op\":\"set_difficulty\",\"amount\":-10,\"entries\":3,\"weight\":100}]}";

        /// <summary>
        /// A SECOND modifier set, for the summary tests only. <see cref="ModifiersJson"/> deliberately gives
        /// savage/hardy/teeming no monsterEffectKind at all, because the load-line tests above are about the
        /// category classifier's fall-through case; that also means those three have no EFFECT to describe,
        /// so they are the wrong fixture for a block whose whole subject is the effect wording. These rows
        /// carry the shipped kinds and magnitudes verbatim from Content/dungeons/dynamic/modifiers.json.
        ///
        /// It keeps savage and hardy, because AttunementJson's ops name them and the store's lint drops a
        /// component whose op names a modifier the file does not carry.
        /// </summary>
        private const string EffectModifiersJson =
            "{\"modifiers\":[" +
            "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\"}," +
            "{\"id\":\"hardy\",\"display\":\"Hardy\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\"}," +
            "{\"id\":\"stalwart\",\"display\":\"Stalwart\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_resist_rating\"}," +
            "{\"id\":\"bounteous\",\"display\":\"Bounteous\",\"rarity\":\"uncommon\",\"target\":\"run\",\"minMagnitude\":1.15,\"maxMagnitude\":1.6,\"monsterEffectKind\":\"loot_quantity\"}," +
            "{\"id\":\"affinity_green_garnet\",\"display\":\"Green Garnet Affinity\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":23,\"salvageBaseWcid\":243}" +
            "],\"xpLadder\":[]}";

        private static ThreadDungeonStore Store()
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, ModifiersJson, new Dictionary<string, string>(), attunementJson: AttunementJson);

        private static ThreadDungeonStore EffectStore()
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, EffectModifiersJson, new Dictionary<string, string>(), attunementJson: AttunementJson);

        /// <summary>A store carrying exactly one component, so a press resolves exactly one slot.</summary>
        private static ThreadDungeonStore SoloStore(string componentJson)
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, ModifiersJson, new Dictionary<string, string>(),
                attunementJson: "{\"version\":1,\"colors\":[],\"components\":[" + componentJson + "]}");

        private static ComponentDef Component(ThreadDungeonStore store, uint wcid)
        {
            Assert.IsTrue(store.Attunement.TryGet(wcid, out var component), $"roster has no wcid {wcid}");
            return component;
        }

        private static ComponentDef OneOpComponent(string op, string modifier = null, int amount = 0,
            string scope = null)
            => new ComponentDef
            {
                Wcid = 1,
                Name = "Test Comp",
                Type = "taper",
                Ops = new List<OpDef> { new OpDef { Op = op, Modifier = modifier, Amount = amount, Scope = scope, Weight = 1 } },
            };

        /// <summary>A fragment. <paramref name="mods"/> matters only for add_or_raise's add/change verb.</summary>
        private static DungeonGemSpec Fragment(IEnumerable<(string Id, double Magnitude)> mods = null,
            IEnumerable<(uint Wcid, int Doses)> load = null)
            => new DungeonGemSpec("any", 195, 5, "any", 0, mods, 0, 0, 0, null, load);

        /// <summary>The one-op sentence for a component, against an empty gem unless a spec is given.</summary>
        private static string Sentence(string op, string modifier = null, int amount = 0, DungeonGemSpec spec = null,
            string scope = null)
            => DungeonGemNarrator.ComposeIntentSentence(OneOpComponent(op, modifier, amount, scope), spec ?? Fragment(),
                Store().Modifiers);

        // ---- op phrasings: category only, never the identity or the magnitude ---------------------------

        /// <summary>
        /// THE RULING, in one assertion. add_or_raise against a modifier the gem does not have reads "add",
        /// the category is named, and neither "Savage" nor any number from its 10-40 range appears.
        /// </summary>
        [TestMethod]
        public void AddOrRaise_on_an_absent_modifier_says_add_and_names_only_the_category()
        {
            var line = Sentence(AttunementOps.AddOrRaise, "savage");

            Assert.AreEqual("It may add a monster modifier.", line);
            Assert.IsFalse(line.Contains("Savage"), line);
            Assert.IsFalse(line.Contains("10"), line);
            Assert.IsFalse(line.Contains("40"), line);
        }

        /// <summary>The verb, and only the verb, changes when the gem already carries the modifier.</summary>
        [TestMethod]
        public void AddOrRaise_on_a_present_modifier_says_change()
        {
            var carrying = Fragment(new[] { ("savage", 22.0) });

            Assert.AreEqual("It may change a monster modifier.",
                Sentence(AttunementOps.AddOrRaise, "savage", spec: carrying));
        }

        /// <summary>A boss-target modifier and a bonus modifier get their own category word.</summary>
        [TestMethod]
        public void AddOrRaise_names_the_boss_and_bonus_categories()
        {
            Assert.AreEqual("It may add a boss modifier.", Sentence(AttunementOps.AddOrRaise, "guarded"));
            Assert.AreEqual("It may add a bonus modifier.", Sentence(AttunementOps.AddOrRaise, "radiant"));
        }

        // ---- the powder exception (owner ruling, 2026-09-07) ----------------------------------------------

        /// <summary>
        /// A MAPPED powder NAMES its material, verbatim from the modifier's Display and with no article: "It
        /// may add Obsidian Affinity." That is the whole exception, on the chat surface.
        ///
        /// affinity_obsidian classifies as BONUS, exactly as radiant does in the test above, so this cannot
        /// pass because of anything about the category - it passes only if the exception is applied.
        /// </summary>
        [TestMethod]
        public void A_mapped_powder_names_its_material()
        {
            Assert.AreEqual(DungeonModifierCategory.Bonus,
                DungeonModifierCategories.Of(Store().Modifiers["affinity_obsidian"]),
                "guard: an affinity is a bonus, so the assertion below is about the exception, not the category");

            Assert.AreEqual("It may add Obsidian Affinity.", Sentence(AttunementOps.AddOrRaise, "affinity_obsidian"));

            // Literally the row's own Display, not a string that merely looks like it today.
            Assert.AreEqual("It may add " + Store().Modifiers["affinity_obsidian"].Display + ".",
                Sentence(AttunementOps.AddOrRaise, "affinity_obsidian"));
        }

        /// <summary>An affinity already on the gem reads "change", the same add/raise verb split as any
        /// other modifier - the exception changed the object, not the sentence.</summary>
        [TestMethod]
        public void A_mapped_powder_reads_change_when_the_affinity_is_already_present()
        {
            var carrying = Fragment(mods: new[] { ("affinity_obsidian", 10.0) });

            Assert.AreEqual("It may change Obsidian Affinity.",
                Sentence(AttunementOps.AddOrRaise, "affinity_obsidian", spec: carrying));
        }

        /// <summary>
        /// A WILDCARD powder says it is random and names no material - it has drawn nothing yet. Contrasted
        /// against add_random at another scope, which stays the generic "add a modifier": the salvage wording
        /// exists so the two powder shapes read as one slot, not as two different kinds of component.
        /// </summary>
        [TestMethod]
        public void A_wildcard_powder_says_random_and_names_no_material()
        {
            var line = Sentence(AttunementOps.AddRandom, scope: AttunementScopes.Salvage);

            Assert.AreEqual("It may add a random salvage affinity.", line);
            Assert.IsFalse(line.Contains("Obsidian"), line);

            Assert.AreEqual("It may add a modifier.",
                Sentence(AttunementOps.AddRandom, scope: AttunementScopes.Difficulty));
        }

        /// <summary>
        /// THE DISCRIMINATING NEGATIVE: the exception did not leak. A taper (savage), a boss taper (guarded),
        /// a herb (radiant) and a talisman (sharpen savage) still name no modifier, asserted against each
        /// modifier's DISPLAY string rather than against a category word being present - a leak would keep
        /// the sentence shape and swap the object, so only the display can catch it.
        ///
        /// The affinity line is asserted to DO name its material in the same test, so this cannot pass by the
        /// narrator naming nothing at all, which is how a negative-only test quietly stops testing.
        /// </summary>
        [TestMethod]
        public void The_naming_exception_does_not_leak_to_other_types()
        {
            // The control: naming IS happening, for the one kind that may.
            StringAssert.Contains(Sentence(AttunementOps.AddOrRaise, "affinity_obsidian"), "Obsidian Affinity");

            var vague = new[]
            {
                Sentence(AttunementOps.AddOrRaise, "savage"),
                Sentence(AttunementOps.AddOrRaise, "guarded"),
                Sentence(AttunementOps.SetMax, "radiant"),
                Sentence(AttunementOps.Sharpen, "savage"),
                Sentence(AttunementOps.LockOne, "savage"),
            };

            foreach (var line in vague)
            {
                foreach (var id in new[] { "savage", "guarded", "radiant" })
                {
                    var display = Store().Modifiers[id].Display;
                    Assert.IsFalse(line.Contains(display), $"'{display}' leaked into: {line}");
                }

                // And no magnitude anywhere, which has no exception at all.
                foreach (var number in new[] { "10", "40", "1.3", "2.0", "1.5", "3.0", "1.25" })
                    Assert.IsFalse(line.Contains(number), $"magnitude '{number}' leaked into: {line}");
            }
        }

        /// <summary>
        /// No MAGNITUDE reaches the affinity line either. The exception is about the name alone, and an
        /// affinity's 5-25 range is exactly the sort of number that would look harmless to add later.
        /// </summary>
        [TestMethod]
        public void The_named_affinity_still_carries_no_magnitude()
        {
            var line = Sentence(AttunementOps.AddOrRaise, "affinity_obsidian");

            foreach (var number in new[] { "5", "25", "69", "243" })
                Assert.IsFalse(line.Contains(number), $"'{number}' must not appear in: {line}");
        }

        /// <summary>
        /// THE SHIPPED WORDING, on the real content rather than on a fixture: both powder shapes, both
        /// surfaces, in one place. Powdered Onyx 790 is mapped to affinity_obsidian and Powdered Agate 782 is
        /// a wildcard, so this breaks if either the mapping or either surface's wording moves - which is what
        /// makes it the thing to read when someone asks what a player actually sees.
        /// </summary>
        [TestMethod]
        public void The_shipped_powders_render_as_specified_on_both_surfaces()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            Assert.IsTrue(store.Attunement.TryGet(790, out var onyx), "shipped roster has no Powdered Onyx");
            Assert.IsTrue(store.Attunement.TryGet(782, out var agate), "shipped roster has no Powdered Agate");

            // Surface 1: the chat load line.
            Assert.AreEqual("It may add Obsidian Affinity.",
                DungeonGemNarrator.ComposeIntentSentence(onyx, Fragment(), store.Modifiers));
            Assert.AreEqual("It may add a random salvage affinity.",
                DungeonGemNarrator.ComposeIntentSentence(agate, Fragment(), store.Modifiers));

            // Surface 2: the fragment panel's "Possible effects:" block.
            Assert.AreEqual("Possible effects:\nObsidian Affinity", ShippedPossibleEffects(store, 790));
            Assert.AreEqual("Possible effects:\nA random salvage affinity", ShippedPossibleEffects(store, 782));
        }

        /// <summary>The "Possible effects:" block a fragment carrying exactly one shipped component shows.</summary>
        private static string ShippedPossibleEffects(ThreadDungeonStore store, uint wcid)
        {
            var spec = Fragment(load: new[] { (wcid, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3,
                id => store.Modifiers.TryGetValue(id, out var d) ? d : null,
                w => store.Attunement.TryGet(w, out var c) ? c.Name : $"wcid {w}",
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap,
                w => store.Attunement.TryGet(w, out var c) ? c : null);

            var start = desc.IndexOf("Possible effects:", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"no Possible effects block in:\n{desc}");

            var block = desc.Substring(start);
            var end = block.IndexOf("\n\n", StringComparison.Ordinal);
            if (end >= 0) block = block.Substring(0, end);

            // The block runs to the next non-block line; the reward summary follows it, so cut at the first
            // line that is neither the header nor an entry.
            var kept = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                if (kept.Count == 0) { kept.Add(line); continue; }
                if (line.StartsWith("Experience", StringComparison.Ordinal)
                    || line.StartsWith("Luminance", StringComparison.Ordinal)
                    || line.StartsWith("Loot", StringComparison.Ordinal)
                    || line.StartsWith("Entries", StringComparison.Ordinal)
                    || line.StartsWith("Unpressed", StringComparison.Ordinal))
                    break;
                kept.Add(line);
            }

            return string.Join("\n", kept);
        }

        /// <summary>
        /// sharpen against an affinity drops the "the gem already has" tail, which exists to say WHICH
        /// monster modifier is meant and reads as a grammatical error after a proper noun. Nothing shipped
        /// sharpens an affinity, but the op is data-driven, so one JSON row would reach this.
        /// </summary>
        [TestMethod]
        public void Sharpen_against_an_affinity_drops_the_trailing_clause()
        {
            Assert.AreEqual("It may strengthen Obsidian Affinity.", Sentence(AttunementOps.Sharpen, "affinity_obsidian"));
            Assert.AreEqual("It may strengthen a monster modifier the gem already has.",
                Sentence(AttunementOps.Sharpen, "savage"));
        }

        [TestMethod]
        public void RaiseRandom_phrase()
            => Assert.AreEqual("It may change one modifier the gem already has.", Sentence(AttunementOps.RaiseRandom));

        [TestMethod]
        public void RaiseAll_phrase()
            => Assert.AreEqual("It may change every modifier the gem has.", Sentence(AttunementOps.RaiseAll));

        /// <summary>set_max is "change" per the ruling, and its named modifier still only yields a category.</summary>
        [TestMethod]
        public void SetMax_phrase_names_only_the_category()
        {
            var line = Sentence(AttunementOps.SetMax, "hardy");

            Assert.AreEqual("It may change a monster modifier.", line);
            Assert.IsFalse(line.Contains("Hardy"), line);
        }

        [TestMethod]
        public void RemoveOne_phrase()
            => Assert.AreEqual("It may remove a modifier.", Sentence(AttunementOps.RemoveOne));

        [TestMethod]
        public void RerollOne_phrase()
            => Assert.AreEqual("It may change a modifier.", Sentence(AttunementOps.RerollOne));

        /// <summary>
        /// lock_one was NOT covered by the owner's verb table, since locking is neither an add, a change nor a
        /// removal of a modifier. It keeps its own verb and gives up only the modifier NAME, which is what the
        /// ruling actually forbids. Flagged in the report as a spec gap resolved this way.
        /// </summary>
        [TestMethod]
        public void LockOne_keeps_its_own_verb_and_gives_up_only_the_name()
        {
            var line = Sentence(AttunementOps.LockOne, "savage");

            Assert.AreEqual("It may lock a monster modifier in place.", line);
            Assert.IsFalse(line.Contains("Savage"), line);
        }

        /// <summary>level_up is "change" per the ruling. It touches no modifier, so there is no category.</summary>
        [TestMethod]
        public void LevelUp_phrase()
            => Assert.AreEqual("It may change the dungeon's level.", Sentence(AttunementOps.LevelUp, amount: 1));

        /// <summary>add_entry, like lock_one, was not in the ruling's verb table; it touches no modifier at
        /// all, so it keeps its pre-existing wording behind the honest "may".</summary>
        [TestMethod]
        public void AddEntry_phrase()
            => Assert.AreEqual("It may add an entry.", Sentence(AttunementOps.AddEntry));

        /// <summary>"nothing" keeps its pre-ruling no-op wording exactly, including the indicative mood.</summary>
        [TestMethod]
        public void Nothing_phrase()
            => Assert.AreEqual("It does nothing.", Sentence(AttunementOps.Nothing));

        /// <summary>
        /// A modifier id the store cannot resolve degrades to the uncategorised object rather than leaking the
        /// raw id - which the pre-ruling wording did print ("It works toward ghost_mod.").
        /// </summary>
        [TestMethod]
        public void Unresolvable_modifier_id_degrades_to_an_uncategorised_modifier()
        {
            var line = Sentence(AttunementOps.AddOrRaise, "ghost_mod");

            Assert.AreEqual("It may add a modifier.", line);
            Assert.IsFalse(line.Contains("ghost_mod"), line);
        }

        // ---- one-op vs several-ops sentence shape -------------------------------------------------------

        [TestMethod]
        public void One_op_uses_the_It_phrase_period_form()
        {
            var store = Store();
            var line = DungeonGemNarrator.ComposeIntentSentence(Component(store, 1650), Fragment(), store.Modifiers);
            Assert.AreEqual("It may add a monster modifier.", line);
        }

        /// <summary>
        /// Round 2 regression pin, PRESERVED through the vague rewrite because the bug it guards is about
        /// sentence assembly rather than wording: round 1 spliced the SINGLE form after "may" and produced
        /// "It may works toward Savage" - ungrammatical, and "does" -> "do" cannot be fixed by a generic
        /// inflection rule since "do" is irregular. The two forms are still carried explicitly for that
        /// reason, and this test still proves the list uses the base form. It also pins the multi-op shape the
        /// owner asked to keep: comma-separated, "or" before the last, no comma before "or".
        /// </summary>
        [TestMethod]
        public void Several_ops_use_the_base_ListForm_not_the_inflected_Single_form()
        {
            var store = Store();
            var line = DungeonGemNarrator.ComposeIntentSentence(Component(store, 900001), Fragment(), store.Modifiers);
            Assert.AreEqual("It may add a monster modifier, change one modifier the gem already has or do nothing.", line);
        }

        /// <summary>
        /// REGRESSION (review finding 3, 2026-09-07). Press v2 gave components several ops that differ only
        /// in which modifier they name, and the vague ruling is precisely what forbids printing that name -
        /// so they render to the SAME words. The shipped Powdered Quartz read "It may add a bonus modifier,
        /// add a bonus modifier or add a bonus modifier." Correct under the ruling and unshippable: it reads
        /// as a bug. Identical clauses collapse, keeping the first occurrence.
        ///
        /// savage, hardy and teeming are three DIFFERENT monster modifiers, so nothing but the phrase text
        /// itself can make this collapse.
        /// </summary>
        [TestMethod]
        public void Identical_clauses_collapse_to_one()
        {
            var component = new ComponentDef
            {
                Wcid = 3,
                Name = "Powdered Quartz",
                Type = "powder",
                Ops = new List<OpDef>
                {
                    new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "savage", Weight = 1 },
                    new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "hardy", Weight = 1 },
                    new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "teeming", Weight = 1 },
                },
            };

            var line = DungeonGemNarrator.ComposeIntentSentence(component, Fragment(), Store().Modifiers);

            // Collapsed to one clause, so the SINGLE form is used and there is no "may ... or ..." list left.
            Assert.AreEqual("It may add a monster modifier.", line);
        }

        /// <summary>
        /// The CONTROL for the collapse: only IDENTICAL clauses merge. A component whose ops render to
        /// different phrases still lists all of them, in its own op order, with the first duplicate's
        /// position kept.
        /// </summary>
        [TestMethod]
        public void Distinct_clauses_survive_the_collapse_in_first_appearance_order()
        {
            var component = new ComponentDef
            {
                Wcid = 4,
                Name = "Mixed",
                Type = "powder",
                Ops = new List<OpDef>
                {
                    new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "savage", Weight = 1 },
                    new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "radiant", Weight = 1 },
                    new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "hardy", Weight = 1 },
                    new OpDef { Op = AttunementOps.RemoveOne, Weight = 1 },
                },
            };

            var line = DungeonGemNarrator.ComposeIntentSentence(component, Fragment(), Store().Modifiers);

            Assert.AreEqual("It may add a monster modifier, add a bonus modifier or remove a modifier.", line);
        }

        /// <summary>A component with no ops at all still produces a sentence rather than an empty string.</summary>
        [TestMethod]
        public void No_ops_reads_as_does_nothing()
        {
            var bare = new ComponentDef { Wcid = 2, Name = "Bare", Type = "potion", Ops = new List<OpDef>() };
            Assert.AreEqual("It does nothing.", DungeonGemNarrator.ComposeIntentSentence(bare, Fragment(), Store().Modifiers));
        }

        // ---- the whole load message ---------------------------------------------------------------------

        /// <summary>
        /// REWRITTEN from Load_message_omits_the_cost_sentence_when_projection_is_zero. The projected-cost
        /// sentence ("This pressing will carry roughly N unsteadiness.") went with the instability mechanic,
        /// so the message is now always exactly two sentences and the omission it tested cannot occur. Kept
        /// rather than deleted because the assembled two-sentence line is the real player-facing string.
        /// </summary>
        [TestMethod]
        public void Load_message_is_the_takes_line_plus_the_intent_sentence()
        {
            var store = Store();
            var loaded = Fragment(load: new (uint, int)[] { (1650, 1) });

            var message = DungeonGemNarrator.ComposeLoadMessage(Component(store, 1650), loaded, store.Attunement, store.Modifiers);

            Assert.AreEqual("The fragment takes the Red Taper. It may add a monster modifier.", message);
            Assert.IsFalse(message.Contains("unsteadiness"), message);
        }

        // ---- dose lines --------------------------------------------------------------------------------

        [TestMethod]
        public void Normal_dose_line_renders_the_modifier_change()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 1650,
                Name = "Red Taper",
                Op = AttunementOps.AddOrRaise,
                Before = "mods=savage:12",
                After = "mods=savage:22",
            };

            Assert.AreEqual("  Red Taper: Savage 12 -> Savage 22", DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        /// <summary>
        /// A dose line is a POST-press surface, so it deliberately still names the real modifier at its real
        /// magnitude. Pinned explicitly so a later reader does not "fix" it into the vague wording: the ruling
        /// governs the pre-press fragment surfaces only.
        /// </summary>
        [TestMethod]
        public void Dose_lines_are_a_post_press_surface_and_stay_specific()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 1650, Name = "Red Taper", Op = AttunementOps.AddOrRaise,
                Before = "mods=", After = "mods=savage:10",
            };

            var line = DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers);
            Assert.AreEqual("  Red Taper: gained Savage 10", line);
            Assert.IsFalse(line.Contains("monster modifier"), line);
        }

        [TestMethod]
        public void A_dose_that_changed_nothing_reads_as_nothing_happened()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 1650,
                Name = "Red Taper",
                Op = AttunementOps.AddOrRaise,
                Before = "mods=savage:40",
                After = "mods=savage:40",
            };

            Assert.AreEqual("  Red Taper: nothing happened", DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        /// <summary>
        /// A Render string written before the instability removal still carried "|inst=<n>". ParseRender keeps
        /// its pipe split so such a line reads back to the same modifier list rather than being mis-parsed -
        /// dose logs go to the server log, and an old line pasted back through here must not say something
        /// different. Note the "(unsteadiness A -> B)" suffix is correctly ABSENT: the term is not read.
        /// </summary>
        [TestMethod]
        public void A_legacy_render_string_carrying_inst_still_reads_back_correctly()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 1650, Name = "Red Taper", Op = AttunementOps.AddOrRaise,
                Before = "mods=savage:12|inst=5",
                After = "mods=savage:22|inst=10",
            };

            Assert.AreEqual("  Red Taper: Savage 12 -> Savage 22", DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        // ---- the counter-gated ops ---------------------------------------------------------------------

        /// <summary>
        /// These three hand-built cases set the counter pair, which is now the ONLY evidence the op landed:
        /// with the instability term gone from Render, a successful lock/level/entry leaves Before EQUAL to
        /// After, so without the counters every one of them would report "nothing happened".
        /// </summary>
        [TestMethod]
        public void LockOne_with_no_modifier_change_names_the_lock()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 746, Name = "Hazel Talisman", Op = AttunementOps.LockOne,
                Before = "mods=savage:12", After = "mods=savage:12",
                LocksBefore = 0, LocksAfter = 1,
            };
            Assert.AreEqual("  Hazel Talisman: locked a trait in place",
                DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        [TestMethod]
        public void LevelUp_with_no_modifier_change_names_the_level_raise()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 689, Name = "Iron Scarab", Op = AttunementOps.LevelUp,
                Before = "mods=savage:12", After = "mods=savage:12",
                LevelBefore = 195, LevelAfter = 205,
            };
            Assert.AreEqual("  Iron Scarab: raised the dungeon's level",
                DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        [TestMethod]
        public void AddEntry_with_no_modifier_change_names_the_entry()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 37155, Name = "Mana Scarab", Op = AttunementOps.AddEntry,
                Before = "mods=savage:12", After = "mods=savage:12",
                EntriesBefore = 1, EntriesAfter = 2,
            };
            Assert.AreEqual("  Mana Scarab: added an entry",
                DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        [TestMethod]
        public void RemoveOne_with_nothing_to_remove_names_the_miss()
        {
            var store = Store();
            var dose = new DoseLogEntry
            {
                Wcid = 753, Name = "Brimstone", Op = AttunementOps.RemoveOne,
                Before = "mods=", After = "mods=",
            };
            Assert.AreEqual("  Brimstone: found nothing to strip",
                DungeonGemNarrator.ComposeDoseLine(dose, store.Modifiers));
        }

        // ---- the three ceiling ops, driven through the real RawFragmentRules.Resolve --------------------

        /// <summary>
        /// A fragment carrying nothing but the state under test. Level, mods and locks are all settable
        /// because the three ceilings live in three different places.
        /// </summary>
        private static DungeonGemSpec PressFragment(int level = 195,
            IEnumerable<(string Id, double Magnitude)> mods = null, IEnumerable<string> locks = null)
            => new DungeonGemSpec("any", level, 5, "any", 0, mods, 0, 0, 0, locks, null);

        /// <summary>
        /// Loads exactly one dose of one component and presses it through the real Resolve, returning the dose
        /// line a player would see. These six cases are the LOAD-BEARING coverage for the counter pairs now:
        /// the hand-built entries above can assert the wording, but only a real Resolve can prove the counters
        /// move on success and do not move on the silent no-op branch, and Before == After on both.
        /// </summary>
        private static string PressOneDoseLine(DungeonGemSpec spec, string componentJson, uint wcid, int entries)
        {
            // A SOLO store, because press v2 resolves nine slots: with the full fixture the taper and potion
            // slots would also draw and the log would carry seven entries instead of the one under test.
            var store = SoloStore(componentJson);
            var loaded = RawFragmentRules.Load(spec, Component(store, wcid));
            var limits = new PressLimits(4, DungeonGemSpec.MaxLocks, RawFragmentRules.MaxEntries, DungeonGemSpec.MaxRunLevel);

            RawFragmentRules.Resolve(new PressState(loaded, entries), store.Attunement, store.Modifiers, limits,
                new Random(1), out var log);

            Assert.AreEqual(1, log.Count, "one loaded dose into a one-component store should produce exactly one log entry");
            Assert.AreEqual(log[0].Before, log[0].After,
                "these ops never move the rendered modifier list, which is exactly why the counters exist");

            return DungeonGemNarrator.ComposeDoseLine(log[0], store.Modifiers);
        }

        /// <summary>
        /// The shipped repro: a gem already holding MaxLocks locks takes a Hazel Talisman. LockOne returns
        /// without touching anything, and the old narrator announced a lock the player never got.
        /// </summary>
        [TestMethod]
        public void LockOne_at_the_lock_ceiling_does_not_claim_a_lock()
        {
            var spec = PressFragment(
                mods: new[] { ("savage", 12.0), ("hardy", 1.6) },
                locks: new[] { "savage", "hardy" });

            Assert.AreEqual("  Hazel Talisman: nothing happened", PressOneDoseLine(spec, HazelTalismanJson, 746, entries: 1));
        }

        /// <summary>The same gem below the ceiling: the lock really happens, so the line names it.</summary>
        [TestMethod]
        public void LockOne_below_the_lock_ceiling_claims_the_lock()
        {
            var spec = PressFragment(mods: new[] { ("savage", 12.0) });

            Assert.AreEqual("  Hazel Talisman: locked a trait in place", PressOneDoseLine(spec, HazelTalismanJson, 746, entries: 1));
        }

        /// <summary>
        /// A gem already at the press's level ceiling takes an Iron Scarab: level_up clamps and nothing moves.
        /// The ceiling is the RUN one (DungeonGemSpec.MaxRunLevel) since the 2026-10-08 split, because that is
        /// what FragmentPressStation.BuildLimits now hands the press.
        /// </summary>
        [TestMethod]
        public void LevelUp_at_the_level_ceiling_does_not_claim_a_level_raise()
        {
            var spec = PressFragment(level: DungeonGemSpec.MaxRunLevel);

            Assert.AreEqual("  Iron Scarab: nothing happened", PressOneDoseLine(spec, IronScarabJson, 689, entries: 1));
        }

        [TestMethod]
        public void LevelUp_below_the_level_ceiling_claims_the_level_raise()
        {
            var spec = PressFragment(level: 195);

            Assert.AreEqual("  Iron Scarab: raised the dungeon's level", PressOneDoseLine(spec, IronScarabJson, 689, entries: 1));
        }

        /// <summary>A gem already at MaxEntries takes a Mana Scarab: add_entry clamps and nothing moves.</summary>
        [TestMethod]
        public void AddEntry_at_the_entry_ceiling_does_not_claim_an_entry()
        {
            Assert.AreEqual("  Mana Scarab: nothing happened",
                PressOneDoseLine(PressFragment(), ManaScarabJson, 37155, entries: RawFragmentRules.MaxEntries));
        }

        [TestMethod]
        public void AddEntry_below_the_entry_ceiling_claims_the_entry()
        {
            Assert.AreEqual("  Mana Scarab: added an entry", PressOneDoseLine(PressFragment(), ManaScarabJson, 37155, entries: 1));
        }

        /// <summary>
        /// Press v2's scarab op moves the level and the entry count, neither of which the modifier diff can
        /// see, so it joins the counter-gated group. Either counter moving is enough - a Lead Scarab that
        /// lowers the level without changing the entry count has still done its job.
        /// </summary>
        [TestMethod]
        public void SetDifficulty_claims_its_move_only_when_a_counter_actually_moved()
        {
            // -10 from 195, and 3 entries where the gem already had 5: both counters move.
            Assert.AreEqual("  Lead Scarab: set the dungeon's difficulty",
                PressOneDoseLine(PressFragment(level: 195), LeadScarabJson, 691, entries: 5));

            // Already at level 1 with 3 entries: the level clamps and the entry count is unchanged, so
            // nothing moved and the line must not claim otherwise.
            Assert.AreEqual("  Lead Scarab: nothing happened",
                PressOneDoseLine(PressFragment(level: 1), LeadScarabJson, 691, entries: 3));
        }

        // ---- summary -----------------------------------------------------------------------------------
        //
        // The summary used to print a display name and a bare magnitude ("  Bounteous 1.27"), which named the
        // modifier and said nothing about what it does. It now prints the same wording the item panel does
        // (owner ruling, 2026-09-07), routed through ThreadDungeonGemHandler.RenderEffect rather than through
        // a second wording map here - see Summary_effect_text_is_literally_RenderEffects_own_output, which is
        // the test that stops one being written.

        /// <summary>
        /// THE RULING, on the surface it changed: three modifiers of three different effect kinds, each
        /// carrying what it DOES rather than a number the player has no way to interpret. The lock marker and
        /// the trailing level/entries line are unchanged, and are asserted here so the rewrite cannot quietly
        /// drop either.
        /// </summary>
        [TestMethod]
        public void Summary_describes_each_effect_and_marks_locked_modifiers()
        {
            var store = EffectStore();
            var spec = new DungeonGemSpec("any", 245, 5, "any", 1,
                new List<(string Id, double Magnitude)> { ("bounteous", 1.27), ("affinity_green_garnet", 17.6), ("stalwart", 35) },
                0, 0, 1, new List<string> { "stalwart" }, null);

            var lines = DungeonGemNarrator.ComposeSummary(spec, 3, store.Modifiers);

            Assert.AreEqual("The gem carries:", lines[0]);
            Assert.AreEqual("  Bounteous - kills drop x1.27 loot", lines[1]);
            Assert.AreEqual("  Green Garnet Affinity - 18% of kills leave green garnet to salvage", lines[2]);
            Assert.AreEqual("  Stalwart - monsters have +35 damage resist rating, taking less damage from you (locked)", lines[3]);
            Assert.AreEqual("Level 245, 3 entries.", lines[4]);
            Assert.AreEqual(5, lines.Count);
        }

        /// <summary>
        /// THE ANTI-DRIFT PIN. Every effect line is byte-for-byte the display name, " - ", and RenderEffect's
        /// own output for that modifier at that magnitude. A second wording switch written here - the failure
        /// TryDescribeEffect's doc comment warns about, and the one the reward block was built to undo - would
        /// fail this the moment the two disagreed about any kind, rather than the next time someone read both.
        /// </summary>
        [TestMethod]
        public void Summary_effect_text_is_literally_RenderEffects_own_output()
        {
            var store = EffectStore();
            var mods = new List<(string Id, double Magnitude)>
            {
                ("savage", 22), ("hardy", 1.6), ("stalwart", 35), ("bounteous", 1.27), ("affinity_green_garnet", 17.6),
            };

            var spec = new DungeonGemSpec("any", 245, 5, "any", 1, mods, 0, 0, 1, null, null);
            var lines = DungeonGemNarrator.ComposeSummary(spec, 3, store.Modifiers);

            for (var i = 0; i < mods.Count; i++)
            {
                var def = store.Modifiers[mods[i].Id];

                Assert.AreEqual($"  {def.Display} - {ThreadDungeonGemHandler.RenderEffect(def, mods[i].Magnitude)}",
                    lines[i + 1], $"line for {mods[i].Id}");
            }
        }

        /// <summary>
        /// A modifier the store cannot resolve degrades to RenderEffect's own raw-number fallback rather than
        /// dropping the line. Nothing shipped reaches this - DungeonGemSummaryEffectTests asserts exactly that
        /// against the shipped file - but a gem written by a build that carried a modifier this one does not
        /// must still describe itself rather than throwing.
        /// </summary>
        [TestMethod]
        public void An_unresolvable_modifier_falls_back_to_the_raw_magnitude()
        {
            var store = EffectStore();
            var spec = new DungeonGemSpec("any", 195, 5, "any", 1,
                new List<(string Id, double Magnitude)> { ("ghost_mod", 1.27) }, 0, 0, 1, null, null);

            var lines = DungeonGemNarrator.ComposeSummary(spec, 1, store.Modifiers);

            Assert.AreEqual("  ghost_mod - 1.27", lines[1]);
        }

        [TestMethod]
        public void No_modifiers_summary()
        {
            var store = Store();
            var spec = new DungeonGemSpec("any", 195, 5, "any", 1, null, 0, 0, 1, null, null);

            var lines = DungeonGemNarrator.ComposeSummary(spec, 1, store.Modifiers);

            Assert.AreEqual("The gem carries no modifiers.", lines[0]);
            Assert.AreEqual("Level 195, 1 entry.", lines[1]);
            Assert.AreEqual(2, lines.Count);
        }

        // ---- the selection lines (owner ruling, 2026-09-07) ---------------------------------------------
        //
        // "Players should discover component effects by trial and error, not be told them." The per-dose
        // breakdown the press used to send to chat named every component - the eight the BOARD drew included -
        // and said what each one did, which is a complete readout of the component roster for the price of a
        // few bare pressings. What replaces it names only what the player chose, and no effect at all.

        /// <summary>One drawn dose, enough to make the "remaining slots" line apply.</summary>
        private static IReadOnlyList<DoseLogEntry> WithADrawnSlot()
            => new List<DoseLogEntry> { new DoseLogEntry { Name = "Some Taper", Drawn = true } };

        /// <summary>A log in which every slot was filled by hand.</summary>
        private static IReadOnlyList<DoseLogEntry> NothingDrawn()
            => new List<DoseLogEntry> { new DoseLogEntry { Name = "Red Taper", Drawn = false } };

        [TestMethod]
        public void Nothing_loaded_says_so_in_one_line()
        {
            var store = Store();

            var lines = DungeonGemNarrator.ComposeSelectionLines(new List<(uint, int)>(), store.Attunement.DisplayName,
                WithADrawnSlot());

            Assert.AreEqual(1, lines.Count, "an empty load gets one line, not a list plus a footnote");
            Assert.AreEqual("Components selected: none. All slots drawn at random.", lines[0]);
            Assert.AreEqual(DungeonGemNarrator.NothingSelectedLine, lines[0]);

            // Null is the same case, since a fragment's load can be absent rather than empty.
            Assert.AreEqual(DungeonGemNarrator.NothingSelectedLine,
                DungeonGemNarrator.ComposeSelectionLines(null, store.Attunement.DisplayName, WithADrawnSlot())[0]);
        }

        [TestMethod]
        public void A_partial_load_names_what_was_chosen_and_says_the_rest_were_random()
        {
            var store = Store();

            var lines = DungeonGemNarrator.ComposeSelectionLines(
                new (uint Wcid, int Doses)[] { (1650, 2), (1645, 1) }, store.Attunement.DisplayName, WithADrawnSlot());

            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("Components selected: Red Taper x2, Green Taper", lines[0]);
            Assert.AreEqual("Remaining slots drawn at random.", lines[1]);
            Assert.AreEqual(DungeonGemNarrator.RemainderDrawnLine, lines[1]);
        }

        /// <summary>
        /// A fully loaded fragment drew nothing, so the second line would be a lie and is omitted. Gated on
        /// the dose log's own Drawn flags rather than on a count of loaded doses against nine, because a slot
        /// type the file carries no component for is SKIPPED and never draws.
        /// </summary>
        [TestMethod]
        public void A_full_load_omits_the_remaining_slots_line()
        {
            var store = Store();

            var lines = DungeonGemNarrator.ComposeSelectionLines(
                new (uint Wcid, int Doses)[] { (1650, 1) }, store.Attunement.DisplayName, NothingDrawn());

            Assert.AreEqual(1, lines.Count, "nothing was drawn, so there is no remainder to report");
            Assert.AreEqual("Components selected: Red Taper", lines[0]);
        }

        /// <summary>
        /// THE MULTIPLICITY CONVENTION IS THE PRESSED: LINE'S, not a second one that agrees today: the list is
        /// asserted to be literally RawFragmentRules.ComposePressedLine's output, which is what the fragment
        /// panel prints for the same load.
        /// </summary>
        [TestMethod]
        public void The_selected_list_is_literally_the_Pressed_lines_own_output()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var load = new (uint Wcid, int Doses)[] { (1650, 2), (1643, 1), (687, 1) };

            var lines = DungeonGemNarrator.ComposeSelectionLines(load, store.Attunement.DisplayName, WithADrawnSlot());

            Assert.AreEqual("Components selected: Red Taper x2, Blue Taper, Gold Scarab", lines[0]);
            Assert.AreEqual("Components selected: " + RawFragmentRules.ComposePressedLine(load, store.Attunement),
                lines[0]);
        }

        /// <summary>
        /// THE DISCRIMINATING NEGATIVE, on the real content and through the real Resolve: after a press of a
        /// partly loaded fragment, the lines name the two loaded components and NOTHING else - not one of the
        /// seven components the board drew, and no effect, modifier name or magnitude from any of them.
        ///
        /// Asserted against every drawn dose's own name and every modifier's own display, read off the press
        /// result rather than listed here, so it cannot pass by the roster happening to change.
        /// </summary>
        [TestMethod]
        public void The_selection_lines_never_name_a_drawn_component_or_an_effect()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var spec = Fragment();
            foreach (var wcid in new uint[] { 1650, 687 })
                spec = RawFragmentRules.Load(spec, Component(store, wcid));

            var limits = new PressLimits(4, DungeonGemSpec.MaxLocks, RawFragmentRules.MaxEntries, DungeonGemSpec.MaxRunLevel);
            var result = RawFragmentRules.Resolve(new PressState(spec, 3), store.Attunement, store.Modifiers, limits,
                new Random(99), out var log);

            var lines = DungeonGemNarrator.ComposeSelectionLines(spec.Load, store.Attunement.DisplayName, log);
            var text = string.Join("\n", lines);

            Assert.AreEqual("Components selected: Red Taper, Gold Scarab\nRemaining slots drawn at random.", text);

            // A drawn taper CAN be a second Red Taper, and naming that one is not a leak - the player loaded
            // one. Only a drawn component the player did not choose is evidence of a leak.
            var chosen = new HashSet<string>(log.Where(d => !d.Drawn).Select(d => d.Name), StringComparer.Ordinal);

            foreach (var dose in log.Where(d => d.Drawn && !chosen.Contains(d.Name)))
                Assert.IsFalse(text.Contains(dose.Name), $"drawn component '{dose.Name}' leaked into: {text}");

            Assert.IsTrue(log.Any(d => d.Drawn && !chosen.Contains(d.Name)),
                "guard: this press must actually have drawn a component the player did not choose");

            foreach (var (id, _) in result.Spec.Modifiers)
            {
                Assert.IsFalse(text.Contains(id), $"modifier id '{id}' leaked into: {text}");
                Assert.IsFalse(text.Contains(store.Modifiers[id].Display), $"modifier '{id}' leaked into: {text}");
            }
        }
    }
}
