using ACE.Server.ClassAbilities;
using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// The two pure halves of the Aun Relaria CAP trophy (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md
    /// section 6 "Trophy", step 11): the drop decision (MlRelariaTrophy.ShouldDropTrophy) and the
    /// one-time claim decision (OneTimeClassAbilityGrant.Decide). Both are written over already-read
    /// values so they can be tested without a live Player - ACE.Server.Tests can never build one, the
    /// Player constructor reaches DatabaseManager.Authentication - and without PropertyManager, whose
    /// reads throw in this harness.
    /// </summary>
    [TestClass]
    public class MlRelariaTrophyTests
    {
        private const int BossMarker = 1004120;

        // ---- the drop side ---------------------------------------------------------------------------

        /// <summary>The baseline positive: a dug-up Relaria, killed by a player who has neither claimed
        /// nor is already carrying a trophy.</summary>
        [TestMethod]
        public void ShouldDropTrophy_FirstKill_Drops()
        {
            Assert.IsTrue(MlRelariaTrophy.ShouldDropTrophy(BossMarker, killedByPlayer: true,
                alreadyClaimed: false, killerHoldsTrophy: false));
        }

        /// <summary>An ordinary Marae Lassel creature carries no 9066 marker (absent reads as 0) and can
        /// never drop the trophy, however it died. Only MlRelariaSpawner stamps the marker, so this is
        /// also what keeps the hook inert for every non-Relaria death inside the ML gate.</summary>
        [TestMethod]
        public void ShouldDropTrophy_NoBossMarker_DoesNotDrop()
        {
            Assert.IsFalse(MlRelariaTrophy.ShouldDropTrophy(0, killedByPlayer: true,
                alreadyClaimed: false, killerHoldsTrophy: false));

            Assert.IsFalse(MlRelariaTrophy.ShouldDropTrophy(-1, killedByPlayer: true,
                alreadyClaimed: false, killerHoldsTrophy: false));
        }

        /// <summary>A Relaria that died to something other than a player (fall damage, another monster,
        /// an Olthoi player) leaves no trophy.</summary>
        [TestMethod]
        public void ShouldDropTrophy_NotKilledByPlayer_DoesNotDrop()
        {
            Assert.IsFalse(MlRelariaTrophy.ShouldDropTrophy(BossMarker, killedByPlayer: false,
                alreadyClaimed: false, killerHoldsTrophy: false));
        }

        /// <summary>OWNER DECISION 14: a repeat kill by a character that has already claimed drops no
        /// second trophy via THIS decision. The repeat-kill currency/aura payout is a separate path -
        /// see MlRelariaTrophy.TryAwardRepeatKillRewards, exercised by the DecideRepeatKillAuraOutcome
        /// tests below.</summary>
        [TestMethod]
        public void ShouldDropTrophy_AlreadyClaimed_DoesNotDrop()
        {
            Assert.IsFalse(MlRelariaTrophy.ShouldDropTrophy(BossMarker, killedByPlayer: true,
                alreadyClaimed: true, killerHoldsTrophy: false));
        }

        /// <summary>Same decision for a character who banked the first trophy instead of using it.
        /// Without this clause the claim stamp would not exist yet, so a hoarder would collect one trophy
        /// per kill and the "nothing tradeable to farm" half of decision 14 would not hold.</summary>
        [TestMethod]
        public void ShouldDropTrophy_KillerAlreadyHoldsOne_DoesNotDrop()
        {
            Assert.IsFalse(MlRelariaTrophy.ShouldDropTrophy(BossMarker, killedByPlayer: true,
                alreadyClaimed: false, killerHoldsTrophy: true));
        }

        /// <summary>The drop side and the consume side must read and write the SAME quest registry row,
        /// and the weenie must author that same name in PropertyString 9017. Pinned as a literal so a
        /// rename cannot silently split the two halves and quietly make the trophy repeatable.</summary>
        [TestMethod]
        public void ClaimQuestName_MatchesTheAuthoredWeenieValue()
        {
            StringAssert.Matches(MlRelariaTrophy.ClaimQuestName, new System.Text.RegularExpressions.Regex("^MlRelariaTrophy$"),
                "Content/sql/weenies/1004121 Relic of the Unburied.sql authors this exact string in PropertyString 9017");

            Assert.IsTrue(OneTimeClassAbilityGrant.IsValidQuestName(MlRelariaTrophy.ClaimQuestName));
        }

        /// <summary>The trophy wcid the drop creates, pinned against its content file.</summary>
        [TestMethod]
        public void TrophyWcid_IsTheAuthoredWeenie()
        {
            Assert.AreEqual(1004121u, MlRelariaTrophy.TrophyWcid,
                "TrophyWcid must name Content/sql/weenies/1004121 Relic of the Unburied.sql");
        }

        // ---- the one-time claim side -----------------------------------------------------------------

        /// <summary>A character who has not claimed may claim.</summary>
        [TestMethod]
        public void Decide_FirstUse_Allows()
        {
            Assert.AreEqual(OneTimeGrantDecision.Allow,
                OneTimeClassAbilityGrant.Decide("MlRelariaTrophy", hasQuest: false, muleBlocked: false));
        }

        /// <summary>THE POINT OF THE WHOLE STEP: a second trophy is refused. The caller treats any
        /// non-Allow decision as "refuse and do NOT consume", so the second copy stays in the pack.</summary>
        [TestMethod]
        public void Decide_SecondUse_IsAlreadyClaimed()
        {
            Assert.AreEqual(OneTimeGrantDecision.AlreadyClaimed,
                OneTimeClassAbilityGrant.Decide("MlRelariaTrophy", hasQuest: true, muleBlocked: false));
        }

        /// <summary>A mule makes no quest progress, so it can never record a claim - and
        /// QuestManager.Update's own mule guard is a SILENT no-op, which would otherwise leave a mule
        /// holding an item that granted points every time. Refused here with a message instead.</summary>
        [TestMethod]
        public void Decide_Mule_IsRefused()
        {
            Assert.AreEqual(OneTimeGrantDecision.MuleBlocked,
                OneTimeClassAbilityGrant.Decide("MlRelariaTrophy", hasQuest: false, muleBlocked: true));
        }

        /// <summary>"Already claimed" is checked before the mule rule, so a mule is never told it has
        /// already claimed something it has not.</summary>
        [TestMethod]
        public void Decide_MuleThatAlreadyClaimed_ReportsAlreadyClaimed()
        {
            Assert.AreEqual(OneTimeGrantDecision.AlreadyClaimed,
                OneTimeClassAbilityGrant.Decide("MlRelariaTrophy", hasQuest: true, muleBlocked: true));
        }

        /// <summary>An item with no claim quest never reaches Decide in production (Gem.UseGem only calls
        /// it when PropertyString 9017 is set), but a null or empty value arriving here must refuse
        /// rather than grant - a missing ledger name means there is nowhere to record the claim.</summary>
        [TestMethod]
        public void Decide_MissingQuestName_IsMalformed()
        {
            Assert.AreEqual(OneTimeGrantDecision.MalformedQuestName,
                OneTimeClassAbilityGrant.Decide(null, hasQuest: false, muleBlocked: false));

            Assert.AreEqual(OneTimeGrantDecision.MalformedQuestName,
                OneTimeClassAbilityGrant.Decide("", hasQuest: false, muleBlocked: false));
        }

        /// <summary>QuestManager parses '@' as a completion marker and '%' as a format marker, so an
        /// authored name carrying either would be read as a different quest than the one written.
        /// Refused as a content bug.</summary>
        [TestMethod]
        public void Decide_QuestNameWithQuestManagerMarkers_IsMalformed()
        {
            Assert.AreEqual(OneTimeGrantDecision.MalformedQuestName,
                OneTimeClassAbilityGrant.Decide("MlRelariaTrophy@2", hasQuest: false, muleBlocked: false));

            Assert.AreEqual(OneTimeGrantDecision.MalformedQuestName,
                OneTimeClassAbilityGrant.Decide("MlRelaria%Trophy", hasQuest: false, muleBlocked: false));

            Assert.AreEqual(OneTimeGrantDecision.MalformedQuestName,
                OneTimeClassAbilityGrant.Decide("Ml Relaria Trophy", hasQuest: false, muleBlocked: false));
        }

        /// <summary>Every refusal carries player-facing wording; Allow carries none, which is what the
        /// caller keys on.</summary>
        [TestMethod]
        public void RefusalMessage_ExistsForEveryRefusalAndNotForAllow()
        {
            Assert.IsNull(OneTimeClassAbilityGrant.RefusalMessage(OneTimeGrantDecision.Allow, "Relic of the Unburied"));

            Assert.IsFalse(string.IsNullOrEmpty(OneTimeClassAbilityGrant.RefusalMessage(OneTimeGrantDecision.AlreadyClaimed, "Relic of the Unburied")));
            Assert.IsFalse(string.IsNullOrEmpty(OneTimeClassAbilityGrant.RefusalMessage(OneTimeGrantDecision.MuleBlocked, "Relic of the Unburied")));
            Assert.IsFalse(string.IsNullOrEmpty(OneTimeClassAbilityGrant.RefusalMessage(OneTimeGrantDecision.MalformedQuestName, "Relic of the Unburied")));
        }

        // ---- the repeat-kill aura roll -----------------------------------------------------------------

        /// <summary>A chance of 0 must never fire, whatever the draw returns - a roll of exactly 0.0 is a
        /// value ThreadSafeRandom.Next(0.0f, 1.0f) can return, so "roll &lt; chance" alone is not enough.</summary>
        [TestMethod]
        public void DecideRepeatKillAuraOutcome_ZeroChance_NeverFires()
        {
            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.NoChange,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: 0.0, roll: 0.0, alreadyHasAura: false));
        }

        /// <summary>A roll at or above the chance fails.</summary>
        [TestMethod]
        public void DecideRepeatKillAuraOutcome_RollAtOrAboveChance_NoChange()
        {
            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.NoChange,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: 0.05, roll: 0.05, alreadyHasAura: false));

            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.NoChange,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: 0.05, roll: 0.9, alreadyHasAura: false));
        }

        /// <summary>A successful roll for a character who does not yet have the aura grants it.</summary>
        [TestMethod]
        public void DecideRepeatKillAuraOutcome_SuccessWithoutAura_GrantsAura()
        {
            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.GrantAura,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: 0.05, roll: 0.0, alreadyHasAura: false));

            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.GrantAura,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: 0.05, roll: 0.0499, alreadyHasAura: false));
        }

        /// <summary>A successful roll for a character who already has the aura pays bonus doubloons
        /// instead of re-stamping an already-stamped quest row.</summary>
        [TestMethod]
        public void DecideRepeatKillAuraOutcome_SuccessAlreadyHasAura_BonusDoubloons()
        {
            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.BonusDoubloons,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: 0.05, roll: 0.0, alreadyHasAura: true));
        }

        /// <summary>A negative chance is refused the same as zero, never treated as "always fires".</summary>
        [TestMethod]
        public void DecideRepeatKillAuraOutcome_NegativeChance_NeverFires()
        {
            Assert.AreEqual(MlRelariaTrophy.RepeatKillAuraOutcome.NoChange,
                MlRelariaTrophy.DecideRepeatKillAuraOutcome(auraChance: -1.0, roll: 0.0, alreadyHasAura: false));
        }

        /// <summary>The repeat-kill aura quest name, pinned as a literal so a rename cannot silently
        /// change what Player.HasRelariaAura reads.</summary>
        [TestMethod]
        public void RepeatAuraQuestName_IsPinned()
        {
            Assert.AreEqual("MlRelariaAura", MlRelariaTrophy.RepeatAuraQuestName);
        }
    }
}
