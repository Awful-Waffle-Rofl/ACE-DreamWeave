using System.Linq;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Soul Jump restore-and-immunity change: the rank-scaled restore (20/40/60%), the companion immunity
    /// handler's band and shared rule, and the rendered description. Player's static initializer does not run
    /// under the test host, so the window itself is covered by source pins elsewhere and the rule by the
    /// pure function it shares with Berserk Rage. Every key read is seeded from the code defaults.
    /// </summary>
    [TestClass]
    public class SoulJumpRestoreImmunityTests
    {
        [ClassInitialize]
        public static void Setup(TestContext context) => DefaultPropertyManager.LoadDefaultProperties();

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        [TestMethod]
        public void RestoreFraction_IsTwentyFortySixtyByRank_AtNeutralAffinity()
        {
            var b = D("class_ability_souljump_restore_fraction");
            var s = D("class_ability_souljump_restore_fraction_step");

            Assert.AreEqual(0.2, SoulJumpMath.RestoreFractionForRank(1, b, s, 1.0), 1e-9);
            Assert.AreEqual(0.4, SoulJumpMath.RestoreFractionForRank(2, b, s, 1.0), 1e-9);
            Assert.AreEqual(0.6, SoulJumpMath.RestoreFractionForRank(3, b, s, 1.0), 1e-9);
            Assert.AreEqual(0.0, SoulJumpMath.RestoreFractionForRank(0, b, s, 1.0), 1e-9, "unlearned restores nothing");
        }

        [TestMethod]
        public void RestoreFraction_AffinityMultipliesTheRankValue_AndClampsAtOne()
        {
            Assert.AreEqual(0.6 * 1.14, SoulJumpMath.RestoreFractionForRank(3, 0.2, 0.2, 1.14), 1e-9);
            Assert.AreEqual(1.0, SoulJumpMath.RestoreFractionForRank(3, 0.2, 0.2, 2.0), 1e-9, "clamped at 100%");
            Assert.AreEqual(1.0, SoulJumpMath.RestoreFractionForRank(3, 0.5, 0.5, 1.0), 1e-9, "a mis-tuned ladder clamps too");
            Assert.AreEqual(0.0, SoulJumpMath.RestoreFractionForRank(3, 0.2, -0.5, 1.0), 1e-9, "a negative step never goes below zero");
        }

        [TestMethod]
        public void Immunity_RuleBlocksCreatureHit_PassesHazardAndPlayerSource_DotLosesOnlyCreatureShare()
        {
            Assert.AreEqual(0u, BerserkRageImmunity.DamageAfterImmunity(250, false, false, false, 0), "a monster hit is blocked");
            Assert.AreEqual(250u, BerserkRageImmunity.DamageAfterImmunity(250, true, false, false, 0), "hazards pass");
            Assert.AreEqual(250u, BerserkRageImmunity.DamageAfterImmunity(250, false, true, false, 0), "player-sourced passes");
            Assert.AreEqual(30u, BerserkRageImmunity.DamageAfterImmunity(100, false, false, true, 70), "DoT keeps the non-creature share");
        }

        [TestMethod]
        public void Immunity_CompanionSitsInTheImmunityBand_SharesDefinition_AndIsNotARegisteredHandler()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();
            var immunity = SoulJumpAbility.Immunity;

            Assert.IsTrue(bucket.Contains(immunity), "the companion is dispatched");
            Assert.AreEqual(DamageMitigationOrder.Immunity, immunity.MitigationOrder);
            Assert.AreEqual(ClassAbilityId.SoulJump, immunity.Definition.Id, "filtered on the Soul Jump rank");
            Assert.IsFalse(immunity.RunsWithoutLearnedRank);
            Assert.IsFalse(ClassAbilityRegistry.Handlers.Contains(immunity), "not a registered handler");

            var save = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.SoulJump);
            Assert.IsTrue(bucket.IndexOf(immunity) < bucket.IndexOf(save), "the window is checked before the death save");
        }

        [TestMethod]
        public void Description_RendersTwentyFortySixtyAndTheImmunitySeconds()
        {
            var text = ClassAbilityText.Describe(ClassAbilityRegistry.Get(ClassAbilityId.SoulJump), ClassAbilityText.Defaults);

            var definition = ClassAbilityRegistry.Get(ClassAbilityId.SoulJump);
            StringAssert.Contains(ClassAbilityText.RankTail(definition, 1, ClassAbilityText.Defaults), "20% health, once every");
            StringAssert.Contains(ClassAbilityText.RankTail(definition, 2, ClassAbilityText.Defaults), "40% health, once every");
            StringAssert.Contains(ClassAbilityText.RankTail(definition, 3, ClassAbilityText.Defaults), "60% health, once every");
            StringAssert.Contains(text, "creatures cannot harm you for 5 seconds");
        }

        [TestMethod]
        public void Immunity_CompanionOrdering_AfterEveryObserver_BeforeEveryAbsorbPool_AndSaveBeforeBerserkTrigger()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();
            var companion = bucket.IndexOf(SoulJumpAbility.Immunity);
            Assert.IsTrue(companion >= 0, "the companion is dispatched");

            var observers = bucket.Where(h => h.MitigationOrder == DamageMitigationOrder.Observe).ToList();
            var pools = bucket.Where(h => h.MitigationOrder == DamageMitigationOrder.AbsorbPool).ToList();
            Assert.IsTrue(observers.Count > 0 && pools.Count > 0, "harness control: both bands are populated");

            foreach (var o in observers)
                Assert.IsTrue(bucket.IndexOf(o) < companion, "observers still see a blocked hit");
            foreach (var pool in pools)
                Assert.IsTrue(companion < bucket.IndexOf(pool), "a blocked hit drains no pool");

            var save = ClassAbilityRegistry.GetHandler(ClassAbilityId.SoulJump);
            var berserk = ClassAbilityRegistry.GetHandler(ClassAbilityId.BerserkRage);
            Assert.IsTrue(bucket.IndexOf((IPreWriteDamageAbility)save) < bucket.IndexOf((IPreWriteDamageAbility)berserk),
                "Soul Jump's save runs before Berserk Rage's trigger");
        }

        [TestMethod]
        public void Source_SaveDoesNotUseLifestoneProtection_AndFacetResetClearsTheWindow()
        {
            var pre = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityPreWriteBuffs.cs");
            var save = PooledLootSourceText.MethodBody(pre, "public uint TrySoulJumpSave(");
            Assert.IsFalse(save.Contains("UnderLifestoneProtection") || save.Contains("SetLifestoneProtection"),
                "the window must not be the 60s persisted lifestone flag");
            StringAssert.Contains(save, "soulJumpImmuneUntil");

            var buffs = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            var reset = PooledLootSourceText.MethodBody(buffs, "public void ResetClassAbilityTransientState(");
            StringAssert.Contains(reset, "soulJumpImmuneUntil = 0");
        }    }
}