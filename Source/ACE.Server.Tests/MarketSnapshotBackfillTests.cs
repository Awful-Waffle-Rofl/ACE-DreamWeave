using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MarketManager.BackfillSnapshots, against the same three fakes every other market test uses:
    /// no MySQL, no network, no live Player.
    ///
    /// The theme running through these is that a backfill may only ever IMPROVE a row. Most of the
    /// cases below are not "did it rewrite the snapshot" but "did it correctly refuse to", because
    /// the failure that matters here is a pass that blanks a good snapshot over a transient
    /// condition and reports success while doing it.
    /// </summary>
    [TestClass]
    public class MarketSnapshotBackfillTests
    {
        private const uint SellerAccount = 7201;
        private const uint OtherAccount = 7202;

        private const uint StoredWcid = 9001;
        private const uint GroupWcid = 9002;
        private const uint LedgerWcid = 9003;

        private const string LedgerName = "Lead Scarab";

        /// <summary>
        /// A value no projection can produce, seeded into every stale snapshot so "the document
        /// changed" is never an accident of two sparse snapshots happening to match.
        /// </summary>
        private const string StaleMarker = "stale-projection";

        /// <summary>Owners of the seeded listings, for driving a REAL Delist from another thread.</summary>
        private static readonly MarketActor SellerActor = new MarketActor(SellerAccount, 0x50000101, "Backfillseller");
        private static readonly MarketActor OtherActor = new MarketActor(OtherAccount, 0x50000102, "Backfillbuyer");

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;

        private IClothingIconSource savedClothingIcons;
        private Func<uint, Weenie> savedWeenieLookup;

        /// <summary>What the ledger oracle answers with. Set to null to model a failed world read.</summary>
        private Weenie ledgerWeenie;

        [TestInitialize]
        public void Setup()
        {
            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();

            savedClothingIcons = MarketSnapshot.ClothingIcons;
            savedWeenieLookup = MarketManager.WeenieLookup;

            // Nothing here may depend on the dats being installed.
            MarketSnapshot.ClothingIcons = null;

            ledgerWeenie = LedgerFixtureWeenie();

            MarketManager.WeenieLookup = wcid => wcid == LedgerWcid ? ledgerWeenie : null;

            MarketManagerTests.SeedMarketTunables();
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();

            MarketManager.WeenieLookup = savedWeenieLookup;
            MarketSnapshot.ClothingIcons = savedClothingIcons;
        }

        /// <summary>
        /// Builds the index from whatever has been seeded into <see cref="repo"/> and
        /// <see cref="store"/>. Called by each test AFTER its fixture, because the manager reads the
        /// listing table exactly once, at Initialize.
        /// </summary>
        private void Boot() => MarketManager.Initialize(store, wallet, repo);

        private static Weenie LedgerFixtureWeenie()
        {
            return new Weenie
            {
                WeenieClassId = LedgerWcid,
                WeenieType = WeenieType.Stackable,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, LedgerName } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.MaterialType, (int)MaterialType.Silver },
                    { PropertyInt.Value, 25 },
                    { PropertyInt.EncumbranceVal, 5 },
                    { PropertyInt.MaxStackSize, 100 },
                },
            };
        }

        /// <summary>
        /// A listing whose stored snapshot carries a NAME and a marker. The name is real, so the
        /// degraded guard is satisfied and every test below exercises the code it means to; every
        /// other real field is absent, so a correct re-projection is always a visible change.
        ///
        /// The marker in <c>ItemTypeName</c> is load bearing. Without it the placeholder fixture -
        /// a row already reading "Item wcid", re-projected by an oracle that also answers "Item
        /// wcid" - serializes to a byte-identical document and its test would report Unchanged for
        /// a reason that has nothing to do with what it is checking.
        /// </summary>
        private ShardMarketListing SeedListingRow(uint id, uint accountId, uint? itemGuid, uint wcid,
                                                  string snapshotName, int status = 0)
        {
            var row = new ShardMarketListing
            {
                Id = id,
                SellerAccountId = accountId,
                SellerCharacterGuid = 0x50000000u + id,
                SellerCharacterName = "Backfillseller",
                ItemGuid = itemGuid,
                ActiveItemGuid = status == 0 ? itemGuid : null,
                ActiveLedgerWcid = status == 0 && itemGuid == null ? (uint?)wcid : null,
                Wcid = wcid,
                Count = 1,
                PriceMmd = 5,
                Status = status,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                ClosedAt = status == 0 ? (DateTime?)null : DateTime.UtcNow,
                SnapshotJson = MarketSnapshot.Serialize(new ListingSnapshot
                {
                    Name = snapshotName,
                    Wcid = wcid,
                    ItemTypeName = StaleMarker,
                }),
            };

            repo.Listings.Add(row);

            return row;
        }

        private WorldObject SeedStoredItem(uint accountId, uint wcid = StoredWcid)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 1);

            store.SeedItem(accountId, item);

            return item;
        }

        private static ShardMarketListing Row(FakeMarketRepository repository, uint id)
            => repository.Listings.Single(l => l.Id == id);

        // ---- refusal to run ----

        [TestMethod]
        public void WithNoIndex_TheBackfillRefusesRatherThanReportingAnEmptyPass()
        {
            // Never booted. "Nothing to do" and "I could not look" must not render the same.
            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.IsTrue(report.NotInitialized, "a manager with no index must say so rather than report a clean pass");
            Assert.AreEqual(0, report.Considered);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
        }

        [TestMethod]
        public void TheKillSwitchBeingOff_DoesNotBlockTheBackfill()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            // The operator state a backfill is most likely to be run inside. Enabled reads the
            // tunable; the pass reads the raw index flag, exactly as the invalidation hooks do.
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));

            try
            {
                var report = MarketManager.BackfillSnapshots(100, false);

                Assert.IsFalse(report.NotInitialized);
                Assert.AreEqual(1, report.Considered);
                Assert.AreEqual(1, report.Rewritten);
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", true));
            }
        }

        // ---- what it walks ----

        [TestMethod]
        public void ClosedListings_AreNeverConsidered()
        {
            var active = SeedStoredItem(SellerAccount);
            var sold = SeedStoredItem(SellerAccount);

            SeedListingRow(1, SellerAccount, active.Guid.Full, StoredWcid, active.Name);

            var soldRow = SeedListingRow(2, SellerAccount, sold.Guid.Full, StoredWcid, sold.Name,
                                         (int)MarketListingStatus.Sold);
            var soldJson = soldRow.SnapshotJson;

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, report.Considered, "only the Active listing may be walked");
            Assert.AreEqual(1, report.Rewritten);

            Assert.AreEqual(soldJson, Row(repo, 2).SnapshotJson,
                            "a closed listing keeps the snapshot it captured, forever");

            CollectionAssert.AreEqual(new List<uint> { 1 }, repo.SnapshotWrites.Select(w => w.ListingId).ToList());
        }

        /// <summary>Seeds <paramref name="count"/> Active stored listings on one account, ids 1..count.</summary>
        private void SeedManyListings(uint count, uint accountId = SellerAccount)
        {
            for (uint i = 1; i <= count; i++)
            {
                var item = SeedStoredItem(accountId);
                SeedListingRow(i, accountId, item.Guid.Full, StoredWcid, item.Name);
            }
        }

        private List<uint> WrittenIds() => repo.SnapshotWrites.Select(w => w.ListingId).ToList();

        [TestMethod]
        public void SuccessivePasses_WalkForwardAndRewindOnceTheSweepFinishes()
        {
            SeedManyListings(3);
            Boot();

            var first = MarketManager.BackfillSnapshots(2, false);

            Assert.AreEqual(2, first.Considered);
            Assert.AreEqual(1, first.RemainingActive, "one listing is still ahead of the cursor");
            Assert.IsFalse(first.SweepComplete);
            CollectionAssert.AreEqual(new List<uint> { 1, 2 }, WrittenIds());

            repo.SnapshotWrites.Clear();

            var second = MarketManager.BackfillSnapshots(2, false);

            // The whole point: the SAME command advances rather than re-walking the same page.
            Assert.AreEqual(2u, second.ResumedFromListingId, "the second pass must resume after listing 2");
            Assert.AreEqual(1, second.Considered);
            Assert.IsTrue(second.SweepComplete);
            CollectionAssert.AreEqual(new List<uint> { 3 }, WrittenIds());

            var third = MarketManager.BackfillSnapshots(2, false);

            Assert.AreEqual(0u, third.ResumedFromListingId, "a finished sweep rewinds to the start of the market");
            Assert.AreEqual(0, third.Considered, "and the rewound sweep has nothing stale left to walk");

            // That the cursor REPORTS zero is not proof it landed at the start. The full-sweep arm
            // ignores the version gate, so it walks from wherever the cursor actually is - and it
            // has to come back with the first page of the market.
            var fourth = MarketManager.BackfillSnapshots(2, false, true);

            Assert.AreEqual(2, fourth.Considered);
            Assert.AreEqual(2, fourth.Unchanged, "and re-walking a done market finds nothing to write");
        }

        [TestMethod]
        public void APassAtTheCap_ResumesInsteadOfRewalkingTheIdenticalPage()
        {
            // Over the per-pass ceiling on purpose. Selection is "the first n by a STABLE ordering",
            // so without a cursor every pass would walk the identical first 500 rows and the tail of
            // the market would be permanently unreachable - raising the cap only moves that wall.
            const uint total = MarketManager.MaxBackfillRows + 5;

            SeedManyListings(total);
            Boot();

            var first = MarketManager.BackfillSnapshots(MarketManager.MaxBackfillRows, false);

            Assert.AreEqual(MarketManager.MaxBackfillRows, first.Considered);
            Assert.AreEqual(5, first.RemainingActive);
            Assert.IsFalse(first.SweepComplete);

            var firstIds = WrittenIds();

            Assert.AreEqual(MarketManager.MaxBackfillRows, firstIds.Count);

            repo.SnapshotWrites.Clear();

            var second = MarketManager.BackfillSnapshots(MarketManager.MaxBackfillRows, false);

            var secondIds = WrittenIds();

            Assert.AreEqual(5, second.Considered, "the second pass at the cap must consider a DIFFERENT set");
            Assert.AreEqual(0, second.RemainingActive);
            Assert.IsTrue(second.SweepComplete);

            CollectionAssert.AreEquivalent(new List<uint> { 501, 502, 503, 504, 505 }, secondIds);
            Assert.AreEqual(0, secondIds.Intersect(firstIds).Count(), "the two passes must not overlap at all");
        }

        [TestMethod]
        public void APreviewDoesNotConsumeTheRunsPlaceInTheWalk()
        {
            SeedManyListings(3);
            Boot();

            var preview = MarketManager.BackfillSnapshots(2, true);

            Assert.AreEqual(2, preview.Considered);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);

            var run = MarketManager.BackfillSnapshots(2, false);

            // An operator who previews and then runs must not have the run skip the page the preview
            // just showed them.
            Assert.AreEqual(0u, run.ResumedFromListingId, "the run's cursor is its own");
            Assert.AreEqual(2, run.Considered);
            CollectionAssert.AreEqual(new List<uint> { 1, 2 }, WrittenIds());
        }

        [TestMethod]
        public void ResetBackfillCursor_SendsTheNextPassBackToTheStart()
        {
            SeedManyListings(3);
            Boot();

            var first = MarketManager.BackfillSnapshots(2, false);

            Assert.IsFalse(first.SweepComplete, "the fixture must leave the sweep part way through");

            MarketManager.ResetBackfillCursor();

            // The full-sweep arm on purpose: the first pass brought its page up to the current
            // version, so a version-gated second pass would skip those rows and could not show
            // whether the cursor moved at all. What is under test here is the cursor, not selection.
            var second = MarketManager.BackfillSnapshots(2, false, true);

            Assert.AreEqual(0u, second.ResumedFromListingId);
            Assert.AreEqual(2, second.Considered);
            Assert.AreEqual(2, second.Unchanged, "it re-walked the page the first pass had already done");
        }

        // ---- the ledger path ----

        [TestMethod]
        public void LedgerListing_ProjectsThroughTheWeenie_NotAnEmptyProbe()
        {
            store.SeedLedger(SellerAccount, LedgerWcid, 40);
            SeedListingRow(1, SellerAccount, null, LedgerWcid, LedgerName);

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            // FromLedgerProbe(entry.WorldObject, wcid) over a ledger row passes NULL and yields an
            // empty snapshot, which the degraded guard would refuse. So a rewrite with a real name
            // here is only reachable through the weenie oracle.
            Assert.AreEqual(0, report.ProjectionDegraded, "the ledger projection must not degrade");
            Assert.AreEqual(1, report.Rewritten);

            var written = MarketSnapshot.Deserialize(Row(repo, 1).SnapshotJson);

            Assert.IsFalse(string.IsNullOrEmpty(written.Name), "a ledger snapshot must carry a real name");
            Assert.AreEqual(LedgerName, written.Name);
            Assert.AreEqual(LedgerWcid, written.Wcid);
            Assert.AreEqual((int)ItemType.Misc, written.ItemType, "the weenie's own fields must come through");
        }

        // ---- resolution ----

        [TestMethod]
        public void UnresolvableItemGuid_WritesNothingAndCountsItemUnresolved()
        {
            // The purchase-in-flight shape: the item has left the seller's vault but the listing has
            // not been closed yet.
            SeedListingRow(1, SellerAccount, 0x7FFFFFF1u, StoredWcid, "Something Listed");

            var before = Row(repo, 1).SnapshotJson;

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, report.Considered);
            Assert.AreEqual(1, report.ItemUnresolved);
            Assert.AreEqual(0, report.Rewritten);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls, "an unresolved item must not reach the database at all");
            Assert.AreEqual(before, Row(repo, 1).SnapshotJson);

            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(1).Status,
                            "the backfill has no authority to invalidate a listing it cannot resolve");
        }

        [TestMethod]
        public void AListingPinnedToAGroupMemberRatherThanItsRepresentative_IsStillResolved()
        {
            var members = store.SeedGroup(SellerAccount, GroupWcid, 3);

            // A group row exposes only members[0] as its Guid. A deposit of an equivalent item
            // inserts at the front and moves that representative, so a listing created earlier is
            // left naming a member the row no longer leads with. Matching on Guid alone silently
            // skips those, and skips them as "unresolved" - indistinguishable from the benign case.
            var anchor = members[2];

            Assert.AreNotEqual(members[0].Guid.Full, anchor.Guid.Full, "the fixture must not pin to the representative");

            SeedListingRow(1, SellerAccount, anchor.Guid.Full, GroupWcid, anchor.Name);

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(0, report.ItemUnresolved, "a listing anchored to a non-leading group member must resolve");
            Assert.AreEqual(1, report.Rewritten);

            var written = MarketSnapshot.Deserialize(Row(repo, 1).SnapshotJson);

            Assert.AreEqual(GroupWcid, written.Wcid);
            Assert.AreEqual(anchor.Name, written.Name);
        }

        // ---- the store being unavailable ----

        [TestMethod]
        public void ANotReadyStore_SkipsEveryListingOnThatAccountAndWritesNothingForIt()
        {
            var first = SeedStoredItem(SellerAccount);
            var second = SeedStoredItem(SellerAccount);
            var elsewhere = SeedStoredItem(OtherAccount);

            SeedListingRow(1, SellerAccount, first.Guid.Full, StoredWcid, first.Name);
            SeedListingRow(2, SellerAccount, second.Guid.Full, StoredWcid, second.Name);
            SeedListingRow(3, OtherAccount, elsewhere.Guid.Full, StoredWcid, elsewhere.Name);

            var firstJson = Row(repo, 1).SnapshotJson;
            var secondJson = Row(repo, 2).SnapshotJson;

            // "Not ready" and "holds nothing" are different facts: an unready store answers
            // GetEntries with an empty list, so a pass that did not check would read every listing
            // on this account as unresolved.
            store.NotReadyAccounts.Add(SellerAccount);

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(3, report.Considered);
            Assert.AreEqual(2, report.StoreNotReady);
            Assert.AreEqual(0, report.ItemUnresolved, "an unready vault is never reported as a missing item");
            Assert.AreEqual(1, report.Rewritten, "the other account is unaffected");

            Assert.AreEqual(firstJson, Row(repo, 1).SnapshotJson);
            Assert.AreEqual(secondJson, Row(repo, 2).SnapshotJson);

            CollectionAssert.AreEqual(new List<uint> { 3 }, repo.SnapshotWrites.Select(w => w.ListingId).ToList());
        }

        // ---- the degraded guard ----

        [TestMethod]
        public void AProjectionThatDegradesToThePlaceholderName_WritesNothing()
        {
            store.SeedLedger(SellerAccount, LedgerWcid, 40);
            SeedListingRow(1, SellerAccount, null, LedgerWcid, LedgerName);

            // The world read fails, so the ledger projection falls back to "Item <wcid>". That is a
            // fine answer for a row that never had a better one and a REGRESSION over this one.
            ledgerWeenie = null;

            var before = Row(repo, 1).SnapshotJson;

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, report.ProjectionDegraded);
            Assert.AreEqual(0, report.Rewritten);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
            Assert.AreEqual(before, Row(repo, 1).SnapshotJson);
            Assert.AreEqual(LedgerName, MarketManager.GetListing(1).Snapshot.Name,
                            "the in-memory snapshot must be left alone too");
        }

        [TestMethod]
        public void ThePlaceholderNameIsAcceptedWhenTheStoredSnapshotHasNoBetterOne()
        {
            store.SeedLedger(SellerAccount, LedgerWcid, 40);

            // The row already reads "Item 9003", so the same answer is not a regression and the rest
            // of the projection is still worth storing. Without this branch the guard would pin every
            // never-named listing to whatever it first captured.
            SeedListingRow(1, SellerAccount, null, LedgerWcid, $"Item {LedgerWcid}");

            ledgerWeenie = null;

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(0, report.ProjectionDegraded);
            Assert.AreEqual(1, report.Rewritten);
        }

        // ---- idempotency ----

        [TestMethod]
        public void ASecondPassOverTheSameFixture_RewritesNothing()
        {
            var stored = SeedStoredItem(SellerAccount);
            store.SeedLedger(SellerAccount, LedgerWcid, 40);

            SeedListingRow(1, SellerAccount, stored.Guid.Full, StoredWcid, stored.Name);
            SeedListingRow(2, SellerAccount, null, LedgerWcid, LedgerName);

            Boot();

            var first = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(2, first.Rewritten);
            Assert.AreEqual(0, first.Unchanged);

            var callsAfterFirst = repo.UpdateListingSnapshotsCalls;

            Assert.AreEqual(1, callsAfterFirst, "one batched write for the whole pass");

            // The full-sweep arm, so both rows are genuinely RE-PROJECTED rather than excused by the
            // version gate. What this checks is that the projection is idempotent - that running it
            // twice over an unchanged item produces the identical document - and skipping the rows
            // would prove nothing about that.
            var second = MarketManager.BackfillSnapshots(100, false, true);

            Assert.AreEqual(2, second.Considered);
            Assert.AreEqual(0, second.Rewritten);
            Assert.AreEqual(2, second.Unchanged);
            Assert.AreEqual(callsAfterFirst, repo.UpdateListingSnapshotsCalls,
                            "a pass that changes nothing must not touch the repository at all");

            // And the cheap version of the same question, which is what a restart actually runs.
            var gated = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(0, gated.Considered, "a version-gated pass over an up-to-date market walks nothing");
            Assert.AreEqual(0, gated.StaleActive);
        }

        // ---- races and write failures ----

        [TestMethod]
        public void AListingClosedInTheDatabaseMidPass_CountsClosedMidPassAndDoesNotThrow()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            // Closed in the database only. The in-memory index still reads Active, which is exactly
            // the window the DAO's `Status == 0` predicate exists to lose safely: the statement
            // matches nothing and reports zero rows rather than reopening a closed listing.
            Row(repo, 1).Status = (int)MarketListingStatus.Sold;

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, report.Considered);
            Assert.AreEqual(0, report.Rewritten);
            Assert.AreEqual(1, report.ClosedMidPass);
            Assert.AreEqual(0, report.WriteFailed, "matching no rows is not a write failure");
            Assert.AreEqual(1, repo.UpdateListingSnapshotsCalls, "the write was attempted");
        }

        [TestMethod]
        public void ARowWhoseWriteThrows_IsAWriteFailureAndNeverAConcurrentClose()
        {
            SeedManyListings(3);
            Boot();

            // Three different fates in ONE batch, which is what makes this discriminating. Listing 1
            // lands. Listing 2's statement THROWS and its listing is still Active, so its stored
            // snapshot is now behind memory. Listing 3's row closed in the database, so its
            // statement runs and matches nothing.
            repo.ThrowOnSnapshotWriteListingIds.Add(2);

            var beforeTwo = Row(repo, 2).SnapshotJson;

            Row(repo, 3).Status = (int)MarketListingStatus.Sold;

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(3, report.Considered);
            Assert.AreEqual(1, report.Rewritten, "only listing 1 actually landed");

            // Deriving this from `submitted - updated`, as a bare row count forces, would file the
            // thrower under closed_mid_pass - where an operator re-running forever reads it as
            // ordinary churn and never learns a write is failing.
            Assert.AreEqual(1, report.WriteFailed, "the thrower is a WRITE FAILURE");
            Assert.AreEqual(1, report.ClosedMidPass, "and exactly one row was a genuine concurrent close");

            Assert.AreEqual(beforeTwo, Row(repo, 2).SnapshotJson, "the thrower keeps its stored snapshot");
        }

        [TestMethod]
        public void AListingWhoseWriteFailed_IsResubmittedUntilAWriteLands()
        {
            SeedManyListings(2);
            Boot();

            repo.ThrowOnSnapshotWriteListingIds.Add(1);

            var first = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, first.Rewritten, "listing 2 landed");
            Assert.AreEqual(1, first.WriteFailed, "listing 1's write threw");

            var staleJson = Row(repo, 1).SnapshotJson;

            // The shard recovers and the operator does exactly what the command told them to do.
            repo.ThrowOnSnapshotWriteListingIds.Clear();
            repo.SnapshotWrites.Clear();
            MarketManager.ResetBackfillCursor();

            var second = MarketManager.BackfillSnapshots(100, false);

            // THE TRAP, and it falls straight out of the memory-first ordering: the failed row's
            // IN-MEMORY snapshot was already updated before the write was attempted, so a later pass
            // re-projects to something byte-identical to it and calls the row Unchanged. The database
            // row stays stale forever while every subsequent pass reports a clean sweep, and a
            // restart then reloads the stale snapshot - reintroducing exactly what this exists to fix.
            CollectionAssert.Contains(WrittenIds(), 1u,
                                      "a listing whose write failed must be resubmitted until a write actually lands");

            Assert.AreEqual(1, second.Rewritten);
            Assert.AreEqual(0, second.WriteFailed);
            Assert.AreNotEqual(staleJson, Row(repo, 1).SnapshotJson,
                               "the database must finally carry the fresh snapshot");

            // And the retry has to CLEAR once it lands, or the row is resubmitted forever.
            repo.SnapshotWrites.Clear();
            MarketManager.ResetBackfillCursor();

            // The full-sweep arm, so the row is definitely WALKED. A version-gated pass would skip
            // it now that its stored snapshot is current, and "skipped" and "walked and left alone"
            // would then be indistinguishable - which is precisely the difference under test. A
            // retry that had not cleared is resubmitted on either arm.
            var third = MarketManager.BackfillSnapshots(100, false, true);

            Assert.AreEqual(0, third.Rewritten);
            Assert.AreEqual(2, third.Unchanged, "the retry must clear once the write has landed");
            Assert.AreEqual(0, repo.SnapshotWrites.Count, "and nothing is resubmitted after that");
        }

        [TestMethod]
        public void ARetryThatFailsAgain_IsNotCountedAsARepair()
        {
            SeedManyListings(2);
            Boot();

            // The shard stays broken across BOTH passes.
            repo.ThrowOnSnapshotWriteListingIds.Add(1);

            var first = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, first.WriteFailed);

            repo.SnapshotWrites.Clear();

            var second = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, second.WriteFailed, "it failed again");
            Assert.AreEqual(0, second.Rewritten, "so nothing was actually rewritten");

            // The counter exists to show an operator that a REPAIR happened. Counting the
            // submission rather than the outcome renders as "rewritten 0, write_failed 1,
            // retried 1", which claims a repair on a pass that repaired nothing.
            Assert.AreEqual(0, second.RetriedAfterWriteFailure,
                            "a retry that failed again is a resubmission, not a repair");
        }

        [TestMethod]
        public void ARememberedFailureBehindTheCursor_IsRetriedOnTheVeryNextRun()
        {
            SeedManyListings(4);
            Boot();

            repo.ThrowOnSnapshotWriteListingIds.Add(1);

            // Page one is listings 1 and 2, and listing 1's write throws. The cursor is now past it.
            var first = MarketManager.BackfillSnapshots(2, false);

            Assert.AreEqual(1, first.WriteFailed);
            Assert.IsFalse(first.SweepComplete, "the fixture must leave the cursor past the failed row");

            repo.ThrowOnSnapshotWriteListingIds.Clear();
            repo.SnapshotWrites.Clear();

            // Page two walks forward - and must ALSO come back for listing 1. Anchoring the retry to
            // the walk means a remembered row waits for the sweep to wrap, so on a market bigger
            // than one page an operator re-running sees "retried: 0" for many invocations while the
            // message tells them the row is remembered and will be resubmitted.
            var second = MarketManager.BackfillSnapshots(2, false);

            CollectionAssert.Contains(WrittenIds(), 1u,
                                      "a remembered failure must be retried on the very next run, not when the sweep wraps");

            Assert.AreEqual(1, second.RetriedAfterWriteFailure);
            Assert.AreEqual(2, second.Considered, "the retry must not be considered twice");

            // ...and the detour must not cost forward progress or drag the walk backwards.
            var third = MarketManager.BackfillSnapshots(2, false);

            Assert.AreEqual(3u, third.ResumedFromListingId,
                            "a behind-the-cursor retry must not drag the cursor back over ground already walked");
        }

        [TestMethod]
        public void ARowTheRepositoryNeverAccountedFor_IsRetriedRatherThanForgotten()
        {
            SeedManyListings(2);
            Boot();

            repo.UnderReportSnapshotWriteListingIds.Add(1);

            var first = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, first.WriteFailed, "an unaccounted row is already reported as a write failure");

            var staleJson = Row(repo, 1).SnapshotJson;

            repo.UnderReportSnapshotWriteListingIds.Clear();
            repo.SnapshotWrites.Clear();

            var second = MarketManager.BackfillSnapshots(100, false);

            // Reporting it as failed but not REMEMBERING it reproduces, for that row, exactly the
            // bug the retry set exists to fix: memory was updated, the database was not, and the
            // comparison will call it unchanged forever.
            CollectionAssert.Contains(WrittenIds(), 1u,
                                      "a row whose outcome nobody knows deserves at least the treatment a known failure gets");

            Assert.AreNotEqual(staleJson, Row(repo, 1).SnapshotJson,
                               "and the database must finally carry the fresh snapshot");
        }

        [TestMethod]
        public void AWholeBatchFailure_IsAlsoRetriedOnTheNextPass()
        {
            SeedManyListings(2);
            Boot();

            repo.FailUpdateListingSnapshots = true;

            var first = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(2, first.WriteFailed, "the whole batch failed");
            Assert.AreEqual(0, first.Rewritten);

            // A batch that never ran leaves memory ahead of the database for EVERY row it carried,
            // so all of them need the same retry treatment a single throwing row does.
            repo.FailUpdateListingSnapshots = false;
            repo.SnapshotWrites.Clear();

            var second = MarketManager.BackfillSnapshots(100, false);

            CollectionAssert.AreEquivalent(new List<uint> { 1, 2 }, WrittenIds());
            Assert.AreEqual(2, second.Rewritten);
            Assert.AreEqual(0, second.WriteFailed);
        }

        [TestMethod]
        public void AnIndexRebuildMidPass_LeavesTheCursorResetRatherThanHoldingAStalePage()
        {
            SeedManyListings(3);
            Boot();

            var hold = new ManualResetEventSlim(false);

            try
            {
                store.StoreHold = hold;

                // Page 1 of 2: this pass has already captured ids 1 and 2 and wants to leave the
                // cursor sitting after listing 2.
                var pass = Task.Run(() => MarketManager.BackfillSnapshots(2, false));

                Assert.IsTrue(store.DelayEntered.Task.Wait(TimeSpan.FromSeconds(10)),
                              "the pass never reached the vault read");

                // The index is rebuilt underneath it, which mints a new FeedGeneration. The page in
                // flight is now a position inside an index that no longer exists.
                MarketManager.Initialize(store, wallet, repo);

                // A THIRD actor - an operator typing /marketbackfill reset - re-syncs the SHARED
                // generation field to the new value. That is what lets an end-of-pass check which
                // compares that shared field against itself agree, even though the page it is about
                // to commit was taken from the old index.
                MarketManager.ResetBackfillCursor();

                store.StoreHold = null;
                hold.Set();

                pass.GetAwaiter().GetResult();
            }
            finally
            {
                store.StoreHold = null;
                hold.Set();
                hold.Dispose();
            }

            var next = MarketManager.BackfillSnapshots(2, false);

            Assert.AreEqual(0u, next.ResumedFromListingId,
                            "a page taken from a previous index must never be written into the rebuilt index's cursor");
            Assert.AreEqual(0u, next.ResumedFromAccountId);
        }

        [TestMethod]
        public void AFailedWrite_CountsWriteFailedRatherThanClosedMidPass()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            repo.FailUpdateListingSnapshots = true;

            var report = MarketManager.BackfillSnapshots(100, false);

            // A shard outage must never read as "every listing closed while I was looking at it".
            Assert.AreEqual(1, report.WriteFailed);
            Assert.AreEqual(0, report.ClosedMidPass);
            Assert.AreEqual(0, report.Rewritten);
        }

        // ---- the IN-MEMORY close guards ----
        //
        // These two are the guards the design rests on, and neither is reachable by closing a row in
        // the fake database: that only exercises the DAO's `status = 0` predicate. A real close has
        // to land in the INDEX, from another thread, while the pass is mid-flight. StoreHold blocks
        // the pass inside the vault read so the window is deterministic rather than raced for.

        [TestMethod]
        public void AListingDelistedWhileThePassIsProjectingIt_IsNotWrittenAnywhere()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            var original = MarketManager.GetListing(1).Snapshot;
            var hold = new ManualResetEventSlim(false);

            try
            {
                store.StoreHold = hold;

                var pass = Task.Run(() => MarketManager.BackfillSnapshots(100, false));

                Assert.IsTrue(store.DelayEntered.Task.Wait(TimeSpan.FromSeconds(10)),
                              "the pass never reached the vault read");

                // A REAL close, through the path a player's /market delist takes, landing after the
                // pass has picked this listing up and before it writes it back.
                var delisted = MarketManager.Delist(SellerActor, 1, MarketChannel.InGame);

                Assert.IsTrue(delisted.Ok, $"the delist itself failed: {delisted.Error}");

                store.StoreHold = null;
                hold.Set();

                var report = pass.GetAwaiter().GetResult();

                Assert.AreEqual(1, report.Considered);
                Assert.AreEqual(0, report.Rewritten);
                Assert.AreEqual(1, report.ClosedMidPass);

                // THE DISCRIMINATORS. Without the `live.Status == Active` re-check at the memory
                // write, the pass would overwrite the closed listing's in-memory snapshot AND submit
                // a database row for it - and the report alone would not show the difference,
                // because the DAO's own predicate would then report the row as a miss anyway.
                Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls,
                                "a listing closed in memory must never reach the database");
                Assert.AreSame(original, MarketManager.GetListing(1).Snapshot,
                               "and must keep the snapshot it was closed with");
            }
            finally
            {
                store.StoreHold = null;
                hold.Set();
                hold.Dispose();
            }

            var row = Row(repo, 1);

            Assert.AreEqual((int)MarketListingStatus.Delisted, row.Status, "the close must have persisted");
            Assert.IsNotNull(row.ClosedAt);
            Assert.IsNull(row.ActiveItemGuid, "and cleared the active key");
        }

        [TestMethod]
        public void AListingDelistedBeforeItsTurnInTheWalk_IsNeverConsidered()
        {
            var mine = SeedStoredItem(SellerAccount);
            var theirs = SeedStoredItem(OtherAccount);

            SeedListingRow(1, SellerAccount, mine.Guid.Full, StoredWcid, mine.Name);
            SeedListingRow(2, OtherAccount, theirs.Guid.Full, StoredWcid, theirs.Name);

            Boot();

            var originalSecond = MarketManager.GetListing(2).Snapshot;
            var hold = new ManualResetEventSlim(false);

            try
            {
                store.StoreHold = hold;

                var pass = Task.Run(() => MarketManager.BackfillSnapshots(100, false));

                Assert.IsTrue(store.DelayEntered.Task.Wait(TimeSpan.FromSeconds(10)),
                              "the pass never reached the vault read");

                // Closed while the pass is still on the FIRST account, so listing 2 is already dead
                // by the time the walk reaches it.
                Assert.IsTrue(MarketManager.Delist(OtherActor, 2, MarketChannel.InGame).Ok);

                store.StoreHold = null;
                hold.Set();

                var report = pass.GetAwaiter().GetResult();

                // The discriminator for the pickup guard: a listing closed before its turn is not
                // merely skipped at the write, it is never examined at all.
                Assert.AreEqual(1, report.Considered, "the closed listing must not be considered");
                Assert.AreEqual(1, report.ClosedMidPass);
                Assert.AreEqual(1, report.Rewritten, "the other account's listing still went through");

                CollectionAssert.AreEqual(new List<uint> { 1 }, WrittenIds());
                Assert.AreSame(originalSecond, MarketManager.GetListing(2).Snapshot);
            }
            finally
            {
                store.StoreHold = null;
                hold.Set();
                hold.Dispose();
            }
        }

        // ---- preview ----

        [TestMethod]
        public void Preview_ReportsWhatWouldChangeAndWritesNothingAnywhere()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            var before = Row(repo, 1).SnapshotJson;
            var inMemoryBefore = MarketManager.GetListing(1).Snapshot;
            var sequenceBefore = MarketManager.ChangeSequence;

            var report = MarketManager.BackfillSnapshots(100, true);

            Assert.AreEqual(1, report.Considered);
            Assert.AreEqual(1, report.Rewritten, "a preview reports what it WOULD rewrite");

            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
            Assert.AreEqual(before, Row(repo, 1).SnapshotJson);
            Assert.AreSame(inMemoryBefore, MarketManager.GetListing(1).Snapshot,
                           "a preview must not touch the in-memory snapshot either");
            Assert.AreEqual(sequenceBefore, MarketManager.ChangeSequence,
                            "and must not advance the feed");
        }

        // ---- the feed ----

        [TestMethod]
        public void ARewrittenListing_ReachesTheChangeFeedWithItsNewSnapshot()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            var since = MarketManager.ChangeSequence;

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, report.Rewritten);

            var page = MarketManager.GetListingChanges(since, 100, out _);
            var changed = page.SingleOrDefault(l => l.Id == 1);

            Assert.IsNotNull(changed, "a rewritten snapshot must advance the sequence so clients resync it");
            Assert.AreEqual(Row(repo, 1).SnapshotJson, MarketSnapshot.Serialize(changed.Snapshot),
                            "the feed and the database must carry the same snapshot");
            Assert.AreEqual((int)ItemType.Misc, changed.Snapshot.ItemType, "and it must be the re-projected one");
        }

        // ---- the snapshot version, and selection by it ----

        /// <summary>
        /// A row that is stale in CONTENT but already stamped current. If version gating works it is
        /// never walked; if it is walked it is rewritten, because the marker guarantees the
        /// re-projection differs. So the two outcomes cannot be confused.
        /// </summary>
        private ShardMarketListing SeedCurrentVersionListingRow(uint id, uint accountId, uint? itemGuid,
                                                                uint wcid, string snapshotName)
        {
            var row = SeedListingRow(id, accountId, itemGuid, wcid, snapshotName);

            var snapshot = MarketSnapshot.Deserialize(row.SnapshotJson);
            snapshot.SnapshotVersion = MarketSnapshot.CurrentVersion;
            row.SnapshotJson = MarketSnapshot.Serialize(snapshot);

            return row;
        }

        [TestMethod]
        public void AProjectionFromAnItem_StampsTheCurrentVersion()
        {
            var item = SeedStoredItem(SellerAccount);

            Assert.AreEqual(MarketSnapshot.CurrentVersion, MarketSnapshot.FromItem(item).SnapshotVersion,
                            "a real projection has to say which projection built it");
        }

        [TestMethod]
        public void AProjectionFromAWeenie_StampsTheSameVersion()
        {
            Assert.AreEqual(MarketSnapshot.CurrentVersion, MarketSnapshot.FromWeenie(LedgerFixtureWeenie()).SnapshotVersion,
                            "the two projections are one contract and must not drift apart");
        }

        [TestMethod]
        public void APlaceholderProjection_IsNotStamped()
        {
            // The version means "a real projection of this version built this". Stamping a document
            // that describes nothing would retire it from selection permanently, which is the exact
            // row that most deserves another attempt once its oracle works again.
            Assert.IsNull(MarketSnapshot.FromItem(null).SnapshotVersion);
            Assert.IsNull(MarketSnapshot.FromWeenie(null).SnapshotVersion);

            // And it must not even appear on the wire, or a consumer reading "is this versioned"
            // sees a key whose presence promises more than the document delivers.
            Assert.IsFalse(MarketSnapshot.Serialize(MarketSnapshot.FromItem(null)).Contains("snapshot_version"));
        }

        [TestMethod]
        public void AStoredSnapshotFromBeforeVersioning_IsSelected()
        {
            var item = SeedStoredItem(SellerAccount);

            // Exactly what a legacy row looks like: no snapshot_version key at all.
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Assert.IsFalse(Row(repo, 1).SnapshotJson.Contains("snapshot_version"),
                           "the fixture must model a document written before the field existed");

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, report.Considered);
            Assert.AreEqual(1, report.Rewritten);
            Assert.AreEqual(1, report.StaleActive, "and it must be reported as the stale row it is");
        }

        [TestMethod]
        public void AStoredSnapshotAlreadyAtTheCurrentVersion_IsNotSelected()
        {
            var item = SeedStoredItem(SellerAccount);

            SeedCurrentVersionListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            var before = Row(repo, 1).SnapshotJson;

            Boot();

            var report = MarketManager.BackfillSnapshots(100, false);

            // The whole point of versioning: a pass over an up-to-date market costs one index scan
            // and touches no vault at all.
            Assert.AreEqual(0, report.Considered, "an up-to-date row must not be walked");
            Assert.AreEqual(0, report.Rewritten);
            Assert.AreEqual(0, report.StaleActive);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
            Assert.AreEqual(before, Row(repo, 1).SnapshotJson);
        }

        [TestMethod]
        public void TheFullSweepArm_WalksAnUpToDateRowAnyway()
        {
            var item = SeedStoredItem(SellerAccount);

            SeedCurrentVersionListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            // The escape hatch for a projection BUGFIX nobody bumped the constant for: version
            // gating is blind to it by construction, so there has to be an arm that ignores version.
            var report = MarketManager.BackfillSnapshots(100, false, true);

            Assert.AreEqual(1, report.Considered, "the full sweep must ignore the version stamp");
            Assert.AreEqual(1, report.Rewritten);
            Assert.AreEqual(0, report.StaleActive,
                            "and must still report the stale count honestly - nothing here is stale by version");
        }

        [TestMethod]
        public void ARowInTheRetrySet_IsWalkedEvenThoughItsMemorySnapshotIsCurrent()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            repo.ThrowOnSnapshotWriteListingIds.Add(1);

            var first = MarketManager.BackfillSnapshots(100, false);

            Assert.AreEqual(1, first.WriteFailed);

            // The write failed, so the DATABASE row is still version 0 - but the MEMORY snapshot was
            // updated to the current version before the write was attempted, and selection reads
            // memory. A version gate applied to the retry set would therefore skip the one row that
            // most needs resubmitting, and it would do so silently.
            Assert.AreEqual(MarketSnapshot.CurrentVersion, MarketManager.GetListing(1).Snapshot.SnapshotVersion,
                            "the fixture only means anything if memory is already current");

            repo.ThrowOnSnapshotWriteListingIds.Clear();
            repo.SnapshotWrites.Clear();

            var second = MarketManager.BackfillSnapshots(100, false);

            CollectionAssert.Contains(WrittenIds(), 1u,
                                      "a remembered write failure outranks the version gate");
            Assert.AreEqual(1, second.RetriedAfterWriteFailure);
        }

        // ---- the automatic pass ----
        //
        // Everything here drives MarketManager.RunAutomaticBackfill directly. The production entry
        // point starts a background thread and waits for vault stores to load; that wait is what a
        // test must not inherit, so the thread and the sweep are separate methods.

        private static void SetAutoBackfill(bool on)
            => Assert.IsTrue(PropertyManager.ModifyBool(MarketManager.AutoBackfillTunable, on),
                             "market_snapshot_backfill_on_start is missing from DefaultBooleanProperties");

        [TestMethod]
        public void TheAutomaticPass_RepairsLegacyListingsAndCompletesTheSweep()
        {
            SeedManyListings(3);
            Boot();

            var outcome = MarketManager.RunAutomaticBackfill();

            Assert.IsTrue(outcome.Ran, $"it declined to run: {outcome.SkipReason}");
            Assert.IsTrue(outcome.Completed, "one page holds this market, so the sweep must finish");
            Assert.AreEqual(3, outcome.Rewritten);
            Assert.AreEqual(0, outcome.StaleActive, "and nothing may be left out of date behind it");
        }

        [TestMethod]
        public void TheAutomaticPass_IsANoOpWhenNothingIsStale()
        {
            var item = SeedStoredItem(SellerAccount);
            SeedCurrentVersionListingRow(1, SellerAccount, item.Guid.Full, StoredWcid, item.Name);

            Boot();

            var outcome = MarketManager.RunAutomaticBackfill();

            // The property that makes this safe to run on EVERY world start: a restart with nothing
            // out of date must not re-project the market, must not touch a vault, and must not write.
            Assert.IsTrue(outcome.Ran);
            Assert.AreEqual(0, outcome.Considered, "an up-to-date market costs one index scan and nothing else");
            Assert.AreEqual(0, outcome.Rewritten);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
            Assert.AreEqual(0, store.GetEntriesCalls, "and no seller's vault may be read at all");
        }

        [TestMethod]
        public void TheAutomaticPass_DoesNotRunWithTheTunableOff()
        {
            SeedManyListings(2);
            Boot();

            SetAutoBackfill(false);

            try
            {
                var outcome = MarketManager.RunAutomaticBackfill();

                Assert.IsFalse(outcome.Ran, "the kill switch must actually stop it");
                Assert.IsNotNull(outcome.SkipReason, "and must say why, or an operator cannot tell it from an empty market");
                Assert.AreEqual(0, outcome.Considered);
                Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
            }
            finally
            {
                SetAutoBackfill(true);
            }
        }

        [TestMethod]
        public void TheAutomaticPass_DoesNotRunWithoutAnIndex()
        {
            // Never booted. An unattended write against an index that was never built is the one
            // thing this must never do - Initialize leaves the index empty when the listing read
            // failed, and a sweep there would be a sweep over a market it cannot see.
            var outcome = MarketManager.RunAutomaticBackfill();

            Assert.IsFalse(outcome.Ran);
            Assert.IsNotNull(outcome.SkipReason);
            Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
        }

        [TestMethod]
        public void TheAutomaticPass_TerminatesWhenARowAlwaysFails()
        {
            SeedManyListings(4);
            Boot();

            // Listing 1 can never be written, so it stays in the retry set and is pulled back into
            // EVERY subsequent page from behind the cursor, consuming half of each one. A sweep
            // driven by "is anything still unrepaired" would never end; this one is driven by the
            // cursor reaching the end of the market, which a permanent failure cannot prevent.
            repo.ThrowOnSnapshotWriteListingIds.Add(1);

            // Page size 2, so the sweep genuinely PAGES and the retry genuinely competes with
            // forward progress. At the production page size this market fits in one pass and the
            // loop under test would never run twice.
            var outcome = MarketManager.RunAutomaticBackfill(2);

            Assert.IsTrue(outcome.Ran);
            Assert.IsTrue(outcome.Completed, "the sweep has to REACH THE END, not merely stop");
            Assert.IsFalse(outcome.StoppedAtBound, "and it must get there on its own, not by hitting a bound");
            Assert.IsTrue(outcome.Passes <= MarketManager.AutoBackfillMaxPasses,
                          "the sweep must be bounded, not merely expected to converge");
            Assert.IsTrue(outcome.WriteFailed >= 1, "the failing row is reported, every time it is retried");

            // The other three were repaired around it: one bad row must not cost the sweep the rest
            // of the market.
            foreach (var id in new uint[] { 2, 3, 4 })
                CollectionAssert.Contains(WrittenIds(), id, $"listing {id} should have been repaired");
        }

        [TestMethod]
        public void TheAutomaticPass_SwallowsAThrowRatherThanFailingWorldStart()
        {
            SeedManyListings(2);
            Boot();

            // A repository that throws OUT of the call rather than reporting per-row failures. This
            // runs on a background thread during world start; letting it escape would take the
            // start-up path down over a cosmetic repair.
            repo.ThrowFromUpdateListingSnapshots = true;

            var outcome = MarketManager.RunAutomaticBackfill();

            Assert.IsTrue(outcome.Faulted, "a throw has to be recorded, not silently rendered as a clean sweep");
            Assert.IsFalse(outcome.Completed);
        }

        [TestMethod]
        public void TheAutomaticPass_DoesNotRunWhenTheKillSwitchCannotBeRead()
        {
            SeedManyListings(2);
            Boot();

            // An uncached PropertyManager read opens a ShardDbContext and can throw. The decision
            // this pins is that a switch we could not READ is treated as a switch that might be OFF:
            // the cost of declining is one skipped repair that the next world start picks up, and
            // the cost of assuming is an unattended write an operator believed they had stopped.
            MarketManager.AutoBackfillTunableSource = () => throw new InvalidOperationException("no shard");

            try
            {
                var outcome = MarketManager.RunAutomaticBackfill();

                Assert.IsFalse(outcome.Ran, "an unreadable kill switch must stop the sweep, not be assumed on");
                Assert.IsNotNull(outcome.SkipReason);
                Assert.AreEqual(0, outcome.Considered);
                Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls);
            }
            finally
            {
                MarketManager.AutoBackfillTunableSource = null;
            }
        }

        [TestMethod]
        public void StoppingTheAutomaticPass_WaitsForAPageAlreadyInFlight()
        {
            SeedManyListings(2);
            Boot();

            var hold = new ManualResetEventSlim(false);
            var stopReturned = false;
            Task stopping = null;

            try
            {
                // Park the sweep INSIDE the vault read, which is after it has taken its page and
                // before it writes anything.
                store.StoreHold = hold;

                MarketManager.StartAutomaticBackfill(TimeSpan.Zero);

                Assert.IsTrue(store.DelayEntered.Task.Wait(TimeSpan.FromSeconds(10)),
                              "the sweep never reached the vault read");

                stopping = Task.Run(() =>
                {
                    MarketManager.StopAutomaticBackfill();
                    Volatile.Write(ref stopReturned, true);
                });

                // The whole finding: Shutdown calls StopAutomaticBackfill and Program.cs calls
                // DatabaseManager.Stop moments later. A Stop that only sets a flag returns here
                // immediately, while this page still holds rows it is about to write through a
                // repository that resolves DatabaseManager.Shard AT CALL TIME - so the write lands
                // against a shard worker being torn down.
                Thread.Sleep(500);

                Assert.AreEqual(0, repo.UpdateListingSnapshotsCalls, "fixture: the page must not have written yet");
                Assert.IsFalse(Volatile.Read(ref stopReturned),
                               "Stop must not return while a page is still able to reach the shard");
            }
            finally
            {
                store.StoreHold = null;
                hold.Set();

                if (stopping != null)
                    Assert.IsTrue(stopping.Wait(TimeSpan.FromSeconds(15)), "Stop never returned at all");

                hold.Dispose();
            }

            Assert.IsTrue(Volatile.Read(ref stopReturned), "and it must return once the page is done");
        }

        [TestMethod]
        public void APageWhoseListingsCloseUnderIt_StillReportsTheGroundItCovered()
        {
            var first = SeedStoredItem(SellerAccount);
            var second = SeedStoredItem(SellerAccount);

            SeedListingRow(1, SellerAccount, first.Guid.Full, StoredWcid, first.Name);
            SeedListingRow(2, SellerAccount, second.Guid.Full, StoredWcid, second.Name);

            Boot();

            var hold = new ManualResetEventSlim(false);
            MarketBackfillReport report;

            try
            {
                store.StoreHold = hold;

                var pass = Task.Run(() => MarketManager.BackfillSnapshots(2, false));

                Assert.IsTrue(store.DelayEntered.Task.Wait(TimeSpan.FromSeconds(10)),
                              "the pass never reached the vault read");

                // Both rows go away underneath a page that had already selected them.
                Assert.IsTrue(MarketManager.Delist(SellerActor, 1, MarketChannel.InGame).Ok);
                Assert.IsTrue(MarketManager.Delist(SellerActor, 2, MarketChannel.InGame).Ok);

                store.StoreHold = null;
                hold.Set();

                Assert.IsTrue(pass.Wait(TimeSpan.FromSeconds(10)), "the pass never finished");

                report = pass.Result;
            }
            finally
            {
                store.StoreHold = null;
                hold.Set();
                hold.Dispose();
            }

            // Considered UNDERCOUNTS the ground a page covered, because a row that closed before its
            // own turn is never counted. The cursor still advanced past it, so a progress check
            // written against Considered reads a page like this as a stuck sweep and aborts with an
            // ERROR - during exactly the busy market this is meant to run on.
            Assert.AreEqual(2, report.ForwardSelected, "the page took two rows from ahead of the cursor");
            Assert.IsTrue(report.Considered < report.ForwardSelected,
                          "fixture: the closes must actually have beaten the walk to them");
            Assert.AreEqual(2, report.ClosedMidPass);
        }
    }
}
