using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MarketAdvertiser's composition and wall-clock scheduling, plus MarketAdvertiserJob's slot
    /// bookkeeping. The Send seam replaces the real Trade broadcast so none of this needs a live
    /// Session or Player - the test harness has no live Player available.
    /// </summary>
    [TestClass]
    public class MarketAdvertiserTests
    {
        private Action<IReadOnlyList<string>> realSend;
        private Func<IReadOnlyList<string>> realBuildLines;
        private List<IReadOnlyList<string>> sent;

        [TestInitialize]
        public void Setup()
        {
            realSend = MarketAdvertiser.Send;
            realBuildLines = MarketAdvertiser.BuildLines;
            sent = new List<IReadOnlyList<string>>();
            MarketAdvertiser.Send = lines => sent.Add(lines);
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketAdvertiser.Send = realSend;
            MarketAdvertiser.BuildLines = realBuildLines;
            MarketManager.Shutdown();
        }

        // ---- NextRunUtc ----

        [TestMethod]
        public void NextRunUtc_ZeroInterval_IsDisabled()
        {
            Assert.IsNull(MarketAdvertiser.NextRunUtc(new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc), 0));
        }

        [TestMethod]
        public void NextRunUtc_NegativeInterval_IsDisabled()
        {
            Assert.IsNull(MarketAdvertiser.NextRunUtc(new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc), -1));
        }

        [TestMethod]
        public void NextRunUtc_IntervalSix_ReturnsTheNextSixHourBoundary()
        {
            var now = new DateTime(2026, 9, 8, 7, 15, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc), MarketAdvertiser.NextRunUtc(now, 6));
        }

        [TestMethod]
        public void NextRunUtc_ExactlyOnABoundary_ReturnsTheNextOneNotTheCurrentInstant()
        {
            var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 9, 8, 18, 0, 0, DateTimeKind.Utc), MarketAdvertiser.NextRunUtc(now, 6));
        }

        [TestMethod]
        public void NextRunUtc_LastSlotOfTheDay_RollsOverToNextMidnight()
        {
            var now = new DateTime(2026, 9, 8, 19, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), MarketAdvertiser.NextRunUtc(now, 6));
        }

        [TestMethod]
        public void NextRunUtc_IntervalFive_RestartsFromMidnightRatherThanEndingAt24()
        {
            // 5 does not divide 24 evenly - slots are 00,05,10,15,20, then a short 4-hour gap to the
            // next midnight rather than a slot at 25 (== the next day's 01:00).
            var now = new DateTime(2026, 9, 8, 21, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), MarketAdvertiser.NextRunUtc(now, 5));

            var earlier = new DateTime(2026, 9, 8, 16, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 9, 8, 20, 0, 0, DateTimeKind.Utc), MarketAdvertiser.NextRunUtc(earlier, 5));
        }

        [TestMethod]
        public void NextRunUtc_IntervalAboveTwentyFour_ClampsToOneDailyPostAtMidnight()
        {
            var now = new DateTime(2026, 9, 8, 5, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), MarketAdvertiser.NextRunUtc(now, 48));
        }

        // ---- BuildLines ----

        [TestMethod]
        public void BuildLines_WithNoActiveListing_OmitsTheListingLineButKeepsTheStandingTwo()
        {
            SeedTunables();
            MarketManager.Initialize(new FakeMarketItemStore(), new FakeMarketWallet(), new FakeMarketRepository());

            var lines = MarketAdvertiser.BuildLines();

            Assert.AreEqual(2, lines.Count);
            Assert.IsTrue(lines[0].StartsWith("[Market] The Market is open at"), lines[0]);
            Assert.IsTrue(lines[1].Contains("/bank") && lines[1].Contains("/mule"), lines[1]);

            // Pinned wording, not just the two command names: /mule is Marketplace-only by default
            // (account_vault_allowlist ships as "01F5@1" - PropertyManager.cs), so the earlier "from
            // anywhere" told players the vault was reachable where it is not.
            StringAssert.Contains(lines[1], "reach your account vault from MP.");
            Assert.IsFalse(lines[1].Contains("anywhere"), lines[1]);
        }

        [TestMethod]
        public void BuildLines_WithASingleUnitListing_DropsTheCountPrefix()
        {
            SeedTunables();
            var repo = new FakeMarketRepository();
            var store = new FakeMarketItemStore();
            MarketManager.Initialize(store, new FakeMarketWallet(), repo);

            var seller = new MarketActor(101, 0x50000501, "Marketseller");
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(101, item);
            var listing = MarketManager.List(seller, item.Guid.Full, 9001, 1, 500, MarketChannel.InGame);
            Assert.IsTrue(listing.Ok, $"seeding a listing failed with {listing.Error}");

            var lines = MarketAdvertiser.BuildLines();

            Assert.AreEqual(3, lines.Count);
            Assert.IsTrue(lines[1].Contains("Newest listing:") && !lines[1].Contains("1x"), lines[1]);
        }

        [TestMethod]
        public void BuildLines_WithAMultiUnitListing_CarriesTheCountPrefix()
        {
            SeedTunables();
            var repo = new FakeMarketRepository();
            var store = new FakeMarketItemStore();
            MarketManager.Initialize(store, new FakeMarketWallet(), repo);

            var seller = new MarketActor(101, 0x50000501, "Marketseller");
            // A specific itemGuid pins a LONE stored biota to count 1 (it cannot be split); a GROUP
            // row (SeedGroup) is what accepts a count above one, per MarketManagerTests.List_AGroupRow_AcceptsACountAboveOne.
            var group = store.SeedGroup(101, 9001, 5);
            var listing = MarketManager.List(seller, group[0].Guid.Full, 9001, 5, 500, MarketChannel.InGame);
            Assert.IsTrue(listing.Ok, $"seeding a listing failed with {listing.Error}");

            var lines = MarketAdvertiser.BuildLines();

            Assert.AreEqual(3, lines.Count);
            Assert.IsTrue(lines[1].Contains("5x"), lines[1]);
        }

        [TestMethod]
        public void BuildLines_WithALongItemName_TruncatesTheNameAndStaysUnderTheCap()
        {
            SeedTunables();
            var repo = new FakeMarketRepository();
            var store = new FakeMarketItemStore();
            MarketManager.Initialize(store, new FakeMarketWallet(), repo);

            var seller = new MarketActor(101, 0x50000501, "Marketseller");
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            item.Name = new string('X', 200);
            store.SeedItem(101, item);
            var listing = MarketManager.List(seller, item.Guid.Full, 9001, 1, 500, MarketChannel.InGame);
            Assert.IsTrue(listing.Ok, $"seeding a listing failed with {listing.Error}");

            var lines = MarketAdvertiser.BuildLines();

            Assert.AreEqual(3, lines.Count);
            Assert.IsTrue(lines[1].Length < 128, $"line must stay under the wire's 128 char cap, was {lines[1].Length}: {lines[1]}");
            Assert.IsTrue(lines[1].Contains("500 MMD each."), "truncation must not eat the price");
        }

        // ---- sender name clamp ----

        [TestMethod]
        public void ClampSenderName_AtOrOverTheWireLimit_IsClampedUnder128()
        {
            var tooLong = new string('N', 200);

            var clamped = MarketAdvertiser.ClampSenderName(tooLong);

            Assert.IsTrue(clamped.Length < 128,
                "GameMessageTurbineChat's >=128 length-prefix branch is documented buggy for the sender name - it must never be reachable here");
            Assert.AreEqual(new string('N', 127), clamped);
        }

        [TestMethod]
        public void ClampSenderName_UnderTheLimit_IsUnchanged()
        {
            Assert.AreEqual("Market Crier", MarketAdvertiser.ClampSenderName("Market Crier"));
        }

        [TestMethod]
        public void ClampSenderName_Null_PassesThrough()
        {
            Assert.IsNull(MarketAdvertiser.ClampSenderName(null));
        }

        // ---- PostNow ----

        [TestMethod]
        public void PostNow_WhenTheMarketIsDisabled_ReturnsFalseAndSendsNothing()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false),
                "market_enabled is missing from DefaultBooleanProperties");

            var posted = MarketAdvertiser.PostNow();

            Assert.IsFalse(posted);
            Assert.AreEqual(0, sent.Count);
        }

        [TestMethod]
        public void PostNow_WhenComposingTheLinesThrows_DoesNotPropagateAndReportsNotPosted()
        {
            // This is the guarantee MarketAdvertiserCommands.HandleMarketAd now relies on entirely -
            // it has no direct BuildLines() call of its own left to guard. Simulating the throw
            // through the BuildLines seam rather than actually breaking MarketManager, since nothing
            // in MarketManager's public surface can be made to throw from a test.
            SeedTunables();
            MarketManager.Initialize(new FakeMarketItemStore(), new FakeMarketWallet(), new FakeMarketRepository());
            MarketAdvertiser.BuildLines = () => throw new InvalidOperationException("composition blew up");

            var posted = MarketAdvertiser.PostNow(out var nothingToSay);

            Assert.IsFalse(posted, "a composition failure must never read as a successful post");
            Assert.IsFalse(nothingToSay, "this is a failure, not the ordinary 'nothing to say' case");
            Assert.AreEqual(0, sent.Count, "Send must never be reached when composing the lines threw");
        }

        [TestMethod]
        public void PostNow_WithNothingToSay_ReportsNothingToSayAndDoesNotPost()
        {
            SeedTunables();
            MarketManager.Initialize(new FakeMarketItemStore(), new FakeMarketWallet(), new FakeMarketRepository());
            MarketAdvertiser.BuildLines = () => new List<string>();

            var posted = MarketAdvertiser.PostNow(out var nothingToSay);

            Assert.IsFalse(posted);
            Assert.IsTrue(nothingToSay);
            Assert.AreEqual(0, sent.Count);
        }

        // ---- MarketAdvertiserJob.Evaluate ----

        [TestMethod]
        public void Evaluate_ZeroInterval_NeverPostsAndStaysUnseeded()
        {
            var decision = MarketAdvertiserJob.Evaluate(DateTime.UtcNow, 0, null, seeded: true);

            Assert.IsNull(decision.SlotToPost);
            Assert.IsNull(decision.NewLastPostedSlot);
            Assert.IsFalse(decision.NewSeeded);
        }

        [TestMethod]
        public void Evaluate_FirstCallAfterStart_SeedsWithoutPosting()
        {
            var now = new DateTime(2026, 9, 8, 6, 30, 0, DateTimeKind.Utc);

            var decision = MarketAdvertiserJob.Evaluate(now, 6, lastPostedSlot: null, seeded: false);

            Assert.IsNull(decision.SlotToPost, "a restart must never immediately fire an advert");
            Assert.AreEqual(new DateTime(2026, 9, 8, 6, 0, 0, DateTimeKind.Utc), decision.NewLastPostedSlot);
            Assert.IsTrue(decision.NewSeeded);
        }

        [TestMethod]
        public void Evaluate_OncePerSlot_DoesNotRepostWithinTheSameSlot()
        {
            var slot = new DateTime(2026, 9, 8, 6, 0, 0, DateTimeKind.Utc);

            var decision = MarketAdvertiserJob.Evaluate(slot.AddMinutes(5), 6, lastPostedSlot: slot, seeded: true);

            Assert.IsNull(decision.SlotToPost);
            Assert.AreEqual(slot, decision.NewLastPostedSlot);
        }

        [TestMethod]
        public void Evaluate_NewSlotArrives_PostsExactlyOnce()
        {
            var previousSlot = new DateTime(2026, 9, 8, 6, 0, 0, DateTimeKind.Utc);
            var now = new DateTime(2026, 9, 8, 12, 5, 0, DateTimeKind.Utc);

            var decision = MarketAdvertiserJob.Evaluate(now, 6, lastPostedSlot: previousSlot, seeded: true);

            Assert.AreEqual(new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc), decision.SlotToPost);
            Assert.AreEqual(decision.SlotToPost, decision.NewLastPostedSlot);
        }

        [TestMethod]
        public void Evaluate_AfterALongStall_PostsOnlyOnceRatherThanBurstingMissedSlots()
        {
            // Two days stalled - many 6-hour slots were missed. Only the CURRENT slot posts.
            var previousSlot = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
            var now = new DateTime(2026, 9, 8, 13, 0, 0, DateTimeKind.Utc);

            var decision = MarketAdvertiserJob.Evaluate(now, 6, lastPostedSlot: previousSlot, seeded: true);

            Assert.AreEqual(new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc), decision.SlotToPost);

            // A second Evaluate at the same "now" (simulating the very next tick, no time passed)
            // must not post again.
            var again = MarketAdvertiserJob.Evaluate(now, 6, lastPostedSlot: decision.NewLastPostedSlot, seeded: decision.NewSeeded);
            Assert.IsNull(again.SlotToPost);
        }

        private static void SeedTunables()
        {
            MarketManagerTests.SeedMarketTunables();
            Assert.IsTrue(PropertyManager.ModifyString("market_ad_site_url", "trade.acdreamweave.com"),
                "market_ad_site_url is missing from DefaultStringProperties");
            Assert.IsTrue(PropertyManager.ModifyString("market_ad_sender_name", "Market Crier"),
                "market_ad_sender_name is missing from DefaultStringProperties");
        }
    }
}
