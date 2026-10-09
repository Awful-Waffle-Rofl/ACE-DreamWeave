using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure decision half of the UseCreateItem one-time payout gate (code review fix, RoZ round 11):
    /// OneTimeItemGrant.Decide/RefusalMessage, written over already-read values so it can be tested
    /// without a live Player - ACE.Server.Tests can never build one, the Player constructor reaches
    /// DatabaseManager.Authentication - and without PropertyManager, whose reads throw in this harness.
    /// Modeled directly on ACE.Server.Tests.MlTreasure.MlRelariaTrophyTests' "one-time claim side"
    /// section, which covers the sibling OneTimeClassAbilityGrant.Decide.
    ///
    /// The one clause order difference from the sibling is deliberate and covered explicitly below:
    /// OneTimeItemGrant checks "on cooldown" (the CanSolve-false case) BEFORE the mule check, where the
    /// sibling checks "already claimed" (hasQuest) before its mule check too - so the two are actually
    /// the SAME order, just named differently because CanSolve and HasQuest are different questions
    /// (cooldown-aware vs cooldown-blind). See OneTimeItemGrant.cs's own remarks for why CanSolve was
    /// chosen over HasQuest here.
    /// </summary>
    [TestClass]
    public class OneTimeItemGrantTests
    {
        private const string QuestName = "BluespireLaboratoryStrongboxNotesPayout";
        private const string ItemName = "Laboratory Trade Note Pouch";

        /// <summary>A character whose quest row is solvable (never claimed, or cooldown elapsed) may claim.</summary>
        [TestMethod]
        public void Decide_CanSolve_Allows()
        {
            Assert.AreEqual(OneTimeItemGrantDecision.Allow,
                OneTimeItemGrant.Decide(QuestName, canSolve: true, muleBlocked: false));
        }

        /// <summary>THE POINT OF THE WHOLE GATE: a second claim while the quest row is on cooldown (or
        /// permanently latched) is refused. The caller treats any non-Allow decision as "refuse and do
        /// NOT consume the pouch, do NOT stamp the quest", so a pooled second pouch stays inert.</summary>
        [TestMethod]
        public void Decide_CannotSolve_IsOnCooldown()
        {
            Assert.AreEqual(OneTimeItemGrantDecision.OnCooldown,
                OneTimeItemGrant.Decide(QuestName, canSolve: false, muleBlocked: false));
        }

        /// <summary>A mule makes no quest progress, so it can never record a claim - and
        /// QuestManager.Update's own mule guard is a SILENT no-op, which would otherwise let a mule Use
        /// an unlimited number of pouches with no stamp ever landing. Refused here with a message
        /// instead.</summary>
        [TestMethod]
        public void Decide_Mule_IsRefused()
        {
            Assert.AreEqual(OneTimeItemGrantDecision.MuleBlocked,
                OneTimeItemGrant.Decide(QuestName, canSolve: true, muleBlocked: true));
        }

        /// <summary>"On cooldown" is checked before the mule rule, so a mule is never told it has already
        /// claimed something it has not - matching the sibling's "already claimed before mule" order.</summary>
        [TestMethod]
        public void Decide_MuleOnCooldown_ReportsOnCooldown()
        {
            Assert.AreEqual(OneTimeItemGrantDecision.OnCooldown,
                OneTimeItemGrant.Decide(QuestName, canSolve: false, muleBlocked: true));
        }

        /// <summary>An item with no grant quest never reaches Decide in production (Gem.UseGem only calls
        /// it when PropertyString.UseCreateGrantQuest is set), but a null or empty value arriving here
        /// must refuse rather than grant - a missing ledger name means there is nowhere to record the
        /// claim.</summary>
        [TestMethod]
        public void Decide_MissingQuestName_IsMalformed()
        {
            Assert.AreEqual(OneTimeItemGrantDecision.MalformedQuestName,
                OneTimeItemGrant.Decide(null, canSolve: true, muleBlocked: false));

            Assert.AreEqual(OneTimeItemGrantDecision.MalformedQuestName,
                OneTimeItemGrant.Decide(string.Empty, canSolve: true, muleBlocked: false));
        }

        /// <summary>QuestManager parses '@' as a completion marker and '%' as a format marker, so an
        /// authored name carrying either would be read as a different quest than the one written.
        /// Refused as a content bug, matching the malformed-name shape even though canSolve/muleBlocked
        /// would otherwise allow.</summary>
        [TestMethod]
        public void Decide_QuestNameWithQuestManagerMarkers_IsMalformed()
        {
            Assert.AreEqual(OneTimeItemGrantDecision.MalformedQuestName,
                OneTimeItemGrant.Decide("BluespireLaboratoryStrongboxNotesPayout@2", canSolve: true, muleBlocked: false));

            Assert.AreEqual(OneTimeItemGrantDecision.MalformedQuestName,
                OneTimeItemGrant.Decide("Bluespire%Payout", canSolve: true, muleBlocked: false));

            Assert.AreEqual(OneTimeItemGrantDecision.MalformedQuestName,
                OneTimeItemGrant.Decide("Bluespire Payout", canSolve: true, muleBlocked: false));
        }

        /// <summary>The authored weenie/quest names are pinned as a literal so a rename on one side cannot
        /// silently desync from the other - IsValidQuestName is the same shape check the gate itself
        /// runs.</summary>
        [TestMethod]
        public void AuthoredQuestName_IsValid()
        {
            Assert.IsTrue(OneTimeItemGrant.IsValidQuestName(QuestName),
                "Content/sql/weenies/1003862 Laboratory Trade Note Pouch.sql authors this exact string in PropertyString.UseCreateGrantQuest, and quests/BluespireLaboratoryStrongboxNotesPayout.sql authors the matching quest row");
        }

        /// <summary>Every refusal carries player-facing wording; Allow carries none, which is what the
        /// caller keys on.</summary>
        [TestMethod]
        public void RefusalMessage_ExistsForEveryRefusalAndNotForAllow()
        {
            Assert.IsNull(OneTimeItemGrant.RefusalMessage(OneTimeItemGrantDecision.Allow, ItemName));

            Assert.IsFalse(string.IsNullOrEmpty(OneTimeItemGrant.RefusalMessage(OneTimeItemGrantDecision.OnCooldown, ItemName)));
            Assert.IsFalse(string.IsNullOrEmpty(OneTimeItemGrant.RefusalMessage(OneTimeItemGrantDecision.MuleBlocked, ItemName)));
            Assert.IsFalse(string.IsNullOrEmpty(OneTimeItemGrant.RefusalMessage(OneTimeItemGrantDecision.MalformedQuestName, ItemName)));
        }

        /// <summary>The refusal text is deliberately NOT QuestManager.HandleSolveError's raw countdown
        /// ("You may complete this quest again in 998d 23h 59m 12s") - see the code review finding this
        /// gate was built to address. Pinned so a future edit cannot quietly reintroduce the countdown by
        /// routing this decision through HandleSolveError instead.</summary>
        [TestMethod]
        public void RefusalMessage_OnCooldown_IsNotARawCountdown()
        {
            var message = OneTimeItemGrant.RefusalMessage(OneTimeItemGrantDecision.OnCooldown, ItemName);

            StringAssert.Contains(message, "already claimed");
            StringAssert.DoesNotMatch(message, new System.Text.RegularExpressions.Regex(@"\d+d \d+h \d+m \d+s"));
        }
    }
}
