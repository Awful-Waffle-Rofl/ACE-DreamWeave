using System;

using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WaveEncounters;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /waveencounter: developer tooling for the object-anchored wave runner (ACE.Server.WaveEncounters).
    ///
    ///   /waveencounter status     - every live encounter and every anchor cooldown. Read-only.
    ///   /waveencounter stop       - ends every live encounter as a failure through the ordinary end path
    ///                               (remaining creatures destroyed without dying, cooldown started).
    ///   /waveencounter cooldowns  - clears every anchor cooldown, so an anchor can be restarted at once.
    /// </summary>
    public static class WaveEncounterCommands
    {
        [CommandHandler("waveencounter", AccessLevel.Developer, CommandHandlerFlag.None, 0,
            "Wave encounter tools: status, stop (ends every live encounter), cooldowns (clears every anchor cooldown).",
            "[status|stop|cooldowns]")]
        public static void HandleWaveEncounter(Session session, params string[] parameters)
        {
            var sub = parameters != null && parameters.Length > 0 ? parameters[0] : "status";

            if (string.Equals(sub, "stop", StringComparison.OrdinalIgnoreCase))
            {
                var stopped = WaveEncounterManager.StopAll();
                CommandHandlerHelper.WriteOutputInfo(session, stopped == 0
                    ? "No wave encounter is running."
                    : $"Stopping {stopped} wave encounter(s) on the next world tick.", ChatMessageType.Broadcast);
                return;
            }

            if (string.Equals(sub, "cooldowns", StringComparison.OrdinalIgnoreCase))
            {
                var cleared = WaveEncounterManager.ClearCooldowns();
                CommandHandlerHelper.WriteOutputInfo(session, $"Cleared {cleared} wave encounter cooldown(s).", ChatMessageType.Broadcast);
                return;
            }

            if (!string.Equals(sub, "status", StringComparison.OrdinalIgnoreCase))
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Usage: /waveencounter [status|stop|cooldowns]", ChatMessageType.Broadcast);
                return;
            }

            foreach (var line in WaveEncounterManager.StatusLines())
                CommandHandlerHelper.WriteOutputInfo(session, line, ChatMessageType.Broadcast);
        }
    }
}
