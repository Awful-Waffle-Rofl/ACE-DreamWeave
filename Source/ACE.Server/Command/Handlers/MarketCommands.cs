using System;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The Market's in-game surface (Docs/Market/DESIGN.md section 8). THIN like MuleCommands: parses,
    /// applies the cooldown, delegates - every rule lives in MarketManager so the HTTP API shares it too.
    /// Parsing/dispatch are separated for no-Session testability; MuleCommands' untested dispatch swap (grant/revoke, suite stayed green) is why that matters.
    /// </summary>
    public static class MarketCommands
    {
        [CommandHandler("market", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Browse, list and buy items on the market.",
            MarketCommandParser.HelpText)]
        public static void HandleMarket(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            // PvP template (economy lock): no market while templated.
            if (player.PvpTemplateRefuses(ACE.Server.Pvp.Templates.PvpTemplateAction.MarketList))
                return;

            var result = MarketCommandParser.Parse(parameters);

            // The same per-character cooldown /bank applies to its mutating subcommands, and the
            // same field, so the two families share one budget rather than each getting a fresh
            // one a script could alternate between.
            if (IsMutating(result.Kind))
            {
                if ((DateTime.UtcNow - player.LastBankCommandTime).TotalSeconds < 1.0)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("[MARKET] You are using market commands too quickly.", ChatMessageType.System));
                    return;
                }

                player.LastBankCommandTime = DateTime.UtcNow;

                if (player.IsBusy)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("[MARKET] You are too busy - finish what you are doing and try again.", ChatMessageType.System));
                    return;
                }

                // Mirrors the bank's mid-trade block for the same reason: items committed to a
                // trade window must not also be listed or sold.
                if (player.IsTrading)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("[MARKET] You cannot use the market while trading.", ChatMessageType.System));
                    return;
                }
            }

            Dispatch(result, new PlayerMarketCommandTarget(player));
        }

        private static bool IsMutating(MarketCommandKind kind)
            => kind == MarketCommandKind.List || kind == MarketCommandKind.Delist || kind == MarketCommandKind.Buy || kind == MarketCommandKind.Upgrade;

        internal static void Dispatch(MarketCommandResult result, IMarketCommandTarget target)
        {
            switch (result.Kind)
            {
                case MarketCommandKind.Vault:
                    target.ShowVault();
                    break;

                case MarketCommandKind.List:
                    target.ListEntry(result.EntryNumber, result.Price, result.Count);
                    break;

                case MarketCommandKind.Delist:
                    target.Delist(result.ListingId);
                    break;

                case MarketCommandKind.Mine:
                    target.ShowMine(result.Page);
                    break;

                case MarketCommandKind.History:
                    target.ShowHistory(result.Limit);
                    break;

                case MarketCommandKind.Search:
                    target.Search(result.Text);
                    break;

                case MarketCommandKind.Buy:
                    target.Buy(result.ListingId, result.Count);
                    break;

                case MarketCommandKind.Upgrade:
                    target.Upgrade();
                    break;

                case MarketCommandKind.Help:
                    target.ShowHelp();
                    break;

                case MarketCommandKind.UsageError:
                default:
                    target.UsageError(result.UsageError ?? MarketCommandParser.UsageMessage);
                    break;
            }
        }
    }

    internal enum MarketCommandKind
    {
        Vault,
        List,
        Delist,
        Mine,
        History,
        Search,
        Buy,
        Upgrade,
        Help,
        UsageError,
    }

    internal sealed class MarketCommandResult
    {
        public MarketCommandKind Kind;

        /// <summary>1-based, as shown by /market vault.</summary>
        public int EntryNumber;

        public uint ListingId;

        /// <summary>Per unit, in whole MMD.</summary>
        public long Price;

        /// <summary>For List, 0 means the whole entry. For Buy, defaults to 1.</summary>
        public int Count;

        public int Limit;

        /// <summary>For Mine, 1-based page number. Defaults to 1.</summary>
        public int Page = 1;

        public string Text;

        public string UsageError;
    }

    internal static class MarketCommandParser
    {
        public const string HelpText =
            "vault|list|delist|mine|history|search|buy|upgrade ...\n" +
            "/market vault                  - your account vault with entry numbers\n" +
            "/market list <entry#> <price> [count] - list an entry at <price> MMD per unit\n" +
            "/market delist <listing#>      - take one of your listings down\n" +
            "/market mine [page]            - your active listings, 25 per page\n" +
            "/market history [n]            - your last n market transactions (default 10)\n" +
            "/market search <text>          - active listings whose name contains <text>\n" +
            "/market buy <listing#> [count] - buy from a listing\n" +
            "/market upgrade                - buy +10 listing slots for your account (price rises each time)\n" +
            "Prices are in Trade Notes (250,000), paid from and into your banked pyreals.";

        public const string UsageMessage = "[MARKET] Type /market for the list of commands.";

        /// <summary>The default depth of /market history when no count is given.</summary>
        public const int DefaultHistoryLimit = 10;

        public static MarketCommandResult Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return new MarketCommandResult { Kind = MarketCommandKind.Help };

            var sub = parameters[0].ToLowerInvariant();

            switch (sub)
            {
                case "help":
                case "?":
                    return new MarketCommandResult { Kind = MarketCommandKind.Help };

                case "vault":
                case "v":
                    return new MarketCommandResult { Kind = MarketCommandKind.Vault };

                case "mine":
                case "m":
                {
                    var page = 1;

                    if (parameters.Length > 1 && (!int.TryParse(parameters[1], out page) || page < 1))
                        return Usage("Usage: /market mine [page]");

                    return new MarketCommandResult { Kind = MarketCommandKind.Mine, Page = page };
                }

                case "history":
                case "h":
                {
                    var limit = DefaultHistoryLimit;

                    if (parameters.Length > 1 && (!int.TryParse(parameters[1], out limit) || limit < 1))
                        return Usage("Usage: /market history [n]");

                    return new MarketCommandResult { Kind = MarketCommandKind.History, Limit = limit };
                }

                case "search":
                case "s":
                {
                    if (parameters.Length < 2)
                        return Usage("Usage: /market search <text>");

                    // The client splits chat on spaces, so a multi-word search arrives as several
                    // parameters and must be rejoined or only the first word ever searches.
                    var text = string.Join(" ", parameters.Skip(1)).Trim();

                    if (text.Length == 0)
                        return Usage("Usage: /market search <text>");

                    return new MarketCommandResult { Kind = MarketCommandKind.Search, Text = text };
                }

                case "list":
                case "l":
                {
                    if (parameters.Length < 3)
                        return Usage("Usage: /market list <entry#> <price> [count]");

                    if (!int.TryParse(parameters[1], out var entry) || entry < 1)
                        return Usage("The entry number comes from /market vault.");

                    // Zero is a giveaway. Negative is not a price at all. This gate is INDEPENDENT of
                    // MarketManager.List's own bounds check and sits in front of it, so both have to
                    // agree about what is legal or the in-game path refuses what the web path allows.
                    if (!long.TryParse(parameters[2], out var price) || price < 0)
                        return Usage("Choose a price of zero or more trade notes. Zero gives the item away.");

                    var count = 0;

                    if (parameters.Length > 3 && (!int.TryParse(parameters[3], out count) || count < 1))
                        return Usage("Usage: /market list <entry#> <price> [count]");

                    return new MarketCommandResult
                    {
                        Kind = MarketCommandKind.List,
                        EntryNumber = entry,
                        Price = price,
                        Count = count,
                    };
                }

                case "delist":
                case "d":
                {
                    if (parameters.Length < 2 || !uint.TryParse(parameters[1], out var listingId) || listingId == 0)
                        return Usage("Usage: /market delist <listing#>");

                    return new MarketCommandResult { Kind = MarketCommandKind.Delist, ListingId = listingId };
                }

                case "buy":
                case "b":
                {
                    if (parameters.Length < 2 || !uint.TryParse(parameters[1], out var listingId) || listingId == 0)
                        return Usage("Usage: /market buy <listing#> [count]");

                    var count = 1;

                    if (parameters.Length > 2 && (!int.TryParse(parameters[2], out count) || count < 1))
                        return Usage("Usage: /market buy <listing#> [count]");

                    return new MarketCommandResult { Kind = MarketCommandKind.Buy, ListingId = listingId, Count = count };
                }

                case "upgrade":
                case "u":
                    return new MarketCommandResult { Kind = MarketCommandKind.Upgrade };

                default:
                    return Usage(UsageMessage);
            }
        }

        private static MarketCommandResult Usage(string message)
            => new MarketCommandResult { Kind = MarketCommandKind.UsageError, UsageError = message };
    }

    /// <summary>
    /// The seam Dispatch is driven through, so the switch is testable without a live Session or
    /// Player. One method per kind, deliberately: a recording double can then assert exactly one
    /// distinct call per arm and a transposition fails the test.
    /// </summary>
    internal interface IMarketCommandTarget
    {
        void ShowVault();
        void ListEntry(int entryNumber, long price, int count);
        void Delist(uint listingId);
        void ShowMine(int page);
        void ShowHistory(int limit);
        void Search(string text);
        void Buy(uint listingId, int count);
        void Upgrade();
        void ShowHelp();
        void UsageError(string message);
    }

    /// <summary>
    /// Production target: a thin adapter over MarketManager, writing to the persistent chat log
    /// rather than to transient floating text - a listing number or a price is something a player
    /// needs to be able to read twice.
    /// </summary>
    internal sealed class PlayerMarketCommandTarget : IMarketCommandTarget
    {
        private readonly Player player;

        public PlayerMarketCommandTarget(Player player)
        {
            this.player = player;
        }

        private void Msg(string message)
            => player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[MARKET] {message}", ChatMessageType.System));

        private MarketActor Actor => new MarketActor(player.Account?.AccountId ?? 0, player.Guid.Full, player.Name);

        public void ShowVault()
        {
            if (!MarketManager.TryGetVaultEntries(Actor.AccountId, out var lines, out var error))
            {
                Msg(MarketErrorCodes.ToMessage(error));
                return;
            }

            if (lines.Count == 0)
            {
                Msg("Your vault is empty.");
                return;
            }

            var text = new StringBuilder("Your vault:\n");

            foreach (var line in lines)
                text.Append(line).Append('\n');

            text.Append("Use /market list <entry#> <price> [count] to sell one.");

            Msg(text.ToString());
        }

        public void ListEntry(int entryNumber, long price, int count)
        {
            var result = MarketManager.ListByEntryNumber(Actor, entryNumber, price, count, MarketChannel.InGame);

            Msg(result.Ok
                ? $"Listed {result.Value.Count} x {result.Value.Snapshot?.Name ?? "item"} at {price:N0} MMD each. Listing #{result.Value.Id}."
                : MarketErrorCodes.ToMessage(result.Error));
        }

        public void Delist(uint listingId)
        {
            var result = MarketManager.Delist(Actor, listingId, MarketChannel.InGame);

            Msg(result.Ok ? $"Listing #{listingId} taken down." : MarketErrorCodes.ToMessage(result.Error));
        }

        /// <summary>Listings shown per page of /market mine.</summary>
        internal const int MineListingsPerPage = 25;

        public void ShowMine(int page)
        {
            var mine = MarketManager.GetActiveListingsForAccount(Actor.AccountId);

            if (mine.Count == 0)
            {
                Msg("You have no active listings.");
                return;
            }

            var cap = MarketManager.EffectiveListingCap(Actor.AccountId);
            var pageCount = Math.Max(1, (mine.Count + MineListingsPerPage - 1) / MineListingsPerPage);

            // Out of range clamps to the last page rather than refusing - a stale bookmark ("page 4"
            // after some listings sold) should still show something.
            if (page > pageCount)
                page = pageCount;
            if (page < 1)
                page = 1;

            var text = new StringBuilder($"Your listings (page {page} of {pageCount}, {mine.Count:N0} active, limit {cap:N0}):\n");

            foreach (var listing in mine.Skip((page - 1) * MineListingsPerPage).Take(MineListingsPerPage))
                text.Append($"#{listing.Id}  {listing.Count} x {listing.Snapshot?.Name ?? $"Item {listing.Wcid}"}  {listing.PriceMmd:N0} MMD each\n");

            if (page < pageCount)
                text.Append($"Type /market mine {page + 1} for more.");

            Msg(text.ToString());
        }

        public void ShowHistory(int limit)
        {
            var history = MarketManager.GetHistoryForCharacterAccount(Actor.AccountId, limit);

            if (history.Count == 0)
            {
                Msg("You have no market history.");
                return;
            }

            var text = new StringBuilder($"Your last {history.Count} market transaction(s):\n");

            foreach (var tx in history)
            {
                var sold = tx.SellerAccountId == Actor.AccountId;
                var counterparty = sold ? tx.BuyerCharacterName : tx.SellerCharacterName;

                text.Append($"{tx.Timestamp:yyyy-MM-dd HH:mm} {(sold ? "sold" : "bought")} {tx.Count} x {tx.ItemName} for {tx.PriceMmdTotal:N0} MMD ({(sold ? "to" : "from")} {counterparty}, {tx.Channel.ToString().ToLowerInvariant()}, {tx.Status.ToString().ToLowerInvariant()})\n");
            }

            Msg(text.ToString());
        }

        public void Search(string text)
        {
            var hits = MarketManager.SearchByName(text, MarketManager.MaxSearchRows);

            if (hits.Count == 0)
            {
                Msg($"Nothing listed matches '{text}'.");
                return;
            }

            var lines = new StringBuilder($"Listings matching '{text}':\n");

            foreach (var listing in hits)
                lines.Append($"#{listing.Id}  {listing.Count} x {listing.Snapshot?.Name ?? $"Item {listing.Wcid}"}  {listing.PriceMmd:N0} MMD each  ({listing.SellerCharacterName})\n");

            lines.Append("Use /market buy <listing#> [count].");

            Msg(lines.ToString());
        }

        public void Buy(uint listingId, int count)
        {
            var listing = MarketManager.GetListing(listingId);

            if (listing == null)
            {
                Msg(MarketErrorCodes.ToMessage(MarketError.ListingNotActive));
                return;
            }

            // In game there is no stale-price problem to guard against - the player is quoting the
            // number this same process just showed them - so the expected price IS the live price.
            // The web app's expected_price_mmd exists because its index can be seconds behind.
            var result = MarketManager.Buy(Actor, listingId, count, listing.PriceMmd, MarketChannel.InGame);

            if (!result.Ok)
            {
                Msg(result.Error == MarketError.PriceChanged
                    ? $"{MarketErrorCodes.ToMessage(result.Error)} It is now {result.CurrentPriceMmd:N0} MMD each."
                    : MarketErrorCodes.ToMessage(result.Error));
                return;
            }

            Msg($"Bought {result.Value.Count} x {result.Value.ItemName} for {result.Value.PriceMmdTotal:N0} MMD. It is in your vault - use /mule to collect it.");
        }

        public void Upgrade() => ACE.Server.Entity.CapacityUpgrades.CapacityUpgradeBroker.RequestUpgrade(player, CapacityUpgradeKind.MarketListings);

        public void ShowHelp() => Msg(MarketCommandParser.HelpText);

        public void UsageError(string message) => Msg(message);
    }
}
