using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Golden pins for the two no-behaviour-change extractions made for the Berserker/Archmage/VoidSummon ceiling probes. The
    /// expected values come from the pre-extraction inline code: SpellAoeAbility did
    /// <c>Math.Clamp(baseMult + rider + gearMod, baseMult, cap)</c> at both RadiatedDamageMult and GetReadout, and
    /// Player.OnFrenzyLandedHit did <c>if (frenzyStacks &lt; cap) frenzyStacks++</c>.
    /// </summary>
    [TestClass]
    public class CeilingExtraction2bGoldenTests
    {
        [TestMethod]
        public void ClampRadiated_MatchesTheOldInlineClamp()
        {
            Assert.AreEqual(0.45, SpellAoeAbility.ClampRadiated(0.30, 0.10, 0.05, 0.75), 1e-12, "inside the band: the plain sum");
            Assert.AreEqual(0.75, SpellAoeAbility.ClampRadiated(0.30, 5.0, 0.0, 0.75), 1e-12, "over the cap: held at the cap");
            Assert.AreEqual(0.75, SpellAoeAbility.ClampRadiated(0.30, 0.0, 5.0, 0.75), 1e-12, "gear alone can also hit the cap");
            Assert.AreEqual(0.30, SpellAoeAbility.ClampRadiated(0.30, 0.0, 0.0, 0.75), 1e-12, "no rider, no gear: the rank base");
            Assert.AreEqual(0.40, SpellAoeAbility.ClampRadiated(0.40, -0.20, 0.0, 0.75), 1e-12, "a negative rider never goes below the rank base");
            Assert.ThrowsExactly<ArgumentException>(() => SpellAoeAbility.ClampRadiated(0.90, 0.0, 0.0, 0.75), "a cap below the base is still Math.Clamp's min > max error, as before");
        }

        [TestMethod]
        public void NextFrenzyStacks_MatchesTheOldInlineCap()
        {
            Assert.AreEqual(1, Player.NextFrenzyStacks(0, 6), "an empty pool gains one");
            Assert.AreEqual(3, Player.NextFrenzyStacks(2, 3), "one below the cap: grows by one");
            Assert.AreEqual(3, Player.NextFrenzyStacks(3, 3), "at the cap: unchanged");
            Assert.AreEqual(5, Player.NextFrenzyStacks(5, 3), "already over the cap: unchanged, never pulled down");
            Assert.AreEqual(0, Player.NextFrenzyStacks(0, 0), "a zero cap (rank 0) gains nothing");
        }
    }
}
