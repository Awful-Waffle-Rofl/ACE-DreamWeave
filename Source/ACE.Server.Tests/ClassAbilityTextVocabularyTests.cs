using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Parser and render tests for the phase 3 vocabulary of ClassAbilityText: single-rank selector (@N / @max),
    /// {rank}, join and unit options, the f1 / f2 / pct1 / pct3 / min formatters, the {an} article, per-rank
    /// tails, the {fn:} formula hook and the cap argument of {v} / {ranks}. Everything reads a
    /// DictionaryTunableValues or the code defaults, never PropertyManager, so the class is isolated.
    /// </summary>
    [TestClass]
    public class ClassAbilityTextVocabularyTests
    {
        private const string EchoBase = "class_ability_echocast_chance_base";      // double 0.08
        private const string EchoStep = "class_ability_echocast_chance_step";      // double 0.08
        private const string KeptPrefix = "class_ability_exsanguinate_charges_kept_r";   // long 2/3/4
        private const string ReserveCapR1 = "class_ability_bloodmage_charge_stack_cap_r1";   // long 3

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static ITunableValues Defaults => new DictionaryTunableValues(new Dictionary<string, double>());

        private static ITunableValues With(params (string Key, double Value)[] overrides) =>
            new DictionaryTunableValues(overrides.ToDictionary(o => o.Key, o => o.Value));

        private static ClassAbilityDefinition Probe(string description, RankTailSpec tail = null, int maxRank = 3) =>
            new ClassAbilityDefinition { Name = "probe", DisplayName = "Probe", MaxRank = maxRank, DescriptionTemplate = description, RankTailTemplate = tail };

        private static string Desc(string template, ITunableValues values = null) =>
            ClassAbilityText.Describe(Probe(template), values ?? Defaults);

        // ---------------------------------------------------------------------------------------------
        // 1. single-rank selector
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Selector_RendersOneRanksValue_ForPerLadderRanksAndFn()
        {
            Assert.AreEqual("24", Desc("{per:" + EchoBase + "@max|pct}"));
            Assert.AreEqual("16", Desc("{per:" + EchoBase + "@2|pct}"));
            Assert.AreEqual("8", Desc("{per:" + EchoBase + "@1|pct}"));
            Assert.AreEqual("24", Desc("{ladder:" + EchoBase + "," + EchoStep + "@max|pct}"));
            Assert.AreEqual("3", Desc("{ranks:" + KeptPrefix + "@2|int}"));
            Assert.AreEqual("4", Desc("{ranks:" + KeptPrefix + "@max|int}"));

            // the full ladder is unchanged beside it
            Assert.AreEqual("8/16/24", Desc("{per:" + EchoBase + "|pct}"));
        }

        [TestMethod]
        public void Selector_OutOfRangeOrMalformed_IsAParseError()
        {
            foreach (var bad in new[]
            {
                "{per:" + EchoBase + "@4|pct}",       // MaxRank is 3
                "{per:" + EchoBase + "@3|pct}",       // the top rank must be written @max
                "{per:" + EchoBase + "@01|pct}",      // leading zero
                "{per:" + EchoBase + "@02|pct}",
                "{per:" + EchoBase + "@0|pct}",
                "{per:" + EchoBase + "@-1|pct}",
                "{per:" + EchoBase + "@|pct}",
                "{per:" + EchoBase + "@maxx|pct}",
                "{per:" + EchoBase + "@2x|pct}",
                "{v:" + EchoBase + "@1|pct}",         // not rank-shaped
                "{maxrank@1}",
            })
            {
                var def = Probe(bad);
                Assert.Throws<FormatException>(() => ClassAbilityText.Describe(def, Defaults), bad);
                Assert.Throws<FormatException>(() => ClassAbilityText.Keys(def), bad);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 2. {rank}
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void RankPlaceholder_RendersTheRankInATail_AndIsAParseErrorInADescription()
        {
            var def = Probe("Adds arrows.", "+1 arrow (for {rank} extra arrows total)");

            Assert.AreEqual("+1 arrow (for 1 extra arrows total)", ClassAbilityText.RankTail(def, 1, Defaults));
            Assert.AreEqual("+1 arrow (for 3 extra arrows total)", ClassAbilityText.RankTail(def, 3, Defaults));
            CollectionAssert.AreEqual(new[] { "Rank 1: +1 arrow (for 1 extra arrows total)", "Rank 2: +1 arrow (for 2 extra arrows total)", "Rank 3: +1 arrow (for 3 extra arrows total)" },
                ClassAbilityText.RankLines(def, Defaults).ToArray());

            var bad = Probe("Rank {rank} does this.");
            Assert.Throws<FormatException>(() => ClassAbilityText.Describe(bad, Defaults));
            Assert.Throws<FormatException>(() => ClassAbilityText.Keys(bad));

            Assert.Throws<FormatException>(() => Desc("{rank|int}"), "{rank} takes no format");
        }

        // ---------------------------------------------------------------------------------------------
        // 3. join and unit options
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Join_Styles_RenderTheDocumentedStrings_AndDefaultIsSlash()
        {
            Assert.AreEqual("8/16/24", Desc("{per:" + EchoBase + "|pct}"));
            Assert.AreEqual("8/16/24", Desc("{per:" + EchoBase + "|pct;join=slash}"));
            Assert.AreEqual("8 / 16 / 24", Desc("{per:" + EchoBase + "|pct;join=spaced}"));
            Assert.AreEqual("0.5, then 1, then 1.5", Desc("{per:class_ability_cloakedinpower_crit_window_r1|num;join=then}"));
            Assert.AreEqual("0.5, then 1.0, then 1.5", Desc("{ranks:class_ability_cloakedinpower_crit_window_r|f1;join=then}"));
        }

        [TestMethod]
        public void Unit_AppendsToEveryValue()
        {
            Assert.AreEqual("8% / 16% / 24%", Desc("{per:" + EchoBase + "|pct;join=spaced;unit=%}"));
            Assert.AreEqual("8%/16%/24%", Desc("{per:" + EchoBase + "|pct;unit=%}"));
            Assert.AreEqual("8%", Desc("{v:" + EchoBase + "|pct;unit=%}"));
            Assert.AreEqual("24%", Desc("{per:" + EchoBase + "@max|pct;unit=%}"));
        }

        [TestMethod]
        public void Options_RejectAnythingOutsideTheClosedSet()
        {
            foreach (var bad in new[]
            {
                "{per:" + EchoBase + "|pct;join=comma}",
                "{per:" + EchoBase + "|pct;join=}",
                "{per:" + EchoBase + "|pct;join}",
                "{per:" + EchoBase + "|pct;join=then;join=spaced}",
                "{per:" + EchoBase + "|pct;sep=x}",
                "{per:" + EchoBase + "|pct;unit=#}",
                "{per:" + EchoBase + "@max|pct;join=spaced}",     // nothing to join
                "{v:" + EchoBase + "|pct;join=spaced}",           // not rank-shaped
                "{maxrank;join=spaced}",
            })
            {
                Assert.Throws<FormatException>(() => Desc(bad), bad);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 4. formatters
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void NewFormatters_RenderTheExactStringsTheAbilitiesNeed()
        {
            Assert.AreEqual("2.00", ClassAbilityText.Format(2.0, "f2"));
            Assert.AreEqual("2.50", ClassAbilityText.Format(2.5, "f2"));
            Assert.AreEqual("3.10", ClassAbilityText.Format(3.1, "f2"));
            Assert.AreEqual("0.5", ClassAbilityText.Format(0.5, "f1"));
            Assert.AreEqual("1.0", ClassAbilityText.Format(1.0, "f1"));
            Assert.AreEqual("0.0", ClassAbilityText.Format(-0.0, "f1"));
            Assert.AreEqual("28.5", ClassAbilityText.Format(0.285, "pct1"));
            Assert.AreEqual("10.0", ClassAbilityText.Format(0.1, "pct1"));
            Assert.AreEqual("0.025", ClassAbilityText.Format(0.00025, "pct3"));
            Assert.AreEqual("0.05", ClassAbilityText.Format(0.0005, "pct3"));
            Assert.AreEqual("0.075", ClassAbilityText.Format(0.00075, "pct3"));
            Assert.AreEqual("6", ClassAbilityText.Format(360, "min"));
            Assert.AreEqual("4", ClassAbilityText.Format(240, "min"));
            Assert.AreEqual("2", ClassAbilityText.Format(120, "min"));
            Assert.AreEqual("1.5", ClassAbilityText.Format(90, "min"));
            Assert.AreEqual("1.67", ClassAbilityText.Format(100, "min"), "min is for whole or half minutes; anything else shows 2 decimals");
            Assert.AreEqual("0.75", ClassAbilityText.Format(45, "min"));

            // uint never renders negative zero
            Assert.AreEqual("0", ClassAbilityText.Format(-0.0, "uint"));
            Assert.AreEqual("0", ClassAbilityText.Format(-0.4, "uint"));
            Assert.AreEqual("5", ClassAbilityText.Format(4.6, "uint"));

            // the two ladders the check named
            Assert.AreEqual("6/4/2", Desc("{ladder:class_ability_souljump_cooldown_seconds_base,class_ability_souljump_cooldown_seconds_step|min}"));
            Assert.AreEqual("2.00/2.50/3.10", Desc("{ranks:class_ability_weakenedblood_resist_r|f2}"));
            Assert.AreEqual("0.025%, then 0.05%, then 0.075%",
                Desc("{per:class_ability_battlehardened_reduction_per_strength_per_rank|pct3;join=then;unit=%}"));

            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.AreEqual("2.50", ClassAbilityText.Format(2.5, "f2"));
                Assert.AreEqual("28.5", ClassAbilityText.Format(0.285, "pct1"));
                Assert.AreEqual("1.5", ClassAbilityText.Format(90, "min"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 5. article
        // ---------------------------------------------------------------------------------------------

        [DataTestMethod]
        [DataRow("1", "a")]
        [DataRow("8", "an")]
        [DataRow("11", "an")]
        [DataRow("18", "an")]
        [DataRow("80", "an")]
        [DataRow("88", "an")]
        [DataRow("100", "a")]
        [DataRow("110", "a")]
        [DataRow("800", "an")]
        [DataRow("8.5", "an")]
        [DataRow("0.5", "a")]
        [DataRow("8000", "an")]
        [DataRow("-8", "a")]          // "minus eight"
        [DataRow("-11", "a")]
        [DataRow("+8", "a")]          // "plus eight"
        [DataRow("180", "a")]         // "one hundred eighty"
        [DataRow("899", "an")]
        [DataRow("1100", "a")]        // read "one thousand one hundred" (thousands grouping), not "eleven hundred"
        [DataRow("1800", "a")]
        [DataRow("0.8", "a")]         // "zero point eight"
        [DataRow("11000", "an")]
        [DataRow("18000", "an")]
        [DataRow("10", "a")]
        [DataRow("12", "a")]
        [DataRow("19", "a")]
        [DataRow("85", "an")]
        [DataRow("8/16/24", "an")]
        [DataRow("16/24", "a")]
        [DataRow("2x", "a")]
        [DataRow("", "a")]
        public void Article_FollowsTheEnglishReadingOfTheNumeral(string rendered, string expected)
        {
            Assert.AreEqual(expected, ClassAbilityText.Article(rendered));
        }

        [TestMethod]
        public void ArticlePlaceholder_ReadsTheFollowingPlaceholder_AndKeepsCapitalisation()
        {
            Assert.AreEqual("with an 8/16/24% chance", Desc("with {an} {per:" + EchoBase + "|pct}% chance"));
            Assert.AreEqual("with an8% chance", Desc("with {an}{v:" + EchoBase + "|pct}% chance"), "an adjacent {an} adds no space of its own");
            Assert.AreEqual("An 8% chance", Desc("{An} {v:" + EchoBase + "|pct}% chance"));
            Assert.AreEqual("with a 16/24% chance", Desc("with {an} {per:" + EchoBase + "@2|pct}/24% chance"));

            // a retune moves the article with the number
            Assert.AreEqual("with a 10/20/30% chance", Desc("with {an} {per:" + EchoBase + "|pct}% chance", With((EchoBase, 0.10))));
            Assert.AreEqual("with an 11/22/33% chance", Desc("with {an} {per:" + EchoBase + "|pct}% chance", With((EchoBase, 0.11))));
            Assert.AreEqual("A 100% chance", Desc("{An} {v:" + EchoBase + "|pct}% chance", With((EchoBase, 1.0))));
            Assert.AreEqual("An 8.5% chance", Desc("{An} {v:" + EchoBase + "|pct}% chance", With((EchoBase, 0.085))));
            Assert.AreEqual("a 0.5% chance", Desc("{an} {v:" + EchoBase + "|pct}% chance", With((EchoBase, 0.005))));

            // before {maxrank}: "an 3" is not reached, "a 3"
            Assert.AreEqual("a 3 rank ability", Desc("{an} {maxrank} rank ability"));
        }

        [TestMethod]
        public void ArticlePlaceholder_WithoutAFollowingPlaceholder_IsAParseError()
        {
            foreach (var bad in new[]
            {
                "{an}",
                "ends with {an}",
                "{an} word {v:" + EchoBase + "|pct}",
                "{an}  x {v:" + EchoBase + "|pct}",
                "{an}{an}{v:" + EchoBase + "|pct}",
                "{an|pct} {v:" + EchoBase + "|pct}",
                "{an:x} {v:" + EchoBase + "|pct}",
                "{AN} {v:" + EchoBase + "|pct}",
            })
            {
                var def = Probe(bad);
                Assert.Throws<FormatException>(() => ClassAbilityText.Describe(def, Defaults), bad);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 6. per-rank tails
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void PerRankTails_RenderOneTemplatePerRank_AndASingleStringStillWorks()
        {
            var perRank = Probe("Spreads.", new[]
            {
                "spreads to {per:class_ability_netherbloom_jumps_per_rank|floor} nearby enemy",
                "spreads to {per:class_ability_netherbloom_jumps_per_rank|floor} nearby enemies",
                "spreads to {per:class_ability_netherbloom_jumps_per_rank|floor} nearby enemies (replaces rank {rank})",
            });

            Assert.AreEqual("spreads to 1 nearby enemy", ClassAbilityText.RankTail(perRank, 1, Defaults));
            Assert.AreEqual("spreads to 2 nearby enemies", ClassAbilityText.RankTail(perRank, 2, Defaults));
            Assert.AreEqual("spreads to 3 nearby enemies (replaces rank 3)", ClassAbilityText.RankTail(perRank, 3, Defaults));
            Assert.AreEqual("Probe - Spreads. This token teaches rank 2: spreads to 2 nearby enemies.", ClassAbilityText.TokenLongDesc(perRank, 2, Defaults));
            Assert.AreEqual(3, ClassAbilityText.RankLines(perRank, Defaults).Count);
            CollectionAssert.AreEqual(new[] { "class_ability_netherbloom_jumps_per_rank" }, ClassAbilityText.Keys(perRank).ToArray());

            var single = Probe("Spreads.", "same for every rank, rank {rank}");
            Assert.AreEqual("same for every rank, rank 2", ClassAbilityText.RankTail(single, 2, Defaults));
        }

        [TestMethod]
        public void PerRankTails_WithTheWrongLength_AreAnError()
        {
            var def = Probe("Spreads.", new[] { "one", "two" });

            Assert.Throws<FormatException>(() => ClassAbilityText.RankTail(def, 1, Defaults));
            Assert.Throws<FormatException>(() => ClassAbilityText.Keys(def));
        }

        [TestMethod]
        public void PerRankTails_AreOnlyUsedByTheAbilitiesThatDeclareThem_AndMatchMaxRank()
        {
            foreach (var def in ClassAbilityRegistry.Abilities.Values.Where(d => d.RankTailTemplate != null && d.RankTailTemplate.IsPerRank))
                Assert.AreEqual(def.MaxRank, def.RankTailTemplate.All(def.MaxRank, def.Name).Count, def.Name);
        }

        // ---------------------------------------------------------------------------------------------
        // 7. formula hook
        // ---------------------------------------------------------------------------------------------

        private static ClassAbilityDefinition FormulaProbe(string description, string tail = null)
        {
            var def = Probe(description, tail);
            def.TextFormulas = new Dictionary<string, TextFormula>
            {
                ["triple"] = new TextFormula((v, rank) => 3.0 * rank),
                ["half"] = new TextFormula((v, rank) => 0.5 * rank),
                ["nan"] = new TextFormula((v, rank) => double.NaN),
                ["inf"] = new TextFormula((v, rank) => double.PositiveInfinity),
                ["keyed"] = new TextFormula((v, rank) => ClassAbilityText.Read(v, EchoBase) * rank, EchoBase),
            };
            return def;
        }

        [TestMethod]
        public void Fn_RendersTheNamedFormula_WithSelectorJoinAndUnit()
        {
            var def = FormulaProbe("{fn:triple|int}|{fn:triple@max|int}|{fn:triple@2|int}|{fn:half|f1;join=then}|{fn:half|pct;join=spaced;unit=%}", "{fn:triple|int}");

            Assert.AreEqual("3/6/9|9|6|0.5, then 1.0, then 1.5|50% / 100% / 150%", ClassAbilityText.Describe(def, Defaults));
            Assert.AreEqual("6", ClassAbilityText.RankTail(def, 2, Defaults));
            CollectionAssert.AreEqual(new[] { "triple", "half" }, ClassAbilityText.FormulaNames(def).ToArray());
            Assert.AreEqual(0, ClassAbilityText.Keys(def).Count, "a formula names no tunable key");
        }

        [TestMethod]
        public void Fn_WithoutAnEntry_IsAnError()
        {
            var noFormulas = Probe("{fn:triple|int}");
            Assert.Throws<FormatException>(() => ClassAbilityText.Describe(noFormulas, Defaults));
            Assert.Throws<FormatException>(() => ClassAbilityText.Keys(noFormulas));

            var wrongName = FormulaProbe("{fn:quadruple|int}");
            Assert.Throws<FormatException>(() => ClassAbilityText.Describe(wrongName, Defaults));

            Assert.Throws<FormatException>(() => Desc("{fn|int}"));
            Assert.Throws<FormatException>(() => Desc("{fn:a,b|int}"));
        }

        [TestMethod]
        public void EveryFnPlaceholder_ExistsInTextFormulas_AndEveryFormulaIsUsed()
        {
            var fnUsers = 0;

            foreach (var def in ClassAbilityRegistry.Abilities.Values)
            {
                var used = ClassAbilityText.FormulaNames(def);
                var declared = def.TextFormulas?.Keys.ToList() ?? new List<string>();

                foreach (var name in used)
                    Assert.IsTrue(declared.Contains(name), $"{def.Name}: {{fn:{name}}} has no TextFormulas entry");

                foreach (var name in declared)
                    Assert.IsTrue(used.Contains(name), $"{def.Name}: TextFormulas entry '{name}' is never used by a template");

                if (used.Count > 0)
                    fnUsers++;
            }

            Assert.IsTrue(fnUsers >= 1, "Exsanguinate is the proof conversion");
        }

        [TestMethod]
        public void ExsanguinateFormula_AtDefaults_EqualsTheHandlersOwnMath_ForEveryRank()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.Exsanguinate);
            var caps = Enumerable.Range(1, 3).Select(r => DefaultPropertyManager.DefaultLongProperties["class_ability_bloodmage_charge_stack_cap_r" + r].Item).ToArray();

            for (var rank = 1; rank <= def.MaxRank; rank++)
            {
                // the handler's method (ApplyBloodChargeDamage / GetReadout call this overload) against the Reserve of the same rank
                var handler = ExsanguinateMath.ChargesKept(ClassAbilityText.Defaults, rank, BloodChargeMath.StackCap(rank, caps[0], caps[1], caps[2]));

                Assert.AreEqual(handler, def.TextFormulas["charges_kept"].Compute(ClassAbilityText.Defaults, rank), $"rank {rank}");
                StringAssert.Contains(ClassAbilityText.RankTail(def, rank, ClassAbilityText.Defaults), $"keeps {handler} charges after the burst");
            }

            // the same numbers as the master text: 2/3/4 (now quoted by the rank tails)
            Assert.AreEqual("keeps 2 charges after the burst", ClassAbilityText.RankTail(def, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("keeps 4 charges after the burst", ClassAbilityText.RankTail(def, 3, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void ExsanguinateFormula_ClampsToTheReserveCapMinusOne_LikeTheMechanic()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.Exsanguinate);

            // kept_r3 retuned past the rank-3 Reserve cap (5): the mechanic keeps cap - 1 = 4, and so does the text
            var values = With((KeptPrefix + "3", 9));
            Assert.AreEqual(4, ExsanguinateMath.ChargesKept(values, 3, 5));
            Assert.AreEqual("keeps 4 charges after the burst", ClassAbilityText.RankTail(def, 3, values));

            // a smaller rank-1 Reserve cap clamps rank 1 the same way
            var small = With((KeptPrefix + "1", 7), (ReserveCapR1, 2));
            Assert.AreEqual(1, ExsanguinateMath.ChargesKept(small, 1, 2));
            Assert.AreEqual("keeps 1 charges after the burst", ClassAbilityText.RankTail(def, 1, small));
        }

        [TestMethod]
        public void ExsanguinateMath_ValueOverload_AgreesWithTheLongOverload_AcrossRanksAndCaps()
        {
            for (var rank = 0; rank <= 4; rank++)
            {
                for (var cap = 0; cap <= 6; cap++)
                {
                    var values = With((KeptPrefix + "1", 2), (KeptPrefix + "2", 3), (KeptPrefix + "3", 4));

                    Assert.AreEqual(ExsanguinateMath.ChargesKept(rank, 2, 3, 4, cap), ExsanguinateMath.ChargesKept(values, rank, cap), $"rank {rank} cap {cap}");
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // cap argument on {v} / {ranks}
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void CapArgument_ClampsVAndRanksToZeroAndTheCap()
        {
            // v: clamp the value to [0, max(0, cap)]
            Assert.AreEqual("8", Desc("{v:" + EchoBase + "," + EchoStep + "|pct}"));
            Assert.AreEqual("5", Desc("{v:" + EchoBase + "," + EchoStep + "|pct}", With((EchoStep, 0.05))));
            Assert.AreEqual("0", Desc("{v:" + EchoBase + "," + EchoStep + "|pct}", With((EchoStep, -0.5))));
            Assert.AreEqual("0", Desc("{v:" + EchoBase + "," + EchoStep + "|pct}", With((EchoBase, -0.2))));

            // ranks: one cap key for every rank
            Assert.AreEqual("2/3/3", Desc("{ranks:" + KeptPrefix + "," + ReserveCapR1 + "|int}", With((ReserveCapR1, 3), (KeptPrefix + "3", 9))));
            Assert.AreEqual("2/3/4", Desc("{ranks:" + KeptPrefix + "," + ReserveCapR1 + "|int}", With((ReserveCapR1, 10))));
            Assert.AreEqual("3", Desc("{ranks:" + KeptPrefix + "," + ReserveCapR1 + "@max|int}", With((ReserveCapR1, 3))));

            // the cap keys are reported for the existence / source-scan guards
            CollectionAssert.AreEquivalent(new[] { EchoBase, EchoStep },
                ClassAbilityText.Keys(Probe("{v:" + EchoBase + "," + EchoStep + "|pct}")).ToArray());
            CollectionAssert.AreEquivalent(new[] { KeptPrefix + "1", KeptPrefix + "2", KeptPrefix + "3", ReserveCapR1 },
                ClassAbilityText.Keys(Probe("{ranks:" + KeptPrefix + "," + ReserveCapR1 + "|int}")).ToArray());

            Assert.Throws<FormatException>(() => Desc("{v:a,b,c|pct}"));
            Assert.Throws<FormatException>(() => Desc("{per:" + EchoBase + "," + EchoStep + "|pct}"));
        }

        // ---------------------------------------------------------------------------------------------
        // review fixes: formula keys, null tails, non-finite formulas, perturbation
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void PerRankTails_WithANullElement_ThrowAFormatExceptionNamingTheRank()
        {
            var def = Probe("Spreads.", new[] { "one", null, "three" });

            var ex = Assert.Throws<FormatException>(() => ClassAbilityText.RankTail(def, 2, Defaults));
            StringAssert.Contains(ex.Message, "rank 2");

            ex = Assert.Throws<FormatException>(() => ClassAbilityText.Keys(def));
            StringAssert.Contains(ex.Message, "rank 2");

            Assert.AreEqual("one", ClassAbilityText.RankTail(def, 1, Defaults), "a good rank still renders");
        }

        [TestMethod]
        public void Fn_ReturningNaNOrInfinity_Throws_AndDeclaredKeysAreReported()
        {
            Assert.Throws<FormatException>(() => ClassAbilityText.Describe(FormulaProbe("{fn:nan|int}"), Defaults));
            Assert.Throws<FormatException>(() => ClassAbilityText.Describe(FormulaProbe("{fn:inf|int}"), Defaults));

            CollectionAssert.AreEqual(new[] { EchoBase }, ClassAbilityText.Keys(FormulaProbe("{fn:keyed|int} {fn:keyed@max|int}")).ToArray());
        }

        [TestMethod]
        public void ExsanguinateFormulaKeys_AreVisibleToTheKeyGuards()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.Exsanguinate);
            var keys = ClassAbilityText.Keys(def);

            for (var r = 1; r <= 3; r++)
            {
                CollectionAssert.Contains(keys.ToList(), "class_ability_exsanguinate_charges_kept_r" + r);
                CollectionAssert.Contains(keys.ToList(), "class_ability_bloodmage_charge_stack_cap_r" + r);
            }
        }

        [TestMethod]
        public void AFormulaWithNoKeys_IsOnlyAllowedWhenBuiltAsConstant()
        {
            var constant = TextFormula.Constant(rank => rank * 10);
            Assert.IsTrue(constant.IsConstant);
            Assert.AreEqual(0, constant.Keys.Length);
            Assert.AreEqual(20.0, constant.Compute(Defaults, 2));

            var keyless = new TextFormula((values, rank) => rank);
            Assert.IsFalse(keyless.IsConstant, "a keyless formula built the ordinary way is not constant, so the key guard still rejects it");
            Assert.AreEqual(0, keyless.Keys.Length);
        }

        [TestMethod]
        public void EveryDeclaredFormulaKey_ChangesTheOutput_ForAtLeastOneRank()
        {
            var formulas = 0;

            foreach (var def in ClassAbilityRegistry.Abilities.Values.Where(d => d.TextFormulas != null))
            {
                foreach (var pair in def.TextFormulas)
                {
                    formulas++;

                    // A constant formula reads a code table (a rank's grant), no tunable: it must say so by being built with
                    // TextFormula.Constant, and then it declares no keys. Every other formula must declare what it reads.
                    if (pair.Value.IsConstant)
                    {
                        Assert.AreEqual(0, pair.Value.Keys.Length, $"{def.Name}.{pair.Key}: a constant formula reads no tunable key");
                        continue;
                    }

                    Assert.IsTrue(pair.Value.Keys.Length > 0, $"{def.Name}.{pair.Key}: a registered formula must declare the keys it reads");

                    var baseline = Enumerable.Range(1, def.MaxRank).Select(r => pair.Value.Compute(Defaults, r)).ToArray();

                    foreach (var key in pair.Value.Keys)
                    {
                        var current = ClassAbilityText.Read(Defaults, key);
                        var moved = false;

                        // a clamp can hide an upward change (kept charges vs the cap), so try several perturbations
                        foreach (var candidate in new[] { 0.0, current + 1, current * 2 + 1, current - 1 })
                        {
                            var perturbed = With((key, candidate));

                            if (Enumerable.Range(1, def.MaxRank).Any(r => pair.Value.Compute(perturbed, r) != baseline[r - 1]))
                            {
                                moved = true;
                                break;
                            }
                        }

                        Assert.IsTrue(moved, $"{def.Name}.{pair.Key}: declared key {key} never changes the formula output - the formula does not read it");
                    }
                }
            }

            Assert.IsTrue(formulas >= 1, "Exsanguinate is the proof conversion");
        }

        // ---------------------------------------------------------------------------------------------
        // the seven proof conversions: text at defaults is master's, except where noted
        // ---------------------------------------------------------------------------------------------

        private static string DescribeAtDefaults(ClassAbilityId id) =>
            ClassAbilityText.Describe(ClassAbilityRegistry.Get(id), ClassAbilityText.Defaults);

        [TestMethod]
        public void ProofConversions_RenderMastersTextAtDefaults()
        {
            var defaults = ClassAbilityText.Defaults;
            Assert.AreEqual("+28.5% missile damage", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.Deadeye), 3, defaults));
            Assert.AreEqual("+5% cast speed per stack", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.NetherRush), 1, defaults));
            Assert.AreEqual("+15% cast speed per stack", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.NetherRush), 3, defaults));
            StringAssert.Contains(ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.WeakenedBlood), 1, defaults), "life resistance 2.00,");
            StringAssert.Contains(ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.WeakenedBlood), 3, defaults), "life resistance 3.10,");
            Assert.AreEqual("an 8% chance of the weakest element", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.VolatileElements), 1, defaults));
            Assert.AreEqual("a 24% chance of the weakest element", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.VolatileElements), 3, defaults));
            Assert.AreEqual("keeps 2 charges after the burst", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.Exsanguinate), 1, defaults));
            Assert.AreEqual("keeps 4 charges after the burst", ClassAbilityText.RankTail(ClassAbilityRegistry.Get(ClassAbilityId.Exsanguinate), 3, defaults));

            var multishot = ClassAbilityRegistry.Get(ClassAbilityId.Multishot);
            Assert.AreEqual("1 extra arrow", ClassAbilityText.RankTail(multishot, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("2 extra arrows", ClassAbilityText.RankTail(multishot, 2, ClassAbilityText.Defaults));
            Assert.AreEqual("3 extra arrows", ClassAbilityText.RankTail(multishot, 3, ClassAbilityText.Defaults));

            var bloom = ClassAbilityRegistry.Get(ClassAbilityId.NetherBloom);
            Assert.AreEqual("spreads to 1 nearby enemy", ClassAbilityText.RankTail(bloom, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("spreads to 2 nearby enemies", ClassAbilityText.RankTail(bloom, 2, ClassAbilityText.Defaults));
            Assert.AreEqual("spreads to 3 nearby enemies", ClassAbilityText.RankTail(bloom, 3, ClassAbilityText.Defaults));
        }
    }
}
