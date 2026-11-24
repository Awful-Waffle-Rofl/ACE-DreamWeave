using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Golden pins for the no-behaviour-change extractions made for the BloodMage/Spellsword ceiling probes. The expected values
    /// come from the pre-extraction inline code, which was, at the Blood Charge site,
    /// <c>if (bloodChargeStacks &lt; cap) bloodChargeStacks++</c>; at the Resonance and Spellsurge sites
    /// <c>if (stacks &gt;= cap) return; stacks++;</c>; and at the Sundermark vulnerability site
    /// <c>(uint)Math.Clamp(tunable, 1L, 8L)</c>.
    /// </summary>
    [TestClass]
    public class CeilingExtraction2cGoldenTests
    {
        [TestMethod]
        public void BloodChargeNextStacks_MatchesTheOldInlineCap()
        {
            Assert.AreEqual(1, BloodChargeMath.NextStacks(0, 5), "an empty pool gains one");
            Assert.AreEqual(5, BloodChargeMath.NextStacks(4, 5), "one below the cap: grows by one");
            Assert.AreEqual(5, BloodChargeMath.NextStacks(5, 5), "at the cap: unchanged");
            Assert.AreEqual(7, BloodChargeMath.NextStacks(7, 5), "already over the cap: unchanged, never pulled down");
            Assert.AreEqual(0, BloodChargeMath.NextStacks(0, 0), "a zero cap (rank 0) gains nothing");
        }

        [TestMethod]
        public void ResonanceAndSpellsurgeNextStacks_MatchTheOldInlineCap()
        {
            foreach (var next in new Func<int, int, int>[] { ResonanceAbility.NextStacks, SpellsurgeAbility.NextStacks })
            {
                Assert.AreEqual(1, next(0, 5), "an empty pool gains one");
                Assert.AreEqual(5, next(4, 5), "one below the cap: grows by one");
                Assert.AreEqual(5, next(5, 5), "at the cap: unchanged");
                Assert.AreEqual(8, next(8, 5), "already over the cap: unchanged");
                Assert.AreEqual(0, next(0, 0), "a zero cap gains nothing");
                Assert.AreEqual(0, next(0, -2), "a negative cap gains nothing");
            }
        }

        [TestMethod]
        public void SundermarkVulnerabilityMaxLevel_MatchesTheOldInlineClamp()
        {
            Assert.AreEqual(1u, SundermarkAbility.VulnerabilityMaxLevel(-5));
            Assert.AreEqual(1u, SundermarkAbility.VulnerabilityMaxLevel(0), "0 clamps up to 1");
            Assert.AreEqual(1u, SundermarkAbility.VulnerabilityMaxLevel(1));
            Assert.AreEqual(5u, SundermarkAbility.VulnerabilityMaxLevel(5));
            Assert.AreEqual(8u, SundermarkAbility.VulnerabilityMaxLevel(8));
            Assert.AreEqual(8u, SundermarkAbility.VulnerabilityMaxLevel(9), "above the ladder clamps down to 8");
            Assert.AreEqual(8u, SundermarkAbility.VulnerabilityMaxLevel(long.MaxValue));
        }
    }
}
