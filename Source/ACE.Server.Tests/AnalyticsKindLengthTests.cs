using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins every literal `kind` value AnalyticsManager writes to item_flow_event / currency_flow_event
    /// against those columns' widths (item_flow_event.kind VARCHAR(8), currency_flow_event.kind
    /// VARCHAR(16) - see ace_analytics.sql). A pure source-text check, not a DB or Player-backed test,
    /// because AnalyticsDatabase needs a live MySQL connection and Record* needs a real Player - neither
    /// is available in ACE.Server.Tests.
    ///
    /// Real case (prod, 2026-09-15/16): RecordMarketSale wrote Kind = "market_sale" (11 chars) into
    /// item_flow_event, whose kind column is only 8 wide. MySqlException on every market sale took the
    /// WHOLE Tier-2 batch (items + currencies dequeued that tick, see AnalyticsManager.DrainTier2) down
    /// with it, not just the offending row - DrainTier2 dequeues before calling WriteTier2 and never
    /// re-enqueues on failure. Fixed by shortening the value to "mkt_sale" (8 chars). This test fails
    /// against that unfixed code and must keep failing if a future kind value overflows its column.
    /// </summary>
    [TestClass]
    public class AnalyticsKindLengthTests
    {
        private const int ItemFlowKindMaxLength = 8;
        private const int CurrencyFlowKindMaxLength = 16;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Managers", "Analytics", "AnalyticsManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Managers/Analytics/AnalyticsManager.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string AnalyticsManagerSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "AnalyticsManager.cs"));

        private static string AnalyticsSchemaSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "ace_analytics.sql"));

        /// <summary>Literal kind values passed to EnqueueItem/EnqueueVendorItem (RecordTradeItem, RecordGive, RecordVendorBuy, RecordVendorSell).</summary>
        private static string[] EnqueueItemCallSiteKinds(string source)
            => Regex.Matches(source, @"EnqueueItem\(\s*""([^""]+)""")
                .Cast<Match>()
                .Concat(Regex.Matches(source, @"EnqueueVendorItem\(\s*""([^""]+)""").Cast<Match>())
                .Select(m => m.Groups[1].Value)
                .ToArray();

        /// <summary>
        /// Every string literal appearing in a `Kind = &lt;expr&gt;` assignment, up to the assignment's
        /// terminating comma or newline. Deliberately not just "the first quoted string right after
        /// Kind =": a real call site (RecordVendorPayment, AnalyticsManager.cs:265) assigns a TERNARY,
        /// `Kind = playerPays ? "buy" : "sell",`, which has two literals and neither sits right after
        /// the `=`. Missing that call site let CurrencyFlowKinds_MatchThePinnedSet pass with an
        /// incomplete pinned set (code review finding on commit 9f4dc516e).
        /// </summary>
        private static IEnumerable<string> KindLiterals(string initializerBody)
        {
            var expr = Regex.Match(initializerBody, @"Kind\s*=\s*(?<expr>[^,\n]+)");

            if (!expr.Success)
                yield break;

            foreach (Match literal in Regex.Matches(expr.Groups["expr"].Value, @"""([^""]+)"""))
                yield return literal.Groups[1].Value;
        }

        /// <summary>Every `Kind = ...` string literal inside every `ItemFlowRow { ... }` initializer.</summary>
        private static string[] ItemFlowRowInlineKinds(string source)
            => Regex.Matches(source, @"AnalyticsDatabase\.ItemFlowRow\s*\{(?<body>(?:(?!\}).)*?)\}", RegexOptions.Singleline)
                .Cast<Match>()
                .SelectMany(m => KindLiterals(m.Groups["body"].Value))
                .ToArray();

        /// <summary>Every `Kind = ...` string literal inside every `CurrencyFlowRow { ... }` initializer.</summary>
        private static string[] CurrencyFlowRowInlineKinds(string source)
            => Regex.Matches(source, @"AnalyticsDatabase\.CurrencyFlowRow\s*\{(?<body>(?:(?!\}).)*?)\}", RegexOptions.Singleline)
                .Cast<Match>()
                .SelectMany(m => KindLiterals(m.Groups["body"].Value))
                .ToArray();

        [TestMethod]
        public void EveryItemFlowKind_FitsTheVarchar8Column()
        {
            var source = AnalyticsManagerSource();
            var kinds = EnqueueItemCallSiteKinds(source).Concat(ItemFlowRowInlineKinds(source)).Distinct().ToArray();

            Assert.IsTrue(kinds.Length > 0, "expected at least one item_flow_event kind literal in AnalyticsManager.cs - did the source move?");

            foreach (var kind in kinds)
                Assert.IsTrue(kind.Length <= ItemFlowKindMaxLength,
                    $"item_flow_event kind '{kind}' is {kind.Length} chars, which overflows VARCHAR({ItemFlowKindMaxLength})");
        }

        [TestMethod]
        public void EveryCurrencyFlowKind_FitsTheVarchar16Column()
        {
            var kinds = CurrencyFlowRowInlineKinds(AnalyticsManagerSource()).Distinct().ToArray();

            Assert.IsTrue(kinds.Length > 0, "expected at least one currency_flow_event kind literal in AnalyticsManager.cs - did the source move?");

            foreach (var kind in kinds)
                Assert.IsTrue(kind.Length <= CurrencyFlowKindMaxLength,
                    $"currency_flow_event kind '{kind}' is {kind.Length} chars, which overflows VARCHAR({CurrencyFlowKindMaxLength})");
        }

        [TestMethod]
        public void ItemFlowKinds_MatchThePinnedSet()
        {
            // Pins the exact set so a future addition is a deliberate, reviewed change to this test
            // (and to ace_analytics.sql's documented list) rather than a silent new literal nobody checked.
            var kinds = EnqueueItemCallSiteKinds(AnalyticsManagerSource())
                .Concat(ItemFlowRowInlineKinds(AnalyticsManagerSource()))
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "buy", "give", "mkt_sale", "sell", "trade" }, kinds);
        }

        [TestMethod]
        public void CurrencyFlowKinds_MatchThePinnedSet()
        {
            var kinds = CurrencyFlowRowInlineKinds(AnalyticsManagerSource())
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "bank_transfer", "buy", "mkt_sale", "sell" }, kinds);
        }

        [TestMethod]
        public void Schema_DocumentsTheSameColumnWidths()
        {
            var sql = AnalyticsSchemaSource();

            StringAssert.Contains(sql, "`kind`         VARCHAR(8)      NOT NULL,   -- 'trade' | 'give' | 'buy' | 'sell' | 'mkt_sale'");
            StringAssert.Contains(sql, "`kind`      VARCHAR(16)     NOT NULL,   -- 'bank_transfer' | 'buy' | 'sell' | 'mkt_sale'");
        }
    }
}
