using log4net;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /weblogin reply text. The "[WEB] " lines are a wire contract parsed by a client plugin: keep them byte-exact.
    /// </summary>
    internal static class WebLaunchCommandText
    {
        internal const string Prefix = "[WEB] ";
        internal const string DisabledText = "[WEB] Signing in to the website from the game is turned off on this server.";
        internal const string UnavailableText = "[WEB] The website sign-in service is not available right now.";
        internal const string TooQuicklyText = "[WEB] You are requesting website sign-in codes too quickly. Wait a minute and try again.";

        internal const string IssuedPrefix = "[WEB] Launch code: ";

        internal static string Issued(string code) => $"{IssuedPrefix}{code} (single use, expires in {WebLaunchCodeStore.LifetimeSeconds} seconds)";
    }

    /// <summary>
    /// /weblogin: issues a one-time code that signs the player in to the DreamWeave website. Memory-only,
    /// so it replies inline on the world thread. The store's issue rate is the throttle. The code is never logged.
    /// </summary>
    public static class WebLaunchCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [CommandHandler("weblogin", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Get a one-time code that signs you in to the DreamWeave website.", "")]
        public static void HandleWebLogin(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var enabled = PropertyManager.GetBool("web_launch_enabled").Item;

            var reply = Execute(WebLaunchCodeStore.Live, enabled, MarketApiHost.IsRunning, session.AccountId, player.Guid.Full);

            if (reply.StartsWith(WebLaunchCommandText.IssuedPrefix, System.StringComparison.Ordinal))
                log.Info($"[WEBLAUNCH] issued for account {session.AccountId} character 0x{player.Guid.Full:X8}");

            session.Network.EnqueueSend(new GameMessageSystemChat(reply, ChatMessageType.System));
        }

        /// <summary>
        /// The decision logic, free of Session/Player/PropertyManager so it tests against its own store.
        /// </summary>
        internal static string Execute(WebLaunchCodeStore store, bool enabled, bool hostRunning, uint accountId, uint characterGuid)
        {
            if (!enabled)
                return WebLaunchCommandText.DisabledText;

            if (store == null || !hostRunning || accountId == 0)
                return WebLaunchCommandText.UnavailableText;

            if (!store.TryIssue(accountId, characterGuid, out var code))
                return WebLaunchCommandText.TooQuicklyText;

            return WebLaunchCommandText.Issued(code);
        }
    }
}
