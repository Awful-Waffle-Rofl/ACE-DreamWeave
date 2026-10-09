using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /marketadmin parsing and dispatch, with no Session and no shard. The dispatch half exists for
    /// the reason MarketCommands' does: a transposed switch arm compiles, ships, and is only ever
    /// noticed by whoever was investigating a dispute at the time.
    /// </summary>
    [TestClass]
    public class MarketAdminCommandParseTests
    {
        private static MarketAdminCommandResult Parse(params string[] parameters)
            => MarketAdminCommandParser.Parse(parameters);

        [TestMethod]
        public void NoArguments_IsHelp()
        {
            Assert.AreEqual(MarketAdminCommandKind.Help, Parse().Kind);
            Assert.AreEqual(MarketAdminCommandKind.Help, MarketAdminCommandParser.Parse(null).Kind);
            Assert.AreEqual(MarketAdminCommandKind.Help, Parse("help").Kind);
            Assert.AreEqual(MarketAdminCommandKind.Help, Parse("?").Kind);
        }

        [TestMethod]
        public void UnknownSubcommand_IsAUsageError()
        {
            var result = Parse("refund");

            Assert.AreEqual(MarketAdminCommandKind.UsageError, result.Kind);
            Assert.AreEqual(MarketAdminCommandParser.UsageMessage, result.UsageError);
        }

        [TestMethod]
        public void Status_TakesNoArguments()
        {
            Assert.AreEqual(MarketAdminCommandKind.Status, Parse("status").Kind);
        }

        [TestMethod]
        public void Char_ParsesTheNameAndTheDefaults()
        {
            var result = Parse("char", "Someplayer");

            Assert.AreEqual(MarketAdminCommandKind.Character, result.Kind);
            Assert.AreEqual("Someplayer", result.Name);
            Assert.AreEqual(MarketAdminCommandParser.DefaultDays, result.Days);
            Assert.AreEqual(MarketAdminCommandParser.DefaultLimit, result.Limit);
        }

        [TestMethod]
        public void Char_ParsesAnExplicitWindowAndPageSize()
        {
            var result = Parse("char", "Someplayer", "7", "5");

            Assert.AreEqual(7, result.Days);
            Assert.AreEqual(5, result.Limit);
        }

        [TestMethod]
        public void Char_WithNoName_IsAUsageError()
        {
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("char").Kind);
        }

        [TestMethod]
        public void Listing_RequiresANonZeroId()
        {
            var result = Parse("listing", "42");

            Assert.AreEqual(MarketAdminCommandKind.Listing, result.Kind);
            Assert.AreEqual(42u, result.ListingId);

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("listing").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("listing", "0").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("listing", "abc").Kind);
        }

        [TestMethod]
        public void Listings_ParsesTheNameAndTail()
        {
            var result = Parse("listings", "Someplayer", "0", "9");

            Assert.AreEqual(MarketAdminCommandKind.Listings, result.Kind);
            Assert.AreEqual("Someplayer", result.Name);
            Assert.AreEqual(0, result.Days, "days 0 is legal and means no time limit");
            Assert.AreEqual(9, result.Limit);

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("listings").Kind);
        }

        [TestMethod]
        public void Wcid_RequiresANonZeroId()
        {
            var result = Parse("wcid", "9001", "14");

            Assert.AreEqual(MarketAdminCommandKind.Wcid, result.Kind);
            Assert.AreEqual(9001u, result.Wcid);
            Assert.AreEqual(14, result.Days);

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("wcid").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("wcid", "0").Kind);
        }

        [TestMethod]
        public void Rejects_WithNoFilter_ReadsTheWholeRecentWindow()
        {
            var result = Parse("rejects");

            Assert.AreEqual(MarketAdminCommandKind.Rejects, result.Kind);
            Assert.AreEqual(MarketRejectFilter.None, result.RejectFilter);
            Assert.AreEqual(MarketAdminCommandParser.DefaultDays, result.Days);
            Assert.AreEqual(MarketAdminCommandParser.DefaultLimit, result.Limit);
        }

        [TestMethod]
        public void Rejects_WithABareTail_ReadsTheWholeWindowAtThatDepth()
        {
            // The optional filter shifts where the tail starts; this is the case that would break
            // first if that offset were computed inline.
            var result = Parse("rejects", "3", "8");

            Assert.AreEqual(MarketRejectFilter.None, result.RejectFilter);
            Assert.AreEqual(3, result.Days);
            Assert.AreEqual(8, result.Limit);
        }

        [TestMethod]
        public void Rejects_ByCharacter_ParsesTheNameThenTheTail()
        {
            var result = Parse("rejects", "char", "Someplayer", "3", "8");

            Assert.AreEqual(MarketRejectFilter.Character, result.RejectFilter);
            Assert.AreEqual("Someplayer", result.Name);
            Assert.AreEqual(3, result.Days);
            Assert.AreEqual(8, result.Limit);

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("rejects", "char").Kind);
        }

        [TestMethod]
        public void Rejects_ByListing_ParsesTheIdThenTheTail()
        {
            var result = Parse("rejects", "listing", "77", "2");

            Assert.AreEqual(MarketRejectFilter.Listing, result.RejectFilter);
            Assert.AreEqual(77u, result.ListingId);
            Assert.AreEqual(2, result.Days);

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("rejects", "listing").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("rejects", "listing", "0").Kind);
        }

        [TestMethod]
        public void Rejects_ByReason_LowercasesTheCode()
        {
            var result = Parse("rejects", "reason", "Price_Changed");

            Assert.AreEqual(MarketRejectFilter.Reason, result.RejectFilter);
            Assert.AreEqual("price_changed", result.ReasonCode, "the stored codes are lower case");

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("rejects", "reason").Kind);
        }

        [TestMethod]
        public void Tail_ClampsThePageSizeButRejectsGarbage()
        {
            Assert.AreEqual(MarketAdminCommandParser.MaxLimit, Parse("char", "Someplayer", "1", "500").Limit,
                "a page size over the cap is clamped, not refused");

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("char", "Someplayer", "-1").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("char", "Someplayer", "soon").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("char", "Someplayer", "1", "0").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("char", "Someplayer", "1", "lots").Kind);
        }

        /// <summary>
        /// The arm that answers "show me every fill of order N". Zero is refused for the reason
        /// `listing 0` is: order ids are 1-based, so 0 is always a typo rather than a wildcard.
        /// </summary>
        [TestMethod]
        public void Order_ParsesAnId_AndRefusesZeroOrNonsense()
        {
            var result = Parse("order", "42");

            Assert.AreEqual(MarketAdminCommandKind.Order, result.Kind);
            Assert.AreEqual(42u, result.BuyOrderId);
            Assert.AreEqual(0u, result.ListingId, "an order id must never be read as a listing id");

            Assert.AreEqual(MarketAdminCommandKind.Order, Parse("o", "42").Kind);

            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("order").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("order", "0").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("order", "forty-two").Kind);
        }

        [TestMethod]
        public void Invalidate_ParsesAnAccountId_DryRunByDefault()
        {
            var result = Parse("invalidate", "86");

            Assert.AreEqual(MarketAdminCommandKind.Invalidate, result.Kind);
            Assert.IsFalse(result.InvalidateAll);
            Assert.AreEqual(86u, result.AccountId);
            Assert.IsFalse(result.Apply, "no trailing 'apply' means dry run");
        }

        [TestMethod]
        public void Invalidate_ParsesAll_DryRunByDefault()
        {
            var result = Parse("invalidate", "all");

            Assert.AreEqual(MarketAdminCommandKind.Invalidate, result.Kind);
            Assert.IsTrue(result.InvalidateAll);
            Assert.IsFalse(result.Apply);
        }

        [TestMethod]
        public void Invalidate_ParsesAnAccountId_WithApply()
        {
            var result = Parse("invalidate", "86", "apply");

            Assert.AreEqual(MarketAdminCommandKind.Invalidate, result.Kind);
            Assert.IsFalse(result.InvalidateAll);
            Assert.AreEqual(86u, result.AccountId);
            Assert.IsTrue(result.Apply);
        }

        [TestMethod]
        public void Invalidate_ParsesAll_WithApply()
        {
            var result = Parse("invalidate", "all", "apply");

            Assert.AreEqual(MarketAdminCommandKind.Invalidate, result.Kind);
            Assert.IsTrue(result.InvalidateAll);
            Assert.IsTrue(result.Apply);
        }

        [TestMethod]
        public void Invalidate_IsCaseInsensitiveOnAllAndApply()
        {
            var result = Parse("invalidate", "ALL", "APPLY");

            Assert.AreEqual(MarketAdminCommandKind.Invalidate, result.Kind);
            Assert.IsTrue(result.InvalidateAll);
            Assert.IsTrue(result.Apply);
        }

        [TestMethod]
        public void Invalidate_WithNoTarget_IsAUsageError()
        {
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("invalidate").Kind);
        }

        [TestMethod]
        public void Invalidate_WithANonNumericNonAllTarget_IsAUsageError()
        {
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("invalidate", "someplayer").Kind);
        }

        [TestMethod]
        public void Invalidate_WithAZeroAccountId_IsAUsageError()
        {
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("invalidate", "0").Kind);
        }

        [TestMethod]
        public void Invalidate_WithAnUnknownTrailingWord_IsAUsageError()
        {
            // Must not silently fall back to "no apply": a typo'd apply must never look like a
            // successful dry run.
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("invalidate", "86", "applyy").Kind);
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("invalidate", "all", "confirm").Kind);
        }

        [TestMethod]
        public void Invalidate_WithExtraTrailingWords_IsAUsageError()
        {
            Assert.AreEqual(MarketAdminCommandKind.UsageError, Parse("invalidate", "86", "apply", "now").Kind);
        }

        [TestMethod]
        public void Subcommands_AreCaseInsensitive()
        {
            Assert.AreEqual(MarketAdminCommandKind.Status, Parse("STATUS").Kind);
            Assert.AreEqual(MarketAdminCommandKind.Character, Parse("Char", "Someplayer").Kind);
            Assert.AreEqual(MarketAdminCommandKind.Rejects, Parse("rejects", "CHAR", "Someplayer").Kind);
        }

        // ---- dispatch ----

        private sealed class RecordingAdminTarget : IMarketAdminCommandTarget
        {
            public readonly List<string> Calls = new List<string>();

            public void ShowCharacter(string name, int days, int limit) => Calls.Add($"char:{name}:{days}:{limit}");
            public void ShowListing(uint listingId) => Calls.Add($"listing:{listingId}");
            public void ShowOrder(uint buyOrderId) => Calls.Add($"order:{buyOrderId}");
            public void ShowListings(string name, int days, int limit) => Calls.Add($"listings:{name}:{days}:{limit}");
            public void ShowWcid(uint wcid, int days, int limit) => Calls.Add($"wcid:{wcid}:{days}:{limit}");

            public void ShowRejects(MarketRejectFilter filter, string name, uint listingId, string reasonCode, int days, int limit)
                => Calls.Add($"rejects:{filter}:{name}:{listingId}:{reasonCode}:{days}:{limit}");

            public void ShowStatus() => Calls.Add("status");
            public void Invalidate(bool all, uint accountId, bool apply) => Calls.Add($"invalidate:{all}:{accountId}:{apply}");
            public void ShowHelp() => Calls.Add("help");
            public void UsageError(string message) => Calls.Add($"usage:{message}");
        }

        private static string DispatchOne(params string[] parameters)
        {
            var target = new RecordingAdminTarget();

            MarketAdminCommands.Dispatch(MarketAdminCommandParser.Parse(parameters), target);

            Assert.AreEqual(1, target.Calls.Count, "exactly one target call per invocation");

            return target.Calls[0];
        }

        [TestMethod]
        public void Dispatch_RoutesEveryArmToItsOwnMethod()
        {
            Assert.AreEqual("char:Someplayer:7:5", DispatchOne("char", "Someplayer", "7", "5"));
            Assert.AreEqual("listing:42", DispatchOne("listing", "42"));
            Assert.AreEqual("order:42", DispatchOne("order", "42"));
            Assert.AreEqual("listings:Someplayer:7:5", DispatchOne("listings", "Someplayer", "7", "5"));
            Assert.AreEqual("wcid:9001:7:5", DispatchOne("wcid", "9001", "7", "5"));
            Assert.AreEqual("rejects:Listing::77::7:5", DispatchOne("rejects", "listing", "77", "7", "5"));
            Assert.AreEqual("rejects:Reason::0:price_changed:7:5", DispatchOne("rejects", "reason", "price_changed", "7", "5"));
            Assert.AreEqual("status", DispatchOne("status"));
            Assert.AreEqual("invalidate:False:86:True", DispatchOne("invalidate", "86", "apply"));
            Assert.AreEqual("invalidate:True:0:False", DispatchOne("invalidate", "all"));
            Assert.AreEqual("help", DispatchOne("help"));
        }

        [TestMethod]
        public void Dispatch_SendsAUsageErrorToUsageErrorAndNowhereElse()
        {
            StringAssert.StartsWith(DispatchOne("refund"), "usage:");
            StringAssert.StartsWith(DispatchOne("listing", "0"), "usage:");
        }

        /// <summary>
        /// Every kind must have an arm. A new kind that falls through to the default would silently
        /// render a usage error instead of the page it was added for.
        /// </summary>
        [TestMethod]
        public void Dispatch_HasAnArmForEveryKind()
        {
            foreach (MarketAdminCommandKind kind in Enum.GetValues(typeof(MarketAdminCommandKind)))
            {
                var target = new RecordingAdminTarget();

                MarketAdminCommands.Dispatch(new MarketAdminCommandResult { Kind = kind, Name = "Someplayer" }, target);

                Assert.AreEqual(1, target.Calls.Count, $"{kind} did not reach exactly one target method");

                if (kind != MarketAdminCommandKind.UsageError)
                    Assert.IsFalse(target.Calls[0].StartsWith("usage:"), $"{kind} fell through to the default arm");
            }
        }
    }
}
