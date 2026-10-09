using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.RefireStations;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Salvage Forge station's own pure surface (ACE.Server/RefireStations/SalvageForgeStation.cs). A
    /// Player/WorldObject cannot be constructed in a test in this repo (see RefireStationsTests' and
    /// SalvageForgeTests' remarks), so VerifyEligible/BuildPrompt/Apply - all of which need a live Player -
    /// are not covered here. What IS pure: the bag-count band the station's eligibility check and prompt both
    /// read from Entity.SalvageForge (shared, not restated - see SalvageForgeTests.cs for the exhaustive band
    /// coverage), the derived Trade Note pyreal total, and SalvageForge.SkillName, now public specifically so
    /// this station can reuse it in its own refusal text.
    /// </summary>
    [TestClass]
    public class SalvageForgeStationTests
    {
        // ---------------- SalvageForgeStation.ForgeCostPyreals ----------------

        /// <summary>
        /// ForgeCostPyreals is DERIVED (ForgeCostNotes * Player.MmdValue), not a literal copied from a sibling
        /// station file - this pins the derivation against Player.MmdValue's current value (250,000, per
        /// Player_Bank.cs:46) so a future change to either constant is caught here rather than silently
        /// drifting the player-facing prompt/insufficient-funds text out of sync with what TryCharge actually
        /// debits.
        /// </summary>
        [TestMethod]
        public void ForgeCostPyreals_IsForgeCostNotesTimesMmdValue()
        {
            Assert.AreEqual(5, SalvageForgeStation.ForgeCostNotes);
            Assert.AreEqual(1_250_000L, SalvageForgeStation.ForgeCostPyreals);
            Assert.AreEqual(SalvageForgeStation.ForgeCostNotes * Player.MmdValue, SalvageForgeStation.ForgeCostPyreals);
        }

        // ---------------- SalvageForge.BagsRequired, as consumed by SalvageForgeStation ----------------

        /// <summary>
        /// SalvageForgeStation.VerifyEligible/BuildPrompt/Apply all call SalvageForge.BagsRequired directly
        /// (never their own copy) - re-exercising the same named edges as SalvageForgeTests.BagsRequired_
        /// MatchesTheSkillBand here documents that this station reads the SAME shared band, not a restated one.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 10)]
        [DataRow(499, 10)]
        [DataRow(500, 9)]
        [DataRow(699, 9)]
        [DataRow(700, 8)]
        [DataRow(899, 8)]
        [DataRow(900, 7)]
        [DataRow(1200, 7)]
        public void BagsRequired_SharedWithHollowHammerPath_MatchesTheSkillBand(int skill, int expected)
        {
            Assert.AreEqual(expected, SalvageForge.BagsRequired(skill, 500, 700, 900));
        }

        // ---------------- SalvageForge.SkillName, now public for this station's reuse ----------------

        [TestMethod]
        public void SkillName_MatchesTheThreeTinkeringSkillsThisForgeAnswersTo()
        {
            Assert.AreEqual("Armor Tinkering", SalvageForge.SkillName(Skill.ArmorTinkering));
            Assert.AreEqual("Weapon Tinkering", SalvageForge.SkillName(Skill.WeaponTinkering));
            Assert.AreEqual("Magic Item Tinkering", SalvageForge.SkillName(Skill.MagicItemTinkering));
        }
    }
}
