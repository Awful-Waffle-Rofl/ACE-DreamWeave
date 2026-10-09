using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using MarketRejectedAttempt = ACE.Database.Models.Shard.MarketRejectedAttempt;

using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;
using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The market's investigation surface (Docs/Market/DESIGN.md section 8.1). READ ONLY - it answers
    /// "what happened", never "make it right"; there is deliberately no refund, no force-delist and no
    /// row edit here, so the command can sit at Sentinel rather than Admin.
    ///
    /// Structured like MarketCommands: a parser returning a result struct, a dispatch switch, and an
    /// IMarketAdminCommandTarget seam so the switch is testable with no Session and no shard.
    ///
    /// A NULL from any read means the read FAILED and renders as "unreadable", never as "no results" -
    /// telling an investigator a disputed purchase does not exist because MySQL was briefly down is
    /// the one wrong answer this command can give.
    /// </summary>
    public static class MarketAdminCommands
    {
        [CommandHandler("marketadmin", AccessLevel.Sentinel, CommandHandlerFlag.RequiresWorld, 0,
            "Investigate market listings, purchases and refused attempts.",
            MarketAdminCommandParser.HelpText)]
        public static void HandleMarketAdmin(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var result = MarketAdminCommandParser.Parse(parameters);

            // invalidate WRITES (Close on apply) and, unlike every read-only arm above it, changes what
            // players can transact against - so it needs Admin even for its dry run, not the Sentinel
            // level the rest of /marketadmin runs at.
            if (result.Kind == MarketAdminCommandKind.Invalidate && session.AccessLevel < AccessLevel.Admin)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    "[MARKETADMIN] invalidate requires Admin access.",
                    ChatMessageType.System));
                return;
            }

            // Every subcommand except help and the usage error opens a ShardDbContext and BLOCKS on it,
            // inline on a world thread - the same shape, and the same five seconds, as the /mule vault
            // commands (Player_Mule_Vendor.MuleVaultReadCooldownSeconds). Help costs nothing, so
            // gating it would only make the cooldown message harder to understand.
            if (NeedsCooldown(result.Kind))
            {
                if ((DateTime.UtcNow - player.LastMarketAdminCommandTime).TotalSeconds < CooldownSeconds)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat(
                        "[MARKETADMIN] You have used a market admin command too recently. Try again in a few seconds.",
                        ChatMessageType.System));
                    return;
                }

                player.LastMarketAdminCommandTime = DateTime.UtcNow;
            }

            Dispatch(result, new PlayerMarketAdminCommandTarget(player));
        }

        /// <summary>Matches the /mule vault read cooldown; see that constant's remarks for the reasoning.</summary>
        internal const double CooldownSeconds = 5.0;

        private static bool NeedsCooldown(MarketAdminCommandKind kind)
            => kind != MarketAdminCommandKind.Help && kind != MarketAdminCommandKind.UsageError;

        internal static void Dispatch(MarketAdminCommandResult result, IMarketAdminCommandTarget target)
        {
            switch (result.Kind)
            {
                case MarketAdminCommandKind.Character:
                    target.ShowCharacter(result.Name, result.Days, result.Limit);
                    break;

                case MarketAdminCommandKind.Listing:
                    target.ShowListing(result.ListingId);
                    break;

                case MarketAdminCommandKind.Order:
                    target.ShowOrder(result.BuyOrderId);
                    break;

                case MarketAdminCommandKind.Listings:
                    target.ShowListings(result.Name, result.Days, result.Limit);
                    break;

                case MarketAdminCommandKind.Wcid:
                    target.ShowWcid(result.Wcid, result.Days, result.Limit);
                    break;

                case MarketAdminCommandKind.Rejects:
                    target.ShowRejects(result.RejectFilter, result.Name, result.ListingId, result.ReasonCode, result.Days, result.Limit);
                    break;

                case MarketAdminCommandKind.Status:
                    target.ShowStatus();
                    break;

                case MarketAdminCommandKind.Invalidate:
                    target.Invalidate(result.InvalidateAll, result.AccountId, result.Apply);
                    break;

                case MarketAdminCommandKind.Help:
                    target.ShowHelp();
                    break;

                case MarketAdminCommandKind.UsageError:
                default:
                    target.UsageError(result.UsageError ?? MarketAdminCommandParser.UsageMessage);
                    break;
            }
        }
    }

    internal enum MarketAdminCommandKind
    {
        Character,
        Listing,
        Order,
        Listings,
        Wcid,
        Rejects,
        Status,
        Invalidate,
        Help,
        UsageError,
    }

    /// <summary>Which key `/marketadmin rejects` was narrowed by. None means the whole recent table.</summary>
    internal enum MarketRejectFilter
    {
        None,
        Character,
        Listing,
        Reason,
    }

    internal sealed class MarketAdminCommandResult
    {
        public MarketAdminCommandKind Kind;

        /// <summary>A character name, for the arms that resolve one.</summary>
        public string Name;

        public uint ListingId;

        /// <summary>A Wanted buy order id, for the arm that shows one order and its fills.</summary>
        public uint BuyOrderId;

        public uint Wcid;

        /// <summary>How far back to look. 0 means no lower bound.</summary>
        public int Days;

        /// <summary>Rows to render, already clamped by the parser.</summary>
        public int Limit;

        public MarketRejectFilter RejectFilter;

        public string ReasonCode;

        /// <summary>True for `/marketadmin invalidate all`; AccountId is unused when this is set.</summary>
        public bool InvalidateAll;

        /// <summary>The account id for `/marketadmin invalidate &lt;accountId&gt;`. Unused when InvalidateAll is set.</summary>
        public uint AccountId;

        /// <summary>False (the default) means invalidate is a dry run: report only, close nothing.</summary>
        public bool Apply;

        public string UsageError;
    }

    internal static class MarketAdminCommandParser
    {
        public const string HelpText =
            "char|listing|order|listings|wcid|rejects|status|invalidate ...\n" +
            "/marketadmin char <name> [days] [n]     - purchases where that account bought or sold\n" +
            "/marketadmin listing <id>               - one listing, its purchases and its refused attempts\n" +
            "/marketadmin order <id>                 - one Wanted buy order and every fill of it\n" +
            "/marketadmin listings <name> [days] [n] - every listing that account has ever created\n" +
            "/marketadmin wcid <id> [days] [n]       - purchases of one wcid\n" +
            "/marketadmin rejects [char <name> | listing <id> | reason <code>] [days] [n]\n" +
            "/marketadmin status                     - kill switch, index sizes and audit queue health\n" +
            "/marketadmin invalidate <accountId|all> [apply] - close Active listings whose item is gone\n" +
            "  (Admin only). Without apply, reports what would be closed and changes nothing.\n" +
            "days 0 means no time limit. n defaults to 20 and is capped at 50.";

        public const string UsageMessage = "[MARKETADMIN] Type /marketadmin for the list of commands.";

        /// <summary>Rows per page when none is given. A chat window shows about this many usefully.</summary>
        public const int DefaultLimit = 20;

        /// <summary>The chat-window ceiling. The DAO clamps again at 1000; this is the readability cap.</summary>
        public const int MaxLimit = 50;

        /// <summary>How far back the time-windowed arms look when no day count is given.</summary>
        public const int DefaultDays = 30;

        public static MarketAdminCommandResult Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return new MarketAdminCommandResult { Kind = MarketAdminCommandKind.Help };

            var sub = parameters[0].ToLowerInvariant();

            switch (sub)
            {
                case "help":
                case "?":
                    return new MarketAdminCommandResult { Kind = MarketAdminCommandKind.Help };

                case "status":
                    return new MarketAdminCommandResult { Kind = MarketAdminCommandKind.Status };

                case "char":
                case "c":
                {
                    if (parameters.Length < 2)
                        return Usage("Usage: /marketadmin char <name> [days] [n]");

                    if (!TryParseTail(parameters, 2, out var days, out var limit, out var error))
                        return Usage(error);

                    return new MarketAdminCommandResult
                    {
                        Kind = MarketAdminCommandKind.Character,
                        Name = parameters[1],
                        Days = days,
                        Limit = limit,
                    };
                }

                case "listing":
                {
                    if (parameters.Length < 2 || !uint.TryParse(parameters[1], out var listingId) || listingId == 0)
                        return Usage("Usage: /marketadmin listing <id>");

                    return new MarketAdminCommandResult { Kind = MarketAdminCommandKind.Listing, ListingId = listingId };
                }

                case "order":
                case "o":
                {
                    if (parameters.Length < 2 || !uint.TryParse(parameters[1], out var buyOrderId) || buyOrderId == 0)
                        return Usage("Usage: /marketadmin order <id>");

                    return new MarketAdminCommandResult { Kind = MarketAdminCommandKind.Order, BuyOrderId = buyOrderId };
                }

                case "listings":
                {
                    if (parameters.Length < 2)
                        return Usage("Usage: /marketadmin listings <name> [days] [n]");

                    if (!TryParseTail(parameters, 2, out var days, out var limit, out var error))
                        return Usage(error);

                    return new MarketAdminCommandResult
                    {
                        Kind = MarketAdminCommandKind.Listings,
                        Name = parameters[1],
                        Days = days,
                        Limit = limit,
                    };
                }

                case "wcid":
                case "w":
                {
                    if (parameters.Length < 2 || !uint.TryParse(parameters[1], out var wcid) || wcid == 0)
                        return Usage("Usage: /marketadmin wcid <id> [days] [n]");

                    if (!TryParseTail(parameters, 2, out var days, out var limit, out var error))
                        return Usage(error);

                    return new MarketAdminCommandResult
                    {
                        Kind = MarketAdminCommandKind.Wcid,
                        Wcid = wcid,
                        Days = days,
                        Limit = limit,
                    };
                }

                case "rejects":
                case "r":
                    return ParseRejects(parameters);

                case "invalidate":
                    return ParseInvalidate(parameters);

                default:
                    return Usage(UsageMessage);
            }
        }

        private const string InvalidateUsage = "Usage: /marketadmin invalidate <accountId|all> [apply]";

        /// <summary>
        /// `invalidate` takes exactly one target (an account id or the literal "all") and an optional
        /// trailing "apply". Anything else - missing target, a non-numeric non-"all" target, or an
        /// unrecognized trailing word - is a usage error, not a silent default to dry run's shape,
        /// because a typo'd "apply" that parses as "no apply" would look like a successful dry run
        /// while the operator believed they had asked to close listings.
        /// </summary>
        private static MarketAdminCommandResult ParseInvalidate(string[] parameters)
        {
            if (parameters.Length < 2)
                return Usage(InvalidateUsage);

            if (parameters.Length > 3)
                return Usage(InvalidateUsage);

            var all = false;
            uint accountId = 0;

            if (string.Equals(parameters[1], "all", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
            }
            else if (!uint.TryParse(parameters[1], out accountId) || accountId == 0)
            {
                return Usage(InvalidateUsage);
            }

            var apply = false;

            if (parameters.Length == 3)
            {
                if (!string.Equals(parameters[2], "apply", StringComparison.OrdinalIgnoreCase))
                    return Usage(InvalidateUsage);

                apply = true;
            }

            return new MarketAdminCommandResult
            {
                Kind = MarketAdminCommandKind.Invalidate,
                InvalidateAll = all,
                AccountId = accountId,
                Apply = apply,
            };
        }

        /// <summary>
        /// `rejects` takes an OPTIONAL key/value filter before its tail, so the tail starts at a
        /// different index depending on whether one is present. Split out because reading that offset
        /// inline is exactly how an off-by-one gets into a parser.
        /// </summary>
        private static MarketAdminCommandResult ParseRejects(string[] parameters)
        {
            var filter = MarketRejectFilter.None;
            string name = null;
            string reason = null;
            uint listingId = 0;
            var tailStart = 1;

            if (parameters.Length > 1)
            {
                switch (parameters[1].ToLowerInvariant())
                {
                    case "char":
                        if (parameters.Length < 3)
                            return Usage("Usage: /marketadmin rejects char <name> [days] [n]");

                        filter = MarketRejectFilter.Character;
                        name = parameters[2];
                        tailStart = 3;
                        break;

                    case "listing":
                        if (parameters.Length < 3 || !uint.TryParse(parameters[2], out listingId) || listingId == 0)
                            return Usage("Usage: /marketadmin rejects listing <id> [days] [n]");

                        filter = MarketRejectFilter.Listing;
                        tailStart = 3;
                        break;

                    case "reason":
                        if (parameters.Length < 3)
                            return Usage("Usage: /marketadmin rejects reason <code> [days] [n]");

                        filter = MarketRejectFilter.Reason;
                        reason = parameters[2].ToLowerInvariant();
                        tailStart = 3;
                        break;
                }
            }

            if (!TryParseTail(parameters, tailStart, out var days, out var limit, out var error))
                return Usage(error);

            return new MarketAdminCommandResult
            {
                Kind = MarketAdminCommandKind.Rejects,
                RejectFilter = filter,
                Name = name,
                ListingId = listingId,
                ReasonCode = reason,
                Days = days,
                Limit = limit,
            };
        }

        /// <summary>
        /// The shared `[days] [n]` tail. days 0 is legal and means "no lower bound"; n is clamped here
        /// rather than rejected, because a Sentinel asking for 500 lines wants as many as fit, not an
        /// error. Anything unparsable IS an error - silently treating "twenty" as the default would
        /// show a page that does not answer what was asked.
        /// </summary>
        private static bool TryParseTail(string[] parameters, int start, out int days, out int limit, out string error)
        {
            days = DefaultDays;
            limit = DefaultLimit;
            error = null;

            if (parameters.Length > start)
            {
                if (!int.TryParse(parameters[start], out days) || days < 0)
                {
                    error = "days must be 0 or more; 0 means no time limit.";
                    return false;
                }
            }

            if (parameters.Length > start + 1)
            {
                if (!int.TryParse(parameters[start + 1], out limit) || limit < 1)
                {
                    error = $"n must be 1 or more (capped at {MaxLimit}).";
                    return false;
                }
            }

            if (limit > MaxLimit)
                limit = MaxLimit;

            return true;
        }

        private static MarketAdminCommandResult Usage(string message)
            => new MarketAdminCommandResult { Kind = MarketAdminCommandKind.UsageError, UsageError = message };
    }

    /// <summary>
    /// The seam Dispatch is driven through, for the same reason IMarketCommandTarget exists: one
    /// method per kind, so a recording double proves exactly one distinct call per arm and a
    /// transposition fails the test.
    /// </summary>
    internal interface IMarketAdminCommandTarget
    {
        void ShowCharacter(string name, int days, int limit);
        void ShowListing(uint listingId);
        void ShowOrder(uint buyOrderId);
        void ShowListings(string name, int days, int limit);
        void ShowWcid(uint wcid, int days, int limit);
        void ShowRejects(MarketRejectFilter filter, string name, uint listingId, string reasonCode, int days, int limit);
        void ShowStatus();
        void Invalidate(bool all, uint accountId, bool apply);
        void ShowHelp();
        void UsageError(string message);
    }

    /// <summary>
    /// Production target: reads through MarketManager.AdminRepository, which is the same DAO the
    /// market itself writes through, so an investigator is never shown a second, differently-cached
    /// view of the same rows.
    /// </summary>
    internal sealed class PlayerMarketAdminCommandTarget : IMarketAdminCommandTarget
    {
        /// <summary>The one message a failed read renders. Never "no results" - see the class doc on MarketAdminCommands.</summary>
        internal const string UnreadableMessage = "The market log is unreadable right now. Try again in a moment.";

        private readonly Player player;

        public PlayerMarketAdminCommandTarget(Player player)
        {
            this.player = player;
        }

        private void Msg(string message)
            => player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[MARKETADMIN] {message}", ChatMessageType.System));

        private static DateTime? Since(int days) => days <= 0 ? (DateTime?)null : DateTime.UtcNow.AddDays(-days);

        private static string Window(int days) => days <= 0 ? "all time" : $"the last {days} day(s)";

        /// <summary>
        /// Resolves a character name the way the vault commands do, and says which of the two failure
        /// modes happened: an unknown name is not the same fact as a known name whose account cannot
        /// be read, and an investigator acting on the wrong one draws the wrong conclusion.
        /// </summary>
        private bool TryResolve(string name, out uint accountId, out string canonicalName)
        {
            accountId = 0;
            canonicalName = name;

            if (!AccountVaultManager.TryResolveCharacter(name, out _, out var resolved, out var resolvedAccount))
                return false;

            canonicalName = resolved ?? name;
            accountId = resolvedAccount;

            return accountId != 0;
        }

        public void ShowCharacter(string name, int days, int limit)
        {
            var repo = MarketManager.AdminRepository;

            if (repo == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (!TryResolve(name, out var accountId, out var canonical))
            {
                // The character is gone (or was never here), so no account id exists to filter on.
                // The snapshot name columns still carry it, which is the whole reason they are
                // snapshots - fall back to them and SAY SO, because a name match is weaker evidence
                // than an account match and the reader has to know which one they are looking at.
                ShowCharacterByNameSnapshot(repo, name, days, limit);
                return;
            }

            var rows = repo.FindTransactions(new MarketTransactionQuery
            {
                EitherSideAccountId = accountId,
                FromUtc = Since(days),
                Limit = limit,
            });

            if (rows == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (rows.Count == 0)
            {
                Msg($"{canonical} (account {accountId}) has no market transactions in {Window(days)}.");
                return;
            }

            Msg(RenderTransactions($"{canonical} (account {accountId}), {Window(days)}", rows));
        }

        private void ShowCharacterByNameSnapshot(IMarketRepository repo, string name, int days, int limit)
        {
            // No account filter is available, so this reads the recent window and filters in memory.
            // The DAO ceiling bounds it; a very old row under a deleted name can fall outside the page,
            // which the header says plainly rather than pretending the list is complete.
            var rows = repo.FindTransactions(new MarketTransactionQuery
            {
                FromUtc = Since(days),
                Limit = ShardDatabase.MaxMarketTransactionRows,
            });

            if (rows == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            var matches = rows
                .Where(t => string.Equals(t.BuyerCharacterName, name, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(t.SellerCharacterName, name, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToList();

            if (matches.Count == 0)
            {
                Msg($"No character named '{name}' exists now, and no transaction in {Window(days)} carries that snapshot name.");
                return;
            }

            Msg(RenderTransactions($"'{name}' - NO SUCH CHARACTER NOW, matched on snapshot names only, {Window(days)}", matches));
        }

        public void ShowListing(uint listingId)
        {
            var repo = MarketManager.AdminRepository;

            if (repo == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            // From the DATABASE, not MarketManager.GetListing: the in-memory index holds what booted
            // plus what changed since, and a disputed listing is usually a closed one.
            var listing = repo.GetListingRow(listingId);

            if (listing == null)
            {
                Msg($"Listing #{listingId} could not be read. It either does not exist or the shard is unreachable.");
                return;
            }

            var text = new StringBuilder();

            text.Append($"Listing #{listing.Id}: {listing.Count} x wcid {listing.Wcid} at {listing.PriceMmd:N0} MMD each\n");
            text.Append($"  seller {listing.SellerCharacterName} (account {listing.SellerAccountId}, character 0x{listing.SellerCharacterGuid:X8})\n");
            // The class key FIRST: a class listing carries a null guid exactly as a ledger listing does,
            // and reading it as "ledger" would send an investigator to the wrong vault row.
            var itemLabel = listing.ClassKey != null
                ? $"class line {listing.ClassKey}"
                : listing.ItemGuid == null ? "ledger" : $"0x{listing.ItemGuid.Value:X8}";

            text.Append($"  status {(MarketListingStatus)listing.Status}, item {itemLabel}\n");
            text.Append($"  created {listing.CreatedAt:yyyy-MM-dd HH:mm} UTC");
            text.Append(listing.ClosedAt == null ? "\n" : $", closed {listing.ClosedAt.Value:yyyy-MM-dd HH:mm} UTC\n");

            var transactions = repo.FindTransactions(new MarketTransactionQuery
            {
                ListingId = listingId,
                Limit = MarketAdminCommandParser.MaxLimit,
            });

            text.Append(transactions == null
                ? $"  purchases: {UnreadableMessage}\n"
                : RenderTransactionLines("purchases", transactions));

            var rejects = repo.FindRejectedAttempts(new MarketRejectQuery
            {
                ListingId = listingId,
                Limit = MarketAdminCommandParser.MaxLimit,
            });

            text.Append(rejects == null
                ? $"  refused attempts: {UnreadableMessage}"
                : RenderRejectLines("refused attempts", rejects));

            Msg(text.ToString());
        }

        /// <summary>
        /// One Wanted buy order and every fill of it. The fills come from market_transaction filtered
        /// on buy_Order_Id, which is what market_transaction_buy_order_idx is for.
        ///
        /// The order itself comes from MarketManager's index rather than the database, unlike
        /// <see cref="ShowListing"/>: the order index holds EVERY row read at boot, closed ones
        /// included, so a disputed closed order still resolves. What it cannot resolve is an order
        /// closed and written by a PREVIOUS process, and that is said plainly rather than rendered as
        /// "no such order" - the fills below it are read from the database either way, so an id with
        /// fills and no order line is itself the answer to "did this order exist".
        /// </summary>
        public void ShowOrder(uint buyOrderId)
        {
            var repo = MarketManager.AdminRepository;

            if (repo == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            var text = new StringBuilder();
            var order = MarketManager.GetBuyOrder(buyOrderId);

            if (order == null)
            {
                text.Append($"Order #{buyOrderId} is not in this process's order index. It may predate the last restart, or never have existed; its fills below are read from the database regardless.\n");
            }
            else
            {
                text.Append($"Order #{order.Id}: {order.CountRemaining} of {order.CountTotal} x {order.BagName} wanted at {order.PriceMmd:N0} MMD each\n");
                text.Append($"  buyer {order.BuyerCharacterName} (account {order.BuyerAccountId}, character 0x{order.BuyerCharacterGuid:X8})\n");
                text.Append($"  status {order.Status}, material {order.MaterialName} ({order.MaterialType}), escrow {order.EscrowMmd:N0} MMD\n");
                text.Append($"  created {order.CreatedAt:yyyy-MM-dd HH:mm} UTC, expires {order.ExpiresAt:yyyy-MM-dd HH:mm} UTC");
                text.Append(order.ClosedAt == null ? "\n" : $", closed {order.ClosedAt.Value:yyyy-MM-dd HH:mm} UTC\n");
            }

            var fills = repo.FindTransactions(new MarketTransactionQuery
            {
                BuyOrderId = buyOrderId,
                Limit = MarketAdminCommandParser.MaxLimit,
            });

            text.Append(fills == null
                ? $"  fills: {UnreadableMessage}"
                : RenderTransactionLines("fills", fills));

            Msg(text.ToString());
        }

        public void ShowListings(string name, int days, int limit)
        {
            var repo = MarketManager.AdminRepository;

            if (repo == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (!TryResolve(name, out var accountId, out var canonical))
            {
                // market_listing carries seller_Character_Name as a snapshot, but no account to filter
                // on once the character is gone, and unlike transactions there is no useful bounded
                // window to scan. Say so rather than showing an empty page.
                Msg($"No character named '{name}' exists now, so their account cannot be resolved. Use /marketadmin listing <id> if you have a listing number.");
                return;
            }

            var rows = repo.FindListings(new MarketListingQuery
            {
                SellerAccountId = accountId,
                FromUtc = Since(days),
                Limit = limit,
            });

            if (rows == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (rows.Count == 0)
            {
                Msg($"{canonical} (account {accountId}) created no listings in {Window(days)}.");
                return;
            }

            var text = new StringBuilder($"Listings by {canonical} (account {accountId}), {Window(days)}:\n");

            foreach (var listing in rows)
            {
                text.Append($"#{listing.Id}  {listing.CreatedAt:yyyy-MM-dd HH:mm}  {listing.Count} x wcid {listing.Wcid}  {listing.PriceMmd:N0} MMD each  {(MarketListingStatus)listing.Status}\n");
            }

            Msg(text.ToString());
        }

        public void ShowWcid(uint wcid, int days, int limit)
        {
            var repo = MarketManager.AdminRepository;

            if (repo == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            var rows = repo.FindTransactions(new MarketTransactionQuery
            {
                Wcid = wcid,
                FromUtc = Since(days),
                Limit = limit,
            });

            if (rows == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (rows.Count == 0)
            {
                Msg($"No transactions for wcid {wcid} in {Window(days)}.");
                return;
            }

            Msg(RenderTransactions($"wcid {wcid}, {Window(days)}", rows));
        }

        public void ShowRejects(MarketRejectFilter filter, string name, uint listingId, string reasonCode, int days, int limit)
        {
            var repo = MarketManager.AdminRepository;

            if (repo == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            var query = new MarketRejectQuery { FromUtc = Since(days), Limit = limit };
            var header = Window(days);

            switch (filter)
            {
                case MarketRejectFilter.Character:
                    if (!TryResolve(name, out var accountId, out var canonical))
                    {
                        Msg($"No character named '{name}' exists now, so their account cannot be resolved.");
                        return;
                    }

                    query.AccountId = accountId;
                    header = $"{canonical} (account {accountId}), {header}";
                    break;

                case MarketRejectFilter.Listing:
                    query.ListingId = listingId;
                    header = $"listing #{listingId}, {header}";
                    break;

                case MarketRejectFilter.Reason:
                    query.ReasonCode = reasonCode;
                    header = $"reason '{reasonCode}', {header}";
                    break;
            }

            var rows = repo.FindRejectedAttempts(query);

            if (rows == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (rows.Count == 0)
            {
                Msg($"No refused market attempts for {header}.");
                return;
            }

            var text = new StringBuilder($"Refused market attempts for {header}:\n");
            text.Append(RenderRejectLines(null, rows));

            Msg(text.ToString());
        }

        public void ShowStatus()
        {
            var stats = MarketRejectionLog.Stats;
            var counts = MarketManager.IndexCounts();

            var text = new StringBuilder("Market status:\n");

            text.Append($"  market_enabled: {MarketManager.Enabled}\n");
            text.Append($"  index: {counts.Listings} listing(s), {counts.ActiveListings} active, {counts.Transactions} history row(s), change sequence {MarketManager.ChangeSequence}\n");
            text.Append($"  reject log: recording={stats.RecordingEnabled}, writer={(stats.WriterRunning ? "running" : "stopped")}, retention={stats.RetentionDays}d\n");
            text.Append($"  reject queue: depth={stats.QueueDepth}, dropped={stats.DroppedSinceBoot}, last flush {stats.LastFlushUtc:yyyy-MM-dd HH:mm:ss} UTC");

            Msg(text.ToString());
        }

        /// <summary>
        /// Closes (apply) or reports (dry run, the default) every Active listing on the target
        /// account(s) whose backing is gone. "all" means every distinct seller account that currently
        /// has an Active listing in the in-memory index. Both branches use
        /// MarketManager.FindInvalidationCandidates, so a dry run and the apply that follows it are
        /// looking at exactly the same decision, not a re-derived one that could disagree with it.
        /// </summary>
        public void Invalidate(bool all, uint accountId, bool apply)
        {
            if (!MarketManager.Enabled)
            {
                Msg("Market is disabled; nothing to check.");
                return;
            }

            var accountIds = all ? MarketManager.ActiveListingSellerAccountIds() : new List<uint> { accountId };

            if (accountIds.Count == 0)
            {
                Msg("No account currently has an active listing.");
                return;
            }

            var skipped = new List<uint>();
            var affected = new List<(uint AccountId, MarketListing Listing)>();

            foreach (var acct in accountIds)
            {
                var found = MarketManager.FindInvalidationCandidates(acct);

                if (found.StoreNotReady)
                {
                    skipped.Add(acct);
                    continue;
                }

                foreach (var listing in found.Listings)
                    affected.Add((acct, listing));
            }

            var text = new StringBuilder();

            foreach (var (acct, listing) in affected)
            {
                var itemName = listing.Snapshot?.Name ?? $"wcid {listing.Wcid}";

                text.Append($"  listing #{listing.Id}  seller {listing.SellerCharacterName} (account {acct})  {listing.Count} x {itemName}  {listing.PriceMmd:N0} MMD each\n");
            }

            if (apply)
            {
                foreach (var acct in accountIds)
                {
                    var thisAccountsListings = affected.Where(a => a.AccountId == acct).Select(a => a.Listing).ToList();

                    if (thisAccountsListings.Count > 0)
                        MarketManager.ApplyInvalidation(acct, thisAccountsListings);
                }
            }

            text.Append($"Accounts checked: {accountIds.Count}. ");

            text.Append(skipped.Count == 0
                ? "Accounts skipped as not ready: none. "
                : $"Accounts skipped as not ready (cold vaults report not ready on first touch; re-run this to catch them): {string.Join(", ", skipped)}. ");

            text.Append(apply
                ? $"Listings invalidated: {affected.Count}."
                : $"Listings that would be invalidated: {affected.Count}.");

            Msg(text.ToString());
        }

        public void ShowHelp() => Msg(MarketAdminCommandParser.HelpText);

        public void UsageError(string message) => Msg(message);

        // ---- rendering ----

        private static string RenderTransactions(string header, IReadOnlyList<ShardMarketTransaction> rows)
        {
            var text = new StringBuilder($"Market transactions for {header}:\n");
            text.Append(RenderTransactionLines(null, rows));

            return text.ToString();
        }

        private static string RenderTransactionLines(string label, IReadOnlyList<ShardMarketTransaction> rows)
        {
            var text = new StringBuilder();

            if (label != null)
                text.Append($"  {label}: {rows.Count}\n");

            foreach (var tx in rows)
            {
                text.Append($"  #{tx.Id} {tx.Timestamp:yyyy-MM-dd HH:mm} listing #{tx.ListingId} {tx.Count} x {tx.ItemName} for {tx.PriceMmdTotal:N0} MMD  {tx.SellerCharacterName} -> {tx.BuyerCharacterName}  ({(MarketChannel)tx.Channel}, {(MarketTransactionStatus)tx.Status})\n");
            }

            return text.ToString();
        }

        private static string RenderRejectLines(string label, IReadOnlyList<MarketRejectedAttempt> rows)
        {
            var text = new StringBuilder();

            if (label != null)
                text.Append($"  {label}: {rows.Count}\n");

            foreach (var row in rows)
            {
                var who = string.IsNullOrEmpty(row.CharacterName) ? $"account {row.AccountId}" : row.CharacterName;

                text.Append($"  #{row.Id} {row.Timestamp:yyyy-MM-dd HH:mm} {(MarketRejectOperation)row.Operation} {row.ReasonCode}  {who}");
                text.Append(row.ListingId == null ? string.Empty : $"  listing #{row.ListingId.Value}");
                text.Append(row.Wcid == 0 ? string.Empty : $"  wcid {row.Wcid}");
                text.Append($"  ({(MarketChannel)row.Channel})");
                text.Append(string.IsNullOrEmpty(row.Detail) ? "\n" : $"  {row.Detail}\n");
            }

            return text.ToString();
        }
    }
}
