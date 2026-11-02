using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads creature-ability catalog and its seeded selection (<see cref="ThreadCreatureAbilities"/>).
    ///
    /// PropertyManager: only <see cref="Read_resolves_every_key_and_folds_in_the_monster_effect_switch"/> reads keys; it
    /// seeds every key it reads and restores each to its compiled default in a finally block. Every other test passes a
    /// <see cref="CreatureAbilitySettings"/> and reads none.
    /// </summary>
    [TestClass]
    public class ThreadCreatureAbilitiesTests
    {
        private static CreatureAbilityFacts Melee(int authored = 0) => new CreatureAbilityFacts { Melee = true, AuthoredRecordCount = authored, BandSpellTier = 5 };
        private static CreatureAbilityFacts Missile() => new CreatureAbilityFacts { Missile = true, BandSpellTier = 5 };
        private static CreatureAbilityFacts Caster() => new CreatureAbilityFacts { Caster = true, BandSpellTier = 5 };
        private static CreatureAbilityFacts Hybrid() => new CreatureAbilityFacts { Hybrid = true, BandSpellTier = 5 };

        private static readonly HashSet<SpellId> DrainFamily = new HashSet<SpellId>(
            SpellLevelProgression.DrainHealth.Concat(SpellLevelProgression.DrainStamina).Concat(SpellLevelProgression.DrainMana));

        private static CreatureAbilityResult Pick(long seed, string family, int budget, CreatureAbilityFacts facts, CreatureAbilitySettings settings = null)
            => ThreadCreatureAbilities.Select(settings ?? CreatureAbilitySettings.Defaults, seed, family, budget, facts);

        private static List<MonsterEffectSpec> Parse(string spec)
        {
            MonsterEffectParser.Parse(spec, out var specs, out var errors);
            Assert.AreEqual(0, errors.Count, "composed spec must parse cleanly: " + spec + " :: " + string.Join(" | ", errors));
            return specs;
        }

        [TestMethod]
        public void Every_default_string_composes_and_passes_the_real_effect_validation()
        {
            var settings = CreatureAbilitySettings.Defaults;

            foreach (var kind in Enum.GetValues(typeof(CreatureAbilityKind)).Cast<CreatureAbilityKind>())
            {
                var records = ThreadCreatureAbilities.Compose(settings, kind, 12345UL, 4);
                Assert.IsNotNull(records, kind + " default must compose");

                var specs = Parse(string.Join("; ", records));

                foreach (var spec in specs)
                {
                    Assert.IsTrue(MonsterEffectRegistry.TryGetHandler(spec.Kind, out var handler), spec.Kind);
                    Assert.IsTrue(handler.Validate(spec, out var error), kind + ": " + spec + " -> " + error);
                }
            }
        }

        [TestMethod]
        public void The_bare_default_strings_parse_as_the_plan_tunables_table_says()
        {
            Assert.AreEqual("avoid pct=0.15 type=melee; riposte pct=0.50 chance=0.20", CreatureAbilitySettings.DefaultRiposte);
            Assert.AreEqual("15,45,90,150", CreatureAbilitySettings.DefaultTiers);

            // draining's tunable is a castspell WITHOUT a spell=; on its own it is not a valid record, by design
            MonsterEffectParser.Parse(CreatureAbilitySettings.DefaultDraining, out var specs, out var errors);
            Assert.AreEqual(0, errors.Count);
            Assert.IsFalse(MonsterEffectRegistry.TryGetHandler("castspell", out var handler) && handler.Validate(specs[0], out _));
        }

        [TestMethod]
        public void Same_inputs_give_the_same_output_and_the_order_of_calls_does_not_matter()
        {
            var first = Pick(777, "olthoi", 150, Hybrid());
            Pick(1, "other", 150, Melee());
            var second = Pick(777, "olthoi", 150, Hybrid());

            Assert.AreEqual(first.Spec, second.Spec);
            CollectionAssert.AreEqual(first.Kinds.ToList(), second.Kinds.ToList());
            Assert.AreEqual(4, first.Kinds.Count);
        }

        [TestMethod]
        public void The_seed_is_a_stable_fnv_of_run_seed_and_family_not_a_runtime_hash_code()
        {
            ulong Reference(string text)
            {
                var hash = 14695981039346656037UL;
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
                {
                    hash ^= b;
                    unchecked { hash *= 1099511628211UL; }
                }
                return hash;
            }

            Assert.AreEqual(Reference("42|drudge"), ThreadCreatureAbilities.StableSeed(42, "drudge"));
            Assert.AreNotEqual(ThreadCreatureAbilities.StableSeed(42, "drudge"), ThreadCreatureAbilities.StableSeed(43, "drudge"));
            Assert.AreNotEqual(ThreadCreatureAbilities.StableSeed(42, "drudge"), ThreadCreatureAbilities.StableSeed(42, "mite"));
        }

        [TestMethod]
        public void Different_seeds_and_families_vary_the_draw()
        {
            var seen = new HashSet<string>();

            for (var seed = 0; seed < 40; seed++)
                seen.Add(string.Join(",", Pick(seed, "f", 45, Hybrid()).Kinds));

            Assert.IsTrue(seen.Count > 3, "the run seed must move the draw, saw " + seen.Count);

            seen.Clear();

            for (var i = 0; i < 40; i++)
                seen.Add(string.Join(",", Pick(9, "family-" + i, 45, Hybrid()).Kinds));

            Assert.IsTrue(seen.Count > 3, "the family key must move the draw, saw " + seen.Count);
        }

        [TestMethod]
        public void Never_a_duplicate_kind_and_the_spec_carries_each_kind_once()
        {
            for (var seed = 0; seed < 300; seed++)
            {
                var result = Pick(seed, "dup", 1000, Hybrid());

                Assert.AreEqual(result.Kinds.Count, result.Kinds.Distinct().Count(), "seed " + seed);

                var records = Parse(result.Spec);
                Assert.AreEqual(result.Kinds.Sum(k => k == CreatureAbilityKind.Riposte ? 2 : 1), records.Count, "seed " + seed);
                Assert.AreEqual(result.Kinds.Contains(CreatureAbilityKind.Leech), records.Any(s => s.Kind == "leech"));
            }
        }

        [TestMethod]
        public void Budget_tiers_buy_one_two_three_four_abilities_and_solo_buys_none()
        {
            Assert.AreEqual(0, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 0));
            Assert.AreEqual(0, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 14));
            Assert.AreEqual(1, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 15));
            Assert.AreEqual(1, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 44));
            Assert.AreEqual(2, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 45));
            Assert.AreEqual(3, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 90));
            Assert.AreEqual(4, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 150));
            Assert.AreEqual(4, ThreadCreatureAbilities.AbilityCount("15,45,90,150", 100000));

            foreach (var (budget, count) in new[] { (0, 0), (14, 0), (15, 1), (44, 1), (45, 2), (89, 2), (90, 3), (149, 3), (150, 4) })
            {
                var result = Pick(5, "tier", budget, Hybrid());
                Assert.AreEqual(count, result.Kinds.Count, "budget " + budget);
                Assert.AreEqual(count == 0, result.Spec.Length == 0);
            }

            // casters get no floor: a caster under the first threshold gets nothing
            Assert.AreEqual(0, Pick(5, "tier", 14, Caster()).Kinds.Count);
            Assert.AreSame(CreatureAbilityResult.Empty, Pick(5, "tier", 0, Caster()));
        }

        [TestMethod]
        public void A_malformed_tier_list_reads_as_the_default()
        {
            foreach (var bad in new[] { null, "", "  ", "abc", "15,15,90", "45,15", "0,10", "-5,10", "15,,45" })
                CollectionAssert.AreEqual(new[] { 15, 45, 90, 150 }, ThreadCreatureAbilities.ParseTiers(bad).ToArray(), "'" + bad + "'");

            CollectionAssert.AreEqual(new[] { 10, 20 }, ThreadCreatureAbilities.ParseTiers(" 10 , 20 ").ToArray());
        }

        [TestMethod]
        public void Caster_only_kinds_need_a_spellbook_and_riposte_needs_a_creature_that_melees()
        {
            var casterOnly = new[] { CreatureAbilityKind.FrenzyCast, CreatureAbilityKind.SpeedCast, CreatureAbilityKind.Draining };

            for (var seed = 0; seed < 200; seed++)
            {
                foreach (var facts in new[] { Melee(), Missile() })
                {
                    var kinds = Pick(seed, "nobook", 1000, facts).Kinds;

                    Assert.IsFalse(kinds.Any(k => casterOnly.Contains(k)), "no spellbook, seed " + seed);
                }

                foreach (var facts in new[] { Missile(), Caster() })
                    Assert.IsFalse(Pick(seed, "nomelee", 1000, facts).Kinds.Contains(CreatureAbilityKind.Riposte), "never melees, seed " + seed);
            }

            // a hybrid has both
            var hybridKinds = new HashSet<CreatureAbilityKind>();
            for (var seed = 0; seed < 200; seed++)
                foreach (var k in Pick(seed, "h", 1000, Hybrid()).Kinds)
                    hybridKinds.Add(k);

            Assert.IsTrue(hybridKinds.Contains(CreatureAbilityKind.Riposte));
            Assert.IsTrue(hybridKinds.Contains(CreatureAbilityKind.Draining));
        }

        [TestMethod]
        public void Kinds_the_creature_already_authors_are_excluded()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var facts = Hybrid();
                facts.AuthoredEffectKinds = new[] { "ramp", "LEECH", "avoid", "castspell" };

                var kinds = Pick(seed, "auth", 1000, facts).Kinds;

                CollectionAssert.DoesNotContain(kinds.ToList(), CreatureAbilityKind.Frenzy);
                CollectionAssert.DoesNotContain(kinds.ToList(), CreatureAbilityKind.FrenzyCast);
                CollectionAssert.DoesNotContain(kinds.ToList(), CreatureAbilityKind.Leech);
                CollectionAssert.DoesNotContain(kinds.ToList(), CreatureAbilityKind.Riposte, "an authored avoid blocks the bundle");
                CollectionAssert.DoesNotContain(kinds.ToList(), CreatureAbilityKind.Draining);
            }

            var onlyRiposte = Melee();
            onlyRiposte.AuthoredEffectKinds = new[] { "riposte" };
            Assert.IsFalse(Pick(3, "r", 1000, onlyRiposte).Kinds.Contains(CreatureAbilityKind.Riposte));
        }

        [TestMethod]
        public void Attack_style_weights_casters_toward_cast_kinds_and_melee_toward_riposte_and_execute()
        {
            const int trials = 3000;
            var casterFavoured = 0;
            var meleeFavoured = 0;
            var meleeSeen = 0;

            for (var seed = 0; seed < trials; seed++)
            {
                var c = Pick(seed, "w", 15, Caster()).Kinds.Single();
                if (c == CreatureAbilityKind.FrenzyCast || c == CreatureAbilityKind.SpeedCast || c == CreatureAbilityKind.Draining)
                    casterFavoured++;

                var m = Pick(seed, "w", 15, Melee()).Kinds.Single();
                meleeSeen++;
                if (m == CreatureAbilityKind.Riposte || m == CreatureAbilityKind.Execute)
                    meleeFavoured++;
            }

            // caster pool: 3 favoured kinds x 3 + 5 neutral = 14 -> 9/14 = 0.64; melee pool: 2 x 3 + 4 neutral = 10 -> 0.60
            Assert.IsTrue(casterFavoured / (double)trials > 0.55, "caster favoured share " + casterFavoured / (double)trials);
            Assert.IsTrue(meleeFavoured / (double)meleeSeen > 0.5, "melee favoured share " + meleeFavoured / (double)meleeSeen);
            Assert.IsTrue(casterFavoured / (double)trials < 0.75, "weighting, not a lock");
        }

        [TestMethod]
        public void The_riposte_bundle_is_always_both_records_with_the_chance_at_or_under_the_cap()
        {
            var seen = 0;

            for (var seed = 0; seed < 400; seed++)
            {
                var result = Pick(seed, "rip", 150, Melee());
                if (!result.Kinds.Contains(CreatureAbilityKind.Riposte))
                    continue;

                seen++;
                var specs = Parse(result.Spec);
                Assert.AreEqual(1, specs.Count(s => s.Kind == "avoid"), "seed " + seed);
                Assert.AreEqual(1, specs.Count(s => s.Kind == "riposte"), "seed " + seed);
                Assert.AreEqual("melee", specs.Single(s => s.Kind == "avoid").GetString("type"));
                Assert.IsTrue(specs.Single(s => s.Kind == "riposte").GetDouble("chance", 1.0) <= 0.20 + 1e-9);
            }

            Assert.IsTrue(seen > 20, "riposte must actually be drawn in this sweep, saw " + seen);
        }

        [TestMethod]
        public void The_riposte_chance_is_clamped_to_the_cap_even_when_the_tunable_asks_for_more_or_omits_it()
        {
            Assert.AreEqual("riposte pct=0.50 chance=0.2", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=0.9", 0.20));
            Assert.AreEqual("riposte pct=0.50 chance=0.1", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=0.1", 0.20));
            Assert.AreEqual("riposte pct=0.50 chance=0.2", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50", 0.20), "the effect's own default is 1.0");
            Assert.AreEqual("riposte pct=0.50 chance=0.2", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=0.9", 0.0), "a non-positive cap reads as the default");
            Assert.AreEqual("riposte pct=0.50 chance=0.2", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=0.9", double.NaN));
            Assert.AreEqual("avoid pct=0.15 type=melee", ThreadCreatureAbilities.ClampRiposteChance("avoid pct=0.15 type=melee", 0.20), "other records are untouched");

            var settings = CreatureAbilitySettings.Defaults;
            settings.Riposte = "avoid pct=0.15 type=melee; riposte pct=0.50 chance=0.95";
            settings.RiposteChanceCap = 0.12;

            var records = ThreadCreatureAbilities.Compose(settings, CreatureAbilityKind.Riposte, 1UL, 3);
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(0.12, Parse(string.Join("; ", records)).Single(s => s.Kind == "riposte").GetDouble("chance", 1.0), 1e-9);
        }

        [TestMethod]
        public void A_riposte_bundle_that_does_not_fit_is_dropped_whole_never_halved()
        {
            // one record of room: a two-record bundle cannot fit, a one-record ability can
            var settings = CreatureAbilitySettings.Defaults;
            settings.MaxPerMonster = 5;

            for (var seed = 0; seed < 300; seed++)
            {
                var result = Pick(seed, "fit", 150, Melee(authored: 4), settings);

                Assert.IsFalse(result.Kinds.Contains(CreatureAbilityKind.Riposte), "seed " + seed);
                Assert.IsTrue(result.Kinds.Count <= 1);
                Assert.IsTrue(Parse(result.Spec).Count <= 1);
            }

            // with room for exactly the bundle, it appears whole or not at all, and the cap holds
            settings.MaxPerMonster = 6;
            var bundles = 0;

            for (var seed = 0; seed < 300; seed++)
            {
                var result = Pick(seed, "fit", 150, Melee(authored: 4), settings);
                var specs = result.Spec.Length == 0 ? new List<MonsterEffectSpec>() : Parse(result.Spec);

                Assert.IsTrue(4 + specs.Count <= 6, "seed " + seed);
                Assert.AreEqual(result.Kinds.Contains(CreatureAbilityKind.Riposte), specs.Any(s => s.Kind == "riposte"));
                Assert.AreEqual(specs.Any(s => s.Kind == "riposte"), specs.Any(s => s.Kind == "avoid"), "never half a bundle");

                if (result.Kinds.Contains(CreatureAbilityKind.Riposte))
                    bundles++;
            }

            Assert.IsTrue(bundles > 0);
        }

        [TestMethod]
        public void A_creature_with_no_record_room_gets_nothing_and_the_default_cap_is_respected()
        {
            Assert.AreSame(CreatureAbilityResult.Empty, Pick(1, "full", 150, Melee(authored: 12)));
            Assert.AreSame(CreatureAbilityResult.Empty, Pick(1, "over", 150, Melee(authored: 30)));

            for (var seed = 0; seed < 100; seed++)
            {
                var result = Pick(seed, "room", 150, Hybrid(), null);
                Assert.IsTrue(Parse(result.Spec).Count <= 12);
            }
        }

        [TestMethod]
        public void A_non_positive_per_monster_cap_means_no_cap_like_the_engine()
        {
            var settings = CreatureAbilitySettings.Defaults;
            settings.MaxPerMonster = 0;

            var result = Pick(5, "uncapped", 150, Hybrid(), settings);
            Assert.AreEqual(4, result.Kinds.Count);

            var crowded = Hybrid();
            crowded.AuthoredRecordCount = 40;
            Assert.AreEqual(4, Pick(5, "uncapped", 150, crowded, settings).Kinds.Count, "authored records do not consume a cap that does not exist");

            settings.MaxPerMonster = -1;
            Assert.AreEqual(4, Pick(5, "uncapped", 150, crowded, settings).Kinds.Count);
        }

        [TestMethod]
        public void The_riposte_chance_never_rounds_above_the_cap_and_a_non_finite_cap_is_the_fallback()
        {
            foreach (var cap in new[] { 0.12345678, 0.19999999, 0.0000123, 0.2, 0.9999999 })
            {
                var text = ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=1", cap);
                var written = double.Parse(text.Substring(text.IndexOf("chance=") + 7), System.Globalization.CultureInfo.InvariantCulture);

                Assert.IsTrue(written <= cap, text + " vs cap " + cap);
            }

            Assert.AreEqual("riposte pct=0.50 chance=0.2", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=0.9", double.PositiveInfinity));
            Assert.AreEqual("riposte pct=0.50 chance=0.2", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=0.9", double.NegativeInfinity));
            Assert.AreEqual("riposte pct=0.50 chance=1", ThreadCreatureAbilities.ClampRiposteChance("riposte pct=0.50 chance=2", 5.0), "a cap above 1 clamps to 1");
        }
        [TestMethod]
        public void Draining_only_ever_yields_a_drain_family_spell_at_a_clamped_tier()
        {
            var healthSeen = false;
            var staminaSeen = false;
            var manaSeen = false;

            for (var seed = 0UL; seed < 300UL; seed++)
            {
                foreach (var tunable in new long[] { 0, 1, 5, 8, 9, 1000, -3 })
                {
                    foreach (var band in new[] { 0, 1, 4, 8, 12, -2 })
                    {
                        var spell = ThreadCreatureAbilities.ChooseDrainSpell(seed, tunable, band);

                        Assert.IsTrue(DrainFamily.Contains(spell), spell.ToString());

                        var tier = tunable > 0 ? Math.Min(tunable, 8) : Math.Max(1, Math.Min(band, 8));
                        Assert.IsTrue(spell.ToString().EndsWith(tier.ToString()), spell + " vs tier " + tier);
                    }
                }

                var s = ThreadCreatureAbilities.ChooseDrainSpell(seed, 0, 3);
                healthSeen |= SpellLevelProgression.DrainHealth.Contains(s);
                staminaSeen |= SpellLevelProgression.DrainStamina.Contains(s);
                manaSeen |= SpellLevelProgression.DrainMana.Contains(s);
            }

            Assert.IsTrue(healthSeen && staminaSeen && manaSeen, "the seeded draw reaches all three drain families");
        }

        [TestMethod]
        public void A_composed_draining_record_names_a_drain_spell_and_drops_any_authored_spell()
        {
            var settings = CreatureAbilitySettings.Defaults;
            settings.Draining = "castspell chance=0.10 spell=FireBolt8";

            var drawn = 0;

            for (var seed = 0; seed < 300; seed++)
            {
                var result = Pick(seed, "drain", 150, Caster(), settings);
                if (!result.Kinds.Contains(CreatureAbilityKind.Draining))
                    continue;

                drawn++;
                var cast = Parse(result.Spec).Single(s => s.Kind == "castspell");
                var spell = cast.GetEnum("spell", SpellId.Undef);

                Assert.IsTrue(DrainFamily.Contains(spell), "castspell spell=" + spell);
                Assert.AreEqual(1, Parse(result.Spec).Count(s => s.Kind == "castspell"));
                Assert.AreEqual("hit,spellhit", cast.GetString("on"));
                Assert.AreEqual(0.10, cast.GetDouble("chance", 0), 1e-9);
            }

            Assert.IsTrue(drawn > 20);

            // a tunable tier moves every spell to that tier; 0 follows the band
            var tiered = CreatureAbilitySettings.Defaults;
            tiered.DrainingTier = 7;
            var spec = ThreadCreatureAbilities.Compose(tiered, CreatureAbilityKind.Draining, 4UL, 2);
            StringAssert.Matches(spec.Single(), new System.Text.RegularExpressions.Regex(@"spell=Drain(Health|Stamina|Mana)7 "));

            var banded = ThreadCreatureAbilities.Compose(CreatureAbilitySettings.Defaults, CreatureAbilityKind.Draining, 4UL, 2);
            StringAssert.Matches(banded.Single(), new System.Text.RegularExpressions.Regex(@"spell=Drain(Health|Stamina|Mana)2 "));
        }

        [TestMethod]
        public void A_disabled_layer_and_an_unusable_tunable_attach_nothing()
        {
            var off = CreatureAbilitySettings.Defaults;
            off.Enabled = false;
            Assert.AreSame(CreatureAbilityResult.Empty, Pick(1, "off", 1000, Hybrid(), off));

            var broken = CreatureAbilitySettings.Defaults;
            broken.Leech = "leech pct=0";
            broken.Execute = "";
            broken.Speed = "flatdamage type=fire amount=30 chance=0.35";
            broken.Scattering = "nonsense x=1";
            broken.Frenzy = "ramp axis=attackspeed";
            broken.Riposte = "riposte pct=0.5 chance=0.2";

            for (var seed = 0; seed < 100; seed++)
            {
                var kinds = Pick(seed, "broken", 1000, Hybrid(), broken).Kinds;

                foreach (var gone in new[] { CreatureAbilityKind.Leech, CreatureAbilityKind.Execute, CreatureAbilityKind.Speed, CreatureAbilityKind.Scattering, CreatureAbilityKind.Frenzy, CreatureAbilityKind.Riposte })
                    CollectionAssert.DoesNotContain(kinds.ToList(), gone, gone + " is misconfigured");
            }
        }

        [TestMethod]
        public void Compiled_defaults_match_the_registered_property_defaults()
        {
            var bools = DefaultPropertyManager.DefaultBooleanProperties;
            var longs = DefaultPropertyManager.DefaultLongProperties;
            var doubles = DefaultPropertyManager.DefaultDoubleProperties;
            var strings = DefaultPropertyManager.DefaultStringProperties;

            Assert.AreEqual(CreatureAbilitySettings.DefaultEnabled, bools["dynamic_dungeons_creature_abilities"].Item);
            Assert.IsTrue(bools["dynamic_dungeons_creature_abilities"].Item, "new settings ship ON");
            Assert.AreEqual(CreatureAbilitySettings.DefaultDrainingTier, longs["dynamic_dungeons_creature_ability_draining_tier"].Item);
            Assert.AreEqual(CreatureAbilitySettings.DefaultRiposteChanceCap, doubles["dynamic_dungeons_creature_ability_riposte_chance_cap"].Item, 0.0);
            Assert.AreEqual(0.20, doubles["dynamic_dungeons_creature_ability_riposte_chance_cap"].Item, 0.0);

            var expected = new Dictionary<string, string>
            {
                ["tiers"] = CreatureAbilitySettings.DefaultTiers,
                ["execute"] = CreatureAbilitySettings.DefaultExecute,
                ["leech"] = CreatureAbilitySettings.DefaultLeech,
                ["frenzy"] = CreatureAbilitySettings.DefaultFrenzy,
                ["frenzy_cast"] = CreatureAbilitySettings.DefaultFrenzyCast,
                ["speed"] = CreatureAbilitySettings.DefaultSpeed,
                ["speed_cast"] = CreatureAbilitySettings.DefaultSpeedCast,
                ["riposte"] = CreatureAbilitySettings.DefaultRiposte,
                ["draining"] = CreatureAbilitySettings.DefaultDraining,
                ["scattering"] = CreatureAbilitySettings.DefaultScattering,
            };

            foreach (var pair in expected)
                Assert.AreEqual(pair.Value, strings["dynamic_dungeons_creature_ability_" + pair.Key].Item, pair.Key);
        }

        [TestMethod]
        public void Read_resolves_every_key_and_folds_in_the_monster_effect_switch()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_creature_abilities", true));
                Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", true));
                Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_max_per_monster", 7));
                Assert.IsTrue(PropertyManager.ModifyLong("dynamic_dungeons_creature_ability_draining_tier", 6));
                Assert.IsTrue(PropertyManager.ModifyDouble("dynamic_dungeons_creature_ability_riposte_chance_cap", 0.11));

                foreach (var suffix in new[] { "tiers", "execute", "leech", "frenzy", "frenzy_cast", "speed", "speed_cast", "riposte", "draining", "scattering" })
                    Assert.IsTrue(PropertyManager.ModifyString("dynamic_dungeons_creature_ability_" + suffix, "seeded-" + suffix), suffix);

                var read = CreatureAbilitySettings.Read();

                Assert.IsTrue(read.Enabled);
                Assert.AreEqual(7, read.MaxPerMonster);
                Assert.AreEqual(6, read.DrainingTier);
                Assert.AreEqual(0.11, read.RiposteChanceCap, 1e-12);
                Assert.AreEqual("seeded-tiers", read.Tiers);
                Assert.AreEqual("seeded-execute", read.Execute);
                Assert.AreEqual("seeded-leech", read.Leech);
                Assert.AreEqual("seeded-frenzy", read.Frenzy);
                Assert.AreEqual("seeded-frenzy_cast", read.FrenzyCast);
                Assert.AreEqual("seeded-speed", read.Speed);
                Assert.AreEqual("seeded-speed_cast", read.SpeedCast);
                Assert.AreEqual("seeded-riposte", read.Riposte);
                Assert.AreEqual("seeded-draining", read.Draining);
                Assert.AreEqual("seeded-scattering", read.Scattering);

                Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
                Assert.IsFalse(CreatureAbilitySettings.Read().Enabled, "monster_effects_enabled off makes the layer inert");
            }
            finally
            {
                PropertyManager.ModifyBool("dynamic_dungeons_creature_abilities", CreatureAbilitySettings.DefaultEnabled);
                PropertyManager.ModifyBool("monster_effects_enabled", true);
                PropertyManager.ModifyLong("monster_effect_max_per_monster", CreatureAbilitySettings.DefaultMaxPerMonster);
                PropertyManager.ModifyLong("dynamic_dungeons_creature_ability_draining_tier", CreatureAbilitySettings.DefaultDrainingTier);
                PropertyManager.ModifyDouble("dynamic_dungeons_creature_ability_riposte_chance_cap", CreatureAbilitySettings.DefaultRiposteChanceCap);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_tiers", CreatureAbilitySettings.DefaultTiers);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_execute", CreatureAbilitySettings.DefaultExecute);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_leech", CreatureAbilitySettings.DefaultLeech);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_frenzy", CreatureAbilitySettings.DefaultFrenzy);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_frenzy_cast", CreatureAbilitySettings.DefaultFrenzyCast);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_speed", CreatureAbilitySettings.DefaultSpeed);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_speed_cast", CreatureAbilitySettings.DefaultSpeedCast);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_riposte", CreatureAbilitySettings.DefaultRiposte);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_draining", CreatureAbilitySettings.DefaultDraining);
                PropertyManager.ModifyString("dynamic_dungeons_creature_ability_scattering", CreatureAbilitySettings.DefaultScattering);
            }
        }
    }
}
