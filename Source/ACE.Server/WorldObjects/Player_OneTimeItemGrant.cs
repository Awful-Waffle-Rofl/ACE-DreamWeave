using ACE.Server.Entity;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // One-time-per-character UseCreateItem payout gate (code review fix, RoZ round 11; see
        // Source/ACE.Server/Entity/OneTimeItemGrant.cs for the full design rationale). The claim ledger
        // is the quest registry - one row per claimed payout, named by the item's PropertyString 9018
        // UseCreateGrantQuest - mirroring Player_ClassAbilityClaim.cs's shape exactly, but deliberately
        // NOT calling into that file's methods: this gate uses QuestManager.CanSolve/Stamp (cooldown-aware, matching
        // Player_Inventory.VerifyQuest's own pickup-gate primitives) where the class-ability grant uses
        // QuestManager.HasQuest/Update (a permanent, cooldown-blind "ever claimed" check, correct for a
        // lifetime CAP trophy but not for an item whose quest row is authored with a real min_Delta).
        //
        // Split into a read-only Try* and a separate Complete* on purpose, same reason as the CAP grant:
        // the payout is placed by Gem.HandleUseCreateItem, which can refuse (full pack, out of inventory
        // slots) after this gate has already said Allow. If the claim were stamped before that call and
        // the call then failed, the character would have burned its one payout and received nothing. See
        // OneTimeItemGrant's remarks for the contract.

        /// <summary>
        /// Whether this character may claim the one-time UseCreateItem payout named by
        /// <paramref name="questName"/>. Reads state and writes NOTHING on any path, so a refusal leaves
        /// the item unconsumed and the character untouched. <paramref name="error"/> is the
        /// player-facing refusal, null on success.
        /// </summary>
        public bool TryBeginOneTimeItemGrant(string questName, string itemName, out string error)
        {
            // PvP template (progression lock): the stamp would be silently dropped for a templated player, leaving a
            // repeatable payout item. Refused before the decision, writing nothing.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.QuestStamp);
            if (templateRefusal != null)
            {
                error = templateRefusal;
                return false;
            }

            var decision = OneTimeItemGrant.Decide(questName,
                QuestManager.CanSolve(questName),
                MuleBlocked(MuleAction.AdvanceQuest, notify: false));

            error = OneTimeItemGrant.RefusalMessage(decision, itemName);

            return decision == OneTimeItemGrantDecision.Allow;
        }

        /// <summary>
        /// Records the claim, once the payout items have actually been created and placed. Stamped via
        /// QuestManager.Stamp (== Update), the same primitive VerifyQuest's own pickup gate uses, so the
        /// quest row's min_Delta/max_Solves genuinely govern both gates on the same item consistently.
        /// </summary>
        public void CompleteOneTimeItemGrant(string questName)
        {
            if (!OneTimeItemGrant.IsValidQuestName(questName))
                return;

            QuestManager.Stamp(questName);

            SaveCharacterToDatabase();
        }
    }
}
