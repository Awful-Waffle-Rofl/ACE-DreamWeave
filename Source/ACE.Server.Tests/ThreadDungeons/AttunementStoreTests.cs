using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Lint rules 1-22 for Content/dungeons/dynamic/attunement.json (PHASE-2-IMPLEMENTATION-PLAN.md task D2),
    /// one test per rule, plus coverage of the shipped file and the colour table. Rules 19-22 arrived with
    /// press v2 (owner ruling, 2026-09-07): the band edges, the op scope, set_difficulty's entry count, and
    /// the type's load limit against the press board's slot count.
    ///
    /// Rules 9, 17 and 18 went with the instability mechanic (owner ruling, 2026-09-07) and their tests were
    /// DELETED rather than rewritten, because each one's subject no longer exists: rule 9 policed
    /// ComponentDef.Instability, and 17/18 policed the wild op list. The rule numbers are left unfilled on
    /// both sides so the surviving names still line up with the plan doc.
    ///
    /// The "instability" and "wild" keys still present in some fixture JSON below are harmless and are left
    /// alone deliberately: System.Text.Json ignores a property the def type does not declare, so those
    /// fixtures also serve as evidence that a STALE attunement.json still loads rather than being rejected.
    /// </summary>
    [TestClass]
    public class AttunementStoreTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";

        /// <summary>A single "savage" modifier - enough for every rule test that needs a real modifier id.</summary>
        private const string OneModifier =
            "{\"modifiers\":[{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\"," +
            "\"minMagnitude\":1,\"maxMagnitude\":2,\"monsterEffectKind\":\"damage_rating\",\"rewardXpKind\":\"constant\",\"rewardXpBase\":1.0}],\"xpLadder\":[]}";

        private static ThreadDungeonStore Parse(string attunementJson, string modifiersJson = OneModifier)
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, modifiersJson, new Dictionary<string, string>(), attunementJson: attunementJson);

        /// <summary>"savage" plus one salvage affinity, for the rule 23 tests.</summary>
        private const string ModifierAndAffinity =
            "{\"modifiers\":[{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\"," +
            "\"minMagnitude\":1,\"maxMagnitude\":2,\"monsterEffectKind\":\"damage_rating\",\"rewardXpKind\":\"constant\",\"rewardXpBase\":1.0}," +
            "{\"id\":\"affinity_iron\",\"display\":\"Iron Affinity\",\"rarity\":\"uncommon\",\"target\":\"monster\"," +
            "\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":61,\"salvageBaseWcid\":243}],\"xpLadder\":[]}";

        private static string OneComponent(string body) =>
            "{\"version\":1,\"colors\":[],\"components\":[" + body + "],\"wild\":[{\"op\":\"nothing\",\"weight\":1}]}";

        [TestMethod]
        public void Rule1_unsupported_version_ignores_whole_file()
        {
            var json = "{\"version\":2,\"colors\":[],\"components\":[" +
                "{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":10,\"limit\":3,\"instability\":5," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}" +
                "],\"wild\":[]}";
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unsupported version")));
        }

        [TestMethod]
        public void Rule2_missing_file_is_a_diagnostic_and_empty()
        {
            var store = Parse("");
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("attunement.json: empty")));
        }

        [TestMethod]
        public void Rule3_wcid_zero_drops_entry()
        {
            var json = OneComponent("{\"wcid\":0,\"name\":\"Nothing\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("wcid 0")));
        }

        [TestMethod]
        public void Rule4_duplicate_wcid_drops_the_later_entry()
        {
            var json = "{\"version\":1,\"colors\":[],\"components\":[" +
                "{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":10,\"limit\":3,\"instability\":5," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}," +
                "{\"wcid\":1650,\"name\":\"Red Taper Two\",\"type\":\"taper\",\"dose\":10,\"limit\":3,\"instability\":5," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}" +
                "],\"wild\":[]}";
            var store = Parse(json);
            Assert.AreEqual(1, store.Attunement.Components.Count);
            Assert.AreEqual("Red Taper", store.Attunement.Components[1650].Name);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("duplicate component wcid 1650")));
        }

        [TestMethod]
        public void Rule5_unknown_type_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"candle\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown type")));
        }

        [TestMethod]
        public void Rule6_missing_name_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"   \",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("no name")));
        }

        [TestMethod]
        public void Rule7_dose_below_one_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":0,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("dose must be")));
        }

        [TestMethod]
        public void Rule8_limit_below_one_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":0,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("limit must be")));
        }

        [TestMethod]
        public void Rule8_limit_above_max_doses_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":100,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("component 1650 limit must be <= 99")),
                "the diagnostic must name the wcid and the bound; got: " + string.Join(" / ", store.Diagnostics));

            // The bound itself is legal for rule 8 - the entry SURVIVES - and press v2's rule 22 then
            // corrects the surviving limit down to the type's slot count. Rule 8 is what keeps this out of
            // the "Serialize writes a load entry TryParse refuses" territory; rule 22 is what keeps the
            // number honest against the board. Both fire, and the entry lives.
            var atBound = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":99,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var kept = Parse(atBound);
            Assert.AreEqual(1, kept.Attunement.Components.Count);
            Assert.AreEqual(RawFragmentRules.SlotCounts["taper"], kept.Attunement.Components[1650].Limit);
            Assert.IsTrue(kept.Diagnostics.Any(m => m.Contains("slot(s) on the press board")), string.Join("\n", kept.Diagnostics));
        }

        /// <summary>
        /// Rule 22 (press v2): a type's load limit IS its slot count, and a mismatch is CORRECTED rather than
        /// dropped. Both directions matter and both are silent gameplay bugs without it - a limit above the
        /// slot count lets a player load doses no slot will resolve, and one below it stops them filling a
        /// slot the board offers.
        /// </summary>
        [TestMethod]
        public void Rule22_a_limit_that_disagrees_with_the_slot_count_is_corrected()
        {
            foreach (var authored in new[] { 1, 9 })
            {
                var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":" + authored + "," +
                    "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}");
                var store = Parse(json);

                Assert.AreEqual(1, store.Attunement.Components.Count, $"authored limit {authored}");
                Assert.AreEqual(4, store.Attunement.Components[1650].Limit, $"authored limit {authored}");
                Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("does not match the 4 'taper' slot(s)")),
                    string.Join("\n", store.Diagnostics));
            }

            // A component that already agrees with the board raises nothing.
            var clean = Parse(OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":4," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}"));
            Assert.AreEqual(0, clean.Diagnostics.Count, string.Join("\n", clean.Diagnostics));
        }

        /// <summary>
        /// Rule 23 (powder aiming, owner ruling 2026-09-07): only a POWDER may reach a salvage affinity, by
        /// naming one. The powder slot is the salvage slot - it carries the salvage budget - and before this
        /// rule nothing said so: CanLoad's one-powder refusal keys on the component TYPE while the naming
        /// exception keys on the modifier's effect KIND, so a taper naming an affinity would have loaded
        /// beside a powder and put a second affinity on a board with one salvage budget (caught in review,
        /// 2026-09-07).
        ///
        /// Drops the OP, not the entry, so a component with other work left still does that work.
        /// </summary>
        [TestMethod]
        public void Rule23_a_non_powder_may_not_name_a_salvage_affinity()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":4,\"ops\":[" +
                "{\"op\":\"add_or_raise\",\"modifier\":\"affinity_iron\",\"weight\":100}," +
                "{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}");

            var store = Parse(json, ModifierAndAffinity);

            // The whole diagnostic, verbatim. A lint message is read by a content author who has no access to
            // this code, so its exact wording is part of the contract rather than an implementation detail.
            CollectionAssert.Contains(store.Diagnostics.ToList(),
                "attunement.json: component 1650 op 'add_or_raise' reaches a salvage affinity (modifier 'affinity_iron') "
                + "but its type is 'taper', not 'powder'; the powder slot is the salvage slot",
                string.Join("\n", store.Diagnostics));

            // The op is dropped; the component survives on its remaining op.
            Assert.AreEqual(1, store.Attunement.Components.Count);
            var kept = store.Attunement.Components[1650];
            Assert.AreEqual(1, kept.Ops.Count);
            Assert.AreEqual("savage", kept.Ops[0].Modifier);
        }

        /// <summary>
        /// Rule 23 covers BOTH routes to an affinity. Naming one is the obvious route; drawing one through
        /// the salvage SCOPE is the other, and a rule that closed only the first would leave a taper carrying
        /// add_random at scope salvage doing exactly what the rule forbids.
        /// </summary>
        [TestMethod]
        public void Rule23_a_non_powder_may_not_draw_at_the_salvage_scope()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":4,\"ops\":[" +
                "{\"op\":\"add_random\",\"scope\":\"salvage\",\"weight\":100}]}");

            var store = Parse(json, ModifierAndAffinity);

            CollectionAssert.Contains(store.Diagnostics.ToList(),
                "attunement.json: component 1650 op 'add_random' reaches a salvage affinity (scope 'salvage') "
                + "but its type is 'taper', not 'powder'; the powder slot is the salvage slot",
                string.Join("\n", store.Diagnostics));

            // Its only op is gone, so rule 14's entry half drops the component entirely.
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("has no ops left after filtering")),
                string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// THE CONTROL for rule 23, and the reason it is not simply "affinities are banned": a POWDER doing
        /// either of those two things is exactly what the design wants, and must load silently. Without this,
        /// a rule that rejected every affinity op everywhere would pass both tests above.
        /// </summary>
        [TestMethod]
        public void Rule23_a_powder_may_do_both()
        {
            var mapped = Parse(OneComponent("{\"wcid\":790,\"name\":\"Powdered Onyx\",\"type\":\"powder\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"affinity_iron\",\"weight\":100}]}"), ModifierAndAffinity);

            Assert.AreEqual(0, mapped.Diagnostics.Count, string.Join("\n", mapped.Diagnostics));
            Assert.AreEqual(1, mapped.Attunement.Components.Count);

            var wildcard = Parse(OneComponent("{\"wcid\":782,\"name\":\"Powdered Agate\",\"type\":\"powder\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"add_random\",\"scope\":\"salvage\",\"weight\":100}]}"), ModifierAndAffinity);

            Assert.AreEqual(0, wildcard.Diagnostics.Count, string.Join("\n", wildcard.Diagnostics));
            Assert.AreEqual(1, wildcard.Attunement.Components.Count);
        }

        /// <summary>The shipped file obeys rule 23 - which Loads_the_shipped_file's zero-diagnostics
        /// assertion already implies, asserted directly so a future content edit names the reason.</summary>
        [TestMethod]
        public void The_shipped_file_reaches_affinities_only_from_powders()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            foreach (var c in store.Attunement.Components.Values)
            {
                foreach (var op in c.Ops)
                {
                    var namesAffinity = op.Modifier != null && store.Modifiers.TryGetValue(op.Modifier, out var def)
                        && DungeonModifierCategories.IsSalvageAffinity(def);

                    if (namesAffinity || op.Scope == AttunementScopes.Salvage)
                        Assert.AreEqual(RawFragmentRules.PowderType, c.Type,
                            $"component {c.Wcid} ({c.Name}) reaches a salvage affinity but is a '{c.Type}'");
                }
            }
        }

        /// <summary>
        /// Rule 19 (press v2): a band edge is a FRACTION in [0, 1] and bandMin must not exceed bandMax. The
        /// expected authoring mistake is typing a MAGNITUDE ("25") into a band, which would clamp to the
        /// modifier's maximum and silently make the component the strongest in the file, so it drops the
        /// entry rather than being corrected.
        /// </summary>
        [DataTestMethod]
        [DataRow(-0.1, 1.0)]
        [DataRow(0.0, 1.5)]
        [DataRow(5.0, 25.0)]
        [DataRow(0.8, 0.2)]
        public void Rule19_an_illegal_band_drops_the_entry(double bandMin, double bandMax)
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":4,"
                + "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"bandMin\":" + bandMin.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"bandMax\":" + bandMax.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"weight\":100}]}");
            var store = Parse(json);

            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("band")), string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// An omitted band is the whole 0.0-1.0 range - the pre-v2 behaviour - and raises no diagnostic.
        /// </summary>
        [TestMethod]
        public void Rule19_an_omitted_band_defaults_to_the_whole_range()
        {
            var store = Parse(OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":4," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}"));

            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));
            Assert.AreEqual(0.0, store.Attunement.Components[1650].Ops[0].BandMin, 0.0001);
            Assert.AreEqual(1.0, store.Attunement.Components[1650].Ops[0].BandMax, 0.0001);
        }

        /// <summary>
        /// Rule 20 (press v2): an unknown scope would silently read as "any" and let a component reach
        /// modifiers its design never intended - an Oak Talisman that could sharpen the boss.
        /// </summary>
        [TestMethod]
        public void Rule20_an_unknown_scope_drops_the_entry()
        {
            var store = Parse(OneComponent("{\"wcid\":748,\"name\":\"Oak Talisman\",\"type\":\"talisman\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"sharpen\",\"scope\":\"everything\",\"weight\":100}]}"));

            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown scope")), string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// Rule 21 (press v2): a set_difficulty entry count above what a gem can hold. 0 is legal and means
        /// "leave the entry count alone".
        /// </summary>
        [TestMethod]
        public void Rule21_an_out_of_range_entry_count_drops_the_entry()
        {
            var bad = Parse(OneComponent("{\"wcid\":691,\"name\":\"Lead Scarab\",\"type\":\"scarab\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"set_difficulty\",\"amount\":-10,\"entries\":9,\"weight\":100}]}"));

            Assert.AreEqual(0, bad.Attunement.Components.Count);
            Assert.IsTrue(bad.Diagnostics.Any(m => m.Contains("entries 9 must be 0..6")), string.Join("\n", bad.Diagnostics));

            var good = Parse(OneComponent("{\"wcid\":691,\"name\":\"Lead Scarab\",\"type\":\"scarab\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"set_difficulty\",\"amount\":-10,\"entries\":3,\"weight\":100}]}"));

            Assert.AreEqual(1, good.Attunement.Components.Count);
            Assert.AreEqual(0, good.Diagnostics.Count, string.Join("\n", good.Diagnostics));
        }

        /// <summary>
        /// The three retired TYPES are gone from the valid set, so a stale file's glyph, ink or quill is
        /// dropped with a diagnostic rather than loading as a component the board has no slot for.
        /// </summary>
        [DataTestMethod]
        [DataRow("glyph")]
        [DataRow("ink")]
        [DataRow("quill")]
        public void A_retired_component_type_is_now_an_unknown_type(string type)
        {
            var store = Parse(OneComponent("{\"wcid\":37353,\"name\":\"Stale\",\"type\":\"" + type + "\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"raise_all\",\"weight\":100}]}"));

            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown type") && m.Contains(type)),
                string.Join("\n", store.Diagnostics));
        }

        // Rule9_instability_out_of_range_drops_entry was DELETED with the mechanic: ComponentDef no longer
        // has an Instability field for the rule to police. A stale file still carrying one is covered by
        // A_stale_component_carrying_instability_still_loads below.

        /// <summary>
        /// The stripped fields are IGNORED, not rejected. A component row left over from before the removal -
        /// carrying "instability" - must still load, because attunement.json is shipped content and a
        /// deployment can pair a new binary with an older content file.
        /// </summary>
        [TestMethod]
        public void A_stale_component_carrying_instability_still_loads()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":4,\"instability\":101," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":1}]}");
            var store = Parse(json);
            Assert.AreEqual(1, store.Attunement.Components.Count);
            Assert.IsTrue(store.Attunement.TryGet(1650, out _));
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// The two retired ops are gone from AttunementOps.Known, so a file still declaring one now fails
        /// rule 11 and the component is DROPPED with a diagnostic. That is deliberate and is the reason they
        /// were not left behind as silent no-ops: a component that loads and does nothing is worse than one
        /// that visibly refuses.
        /// </summary>
        [DataTestMethod]
        [DataRow("calm")]
        [DataRow("zero_instability")]
        public void A_retired_instability_op_is_now_an_unknown_op(string op)
        {
            var json = OneComponent("{\"wcid\":691,\"name\":\"Lead Scarab\",\"type\":\"scarab\",\"dose\":1,\"limit\":1," +
                "\"ops\":[{\"op\":\"" + op + "\",\"amount\":15,\"weight\":100}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown op") && m.Contains(op)),
                string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Rule10_empty_ops_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("no ops")));
        }

        [TestMethod]
        public void Rule11_unknown_op_drops_the_whole_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"combust\",\"weight\":100}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown op")));
        }

        [TestMethod]
        public void Rule12_op_missing_required_modifier_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"weight\":100}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("requires a modifier")));
        }

        [TestMethod]
        public void Rule13_op_names_unknown_modifier_drops_entry()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"nonexistent\",\"weight\":100}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown modifier")));
        }

        [TestMethod]
        public void Rule14_zero_weight_op_is_dropped_but_entry_survives_with_a_remaining_op()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":0},{\"op\":\"raise_random\",\"weight\":100}]}");
            var store = Parse(json);
            Assert.IsTrue(store.Attunement.TryGet(1650, out var c));
            Assert.AreEqual(1, c.Ops.Count);
            Assert.AreEqual("raise_random", c.Ops[0].Op);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("weight <= 0")));
        }

        [TestMethod]
        public void Rule14_entry_dropped_when_every_op_is_filtered_out()
        {
            var json = OneComponent("{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":1,\"limit\":1,\"instability\":0," +
                "\"ops\":[{\"op\":\"nothing\",\"weight\":0}]}");
            var store = Parse(json);
            Assert.AreEqual(0, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("no ops left after filtering")));
        }

        [TestMethod]
        public void Rule15_unknown_colour_modifier_is_dropped_not_fatal()
        {
            var json = "{\"version\":1,\"colors\":[{\"color\":\"red\",\"modifier\":\"nonexistent\"}],\"components\":[]," +
                "\"wild\":[{\"op\":\"nothing\",\"weight\":1}]}";
            var store = Parse(json);
            Assert.IsFalse(store.Attunement.ColorModifier.ContainsKey("red"));
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown modifier")));
        }

        [TestMethod]
        public void Rule16_disagreeing_limit_within_a_type_is_a_diagnostic_and_the_first_wins()
        {
            var json = "{\"version\":1,\"colors\":[],\"components\":[" +
                "{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"dose\":10,\"limit\":3,\"instability\":5," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}," +
                "{\"wcid\":1643,\"name\":\"Blue Taper\",\"type\":\"taper\",\"dose\":10,\"limit\":5,\"instability\":5," +
                "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}" +
                "],\"wild\":[]}";
            var store = Parse(json);
            Assert.AreEqual(2, store.Attunement.Components.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("disagrees with component 1650")), string.Join("\n", store.Diagnostics));

            // Rule 16 reports the AUTHORING disagreement on its own terms and hands the first entry's limit
            // to the second; press v2's rule 22 then corrects the survivor to the board's slot count. That
            // ordering is deliberate (see ParseAttunement), so the diagnostic above still names the real
            // problem and the value that ends up on both components is the board's.
            Assert.AreEqual(4, store.Attunement.Components[1650].Limit);
            Assert.AreEqual(4, store.Attunement.Components[1643].Limit);
        }

        // Rule17_forbidden_wild_op_is_dropped, Rule17_wild_op_carrying_a_modifier_is_dropped and
        // Rule18_wild_empty_after_filtering_is_a_diagnostic were all DELETED with the wild op list, which no
        // longer exists on AttunementDef. A_stale_file_carrying_a_wild_block_still_loads is what remains
        // relevant from them.

        /// <summary>
        /// A file still carrying the removed top-level "wild" block loads with no diagnostic and the block is
        /// simply not read - the same forwards-compatibility contract as the stripped component field.
        /// </summary>
        [TestMethod]
        public void A_stale_file_carrying_a_wild_block_still_loads()
        {
            var json = "{\"version\":1,\"colors\":[],\"components\":[]," +
                "\"wild\":[{\"op\":\"set_max\",\"modifier\":\"savage\",\"weight\":100},{\"op\":\"nothing\",\"weight\":1}]}";
            var store = Parse(json);
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Loads_the_shipped_file()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));
            Assert.AreEqual(75, store.Attunement.Components.Count);
        }

        /// <summary>
        /// The shipped board: exactly the six press v2 types, and every one of them populated. A type with no
        /// components would leave its slot permanently unresolvable (ResolveSlots skips it), which is the one
        /// content mistake the lint cannot see - every individual component would be valid.
        /// </summary>
        [TestMethod]
        public void The_shipped_file_populates_every_slot_type()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            var byType = store.Attunement.Components.Values
                .GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            CollectionAssert.AreEquivalent(RawFragmentRules.SlotCounts.Keys.ToArray(), byType.Keys.ToArray());

            // The counts the component table was built to (design doc section 1-6).
            Assert.AreEqual(10, byType["scarab"]);
            Assert.AreEqual(18, byType["herb"]);
            Assert.AreEqual(12, byType["powder"]);
            Assert.AreEqual(12, byType["taper"]);
            Assert.AreEqual(12, byType["potion"]);
            Assert.AreEqual(11, byType["talisman"]);
        }

        /// <summary>
        /// EVERY salvage affinity is reachable from the powder slot, and nothing counts the materials.
        ///
        /// The owner's ruling (2026-09-07) split the powders in two: some map 1:1 onto a material the owner
        /// chose, the rest carry a single add_random at the salvage scope and draw from whatever the
        /// modifier table holds. That makes "add a ninth material" one modifiers.json row plus converting one
        /// wildcard powder - so this test is written as a COVERAGE assertion over whatever ships, never as a
        /// count. It fails only in the way that actually matters: an affinity row nothing can reach, which is
        /// the exact defect the retired 1:1 alignment left behind for tourmaline, amethyst and obsidian.
        ///
        /// The wildcards are asserted to exist as well. Without one, an affinity row added later would be
        /// unreachable again the moment it ships, and the mapped powders alone would not say so.
        /// </summary>
        [TestMethod]
        public void The_powder_slot_covers_every_affinity()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            var affinities = store.Modifiers.Values
                .Where(m => m.MonsterEffectKind == DungeonRewardMath.SalvageAffinity)
                .Select(m => m.Id)
                .ToList();

            Assert.IsTrue(affinities.Count > 0, "guard: the shipped file must carry some affinities to cover");

            var powders = store.Attunement.Components.Values.Where(c => c.Type == "powder").ToList();

            var mapped = powders
                .SelectMany(c => c.Ops)
                .Where(o => o.Op == AttunementOps.AddOrRaise && o.Modifier != null)
                .Select(o => o.Modifier)
                .ToList();

            var wildcards = powders
                .Where(c => c.Ops.Any(o => o.Op == AttunementOps.AddRandom && o.Scope == AttunementScopes.Salvage))
                .ToList();

            Assert.IsTrue(wildcards.Count > 0,
                "at least one powder must draw at the salvage scope, or a new affinity row ships unreachable");

            foreach (var id in affinities)
                Assert.IsTrue(mapped.Contains(id) || wildcards.Count > 0, $"{id} is reachable from no powder");

            // A powder maps to at most one material, and no two mapped powders fight over the same one.
            CollectionAssert.AllItemsAreUnique(mapped, "two powders naming one affinity would be duplicates");

            // Every mapped id resolves, and resolves to an affinity - a powder pointing at a monster modifier
            // would load clean and quietly spend the salvage budget on something that is not salvage.
            foreach (var id in mapped)
            {
                Assert.IsTrue(store.Modifiers.TryGetValue(id, out var def), $"powder names unknown modifier {id}");
                Assert.AreEqual(DungeonRewardMath.SalvageAffinity, def.MonsterEffectKind, id);
            }

            // Mapped plus wildcard accounts for every powder: none was left on a retired op shape.
            Assert.AreEqual(powders.Count, mapped.Count + wildcards.Count);
        }

        /// <summary>
        /// REPLACES The_five_inert_components_load_and_carry_only_the_no_op. Press v2 gave the Lead Scarab a
        /// real role (the -10 rung of the difficulty ladder) and retired the four Quills with their type, so
        /// no shipped component is inert any more. The inverse is pinned instead, because "every component
        /// does something" is the property that mattered all along: a slot that draws an inert component
        /// wastes itself, and under the slot board that now happens without the player having chosen it.
        /// </summary>
        [TestMethod]
        public void No_shipped_component_carries_the_no_op()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            foreach (var component in store.Attunement.Components.Values)
                foreach (var op in component.Ops)
                    Assert.AreNotEqual(AttunementOps.Nothing, op.Op,
                        $"{component.Name} ({component.Wcid}) would waste the {component.Type} slot it fills");
        }

        [TestMethod]
        public void Every_colour_maps_to_a_modifier_that_exists_in_modifiers_json()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            foreach (var kv in store.Attunement.ColorModifier)
                Assert.IsTrue(store.Modifiers.ContainsKey(kv.Value), $"colour '{kv.Key}' maps to unknown modifier '{kv.Value}'");
        }

        [TestMethod]
        public void Turquoise_maps_to_no_modifier()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.IsFalse(store.Attunement.ColorModifier.ContainsKey("turquoise"));
        }
    }
}
