using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /market parsing and dispatch (DESIGN section 8). Both halves matter: MuleCommands' remarks record
    /// a transposed dispatch switch passing a green suite while swapping grant/revoke; /market buy moves items and money.
    /// </summary>
    [TestClass]
    public class MarketCommandParseTests
    {
        private static MarketCommandResult Parse(params string[] args) => MarketCommandParser.Parse(args);

        [TestMethod]
        public void BareMarket_ShowsHelp()
        {
            Assert.AreEqual(MarketCommandKind.Help, Parse().Kind);
            Assert.AreEqual(MarketCommandKind.Help, Parse("help").Kind);
            Assert.AreEqual(MarketCommandKind.Help, Parse("?").Kind);
        }

        [TestMethod]
        public void Vault_Mine_History_Parse()
        {
            Assert.AreEqual(MarketCommandKind.Vault, Parse("vault").Kind);
            Assert.AreEqual(MarketCommandKind.Mine, Parse("mine").Kind);

            var history = Parse("history");
            Assert.AreEqual(MarketCommandKind.History, history.Kind);
            Assert.AreEqual(10, history.Limit, "a bare history shows the last 10");

            Assert.AreEqual(25, Parse("history", "25").Limit);
        }

        [TestMethod]
        public void List_TakesAnEntryNumberAPriceAndAnOptionalCount()
        {
            var withoutCount = Parse("list", "3", "50");

            Assert.AreEqual(MarketCommandKind.List, withoutCount.Kind);
            Assert.AreEqual(3, withoutCount.EntryNumber);
            Assert.AreEqual(50, withoutCount.Price);
            Assert.AreEqual(0, withoutCount.Count, "0 means the whole entry");

            var withCount = Parse("list", "3", "50", "20");
            Assert.AreEqual(20, withCount.Count);
        }

        [TestMethod]
        public void List_RejectsNonNumericAndNonPositiveArguments()
        {
            foreach (var args in new[]
            {
                new[] { "list" },
                new[] { "list", "3" },
                new[] { "list", "abc", "50" },
                new[] { "list", "3", "abc" },
                new[] { "list", "0", "50" },
                new[] { "list", "3", "-1" },
                new[] { "list", "3", "50", "-1" },
            })
            {
                Assert.AreEqual(MarketCommandKind.UsageError, Parse(args).Kind, $"'{string.Join(" ", args)}' must be a usage error");
            }
        }

        [TestMethod]
        public void List_AcceptsAPriceOfZeroAsAGiveaway()
        {
            var result = Parse("list", "3", "0");

            Assert.AreEqual(MarketCommandKind.List, result.Kind, "zero is a giveaway price, not a usage error");
            Assert.AreEqual(3, result.EntryNumber);
            Assert.AreEqual(0, result.Price);
        }

        [TestMethod]
        public void Delist_And_Buy_TakeAListingNumber()
        {
            var delist = Parse("delist", "42");
            Assert.AreEqual(MarketCommandKind.Delist, delist.Kind);
            Assert.AreEqual(42u, delist.ListingId);

            var buy = Parse("buy", "42");
            Assert.AreEqual(MarketCommandKind.Buy, buy.Kind);
            Assert.AreEqual(42u, buy.ListingId);
            Assert.AreEqual(1, buy.Count, "a bare buy takes one");

            Assert.AreEqual(7, Parse("buy", "42", "7").Count);

            Assert.AreEqual(MarketCommandKind.UsageError, Parse("buy").Kind);
            Assert.AreEqual(MarketCommandKind.UsageError, Parse("buy", "abc").Kind);
            Assert.AreEqual(MarketCommandKind.UsageError, Parse("delist").Kind);
        }

        [TestMethod]
        public void Search_JoinsEveryRemainingWord()
        {
            var result = Parse("search", "olthoi", "armor", "greaves");

            Assert.AreEqual(MarketCommandKind.Search, result.Kind);
            Assert.AreEqual("olthoi armor greaves", result.Text,
                "the client splits chat on spaces, so a multi-word search must be rejoined or only the first word searches");

            Assert.AreEqual(MarketCommandKind.UsageError, Parse("search").Kind);
        }

        [TestMethod]
        public void UnknownSubcommand_IsAUsageErrorAndNotSilentlyHelp()
        {
            Assert.AreEqual(MarketCommandKind.UsageError, Parse("sell", "3", "50").Kind);
        }

        [TestMethod]
        public void Dispatch_CallsExactlyOneDistinctMethodPerKind()
        {
            var target = new RecordingMarketTarget();

            MarketCommandParser.Parse(new[] { "vault" }).Also(r => MarketCommands.Dispatch(r, target));
            MarketCommands.Dispatch(Parse("list", "1", "5"), target);
            MarketCommands.Dispatch(Parse("delist", "9"), target);
            MarketCommands.Dispatch(Parse("mine"), target);
            MarketCommands.Dispatch(Parse("history", "5"), target);
            MarketCommands.Dispatch(Parse("search", "sword"), target);
            MarketCommands.Dispatch(Parse("buy", "9", "2"), target);
            MarketCommands.Dispatch(Parse("help"), target);
            MarketCommands.Dispatch(Parse("nonsense"), target);

            CollectionAssert.AreEqual(
                new List<string> { "vault", "list:1:5:0", "delist:9", "mine", "history:5", "search:sword", "buy:9:2", "help", "usage" },
                target.Calls,
                "every arm must reach its own method - a transposed pair here swaps two player-visible commands silently");
        }

        private sealed class RecordingMarketTarget : IMarketCommandTarget
        {
            public readonly List<string> Calls = new List<string>();

            public void ShowVault() => Calls.Add("vault");
            public void ListEntry(int entryNumber, long price, int count) => Calls.Add($"list:{entryNumber}:{price}:{count}");
            public void Delist(uint listingId) => Calls.Add($"delist:{listingId}");
            public void ShowMine() => Calls.Add("mine");
            public void ShowHistory(int limit) => Calls.Add($"history:{limit}");
            public void Search(string text) => Calls.Add($"search:{text}");
            public void Buy(uint listingId, int count) => Calls.Add($"buy:{listingId}:{count}");
            public void ShowHelp() => Calls.Add("help");
            public void UsageError(string message) => Calls.Add("usage");
        }
    }

    internal static class MarketTestExtensions
    {
        public static void Also<T>(this T value, System.Action<T> action) => action(value);
    }
}
