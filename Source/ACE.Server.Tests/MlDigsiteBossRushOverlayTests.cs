using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Boss Rush mechanic overlay: ml_digsite_bossrush_mechanic's shipped default must be valid monster
    /// effect grammar (it is authored as a string, so nothing else would catch a typo before a live boss
    /// silently fought without it), and Creature.ApplyMonsterEffectOverlay must add it on top of a creature's
    /// authored effects through the same cache and resolve path construction uses.
    ///
    /// Reads only code defaults (LoadDefaultProperties below: creature construction, monster_effects_enabled,
    /// monster_effect_max_per_monster and monster_effect_ward_cap), and modifies none of them.
    /// </summary>
    [TestClass]
    public class MlDigsiteBossRushOverlayTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void Cleanup()
        {
            WardEffect.AnnounceHookForTests = null;
        }

        [TestMethod]
        public void The_default_mechanic_parses_and_validates_with_no_errors()
        {
            MonsterEffectParser.Parse(MlDigsiteRules.DefaultBossRushMechanic, out var specs, out var errors);

            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));
            CollectionAssert.AreEqual(new[] { "ward" }, specs.Select(s => s.Kind).ToArray());

            var buildErrors = new List<string>();
            var set = MonsterEffectSet.Build(specs, buildErrors);

            Assert.AreEqual(0, buildErrors.Count, string.Join(" | ", buildErrors));
            Assert.IsNotNull(set, "the default mechanic must resolve to a live effect");
            Assert.AreEqual(1, set.Count);
        }

        [TestMethod]
        public void The_spec_as_originally_worded_with_pcthp_20_would_have_been_rejected()
        {
            // pcthp is a FRACTION of max health in (0, 1]. The design note said "pcthp=20"; that record is
            // refused by WardEffect.Validate, which is why the shipped default says 0.2.
            MonsterEffectParser.Parse("ward on=hpbelow trigger=0.5 pcthp=20 secs=10", out var specs, out _);

            var buildErrors = new List<string>();
            MonsterEffectSet.Build(specs, buildErrors);

            Assert.AreEqual(1, buildErrors.Count);
        }

        [TestMethod]
        public void ComposeMonsterEffectSpec_appends_to_the_authored_spec()
        {
            Assert.AreEqual("ward secs=1", Creature.ComposeMonsterEffectSpec(null, " ward secs=1 "));
            Assert.AreEqual("ward secs=1", Creature.ComposeMonsterEffectSpec("   ", "ward secs=1"));
            Assert.AreEqual("leech vital=health pct=0.1; ward secs=1", Creature.ComposeMonsterEffectSpec("leech vital=health pct=0.1", "ward secs=1"));
            Assert.AreEqual("leech vital=health pct=0.1; ward secs=1", Creature.ComposeMonsterEffectSpec("leech vital=health pct=0.1 ; ", "ward secs=1"));
            Assert.AreEqual("leech vital=health pct=0.1", Creature.ComposeMonsterEffectSpec("leech vital=health pct=0.1", "  "));
        }

        [TestMethod]
        public void ApplyMonsterEffectOverlay_adds_the_mechanic_to_a_creature_that_authors_nothing()
        {
            var boss = TestCreatures.CreateEffectCarrier();

            Assert.IsNull(boss.MonsterEffects);

            Assert.IsTrue(boss.ApplyMonsterEffectOverlay(MlDigsiteRules.DefaultBossRushMechanic));

            Assert.IsNotNull(boss.MonsterEffects);
            Assert.AreEqual(1, boss.MonsterEffects.Count);
            Assert.AreEqual(1, boss.MonsterEffectStates.Length, "a state slot per effect, allocated with the set");
        }

        [TestMethod]
        public void ApplyMonsterEffectOverlay_keeps_the_authored_effects_and_never_touches_another_creatures_set()
        {
            const string authored = "leech vital=health pct=0.12";

            var wcid = TestCreatures.NextWcid();
            var boss = TestCreatures.CreateEffectCarrier(authored, wcid);
            var ordinary = TestCreatures.CreateEffectCarrier(authored, wcid);

            Assert.AreEqual(1, boss.MonsterEffects.Count);
            Assert.AreSame(boss.MonsterEffects, ordinary.MonsterEffects, "same wcid and spec share one immutable set");

            Assert.IsTrue(boss.ApplyMonsterEffectOverlay(MlDigsiteRules.DefaultBossRushMechanic));

            Assert.AreEqual(2, boss.MonsterEffects.Count, "authored leech plus the overlaid ward");
            Assert.AreEqual(1, ordinary.MonsterEffects.Count, "the plain authored entry other creatures resolve to is untouched");
            Assert.AreNotSame(boss.MonsterEffects, ordinary.MonsterEffects);

            // and a fresh creature of the same wcid still resolves the plain spec
            var later = TestCreatures.CreateEffectCarrier(authored, wcid);
            Assert.AreEqual(1, later.MonsterEffects.Count);
        }

        [TestMethod]
        public void ApplyMonsterEffectOverlay_with_an_empty_mechanic_changes_nothing()
        {
            var boss = TestCreatures.CreateEffectCarrier("leech vital=health pct=0.12");
            var before = boss.MonsterEffects;

            Assert.IsFalse(boss.ApplyMonsterEffectOverlay(""));
            Assert.IsFalse(boss.ApplyMonsterEffectOverlay("   "));
            Assert.IsFalse(boss.ApplyMonsterEffectOverlay(null));

            Assert.AreSame(before, boss.MonsterEffects);
        }

        [TestMethod]
        public void The_default_mechanic_raises_its_ward_once_the_boss_drops_below_half_health()
        {
            var boss = TestCreatures.CreateDefender(maxHealth: 1000);

            Assert.IsTrue(boss.ApplyMonsterEffectOverlay(MlDigsiteRules.DefaultBossRushMechanic));

            // Read back rather than assumed: endurance contributes to max health, so this is not exactly 1000.
            var max = boss.Health.MaxValue;

            var announced = new List<WardTransition>();
            WardEffect.AnnounceHookForTests = (c, transition, message) => announced.Add(transition);

            // exactly half is NOT below the trigger: on=hpbelow reads ExecutionerAbility.IsExecuteRange, a strict <
            boss.Health.Current = max / 2 + max % 2;
            boss.MonsterEffectHeartbeat();

            Assert.AreEqual(0u, boss.MonsterEffectStates[0].WardAmount, "no ward at or above half health");
            Assert.AreEqual(0, announced.Count);

            boss.Health.Current = max / 2 - 1;
            boss.MonsterEffectHeartbeat();

            Assert.AreEqual((uint)System.Math.Round(max * 0.2), boss.MonsterEffectStates[0].WardAmount, "20% of max health");
            CollectionAssert.AreEqual(new[] { WardTransition.Up }, announced);

            // one-shot: a further heartbeat below half does not regrant
            boss.MonsterEffectStates[0].WardAmount = 0;
            boss.MonsterEffectHeartbeat();
            Assert.AreEqual(0u, boss.MonsterEffectStates[0].WardAmount);
        }
    }
}
