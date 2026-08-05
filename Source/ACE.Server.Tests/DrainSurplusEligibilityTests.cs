using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The gate on Drain Health's fellowship-surplus cascade.
    ///
    /// The cascade is the Blood Mage class ability "Transfusion", not base life magic. A caster without it
    /// must take the retail path: no widened capacity, no split, no payout, no broadcast, no message. In
    /// WorldObject.GetDrainSurplusFellows that is the single `return null` these predicates drive, and every
    /// downstream effect is guarded on the resulting list being non-null.
    ///
    /// Only the scalar conditions are testable in isolation - the per-recipient half (alive, same landblock,
    /// within range) needs a live Player, whose static initializer cannot run under the test host.
    ///
    /// Since 2026-08-03 a recipient is a fellow OR one of the caster's own summons, so the last condition is
    /// hasFellowship OR hasSummon rather than hasFellowship alone. The summon side of that is covered in
    /// BloodMageMechanicsTests alongside the rest of the Transfusion cascade.
    /// </summary>
    [TestClass]
    public class DrainSurplusEligibilityTests
    {
        // the caster shape a real Blood Mage Drain Health cast presents
        private const bool IsPlayer = true;
        private const bool IsDestination = true;
        private const bool ValidVictim = true;
        private const bool InFellowship = true;
        private const bool HasSummon = false;

        private static bool Qualifies(int transfusionRank) =>
            DrainSurplusEligibility.CasterQualifies(IsPlayer, IsDestination, ValidVictim, transfusionRank, InFellowship, HasSummon);

        // ------------------------------------------------------------------
        // the class gate - the whole point of this change
        // ------------------------------------------------------------------

        [TestMethod]
        public void CasterQualifies_WithoutTransfusion_TakesTheRetailPath()
        {
            Assert.IsFalse(Qualifies(0),
                "A caster who has not learned Transfusion must get no cascade at all - GetDrainSurplusFellows returns null, so there is no widened capacity, no split, no heal, no broadcast and no message.");
        }

        [TestMethod]
        public void CasterQualifies_WithTransfusion_GetsTheCascade()
        {
            Assert.IsTrue(Qualifies(1), "Rank 1 is the only rank Transfusion has, and it must enable the cascade.");
        }

        [TestMethod]
        public void CasterQualifies_TransfusionIsSingleRank_NoScaling()
        {
            // Transfusion is a single-rank ability, but the predicate must not misbehave if a rank above 1
            // is ever persisted - it is on or off, never scaled.
            Assert.IsTrue(Qualifies(2));
            Assert.IsTrue(Qualifies(99));
        }

        [TestMethod]
        public void CasterQualifies_NegativeRank_IsTreatedAsUnlearned()
        {
            Assert.IsFalse(Qualifies(-1), "A negative rank is not a learned ability.");
        }

        [TestMethod]
        public void TransfusionId_IsTheBloodMageScaffoldId()
        {
            // the gate reads ClassAbilityId.Transfusion; renumbering it would silently point the cascade at
            // a different ability, and class-ability ids are append-only precisely because they are
            // persisted per character
            Assert.AreEqual(53, (int)ClassAbilityId.Transfusion);
        }

        // ------------------------------------------------------------------
        // the pre-existing gates, unchanged by this commit - pinned so the
        // Transfusion condition cannot be "simplified" into replacing them
        // ------------------------------------------------------------------

        [TestMethod]
        public void CasterQualifies_MonsterCaster_NeverCascades()
        {
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(
                casterIsPlayer: false, casterIsTransferDestination: true, targetIsEligibleVictim: true,
                transfusionRank: 1, hasFellowship: true, hasSummon: true),
                "Monsters casting Drain must be completely unaffected, Transfusion or not - and a monster's own summons do not change that.");
        }

        [TestMethod]
        public void CasterQualifies_PlayerTarget_NeverCascades()
        {
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(
                casterIsPlayer: true, casterIsTransferDestination: true, targetIsEligibleVictim: false,
                transfusionRank: 1, hasFellowship: true, hasSummon: true),
                "PvE only - draining another player must never feed the cascade, summon or no summon.");
        }

        [TestMethod]
        public void CasterQualifies_NoFellowshipAndNoSummon_NeverCascades()
        {
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(
                casterIsPlayer: true, casterIsTransferDestination: true, targetIsEligibleVictim: true,
                transfusionRank: 1, hasFellowship: false, hasSummon: false),
                "A Blood Mage with neither a fellowship nor a summon has nobody to cascade to and must behave exactly as retail.");
        }

        [TestMethod]
        public void CasterQualifies_CasterNotTheTransferDestination_NeverCascades()
        {
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(
                casterIsPlayer: true, casterIsTransferDestination: false, targetIsEligibleVictim: true,
                transfusionRank: 1, hasFellowship: true, hasSummon: true),
                "'Surplus the caster cannot absorb' is only meaningful when the caster is the one receiving the transfer.");
        }

        [TestMethod]
        public void CasterQualifies_EveryConditionIsNecessary()
        {
            // flipping any single condition off the qualifying shape must block the cascade. The last two
            // are the exception and are deliberately NOT independent: they are an OR, covered on their own
            // in CasterQualifies_RecipientSourcesAreAnOr.
            Assert.IsTrue(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, true, false), "control: the qualifying shape");

            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(false, true, true, 1, true, false));
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, false, true, 1, true, false));
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, false, 1, true, false));
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 0, true, false));
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, false, false));
        }

        /// <summary>
        /// The recipient sources are an OR, not an AND (user, 2026-08-03: "Transfusion should also work on
        /// player summons in range"). Either alone must open the cascade; only having neither closes it.
        /// </summary>
        [TestMethod]
        public void CasterQualifies_RecipientSourcesAreAnOr()
        {
            Assert.IsTrue(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, hasFellowship: true, hasSummon: false),
                "a fellowship alone has always qualified and must keep qualifying");

            Assert.IsTrue(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, hasFellowship: false, hasSummon: true),
                "a summon alone must now qualify - this is the whole change");

            Assert.IsTrue(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, hasFellowship: true, hasSummon: true),
                "having both is not a conflict");

            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, hasFellowship: false, hasSummon: false),
                "neither is still the retail path");
        }

        [TestMethod]
        public void CasterQualifies_SummonDoesNotBypassTheClassGate()
        {
            // widening WHO can receive must not widen WHO can cast - a summoner without Transfusion is
            // still on the retail path
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(
                casterIsPlayer: true, casterIsTransferDestination: true, targetIsEligibleVictim: true,
                transfusionRank: 0, hasFellowship: false, hasSummon: true),
                "a pet does not substitute for having learned Transfusion.");
        }

        // ------------------------------------------------------------------
        // spell shape - Health drains only
        // ------------------------------------------------------------------

        [TestMethod]
        public void SpellQualifies_HealthDrain_Qualifies()
        {
            Assert.IsTrue(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Health, PropertyAttribute2nd.Health));
        }

        [TestMethod]
        public void SpellQualifies_NonDrainTransfer_DoesNot()
        {
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(false, PropertyAttribute2nd.Health, PropertyAttribute2nd.Health),
                "Stamina to Health and friends are transfers but not drains.");
        }

        [TestMethod]
        public void SpellQualifies_StaminaAndManaDrains_KeepRetailBehaviour()
        {
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Stamina, PropertyAttribute2nd.Stamina));
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Mana, PropertyAttribute2nd.Mana));
        }

        [TestMethod]
        public void SpellQualifies_HealthSourceButOtherDestination_DoesNot()
        {
            // this is the condition that keeps missing HEALTH from being added to a missing-mana budget
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Health, PropertyAttribute2nd.Mana));
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Health, PropertyAttribute2nd.Stamina));
        }

        [TestMethod]
        public void SpellQualifies_OtherSourceButHealthDestination_DoesNot()
        {
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Mana, PropertyAttribute2nd.Health));
            Assert.IsFalse(DrainSurplusEligibility.SpellQualifies(true, PropertyAttribute2nd.Stamina, PropertyAttribute2nd.Health));
        }
    }
}
