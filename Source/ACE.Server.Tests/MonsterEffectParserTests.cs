using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.MonsterEffects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure parse half of the monster combat effect system: a weenie's PropertyString 9015
    /// MonsterCombatEffects string into effect specs. No database, no dat files, no creature.
    ///
    /// The load-bearing assertions here are the NEGATIVE ones. ClassAbilityTrainer.ParseTrainerAbilities
    /// silently skips a token it cannot resolve, which made a typo'd trainer weenie invisible until a
    /// hand-written test was added per trainer; this parser must report every one instead, so the tests
    /// below check that a bad record lands in errors rather than merely that a good one parses.
    /// </summary>
    [TestClass]
    public class MonsterEffectParserTests
    {
        /// <summary>The example a designer would author, used as the round-trip fixture.</summary>
        private const string ThreeEffects =
            "flatdamage type=fire amount=30 chance=0.35; leech vital=health pct=0.12; ramp axis=attackspeed on=hit per=0.04 max=8 window=12";

        [TestMethod]
        public void Parse_NullOrBlank_YieldsNothingAndNoErrors()
        {
            MonsterEffectParser.Parse(null, out var specs, out var errors);
            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(0, errors.Count);

            MonsterEffectParser.Parse("   ", out specs, out errors);
            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void Parse_ThreeEffectExample_RoundTripsEveryArg()
        {
            MonsterEffectParser.Parse(ThreeEffects, out var specs, out var errors);

            CollectionAssert.AreEqual(new[] { "flatdamage", "leech", "ramp" }, specs.Select(s => s.Kind).ToArray());
            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));

            var flat = specs[0];
            Assert.AreEqual("fire", flat.GetString("type"));
            Assert.AreEqual(30, flat.GetInt("amount", 0));
            Assert.AreEqual(0.35, flat.GetDouble("chance", 0.0), 0.0001);

            var leech = specs[1];
            Assert.AreEqual("health", leech.GetString("vital"));
            Assert.AreEqual(0.12, leech.GetDouble("pct", 0.0), 0.0001);

            var ramp = specs[2];
            Assert.AreEqual(MonsterRampAxis.AttackSpeed, ramp.GetEnum("axis", MonsterRampAxis.MagicDamage));
            Assert.AreEqual(MonsterEffectTrigger.Hit, ramp.GetFlags("on", MonsterEffectTrigger.None));
            Assert.AreEqual(0.04, ramp.GetDouble("per", 0.0), 0.0001);
            Assert.AreEqual(8, ramp.GetInt("max", 0));
            Assert.AreEqual(12, ramp.GetInt("window", 0));
        }

        [TestMethod]
        public void Parse_AbsentArg_FallsBackToTheCallersDefault()
        {
            MonsterEffectParser.Parse("leech vital=health pct=0.12", out var specs, out _);

            Assert.IsFalse(specs[0].Has("chance"));
            Assert.AreEqual(1.0, specs[0].GetDouble("chance", 1.0), 0.0001);
        }

        [TestMethod]
        public void Parse_ToleratesWhitespaceCasingAndEmptyRecords()
        {
            MonsterEffectParser.Parse("  ;;  FlatDamage   TYPE=fire    Amount=30 ;; leech vital=health ;  ", out var specs, out var errors);

            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));
            CollectionAssert.AreEqual(new[] { "flatdamage", "leech" }, specs.Select(s => s.Kind).ToArray());

            // kind and keys are lowercased on the way in; values are kept verbatim
            Assert.AreEqual("fire", specs[0].GetString("type"));
            Assert.AreEqual(30, specs[0].GetInt("AMOUNT", 0));
        }

        /// <summary>
        /// The trap this parser exists to avoid: an unknown kind must be REPORTED, not dropped in silence.
        /// The surrounding good records still parse, so one typo does not disarm a whole monster.
        /// </summary>
        [TestMethod]
        public void Parse_UnknownKind_IsReportedAndNotSilentlyDropped()
        {
            MonsterEffectParser.Parse("flatdamage type=fire amount=30; flatdmage type=cold amount=10; leech pct=0.1", out var specs, out var errors);

            CollectionAssert.AreEqual(new[] { "flatdamage", "leech" }, specs.Select(s => s.Kind).ToArray());

            Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
            StringAssert.Contains(errors[0], "flatdmage");
        }

        [TestMethod]
        public void Parse_MalformedToken_IsReportedAndDropsOnlyThatRecord()
        {
            MonsterEffectParser.Parse("flatdamage type=fire amount; leech vital=health pct=0.12", out var specs, out var errors);

            CollectionAssert.AreEqual(new[] { "leech" }, specs.Select(s => s.Kind).ToArray());

            Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
            StringAssert.Contains(errors[0], "amount");
        }

        [TestMethod]
        public void Parse_EmptyKeyOrValue_IsReported()
        {
            MonsterEffectParser.Parse("flatdamage =30", out var specs, out var errors);
            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));

            MonsterEffectParser.Parse("flatdamage amount=", out specs, out errors);
            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
        }

        /// <summary>
        /// A record whose first token is an arg is a missing kind, not an unknown one - the author most
        /// likely dropped a ';'. Reported distinctly so the message points at the real mistake.
        /// </summary>
        [TestMethod]
        public void Parse_RecordStartingWithAnArg_IsReported()
        {
            MonsterEffectParser.Parse("type=fire amount=30", out var specs, out var errors);

            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
            StringAssert.Contains(errors[0], "effect kind");
        }

        /// <summary>
        /// Two records of the same kind are two independent effects with their own args and their own state
        /// slot - "this monster burns you twice, for different amounts" must be authorable.
        /// </summary>
        [TestMethod]
        public void Parse_DuplicateKinds_BothSurviveAsSeparateSpecs()
        {
            MonsterEffectParser.Parse("flatdamage type=fire amount=30; flatdamage type=cold amount=10", out var specs, out var errors);

            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));
            Assert.AreEqual(2, specs.Count);
            Assert.AreEqual("fire", specs[0].GetString("type"));
            Assert.AreEqual("cold", specs[1].GetString("type"));
        }

        [TestMethod]
        public void Parse_MultiValueFlagArg_ResolvesEveryValue()
        {
            MonsterEffectParser.Parse("reflect pct=0.2 on=hit,avoid", out var specs, out var errors);

            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));

            var on = specs[0].GetFlags("on", MonsterEffectTrigger.None);

            Assert.AreEqual(MonsterEffectTrigger.Hit | MonsterEffectTrigger.Avoid, on);
            Assert.IsTrue(on.HasFlag(MonsterEffectTrigger.Hit));
            Assert.IsTrue(on.HasFlag(MonsterEffectTrigger.Avoid));
            Assert.IsFalse(on.HasFlag(MonsterEffectTrigger.SpellHit));
        }

        [TestMethod]
        public void Parse_MultiValueFlagArg_IsCaseInsensitiveAndIgnoresUnknownMembers()
        {
            MonsterEffectParser.Parse("ward amount=500 secs=30 on=Spawn,HEARTBEAT,notatrigger", out var specs, out _);

            Assert.AreEqual(
                MonsterEffectTrigger.Spawn | MonsterEffectTrigger.Heartbeat,
                specs[0].GetFlags("on", MonsterEffectTrigger.None));
        }

        /// <summary>
        /// Tokens split on whitespace BEFORE args split on ',', so a space inside a multi-value arg breaks
        /// the token in two and the second half has no '='. That is reported rather than half-parsed, which
        /// is the point: "on=hit, avoid" is a plausible authoring mistake and must not quietly become
        /// "on=hit".
        /// </summary>
        [TestMethod]
        public void Parse_SpaceInsideAMultiValueArg_IsReportedNotHalfParsed()
        {
            MonsterEffectParser.Parse("reflect pct=0.2 on=hit, avoid", out var specs, out var errors);

            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
            StringAssert.Contains(errors[0], "avoid");
        }

        [TestMethod]
        public void GetBool_ReadsTheAuthoredFlagForms()
        {
            MonsterEffectParser.Parse("avoid pct=0.1 silent=true; avoid pct=0.1 silent=no; avoid pct=0.1", out var specs, out _);

            Assert.IsTrue(specs[0].GetBool("silent", false));
            Assert.IsFalse(specs[1].GetBool("silent", true));
            Assert.IsTrue(specs[2].GetBool("silent", true), "an absent flag must fall back to the caller's default");
        }

        /// <summary>
        /// Every kind the design specifies must be authorable today, whether or not its handler has shipped -
        /// otherwise a content author cannot stage a weenie ahead of its phase, and the reserved-kind list in
        /// the registry would drift from the design without anything noticing.
        /// </summary>
        [TestMethod]
        public void Parse_AcceptsAllSixteenDesignedKinds()
        {
            var kinds = new[]
            {
                "flatdamage", "leech", "execute", "rangeramp", "speed", "reflect", "avoid", "dot",
                "debuff", "castspell", "dispel", "recast", "ramp", "ward", "manabarrier", "riposte",
            };

            MonsterEffectParser.Parse(string.Join("; ", kinds), out var specs, out var errors);

            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));
            CollectionAssert.AreEqual(kinds, specs.Select(s => s.Kind).ToArray());
        }
    }
}
