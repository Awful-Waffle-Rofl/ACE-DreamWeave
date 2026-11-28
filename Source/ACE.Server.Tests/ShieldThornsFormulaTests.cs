using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure parts of the shield Thorns formula (Docs/WeaponMods/SHIELDS-DESIGN.md "The Thorns formula after this
    /// change"): the six-factor ScaleReflect (Heft flat damage plus the damage-rating and crit multipliers), the crit damage
    /// multiplier (Crippling Blow, Execution), the rating multiplier (Damage Rating, plus Crit Damage Rating additively on a crit), and the crit roll comparison. The wiring
    /// through the real Player.ApplyThornsReflect lives in ShieldThornsReflectTests.
    /// </summary>
    [TestClass]
    public class ShieldThornsFormulaTests
    {
        [TestMethod]
        public void ScaleReflect_AllNeutral_IsTheOriginMasterAmount()
        {
            Assert.AreEqual(500u, ThornsAbility.ScaleReflect(500u, 0.0, 1.0, 1.0, 1.0, 1.0));
            Assert.AreEqual(ThornsAbility.ScaleReflect(500u, 1.0, 1.0, 1.0), ThornsAbility.ScaleReflect(500u, 0.0, 1.0, 1.0, 1.0, 1.0),
                "with no Heft and a neutral damage rating, the six-factor form equals the four-factor form");
        }

        [TestMethod]
        public void ScaleReflect_CritMultiplierScalesTheWhole()
        {
            Assert.AreEqual(1500u, ThornsAbility.ScaleReflect(500u, 0.0, 1.0, 1.0, 3.0, 1.0));
        }

        [TestMethod]
        public void ScaleReflect_HeftIsFlatDamageAddedBeforeTheMultipliers()
        {
            Assert.AreEqual(522u, ThornsAbility.ScaleReflect(500u, 22.0, 1.0, 1.0, 1.0, 1.0));
            Assert.AreEqual(626u, ThornsAbility.ScaleReflect(500u, 22.0, 1.0, 1.2, 1.0, 1.0), "(500 + 22) x 1.2 = 626.4");
        }

        [TestMethod]
        public void ScaleReflect_FloorsANegativeHeftAndANegativeMultiplier()
        {
            Assert.AreEqual(500u, ThornsAbility.ScaleReflect(500u, -5.0, 1.0, 1.0, 1.0, 1.0), "negative heft floored at 0");
            Assert.AreEqual(0u, ThornsAbility.ScaleReflect(500u, 22.0, 1.0, -1.0, 1.0, 1.0), "a negative multiplier never heals");
        }

        [TestMethod]
        public void CritDamageMultiplier_WithoutCripplingBlowOrExecution_IsTheBare2x()
        {
            Assert.AreEqual(2.0, ThornsAbility.CritDamageMultiplier(1.0f, 0.0), 1e-9);
            Assert.AreEqual(CloakedInPowerAbility.CritThornsBaseMultiplier, ThornsAbility.CritDamageMultiplier(1.0f, 0.0), 1e-9);
        }

        [TestMethod]
        public void CritDamageMultiplier_TakesCripplingBlowAndExecution()
        {
            Assert.AreEqual(10.5, ThornsAbility.CritDamageMultiplier(6.0f, 0.5), 1e-6, "(1 + 6) x 1.5");
            Assert.AreEqual(4.5, ThornsAbility.CritDamageMultiplier(2.0f, 0.5), 1e-6, "(1 + 2) x 1.5");
            Assert.AreEqual(2.0, ThornsAbility.CritDamageMultiplier(1.0f, -0.5), 1e-9, "a negative Execution is floored at 0");
        }

        // ---- the rating multiplier: Damage Rating alone, or combined additively with Crit Damage Rating on a crit ----

        [TestMethod]
        public void DamageRatingMultiplier_NonCrit_IsTheDamageRatingModAlone()
        {
            Assert.AreEqual(1.0, ThornsAbility.DamageRatingMultiplier(1.0f, false, 50), 1e-9, "Crit Damage Rating is ignored without a crit");
            Assert.AreEqual(1.2, ThornsAbility.DamageRatingMultiplier(1.2f, false, 50), 1e-6);
        }

        [TestMethod]
        public void DamageRatingMultiplier_Crit_CombinesAdditively_LikeAWeaponCrit()
        {
            // Creature.AdditiveCombine: each mod back to a rating (ModToRating), ratings summed, one mod again.
            Assert.AreEqual(1.4, ThornsAbility.DamageRatingMultiplier(1.2f, true, 20), 1e-6, "DR 20 + CDR 20 = rating 40 = 1.4, not 1.2 x 1.2 = 1.44");
            Assert.AreEqual(1.5, ThornsAbility.DamageRatingMultiplier(1.0f, true, 50), 1e-6, "DR 0 + CDR 50 = 1.5, the Cloaked in Power value");
            Assert.AreEqual(1.0, ThornsAbility.DamageRatingMultiplier(1.0f, true, 0), 1e-9);

            // the same helper DamageEvent calls on a weapon crit
            Assert.AreEqual((double)Creature.AdditiveCombine(1.2f, 1.3f), ThornsAbility.DamageRatingMultiplier(1.2f, true, 30), 1e-9);
        }

        [TestMethod]
        public void CritAtZeroDamageRating_MatchesTheOriginMasterCritThornsMultiplier()
        {
            foreach (var cdr in new[] { 0, 50, 100 })
                Assert.AreEqual(CloakedInPowerAbility.CritThornsMultiplier(cdr),
                    ThornsAbility.CritDamageMultiplier(1.0f, 0.0) * ThornsAbility.DamageRatingMultiplier(1.0f, true, cdr), 1e-6, $"CDR {cdr}");
        }

        [TestMethod]
        public void RollsCritical_IsChanceGreaterThanRoll_LikeAWeaponHit()
        {
            Assert.IsTrue(ThornsAbility.RollsCritical(0.1, 0.0999));
            Assert.IsFalse(ThornsAbility.RollsCritical(0.1, 0.11));
            Assert.IsFalse(ThornsAbility.RollsCritical(1.0, 1.0), "strict compare, same as DamageEvent; 1.0 is the never-crit test roll (production draws are < 1.0)");
        }
    }
}
