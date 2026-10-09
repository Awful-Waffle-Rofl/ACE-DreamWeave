using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // One-time-per-character class ability point grants (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md
        // section 6 "Trophy", step 11). The claim ledger is the quest registry - one row per claimed
        // grant, named by the item's PropertyString 9017 ClassAbilityGrantQuest - NOT a new player biota
        // property, mirroring Player_PickupBoons.cs.
        //
        // Split into a read-only Try* and a separate Complete* on purpose. The points are paid by
        // Player.GrantClassAbilityPoints, which is the ONLY sanctioned way to move PropertyInt 9017 and
        // 9018 together; if the claim were stamped before that call and the call then refused, the
        // character would have burned its single claim and received nothing. See
        // OneTimeClassAbilityGrant's remarks for the contract.

        /// <summary>
        /// Whether this character may claim the one-time grant named by <paramref name="questName"/>.
        /// Reads state and writes NOTHING on any path, so a refusal leaves the item unconsumed and the
        /// character untouched. <paramref name="error"/> is the player-facing refusal, null on success.
        /// </summary>
        public bool TryBeginOneTimeClassAbilityGrant(string questName, string itemName, out string error)
        {
            // Explicit mule check, mirroring Player_PickupBoons.TryClaimPickupBoon: QuestManager.Update has
            // its own internal MuleBlocked(AdvanceQuest) guard, but that guard is a SILENT no-op, so a mule
            // would otherwise reach CompleteOneTimeClassAbilityGrant, have its stamp quietly dropped, and be
            // left holding a repeatable CAP item. Refusing here refuses cleanly and with a message.
            // PvP template (progression lock): the same drop happens for a templated player, so refuse first.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.QuestStamp);
            if (templateRefusal != null)
            {
                error = templateRefusal;
                return false;
            }

            var decision = OneTimeClassAbilityGrant.Decide(questName,
                QuestManager.HasQuest(questName),
                MuleBlocked(MuleAction.AdvanceQuest, notify: false));

            error = OneTimeClassAbilityGrant.RefusalMessage(decision, itemName);

            return decision == OneTimeGrantDecision.Allow;
        }

        /// <summary>
        /// Records the claim, once the points have actually been paid. Stamped via QuestManager.Update
        /// rather than Character.GetOrCreateQuest, which would bypass the mule guard - see the trap noted
        /// in Player_ClassAbilities.LearnClassAbility and Player_PickupBoons.TryClaimPickupBoon.
        /// </summary>
        public void CompleteOneTimeClassAbilityGrant(string questName)
        {
            if (!OneTimeClassAbilityGrant.IsValidQuestName(questName))
                return;

            QuestManager.Update(questName);

            SaveCharacterToDatabase();
        }
    }
}
