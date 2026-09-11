using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;
using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The ONLY place market listing/purchase logic lives (DESIGN 4.1); /market and the HTTP API both call this.
    /// THE CHANGE SEQUENCE RESTARTS AT ZERO IN MEMORY, so <see cref="FeedGeneration"/> tells a stale client to resync from 0; feeds are served from the index, never the database.
    /// ONE LOCK covers only in-memory work - the database's two UNIQUE keys, not this lock, enforce "one Active listing per item".
    /// </summary>
    public static partial class MarketManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>DESIGN section 6: cursor paged, max 500 rows.</summary>
        public const int MaxFeedRows = 500;

        /// <summary>DESIGN section 8: /market search is capped at 20 lines.</summary>
        public const int MaxSearchRows = 20;

        /// <summary>History depth preloaded into the in-memory feed at boot; equal to the DAO's row ceiling.</summary>
        private const int ShardTransactionPreload = 1000;

        private static readonly object indexLock = new object();

        private static readonly Dictionary<uint, MarketListing> listings = new Dictionary<uint, MarketListing>();
        private static readonly Dictionary<uint, MarketTransaction> transactions = new Dictionary<uint, MarketTransaction>();

        /// <summary>
        /// Listing ids whose sale is mid-flight, refcounted for concurrent buyers of one listing. The
        /// market's own take goes through the same vault withdraw the auto-delist hook watches, so
        /// without this a sale delists the listing it is selling. Guarded by indexLock.
        /// </summary>
        private static readonly Dictionary<uint, int> salesInFlight = new Dictionary<uint, int>();

        private static IMarketItemStore itemStore;
        private static IMarketWallet wallet;
        private static IMarketRepository repository;

        private static long changeSequence;
        private static volatile bool enabled;

        /// <summary>
        /// False until Initialize has rebuilt the index successfully, AND false whenever the
        /// market_enabled tunable is off. Every player-facing entry point refuses while false.
        /// </summary>
        public static bool Enabled => enabled && PropertyManager.GetBool("market_enabled", false).Item;

        /// <summary>Minted once per process; a client whose stored generation differs must resync from cursor 0.</summary>
        public static string FeedGeneration { get; private set; } = Guid.NewGuid().ToString("N");

        public static long ChangeSequence
        {
            get { lock (indexLock) return changeSequence; }
        }

        /// <summary>Production wiring. Called from Program.cs only when Market.Enabled is true.</summary>
        public static void Initialize()
        {
            // Nothing calls this until Program.cs wires it in Task 7.
            var marketRepository = new ShardMarketRepository();

            Initialize(new VaultMarketItemStore(), new BankMarketWallet(), marketRepository);

            // Started HERE and not inside the three-seam Initialize below, which every unit test
            // drives: a background writer there would race a test's assertions against its own flush
            // and would read PropertyManager in a process with no shard.
            MarketRejectionLog.Start(marketRepository);

            // Same rule, same reason: a background thread here would race every unit test's own
            // assertions. It waits for the world to settle and for seller vaults to load before it
            // touches anything - see StartAutomaticBackfill - so this is a start, not a pass.
            StartAutomaticBackfill();

            AccountVaultStore.PreWithdrawHook = OnVaultWithdraw;
            AccountVaultStore.IsListedHook = IsListed;
        }

        /// <summary>
        /// Wires the three seams and rebuilds the index. Leaves <see cref="Enabled"/> FALSE if the
        /// listing read failed - an empty market would let sellers re-list what is already listed.
        /// </summary>
        public static void Initialize(IMarketItemStore store, IMarketWallet marketWallet, IMarketRepository marketRepository)
        {
            lock (indexLock)
            {
                itemStore = store;
                wallet = marketWallet;
                repository = marketRepository;

                listings.Clear();
                transactions.Clear();
                salesInFlight.Clear();
                ledgerSnapshots.Clear();
                buyOrders.Clear();
                placementsInFlight.Clear();
                buyOrdersReadFailed = false;
                changeSequence = 0;
                FeedGeneration = Guid.NewGuid().ToString("N");
                enabled = false;
            }

            var rows = marketRepository.GetAllListings();

            if (rows == null)
            {
                log.Error("[MARKET] could not read market_listing; the market stays DISABLED rather than serving an empty index. Restart once the shard is reachable.");
                return;
            }

            var history = marketRepository.GetTransactions(ShardTransactionPreload);

            // A failed history read does NOT block boot - it costs a short history FEED, not custody or
            // money; interrupted purchases recover separately via GetPendingTransactions (skips on null).
            if (history == null)
                log.Error("[MARKET] could not read market_transaction; the market boots with an EMPTY history feed. Pending-transaction recovery is unaffected. Restart to backfill the feed.");

            // Takes indexLock itself, so it runs BEFORE the block below rather than inside it. A failed
            // order read does NOT block boot (WANTED-DESIGN 6.5); it refuses place and fill instead.
            LoadOrders(marketRepository);

            lock (indexLock)
            {
                foreach (var row in rows.OrderBy(r => r.Id))
                {
                    var listing = FromRow(row);
                    listing.Seq = ++changeSequence;
                    listings[listing.Id] = listing;
                }

                if (history != null)
                {
                    foreach (var row in history.OrderBy(r => r.Id))
                    {
                        var tx = FromRow(row);
                        tx.Seq = ++changeSequence;
                        transactions[tx.Id] = tx;
                    }
                }

                enabled = true;
            }

            log.Info($"[MARKET] index rebuilt: {rows.Count} listing(s), {history?.Count ?? 0} history row(s), generation {FeedGeneration}.");
        }

        /// <summary>
        /// The repository, for the /marketadmin investigation surface ONLY. Null before Initialize.
        ///
        /// Deliberately NOT gated on <see cref="Enabled"/>: the kill switch being off is one of the
        /// states an operator most needs to investigate, and an investigation tool that goes blind
        /// exactly when the market is switched off is the wrong shape.
        /// </summary>
        internal static IMarketRepository AdminRepository
        {
            get { lock (indexLock) return repository; }
        }

        public static void Shutdown()
        {
            // Outside the lock, and first: Stop joins the writer thread and drains it, and neither
            // should happen while this thread holds the index lock.
            MarketRejectionLog.Stop();

            // JOINED, bounded, exactly like MarketRejectionLog.Stop above and for the same reason: a
            // page already in flight can still write, and Program.cs stops DatabaseManager moments
            // after this returns.
            StopAutomaticBackfill();

            lock (indexLock)
            {
                // A stopped or disabled market must not keep intercepting vault withdraws.
                AccountVaultStore.PreWithdrawHook = null;

                // Cleared with it, and for the same reason: the index below is about to be emptied, so
                // leaving the hook installed would answer "nothing is listed" from an index that no
                // longer knows anything rather than from one that does.
                AccountVaultStore.IsListedHook = null;

                enabled = false;
                listings.Clear();
                transactions.Clear();
                salesInFlight.Clear();
                ledgerSnapshots.Clear();
                buyOrders.Clear();
                placementsInFlight.Clear();
                changeSequence = 0;
                itemStore = null;
                wallet = null;
                repository = null;
            }
        }

        // ---- listing ----

        /// <summary>
        /// Lists an item or a number of ledger units at a per-unit price. Only the ACCOUNT OWNER may
        /// list (DESIGN 5.3); the actor's account id IS the vault's, so there is no grant check here.
        ///
        /// <paramref name="channel"/> is required rather than defaulted so a new caller cannot silently
        /// record its refusals as web traffic; it is carried only into
        /// <see cref="MarketRejectionLog"/>, which every refusal below writes to (DESIGN 5.5). Those
        /// Record calls are a bounded ENQUEUE and nothing else - see MarketRejectionLog's threading
        /// contract for why this path must stay allocation-cheap and DB-free.
        /// </summary>
        public static MarketResult<MarketListing> List(MarketActor actor, uint? itemGuid, uint wcid, int count, long priceMmd,
                                                       MarketChannel channel)
        {
            // NOT recorded: the kill switch being off is an operator state, not a player event.
            if (!Enabled)
                return MarketResult<MarketListing>.Fail(MarketError.Disabled);

            if (actor.AccountId == 0 || actor.CharacterGuid == 0)
                return RefuseList(MarketError.NotOwner, channel, actor, itemGuid, wcid, count, priceMmd);

            // Zero is a giveaway, not a refusal. Negative is still meaningless, and above MaxPriceMmd
            // is a price no bank can be credited with, so the sale would fail at step 5 with the goods
            // already moved. Every caller that reaches here has SUPPLIED a price: the API layer
            // refuses an absent one before this point, because an absent price deserializing to zero
            // would be an accidental giveaway rather than a chosen one.
            if (priceMmd < 0 || priceMmd > MaxPriceMmd)
                return RefuseList(MarketError.InvalidPrice, channel, actor, itemGuid, wcid, count, priceMmd);

            // Checked separately from the bounds above so the kill switch cannot be confused with the
            // hard rule: negative is always refused, zero is refused only while giveaways are off.
            if (priceMmd == 0 && !PropertyManager.GetBool("market_allow_free_listings").Item)
                return RefuseList(MarketError.InvalidPrice, channel, actor, itemGuid, wcid, count, priceMmd,
                                  "free listings are disabled by market_allow_free_listings");

            // The operator's soft cap, under the hard one above. 0 or less leaves only the hard one.
            var maxPrice = PropertyManager.GetLong("market_max_price_mmd").Item;

            if (maxPrice > 0 && priceMmd > maxPrice)
                return RefuseList(MarketError.InvalidPrice, channel, actor, itemGuid, wcid, count, priceMmd,
                                  $"over the market_max_price_mmd soft cap of {maxPrice}");

            if (count < 1)
                return RefuseList(MarketError.CountUnavailable, channel, actor, itemGuid, wcid, count, priceMmd);

            if (!itemStore.IsReady(actor.AccountId, out _))
                return RefuseList(MarketError.VaultUnavailable, channel, actor, itemGuid, wcid, count, priceMmd);

            var maxListings = PropertyManager.GetLong("market_max_listings_per_account").Item;

            if (GetActiveListingsForAccount(actor.AccountId).Count >= Math.Max(1, maxListings))
                return RefuseList(MarketError.ListingLimit, channel, actor, itemGuid, wcid, count, priceMmd,
                                  "at the market_max_listings_per_account cap");

            var entry = FindEntry(actor.AccountId, itemGuid, wcid);

            if (entry == null)
                return RefuseList(MarketError.ItemNotFound, channel, actor, itemGuid, wcid, count, priceMmd);

            // WHY THIS RUNS AFTER FindEntry, and not before it as a bare "itemGuid means count 1":
            // "a stored item" covers two different vault rows and only one of them is indivisible.
            //
            // A LONE stored biota really is one object. AccountVaultStore.TryWithdraw refuses any
            // amount but its whole count, so a listing above 1 would promise a split the vault will
            // not perform - and this refusal is the FAIL-SAFE direction, so it stays unconditional
            // regardless of what a form posts.
            //
            // A GROUP row is N separate WHOLE biotas drawn on one panel line. Nothing is split by
            // selling k of them: WithdrawGroup takes exactly k whole members and refuses more than the
            // row holds. The entry.Count check immediately below is what bounds a group listing to its
            // member count, so no separate ceiling is needed here.
            if (itemGuid != null && !entry.IsGroup && count != 1)
                return RefuseList(MarketError.CountUnavailable, channel, actor, itemGuid, wcid, count, priceMmd,
                                  "a lone stored item cannot be split");

            if (entry.Count < count)
                return RefuseList(MarketError.CountUnavailable, channel, actor, itemGuid, wcid, count, priceMmd,
                                  $"the vault holds {entry.Count}");

            // Cheap in-memory refusal so the ordinary double-list never reaches the database; the
            // UNIQUE keys are still the authority. The refusal itself is recorded OUTSIDE the lock so
            // the index lock never covers an audit enqueue.
            bool alreadyListed;

            lock (indexLock)
                alreadyListed = ActiveListingFor(actor.AccountId, itemGuid, wcid) != null;

            if (alreadyListed)
                return RefuseList(MarketError.AlreadyListed, channel, actor, itemGuid, wcid, count, priceMmd);

            var snapshot = entry.IsLedger
                ? MarketSnapshot.FromLedgerProbe(entry.WorldObject, wcid)
                : MarketSnapshot.FromItem(entry.WorldObject);

            // A ledger entry carries no WorldObject, so the projection above falls back to the wcid
            // alone; the vault store can build a real probe of it.
            if (entry.IsLedger && itemStore is VaultMarketItemStore vaultStore)
                snapshot = vaultStore.DescribeLedger(actor.AccountId, wcid);

            var row = new ShardMarketListing
            {
                SellerAccountId = actor.AccountId,
                SellerCharacterGuid = actor.CharacterGuid,
                SellerCharacterName = actor.Name,
                ItemGuid = itemGuid,
                ActiveItemGuid = itemGuid,
                ActiveLedgerWcid = itemGuid == null ? (uint?)wcid : null,
                Wcid = wcid,
                Count = count,
                PriceMmd = priceMmd,
                Status = (int)MarketListingStatus.Active,
                CreatedAt = DateTime.UtcNow,
                ClosedAt = null,
                SnapshotJson = MarketSnapshot.Serialize(snapshot),
            };

            if (!repository.AddListing(row))
            {
                // A UNIQUE refusal or a shard failure. already_listed is the safe report for both:
                // nothing was written either way and the seller retries against the real state.
                log.Warn($"[MARKET] AddListing refused or failed for account {actor.AccountId}, wcid {wcid}, item {itemGuid?.ToString() ?? "ledger"}.");

                // The ONE synthetic reason code. The player was told already_listed, but that is a
                // guess covering two very different causes, so the audit row must not repeat it -
                // an investigator reading already_listed here would conclude the seller double-listed.
                MarketRejectionLog.Record(MarketRejectOperation.List, MarketRejectionLog.RepositoryRefusedCode, channel,
                                          actor, null, itemGuid, wcid, count, priceMmd,
                                          "AddListing refused or failed; the player was told already_listed");

                return MarketResult<MarketListing>.Fail(MarketError.AlreadyListed);
            }

            var listing = FromRow(row);
            listing.Snapshot = snapshot;

            lock (indexLock)
            {
                listing.Seq = ++changeSequence;
                listings[listing.Id] = listing;
            }

            return MarketResult<MarketListing>.Success(listing);
        }

        /// <summary>
        /// One refused List attempt, enqueued and turned into the failure the caller returns. Exists so
        /// each refusal branch above stays one line and cannot record a code that disagrees with the
        /// code it returns - the same MarketError produces both.
        /// </summary>
        private static MarketResult<MarketListing> RefuseList(MarketError error, MarketChannel channel, MarketActor actor,
                                                              uint? itemGuid, uint wcid, int count, long priceMmd,
                                                              string detail = null)
        {
            MarketRejectionLog.Record(MarketRejectOperation.List, error, channel, actor, null, itemGuid, wcid, count, priceMmd, detail);

            return MarketResult<MarketListing>.Fail(error);
        }

        /// <summary>The Delist counterpart of <see cref="RefuseList"/>.</summary>
        private static MarketResult<MarketListing> RefuseDelist(MarketError error, MarketChannel channel, MarketActor actor,
                                                                uint listingId, MarketListing listing, string detail = null)
        {
            MarketRejectionLog.Record(MarketRejectOperation.Delist, error, channel, actor, listingId,
                                      listing?.ItemGuid, listing?.Wcid ?? 0, listing?.Count ?? 0, listing?.PriceMmd ?? 0, detail);

            return MarketResult<MarketListing>.Fail(error);
        }

        /// <summary>
        /// Owner-only delist. An already-closed listing is not an error worth two codes.
        ///
        /// <paramref name="channel"/> is required for the same reason it is on <see cref="List"/>; the
        /// web route resolves no character, so those rows carry character_Guid 0.
        /// </summary>
        public static MarketResult<MarketListing> Delist(MarketActor actor, uint listingId, MarketChannel channel)
        {
            // NOT recorded, as in List: the kill switch is operator state, not a player event.
            if (!Enabled)
                return MarketResult<MarketListing>.Fail(MarketError.Disabled);

            MarketListing listing;

            lock (indexLock)
                listings.TryGetValue(listingId, out listing);

            if (listing == null)
                return RefuseDelist(MarketError.ListingNotActive, channel, actor, listingId, null, "no such listing in the index");

            if (listing.SellerAccountId != actor.AccountId || actor.AccountId == 0)
                return RefuseDelist(MarketError.NotOwner, channel, actor, listingId, listing);

            if (listing.Status != MarketListingStatus.Active)
                return RefuseDelist(MarketError.ListingNotActive, channel, actor, listingId, listing,
                                    $"already {listing.Status.ToString().ToLowerInvariant()}");

            if (Close(listing, MarketListingStatus.Delisted))
                return MarketResult<MarketListing>.Success(GetListing(listingId));

            // Close already logged the shard failure; this row is what makes the player's report of it
            // findable, since nothing else records that this seller tried and could not.
            return RefuseDelist(MarketError.ServerError, channel, actor, listingId, listing, "the close write failed");
        }

        /// <summary>
        /// The single close path: terminal status, both active_* keys CLEARED so the unique
        /// constraints are freed, closed_At stamped, and the sequence bumped so the feed carries it.
        /// </summary>
        private static bool Close(MarketListing listing, MarketListingStatus status)
        {
            var row = ToRow(listing);
            row.Status = (int)status;
            row.ActiveItemGuid = null;
            row.ActiveLedgerWcid = null;
            row.ClosedAt = DateTime.UtcNow;

            if (!repository.UpdateListing(row))
            {
                log.Error($"[MARKET] could not close listing {listing.Id} as {status}. It stays Active in memory and in the database, which is the safe direction: the seller can retry.");
                return false;
            }

            lock (indexLock)
            {
                if (listings.TryGetValue(listing.Id, out var live))
                {
                    live.Status = status;
                    live.ClosedAt = row.ClosedAt;
                    live.Seq = ++changeSequence;
                }
            }

            return true;
        }

        /// <summary>Decrements a partially sold listing, closing it as Sold at zero. Called by the purchase path.</summary>
        internal static bool ApplySale(MarketListing listing, int soldCount)
        {
            var remaining = listing.Count - soldCount;

            if (remaining <= 0)
                return Close(listing, MarketListingStatus.Sold);

            var row = ToRow(listing);
            row.Count = remaining;

            if (!repository.UpdateListing(row))
            {
                log.Error($"[MARKET] could not decrement listing {listing.Id} after selling {soldCount}. The listing still reads its pre-sale count; the next purchase will be refused by the store, not by this number.");
                return false;
            }

            lock (indexLock)
            {
                if (listings.TryGetValue(listing.Id, out var live))
                {
                    live.Count = remaining;
                    live.Seq = ++changeSequence;
                }
            }

            return true;
        }

        // ---- delist and invalidation hooks ----

        /// <summary>
        /// Claims a listing for a purchase in progress, exempting it from the auto-delist hook. ALWAYS
        /// paired with <see cref="EndSale"/> in a finally. Keyed on the listing, not on the calling
        /// thread: AccountVaultStore.Drain can run one thread's queued work on another's.
        /// </summary>
        internal static void BeginSale(uint listingId)
        {
            lock (indexLock)
                salesInFlight[listingId] = salesInFlight.TryGetValue(listingId, out var claims) ? claims + 1 : 1;
        }

        internal static void EndSale(uint listingId)
        {
            lock (indexLock)
            {
                if (!salesInFlight.TryGetValue(listingId, out var claims))
                    return;

                if (claims <= 1)
                    salesInFlight.Remove(listingId);
                else
                    salesInFlight[listingId] = claims - 1;
            }
        }

        /// <summary>
        /// The pre-withdraw hook (DESIGN 5.3), run on the vault's own mutation queue so a withdraw and
        /// a buy on one account cannot interleave. Withdrawing a listed item delists it; drawing a
        /// ledger stack below its listed count does too. NEVER throws - it runs inside TryWithdraw.
        /// </summary>
        /// <summary>
        /// Destroys one of the actor's own vault holdings by feeding it to their barrel
        /// (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6.3).
        ///
        /// A VAULT operation reached through the market's door: the market host is the only HTTP
        /// surface the web app speaks, so the route lives there, but nothing about a barreling is a
        /// market rule. The one market fact involved is the listing check, and it is checked HERE, in
        /// the index that owns the answer, so the caller gets item_listed rather than the generic
        /// vault refusal the store would produce on its own.
        ///
        /// THE LISTING CHECK IS NOT THE GUARANTEE - AccountVaultStore.TryBarrel checks again through
        /// its own hook, inside the serialized region, which is where a listing created between this
        /// check and the take would be caught. This one exists to produce the right error code.
        /// </summary>
        public static MarketResult<bool> Barrel(MarketActor actor, uint? itemGuid, uint wcid, int count)
        {
            if (!Enabled)
                return MarketResult<bool>.Fail(MarketError.Disabled);

            if (actor.AccountId == 0)
                return MarketResult<bool>.Fail(MarketError.NotOwner);

            if (count < 1)
                return MarketResult<bool>.Fail(MarketError.CountUnavailable);

            if (!itemStore.IsReady(actor.AccountId, out _))
                return MarketResult<bool>.Fail(MarketError.VaultUnavailable);

            bool alreadyListed;

            lock (indexLock)
                alreadyListed = ActiveListingFor(actor.AccountId, itemGuid, wcid) != null;

            if (alreadyListed)
                return MarketResult<bool>.Fail(MarketError.ItemListed);

            var ok = false;
            var error = MarketError.ServerError;

            if (!itemStore.RunSerialized(actor.AccountId, () => ok = itemStore.TryBarrel(actor.AccountId, itemGuid, wcid, count, actor, out error)))
                return MarketResult<bool>.Fail(MarketError.VaultUnavailable);

            return ok ? MarketResult<bool>.Success(true) : MarketResult<bool>.Fail(error);
        }

        /// <summary>
        /// Whether this vault row carries an ACTIVE listing right now. Wired into
        /// <see cref="AccountVaultStore.IsListedHook"/> on startup, beside
        /// <see cref="OnVaultWithdraw"/>, so the vault can refuse to barrel something that is for
        /// sale without naming a market type.
        ///
        /// Answers from the in-memory index, which is the same authority
        /// <see cref="MarketManager.List"/>'s double-list refusal uses. DELIBERATELY NOT gated on
        /// <see cref="Enabled"/>: the index is emptied by <see cref="Shutdown"/> and the hook is
        /// cleared with it, so an unindexed process answers through a null hook rather than through a
        /// false here, and the two cases stay distinguishable.
        ///
        /// NEVER throws - it runs inside a vault mutation.
        /// </summary>
        public static bool IsListed(uint accountId, uint? itemGuid, uint wcid)
        {
            lock (indexLock)
                return ActiveListingFor(accountId, itemGuid, wcid) != null;
        }

        public static void OnVaultWithdraw(uint accountId, uint? itemGuid, uint wcid, int amount)
        {
            try
            {
                // The raw field, not Enabled: invalidation must keep running while the
                // market_enabled kill switch is off, or re-enabling resurrects unbacked listings.
                if (!enabled)
                    return;

                MarketListing listing;
                bool ownTake;

                lock (indexLock)
                {
                    listing = ActiveListingFor(accountId, itemGuid, wcid);
                    ownTake = listing != null && salesInFlight.ContainsKey(listing.Id);
                }

                if (listing == null)
                    return;

                // The market's own take for a sale in flight. The purchase's own step 6 closes or
                // decrements this listing; delisting here would destroy a partial sale's remainder.
                if (ownTake)
                    return;

                if (itemGuid != null)
                {
                    Close(listing, MarketListingStatus.Delisted);
                    return;
                }

                var heldAfter = HeldLedgerUnits(accountId, wcid) - amount;

                if (heldAfter < listing.Count)
                    Close(listing, MarketListingStatus.Delisted);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] the pre-withdraw hook threw for account {accountId}, wcid {wcid}: {ex.GetFullMessage()}. The withdraw proceeds; the listing may be stale until the next invalidation pass.");
            }
        }

        /// <summary>
        /// Marks every Active listing on this account whose backing is gone (DESIGN 5.4). Skipped when
        /// the store is not ready: "not ready" and "holds nothing" are different facts, and acting on
        /// the first as the second invalidates a whole account over a transient shard hiccup.
        /// </summary>
        public static void InvalidateForAccount(uint accountId)
        {
            // The raw field, for the same reason as OnVaultWithdraw above.
            if (!enabled)
                return;

            if (!itemStore.IsReady(accountId, out _))
                return;

            List<MarketListing> candidates;

            lock (indexLock)
                candidates = listings.Values
                    .Where(l => l.SellerAccountId == accountId && l.Status == MarketListingStatus.Active)
                    .ToList();

            foreach (var listing in candidates)
            {
                if (itemStore.Holds(accountId, listing.ItemGuid, listing.Wcid, listing.Count))
                    continue;

                log.Info($"[MARKET] invalidating listing {listing.Id}: account {accountId} no longer holds {listing.Count} x wcid {listing.Wcid}.");
                Close(listing, MarketListingStatus.Invalidated);
            }
        }

        // ---- reads ----

        public static MarketListing GetListing(uint listingId)
        {
            lock (indexLock)
                return listings.TryGetValue(listingId, out var listing) ? listing : null;
        }

        /// <summary>
        /// Index sizes for /marketadmin status. Counts only, deliberately: the alternative is asking
        /// for a feed page and taking its Count, which copies up to 500 listings on a world thread to
        /// print one number.
        /// </summary>
        internal static (int Listings, int ActiveListings, int Transactions) IndexCounts()
        {
            lock (indexLock)
                return (listings.Count,
                        listings.Values.Count(l => l.Status == MarketListingStatus.Active),
                        transactions.Count);
        }

        /// <summary>
        /// Rows whose Seq is above <paramref name="sinceSeq"/>, oldest first, capped at
        /// <paramref name="max"/> and <see cref="MaxFeedRows"/>. nextSeq is the LAST ROW RETURNED, not
        /// the head: returning the head on a truncated page silently skips the rest forever.
        /// </summary>
        public static IReadOnlyList<MarketListing> GetListingChanges(long sinceSeq, int max, out long nextSeq)
        {
            if (max < 1 || max > MaxFeedRows)
                max = MaxFeedRows;

            lock (indexLock)
            {
                var page = listings.Values
                    .Where(l => l.Seq > sinceSeq)
                    .OrderBy(l => l.Seq)
                    .Take(max)
                    .Select(Copy)
                    .ToList();

                nextSeq = page.Count > 0 ? page[page.Count - 1].Seq : changeSequence;

                return page;
            }
        }

        /// <summary>History feed, same cursor contract as <see cref="GetListingChanges"/>.</summary>
        public static IReadOnlyList<MarketTransaction> GetHistoryChanges(long sinceSeq, int max, out long nextSeq)
        {
            if (max < 1 || max > MaxFeedRows)
                max = MaxFeedRows;

            lock (indexLock)
            {
                var page = transactions.Values
                    .Where(t => t.Seq > sinceSeq)
                    .OrderBy(t => t.Seq)
                    .Take(max)
                    .Select(Copy)
                    .ToList();

                nextSeq = page.Count > 0 ? page[page.Count - 1].Seq : changeSequence;

                return page;
            }
        }

        public static IReadOnlyList<MarketListing> GetActiveListingsForAccount(uint accountId)
        {
            lock (indexLock)
                return listings.Values
                    .Where(l => l.SellerAccountId == accountId && l.Status == MarketListingStatus.Active)
                    .OrderBy(l => l.Id)
                    .Select(Copy)
                    .ToList();
        }

        /// <summary>
        /// Name-contains over the index, for /market search (DESIGN section 8). The web app searches
        /// its own index off its own feed, so this cap is a chat-window cap, not a scaling one.
        /// </summary>
        public static IReadOnlyList<MarketListing> SearchByName(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<MarketListing>();

            if (max < 1 || max > MaxSearchRows)
                max = MaxSearchRows;

            lock (indexLock)
                return listings.Values
                    .Where(l => l.Status == MarketListingStatus.Active
                                && l.Snapshot?.Name != null
                                && l.Snapshot.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(l => l.PriceMmd)
                    .ThenBy(l => l.Id)
                    .Take(max)
                    .Select(Copy)
                    .ToList();
        }

        /// <summary>
        /// Weenie oracle for ledger rows, which carry no WorldObject. Cached lookup, because this runs
        /// once per vault row. Settable so a test needs no world database.
        /// </summary>
        public static Func<uint, Weenie> WeenieLookup { get; set; }
            = wcid => DatabaseManager.World.GetCachedWeenie(wcid);

        /// <summary>
        /// Ledger snapshots by wcid, each held with the weenie INSTANCE it was projected from. Every
        /// ledger row of a wcid projects identically and a vault is dominated by them, so the projection
        /// runs once rather than once per row. Read-only after publication; cleared by Initialize and Shutdown.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (Weenie Source, ListingSnapshot Snapshot)> ledgerSnapshots
            = new System.Collections.Concurrent.ConcurrentDictionary<uint, (Weenie, ListingSnapshot)>();

        /// <summary>An unreadable row degrades to "Item wcid" rather than failing the whole vault view.</summary>
        private static ListingSnapshot FallbackSnapshot(uint wcid)
            => new ListingSnapshot { Name = $"Item {wcid}", Wcid = wcid };

        /// <summary>
        /// The searchable projection for one vault row. A stored entry reads its WorldObject through the
        /// same FromItem path a listing uses; a ledger entry has only a wcid, so it reads the weenie.
        /// Never throws.
        /// </summary>
        private static ListingSnapshot SnapshotVaultEntry(VaultEntry entry)
        {
            try
            {
                if (entry.WorldObject == null)
                    return LedgerSnapshot(entry.Wcid);

                var stored = MarketSnapshot.FromItem(entry.WorldObject);

                if (string.IsNullOrEmpty(stored.Name))
                    stored.Name = $"Item {entry.Wcid}";

                return stored;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] could not describe vault wcid {entry.Wcid}; showing it unnamed: {ex.GetFullMessage()}");
                return FallbackSnapshot(entry.Wcid);
            }
        }

        /// <summary>
        /// SELF-INVALIDATING: the lookup runs every time (it is a dictionary hit on the world cache) and
        /// the returned INSTANCE is the staleness signal. WorldDatabaseWithEntityCache.GetWeenie always
        /// stores a freshly converted Weenie, so ClearCachedWeenie - which the live /import content path
        /// calls - guarantees a different instance and forces a rebuild here with no call-site plumbing.
        /// </summary>
        private static ListingSnapshot LedgerSnapshot(uint wcid)
        {
            var weenie = WeenieLookup?.Invoke(wcid);

            // A failed lookup is never cached: a transient world read must not pin "Item wcid" forever.
            if (weenie == null)
                return FallbackSnapshot(wcid);

            if (ledgerSnapshots.TryGetValue(wcid, out var cached) && ReferenceEquals(cached.Source, weenie))
                return cached.Snapshot;

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            if (string.IsNullOrEmpty(snapshot.Name))
                snapshot.Name = $"Item {wcid}";

            ledgerSnapshots[wcid] = (weenie, snapshot);
            return snapshot;
        }

        /// <summary>
        /// The live vault view behind GET /v1/accounts/me/vault. A WRITE-TIER read (DESIGN 7), per-user
        /// against the vault not the index; returns false with vault_unavailable rather than empty so a shard hiccup never reads as "your vault is empty".
        /// </summary>
        public static bool TryGetVaultView(uint accountId, out IReadOnlyList<object> entries, out MarketError error)
        {
            entries = new List<object>();
            error = MarketError.None;

            if (!Enabled)
            {
                error = MarketError.Disabled;
                return false;
            }

            if (!itemStore.IsReady(accountId, out _))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            var active = GetActiveListingsForAccount(accountId);

            var view = new List<object>();
            var index = 0;

            foreach (var entry in itemStore.GetEntries(accountId, 0, -1))
            {
                index++;

                var listing = active.FirstOrDefault(l => entry.IsLedger
                    ? l.ItemGuid == null && l.Wcid == entry.Wcid
                    : l.ItemGuid == entry.Guid.Full);

                var snapshot = SnapshotVaultEntry(entry);

                view.Add(new
                {
                    entry_number = index,
                    is_ledger = entry.IsLedger,

                    // N separate whole biotas on one line. The web needs this to know that `count` is
                    // a MEMBER count and that a listing over this row may name more than 1, where a
                    // lone stored item is still pinned to exactly 1. item_guid stays the group's
                    // representative and remains the identity a listing is created against.
                    is_group = entry.IsGroup,
                    item_guid = entry.IsLedger ? (uint?)null : entry.Guid.Full,
                    wcid = entry.Wcid,
                    count = entry.Count,
                    name = snapshot.Name,
                    icon_id = snapshot.IconId,
                    icon_overlay_id = snapshot.IconOverlayId,
                    icon_underlay_id = snapshot.IconUnderlayId,
                    palette_template = snapshot.PaletteTemplate,
                    listing_id = listing?.Id,
                    listed_count = listing?.Count,
                    listed_price_mmd = listing?.PriceMmd,
                    snapshot,
                });
            }

            entries = view;
            return true;
        }

        /// <summary>Transactions this account was on either side of, newest first.</summary>
        public static IReadOnlyList<MarketTransaction> GetHistoryForCharacterAccount(uint accountId, int max)
        {
            if (max < 1)
                max = 1;

            lock (indexLock)
                return transactions.Values
                    .Where(t => t.BuyerAccountId == accountId || t.SellerAccountId == accountId)
                    .OrderByDescending(t => t.Id)
                    .Take(max)
                    .Select(Copy)
                    .ToList();
        }

        /// <summary>
        /// The vault as numbered chat lines for /market vault (DESIGN section 8), 1-based and matching
        /// GetEntries' paging order so a number here is the number ListByEntryNumber resolves.
        /// NOT STABLE ACROSS RESTARTS (vault reorders on load, AccountVaultStore.cs:1007-1016) - a listing is created from a resolved entry within one call, never a number held across sessions.
        /// </summary>
        public static bool TryGetVaultEntries(uint accountId, out IReadOnlyList<string> lines, out MarketError error)
        {
            lines = new List<string>();
            error = MarketError.None;

            if (!Enabled)
            {
                error = MarketError.Disabled;
                return false;
            }

            if (!itemStore.IsReady(accountId, out _))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            var active = GetActiveListingsForAccount(accountId);
            var rendered = new List<string>();
            var index = 0;

            foreach (var entry in itemStore.GetEntries(accountId, 0, -1))
            {
                index++;

                var listing = active.FirstOrDefault(l => entry.IsLedger
                    ? l.ItemGuid == null && l.Wcid == entry.Wcid
                    : l.ItemGuid == entry.Guid.Full);

                var name = entry.WorldObject?.Name ?? $"Item {entry.Wcid}";
                var tail = listing != null ? $"  [listed #{listing.Id}, {listing.Count} at {listing.PriceMmd:N0} MMD]" : string.Empty;

                rendered.Add($"{index}. {entry.Count} x {name}{tail}");
            }

            lines = rendered;
            return true;
        }

        /// <summary>
        /// Lists the entry named by its 1-based /market vault number; resolve-and-create happen in ONE
        /// call so the number cannot go stale. <paramref name="count"/> 0 means the whole entry: the
        /// full ledger count, every member of a group row, or 1 for a lone stored item (which is the
        /// only count a lone stored item can be listed at).
        /// </summary>
        public static MarketResult<MarketListing> ListByEntryNumber(MarketActor actor, int entryNumber, long priceMmd, int count,
                                                                   MarketChannel channel)
        {
            if (!Enabled)
                return MarketResult<MarketListing>.Fail(MarketError.Disabled);

            // The three refusals below are all pre-resolution, so the row carries no item and no wcid;
            // the entry number itself goes in detail, which is the only thing that identifies them.
            if (entryNumber < 1)
                return RefuseList(MarketError.ItemNotFound, channel, actor, null, 0, count, priceMmd,
                                  $"vault entry number {entryNumber}");

            if (!itemStore.IsReady(actor.AccountId, out _))
                return RefuseList(MarketError.VaultUnavailable, channel, actor, null, 0, count, priceMmd,
                                  $"vault entry number {entryNumber}");

            var entries = itemStore.GetEntries(actor.AccountId, 0, -1);

            if (entryNumber > entries.Count)
                return RefuseList(MarketError.ItemNotFound, channel, actor, null, 0, count, priceMmd,
                                  $"vault entry number {entryNumber} of {entries.Count}");

            var entry = entries[entryNumber - 1];

            // A group row is divisible in whole members, so "the whole entry" means all of them, the
            // same as it does for a ledger row. Only a LONE stored biota defaults to 1.
            var wanted = count > 0
                ? count
                : (entry.IsLedger || entry.IsGroup ? (int)Math.Min(entry.Count, int.MaxValue) : 1);

            // List records its own refusals, so this hands off rather than wrapping - one attempt
            // produces one row either way.
            return List(actor, entry.IsLedger ? (uint?)null : entry.Guid.Full, entry.Wcid, wanted, priceMmd, channel);
        }

        // ---- helpers ----

        /// <summary>Caller must hold <see cref="indexLock"/>.</summary>
        private static MarketListing ActiveListingFor(uint accountId, uint? itemGuid, uint wcid)
        {
            return listings.Values.FirstOrDefault(l =>
                l.Status == MarketListingStatus.Active &&
                l.SellerAccountId == accountId &&
                (itemGuid != null ? l.ItemGuid == itemGuid : l.ItemGuid == null && l.Wcid == wcid));
        }

        private static VaultEntry FindEntry(uint accountId, uint? itemGuid, uint wcid)
        {
            foreach (var entry in itemStore.GetEntries(accountId, 0, -1))
            {
                if (itemGuid != null)
                {
                    if (!entry.IsLedger && entry.Guid.Full == itemGuid.Value)
                        return entry;
                }
                else if (entry.IsLedger && entry.Wcid == wcid)
                {
                    return entry;
                }
            }

            return null;
        }

        private static long HeldLedgerUnits(uint accountId, uint wcid)
        {
            var entry = FindEntry(accountId, null, wcid);
            return entry?.Count ?? 0;
        }

        private static MarketListing FromRow(ShardMarketListing row) => new MarketListing
        {
            Id = row.Id,
            SellerAccountId = row.SellerAccountId,
            SellerCharacterGuid = row.SellerCharacterGuid,
            SellerCharacterName = row.SellerCharacterName,
            ItemGuid = row.ItemGuid,
            Wcid = row.Wcid,
            Count = row.Count,
            PriceMmd = row.PriceMmd,
            Status = (MarketListingStatus)row.Status,
            CreatedAt = row.CreatedAt,
            ClosedAt = row.ClosedAt,
            Snapshot = MarketSnapshot.Deserialize(row.SnapshotJson),
        };

        internal static ShardMarketListing ToRow(MarketListing listing) => new ShardMarketListing
        {
            Id = listing.Id,
            SellerAccountId = listing.SellerAccountId,
            SellerCharacterGuid = listing.SellerCharacterGuid,
            SellerCharacterName = listing.SellerCharacterName,
            ItemGuid = listing.ItemGuid,
            ActiveItemGuid = listing.Status == MarketListingStatus.Active ? listing.ItemGuid : null,
            ActiveLedgerWcid = listing.Status == MarketListingStatus.Active && listing.ItemGuid == null
                ? (uint?)listing.Wcid
                : null,
            Wcid = listing.Wcid,
            Count = listing.Count,
            PriceMmd = listing.PriceMmd,
            Status = (int)listing.Status,
            CreatedAt = listing.CreatedAt,
            ClosedAt = listing.ClosedAt,
            SnapshotJson = MarketSnapshot.Serialize(listing.Snapshot),
        };

        private static MarketTransaction FromRow(ShardMarketTransaction row) => new MarketTransaction
        {
            Id = row.Id,
            ListingId = row.ListingId,
            BuyOrderId = row.BuyOrderId,
            BuyerAccountId = row.BuyerAccountId,
            BuyerCharacterGuid = row.BuyerCharacterGuid,
            BuyerCharacterName = row.BuyerCharacterName,
            SellerAccountId = row.SellerAccountId,
            SellerCharacterGuid = row.SellerCharacterGuid,
            SellerCharacterName = row.SellerCharacterName,
            Wcid = row.Wcid,
            ItemName = row.ItemName,
            Count = row.Count,
            PriceMmdTotal = row.PriceMmdTotal,
            Timestamp = row.Timestamp,
            Channel = (MarketChannel)row.Channel,
            Status = (MarketTransactionStatus)row.Status,
        };

        /// <summary>
        /// Feeds hand back COPIES: the index holds live objects a concurrent close mutates in place,
        /// and a caller serializing one mid-mutation would emit a disagreeing status and sequence.
        /// </summary>
        private static MarketListing Copy(MarketListing l) => new MarketListing
        {
            Id = l.Id,
            SellerAccountId = l.SellerAccountId,
            SellerCharacterGuid = l.SellerCharacterGuid,
            SellerCharacterName = l.SellerCharacterName,
            ItemGuid = l.ItemGuid,
            Wcid = l.Wcid,
            Count = l.Count,
            PriceMmd = l.PriceMmd,
            Status = l.Status,
            CreatedAt = l.CreatedAt,
            ClosedAt = l.ClosedAt,
            Snapshot = l.Snapshot,
            Seq = l.Seq,
        };

        private static MarketTransaction Copy(MarketTransaction t) => new MarketTransaction
        {
            Id = t.Id,
            ListingId = t.ListingId,
            BuyOrderId = t.BuyOrderId,
            BuyerAccountId = t.BuyerAccountId,
            BuyerCharacterGuid = t.BuyerCharacterGuid,
            BuyerCharacterName = t.BuyerCharacterName,
            SellerAccountId = t.SellerAccountId,
            SellerCharacterGuid = t.SellerCharacterGuid,
            SellerCharacterName = t.SellerCharacterName,
            Wcid = t.Wcid,
            ItemName = t.ItemName,
            Count = t.Count,
            PriceMmdTotal = t.PriceMmdTotal,
            Timestamp = t.Timestamp,
            Channel = t.Channel,
            Status = t.Status,
            Seq = t.Seq,
        };
    }
}
