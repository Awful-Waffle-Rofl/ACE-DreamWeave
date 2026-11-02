using ACE.Common;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Called once per ClassAbilityBuffsHeartbeat (Player_ClassAbilityBuffs.cs). Announces the Hemomancy
        /// heal-armed damage buff's active -> inactive lapse exactly once. The state lives in
        /// Player_Hemomancy.cs; a player who never armed the buff has no state and costs one null check.
        /// </summary>
        private void TickHemomancyExpiry()
        {
            var state = hemomancyState;

            if (state == null)
                return;

            if (state.TryConsumeExpiry(Time.GetUnixTime()))
                SendClassAbilityBuffMessage("Hemomancy fades.");
        }
    }
}
