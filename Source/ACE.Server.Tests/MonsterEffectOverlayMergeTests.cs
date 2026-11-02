using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Creature.ApplyMonsterEffectOverlay composes onto the spec CURRENTLY ATTACHED, so overlays accumulate
    /// instead of the second one replacing the first, and GetAttachedMonsterEffectKinds reports what is
    /// carried. Reads monster_effects_enabled and monster_effect_max_per_monster (seeded from code defaults
    /// in ClassInitialize; the one test that moves max_per_monster restores it in a finally).
    /// </summary>
    [TestClass]
    public class MonsterEffectOverlayMergeTests
    {
        private const string Authored = "leech vital=health pct=0.12";

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyLong("monster_effect_max_per_monster",
                DefaultPropertyManager.DefaultLongProperties["monster_effect_max_per_monster"].Item);
        }

        private static string[] KindsOf(Creature creature) =>
            Enumerable.Range(0, creature.MonsterEffects.Count).Select(i => creature.MonsterEffects[i].Spec.Kind).OrderBy(k => k).ToArray();

        [TestMethod]
        public void TwoOverlays_BothPresent_AndAuthoredSurvives()
        {
            var carrier = TestCreatures.CreateEffectCarrier(Authored);

            Assert.IsTrue(carrier.ApplyMonsterEffectOverlay("scatter pct=0.4"));
            Assert.IsTrue(carrier.ApplyMonsterEffectOverlay("speed axis=attack pct=0.2"));

            CollectionAssert.AreEqual(new[] { "leech", "scatter", "speed" }, KindsOf(carrier));
            Assert.AreEqual(3, carrier.MonsterEffectStates.Length);
        }

        [TestMethod]
        public void TwoOverlays_OnACreatureThatAuthorsNothing_BothPresent()
        {
            var carrier = TestCreatures.CreateEffectCarrier();

            Assert.IsTrue(carrier.ApplyMonsterEffectOverlay("scatter pct=0.4"));
            Assert.IsTrue(carrier.ApplyMonsterEffectOverlay("speed axis=attack pct=0.2"));

            CollectionAssert.AreEqual(new[] { "scatter", "speed" }, KindsOf(carrier));
        }

        [TestMethod]
        public void Overlays_KeepTheParseOnceCache_ForCreaturesOfOneWcid()
        {
            var wcid = TestCreatures.NextWcid();
            var a = TestCreatures.CreateEffectCarrier(Authored, wcid);
            var b = TestCreatures.CreateEffectCarrier(Authored, wcid);

            a.ApplyMonsterEffectOverlay("scatter pct=0.4");
            a.ApplyMonsterEffectOverlay("speed axis=attack pct=0.2");
            b.ApplyMonsterEffectOverlay("scatter pct=0.4");
            b.ApplyMonsterEffectOverlay("speed axis=attack pct=0.2");

            Assert.AreSame(a.MonsterEffects, b.MonsterEffects, "the same (wcid, composed spec) shares one immutable set");
            Assert.AreNotSame(a.MonsterEffectStates, b.MonsterEffectStates, "state stays per creature");
        }

        [TestMethod]
        public void Overlays_StillDropTheTailAtMaxPerMonster()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_max_per_monster", 2));

                var carrier = TestCreatures.CreateEffectCarrier(Authored);

                carrier.ApplyMonsterEffectOverlay("scatter pct=0.4");
                carrier.ApplyMonsterEffectOverlay("speed axis=attack pct=0.2");

                Assert.AreEqual(2, carrier.MonsterEffects.Count);
                CollectionAssert.AreEqual(new[] { "leech", "scatter" }, KindsOf(carrier), "the LAST record is the one dropped");
            }
            finally
            {
                PropertyManager.ModifyLong("monster_effect_max_per_monster",
                    DefaultPropertyManager.DefaultLongProperties["monster_effect_max_per_monster"].Item);
            }
        }

        [TestMethod]
        public void GetAttachedMonsterEffectKinds_ReportsKindsAndCount()
        {
            var bare = TestCreatures.CreateEffectCarrier();
            var (noKinds, noCount) = bare.GetAttachedMonsterEffectKinds();
            Assert.AreEqual(0, noKinds.Count);
            Assert.AreEqual(0, noCount);

            var carrier = TestCreatures.CreateEffectCarrier(Authored);
            carrier.ApplyMonsterEffectOverlay("scatter pct=0.4; scatter pct=0.2");

            var (kinds, count) = carrier.GetAttachedMonsterEffectKinds();

            Assert.AreEqual(3, count, "a record count, not a distinct-kind count");
            Assert.AreEqual(2, kinds.Count);
            Assert.IsTrue(kinds.Contains("LEECH"), "case-insensitive");
            Assert.IsTrue(kinds.Contains("scatter"));
        }
    }
}
