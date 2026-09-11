using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// A manual trigger for MarketAdvertiser, separate from MarketAdminCommands' investigation
    /// surface because this one DOES something rather than only reading. Its whole purpose is to let
    /// the owner see how the client renders a guid-0 Trade line (see MarketAdvertiser's doc comment)
    /// before market_ad_interval_hours is turned on for real.
    /// </summary>
    public static class MarketAdvertiserCommands
    {
        [CommandHandler("marketad", AccessLevel.Sentinel, CommandHandlerFlag.RequiresWorld, 0,
            "Posts the periodic Market Trade advert right now, on demand.",
            "Ignores market_ad_interval_hours for POSTING - use this to see how the client renders the\n" +
            "advert's guid-0 sender before enabling the scheduled job. Also reports when the scheduled\n" +
            "job would next fire, given the tunable's current value.")]
        public static void HandleMarketAd(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            if (!MarketManager.Enabled)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    "[MARKETAD] The market is disabled (market_enabled is off); nothing posted.", ChatMessageType.System));
                return;
            }

            // ONE call, guarded by PostNow's own try/catch - this command must never call
            // MarketAdvertiser.BuildLines() directly. A second, unguarded call here (to decide the
            // "nothing to say" message before posting) previously meant a composition failure could
            // propagate up through GameActionTalk.Handle's catch, which only logs, leaving the player
            // with no feedback at all - unlike every other failure path in this command. It also built
            // the lines twice per invocation for no reason.
            var posted = MarketAdvertiser.PostNow(out var nothingToSay);

            session.Network.EnqueueSend(new GameMessageSystemChat(
                nothingToSay ? "[MARKETAD] Nothing to say right now."
                : posted ? "[MARKETAD] Posted the Trade advert."
                : "[MARKETAD] The advert did not post; check the server log.",
                ChatMessageType.System));

            session.Network.EnqueueSend(new GameMessageSystemChat(
                $"[MARKETAD] {ScheduleStatusLine()}", ChatMessageType.System));
        }

        /// <summary>
        /// The one production caller of MarketAdvertiser.NextRunUtc - MarketAdvertiserJob computes its
        /// own slot independently (it needs the last-posted-slot bookkeeping NextRunUtc has no notion
        /// of), so without this the pure scheduling primitive would have no real call site at all.
        /// </summary>
        private static string ScheduleStatusLine()
        {
            var intervalHours = (int)PropertyManager.GetLong("market_ad_interval_hours").Item;

            var next = MarketAdvertiser.NextRunUtc(DateTime.UtcNow, intervalHours);

            return next == null
                ? "The scheduled job is off (market_ad_interval_hours is 0)."
                : $"The scheduled job would next post at {next:yyyy-MM-dd HH:mm} UTC.";
        }
    }
}
