using System;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The periodic "the Market exists" line to Trade chat: names the newest active listing, points at
    /// the web market, and reminds players of /bank and /mule. Ships DISABLED
    /// (market_ad_interval_hours defaults to 0) - see the doc comment on BroadcastToTradeChannel for
    /// why turning it on is a live-client question this source cannot answer alone.
    /// </summary>
    public static class MarketAdvertiser
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>A composed line is truncated toward this length, not the wire's 128-byte cap, so there is headroom for the "..." and for the >=128 name/length bug documented on GameMessageTurbineChat.</summary>
        private const int MaxLineLength = 120;

        /// <summary>
        /// How the finished lines reach players. Settable so a test needs no Session - exactly the
        /// seam MarketNotifier.Send already uses. Defaults to the real Trade-channel broadcast.
        /// </summary>
        public static Action<IReadOnlyList<string>> Send { get; set; } = BroadcastToTradeChannel;

        /// <summary>
        /// The lines to post, or an empty list when there is nothing to say. Settable, purely so a
        /// test can simulate a composition failure (MarketManager internals throwing) without needing
        /// to actually break MarketManager to do it - PostNow's guard is what a test like that is
        /// really pinning. Every real caller still just writes MarketAdvertiser.BuildLines().
        /// </summary>
        public static Func<IReadOnlyList<string>> BuildLines { get; set; } = ComposeLines;

        private static IReadOnlyList<string> ComposeLines()
        {
            var siteUrl = PropertyManager.GetString("market_ad_site_url").Item;

            var lines = new List<string>
            {
                Truncate($"[Market] The Market is open at {siteUrl} - sign in with your game account name and password.")
            };

            var newest = MarketManager.GetNewestActiveListing();
            if (newest != null)
            {
                var name = newest.Snapshot.Name;
                var prefix = "[Market] Newest listing: ";
                var suffix = $" at {newest.PriceMmd:N0} MMD each.";

                // "1x" reads as machine output in a chat window - MarketNotifier.Quantity is the
                // existing precedent for dropping the count prefix on a single unit.
                var quantified = newest.Count == 1 ? name : $"{newest.Count}x {name}";

                var line = prefix + quantified + suffix;
                if (line.Length > MaxLineLength)
                {
                    // Truncate the ITEM NAME, not the whole line, so the price and the "Newest
                    // listing:" framing always survive - a cut-off price would be actively misleading.
                    var room = MaxLineLength - prefix.Length - suffix.Length - "...".Length
                               - (newest.Count == 1 ? 0 : $"{newest.Count}x ".Length);
                    var shortName = room > 0 ? name.Substring(0, Math.Min(name.Length, room)) + "..." : "...";
                    quantified = newest.Count == 1 ? shortName : $"{newest.Count}x {shortName}";
                    line = prefix + quantified + suffix;
                }

                lines.Add(line);
            }

            lines.Add(Truncate("[Market] Use /bank to hold your MMD and /mule to reach your account vault from MP."));

            return lines;
        }

        private static string Truncate(string line)
            => line.Length <= MaxLineLength ? line : line.Substring(0, MaxLineLength - 3) + "...";

        /// <summary>Posts the advert now, unconditionally of the schedule. See the out-parameter overload for the full contract.</summary>
        public static bool PostNow() => PostNow(out _);

        /// <summary>
        /// Posts the advert now, unconditionally of the schedule - used by the /marketad admin command
        /// and by MarketAdvertiserJob when a slot comes due. Returns false, and posts nothing, when the
        /// market is off, there is nothing to say (nothingToSay is set true in that one case only), or
        /// composing/sending the advert throws.
        ///
        /// The whole body is ONE try/catch covering BuildLines() as well as Send, deliberately: this is
        /// the ONLY place either is called from /marketad (MarketAdvertiserCommands.HandleMarketAd no
        /// longer calls BuildLines() directly), so a composition failure that reached the command
        /// unguarded would surface as a silent no-op there (GameActionTalk.Handle's own catch only
        /// logs) instead of the player-visible failure message every other path in that command gives.
        /// </summary>
        public static bool PostNow(out bool nothingToSay)
        {
            nothingToSay = false;

            if (!MarketManager.Enabled)
                return false;

            try
            {
                var lines = BuildLines();
                if (lines.Count == 0)
                {
                    nothingToSay = true;
                    return false;
                }

                Send?.Invoke(lines);
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] the trade advert failed to send: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// The next wall-clock UTC instant, strictly after nowUtc, at which the advert should fire.
        /// Null means disabled.
        ///
        /// The schedule is aligned to UTC midnight, not to whenever the server happened to start: with
        /// intervalHours = 6 the slots are 00:00, 06:00, 12:00, 18:00 every day. When intervalHours does
        /// not divide 24 evenly (e.g. 5 -> 00, 05, 10, 15, 20) the schedule still restarts from each UTC
        /// midnight, so the last gap before the next midnight is shorter than the rest; that is
        /// intentional rather than a bug to smooth over. intervalHours is clamped to at most 24, so a
        /// larger value collapses to one post a day at 00:00 UTC.
        /// </summary>
        public static DateTime? NextRunUtc(DateTime nowUtc, int intervalHours)
        {
            if (intervalHours <= 0)
                return null;

            if (intervalHours > 24)
                intervalHours = 24;

            var midnight = nowUtc.Date;
            for (var hour = 0; hour < 24; hour += intervalHours)
            {
                var candidate = midnight.AddHours(hour);
                if (candidate > nowUtc)
                    return candidate;
            }

            // Every slot today has passed - the next one is at (or past) tomorrow's midnight.
            return midnight.AddDays(1);
        }

        /// <summary>
        /// GameMessageTurbineChat's own length-prefix branch for a sender name >=128 chars is
        /// documented buggy (it measures the MESSAGE's length instead of the sender name's), which
        /// corrupts the packet for every recipient, not just whoever set the tunable. Internal, so a
        /// test can pin this without a live client or a real send: market_ad_sender_name's own
        /// description asks operators to keep it short, but an operator config value must never be the
        /// only thing standing between a typo and a broken packet for the whole channel.
        /// </summary>
        internal static string ClampSenderName(string senderName)
            => senderName != null && senderName.Length >= 128 ? senderName.Substring(0, 127) : senderName;

        /// <summary>
        /// senderID is written to the wire as the speaker's object guid (see the doc comment on
        /// GameMessageTurbineChat), and there is no speaking player here, so 0 is sent. Whether the
        /// client renders that as a blank name, the literal sender-name string, or refuses the line
        /// outright is a LIVE-CLIENT question that server source cannot answer - that is exactly why
        /// this feature ships disabled and carries the /marketad admin command, so the owner can see
        /// the real render before turning the timer on.
        ///
        /// Squelch is deliberately NOT applied: it is keyed on a sending Player object, and there is no
        /// sender to key it on.
        ///
        /// Deliberately does NOT call DiscordRelayManager.QueueMessage. The repo owner ruled this
        /// advert is in-game only - do not "helpfully" add a Discord relay for it later.
        /// </summary>
        private static void BroadcastToTradeChannel(IReadOnlyList<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return;

            if (PropertyManager.GetBool("chat_disable_trade").Item || !PropertyManager.GetBool("use_turbine_chat").Item)
                return;

            var senderName = ClampSenderName(PropertyManager.GetString("market_ad_sender_name").Item);

            // One GameMessageTurbineChat per line, built once and reused across every recipient -
            // exactly what TurbineChatHandler does for a player-sent Trade message.
            var messages = new GameMessageTurbineChat[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                messages[i] = new GameMessageTurbineChat(
                    ChatNetworkBlobType.NETBLOB_EVENT_BINARY,
                    ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                    TurbineChatChannel.Trade,
                    senderName,
                    lines[i],
                    0,
                    ChatType.Trade);
            }

            foreach (var recipient in PlayerManager.GetAllOnline())
            {
                if (recipient == null || recipient.Session?.Network == null)
                    continue;

                if (!recipient.GetCharacterOption(CharacterOption.ListenToTradeChat) || recipient.IsOlthoiPlayer)
                    continue;

                foreach (var message in messages)
                    recipient.Session.Network.EnqueueSend(message);
            }
        }
    }
}
