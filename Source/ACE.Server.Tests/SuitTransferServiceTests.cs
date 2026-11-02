using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The suit builder's vault-to-character transfer, driven through a fake world seam over a REAL
    /// AccountVaultStore (FakeVaultBackend / FakeVaultWorld), so every withdraw runs the real store and the
    /// real VaultPackDelivery core. The world queue and the character's action queue are both pumped by hand.
    /// </summary>
    [TestClass]
    public class SuitTransferServiceTests
    {
        private const uint Account = 9411;
        private const uint OtherAccount = 9412;
        private const uint Character = 0x50000941;
        private const uint OtherCharacter = 0x50000942;

        private const uint ArmorWcid = 94001;
        private const uint RingWcid = 94002;
        private const uint BagWcid = 21021;

        private FakeVaultBackend backend;
        private FakeVaultWorld vaultWorld;
        private Container vaultContainer;
        private AccountVaultStore store;
        private FakeSuitTransferWorld world;
        private SuitTransferService service;

        /// <summary>
        /// Template weenies by wcid for MarketManager.WeenieLookup, which the shared bonded rule reads for ledger rows and
        /// class lines. A wcid not in here has no template (null), which is not bonded. The default lookup reads the world
        /// database, which a unit test does not have, so every test gets this map and the previous lookup is restored.
        /// </summary>
        private readonly Dictionary<uint, ACE.Entity.Models.Weenie> templates = new Dictionary<uint, ACE.Entity.Models.Weenie>();

        private Func<uint, ACE.Entity.Models.Weenie> savedTemplateLookup;

        [TestCleanup]
        public void RestoreTemplateLookup() => MarketManager.WeenieLookup = savedTemplateLookup;

        [TestInitialize]
        public void Setup()
        {
            savedTemplateLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = wcid => templates.TryGetValue(wcid, out var w) ? w : null;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));
            VaultClassTestConfig.Seed();

            backend = new FakeVaultBackend();
            vaultWorld = new FakeVaultWorld();

            vaultContainer = FakeVaultWorld.MakeContainer(255);
            vaultWorld.Containers[vaultContainer.Guid.Full] = vaultContainer;
            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9941,
                AccountId = Account,
                ContainerGuid = vaultContainer.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            store = new AccountVaultStore(Account, backend, vaultWorld);

            world = new FakeSuitTransferWorld { Store = store };
            world.Online[(Account, Character)] = new SuitTransferCharacter(Account, Character, "Suitmover", null, null);

            service = new SuitTransferService(world);

            SetUpSharedOwner();
        }

        // ---- a mule shared with Character, owned by another account ----

        private const uint OwnerAccount = 9413;
        private const uint GranterCharacter = 0x50000943;
        private const uint OwnerOtherCharacter = 0x50000945;
        private const uint AltCharacter = 0x50000944;   // a second character on Account, the caller's own

        private FakeVaultBackend ownerBackend;
        private FakeVaultWorld ownerVaultWorld;
        private Container ownerContainer;
        private AccountVaultStore ownerStore;
        private AccountVaultGrant grant;

        private void SetUpSharedOwner()
        {
            ownerBackend = new FakeVaultBackend();
            ownerVaultWorld = new FakeVaultWorld();

            ownerContainer = FakeVaultWorld.MakeContainer(255);
            ownerVaultWorld.Containers[ownerContainer.Guid.Full] = ownerContainer;
            ownerBackend.Vaults.Add(new ShardAccountVault
            {
                Id = 9942,
                AccountId = OwnerAccount,
                ContainerGuid = ownerContainer.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            ownerStore = new AccountVaultStore(OwnerAccount, ownerBackend, ownerVaultWorld);
            world.OtherStores[OwnerAccount] = ownerStore;

            grant = new AccountVaultGrant
            {
                Id = 77,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = Character,
                GranteeCharacterName = "Suitmover",
                CanWithdraw = true,
                GrantedByCharacterGuid = GranterCharacter,
                GrantedByCharacterName = "Granter",
            };
            ownerBackend.Grants.Add(grant);

            // The production read: grantee-keyed, over the owner's grant rows.
            world.GrantsForGrantee = guid => ownerBackend.GetAccountVaultGrantsForGrantee(guid);
        }

        // ---- fixtures ----

        private WorldObject SeedStored(uint wcid, int burden = 0)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 1);
            item.EncumbranceVal = burden;
            Assert.IsTrue(vaultContainer.TryAddToInventory(item), "could not seed a stored item");
            return item;
        }

        private void SeedLedger(uint wcid, long count) =>
            backend.Stacks.Add(new AccountVaultStack { Id = (uint)(7400 + backend.Stacks.Count), AccountId = Account, Wcid = wcid, Count = count });

        private long LedgerCount(uint wcid) => backend.Stacks.Where(s => s.Wcid == wcid).Sum(s => s.Count);

        private static SuitTransferLine Stored(WorldObject item) => new SuitTransferLine(item.Guid.Full, item.WeenieClassId, null, 1);

        private static SuitTransferLine Ledger(uint wcid, int count) => new SuitTransferLine(null, wcid, null, count);

        private SuitTransferSubmitResult Submit(string key, bool allowListed, params SuitTransferLine[] lines) =>
            service.Submit(new SuitTransferRequest(Account, Character, key, lines, allowListed));

        private SuitTransferStatus SubmitAndRun(string key, bool allowListed, params SuitTransferLine[] lines)
        {
            var submitted = Submit(key, allowListed, lines);
            Assert.AreEqual(MarketError.None, submitted.Error, "the submit itself must be accepted");

            world.Pump();

            return service.GetStatus(Account, submitted.TransferId);
        }

        private bool InVault(WorldObject item) => vaultContainer.Inventory.ContainsKey(item.Guid);

        // ---- idempotency and single flight ----

        [TestMethod]
        public void Replay_OfTheSameKey_ReturnsTheSameId_AndDoesNotRunTwice()
        {
            var ring = SeedStored(RingWcid);

            var first = Submit("key-1", false, Stored(ring));
            var replay = Submit("key-1", false, Stored(ring));

            Assert.AreEqual(MarketError.None, first.Error);
            Assert.AreEqual(MarketError.None, replay.Error, "a replay while the first is still queued is not transfer_in_progress");
            Assert.AreEqual(first.TransferId, replay.TransferId);
            Assert.AreEqual(1, world.WorldQueue.Count, "the replay must not enqueue a second run");

            world.Pump();

            var afterRun = Submit("key-1", false, Stored(ring));
            Assert.AreEqual(first.TransferId, afterRun.TransferId, "a replay after completion still answers the same id");

            world.Pump();

            Assert.AreEqual(1, world.WithdrawCalls, "exactly one withdraw for one requested item, however often it is replayed");
            Assert.AreEqual(SuitTransferState.Completed, service.GetStatus(Account, first.TransferId).State);
        }

        /// <summary>
        /// Idempotency keys are scoped per account: account B posting the key account A already used gets a
        /// NEW transfer id for its own job, never A's - and is not blocked by A's single-flight slot either.
        /// </summary>
        [TestMethod]
        public void SameKey_FromAnotherAccount_IsANewTransfer_NeverTheFirstAccounts()
        {
            var ring = SeedStored(RingWcid);
            world.Online[(OtherAccount, OtherCharacter)] = new SuitTransferCharacter(OtherAccount, OtherCharacter, "Otheraccount", null, null);

            var mine = Submit("shared-key", false, Stored(ring));
            var theirs = service.Submit(new SuitTransferRequest(OtherAccount, OtherCharacter, "shared-key", new[] { Stored(ring) }, false));

            Assert.AreEqual(MarketError.None, mine.Error);
            Assert.AreEqual(MarketError.None, theirs.Error, "another account's active transfer must not block this one");
            Assert.AreNotEqual(mine.TransferId, theirs.TransferId, "B must never be handed A's transfer id");
            Assert.AreEqual(2, world.WorldQueue.Count, "B's transfer is its own job");

            Assert.IsNull(service.GetStatus(OtherAccount, mine.TransferId), "and B still cannot read A's");
            Assert.IsNotNull(service.GetStatus(OtherAccount, theirs.TransferId));
            Assert.IsNull(service.GetStatus(Account, theirs.TransferId));
        }

        [TestMethod]
        public void SecondTransfer_WhileOneIsActive_IsTransferInProgress()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            var first = Submit("key-a", false, Stored(ring));
            var second = Submit("key-b", false, Stored(armor));

            Assert.AreEqual(MarketError.None, first.Error);
            Assert.AreEqual(MarketError.TransferInProgress, second.Error);

            world.Pump();

            var third = Submit("key-c", false, Stored(armor));
            Assert.AreEqual(MarketError.None, third.Error, "the slot frees once the first finishes");
        }

        [TestMethod]
        public void Offline_OrAnotherAccountsCharacter_IsCharacterOffline()
        {
            var ring = SeedStored(RingWcid);

            var otherChar = service.Submit(new SuitTransferRequest(Account, OtherCharacter, "k1", new[] { Stored(ring) }, false));
            Assert.AreEqual(MarketError.CharacterOffline, otherChar.Error, "a character not online on THIS account's session");

            world.Online.Clear();

            var offline = Submit("k2", false, Stored(ring));
            Assert.AreEqual(MarketError.CharacterOffline, offline.Error);
            Assert.AreEqual(0, world.WorldQueue.Count, "nothing is enqueued for an offline character");
        }

        [TestMethod]
        public void Validate_RefusesTooManyObjects_AndMalformedLines()
        {
            var lines25 = Enumerable.Range(0, 25).Select(i => Ledger(RingWcid, 1)).ToArray();
            Assert.AreEqual(MarketError.TooManyItems, Submit("k", false, lines25).Error);
            Assert.AreEqual(MarketError.TooManyItems, Submit("k", false, Ledger(RingWcid, 25)).Error, "the cap counts objects, not lines");
            Assert.AreEqual(MarketError.None, SuitTransferService.Validate(new SuitTransferRequest(Account, Character, "k", new[] { Ledger(RingWcid, 24) }, false)));

            Assert.AreEqual(MarketError.InvalidTransfer, Submit("", false, Ledger(RingWcid, 1)).Error);
            Assert.AreEqual(MarketError.InvalidTransfer, Submit(new string('x', 65), false, Ledger(RingWcid, 1)).Error);
            Assert.AreEqual(MarketError.InvalidTransfer, Submit("k", false).Error);
            Assert.AreEqual(MarketError.InvalidTransfer, Submit("k", false, new SuitTransferLine(5, RingWcid, "class", 1)).Error, "a guid and a class key together");
            Assert.AreEqual(MarketError.InvalidTransfer, Submit("k", false, Ledger(RingWcid, 0)).Error);
        }

        // ---- world-thread gates ----

        [TestMethod]
        public void SessionDrops_BetweenAcceptAndRun_IsRejected_NothingWithdrawn()
        {
            var ring = SeedStored(RingWcid);

            var submitted = Submit("k", false, Stored(ring));
            Assert.AreEqual(MarketError.None, submitted.Error);

            world.Eligible = false;
            world.Pump();

            var status = service.GetStatus(Account, submitted.TransferId);

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.CharacterOffline, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
        }

        [TestMethod]
        public void OutsideTheVaultArea_IsRejected_NothingWithdrawn()
        {
            var ring = SeedStored(RingWcid);
            world.GateError = MarketError.NotInVaultArea;

            var status = SubmitAndRun("k", false, Stored(ring));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.NotInVaultArea, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));
        }

        [TestMethod]
        public void VaultPanelOpen_IsRejected_NothingWithdrawn()
        {
            var ring = SeedStored(RingWcid);
            world.GateError = MarketError.VaultPanelOpen;

            var status = SubmitAndRun("k", false, Stored(ring));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.VaultPanelOpen, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls);
        }

        [TestMethod]
        public void PackFull_BySlots_RefusesTheWholeJob()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);
            world.FreeSlots = 1;

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.PackFull, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls, "the whole job is refused up front - not one item then a failure");
            Assert.IsTrue(InVault(ring) && InVault(armor));
        }

        [TestMethod]
        public void PackFull_ByBurden_RefusesTheWholeJob_SummingEveryLine()
        {
            var armor = SeedStored(ArmorWcid, burden: 600);
            SeedLedger(RingWcid, 5);
            world.TemplateBurdens[RingWcid] = 50;

            // 600 + 3 x 50 = 750 > 700, while each line alone would fit.
            world.AvailableBurdenValue = 700;

            var status = SubmitAndRun("k", false, Stored(armor), Ledger(RingWcid, 3));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.PackFull, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.AreEqual(5L, LedgerCount(RingWcid));
            Assert.IsTrue(InVault(armor));
        }

        [TestMethod]
        public void PackFits_ExactlyAtTheBurdenLimit_Runs()
        {
            var armor = SeedStored(ArmorWcid, burden: 600);
            SeedLedger(RingWcid, 5);
            world.TemplateBurdens[RingWcid] = 50;
            world.AvailableBurdenValue = 750;

            var status = SubmitAndRun("k", false, Stored(armor), Ledger(RingWcid, 3));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.IsTrue(status.Items.All(i => i.Outcome == SuitTransferOutcome.Delivered));
        }

        [TestMethod]
        public void ColdVault_WaitsForTheLoad_ThenRuns()
        {
            var ring = SeedStored(RingWcid);
            FakeVaultWorld.SetInventoryLoaded(vaultContainer, false);

            var submitted = Submit("k", false, Stored(ring));

            world.RunWorldQueueOnly();
            Assert.AreEqual(SuitTransferState.Running, service.GetStatus(Account, submitted.TransferId).State, "still waiting, not refused");
            Assert.AreEqual(0, world.WithdrawCalls);

            FakeVaultWorld.SetInventoryLoaded(vaultContainer, true);
            world.Pump();

            var status = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items.Single().Outcome);
        }

        // ---- per-item outcomes ----

        [TestMethod]
        public void PartialFailure_OneDeliveryFails_ItIsReturned_TheOthersDelivered()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.Deliver = item => item.WeenieClassId != ArmorWcid;

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Returned, status.Items[1].Outcome);
            Assert.IsFalse(InVault(ring), "the delivered item stays delivered");
            Assert.IsTrue(InVault(armor), "the undeliverable item is back in the vault");
        }

        [TestMethod]
        public void Listed_IsRefusedWithoutOptIn_AndDeliveredWithIt()
        {
            var ring = SeedStored(RingWcid);
            world.Listed = row => row.Guid.Full == ring.Guid.Full ? 1 : (int?)null;

            var refused = SubmitAndRun("k1", false, Stored(ring));
            Assert.AreEqual(SuitTransferOutcome.Refused, refused.Items.Single().Outcome);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));

            var allowed = SubmitAndRun("k2", true, Stored(ring));
            Assert.AreEqual(SuitTransferOutcome.Delivered, allowed.Items.Single().Outcome);
            Assert.IsFalse(InVault(ring));
        }

        /// <summary>
        /// A listing of 2 over a ledger row of 5: three units are free, the fourth would close the listing.
        /// </summary>
        [TestMethod]
        public void ListedLedger_OnlyTheUnitsThatWouldDelistAreRefused()
        {
            SeedLedger(RingWcid, 5);
            world.Listed = row => row.Kind == VaultEntryKind.Ledger ? 2 : (int?)null;

            var free = SubmitAndRun("k1", false, Ledger(RingWcid, 3));
            Assert.AreEqual(SuitTransferOutcome.Delivered, free.Items.Single().Outcome);
            Assert.AreEqual(2L, LedgerCount(RingWcid));

            var intoListed = SubmitAndRun("k2", false, Ledger(RingWcid, 1));
            Assert.AreEqual(SuitTransferOutcome.Refused, intoListed.Items.Single().Outcome);
            Assert.AreEqual(2L, LedgerCount(RingWcid));
        }

        [TestMethod]
        public void LedgerCountThree_IsThreeSingleUnitDeliveries()
        {
            SeedLedger(RingWcid, 5);

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 3));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items.Single().Outcome);
            Assert.AreEqual(3, world.WithdrawCalls, "one withdraw per unit");
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, world.Amounts.ToArray(), "every withdraw asks for exactly one unit");
            Assert.AreEqual(3, world.Delivered.Count);
            Assert.IsTrue(world.Delivered.All(i => (i.StackSize ?? 1) == 1));
            Assert.AreEqual(2L, LedgerCount(RingWcid));
        }

        /// <summary>
        /// A ledger line whose first unit's return THROWS: the line stops there. The two units after it are
        /// never attempted, so a Threw unit is never followed by another withdraw of the same line.
        /// </summary>
        [TestMethod]
        public void Threw_IsNeverRetried()
        {
            SeedLedger(RingWcid, 5);

            world.Deliver = item => false;
            backend.LedgerAdjustOverride = (account, wcid, delta) => delta > 0 ? throw new InvalidOperationException("credit throws") : (AccountVaultStackAdjustResult?)null;

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 3));

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items.Single().Outcome);
            Assert.AreEqual(1, world.WithdrawCalls, "after a Threw the line is never withdrawn from again");
            Assert.AreEqual(SuitTransferState.Completed, status.State);
        }

        [TestMethod]
        public void GroupMember_IsWithdrawnByItsOwnGuid()
        {
            var bags = new List<WorldObject>();

            for (var i = 0; i < 3; i++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640 + i);
                Assert.IsTrue(vaultContainer.TryAddToInventory(bag));
                bags.Add(bag);
            }

            var group = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(group.IsGroup, "precondition: three equivalent bags are one group row");

            // The LAST member, which a group-row withdraw (front of the bucket) would never pick.
            var wanted = group.Members[2];

            var status = SubmitAndRun("k", false, new SuitTransferLine(wanted.Guid.Full, BagWcid, null, 1));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items.Single().Outcome);
            Assert.AreSame(wanted, world.Delivered.Single(), "exactly the member asked for, by guid");
            Assert.IsFalse(InVault(wanted));
            Assert.IsTrue(InVault(group.Members[0]) && InVault(group.Members[1]), "no other member moved");
            Assert.AreEqual(group.Guid.Full, world.GroupNotices.Single().RepresentativeGuid, "reported under the representative, as a group withdraw is");
        }

        [TestMethod]
        public void ClassLine_IsDelivered_AndAFailedDeliveryGoesBackToTheClassRow()
        {
            vaultWorld.PristineResult = false;
            vaultWorld.ClassifyResult = true;

            for (var i = 0; i < 3; i++)
            {
                var ok = false;
                string reason = null;
                var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640);
                store.Enqueue(() => ok = store.TryDeposit(bag, new VaultActor(Account, Character, "Suitmover"), out reason));
                Assert.IsTrue(ok, reason);
            }

            var classRow = store.GetEntries(0, -1).Single();
            Assert.AreEqual(VaultEntryKind.Class, classRow.Kind, "precondition: one class row");

            var classLine = new SuitTransferLine(null, BagWcid, classRow.ClassDisplayId, 1);

            var delivered = SubmitAndRun("k1", false, classLine);
            Assert.AreEqual(SuitTransferOutcome.Delivered, delivered.Items.Single().Outcome);
            Assert.AreEqual(2L, store.GetEntries(0, -1).Single().Count);

            world.Deliver = item => false;

            var returned = SubmitAndRun("k2", false, classLine);
            Assert.AreEqual(SuitTransferOutcome.Returned, returned.Items.Single().Outcome);

            var rows = store.GetEntries(0, -1);
            Assert.AreEqual(1, rows.Count, "back onto the class row, never a stored biota");
            Assert.AreEqual(VaultEntryKind.Class, rows.Single().Kind);
            Assert.AreEqual(2L, rows.Single().Count);
        }

        /// <summary>
        /// The feature's exactly-one-object contract. The fake core is made to ask for three units where the
        /// service asked for one, so three objects come out: the core puts the two extras back, and the
        /// service must stop the line and say so rather than carry on as though one object had moved.
        /// </summary>
        [TestMethod]
        public void MoreThanOneObject_FromOneWithdraw_StopsTheLine_AndTheExtrasGoBack()
        {
            SeedLedger(RingWcid, 10);
            vaultWorld.ItemMaxStackSize = 1;
            world.AmountBump = 2;

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 3));

            var line = status.Items.Single();
            Assert.AreEqual(1, world.WithdrawCalls, "the line stops at the first anomalous withdraw");
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome, "one of three delivered, then stopped");
            StringAssert.Contains(line.Message, "more than one object");
            Assert.AreEqual(1, world.Delivered.Count);
            Assert.AreEqual(9L, LedgerCount(RingWcid), "only the one delivered unit left the ledger");
        }

        [TestMethod]
        public void StoredItem_AskedForInPart_IsRefused()
        {
            var stack = FakeVaultWorld.MakeStack(RingWcid, 5, 10);
            Assert.IsTrue(vaultContainer.TryAddToInventory(stack));

            var status = SubmitAndRun("k", false, new SuitTransferLine(stack.Guid.Full, RingWcid, null, 1));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
            Assert.AreEqual(0, world.WithdrawCalls);
        }

        [TestMethod]
        public void MidRunLogout_StopsTheRest_DeliveredStayDelivered()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                world.Eligible = false;   // the character logs out right after the first delivery
                return true;
            };

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.CharacterOffline, status.Reason);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.IsTrue(InVault(armor));
        }

        // ---- status ownership and expiry ----

        [TestMethod]
        public void Status_IsReadableOnlyByTheOwningAccount()
        {
            var ring = SeedStored(RingWcid);
            var submitted = Submit("k", false, Stored(ring));

            Assert.IsNotNull(service.GetStatus(Account, submitted.TransferId));
            Assert.IsNull(service.GetStatus(OtherAccount, submitted.TransferId), "another account's id reads as unknown");
            Assert.IsNull(service.GetStatus(Account, "nope"));
        }

        [TestMethod]
        public void QueuedTransfer_TheWorldNeverStarts_IsClosed_AndFreesTheAccount()
        {
            var ring = SeedStored(RingWcid);
            var submitted = Submit("k", false, Stored(ring));

            world.Now += SuitTransferService.QueuedTimeout + TimeSpan.FromSeconds(1);

            var status = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(SuitTransferState.Rejected, status.State);

            // The world queue finally runs: the closed job must not start.
            world.Pump();
            Assert.AreEqual(0, world.WithdrawCalls);

            Assert.AreEqual(MarketError.None, Submit("k2", false, Stored(ring)).Error, "the account is no longer wedged");
        }

        [TestMethod]
        public void FinishedTransfer_IsForgottenAfterRetention_AndItsKeyCanRunAgain()
        {
            var ring = SeedStored(RingWcid);
            var status = SubmitAndRun("k", false, Stored(ring));

            world.Now += SuitTransferService.Retention + TimeSpan.FromSeconds(1);

            Assert.IsNull(service.GetStatus(Account, status.TransferId));

            var again = Submit("k", false, Stored(ring));
            Assert.AreEqual(MarketError.None, again.Error);
            Assert.AreNotEqual(status.TransferId, again.TransferId);
        }

        // ---- the gate decision ----

        [TestMethod]
        public void Gates_DecideInTheFacetOrder()
        {
            var clear = SuitTransferGateState.Clear;
            Assert.AreEqual(MarketError.None, SuitTransferGates.Decide(clear, out _));

            Assert.AreEqual(MarketError.CharacterBusy, SuitTransferGates.Decide(new SuitTransferGateState(true, false, false, false, false, true, false, null), out _), "busy outranks everything after it");
            Assert.AreEqual(MarketError.CharacterBusy, SuitTransferGates.Decide(new SuitTransferGateState(false, true, false, false, false, false, true, null), out _));
            Assert.AreEqual(MarketError.CharacterBusy, SuitTransferGates.Decide(new SuitTransferGateState(false, false, true, false, false, false, true, null), out _));
            Assert.AreEqual(MarketError.CharacterBusy, SuitTransferGates.Decide(new SuitTransferGateState(false, false, false, true, false, false, true, null), out _));
            Assert.AreEqual(MarketError.CharacterBusy, SuitTransferGates.Decide(new SuitTransferGateState(false, false, false, false, true, false, true, null), out _), "the PK timer");
            Assert.AreEqual(MarketError.VaultPanelOpen, SuitTransferGates.Decide(new SuitTransferGateState(false, false, false, false, false, true, false, null), out _), "the vault vendor outranks the area");

            Assert.AreEqual(MarketError.NotInVaultArea, SuitTransferGates.Decide(new SuitTransferGateState(false, false, false, false, false, false, false, "the Marketplace"), out var message));
            StringAssert.Contains(message, "the Marketplace");
        }

        /// <summary>
        /// The facet switch's first two gates, first here too: a PvP match and a PvP template each refuse as
        /// in_pvp, and outrank every other gate (a match teleports and busies its players, which would otherwise
        /// surface as a misleading character_busy).
        /// </summary>
        [TestMethod]
        public void Gates_PvpMatchAndPvpTemplate_AreInPvp_AndComeFirst()
        {
            var everythingElseFails = new SuitTransferGateState(true, true, true, true, true, true, false, null, inPvpMatch: true);
            Assert.AreEqual(MarketError.InPvp, SuitTransferGates.Decide(everythingElseFails, out _), "a PvP match outranks busy and the rest");

            var templated = new SuitTransferGateState(true, false, false, false, false, false, true, null, pvpTemplateRefusal: "template says no");
            Assert.AreEqual(MarketError.InPvp, SuitTransferGates.Decide(templated, out var templateMessage), "a PvP template outranks busy");
            Assert.AreEqual("template says no", templateMessage, "the template's own refusal text reaches the player");

            Assert.AreEqual(MarketError.None, SuitTransferGates.Decide(new SuitTransferGateState(false, false, false, false, false, false, true, null, inPvpMatch: false, pvpTemplateRefusal: null), out _));
        }

        [TestMethod]
        public void InPvp_AtStart_IsRejected_NothingWithdrawn()
        {
            var ring = SeedStored(RingWcid);
            world.GateError = MarketError.InPvp;

            var status = SubmitAndRun("k", false, Stored(ring));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.InPvp, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));
        }

        /// <summary>The gates are re-run before every step: entering a PvP match mid-run stops the rest.</summary>
        [TestMethod]
        public void InPvp_MidRun_StopsTheRest_DeliveredStayDelivered()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                world.GateError = MarketError.InPvp;   // queued into a match right after the first delivery
                return true;
            };

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.InPvp, status.Reason);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.IsTrue(InVault(armor));
        }

        // ---- review fixes: a fault stops the whole transfer ----

        /// <summary>F1: a unit that THREW (its return threw) stops the whole transfer, not just its own line.</summary>
        [TestMethod]
        public void Threw_StopsTheWholeTransfer_TheNextLineIsNeverWithdrawn()
        {
            SeedLedger(RingWcid, 5);
            SeedLedger(ArmorWcid, 5);

            world.Deliver = item => false;
            backend.LedgerAdjustOverride = (account, wcid, delta) => delta > 0 ? throw new InvalidOperationException("credit throws") : (AccountVaultStackAdjustResult?)null;

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 1), Ledger(ArmorWcid, 1));

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items[0].Outcome, "precondition: line 1 threw");
            Assert.AreEqual(1, world.WithdrawCalls, "nothing is withdrawn after a Threw unit");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.AreEqual(5L, LedgerCount(ArmorWcid), "line 2's row is untouched");
        }

        /// <summary>
        /// F1: a STRANDED unit stops the whole transfer. The vault stops being ready between the withdraw and
        /// the return (so the return is refused: Stranded), and is ready again by the time the player is told,
        /// so a second line WOULD pass its own readiness check - only the stop rule keeps it from running.
        /// </summary>
        [TestMethod]
        public void Stranded_StopsTheWholeTransfer_TheNextLineIsNeverWithdrawn()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.Deliver = item =>
            {
                FakeVaultWorld.SetInventoryLoaded(vaultContainer, false);
                return false;
            };
            world.OnTell = _ => FakeVaultWorld.SetInventoryLoaded(vaultContainer, true);

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferOutcome.Stranded, status.Items[0].Outcome, "precondition: line 1 stranded");
            Assert.AreEqual(1, world.WithdrawCalls, "nothing is withdrawn after a Stranded unit");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.IsTrue(InVault(armor));
        }

        /// <summary>F2: a throw from the withdraw itself is Threw (state unknown) and stops the transfer.</summary>
        [TestMethod]
        public void WithdrawThrows_IsThrew_AndStopsTheTransfer()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.ThrowFromWithdraw = new InvalidOperationException("pack delivery throws");

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
        }

        /// <summary>
        /// F2: a throw BEFORE a unit's withdraw (here the gate read) means nothing left the vault for it: the line
        /// is refused, not Threw, and the transfer stops.
        /// </summary>
        [TestMethod]
        public void ThrowBeforeTheWithdraw_IsRefusedNotThrew_AndStopsTheTransfer()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);
            var third = SeedStored(RingWcid + 10);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                world.ThrowFromGates = new InvalidOperationException("gate read throws");
                return true;
            };

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor), Stored(third));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome, "nothing was withdrawn for it, so it is refused, never threw");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[2].Outcome, "the transfer stopped");
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.IsTrue(InVault(armor) && InVault(third));
        }

        /// <summary>F2: a throw in the reporting after a delivery does not turn the delivered unit into a fault.</summary>
        [TestMethod]
        public void ThrowAfterADelivery_StillDelivered_AndTheTransferCarriesOn()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                world.TellThrows = true;
                return true;
            };

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[1].Outcome);
            Assert.AreEqual(2, world.WithdrawCalls);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.None, status.Reason);
        }

        // ---- review fixes: the per-unit pack re-check ----

        /// <summary>F3: the pack fills up mid-run (the plan's fit was a snapshot): the rest stops as pack_full.</summary>
        [TestMethod]
        public void PackFillsMidRun_StopsTheRest_AsPackFull()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                world.FreeSlots = 0;
                return true;
            };

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.AreEqual(MarketError.PackFull, status.Reason);
            Assert.IsTrue(InVault(armor));
        }

        [TestMethod]
        public void BurdenFillsMidRun_StopsTheRest_AsPackFull()
        {
            var ring = SeedStored(RingWcid, burden: 10);
            var armor = SeedStored(ArmorWcid, burden: 50);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                world.AvailableBurdenValue = 49;
                return true;
            };

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.AreEqual(MarketError.PackFull, status.Reason);
        }

        // ---- review fixes: the arena-queue gate ----

        /// <summary>
        /// F4: queued for, offered or dispatched into an arena match is in_pvp, as the facet switch's arena gate
        /// refuses it, after the match and template gates and before every other one.
        /// </summary>
        [TestMethod]
        public void Gates_ArenaQueue_IsInPvp_AfterTheMatchAndTemplateGates()
        {
            var queued = new SuitTransferGateState(true, true, true, true, true, true, false, null, inArenaQueue: true);
            Assert.AreEqual(MarketError.InPvp, SuitTransferGates.Decide(queued, out var message), "the arena queue outranks busy and the rest");
            Assert.AreEqual(SuitTransferGates.ArenaQueueMessage, message);

            var templatedToo = new SuitTransferGateState(false, false, false, false, false, false, true, null, pvpTemplateRefusal: "template says no", inArenaQueue: true);
            SuitTransferGates.Decide(templatedToo, out var first);
            Assert.AreEqual("template says no", first, "the template gate is read before the arena queue");

            Assert.AreEqual(MarketError.None, SuitTransferGates.Decide(new SuitTransferGateState(false, false, false, false, false, false, true, null, inArenaQueue: false), out _));
        }

        /// <summary>
        /// F4: the live world reads the arena status through the facet switch's own rule. Not reachable without a
        /// live Player, so this binds the named argument to that exact expression in LiveSuitTransferWorld.CheckGates.
        /// </summary>
        [TestMethod]
        public void LiveGates_ReadTheArenaQueue_ThroughTheFacetSwitchRule()
        {
            var code = AccountVaultPurgeTests.StripComments(System.IO.File.ReadAllText(FindInSourceTree("Source/ACE.Server/Entity/AccountVault/SuitTransferWorld.cs")));

            const string signature = "public MarketError CheckGates(SuitTransferCharacter character, out string message, bool characterMove = false)";
            var start = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "LiveSuitTransferWorld.CheckGates was not found - has it been renamed? This guard must follow it.");
            Assert.AreEqual(-1, code.IndexOf(signature, start + 1, StringComparison.Ordinal), "exactly one live CheckGates");

            var end = code.IndexOf("\n        public ", start + signature.Length, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "no member follows CheckGates, so its body cannot be bounded");

            var body = new string(code.Substring(start, end - start).Where(c => !char.IsWhiteSpace(c)).ToArray());

            StringAssert.Contains(body, "inArenaQueue:ACE.Server.Pvp.PvpPlayerRules.RefusesFacetSwitchForArena(ACE.Server.Pvp.PvpMatchManager.Status(player).Kind)");
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);

            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, native);

                if (System.IO.File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        // ---- review fixes: request shape ----

        /// <summary>F5: the same item_guid twice in one request is invalid_transfer.</summary>
        [TestMethod]
        public void DuplicateItemGuid_InOneRequest_IsInvalidTransfer()
        {
            var ring = SeedStored(RingWcid);

            Assert.AreEqual(MarketError.InvalidTransfer, Submit("k", false, Stored(ring), Stored(ring)).Error);
            Assert.AreEqual(0, world.WorldQueue.Count, "nothing was recorded");
            Assert.AreEqual(MarketError.None, Submit("k2", false, Stored(ring)).Error, "control: the same line once is accepted");
        }

        // ---- review fixes: missing coverage ----

        /// <summary>A running job whose action chain stopped is closed by the watchdog and frees the account.</summary>
        [TestMethod]
        public void RunningTransfer_ThatStopsMakingProgress_IsClosed_AndFreesTheAccount()
        {
            var ring = SeedStored(RingWcid);
            var submitted = Submit("k", false, Stored(ring));

            // Begin runs and schedules the first step, which the character's action queue never runs.
            world.RunWorldQueueOnly();
            Assert.AreEqual(SuitTransferState.Running, service.GetStatus(Account, submitted.TransferId).State, "precondition: running");

            world.Now += SuitTransferService.StallTimeout - TimeSpan.FromSeconds(1);
            Assert.AreEqual(SuitTransferState.Running, service.GetStatus(Account, submitted.TransferId).State, "control: not closed before the stall timeout");
            Assert.AreEqual(MarketError.TransferInProgress, Submit("k2", false, Stored(ring)).Error, "control: still the account's one transfer");

            world.Now += TimeSpan.FromSeconds(2);

            var status = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.CharacterOffline, status.Reason);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);

            // The stale step finally runs: the closed job must not withdraw.
            world.Pump();
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));

            Assert.AreEqual(MarketError.None, Submit("k3", false, Stored(ring)).Error, "the account is no longer wedged");
        }

        [TestMethod]
        public void LedgerLine_AboveTheRowCount_IsRefused_NothingWithdrawn()
        {
            SeedLedger(RingWcid, 5);

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 6));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.AreEqual(5L, LedgerCount(RingWcid));
        }

        /// <summary>Two lines on the same ledger row are budgeted together: 3 + 3 over a row of 5 runs the first only.</summary>
        [TestMethod]
        public void TwoLedgerLines_OnTheSameWcid_ShareTheRowsBudget()
        {
            SeedLedger(RingWcid, 5);

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 3), Ledger(RingWcid, 3));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome, "the second line would overdraw the row the first already claimed");
            Assert.AreEqual(3, world.WithdrawCalls);
            Assert.AreEqual(2L, LedgerCount(RingWcid));
        }

        /// <summary>
        /// Something else takes the stored item through the REAL store between the plan and the step: the step
        /// re-resolves, finds it gone, and refuses - one object out, never two.
        /// </summary>
        [TestMethod]
        public void ConcurrentTake_BetweenPlanAndStep_IsRefused_NoDupe()
        {
            var ring = SeedStored(RingWcid);
            var submitted = Submit("k", false, Stored(ring));

            world.RunWorldQueueOnly();
            Assert.AreEqual(1, world.Scheduled.Count, "precondition: planned, step scheduled");

            var entry = store.GetEntries(0, -1).Single(e => e.Guid.Full == ring.Guid.Full);
            var ok = false;
            List<WorldObject> taken = null;
            store.Enqueue(() => ok = store.TryWithdraw(entry, 1, new VaultActor(Account, Character, "Suitmover"), out taken, out _, GroupTakeOrder.Front));
            Assert.IsTrue(ok, "the concurrent take itself must succeed");

            world.Pump();

            var status = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
            Assert.AreEqual(0, world.WithdrawCalls, "the step never withdraws what is gone");
            Assert.AreEqual(0, world.Delivered.Count);
            Assert.AreEqual(1, taken.Count, "exactly one object left the vault, by the concurrent take");
        }

        [TestMethod]
        public void ConcurrentLedgerTake_BetweenPlanAndStep_DeliversOnlyWhatIsLeft()
        {
            SeedLedger(RingWcid, 2);
            var submitted = Submit("k", false, Ledger(RingWcid, 2));

            world.RunWorldQueueOnly();

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == RingWcid);
            var ok = false;
            store.Enqueue(() => ok = store.TryWithdraw(entry, 1, new VaultActor(Account, Character, "Suitmover"), out _, out _, GroupTakeOrder.Front));
            Assert.IsTrue(ok, "the concurrent take itself must succeed");

            world.Pump();

            var line = service.GetStatus(Account, submitted.TransferId).Items.Single();
            Assert.AreEqual(1, world.Delivered.Count, "one unit was left, one delivered");
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome, "the second unit is gone");
            Assert.AreEqual(0L, LedgerCount(RingWcid), "never below zero: two units existed, two left");
        }

        /// <summary>
        /// allow_listed on ONE member of a listed stored GROUP really closes the group's listing, through the
        /// live hook body and the real market manager over the real vault store.
        /// </summary>
        [TestMethod]
        public void AllowListed_OnAGroupMember_ClosesTheGroupsListing()
        {
            var savedPreWithdraw = AccountVaultStore.PreWithdrawHook;
            var savedIsListed = AccountVaultStore.IsListedHook;
            var savedWeenieLookup = MarketManager.WeenieLookup;

            try
            {
                MarketManagerTests.SeedMarketTunables();
                MarketManager.WeenieLookup = _ => null;

                for (var i = 0; i < 3; i++)
                    Assert.IsTrue(vaultContainer.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640 + i)));

                var group = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem);
                Assert.IsTrue(group.IsGroup, "precondition: one group row");

                MarketManager.Initialize(new VaultMarketItemStore(a => a == Account ? store : null), new FakeMarketWallet(), new FakeMarketRepository());
                AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
                AccountVaultStore.IsListedHook = MarketManager.IsListed;

                var listed = MarketManager.List(new MarketActor(Account, Character, "Suitmover"), group.Guid.Full, BagWcid, 1, 5, MarketChannel.Web);
                Assert.IsTrue(listed.Ok, $"precondition: the group lists ({listed.Error})");
                Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listed.Value.Id).Status);

                world.UseLiveGroupHook = true;

                var status = SubmitAndRun("k", true, new SuitTransferLine(group.Members[2].Guid.Full, BagWcid, null, 1));

                Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items.Single().Outcome);
                Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listed.Value.Id).Status, "moving a listed group member takes the group's listing down");
            }
            finally
            {
                MarketManager.Shutdown();
                AccountVaultStore.PreWithdrawHook = savedPreWithdraw;
                AccountVaultStore.IsListedHook = savedIsListed;
                MarketManager.WeenieLookup = savedWeenieLookup;
            }
        }

        /// <summary>F7: a REJECTED transfer replays under its key (same id, still rejected, nothing run), so a retry needs a new key.</summary>
        [TestMethod]
        public void RejectedTransfer_ReplaysUnderItsKey_AndNeverRuns()
        {
            var ring = SeedStored(RingWcid);
            world.GateError = MarketError.NotInVaultArea;

            var rejected = SubmitAndRun("k", false, Stored(ring));
            Assert.AreEqual(SuitTransferState.Rejected, rejected.State, "precondition: rejected");

            world.GateError = MarketError.None;

            var replay = Submit("k", false, Stored(ring));
            Assert.AreEqual(MarketError.None, replay.Error);
            Assert.AreEqual(rejected.TransferId, replay.TransferId);
            Assert.AreEqual(0, world.WorldQueue.Count, "the replay starts nothing");

            world.Pump();
            Assert.AreEqual(SuitTransferState.Rejected, service.GetStatus(Account, replay.TransferId).State);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));

            var fresh = SubmitAndRun("k2", false, Stored(ring));
            Assert.AreNotEqual(rejected.TransferId, fresh.TransferId);
            Assert.AreEqual(SuitTransferOutcome.Delivered, fresh.Items.Single().Outcome, "control: a fresh key runs");
        }

        // ---- second review round ----

        /// <summary>
        /// The diagnostics (the logging that reads the withdrawn objects) now run after Apply in their own guarded
        /// region: a throw there on unit 1 of a 3-unit ledger line changes nothing - all three units move.
        /// </summary>
        [TestMethod]
        public void ThrowInTheDiagnostics_OnUnitOne_AllThreeUnitsStillMove()
        {
            SeedLedger(RingWcid, 5);

            var calls = 0;
            service.DiagnoseFaultForTests = () =>
            {
                if (++calls == 1)
                    throw new InvalidOperationException("diagnostics throw");
            };

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 3));

            Assert.AreEqual(3, calls, "precondition: the diagnostics ran for every unit, and threw on the first");
            Assert.AreEqual(3, world.WithdrawCalls);
            Assert.AreEqual(3, world.Delivered.Count);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items.Single().Outcome);
            Assert.AreEqual(MarketError.None, status.Reason);
            Assert.AreEqual(2L, LedgerCount(RingWcid));
        }

        /// <summary>
        /// Apply itself throwing on unit 1 of 3: the fallback COUNTS the delivered unit and does not end the line
        /// early, so units 2 and 3 still move and the line ends Delivered with all three counted.
        /// </summary>
        [TestMethod]
        public void ThrowInApply_OnUnitOne_TheFallbackCountsIt_AndTheLineCarriesOn()
        {
            SeedLedger(RingWcid, 5);

            var calls = 0;
            service.ApplyFaultForTests = () =>
            {
                if (++calls == 1)
                    throw new InvalidOperationException("apply throws");
            };

            var status = SubmitAndRun("k", false, Ledger(RingWcid, 3));

            Assert.AreEqual(3, world.WithdrawCalls, "the line is not ended by unit 1's fallback");
            Assert.AreEqual(3, world.Delivered.Count);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items.Single().Outcome, "all three counted, so the line ends delivered");
            Assert.IsNull(status.Items.Single().Message, "no 'Moved k of n' partial note");
        }

        /// <summary>A step that cannot be scheduled stops the transfer at once instead of waiting for the watchdog.</summary>
        [TestMethod]
        public void ScheduleThrows_BeforeTheFirstStep_StopsTheTransfer_NothingWithdrawn()
        {
            var ring = SeedStored(RingWcid);
            world.ScheduleThrows = n => true;

            var status = SubmitAndRun("k", false, Stored(ring));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));
            Assert.AreEqual(MarketError.None, Submit("k2", false, Stored(ring)).Error, "the account is not wedged");
        }

        [TestMethod]
        public void ScheduleThrows_MidRun_StopsTheRest_DeliveredStayDelivered()
        {
            var ring = SeedStored(RingWcid);
            var armor = SeedStored(ArmorWcid);
            world.ScheduleThrows = n => n == 2;

            var status = SubmitAndRun("k", false, Stored(ring), Stored(armor));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.IsTrue(InVault(armor));
        }

        /// <summary>The cold-vault poll that cannot be scheduled rejects the transfer: nothing was withdrawn.</summary>
        [TestMethod]
        public void ScheduleThrows_ForTheColdVaultPoll_IsRejected()
        {
            var ring = SeedStored(RingWcid);
            FakeVaultWorld.SetInventoryLoaded(vaultContainer, false);
            world.ScheduleThrows = n => true;

            var submitted = Submit("k", false, Stored(ring));
            world.Pump();

            var status = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(1, world.ScheduleCalls, "precondition: the poll was the call that threw");
            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.AreEqual(0, world.WithdrawCalls);
        }

        // ---- shared mules ----

        private void SeedOwnerLedger(uint wcid, long count) =>
            ownerBackend.Stacks.Add(new AccountVaultStack { Id = (uint)(7600 + ownerBackend.Stacks.Count), AccountId = OwnerAccount, Wcid = wcid, Count = count });

        private long OwnerLedgerCount(uint wcid) => ownerBackend.Stacks.Where(s => s.AccountId == OwnerAccount && s.Wcid == wcid).Sum(s => s.Count);

        private WorldObject SeedOwnerStored(uint wcid)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 1);
            Assert.IsTrue(ownerContainer.TryAddToInventory(item), "could not seed the owner's stored item");
            return item;
        }

        private bool InOwnerVault(WorldObject item) => ownerContainer.Inventory.ContainsKey(item.Guid);

        private SuitTransferLine SharedLedger(uint wcid, int count, uint? grantId = null) => new SuitTransferLine(null, wcid, null, count, grantId ?? grant.Id);

        private SuitTransferLine SharedStored(WorldObject item) => new SuitTransferLine(item.Guid.Full, item.WeenieClassId, null, 1, grant.Id);

        /// <summary>
        /// Revoked between Submit and Begin: the line is refused at Begin and nothing is withdrawn. The CALLER'S
        /// own vault holds the same wcid, so a line that lost its grant but was not refused would fall through to
        /// the caller's own store and move the wrong item - the assertion on the own ledger is what catches that.
        /// </summary>
        [TestMethod]
        public void Shared_RevokedBetweenSubmitAndBegin_IsRefused_NothingWithdrawnFromEitherVault()
        {
            SeedOwnerLedger(RingWcid, 5);
            SeedLedger(RingWcid, 5);

            var submitted = Submit("k", false, SharedLedger(RingWcid, 2));
            Assert.AreEqual(MarketError.None, submitted.Error);

            ownerBackend.Grants.Clear();
            world.Pump();

            var line = service.GetStatus(Account, submitted.TransferId).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Moved 0 of 2. That shared mule is no longer shared with you.", line.Message);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.AreEqual(5L, OwnerLedgerCount(RingWcid));
            Assert.AreEqual(5L, LedgerCount(RingWcid), "the caller's own vault is never the fallback for a shared line");
        }

        /// <summary>Revoked after unit 1 of 3: unit 2 is refused by TryWithdraw's own access check, and unit 1 stays delivered.</summary>
        [TestMethod]
        public void Shared_RevokedAfterUnitOne_TheNextUnitIsRefused_TheDeliveredUnitStays()
        {
            SeedOwnerLedger(RingWcid, 5);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                ownerBackend.Grants.Clear();
                return true;
            };

            var status = SubmitAndRun("k", false, SharedLedger(RingWcid, 3));
            var line = status.Items.Single();

            Assert.AreEqual(1, world.Delivered.Count, "unit 1 stays delivered");
            Assert.AreEqual(2, world.WithdrawCalls, "unit 2 was attempted and refused by the vault; unit 3 never");
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Moved 1 of 3. Granter's mule is no longer shared with you.", line.Message);
            Assert.AreEqual(4L, OwnerLedgerCount(RingWcid));
            Assert.AreEqual(1, ownerBackend.GranteeGrantReads, "the grant is resolved once, at Begin; each unit relies on TryWithdraw");
        }

        /// <summary>Downgraded to deposit-only mid-run: refused the same way, by the vault's withdraw-access refusal.</summary>
        [TestMethod]
        public void Shared_DowngradedToDepositOnlyMidRun_IsRefused()
        {
            SeedOwnerLedger(RingWcid, 5);

            world.Deliver = item =>
            {
                world.Delivered.Add(item);
                grant.CanWithdraw = false;
                return true;
            };

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 3)).Items.Single();

            Assert.AreEqual(1, world.Delivered.Count);
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Moved 1 of 3. Granter's mule is no longer shared with you.", line.Message);
            Assert.AreEqual(4L, OwnerLedgerCount(RingWcid));
        }

        [TestMethod]
        public void Shared_DepositOnlyBeforeBegin_IsRefusedAtThePlan_NothingWithdrawn()
        {
            SeedOwnerLedger(RingWcid, 5);
            grant.CanWithdraw = false;

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Granter's mule is no longer shared with you.", line.Message);
            Assert.AreEqual(0, world.WithdrawCalls);
        }

        /// <summary>
        /// A grant to ANOTHER character on the caller's account (an alt) does not cover this transfer's character.
        /// The grant read here returns every row unfiltered, so it is Begin's own grantee check that refuses
        /// (its wording names no mule), not the read's WHERE clause or the plan's access check behind it.
        /// </summary>
        [TestMethod]
        public void Shared_AGrantToADifferentCharacterOnTheSameAccount_IsRefused()
        {
            SeedOwnerLedger(RingWcid, 5);
            grant.GranteeCharacterGuid = AltCharacter;
            world.GrantsForGrantee = _ => ownerBackend.Grants.ToList();

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That shared mule is no longer shared with you.", line.Message, "refused at Begin, before the store is consulted");
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.AreEqual(5L, OwnerLedgerCount(RingWcid));
            Assert.IsFalse(world.GetStoreCalls.Contains(OwnerAccount) || world.ExistingStoreCalls.Contains(OwnerAccount), "the owner's store is never even looked up");
        }

        [TestMethod]
        public void Shared_AGrantOwnedByTheCallersOwnAccount_IsRefused()
        {
            SeedLedger(RingWcid, 5);
            world.GrantsForGrantee = _ => new List<AccountVaultGrant>
            {
                new AccountVaultGrant { Id = 90, OwnerAccountId = Account, GranteeCharacterGuid = Character, CanWithdraw = true },
            };

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1, 90)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.AreEqual(5L, LedgerCount(RingWcid));
        }

        [TestMethod]
        public void Shared_AFailedGrantRead_RefusesTheSharedLines_TheOwnLineStillMoves()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            world.GrantsForGrantee = _ => null;

            var status = SubmitAndRun("k", false, Stored(ring), SharedLedger(ArmorWcid, 1));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(SuitTransferService.SharedUnavailableMessage, status.Items[1].Message, "a failed read is unavailable, never 'not shared'");
            Assert.AreEqual(5L, OwnerLedgerCount(ArmorWcid));
        }

        /// <summary>
        /// allow_listed=true never applies to a shared line: through the REAL market over the real owner store, the
        /// line is refused, nothing leaves the owner's vault, and the owner's listing stays Active.
        /// </summary>
        [TestMethod]
        public void Shared_ListedLine_WithAllowListed_IsRefused_AndTheListingStands()
        {
            var savedPreWithdraw = AccountVaultStore.PreWithdrawHook;
            var savedIsListed = AccountVaultStore.IsListedHook;
            var savedWeenieLookup = MarketManager.WeenieLookup;

            try
            {
                MarketManagerTests.SeedMarketTunables();
                MarketManager.WeenieLookup = _ => null;

                var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640);
                Assert.IsTrue(ownerContainer.TryAddToInventory(bag));

                MarketManager.Initialize(new VaultMarketItemStore(a => a == OwnerAccount ? ownerStore : a == Account ? store : null), new FakeMarketWallet(), new FakeMarketRepository());
                AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
                AccountVaultStore.IsListedHook = MarketManager.IsListed;

                var listed = MarketManager.List(new MarketActor(OwnerAccount, GranterCharacter, "Granter"), bag.Guid.Full, BagWcid, 1, 5, MarketChannel.Web);
                Assert.IsTrue(listed.Ok, $"precondition: the owner's item lists ({listed.Error})");

                world.ListedFor = (account, row) => MarketManager.SuitListedCount(account, row);

                var line = SubmitAndRun("k", true, SharedStored(bag)).Items.Single();

                Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
                Assert.AreEqual(SuitTransferService.SharedListedMessage, line.Message);
                Assert.AreEqual(0, world.WithdrawCalls);
                Assert.IsTrue(InOwnerVault(bag));
                Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listed.Value.Id).Status, "the owner's listing is untouched");
                Assert.IsTrue(world.ListedAccounts.All(a => a == OwnerAccount), "the listing question is asked of the OWNER's account");
            }
            finally
            {
                MarketManager.Shutdown();
                AccountVaultStore.PreWithdrawHook = savedPreWithdraw;
                AccountVaultStore.IsListedHook = savedIsListed;
                MarketManager.WeenieLookup = savedWeenieLookup;
            }
        }

        [TestMethod]
        public void Shared_PartlyListedLedger_WithAllowListed_OnlyTheFreeUnitsMove()
        {
            SeedOwnerLedger(RingWcid, 5);
            world.ListedFor = (account, row) => account == OwnerAccount && row.Kind == VaultEntryKind.Ledger ? 2 : (int?)null;

            var free = SubmitAndRun("k1", true, SharedLedger(RingWcid, 3)).Items.Single();
            Assert.AreEqual(SuitTransferOutcome.Delivered, free.Outcome);

            var intoListed = SubmitAndRun("k2", true, SharedLedger(RingWcid, 1)).Items.Single();
            Assert.AreEqual(SuitTransferOutcome.Refused, intoListed.Outcome);
            Assert.AreEqual(SuitTransferService.SharedPartListedMessage, intoListed.Message);
            Assert.AreEqual(2L, OwnerLedgerCount(RingWcid));
        }

        /// <summary>A unit that cannot be delivered goes back to the OWNER's mule, never to the caller's vault.</summary>
        [TestMethod]
        public void Shared_FailedDelivery_ReturnsToTheOwnersStore()
        {
            SeedOwnerLedger(RingWcid, 5);
            world.Deliver = item => false;

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Returned, line.Outcome);
            StringAssert.Contains(line.Message, "it was left in Granter's mule");
            Assert.AreEqual(5L, OwnerLedgerCount(RingWcid), "the owner's count is restored");
            Assert.AreEqual(0L, LedgerCount(RingWcid), "nothing was credited to the caller's own vault");
            Assert.AreEqual(1, world.WithdrawCalls);
        }

        /// <summary>Owner ruling: an item bonded AFTER the plan is refused at the step, from the live object, and stays in the mule.</summary>
        [TestMethod]
        public void Shared_ItemBondedAfterThePlan_IsRefusedAtTheStep_AndStaysInTheMule()
        {
            var ring = SeedOwnerStored(RingWcid);
            WarmOwner();

            var planned = false;

            world.BeforeStep = () =>
            {
                planned = true;
                ring.Bonded = BondedStatus.Bonded;
            };

            var line = SubmitAndRun("k", false, SharedStored(ring)).Items.Single();

            Assert.IsTrue(planned, "precondition: the item was bonded between the plan and the step");
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That item is bonded and can't leave Granter's mule.", line.Message);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InOwnerVault(ring));
        }

        /// <summary>An item already bonded (or attuned) at the plan is refused there; the own vault's bonded item still moves.</summary>
        [TestMethod]
        public void Shared_BondedOrAttunedAtThePlan_IsRefused_TheOwnBondedItemMoves()
        {
            var bonded = SeedOwnerStored(RingWcid);
            bonded.Bonded = BondedStatus.Sticky;
            var attuned = SeedOwnerStored(ArmorWcid);
            attuned.Attuned = AttunedStatus.Attuned;
            var mine = SeedStored(RingWcid);
            mine.Bonded = BondedStatus.Bonded;
            WarmOwner();

            var steps = 0;
            world.BeforeStep = () => steps++;

            var status = SubmitAndRun("k", false, SharedStored(bonded), SharedStored(attuned), Stored(mine));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[0].Outcome);
            Assert.AreEqual("That item is bonded and can't leave Granter's mule.", status.Items[0].Message);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[2].Outcome, "the own vault keeps moving bonded items");
            Assert.AreEqual(1, world.WithdrawCalls, "only the own line was withdrawn");
            Assert.AreEqual(1, steps, "refused at the plan: only the own line reached a step");
            Assert.IsTrue(InOwnerVault(bonded));
            Assert.IsTrue(InOwnerVault(attuned));
        }

        /// <summary>
        /// A shared group member skips the group-member pre-withdraw report (which would close the owner's group
        /// listing) and asks the non-owner listing guard instead, about the OWNER's account and the representative.
        /// </summary>
        [TestMethod]
        public void Shared_GroupMember_SkipsTheGroupHook_AndAsksTheGuardAboutTheRepresentative()
        {
            var savedGuard = AccountVaultStore.WithdrawWouldDelistHook;
            var asked = new List<(uint Account, uint? Guid)>();

            var bags = new List<WorldObject>();

            for (var i = 0; i < 3; i++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640 + i);
                Assert.IsTrue(ownerContainer.TryAddToInventory(bag));
                bags.Add(bag);
            }

            var group = ownerStore.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(group.IsGroup, "precondition: one group row in the owner's vault");

            try
            {
                AccountVaultStore.WithdrawWouldDelistHook = (account, guid, wcid, amount, classId) =>
                {
                    asked.Add((account, guid));
                    return null;
                };

                var line = SubmitAndRun("k", false, SharedStored(group.Members[2])).Items.Single();

                Assert.AreEqual(SuitTransferOutcome.Delivered, line.Outcome);
                Assert.AreEqual(0, world.GroupNotices.Count, "a shared line never reports the group-member withdraw");
                CollectionAssert.Contains(asked, (OwnerAccount, (uint?)group.Guid.Full), "the guard is asked about the OWNER's account and the group's representative");
            }
            finally
            {
                AccountVaultStore.WithdrawWouldDelistHook = savedGuard;
            }
        }

        /// <summary>
        /// F5: the owner lists a stored GROUP after the plan and a NON-representative member is requested. The step's
        /// listed check is blinded here (ListedFor answers null) to model the listing landing after it, so the guard
        /// under the representative is the only thing standing between the step and the owner's listing. The unit is
        /// refused and the listing stays Active.
        /// </summary>
        [TestMethod]
        public void Shared_GroupListedAfterThePlan_ANonRepresentativeMember_IsRefused_AndTheListingStands()
        {
            for (var i = 0; i < 3; i++)
                Assert.IsTrue(ownerContainer.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640 + i)));

            var group = ownerStore.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(group.IsGroup, "precondition: one group row in the owner's vault");

            var member = group.Members.First(m => m.Guid.Full != group.Guid.Full);
            WarmOwner();

            var savedGuard = AccountVaultStore.WithdrawWouldDelistHook;

            WithRealMarket(() =>
            {
                try
                {
                    AccountVaultStore.WithdrawWouldDelistHook = MarketManager.WithdrawWouldDelist;
                    world.ListedFor = (account, row) => null;
                    world.UseLiveGroupHook = true;

                    MarketResult<ACE.Server.Managers.Market.MarketListing> listed = null;

                    world.BeforeStep = () =>
                    {
                        if (listed != null)
                            return;

                        listed = MarketManager.List(new MarketActor(OwnerAccount, GranterCharacter, "Granter"), group.Guid.Full, BagWcid, 1, 5, MarketChannel.Web);
                        Assert.IsTrue(listed.Ok, $"precondition: the owner lists the group after the plan ({listed.Error})");
                    };

                    var line = SubmitAndRun("k", true, SharedStored(member)).Items.Single();

                    Assert.IsNotNull(listed, "precondition: the listing was made between the plan and the step");
                    Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome, line.Message);
                    Assert.AreEqual(SuitTransferService.SharedListedMessage, line.Message);
                    Assert.AreEqual(0, world.WithdrawCalls);
                    Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listed.Value.Id).Status);
                }
                finally
                {
                    AccountVaultStore.WithdrawWouldDelistHook = savedGuard;
                }
            });
        }

        [TestMethod]
        public void Shared_MixedOwnAndSharedJob_MovesBoth_FromTheirOwnVaults()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            SeedLedger(ArmorWcid, 4);

            var status = SubmitAndRun("k", false, Stored(ring), SharedLedger(ArmorWcid, 2));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[1].Outcome);
            Assert.IsFalse(InVault(ring));
            Assert.AreEqual(3L, OwnerLedgerCount(ArmorWcid), "the shared units came out of the owner's mule");
            Assert.AreEqual(4L, LedgerCount(ArmorWcid), "and not out of the caller's own row of the same wcid");
            Assert.IsNull(status.Items[0].VaultKey);
            Assert.AreEqual("s:77", status.Items[1].VaultKey);
            Assert.IsTrue(world.Told.Any(t => t == "Armor moved from Granter's mule to your pack." || t.EndsWith(" moved from Granter's mule to your pack.", StringComparison.Ordinal)));
        }

        /// <summary>A cold SHARED store is waited for exactly like the own one, within the same bounded wait.</summary>
        [TestMethod]
        public void Shared_ColdOwnerStore_IsWaitedFor_ThenRuns()
        {
            SeedOwnerLedger(RingWcid, 5);
            FakeVaultWorld.SetInventoryLoaded(ownerContainer, false);

            var submitted = Submit("k", false, SharedLedger(RingWcid, 1));

            world.RunWorldQueueOnly();
            Assert.AreEqual(SuitTransferState.Running, service.GetStatus(Account, submitted.TransferId).State, "still waiting, not refused");
            Assert.AreEqual(0, world.WithdrawCalls);

            FakeVaultWorld.SetInventoryLoaded(ownerContainer, true);
            world.Pump();

            Assert.AreEqual(SuitTransferOutcome.Delivered, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
            Assert.AreEqual(4L, OwnerLedgerCount(RingWcid));
        }

        [TestMethod]
        public void Shared_ColdOwnerStore_PastTheWait_RefusesItsLines_TheOwnLineStillMoves()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            FakeVaultWorld.SetInventoryLoaded(ownerContainer, false);

            var status = SubmitAndRun("k", false, Stored(ring), SharedLedger(ArmorWcid, 1));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            StringAssert.Contains(status.Items[1].Message, "Granter's mule took too long to load");
            Assert.AreEqual(5L, OwnerLedgerCount(ArmorWcid));
        }

        [TestMethod]
        public void Shared_ItemGoneFromTheMule_IsRefused_WithTheMulesName()
        {
            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That item is no longer in Granter's mule.", line.Message);
        }

        // ---- review round 1: the step-time listed guard, warm-only shared stores, per-line shared failures ----

        /// <summary>Loads the owner's store now (as a warm-up would), so Begin plans on its first run and the first scheduled action is a step.</summary>
        private void WarmOwner() => Assert.IsTrue(ownerStore.TryCheckReady(out var reason), $"precondition: the owner's store warms ({reason})");

        /// <summary>
        /// Runs <paramref name="body"/> with a REAL market over the real owner and caller stores and the live pre-withdraw
        /// hook, so a withdraw that should not happen would visibly delist.
        /// </summary>
        private void WithRealMarket(Action body)
        {
            var savedPreWithdraw = AccountVaultStore.PreWithdrawHook;
            var savedIsListed = AccountVaultStore.IsListedHook;
            var savedWeenieLookup = MarketManager.WeenieLookup;

            try
            {
                MarketManagerTests.SeedMarketTunables();
                MarketManager.WeenieLookup = _ => null;
                MarketManager.Initialize(new VaultMarketItemStore(a => a == OwnerAccount ? ownerStore : a == Account ? store : null), new FakeMarketWallet(), new FakeMarketRepository());
                AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
                AccountVaultStore.IsListedHook = MarketManager.IsListed;
                world.ListedFor = (account, row) => MarketManager.SuitListedCount(account, row);

                body();
            }
            finally
            {
                MarketManager.Shutdown();
                AccountVaultStore.PreWithdrawHook = savedPreWithdraw;
                AccountVaultStore.IsListedHook = savedIsListed;
                MarketManager.WeenieLookup = savedWeenieLookup;
            }
        }

        /// <summary>
        /// The owner lists a shared stored item AFTER the plan and before the step: the plan saw no listing, so only the
        /// STEP's guard stands between allow_listed=true and a withdraw that would take the owner's listing down.
        /// </summary>
        [TestMethod]
        public void Shared_StoredItemListedAfterThePlan_WithAllowListed_IsRefusedAtTheStep_AndTheListingStands()
        {
            var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640);
            Assert.IsTrue(ownerContainer.TryAddToInventory(bag));
            WarmOwner();

            WithRealMarket(() =>
            {
                MarketResult<ACE.Server.Managers.Market.MarketListing> listed = null;

                world.BeforeStep = () =>
                {
                    if (listed != null)
                        return;

                    listed = MarketManager.List(new MarketActor(OwnerAccount, GranterCharacter, "Granter"), bag.Guid.Full, BagWcid, 1, 5, MarketChannel.Web);
                    Assert.IsTrue(listed.Ok, $"precondition: the owner lists after the plan ({listed.Error})");
                };

                var line = SubmitAndRun("k", true, SharedStored(bag)).Items.Single();

                Assert.IsNotNull(listed, "precondition: the listing was made between the plan and the step");
                Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
                Assert.AreEqual(SuitTransferService.SharedListedMessage, line.Message);
                Assert.AreEqual(0, world.WithdrawCalls);
                Assert.IsTrue(InOwnerVault(bag));
                Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listed.Value.Id).Status);
            });
        }

        [TestMethod]
        public void Shared_LedgerRowListedAfterThePlan_WithAllowListed_IsRefusedAtTheStep_AndTheListingStands()
        {
            SeedOwnerLedger(RingWcid, 5);
            WarmOwner();

            WithRealMarket(() =>
            {
                MarketResult<ACE.Server.Managers.Market.MarketListing> listed = null;

                world.BeforeStep = () =>
                {
                    if (listed != null)
                        return;

                    listed = MarketManager.List(new MarketActor(OwnerAccount, GranterCharacter, "Granter"), null, RingWcid, 5, 5, MarketChannel.Web);
                    Assert.IsTrue(listed.Ok, $"precondition: the owner lists the whole row after the plan ({listed.Error})");
                };

                var line = SubmitAndRun("k", true, SharedLedger(RingWcid, 2)).Items.Single();

                Assert.IsNotNull(listed);
                Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
                StringAssert.Contains(line.Message, SuitTransferService.SharedListedMessage);
                Assert.AreEqual(0, world.WithdrawCalls);
                Assert.AreEqual(5L, OwnerLedgerCount(RingWcid));
                Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listed.Value.Id).Status);
            });
        }

        /// <summary>The step asks the listing question of the OWNER's account: a listing answered only for the owner refuses.</summary>
        [TestMethod]
        public void Shared_StepListingCheck_AsksTheOwnersAccount()
        {
            var ring = SeedOwnerStored(RingWcid);
            WarmOwner();

            var planned = false;
            world.ListedFor = (account, row) => planned && account == OwnerAccount ? 1 : (int?)null;
            world.BeforeStep = () => planned = true;

            var line = SubmitAndRun("k", true, SharedStored(ring)).Items.Single();

            Assert.IsTrue(planned, "precondition: a step ran");
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InOwnerVault(ring));
        }

        /// <summary>
        /// At the PLAN, a listed shared line with allow_listed=true is refused as a line before the pack is sized, so a
        /// full pack does not turn it into a whole-job pack_full.
        /// </summary>
        [TestMethod]
        public void Shared_ListedLineAtThePlan_WithAllowListedAndAFullPack_IsRefused_NotPackFull()
        {
            var ring = SeedOwnerStored(RingWcid);
            world.ListedFor = (account, row) => account == OwnerAccount ? 1 : (int?)null;
            world.FreeSlots = 0;

            var status = SubmitAndRun("k", true, SharedStored(ring));

            Assert.AreEqual(SuitTransferState.Completed, status.State, "not rejected as pack_full");
            Assert.AreEqual(MarketError.None, status.Reason);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
            Assert.AreEqual(SuitTransferService.SharedListedMessage, status.Items.Single().Message);
            Assert.AreEqual(0, world.WithdrawCalls);
        }

        [TestMethod]
        public void Validate_MoreThanTenSharedMules_IsInvalidTransfer()
        {
            SuitTransferLine[] Lines(int mules) => Enumerable.Range(1, mules).Select(i => SharedLedger(RingWcid, 1, (uint)(100 + i))).ToArray();

            Assert.AreEqual(MarketError.None, SuitTransferService.Validate(new SuitTransferRequest(Account, Character, "k", Lines(VaultGranterName.MaxSharedMules), false)));
            Assert.AreEqual(MarketError.InvalidTransfer, Submit("k", false, Lines(VaultGranterName.MaxSharedMules + 1)).Error);
            Assert.AreEqual(0, world.WorldQueue.Count);

            var sameMuleTwelveTimes = Enumerable.Range(0, 12).Select(_ => SharedLedger(RingWcid, 1)).ToArray();
            Assert.AreEqual(MarketError.None, SuitTransferService.Validate(new SuitTransferRequest(Account, Character, "k", sameMuleTwelveTimes, false)), "the cap counts distinct mules, not lines");
        }

        /// <summary>Begin never cold-loads a shared store on the world thread: it queues a warm-up and waits.</summary>
        [TestMethod]
        public void Shared_ColdOwnerStore_IsQueuedForWarming_NeverLoadedByBegin()
        {
            SeedOwnerLedger(RingWcid, 5);
            world.DrainWarmRequests = false;

            var submitted = Submit("k", false, SharedLedger(RingWcid, 1));
            world.RunWorldQueueOnly();

            Assert.AreEqual(SuitTransferState.Running, service.GetStatus(Account, submitted.TransferId).State, "waiting, not refused");
            CollectionAssert.Contains(world.WarmRequests, OwnerAccount);
            Assert.AreEqual(0, ownerBackend.StackReads, "nothing read the owner's ledger: no load ran on the world thread");
            Assert.IsFalse(ownerStore.IsWarm);

            // The warm queue serves it; the next poll proceeds.
            Assert.IsTrue(ownerStore.TryCheckReady(out _));
            world.Pump();

            Assert.AreEqual(SuitTransferOutcome.Delivered, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
        }

        /// <summary>A shared unit the store refuses for its own reasons is worded for the grantee, never "Your vault holds only N".</summary>
        [TestMethod]
        public void Shared_AStoreRefusal_IsWordedAsNoLongerInTheMule()
        {
            SeedOwnerLedger(RingWcid, 1);
            world.AmountBump = 5;   // the core asks the store for 6 of a row of 1: the store refuses with its owner wording

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1)).Items.Single();

            Assert.AreEqual(1, world.WithdrawCalls);
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That item is no longer in Granter's mule.", line.Message);
            Assert.AreEqual(1L, OwnerLedgerCount(RingWcid));
        }

        /// <summary>A shared store that stops being ready mid-run refuses ITS line only; the caller's own line still moves.</summary>
        [TestMethod]
        public void Shared_StoreNotReadyMidRun_RefusesThatLineOnly_TheRestCarriesOn()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            WarmOwner();
            world.DrainWarmRequests = false;

            world.Deliver = item =>
            {
                world.Delivered.Add(item);

                if (world.Delivered.Count == 1)
                    FakeVaultWorld.SetInventoryLoaded(ownerContainer, false);

                return true;
            };

            var status = SubmitAndRun("k", false, SharedLedger(ArmorWcid, 2), Stored(ring));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(MarketError.None, status.Reason, "the transfer was not stopped as a whole");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[0].Outcome);
            Assert.AreEqual("Moved 1 of 2. Granter's mule is unavailable right now, so nothing was moved from it.", status.Items[0].Message);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[1].Outcome);
            Assert.IsFalse(InVault(ring));
            Assert.AreEqual(4L, OwnerLedgerCount(ArmorWcid));

            // The step saw a live but no-longer-warm store: it queued a warm-up rather than asking TryCheckReady, which
            // would reload synchronously on the step's thread.
            CollectionAssert.AreEqual(new[] { OwnerAccount }, world.WarmRequests.ToArray());
        }

        // ---- review round 2: never build a shared store, fail fast on a store that cannot load ----

        /// <summary>A cold (not live) owner store is never built by Begin: it is queued for warming and waited for.</summary>
        [TestMethod]
        public void Shared_ColdOwnerWithNoLiveStore_BeginBuildsNothing_AndQueuesAWarmUp()
        {
            SeedOwnerLedger(RingWcid, 5);
            world.ColdOwners.Add(OwnerAccount);
            world.DrainWarmRequests = false;

            var submitted = Submit("k", false, SharedLedger(RingWcid, 1));
            world.RunWorldQueueOnly();

            Assert.AreEqual(SuitTransferState.Running, service.GetStatus(Account, submitted.TransferId).State);
            Assert.AreEqual(0, world.StoreConstructions, "Begin never builds a shared store");
            Assert.IsFalse(world.GetStoreCalls.Contains(OwnerAccount), "GetStore (which builds) is never asked for a shared store");
            CollectionAssert.Contains(world.WarmRequests, OwnerAccount);

            // The warm queue builds and loads it; the next poll proceeds.
            world.ColdOwners.Remove(OwnerAccount);
            Assert.IsTrue(ownerStore.TryCheckReady(out _));
            world.Pump();

            Assert.AreEqual(SuitTransferOutcome.Delivered, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
            Assert.AreEqual(0, world.StoreConstructions);
        }

        /// <summary>
        /// A shared store that FAILS to load (unavailable, not still loading) is refused at the one-time probe, well
        /// before the 20 s wait runs out, and the own line moves.
        /// </summary>
        [TestMethod]
        public void Shared_StoreThatCannotLoad_IsRefusedAtTheProbe_LongBeforeTheWaitEnds_TheOwnLineMoves()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            ownerBackend.FailVaultRead = true;

            Assert.IsFalse(ownerStore.TryCheckReady(out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason, "precondition: the owner's store reports unavailable, not still loading");

            var status = SubmitAndRun("k", false, Stored(ring), SharedLedger(ArmorWcid, 1));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome, "the own line moves");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual("Granter's mule is unavailable right now, so nothing was moved from it.", status.Items[1].Message);

            // Polls are VaultPollSeconds apart: a refusal at the probe is about SharedLoadProbeSeconds of polling, not
            // the VaultMaxWaitSeconds a timeout would take.
            var maxPollsBeforeProbe = (int)Math.Ceiling(SuitTransferService.SharedLoadProbeSeconds / SuitTransferService.VaultPollSeconds) + 1;
            Assert.IsTrue(world.ScheduleCalls <= maxPollsBeforeProbe + 2, $"refused at the probe, not at the 20 s timeout: {world.ScheduleCalls} schedules");
            Assert.AreEqual(5L, OwnerLedgerCount(ArmorWcid));
        }

        // ---- review round 3: the probe reads state only; a pending ledger reload does not refuse a line ----

        /// <summary>
        /// A store whose last load failed is refused at the probe from its LastLoadFailed flag alone: the world thread
        /// makes no further load attempt (no extra vault index read), and the own line still moves.
        /// </summary>
        [TestMethod]
        public void Shared_FailedStore_IsRefusedAtTheProbe_WithNoFurtherLoadAttempt()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            ownerBackend.FailVaultRead = true;
            world.DrainWarmRequests = false;

            Assert.IsFalse(ownerStore.TryCheckReady(out _));
            Assert.IsTrue(ownerStore.LastLoadFailed, "precondition: the failed load set the flag");

            var readsBefore = ownerBackend.VaultReads;

            var status = SubmitAndRun("k", false, Stored(ring), SharedLedger(ArmorWcid, 1));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome, "the own line moves");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual("Granter's mule is unavailable right now, so nothing was moved from it.", status.Items[1].Message);
            Assert.AreEqual(readsBefore, ownerBackend.VaultReads, "the probe never asked the failed store to load again");

            var maxPollsBeforeProbe = (int)Math.Ceiling(SuitTransferService.SharedLoadProbeSeconds / SuitTransferService.VaultPollSeconds) + 1;
            Assert.IsTrue(world.ScheduleCalls <= maxPollsBeforeProbe + 2, $"refused at the probe, not at the 20 s timeout: {world.ScheduleCalls} schedules");
        }

        /// <summary>
        /// LastLoadFailed is cleared by the next successful load, so a store that recovered and is merely not warm for
        /// another reason (a vault inventory still loading) is NOT refused at the probe: it is waited for.
        /// </summary>
        [TestMethod]
        public void Shared_LoadFailedThenSucceeded_ClearsTheFlag_AndTheProbeDoesNotRefuse()
        {
            SeedOwnerLedger(ArmorWcid, 5);
            ownerBackend.FailVaultRead = true;

            Assert.IsFalse(ownerStore.TryCheckReady(out _));
            Assert.IsTrue(ownerStore.LastLoadFailed, "precondition: the failed load set the flag");

            ownerBackend.FailVaultRead = false;

            Assert.IsTrue(ownerStore.TryCheckReady(out var reason), $"precondition: the next load succeeds ({reason})");
            Assert.IsFalse(ownerStore.LastLoadFailed, "a successful load clears the flag");

            FakeVaultWorld.SetInventoryLoaded(ownerContainer, false);
            world.DrainWarmRequests = false;

            var line = SubmitAndRun("k", false, SharedLedger(ArmorWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "took too long to load", "waited out the bounded wait, not refused at the probe");
            Assert.AreEqual(5L, OwnerLedgerCount(ArmorWcid));
        }

        /// <summary>A vault container that could not be READ (not the barrel, not an absent biota) sets the flag, and the probe refuses on it.</summary>
        [TestMethod]
        public void Shared_UnreadableVaultContainer_IsRefusedAtTheProbe_WithNoFurtherLoadAttempt()
        {
            SeedOwnerLedger(ArmorWcid, 5);
            ownerVaultWorld.FailLoadContainerGuids.Add(ownerContainer.Guid.Full);
            world.DrainWarmRequests = false;

            Assert.IsFalse(ownerStore.TryCheckReady(out _));
            Assert.IsTrue(ownerStore.LastLoadFailed, "precondition: an unreadable vault container is a failed load");

            var readsBefore = ownerBackend.VaultReads;

            var line = SubmitAndRun("k", false, SharedLedger(ArmorWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual(SuitTransferService.MuleUnavailableMessage("Granter"), line.Message);
            Assert.AreEqual(readsBefore, ownerBackend.VaultReads, "the probe never asked the failed store to load again");

            var maxPollsBeforeProbe = (int)Math.Ceiling(SuitTransferService.SharedLoadProbeSeconds / SuitTransferService.VaultPollSeconds) + 1;
            Assert.IsTrue(world.ScheduleCalls <= maxPollsBeforeProbe + 2, $"refused at the probe, not at the 20 s timeout: {world.ScheduleCalls} schedules");
        }

        /// <summary>Seeds one stored GROUP of three bags in the owner's vault and returns its row.</summary>
        private VaultEntry SeedOwnerGroup()
        {
            for (var i = 0; i < 3; i++)
                Assert.IsTrue(ownerContainer.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640 + i)));

            var group = ownerStore.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(group.IsGroup, "precondition: one group row in the owner's vault");
            return group;
        }

        /// <summary>Runs one shared group-member transfer with the listing guard answering through <paramref name="guard"/>.</summary>
        private SuitTransferItemStatus RunGroupMemberWithGuard(Func<uint, uint?, uint, int, string, int?> guard, out WorldObject member)
        {
            var group = SeedOwnerGroup();
            member = group.Members.First(m => m.Guid.Full != group.Guid.Full);

            var savedGuard = AccountVaultStore.WithdrawWouldDelistHook;

            try
            {
                AccountVaultStore.WithdrawWouldDelistHook = guard;
                return SubmitAndRun("k", false, SharedStored(member)).Items.Single();
            }
            finally
            {
                AccountVaultStore.WithdrawWouldDelistHook = savedGuard;
            }
        }

        /// <summary>A listing guard that THROWS refuses the shared group member as unavailable; nothing is withdrawn.</summary>
        [TestMethod]
        public void Shared_GroupMember_AGuardThatThrows_IsRefusedAsUnavailable()
        {
            var line = RunGroupMemberWithGuard((a, g, w, n, c) => throw new InvalidOperationException("guard throws"), out var member);

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual(SuitTransferService.MuleUnavailableMessage("Granter"), line.Message);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InOwnerVault(member));
        }

        /// <summary>The guard's "cannot tell" sentinel refuses the shared group member as unavailable; nothing is withdrawn.</summary>
        [TestMethod]
        public void Shared_GroupMember_TheUnavailableSentinel_IsRefusedAsUnavailable()
        {
            var line = RunGroupMemberWithGuard((a, g, w, n, c) => AccountVaultStore.WithdrawGuardUnavailable, out var member);

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual(SuitTransferService.MuleUnavailableMessage("Granter"), line.Message);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InOwnerVault(member));
        }
        /// <summary>A template weenie carrying one int property, for the bonded rule on ledger rows and class lines.</summary>
        private static ACE.Entity.Models.Weenie Template(uint wcid, ACE.Entity.Enum.Properties.PropertyInt property, int value)
            => new ACE.Entity.Models.Weenie
            {
                WeenieClassId = wcid,
                PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int> { [property] = value },
            };

        /// <summary>A shared ledger row whose TEMPLATE is bonded (or attuned) is refused at the plan; the own row of the same wcid moves.</summary>
        [TestMethod]
        public void Shared_LedgerRowWithABondedTemplate_IsRefusedAtThePlan_TheOwnRowMoves()
        {
            SeedOwnerLedger(RingWcid, 5);
            SeedOwnerLedger(ArmorWcid, 5);
            SeedLedger(RingWcid, 4);
            templates[RingWcid] = Template(RingWcid, ACE.Entity.Enum.Properties.PropertyInt.Bonded, (int)BondedStatus.Bonded);
            templates[ArmorWcid] = Template(ArmorWcid, ACE.Entity.Enum.Properties.PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            WarmOwner();

            var steps = 0;
            world.BeforeStep = () => steps++;

            var status = SubmitAndRun("k", false, SharedLedger(RingWcid, 1), SharedLedger(ArmorWcid, 1), Ledger(RingWcid, 1));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[0].Outcome);
            Assert.AreEqual("That item is bonded and can't leave Granter's mule.", status.Items[0].Message);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome, "an attuned template is refused too");
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[2].Outcome, "the own vault keeps moving a bonded template");
            Assert.AreEqual(1, steps, "refused at the plan: only the own line reached a step");
            Assert.AreEqual(5L, OwnerLedgerCount(RingWcid));
            Assert.AreEqual(5L, OwnerLedgerCount(ArmorWcid));
            Assert.AreEqual(3L, LedgerCount(RingWcid));
        }

        /// <summary>A template that becomes bonded after the plan (a world-data reload) refuses the shared row's next unit at the step.</summary>
        [TestMethod]
        public void Shared_LedgerTemplateBondedAfterThePlan_IsRefusedAtTheStep()
        {
            SeedOwnerLedger(RingWcid, 5);
            WarmOwner();

            world.BeforeStep = () => templates[RingWcid] = Template(RingWcid, ACE.Entity.Enum.Properties.PropertyInt.Bonded, (int)BondedStatus.Sticky);

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 2)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Moved 0 of 2. That item is bonded and can't leave Granter's mule.", line.Message);
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.AreEqual(5L, OwnerLedgerCount(RingWcid));
        }

        /// <summary>
        /// A debit whose resulting count is unknown, with the ledger re-read failing at that moment, leaves the owner's
        /// store index-loaded but not warm (ledgerReloadPending). The next unit's step falls back to TryCheckReady,
        /// which re-reads the ledger, and the line carries on rather than being refused.
        /// </summary>
        [TestMethod]
        public void Shared_LedgerReloadPendingForOneUnit_TheLineContinues()
        {
            SeedOwnerLedger(ArmorWcid, 5);
            WarmOwner();
            world.DrainWarmRequests = false;

            ownerBackend.LedgerAdjustCountUnknownWcids.Add(ArmorWcid);
            ownerBackend.FailStackRead = true;

            var pendingSeen = false;

            world.Deliver = item =>
            {
                world.Delivered.Add(item);

                if (world.Delivered.Count == 1)
                {
                    pendingSeen = ownerStore.IsIndexLoaded && !ownerStore.IsWarm;
                    ownerBackend.LedgerAdjustCountUnknownWcids.Remove(ArmorWcid);
                    ownerBackend.FailStackRead = false;
                }

                return true;
            };

            var line = SubmitAndRun("k", false, SharedLedger(ArmorWcid, 2)).Items.Single();

            Assert.IsTrue(pendingSeen, "precondition: after unit one the store was index-loaded with a ledger reload pending");
            Assert.AreEqual(SuitTransferOutcome.Delivered, line.Outcome, line.Message);
            Assert.AreEqual(2, world.Delivered.Count);
            Assert.AreEqual(3L, OwnerLedgerCount(ArmorWcid));
            Assert.AreEqual(0, world.WarmRequests.Count, "the step did not treat the pending reload as a cold store");
        }

        /// <summary>A shared store that drops out of memory mid-run is never rebuilt or loaded on the step thread.</summary>
        [TestMethod]
        public void Shared_StoreRetiredMidRun_TheStepBuildsNothing_QueuesAWarmUp_AndRefusesTheLine()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 5);
            WarmOwner();
            world.DrainWarmRequests = false;

            world.Deliver = item =>
            {
                world.Delivered.Add(item);

                if (world.Delivered.Count == 1)
                    world.ColdOwners.Add(OwnerAccount);   // the idle sweep retired the owner's store

                return true;
            };

            var status = SubmitAndRun("k", false, SharedLedger(ArmorWcid, 2), Stored(ring));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[0].Outcome);
            Assert.AreEqual("Moved 1 of 2. Granter's mule is unavailable right now, so nothing was moved from it.", status.Items[0].Message);
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[1].Outcome);
            Assert.AreEqual(0, world.StoreConstructions, "the step never builds a shared store");
            CollectionAssert.Contains(world.WarmRequests, OwnerAccount);
        }

        [TestMethod]
        public void Shared_WarmQueueRefusal_IsLoggedOnce_BuildsNothing_AndTheLineTimesOut()
        {
            SeedOwnerLedger(RingWcid, 5);
            world.ColdOwners.Add(OwnerAccount);
            world.WarmQueueAccepts = false;

            var line = SubmitAndRun("k", false, SharedLedger(RingWcid, 1)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "took too long to load");
            Assert.IsTrue(world.WarmRequests.Count > 1, "precondition: every poll asked again");
            Assert.AreEqual(0, world.StoreConstructions);
            Assert.AreEqual(1, service.WarmRefusalsLogged, "logged once per transfer, not once per poll");
        }

        [TestMethod]
        public void Validate_AZeroGrantId_IsInvalidTransfer()
        {
            Assert.AreEqual(MarketError.InvalidTransfer, Submit("k", false, new SuitTransferLine(null, RingWcid, null, 1, 0)).Error);
            Assert.AreEqual(MarketError.None, SuitTransferService.Validate(new SuitTransferRequest(Account, Character, "k", new[] { SharedLedger(RingWcid, 1) }, false)));
        }

        [TestMethod]
        public void VaultKeys_ParseOnlyTheSharedShape()
        {
            Assert.IsTrue(MarketManager.TryParseVaultKey(null, out var own));
            Assert.IsNull(own, "absent is the own vault");

            Assert.IsTrue(MarketManager.TryParseVaultKey("s:77", out var shared));
            Assert.AreEqual(77u, shared);
            Assert.AreEqual("s:77", MarketManager.SharedVaultKey(77));

            foreach (var bad in new[] { "", "s:", "s:0", "s: 7", "s:+7", "s:7a", "S:7", "x:7", "s:4294967296", "s:00000000001" })
                Assert.IsFalse(MarketManager.TryParseVaultKey(bad, out _), bad);
        }

        /// <summary>
        /// One transfer mixing all three sources: an own-vault stored item, a shared-mule ledger row, and an item in
        /// another of the caller's characters' pack (a holder line). Each source keeps its own scoping: the vault lines
        /// withdraw from their stores, the holder line touches no store and moves its object by re-parenting it.
        /// </summary>
        [TestMethod]
        public void Mixed_OwnVault_Shared_AndHolder_AllDeliver_EachFromItsOwnSource()
        {
            var ring = SeedStored(RingWcid);
            SeedOwnerLedger(ArmorWcid, 3);
            WarmOwner();

            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 94009,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int>
                {
                    { ACE.Entity.Enum.Properties.PropertyInt.ItemType, (int)ItemType.Armor },
                    { ACE.Entity.Enum.Properties.PropertyInt.ValidLocations, (int)EquipMask.ChestArmor },
                },
                PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>
                {
                    { ACE.Entity.Enum.Properties.PropertyString.Name, "Alt Breastplate" },
                },
            };
            var breastplate = new GenericObject(weenie, new ACE.Entity.ObjectGuid(FakeVaultWorld.NextGuid()));

            world.Roster[(Account, AltCharacter)] = "Altsuit";
            world.Holders[AltCharacter] = new SuitHolderState(true, Account, false, false, "Altsuit");
            world.HolderPacks[AltCharacter] = new Dictionary<uint, (WorldObject, SuitHolderReadStatus)>
            {
                [breastplate.Guid.Full] = (breastplate, SuitHolderReadStatus.Found),
            };

            var status = SubmitAndRun("k", false,
                Stored(ring),
                SharedLedger(ArmorWcid, 1),
                new SuitTransferLine(breastplate.Guid.Full, 94009, null, 1, null, AltCharacter));

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.IsTrue(status.Items.All(i => i.Outcome == SuitTransferOutcome.Delivered), string.Join(" | ", status.Items.Select(i => i.Message)));

            Assert.IsFalse(InVault(ring), "the own-vault item left the caller's vault");
            Assert.AreEqual(2L, OwnerLedgerCount(ArmorWcid), "the shared unit left the OWNER's ledger");
            Assert.AreSame(breastplate, world.HolderDelivered.Single(), "the holder item was re-parented, not withdrawn");
            Assert.AreEqual(2, world.WithdrawCalls, "only the two vault lines withdraw");
            Assert.IsFalse(world.HolderPacks[AltCharacter].ContainsKey(breastplate.Guid.Full));
            Assert.AreEqual(0, world.Claimed.Count);

            Assert.IsNull(status.Items[0].HolderGuid);
            Assert.AreEqual("s:77", status.Items[1].VaultKey);
            Assert.IsNull(status.Items[1].HolderGuid);
            Assert.AreEqual(AltCharacter, status.Items[2].HolderGuid);
            Assert.IsNull(status.Items[2].VaultKey);
            Assert.IsFalse(breastplate.IsDestroyed);
        }

        /// <summary>F5: a transfer with ANY vault line keeps the vault wording; only a holder-only one switches.</summary>
        [TestMethod]
        public void Mixed_VaultAndHolder_OutsideTheArea_KeepsTheVaultWording()
        {
            var ring = SeedStored(RingWcid);

            world.Roster[(Account, AltCharacter)] = "Altsuit";
            world.Holders[AltCharacter] = new SuitHolderState(true, Account, false, false, "Altsuit");
            world.GateState = new SuitTransferGateState(false, false, false, false, false, false, false, "the Market Square");

            var status = SubmitAndRun("k", false, Stored(ring), new SuitTransferLine(0x7F4A0001, 94009, null, 1, null, AltCharacter));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.NotInVaultArea, status.Reason);
            Assert.IsTrue(world.GateCharacterMoves.Count > 0 && world.GateCharacterMoves.All(m => !m), "a mixed transfer asks for the vault wording");
            CollectionAssert.Contains(world.Told, "You can only receive items from your vault in the Market Square.");
            Assert.AreEqual(0, world.WithdrawCalls);
            Assert.IsTrue(InVault(ring));
        }
    }

    /// <summary>The suit transfer's world seam, with both queues pumped by hand and the vault store injected.</summary>
    internal sealed class FakeSuitTransferWorld : ISuitTransferWorld
    {
        public DateTime Now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        public AccountVaultStore Store;

        public readonly Dictionary<(uint AccountId, uint CharacterGuid), SuitTransferCharacter> Online = new Dictionary<(uint, uint), SuitTransferCharacter>();

        public bool Eligible = true;
        public MarketError GateError = MarketError.None;
        public int FreeSlots = 100;
        public int FreeContainers = 7;
        public int AvailableBurdenValue = 1_000_000;
        public readonly Dictionary<uint, int> TemplateBurdens = new Dictionary<uint, int>();
        public Func<VaultEntry, int?> Listed = _ => null;

        /// <summary>Added to every amount the service asks for, to simulate a core handing back more than one object.</summary>
        public int AmountBump;

        public Func<WorldObject, bool> Deliver;

        /// <summary>Thrown from CheckGates when set: a fault BEFORE a unit's withdraw.</summary>
        public Exception ThrowFromGates;

        /// <summary>Thrown from WithdrawToPack when set: a fault outside VaultPackDelivery's own handling.</summary>
        public Exception ThrowFromWithdraw;

        /// <summary>Every Tell throws when set: a fault in the reporting AFTER a withdraw.</summary>
        public bool TellThrows;

        public Action<string> OnTell;

        /// <summary>Also runs LiveSuitTransferWorld's own group-member hook body.</summary>
        public bool UseLiveGroupHook;

        public readonly Queue<Action> WorldQueue = new Queue<Action>();
        public readonly Queue<Action> Scheduled = new Queue<Action>();
        public readonly List<WorldObject> Delivered = new List<WorldObject>();
        public readonly List<int> Amounts = new List<int>();
        public readonly List<string> Told = new List<string>();
        public readonly List<(uint AccountId, uint RepresentativeGuid, uint Wcid)> GroupNotices = new List<(uint, uint, uint)>();

        public int WithdrawCalls;

        public FakeSuitTransferWorld()
        {
            Deliver = item => { Delivered.Add(item); return true; };
        }

        public DateTime UtcNow => Now;

        public SuitTransferCharacter FindOnlineCharacter(uint accountId, uint characterGuid) =>
            Online.TryGetValue((accountId, characterGuid), out var c) ? c : null;

        public bool IsStillEligible(SuitTransferCharacter character) => Eligible;

        public void EnqueueWorld(Action work) => WorldQueue.Enqueue(work);

        public void Schedule(SuitTransferCharacter character, double delaySeconds, Action work)
        {
            ScheduleCalls++;

            if (ScheduleThrows != null && ScheduleThrows(ScheduleCalls))
                throw new InvalidOperationException("schedule throws");

            Scheduled.Enqueue(work);
        }

        /// <summary>Given the 1-based Schedule call number; true makes that call throw.</summary>
        public Func<int, bool> ScheduleThrows;

        public int ScheduleCalls;

        /// <summary>When set, CheckGates answers through the real SuitTransferGates.Decide (so its wording is under test).</summary>
        public SuitTransferGateState? GateState;

        /// <summary>The characterMove flag of every CheckGates call, in order.</summary>
        public readonly List<bool> GateCharacterMoves = new List<bool>();

        public MarketError CheckGates(SuitTransferCharacter character, out string message, bool characterMove = false)
        {
            GateCharacterMoves.Add(characterMove);

            if (ThrowFromGates != null)
                throw ThrowFromGates;

            if (GateState.HasValue)
                return SuitTransferGates.Decide(GateState.Value, out message, characterMove);

            message = GateError == MarketError.None ? null : $"gate {GateError}";
            return GateError;
        }

        /// <summary>Further stores by account (a shared mule's owner), beside <see cref="Store"/>.</summary>
        public readonly Dictionary<uint, AccountVaultStore> OtherStores = new Dictionary<uint, AccountVaultStore>();

        public readonly List<uint> GetStoreCalls = new List<uint>();

        /// <summary>
        /// Accounts in <see cref="OtherStores"/> whose store is NOT live yet: GetExistingStore answers null for them, and
        /// GetStore "builds" one (counted in <see cref="StoreConstructions"/>), as AccountVaultManager.GetStore would.
        /// </summary>
        public readonly HashSet<uint> ColdOwners = new HashSet<uint>();

        /// <summary>Stores GetStore had to build (a cold owner asked for through GetStore). Shared lookups must keep this at 0.</summary>
        public int StoreConstructions;

        public AccountVaultStore GetStore(uint accountId)
        {
            GetStoreCalls.Add(accountId);

            if (Store != null && Store.AccountId == accountId)
                return Store;

            if (ColdOwners.Remove(accountId))
                StoreConstructions++;

            return OtherStores.TryGetValue(accountId, out var other) ? other : null;
        }

        public readonly List<uint> ExistingStoreCalls = new List<uint>();

        public AccountVaultStore GetExistingStore(uint accountId)
        {
            ExistingStoreCalls.Add(accountId);

            if (Store != null && Store.AccountId == accountId)
                return Store;

            if (ColdOwners.Contains(accountId))
                return null;

            return OtherStores.TryGetValue(accountId, out var other) ? other : null;
        }

        /// <summary>The grantee-keyed grant read; null answers a read failure. Unset reads nothing (no grants).</summary>
        public Func<uint, List<AccountVaultGrant>> GrantsForGrantee = _ => new List<AccountVaultGrant>();

        public int GranteeGrantReads;

        public List<AccountVaultGrant> GetGrantsForGrantee(uint granteeCharacterGuid)
        {
            GranteeGrantReads++;
            return GrantsForGrantee(granteeCharacterGuid);
        }

        public readonly Dictionary<uint, string> CurrentNames = new Dictionary<uint, string>();

        public string GranterName(AccountVaultGrant grant) =>
            VaultGranterName.Resolve(grant, guid => CurrentNames.TryGetValue(guid, out var name) ? name : null);

        public readonly List<uint> WarmRequests = new List<uint>();

        /// <summary>
        /// True: a warm request is served at once, as the world heartbeat's DrainWarmQueue would (it reads IsLoaded,
        /// which loads). False: nothing drains, so a cold store stays cold.
        /// </summary>
        public bool DrainWarmRequests = true;

        /// <summary>False: the warm queue refuses every request (full).</summary>
        public bool WarmQueueAccepts = true;

        public bool RequestWarm(uint accountId)
        {
            WarmRequests.Add(accountId);

            if (!WarmQueueAccepts)
                return false;

            // The drain builds the store (DrainWarmQueue's storeFor is GetStore) and loads it; not this code's construction.
            if (DrainWarmRequests && OtherStores.TryGetValue(accountId, out var other))
            {
                ColdOwners.Remove(accountId);
                other.TryCheckReady(out _);
            }

            return true;
        }

        /// <summary>Runs between the plan and each step (the character's action queue): a seam to change the world after the plan.</summary>
        public Action BeforeStep;

        public int FreeMainPackSlots(SuitTransferCharacter character) => FreeSlots;

        public int FreeContainerSlots(SuitTransferCharacter character) => FreeContainers;

        public int AvailableBurden(SuitTransferCharacter character) => AvailableBurdenValue;

        public int TemplateBurden(uint wcid) => TemplateBurdens.TryGetValue(wcid, out var b) ? b : 0;

        /// <summary>When set, answers ListedCount with the account it was asked about (a real market's SuitListedCount).</summary>
        public Func<uint, VaultEntry, int?> ListedFor;

        public readonly List<uint> ListedAccounts = new List<uint>();

        public int? ListedCount(uint accountId, VaultEntry row)
        {
            ListedAccounts.Add(accountId);
            return ListedFor != null ? ListedFor(accountId, row) : Listed(row);
        }

        public void BeforeGroupMemberWithdraw(uint accountId, uint representativeGuid, uint wcid)
        {
            GroupNotices.Add((accountId, representativeGuid, wcid));

            // The live body (it only reads AccountVaultStore.PreWithdrawHook), for a test wired to a real market.
            if (UseLiveGroupHook)
                LiveSuitTransferWorld.Instance.BeforeGroupMemberWithdraw(accountId, representativeGuid, wcid);
        }

        public VaultPackDeliveryResult WithdrawToPack(SuitTransferCharacter character, AccountVaultStore store, VaultEntry entry, int amount, bool isLedger)
        {
            WithdrawCalls++;
            Amounts.Add(amount);

            if (ThrowFromWithdraw != null)
                throw ThrowFromWithdraw;

            return VaultPackDelivery.WithdrawAndDeliver(store, entry, amount + AmountBump, isLedger,
                new VaultActor(character.AccountId, character.CharacterGuid, character.Name), null, item => Deliver(item));
        }

        public void Tell(SuitTransferCharacter character, string text)
        {
            Told.Add(text);
            OnTell?.Invoke(text);

            if (TellThrows)
                throw new InvalidOperationException("tell throws");
        }

        // ---- holder lines (SuitTransferHolderTests) ----

        public bool CharacterMoves = true;

        public bool CharacterMovesEnabled => CharacterMoves;

        /// <summary>The account roster: (account, character) -> name. A pair not in here is not on that account's roster.</summary>
        public readonly Dictionary<(uint AccountId, uint CharacterGuid), string> Roster = new Dictionary<(uint, uint), string>();

        public bool TryResolveRosterCharacter(uint accountId, uint characterGuid, out string name)
        {
            RosterReads++;
            return Roster.TryGetValue((accountId, characterGuid), out name);
        }

        public int RosterReads;

        /// <summary>PlayerManager's view of each holder. Absent is "no such character".</summary>
        public readonly Dictionary<uint, SuitHolderState> Holders = new Dictionary<uint, SuitHolderState>();

        public SuitHolderState GetHolderState(uint holderGuid)
        {
            HolderStateReads.Add(holderGuid);
            StateReadWhileClaimed.Add(Claimed.Contains(holderGuid));
            return Holders.TryGetValue(holderGuid, out var state) ? state : new SuitHolderState(false, 0, false, false, null);
        }

        public readonly List<uint> HolderStateReads = new List<uint>();

        /// <summary>Per GetHolderState call: was the holder claimed at that moment (the claim must come first).</summary>
        public readonly List<bool> StateReadWhileClaimed = new List<bool>();

        /// <summary>Holders whose login is in flight (SuitHolderMoveGuard's entering mark): a claim on one fails.</summary>
        public readonly HashSet<uint> EnteringWorld = new HashSet<uint>();

        public readonly HashSet<uint> Claimed = new HashSet<uint>();
        public readonly HashSet<uint> Pinned = new HashSet<uint>();
        public readonly List<string> ClaimLog = new List<string>();

        /// <summary>Also drives the real SuitHolderMoveGuard, as LiveSuitTransferWorld does (the test must reset it).</summary>
        public bool UseRealGuard;

        public bool TryClaimHolder(uint holderGuid)
        {
            ClaimLog.Add($"claim {holderGuid:X8}");

            if (EnteringWorld.Contains(holderGuid) || Claimed.Contains(holderGuid) || Pinned.Contains(holderGuid))
                return false;

            if (UseRealGuard && !SuitHolderMoveGuard.TryClaimForMove(holderGuid))
                return false;

            Claimed.Add(holderGuid);
            return true;
        }

        public void ReleaseHolder(uint holderGuid)
        {
            ClaimLog.Add($"release {holderGuid:X8}");
            Claimed.Remove(holderGuid);
            Pinned.Remove(holderGuid);

            if (UseRealGuard)
                SuitHolderMoveGuard.ReleaseMove(holderGuid);
        }

        public void PinHolder(uint holderGuid)
        {
            ClaimLog.Add($"pin {holderGuid:X8}");
            Claimed.Remove(holderGuid);
            Pinned.Add(holderGuid);

            if (UseRealGuard)
                SuitHolderMoveGuard.PinUnconfirmed(holderGuid);
        }

        /// <summary>
        /// Each holder's possessions as the shard row says: item guid -> (object, where). Found means the main pack or a
        /// side pack; Worn a Wielder link. A successful save moves the row out (it now names the target).
        /// </summary>
        public readonly Dictionary<uint, Dictionary<uint, (WorldObject Item, SuitHolderReadStatus Where)>> HolderPacks = new Dictionary<uint, Dictionary<uint, (WorldObject, SuitHolderReadStatus)>>();

        /// <summary>The shard queue: holder reads and saves, FIFO, run by <see cref="Pump"/> unless <see cref="ShardStalled"/>.</summary>
        public readonly Queue<Action> ShardQueue = new Queue<Action>();

        public bool ShardStalled;

        public bool ReadsFail;
        public bool ReadsNeverCallBack;
        public int HolderReads;

        public void ReadHolderItem(uint holderGuid, uint itemGuid, Action<SuitHolderRead> callback)
        {
            HolderReads++;

            ShardQueue.Enqueue(() =>
            {
                if (ReadsNeverCallBack)
                    return;

                if (ReadsFail)
                {
                    callback(SuitHolderRead.Failed("read fails"));
                    return;
                }

                SuitHolderRead read;

                if (!HolderPacks.TryGetValue(holderGuid, out var pack) || !pack.TryGetValue(itemGuid, out var row))
                    read = new SuitHolderRead { Status = SuitHolderReadStatus.NotFound };
                else if (row.Where != SuitHolderReadStatus.Found)
                    read = new SuitHolderRead { Status = row.Where };
                else
                {
                    BuiltFrom[row.Item.Biota.Id] = row.Item;
                    read = new SuitHolderRead { Status = SuitHolderReadStatus.Found, ItemBiota = row.Item.Biota };
                }

                callback(read);
            });
        }

        private readonly Dictionary<uint, WorldObject> BuiltFrom = new Dictionary<uint, WorldObject>();

        /// <summary>Every object this fake handed the service; TestCleanup asserts none was ever Destroy()ed.</summary>
        public readonly List<WorldObject> HolderObjects = new List<WorldObject>();

        public WorldObject CreateHolderObject(ACE.Entity.Models.Biota biota)
        {
            if (biota == null || !BuiltFrom.TryGetValue(biota.Id, out var item))
                return null;

            HolderObjects.Add(item);
            return item;
        }

        public bool AddSucceeds = true;
        public Exception AddThrows;
        public int AddCalls;
        public readonly List<WorldObject> HolderDelivered = new List<WorldObject>();

        public bool AddToPack(SuitTransferCharacter character, WorldObject item)
        {
            AddCalls++;

            if (AddThrows != null)
                throw AddThrows;

            if (!AddSucceeds)
                return false;

            HolderDelivered.Add(item);
            return true;
        }

        public bool SaveResult = true;
        public Exception SaveThrows;
        /// <summary>Stalls the shard queue the moment a save is queued, so a test can look while the save is in flight.</summary>
        public bool StallAfterSave;
        public int SaveCalls;

        public void SaveHolderItem(WorldObject item, Action<bool> callback)
        {
            SaveCalls++;

            if (SaveThrows != null)
                throw SaveThrows;

            if (StallAfterSave)
                ShardStalled = true;

            ShardQueue.Enqueue(() =>
            {
                if (SaveResult)
                {
                    // The row now names the target: it leaves whichever holder pack it was in.
                    foreach (var pack in HolderPacks.Values)
                        pack.Remove(item.Guid.Full);
                }

                callback(SaveResult);
            });
        }

        /// <summary>Runs the world queue only, leaving anything scheduled on the character's action queue.</summary>
        public void RunWorldQueueOnly()
        {
            while (WorldQueue.Count > 0)
                WorldQueue.Dequeue()();
        }

        /// <summary>
        /// Runs every queue until all are empty (bounded, so a scheduling loop fails instead of hanging): the world
        /// queue first, then the shard queue (unless stalled), then the character's action queue. Delays are not
        /// modelled: a scheduled timeout runs in FIFO order with everything else scheduled.
        /// </summary>
        public void Pump(int maxActions = 2000)
        {
            for (var i = 0; i < maxActions; i++)
            {
                if (WorldQueue.Count > 0)
                    WorldQueue.Dequeue()();
                else if (!ShardStalled && ShardQueue.Count > 0)
                    ShardQueue.Dequeue()();
                else if (Scheduled.Count > 0)
                {
                    BeforeStep?.Invoke();
                    Scheduled.Dequeue()();
                }
                else
                    return;
            }

            Assert.Fail("the transfer kept scheduling work past the pump bound");
        }
    }
}
