using System;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Called only from QuestManager.PayBluespireLadderReward, which has already filtered to the six
        /// rung-clear names: with firstClear:true from the created-row branches (Update and
        /// SetQuestCompletions), and with firstClear:false from Update's existing-row branch for a re-clear
        /// landed after the rung's cooldown, whether the rung was cleared by an NPC StampQuest or by a boss
        /// kill. See BluespireLadderRewards.Pay's firstClear parameter for the wording/log split; the amount
        /// paid is identical either way.
        ///
        /// Wrapped in its own try/catch because it runs INSIDE quest bookkeeping: a payout that throws must
        /// not be able to abandon the registry row, the stamp ledger or the contract notification that
        /// surround it. Losing one reward to an exception is bad; losing the quest progress it was paid for
        /// is worse.
        /// </summary>
        public void TryPayBluespireLadderReward(string questName, bool firstClear = true)
        {
            try
            {
                ACE.Server.Entity.BluespireLadderRewards.Pay(this, questName, firstClear);
            }
            catch (Exception ex)
            {
                log.Error($"[BLUESPIRE] ladder reward for '{questName}' threw for {Name}; the quest row itself is unaffected", ex);
            }
        }
    }
}
