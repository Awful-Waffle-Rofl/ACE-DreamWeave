using System;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Called from QuestManager's created-row branches (Update and SetQuestCompletions) for EVERY quest
        /// row this character has ever created, and returns immediately for the overwhelming majority that
        /// are not Bluespire ladder clears. The rung lookup is a dictionary miss, which is the whole cost on
        /// that path.
        ///
        /// Also called from QuestManager.HandleKillTask with firstClear:false for the ladder's daily repeat
        /// payout - a bootstrap row re-stamped after its cooldown elapsed. See BluespireLadderRewards.Pay's
        /// firstClear parameter for the wording/log split; the amount paid is identical either way.
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
