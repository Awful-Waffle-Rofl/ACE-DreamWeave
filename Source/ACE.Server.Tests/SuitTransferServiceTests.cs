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

        [TestInitialize]
        public void Setup()
        {
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

            const string signature = "public MarketError CheckGates(SuitTransferCharacter character, out string message)";
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

        public MarketError CheckGates(SuitTransferCharacter character, out string message)
        {
            if (ThrowFromGates != null)
                throw ThrowFromGates;

            message = GateError == MarketError.None ? null : $"gate {GateError}";
            return GateError;
        }

        public AccountVaultStore GetStore(uint accountId) => Store != null && Store.AccountId == accountId ? Store : null;

        public int FreeMainPackSlots(SuitTransferCharacter character) => FreeSlots;

        public int FreeContainerSlots(SuitTransferCharacter character) => FreeContainers;

        public int AvailableBurden(SuitTransferCharacter character) => AvailableBurdenValue;

        public int TemplateBurden(uint wcid) => TemplateBurdens.TryGetValue(wcid, out var b) ? b : 0;

        public int? ListedCount(uint accountId, VaultEntry row) => Listed(row);

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

        /// <summary>Runs the world queue only, leaving anything scheduled on the character's action queue.</summary>
        public void RunWorldQueueOnly()
        {
            while (WorldQueue.Count > 0)
                WorldQueue.Dequeue()();
        }

        /// <summary>Runs both queues until both are empty (bounded, so a scheduling loop fails instead of hanging).</summary>
        public void Pump(int maxActions = 2000)
        {
            for (var i = 0; i < maxActions; i++)
            {
                if (WorldQueue.Count > 0)
                    WorldQueue.Dequeue()();
                else if (Scheduled.Count > 0)
                    Scheduled.Dequeue()();
                else
                    return;
            }

            Assert.Fail("the transfer kept scheduling work past the pump bound");
        }
    }
}
