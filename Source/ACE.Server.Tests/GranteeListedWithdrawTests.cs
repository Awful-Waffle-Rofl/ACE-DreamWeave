using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;
using MarketListing = ACE.Server.Managers.Market.MarketListing;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A mule GRANTEE (another account's character holding a withdraw grant) must not cancel the
    /// OWNER's market listing by withdrawing the listed item. Driven through a real AccountVaultStore
    /// with all three of the market's vault hooks wired exactly as MarketManager.Initialize() wires
    /// them, so the refusal is proven at the shared TryWithdraw core every entry point reaches, and
    /// the owner's own auto-delist is proven unchanged beside it.
    ///
    /// The vault holds: one stored item (StoredWcid), one unlisted stored item (FreeWcid), a group of
    /// three equivalent bags (GroupWcid), and a stack ledger row of 10 units (LedgerWcid).
    /// </summary>
    [TestClass]
    public class GranteeListedWithdrawTests
    {
        private const uint OwnerAccount = 9101;
        private const uint OwnerCharacter = 0x50000911;
        private const uint GranteeAccount = 9102;
        private const uint GranteeCharacter = 0x50000912;

        private const uint StoredWcid = 20991;
        private const uint FreeWcid = 20992;
        private const uint GroupWcid = 20993;
        private const uint LedgerWcid = 20994;
        private const long LedgerHeld = 10;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");
        private static readonly VaultActor Grantee = new VaultActor(GranteeAccount, GranteeCharacter, "Muleuser");
        private static readonly MarketActor Seller = new MarketActor(OwnerAccount, OwnerCharacter, "Vaultowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;
        private AccountVaultStore vault;
        private ACE.Server.WorldObjects.Container container;

        private Action<uint, uint?, uint, int, string> savedPreWithdrawHook;
        private Func<uint, uint?, uint, string, bool> savedIsListedHook;
        private Func<uint, uint?, uint, int, string, int?> savedWouldDelistHook;
        private Func<uint, ACE.Entity.Models.Weenie> savedWeenieLookup;

        [TestInitialize]
        public void Setup()
        {
            MarketManagerTests.SeedMarketTunables();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            savedPreWithdrawHook = AccountVaultStore.PreWithdrawHook;
            savedIsListedHook = AccountVaultStore.IsListedHook;
            savedWouldDelistHook = AccountVaultStore.WithdrawWouldDelistHook;
            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;

            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            container = FakeVaultWorld.MakeContainer(255);
            world.Containers[container.Guid.Full] = container;
            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9970,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(StoredWcid, 100, 70, 10, 500)), "could not seed the stored item");
            Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(FreeWcid, 100, 70, 10, 501)), "could not seed the free item");

            for (var i = 0; i < 3; i++)
                Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(GroupWcid, 100, 77, 12, 640 + i)), "could not seed a group member");

            backend.Stacks.Add(new AccountVaultStack { Id = 7301, AccountId = OwnerAccount, Wcid = LedgerWcid, Count = LedgerHeld });

            backend.Grants.Add(new AccountVaultGrant
            {
                OwnerAccountId = OwnerAccount, GranteeCharacterGuid = GranteeCharacter, GranteeCharacterName = "Muleuser", CanWithdraw = true,
            });

            vault = new AccountVaultStore(OwnerAccount, backend, world);
            Assert.IsTrue(vault.IsLoaded, "the owner's store must load");

            var itemStore = new VaultMarketItemStore(account => account == OwnerAccount ? vault : null);

            MarketManager.Initialize(itemStore, new FakeMarketWallet(), new FakeMarketRepository());

            // Production wiring (MarketManager.Initialize()), which the three-seam overload leaves out.
            AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
            AccountVaultStore.IsListedHook = MarketManager.IsListed;
            AccountVaultStore.WithdrawWouldDelistHook = MarketManager.WithdrawWouldDelist;
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();

            AccountVaultStore.PreWithdrawHook = savedPreWithdrawHook;
            AccountVaultStore.IsListedHook = savedIsListedHook;
            AccountVaultStore.WithdrawWouldDelistHook = savedWouldDelistHook;
            MarketManager.WeenieLookup = savedWeenieLookup;

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
        }

        // ---- fixture ----

        private VaultEntry Stored(uint wcid) => vault.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem && e.Wcid == wcid);

        private VaultEntry Ledger() => vault.GetEntries(0, -1).SingleOrDefault(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == LedgerWcid);

        private long LedgerUnits() => backend.Stacks.Where(s => s.AccountId == OwnerAccount && s.Wcid == LedgerWcid).Sum(s => s.Count);

        private MarketListing List(uint? itemGuid, uint wcid, int count)
        {
            var result = MarketManager.List(Seller, itemGuid, wcid, count, 50, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"listing of wcid {wcid} x{count} refused: {result.Error}");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(result.Value.Id).Status, "precondition: the listing is live");

            return result.Value;
        }

        private bool Withdraw(VaultActor actor, VaultEntry entry, int amount, out List<WorldObject> withdrawn, out string reason)
        {
            var ok = false;
            List<WorldObject> got = null;
            string why = null;

            Assert.IsTrue(vault.Enqueue(() => ok = vault.TryWithdraw(entry, amount, actor, out got, out why)), "the withdraw never ran");

            withdrawn = got ?? new List<WorldObject>();
            reason = why;
            return ok;
        }

        private MarketListingStatus StatusOf(MarketListing listing) => MarketManager.GetListing(listing.Id).Status;

        // ---- stored item ----

        [TestMethod]
        public void Grantee_WithdrawingAListedStoredItem_IsRefused_AndTheListingAndItemStay()
        {
            var entry = Stored(StoredWcid);
            var listing = List(entry.Guid.Full, StoredWcid, 1);

            var ok = Withdraw(Grantee, entry, 1, out var got, out var reason);

            Assert.IsFalse(ok, "a grantee withdrew the owner's listed item");
            Assert.AreEqual(AccountVaultStore.ListedByOwnerMessage, reason);
            Assert.AreEqual(0, got.Count, "nothing may come out of a refused withdraw");
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing), "the owner's listing must survive a grantee's attempt");
            Assert.IsTrue(container.Inventory.ContainsKey(entry.Guid), "the listed item is still in the vault container");
            Assert.AreEqual(1, vault.GetEntries(0, -1).Count(e => e.Kind == VaultEntryKind.StoredItem && e.Wcid == StoredWcid), "and still on the vault's rows");
        }

        [TestMethod]
        public void Grantee_WithdrawingAnUnlistedStoredItem_Works_EvenWithAnotherItemListed()
        {
            // A listing elsewhere in the vault proves the guard is per row, not "anything listed".
            List(Stored(StoredWcid).Guid.Full, StoredWcid, 1);

            var entry = Stored(FreeWcid);

            var ok = Withdraw(Grantee, entry, 1, out var got, out var reason);

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual(entry.Guid, got[0].Guid);
            Assert.IsFalse(container.Inventory.ContainsKey(entry.Guid), "the unlisted item left the vault");
        }

        [TestMethod]
        public void Owner_WithdrawingAListedStoredItem_StillAutoDelists()
        {
            var entry = Stored(StoredWcid);
            var listing = List(entry.Guid.Full, StoredWcid, 1);

            var ok = Withdraw(Owner, entry, 1, out var got, out var reason);

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual(MarketListingStatus.Delisted, StatusOf(listing), "the owner's own withdraw keeps today's auto-delist");
        }

        // ---- group: any withdraw names the representative and closes the whole listing ----

        [TestMethod]
        public void Grantee_WithdrawingFromAPartlyListedGroup_IsRefused_BecauseEveryMemberIsListed()
        {
            var group = Stored(GroupWcid);
            Assert.IsTrue(group.IsGroup, "precondition: three equivalent bags are one group row");
            Assert.AreEqual(3L, group.Count);

            // Two of three listed: the panel still treats every member as listed (MarketManager_Suit's
            // AddStored), because any member's withdraw closes the whole listing.
            var listing = List(group.Guid.Full, GroupWcid, 2);

            var ok = Withdraw(Grantee, group, 1, out var got, out var reason);

            Assert.IsFalse(ok, "a grantee took a member of a listed group");
            Assert.AreEqual(AccountVaultStore.ListedByOwnerMessage, reason);
            Assert.AreEqual(0, got.Count);
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing));
            Assert.AreEqual(3L, Stored(GroupWcid).Count, "every member is still in the vault");
        }

        // ---- ledger: the grantee may take the unlisted remainder and no more ----

        [TestMethod]
        public void Grantee_PartlyListedLedger_MayTakeTheUnlistedRemainder_AndTheListingStands()
        {
            var listing = List(null, LedgerWcid, 4);

            var ok = Withdraw(Grantee, Ledger(), 6, out var got, out var reason);

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(6, got.Sum(o => o.StackSize ?? 1), "the six unlisted units came out");
            Assert.AreEqual(4, LedgerUnits(), "the four listed units are still in the ledger");
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing), "taking only the free remainder leaves the listing standing");
        }

        [TestMethod]
        public void Grantee_PartlyListedLedger_DiggingIntoTheListedPart_IsRefused_AndNothingMoves()
        {
            var listing = List(null, LedgerWcid, 4);

            var ok = Withdraw(Grantee, Ledger(), 7, out var got, out var reason);

            Assert.IsFalse(ok, "a grantee withdrew units the owner has listed");
            Assert.AreEqual("Only 6 of that item can be withdrawn. The rest is listed on the market by its owner.", reason);
            Assert.AreEqual(0, got.Count);
            Assert.AreEqual(LedgerHeld, LedgerUnits(), "the ledger did not move");
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing));
        }

        [TestMethod]
        public void Grantee_FullyListedLedger_IsRefusedWithThePlainMessage()
        {
            var listing = List(null, LedgerWcid, (int)LedgerHeld);

            var ok = Withdraw(Grantee, Ledger(), 1, out _, out var reason);

            Assert.IsFalse(ok);
            Assert.AreEqual(AccountVaultStore.ListedByOwnerMessage, reason, "no free remainder means the plain refusal, never 'Only 0'");
            Assert.AreEqual(LedgerHeld, LedgerUnits());
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing));
        }

        [TestMethod]
        public void Owner_PartlyListedLedger_DiggingIntoTheListedPart_StillAutoDelists()
        {
            var listing = List(null, LedgerWcid, 4);

            var ok = Withdraw(Owner, Ledger(), 7, out var got, out var reason);

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(7, got.Sum(o => o.StackSize ?? 1));
            Assert.AreEqual(MarketListingStatus.Delisted, StatusOf(listing), "the owner's withdraw below the listed count delists, as before");
        }

        [TestMethod]
        public void Grantee_PartlyListedLedger_TwoSteps_TheFreeRemainderShrinksAfterTheFirstTake()
        {
            var listing = List(null, LedgerWcid, 4);

            Assert.IsTrue(Withdraw(Grantee, Ledger(), 3, out var first, out var firstReason), firstReason);
            Assert.AreEqual(3, first.Sum(o => o.StackSize ?? 1));
            Assert.AreEqual(7, LedgerUnits());

            // Six were free; three are now. A stale "6" here would mean the guard read a cached count.
            var ok = Withdraw(Grantee, Ledger(), 4, out var second, out var reason);

            Assert.IsFalse(ok, "the second take dug into the listed part");
            Assert.AreEqual("Only 3 of that item can be withdrawn. The rest is listed on the market by its owner.", reason);
            Assert.AreEqual(0, second.Count);
            Assert.AreEqual(7, LedgerUnits(), "the refused second take moved nothing");
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing));
        }

        [TestMethod]
        public void Grantee_OverCountLedgerAsk_GetsTheOverCountRefusal_AndTheListingStays()
        {
            var listing = List(null, LedgerWcid, 4);

            var ok = Withdraw(Grantee, Ledger(), (int)LedgerHeld + 1, out var got, out var reason);

            Assert.IsFalse(ok);
            Assert.AreEqual($"Your vault holds only {LedgerHeld} of those.", reason, "an over-count ask is told the row's size, not 'Only 6'");
            Assert.AreEqual(0, got.Count);
            Assert.AreEqual(LedgerHeld, LedgerUnits());
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing), "the over-count refusal must come before the auto-delist hook");
        }

        // ---- a market whose listing read failed fails closed for a grantee ----

        [TestMethod]
        public void Grantee_WhenTheMarketsListingReadFailed_IsRefused_AndTheShardRowStaysActive()
        {
            var entry = Stored(StoredWcid);
            var repo = new FakeMarketRepository();

            // List against a healthy market so the shard holds a real Active row over this item.
            MarketManager.Initialize(new VaultMarketItemStore(account => account == OwnerAccount ? vault : null), new FakeMarketWallet(), repo);
            var listing = List(entry.Guid.Full, StoredWcid, 1);

            // Restart over a listing read that fails. Production Initialize() wires the hooks after the
            // three-seam overload returns whether or not the read succeeded, so they are re-wired here.
            repo.FailListingRead = true;
            MarketManager.Initialize(new VaultMarketItemStore(account => account == OwnerAccount ? vault : null), new FakeMarketWallet(), repo);
            AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
            AccountVaultStore.IsListedHook = MarketManager.IsListed;
            AccountVaultStore.WithdrawWouldDelistHook = MarketManager.WithdrawWouldDelist;

            Assert.IsNull(MarketManager.GetListing(listing.Id), "control: the failed read left the index empty");

            var ok = Withdraw(Grantee, entry, 1, out var got, out var reason);

            Assert.IsFalse(ok, "a grantee withdrew over an index the market could not load");
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason, "the market cannot tell whether it is listed, so it must not claim it is");
            Assert.AreEqual(0, got.Count);
            Assert.IsTrue(container.Inventory.ContainsKey(entry.Guid), "the item is still in the vault");
            Assert.AreEqual(0, repo.Listings.Single(l => l.Id == listing.Id).Status, "the shard row is still Active");

            // The owner is unaffected by the fail-closed rule.
            Assert.IsTrue(Withdraw(Owner, entry, 1, out _, out var ownerReason), ownerReason);
        }

        // ---- the wiring pin ----

        /// <summary>
        /// The guard is only as good as its wiring, and every test here wires the hook by hand, so none of
        /// them would notice production Initialize() losing the line. Binds the assignment statement
        /// itself, inside each method's own body, rather than matching the identifier anywhere in the file.
        /// </summary>
        [TestMethod]
        public void MarketManager_WiresTheGuardInInitialize_AndClearsItInShutdown()
        {
            var path = FindInSourceTree("Source/ACE.Server/Managers/Market/MarketManager.cs");
            Assert.IsNotNull(path, $"Could not find MarketManager.cs by walking up from {AppContext.BaseDirectory}");

            var source = System.IO.File.ReadAllText(path).Replace("\r\n", "\n");

            var init = MethodBody(source, "public static void Initialize()");
            var shutdown = MethodBody(source, "public static void Shutdown()");

            var wire = new System.Text.RegularExpressions.Regex(@"(?<![\w.])AccountVaultStore\.WithdrawWouldDelistHook\s*=\s*WithdrawWouldDelist\s*;");
            var clear = new System.Text.RegularExpressions.Regex(@"(?<![\w.])AccountVaultStore\.WithdrawWouldDelistHook\s*=\s*null\s*;");

            Assert.AreEqual(1, wire.Matches(StripComments(init)).Count, "Initialize() must assign WithdrawWouldDelistHook = WithdrawWouldDelist exactly once");
            Assert.AreEqual(1, clear.Matches(StripComments(shutdown)).Count, "Shutdown() must null WithdrawWouldDelistHook exactly once");
        }

        private static string StripComments(string body)
            => System.Text.RegularExpressions.Regex.Replace(body, @"//[^\n]*", "");

        /// <summary>The brace-balanced body following <paramref name="signature"/>, which must occur exactly once.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"signature not found: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"signature is not unique: {signature}");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced body for {signature}");
            return null;
        }

        /// <summary>Same walk-up idiom as AccountVaultStoreTests.FindInSourceTree.</summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);

            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, native);

                if (System.IO.File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        // ---- the guard's own failure modes ----

        [TestMethod]
        public void Grantee_WhenTheGuardThrows_IsRefusedAsUnavailable_AndTheListingStays()
        {
            var entry = Stored(StoredWcid);
            var listing = List(entry.Guid.Full, StoredWcid, 1);

            AccountVaultStore.WithdrawWouldDelistHook = (_, _, _, _, _) => throw new InvalidOperationException("index hiccup");

            var ok = Withdraw(Grantee, entry, 1, out var got, out var reason);

            Assert.IsFalse(ok, "a throwing guard must fail closed for a non-owner");
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);
            Assert.AreEqual(0, got.Count);
            Assert.AreEqual(MarketListingStatus.Active, StatusOf(listing));
        }

        [TestMethod]
        public void Owner_NeverConsultsTheGuard()
        {
            var entry = Stored(StoredWcid);
            List(entry.Guid.Full, StoredWcid, 1);

            var calls = 0;
            AccountVaultStore.WithdrawWouldDelistHook = (_, _, _, _, _) => { calls++; return 0; };

            var ok = Withdraw(Owner, entry, 1, out _, out var reason);

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(0, calls, "the owner's withdraw must not ask the non-owner guard");
        }
    }
}
