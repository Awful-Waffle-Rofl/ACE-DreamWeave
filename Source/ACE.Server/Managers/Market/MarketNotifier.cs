using System;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;

using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The in-game "your sale went through" line, for both channels of a completed purchase.
    ///
    /// It hangs off <see cref="MarketManager.Buy"/> AFTER the row is Completed and after analytics,
    /// so it can never be part of an unwind: a sale is not less sold because a chat line was not
    /// delivered. Every send is wrapped and logged at Debug, because Buy's caller is a Kestrel
    /// request on the web channel and an escaping exception there would answer 500 on a purchase
    /// that actually succeeded.
    ///
    /// THREADING: the send needs no marshalling onto the world thread.
    /// NetworkSession.EnqueueSend is documented "This may be called from many threads" and takes the
    /// per-group bundle lock around every mutation, and the market money path already relies on
    /// exactly that - BankMarketWallet.TryCredit reaches RefreshOnlineCoin ->
    /// SendSpendableCoinValue -> Session.Network.EnqueueSend on the caller's thread. A chat line is
    /// strictly less than what the credit beside it already does.
    /// </summary>
    public static class MarketNotifier
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Teal (0x0D), the client-verified Advancement colour. Loud enough to catch a player mid-fight
        /// without wearing the red of a combat line or the pink of a system error.
        /// </summary>
        public const ChatMessageType Channel = ChatMessageType.Advancement;

        /// <summary>
        /// How one line reaches one character. Settable so a test needs no Session; the default
        /// delivers only if that character is online right now and does nothing at all if it is not.
        /// An offline counterparty is a normal outcome here, never an error.
        /// </summary>
        public static Action<uint, string> Send { get; set; } = SendToOnlineCharacter;

        /// <summary>
        /// "3x Obsidian Hammer", or bare "Obsidian Hammer" for a single unit. A "1x" prefix reads as
        /// machine output in a chat window, which is the whole reason this is not just a format string.
        /// </summary>
        private static string Quantity(int count, string itemName)
            => count == 1 ? itemName : $"{count}x {itemName}";

        public static string SellerLine(int count, string itemName, string buyerName, long totalMmd)
            => $"[Market] Sold {Quantity(count, itemName)} to {buyerName} for {totalMmd:N0} MMD.";

        public static string BuyerLine(int count, string itemName, string sellerName, long totalMmd)
            => $"[Market] Bought {Quantity(count, itemName)} from {sellerName} for {totalMmd:N0} MMD. It is in your vault.";

        public static string FillSellerLine(int count, string itemName, string buyerName, long totalMmd)
            => $"[Market] Sold {Quantity(count, itemName)} to {buyerName}'s buy order for {totalMmd:N0} MMD.";

        public static string FillBuyerLine(int count, string itemName, string sellerName, long totalMmd)
            => $"[Market] {sellerName} sold {Quantity(count, itemName)} into your buy order for {totalMmd:N0} MMD. It is in your vault.";

        /// <summary>Both sides of a completed fill, each only if online. The seller acted on the web, the buyer was absent by definition, so both are told.</summary>
        public static void NotifyFill(ShardMarketTransaction row)
        {
            if (row == null) return;
            try
            {
                Notify(row.SellerCharacterGuid, FillSellerLine(row.Count, row.ItemName, row.BuyerCharacterName, row.PriceMmdTotal));
                Notify(row.BuyerCharacterGuid, FillBuyerLine(row.Count, row.ItemName, row.SellerCharacterName, row.PriceMmdTotal));
            }
            catch (Exception ex)
            {
                log.Debug($"[MARKET] NotifyFill failed for transaction {row.Id}: {ex.GetFullMessage()}");
            }
        }

        /// <summary>
        /// Tells both sides of a completed sale, each only if they are online.
        ///
        /// The SELLER is told on both channels: they were not present for the sale by definition, and
        /// on the web channel this is the only signal they ever get. The BUYER is told on the WEB
        /// channel only - the in-game /market buy handler already prints its own confirmation
        /// (MarketCommands.Buy), and a second line would say the same thing twice.
        /// </summary>
        public static void NotifySale(ShardMarketTransaction row)
        {
            if (row == null)
                return;

            try
            {
                Notify(row.SellerCharacterGuid,
                       SellerLine(row.Count, row.ItemName, row.BuyerCharacterName, row.PriceMmdTotal));

                if ((MarketChannel)row.Channel != MarketChannel.InGame)
                    Notify(row.BuyerCharacterGuid,
                           BuyerLine(row.Count, row.ItemName, row.SellerCharacterName, row.PriceMmdTotal));
            }
            catch (Exception ex)
            {
                // Debug, not Error: the sale is complete and correct either way, and this must never
                // read as a money or custody problem in the log beside the lines that are.
                log.Debug($"[MARKET] could not deliver a sale notification for transaction {row.Id}: {ex.GetFullMessage()}");
            }
        }

        private static void Notify(uint characterGuid, string text)
        {
            if (characterGuid == 0 || string.IsNullOrEmpty(text))
                return;

            try
            {
                Send?.Invoke(characterGuid, text);
            }
            catch (Exception ex)
            {
                log.Debug($"[MARKET] could not deliver a sale notification to character 0x{characterGuid:X8}: {ex.GetFullMessage()}");
            }
        }

        private static void SendToOnlineCharacter(uint characterGuid, string text)
        {
            var player = PlayerManager.GetOnlinePlayer(characterGuid);

            player?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, Channel));
        }
    }
}
