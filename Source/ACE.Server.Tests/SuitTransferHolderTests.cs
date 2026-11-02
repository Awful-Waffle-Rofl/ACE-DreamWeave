using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Holder lines: an item moved out of another (offline) character's pack on the same account into the online
    /// character's pack (SuitTransferService_Holder). Driven through FakeSuitTransferWorld with the shard queue pumped by
    /// hand. TestCleanup asserts that no object the fake ever handed the service was Destroy()ed.
    /// </summary>
    [TestClass]
    public class SuitTransferHolderTests
    {
        private const uint Account = 9511;
        private const uint OtherAccount = 9512;
        private const uint Character = 0x50000951;
        private const uint Alt = 0x50000952;
        private const uint AltTwo = 0x50000953;
        private const uint Stranger = 0x50000954;

        private const uint ArmorWcid = 95001;
        private const uint RingWcid = 95002;

        private FakeSuitTransferWorld world;
        private SuitTransferService service;

        [TestInitialize]
        public void Setup()
        {
            world = new FakeSuitTransferWorld();
            world.Online[(Account, Character)] = new SuitTransferCharacter(Account, Character, "Suitmover", null, null);

            world.Roster[(Account, Character)] = "Suitmover";
            world.Roster[(Account, Alt)] = "Altone";
            world.Roster[(Account, AltTwo)] = "Alttwo";
            world.Roster[(OtherAccount, Stranger)] = "Stranger";

            world.Holders[Alt] = Offline(Alt, "Altone");
            world.Holders[AltTwo] = Offline(AltTwo, "Alttwo");
            world.Holders[Stranger] = new SuitHolderState(true, OtherAccount, false, false, "Stranger");

            service = new SuitTransferService(world);
        }

        [TestCleanup]
        public void NothingWasEverDestroyed()
        {
            foreach (var item in world.HolderObjects)
                Assert.IsFalse(item.IsDestroyed, $"0x{item.Guid.Full:X8} was Destroy()ed: Destroy deletes the holder's row");

            foreach (var pack in world.HolderPacks.Values)
            {
                foreach (var row in pack.Values)
                    Assert.IsFalse(row.Item.IsDestroyed, $"0x{row.Item.Guid.Full:X8} (still the holder's) was Destroy()ed");
            }
        }

        // ---- fixtures ----

        private static SuitHolderState Offline(uint guid, string name) => new SuitHolderState(true, Account, false, false, name);

        private static WorldObject MakeSuitItem(uint wcid, int burden = 10)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ValidLocations, (int)EquipMask.ChestArmor },
                    { PropertyInt.EncumbranceVal, burden },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, $"Holder Armor {wcid}" },
                },
            };

            return new GenericObject(weenie, new ObjectGuid(FakeVaultWorld.NextGuid()));
        }

        private WorldObject SeedHolderItem(uint holder, uint wcid = ArmorWcid, SuitHolderReadStatus where = SuitHolderReadStatus.Found, Action<WorldObject> tweak = null)
        {
            var item = MakeSuitItem(wcid);
            tweak?.Invoke(item);

            if (!world.HolderPacks.TryGetValue(holder, out var pack))
                world.HolderPacks[holder] = pack = new Dictionary<uint, (WorldObject, SuitHolderReadStatus)>();

            pack[item.Guid.Full] = (item, where);
            return item;
        }

        private bool StillWithHolder(uint holder, WorldObject item) => world.HolderPacks.TryGetValue(holder, out var pack) && pack.ContainsKey(item.Guid.Full);

        private static SuitTransferLine Holder(WorldObject item, uint holder) => new SuitTransferLine(item.Guid.Full, item.WeenieClassId, null, 1, null, holder);

        private SuitTransferSubmitResult Submit(params SuitTransferLine[] lines) =>
            service.Submit(new SuitTransferRequest(Account, Character, "k-" + Guid.NewGuid().ToString("N"), lines, false));

        private SuitTransferStatus SubmitAndRun(params SuitTransferLine[] lines)
        {
            var submitted = Submit(lines);
            Assert.AreEqual(MarketError.None, submitted.Error, "the submit itself must be accepted");

            world.Pump();

            return service.GetStatus(Account, submitted.TransferId);
        }

        // ---- the happy path ----

        [TestMethod]
        public void Holder_Delivers_ByReparentingTheSameObject_AndConfirmsTheSave()
        {
            var armor = SeedHolderItem(Alt);

            var status = SubmitAndRun(Holder(armor, Alt));
            var line = status.Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Delivered, line.Outcome, line.Message);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(1, world.AddCalls);
            Assert.AreSame(armor, world.HolderDelivered.Single(), "the same object (same guid, same row) is added, never a copy");
            Assert.AreEqual(1, world.SaveCalls, "the forced save is queued once and awaited");
            Assert.IsFalse(StillWithHolder(Alt, armor), "the confirmed save re-parents the row");
            Assert.AreEqual(0, world.Claimed.Count, "the claim is released once the save confirmed");
            Assert.AreEqual(0, world.Pinned.Count);
            Assert.AreEqual(0, world.WithdrawCalls, "a holder line touches no vault");
            Assert.AreEqual(0, world.GetStoreCalls.Count + world.ExistingStoreCalls.Count, "a holder-only transfer loads no store");
            CollectionAssert.Contains(world.Told, "Holder Armor 95001 moved from Altone's pack to your pack.");
        }

        [TestMethod]
        public void Status_EchoesHolderGuid()
        {
            var armor = SeedHolderItem(Alt);

            var line = SubmitAndRun(Holder(armor, Alt)).Items.Single();

            Assert.AreEqual(Alt, line.HolderGuid);
            Assert.IsNull(line.VaultKey);
            Assert.AreEqual(armor.Guid.Full, line.ItemGuid);
        }

        [TestMethod]
        public void Claim_ComesBeforeEveryHolderStateRead_IsHeldThroughTheSave_AndReleasedAfterIt()
        {
            var armor = SeedHolderItem(Alt);
            world.StallAfterSave = true;

            var submitted = Submit(Holder(armor, Alt));
            world.Pump();                            // runs up to the queued save, which is held

            Assert.AreEqual(1, world.AddCalls);
            Assert.AreEqual(1, world.SaveCalls);
            Assert.IsTrue(world.StateReadWhileClaimed.Count >= 2, "precondition: the holder was checked before the read and again before the add");
            Assert.IsTrue(world.StateReadWhileClaimed.All(c => c), "every holder state read happens UNDER the claim");
            Assert.IsTrue(world.Claimed.Contains(Alt), "no login of the holder may start while the save is unconfirmed");
            Assert.AreEqual(SuitTransferOutcome.Pending, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);

            world.ShardStalled = false;
            world.Pump();

            Assert.AreEqual(SuitTransferOutcome.Delivered, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
            Assert.IsFalse(world.Claimed.Contains(Alt), "released after the save confirmed");
            CollectionAssert.AreEqual(new[] { "claim 50000952", "release 50000952" }, world.ClaimLog);
        }

        // ---- submit checks ----

        [TestMethod]
        public void Submit_HolderNotOnTheCallersRoster_IsNotOwner()
        {
            var item = SeedHolderItem(Stranger);

            Assert.AreEqual(MarketError.NotOwner, Submit(Holder(item, Stranger)).Error, "another account's character");
            Assert.AreEqual(MarketError.NotOwner, Submit(new SuitTransferLine(item.Guid.Full, ArmorWcid, null, 1, null, 0x50000999)).Error, "no such character");
            Assert.AreEqual(0, world.WorldQueue.Count, "nothing is queued");
            Assert.AreEqual(0, world.HolderReads);
        }

        [TestMethod]
        public void Submit_HolderIsTheTransfersOwnCharacter_IsInvalid()
        {
            var item = SeedHolderItem(Character);

            Assert.AreEqual(MarketError.InvalidTransfer, Submit(Holder(item, Character)).Error);
            Assert.AreEqual(0, world.WorldQueue.Count);
        }

        [TestMethod]
        public void Submit_HolderLineShape_NeedsAGuidCountOneNoClassKeyNoVaultKey()
        {
            var item = SeedHolderItem(Alt);

            Assert.AreEqual(MarketError.InvalidTransfer, Submit(new SuitTransferLine(null, ArmorWcid, null, 1, null, Alt)).Error, "no item guid");
            Assert.AreEqual(MarketError.InvalidTransfer, Submit(new SuitTransferLine(item.Guid.Full, ArmorWcid, null, 2, null, Alt)).Error, "count 2");
            Assert.AreEqual(MarketError.InvalidTransfer, Submit(new SuitTransferLine(item.Guid.Full, ArmorWcid, "c1", 1, null, Alt)).Error, "a class key");
            Assert.AreEqual(MarketError.InvalidTransfer, Submit(new SuitTransferLine(item.Guid.Full, ArmorWcid, null, 1, 77, Alt)).Error, "a vault key too");
            Assert.AreEqual(MarketError.InvalidTransfer, Submit(new SuitTransferLine(item.Guid.Full, ArmorWcid, null, 1, null, 0)).Error, "holder 0");
            Assert.AreEqual(MarketError.None, Submit(Holder(item, Alt)).Error, "the well-formed line is accepted");
        }

        [TestMethod]
        public void Submit_TheSameGuidOnTwoLines_IsInvalid_EvenAcrossVaultAndHolder()
        {
            var item = SeedHolderItem(Alt);

            Assert.AreEqual(MarketError.InvalidTransfer, Submit(Holder(item, Alt), Holder(item, AltTwo)).Error);
            Assert.AreEqual(MarketError.InvalidTransfer, Submit(Holder(item, Alt), new SuitTransferLine(item.Guid.Full, ArmorWcid, null, 1)).Error);
        }

        [TestMethod]
        public void Submit_MoreDistinctHoldersThanTheCap_IsInvalid()
        {
            var lines = new List<SuitTransferLine>();

            for (uint i = 0; i < SuitTransferService.MaxHolders + 1; i++)
            {
                var holder = 0x50000A00 + i;
                world.Roster[(Account, holder)] = $"Alt{i}";
                lines.Add(new SuitTransferLine(0x7F300000 + i, ArmorWcid, null, 1, null, holder));
            }

            Assert.AreEqual(MarketError.InvalidTransfer, Submit(lines.ToArray()).Error);
            Assert.AreEqual(MarketError.None, Submit(lines.Take(SuitTransferService.MaxHolders).ToArray()).Error, "exactly the cap is accepted");
        }

        [TestMethod]
        public void KillSwitchOff_RefusesHolderLines_AtSubmit()
        {
            var item = SeedHolderItem(Alt);
            world.CharacterMoves = false;

            Assert.AreEqual(MarketError.InvalidTransfer, Submit(Holder(item, Alt)).Error);
            Assert.AreEqual(0, world.WorldQueue.Count);
            Assert.AreEqual(0, world.HolderReads);
            Assert.IsTrue(StillWithHolder(Alt, item));
        }

        [TestMethod]
        public void KillSwitchTurnedOffMidRun_RefusesTheRemainingHolderLines()
        {
            var first = SeedHolderItem(Alt);
            var second = SeedHolderItem(Alt, RingWcid);

            world.SaveResult = true;
            var saves = 0;
            world.BeforeStep = () =>
            {
                if (world.SaveCalls > saves)
                {
                    saves = world.SaveCalls;
                    world.CharacterMoves = false;
                }
            };

            var status = SubmitAndRun(Holder(first, Alt), Holder(second, Alt));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(SuitTransferService.CharacterMovesOffMessage, status.Items[1].Message);
            Assert.IsTrue(StillWithHolder(Alt, second));
        }

        [TestMethod]
        public void Preflight_CountsHolderLines_ForSlotsAndBurden()
        {
            var item = SeedHolderItem(Alt);

            world.FreeSlots = 0;
            var noSlot = SubmitAndRun(Holder(item, Alt));
            Assert.AreEqual(SuitTransferState.Rejected, noSlot.State);
            Assert.AreEqual(MarketError.PackFull, noSlot.Reason);

            world.FreeSlots = 100;
            world.TemplateBurdens[ArmorWcid] = 500;
            world.AvailableBurdenValue = 499;
            var tooHeavy = SubmitAndRun(Holder(item, Alt));
            Assert.AreEqual(SuitTransferState.Rejected, tooHeavy.State);
            Assert.AreEqual(MarketError.PackFull, tooHeavy.Reason);

            Assert.AreEqual(0, world.HolderReads, "a rejected plan reads nothing");
            Assert.AreEqual(0, world.ClaimLog.Count, "and claims nothing");
            Assert.IsTrue(StillWithHolder(Alt, item));
        }

        // ---- per unit, before the read ----

        [TestMethod]
        public void Holder_Online_IsRefused_LogOutFirst_NothingRead()
        {
            var item = SeedHolderItem(Alt);
            world.Holders[Alt] = new SuitHolderState(true, Account, false, true, "Altone");

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Log out of Altone first.", line.Message);
            Assert.AreEqual(0, world.HolderReads, "an online holder's possessions are never read");
            Assert.AreEqual(0, world.AddCalls);
            Assert.AreEqual(0, world.Claimed.Count, "the claim taken for the check is released");
            Assert.IsTrue(StillWithHolder(Alt, item));
        }

        [TestMethod]
        public void Holder_EnteringTheWorld_FailsTheClaim_IsRefused_NothingRead()
        {
            var item = SeedHolderItem(Alt);
            world.EnteringWorld.Add(Alt);

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Log out of Altone first.", line.Message);
            Assert.AreEqual(0, world.HolderReads);
            Assert.AreEqual(0, world.HolderStateReads.Count, "nothing about the holder is read without the claim");
        }

        [TestMethod]
        public void Holder_DeletedOrPendingDeletion_IsRefused_NothingRead()
        {
            var item = SeedHolderItem(Alt);
            world.Holders[Alt] = new SuitHolderState(true, Account, true, false, "Altone");

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "deleted or pending deletion");
            Assert.AreEqual(0, world.HolderReads);
            Assert.AreEqual(0, world.Claimed.Count);
        }

        [TestMethod]
        public void Holder_NoLongerOnThisAccount_AtTheStep_IsRefused()
        {
            var item = SeedHolderItem(Alt);
            world.Holders[Alt] = new SuitHolderState(true, OtherAccount, false, false, "Altone");

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "no longer one of your characters");
            Assert.AreEqual(0, world.HolderReads);
        }

        // ---- per unit, the read ----

        [TestMethod]
        public void ItemMovedInGame_BetweenPlanAndStep_IsRefused_NothingAdded()
        {
            var item = SeedHolderItem(Alt);
            var submitted = Submit(Holder(item, Alt));

            world.RunWorldQueueOnly();               // planned
            world.HolderPacks[Alt].Remove(item.Guid.Full);   // dropped, traded or moved before the step's read

            world.Pump();

            var line = service.GetStatus(Account, submitted.TransferId).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That item is no longer in Altone's pack.", line.Message);
            Assert.AreEqual(0, world.AddCalls);
            Assert.AreEqual(0, world.SaveCalls);
            Assert.AreEqual(0, world.Claimed.Count);
        }

        [TestMethod]
        public void Worn_IsRefused()
        {
            var item = SeedHolderItem(Alt, where: SuitHolderReadStatus.Worn);

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "wearing");
            Assert.AreEqual(0, world.AddCalls);
            Assert.IsTrue(StillWithHolder(Alt, item));
        }

        [TestMethod]
        public void FailedRead_IsRefused()
        {
            var item = SeedHolderItem(Alt);
            world.ReadsFail = true;

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "could not be read");
            Assert.AreEqual(0, world.AddCalls);
            Assert.AreEqual(0, world.Claimed.Count);
        }

        [TestMethod]
        public void ReadNeverCallsBack_IsRefusedAtTheTimeout_AndTheClaimIsReleased()
        {
            var item = SeedHolderItem(Alt);
            world.ReadsNeverCallBack = true;

            var status = SubmitAndRun(Holder(item, Alt));
            var line = status.Items.Single();

            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "could not be read in time");
            Assert.AreEqual(0, world.Claimed.Count, "an abandoned read writes nothing, so the holder may log in again");
            Assert.AreEqual(0, world.AddCalls);
        }

        [TestMethod]
        public void LateReadCallback_AfterTheTimeout_DeliversNothing()
        {
            var item = SeedHolderItem(Alt);
            var submitted = Submit(Holder(item, Alt));

            world.ShardStalled = true;
            world.Pump();                            // the step queues the read, then the timeout abandons it

            var afterTimeout = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(SuitTransferState.Completed, afterTimeout.State, "precondition: the job moved on at the timeout");
            Assert.AreEqual(1, world.ShardQueue.Count, "precondition: the read is still queued");

            world.ShardStalled = false;
            world.Pump();                            // the late read answers Found

            Assert.AreEqual(0, world.AddCalls, "a late read must never deliver");
            Assert.AreEqual(0, world.SaveCalls);
            Assert.AreEqual(SuitTransferOutcome.Refused, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
            Assert.IsTrue(StillWithHolder(Alt, item));
            Assert.AreEqual(0, world.Claimed.Count);
        }

        // ---- per unit, on the target's queue after the read ----

        [TestMethod]
        public void HolderComesOnline_BetweenReadAndDeliver_IsRefused()
        {
            var item = SeedHolderItem(Alt);
            var reads = 0;

            // After the read has run (the shard queue is pumped before the action queue), before the delivery step.
            world.BeforeStep = () =>
            {
                if (world.HolderReads > reads && world.ShardQueue.Count == 0)
                {
                    reads = world.HolderReads;
                    world.Holders[Alt] = new SuitHolderState(true, Account, false, true, "Altone");
                }
            };

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(1, world.HolderReads, "precondition: the read ran");
            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Log out of Altone first.", line.Message);
            Assert.AreEqual(0, world.AddCalls, "the re-check on the target's queue refuses before the add");
            Assert.IsTrue(StillWithHolder(Alt, item));
            Assert.AreEqual(0, world.Claimed.Count);
        }

        [TestMethod]
        public void AltComesOnlineMidJob_RefusesTheRemainingLines()
        {
            var first = SeedHolderItem(Alt);
            var second = SeedHolderItem(Alt, RingWcid);

            var saves = 0;
            world.BeforeStep = () =>
            {
                if (world.SaveCalls > saves && world.ShardQueue.Count == 0)
                {
                    saves = world.SaveCalls;
                    world.Holders[Alt] = new SuitHolderState(true, Account, false, true, "Altone");
                }
            };

            var status = SubmitAndRun(Holder(first, Alt), Holder(second, Alt));

            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual("Log out of Altone first.", status.Items[1].Message);
            Assert.AreEqual(1, world.HolderReads, "the second line reads nothing");
            Assert.IsTrue(StillWithHolder(Alt, second));
        }

        [TestMethod]
        public void TargetGoesOffline_BeforeTheAdd_ClosesTheJob_NothingAdded()
        {
            var item = SeedHolderItem(Alt);
            var reads = 0;

            world.BeforeStep = () =>
            {
                if (world.HolderReads > reads && world.ShardQueue.Count == 0)
                {
                    reads = world.HolderReads;
                    world.Eligible = false;
                }
            };

            var status = SubmitAndRun(Holder(item, Alt));

            Assert.AreEqual(MarketError.CharacterOffline, status.Reason);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);
            Assert.AreEqual(0, world.AddCalls);
            Assert.AreEqual(0, world.Claimed.Count);
            Assert.IsTrue(StillWithHolder(Alt, item));
        }

        [TestMethod]
        public void Attuned_IsRefused()
        {
            var item = SeedHolderItem(Alt, tweak: i => i.SetProperty(PropertyInt.Attuned, 1));

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That item is attuned and cannot be moved between characters.", line.Message);
            Assert.AreEqual(0, world.AddCalls);
            Assert.IsTrue(StillWithHolder(Alt, item));
        }

        [TestMethod]
        public void Bonded_IsRefused()
        {
            var item = SeedHolderItem(Alt, tweak: i => i.SetProperty(PropertyInt.Bonded, 1));

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("That item is bonded and cannot be moved between characters.", line.Message);
            Assert.AreEqual(0, world.AddCalls);
        }

        [TestMethod]
        public void PvpTemplateIssued_IsRefused()
        {
            var item = SeedHolderItem(Alt, tweak: i => i.SetProperty(PropertyBool.PvpTemplateIssued, true));

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            StringAssert.Contains(line.Message, "PvP template");
            Assert.AreEqual(0, world.AddCalls);
        }

        [TestMethod]
        public void AllowedWielder_OtherThanTheTarget_IsRefused_TheTargetItselfIsFine()
        {
            var locked = SeedHolderItem(Alt, tweak: i => i.SetProperty(PropertyInstanceId.AllowedWielder, Alt));
            var mine = SeedHolderItem(Alt, RingWcid, tweak: i => i.SetProperty(PropertyInstanceId.AllowedWielder, Character));

            var status = SubmitAndRun(Holder(locked, Alt), Holder(mine, Alt));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[0].Outcome);
            StringAssert.Contains(status.Items[0].Message, "only be wielded by its owner");
            Assert.AreEqual(SuitTransferOutcome.Delivered, status.Items[1].Outcome, status.Items[1].Message);
        }

        [TestMethod]
        public void NotSuitRelevant_AndPackSlotObjects_AreRefused()
        {
            var weapon = SeedHolderItem(Alt, tweak: i => i.SetProperty(PropertyInt.ValidLocations, (int)EquipMask.MeleeWeapon));

            var pack = FakeVaultWorld.MakeContainer(24);
            pack.SetProperty(PropertyInt.ValidLocations, (int)EquipMask.ChestArmor);
            world.HolderPacks[Alt][pack.Guid.Full] = (pack, SuitHolderReadStatus.Found);

            var status = SubmitAndRun(Holder(weapon, Alt), new SuitTransferLine(pack.Guid.Full, pack.WeenieClassId, null, 1, null, Alt));

            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[0].Outcome);
            StringAssert.Contains(status.Items[0].Message, "armor, clothing and jewelry");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            StringAssert.Contains(status.Items[1].Message, "A pack cannot be moved");
            Assert.AreEqual(0, world.AddCalls);
        }

        [TestMethod]
        public void WrongWcid_ForTheGuid_IsRefused()
        {
            var item = SeedHolderItem(Alt);

            var line = SubmitAndRun(new SuitTransferLine(item.Guid.Full, RingWcid, null, 1, null, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual(0, world.AddCalls);
        }

        // ---- delivery failures ----

        [TestMethod]
        public void AddFails_DiscardsWithoutDestroy_RowStillNamesTheHolder()
        {
            var item = SeedHolderItem(Alt);
            world.AddSucceeds = false;

            var line = SubmitAndRun(Holder(item, Alt)).Items.Single();

            Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
            Assert.AreEqual("Your pack had no room for Holder Armor 95001; it was left in Altone's pack.", line.Message);
            Assert.AreEqual(1, world.AddCalls);
            Assert.AreEqual(0, world.SaveCalls, "nothing is saved for an object that never landed");
            Assert.IsTrue(StillWithHolder(Alt, item), "the row still names the holder");
            Assert.IsFalse(item.IsDestroyed);
            Assert.AreEqual(0, world.Claimed.Count);
            Assert.AreEqual(0, world.Pinned.Count);
        }

        [TestMethod]
        public void AddThrows_IsThrew_PinsTheHolder_StopsTheJob()
        {
            var first = SeedHolderItem(Alt);
            var second = SeedHolderItem(AltTwo);
            world.AddThrows = new InvalidOperationException("add throws");

            var status = SubmitAndRun(Holder(first, Alt), Holder(second, AltTwo));

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items[0].Outcome);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome, "the job stopped");
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.IsTrue(world.Pinned.Contains(Alt), "an unknown add keeps the holder out of the world");
            Assert.AreEqual(1, world.HolderReads, "the second line is never read");
        }

        [TestMethod]
        public void SaveReturnsFalse_IsThrew_StopsTheJob_PinsTheHolder()
        {
            var first = SeedHolderItem(Alt);
            var second = SeedHolderItem(AltTwo);
            world.SaveResult = false;

            var status = SubmitAndRun(Holder(first, Alt), Holder(second, AltTwo));

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items[0].Outcome);
            StringAssert.Contains(status.Items[0].Message, "could not be confirmed");
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items[1].Outcome);
            Assert.AreEqual(SuitTransferService.HolderSaveStopMessage, status.Items[1].Message);
            Assert.AreEqual(MarketError.ServerError, status.Reason);
            Assert.AreEqual(1, world.SaveCalls, "never a retry");
            Assert.IsTrue(world.Pinned.Contains(Alt));
            Assert.IsFalse(world.Claimed.Contains(AltTwo), "the second holder was never claimed");
        }

        [TestMethod]
        public void SaveThrows_IsThrew_PinsTheHolder()
        {
            var item = SeedHolderItem(Alt);
            world.SaveThrows = new InvalidOperationException("save throws");

            var status = SubmitAndRun(Holder(item, Alt));

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items.Single().Outcome);
            Assert.IsTrue(world.Pinned.Contains(Alt));
            Assert.AreEqual(SuitTransferState.Completed, status.State);
        }

        [TestMethod]
        public void SaveNeverCallsBack_TheStallWatchdog_IsThrew_AndPins_ALateConfirmationUnpins()
        {
            var item = SeedHolderItem(Alt);
            world.StallAfterSave = true;

            var submitted = Submit(Holder(item, Alt));
            world.Pump();                            // runs up to the queued save, which is held

            Assert.AreEqual(1, world.SaveCalls, "precondition: the save was queued");
            world.StallAfterSave = false;

            world.Now = world.Now + SuitTransferService.StallTimeout + TimeSpan.FromSeconds(1);
            var status = service.GetStatus(Account, submitted.TransferId);   // the watchdog runs here

            Assert.AreEqual(SuitTransferOutcome.Threw, status.Items.Single().Outcome);
            Assert.IsTrue(world.Pinned.Contains(Alt), "an unconfirmed save keeps the holder out of the world");

            world.ShardStalled = false;
            world.Pump();                            // the late save answers true

            Assert.IsFalse(world.Pinned.Contains(Alt), "a late confirmed save lifts the pin");
            Assert.AreEqual(SuitTransferOutcome.Threw, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome, "the reported outcome stands");
        }

        // ---- the pure read ----

        private static ShardBiota Row(uint id, uint container, int weenieType = (int)WeenieType.Generic, uint? wielder = null)
        {
            var biota = new ShardBiota { Id = id, WeenieClassId = 1, WeenieType = weenieType };

            if (container != 0)
                biota.BiotaPropertiesIID.Add(new ACE.Database.Models.Shard.BiotaPropertiesIID { ObjectId = id, Type = (ushort)PropertyInstanceId.Container, Value = container });

            if (wielder != null)
                biota.BiotaPropertiesIID.Add(new ACE.Database.Models.Shard.BiotaPropertiesIID { ObjectId = id, Type = (ushort)PropertyInstanceId.Wielder, Value = wielder.Value });

            return biota;
        }

        [TestMethod]
        public void FromPossessions_MainPackAndSidePack_AreFound_DeeperWornAndAbsentAreNot()
        {
            const uint holder = Alt;
            var main = Row(0x7F400001, holder);
            var sidePack = Row(0x7F400002, holder, (int)WeenieType.Container);
            var inSide = Row(0x7F400003, sidePack.Id);
            var innerPack = Row(0x7F400004, sidePack.Id, (int)WeenieType.Container);
            var deeper = Row(0x7F400005, innerPack.Id);
            var worn = Row(0x7F400006, 0, wielder: holder);
            var inventory = new[] { main, sidePack, inSide, innerPack, deeper };
            var wielded = new[] { worn };

            var converted = new List<uint>();
            ACE.Entity.Models.Biota Convert(ShardBiota b) { converted.Add(b.Id); return new ACE.Entity.Models.Biota { Id = b.Id }; }

            Assert.AreEqual(SuitHolderReadStatus.Found, SuitHolderRead.FromPossessions(holder, main.Id, inventory, wielded, Convert).Status);
            Assert.AreEqual(SuitHolderReadStatus.Found, SuitHolderRead.FromPossessions(holder, inSide.Id, inventory, wielded, Convert).Status);
            Assert.AreEqual(SuitHolderReadStatus.NotFound, SuitHolderRead.FromPossessions(holder, deeper.Id, inventory, wielded, Convert).Status, "a pack in a pack is not the holder's side pack");
            Assert.AreEqual(SuitHolderReadStatus.Worn, SuitHolderRead.FromPossessions(holder, worn.Id, inventory, wielded, Convert).Status);
            Assert.AreEqual(SuitHolderReadStatus.NotFound, SuitHolderRead.FromPossessions(holder, 0x7F4000FF, inventory, wielded, Convert).Status);
            Assert.AreEqual(SuitHolderReadStatus.NotFound, SuitHolderRead.FromPossessions(Stranger, main.Id, inventory, wielded, Convert).Status, "another holder's row");

            CollectionAssert.AreEqual(new[] { main.Id, inSide.Id }, converted, "only a found row is converted");
        }

        // ---- the login guard ----

        [TestMethod]
        public void Guard_LoginAndMove_ExcludeEachOther_AndAPinHoldsUntilAConfirmedRelease()
        {
            const uint guid = 0x50000960;
            SuitHolderMoveGuard.ResetForTests();

            try
            {
                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(guid));
                Assert.IsFalse(SuitHolderMoveGuard.TryClaimForMove(guid), "a move cannot claim a character that is entering the world");
                SuitHolderMoveGuard.EndEnterWorld(guid);

                Assert.IsTrue(SuitHolderMoveGuard.TryClaimForMove(guid));
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "a login is refused while a move holds the character");
                Assert.IsFalse(SuitHolderMoveGuard.TryClaimForMove(guid), "one move at a time");

                SuitHolderMoveGuard.PinUnconfirmed(guid);
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "a pinned character stays out");
                Assert.IsFalse(SuitHolderMoveGuard.TryClaimForMove(guid));

                SuitHolderMoveGuard.ReleaseMove(guid);
                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "a confirmed release lets it in again");
                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "two racing logins both mark");
                SuitHolderMoveGuard.EndEnterWorld(guid);
                Assert.IsTrue(SuitHolderMoveGuard.IsEnteringForTests(guid), "one mark is still open");
                SuitHolderMoveGuard.EndEnterWorld(guid);
                Assert.IsFalse(SuitHolderMoveGuard.IsEnteringForTests(guid));
                Assert.IsTrue(SuitHolderMoveGuard.TryClaimForMove(guid));
            }
            finally
            {
                SuitHolderMoveGuard.ResetForTests();
            }
        }

        [TestMethod]
        public void Guard_AnOldClaim_StillRefusesALogin_AndSoDoesAnOldPin()
        {
            const uint guid = 0x50000961;
            const uint pinnedGuid = 0x50000962;
            SuitHolderMoveGuard.ResetForTests();
            var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
            SuitHolderMoveGuard.Clock = () => now;

            try
            {
                Assert.IsTrue(SuitHolderMoveGuard.TryClaimForMove(guid));
                Assert.IsTrue(SuitHolderMoveGuard.TryClaimForMove(pinnedGuid));
                SuitHolderMoveGuard.PinUnconfirmed(pinnedGuid);

                now += TimeSpan.FromHours(5);
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "a claim is held until the transfer lets go, however old");
                Assert.IsFalse(SuitHolderMoveGuard.TryClaimForMove(guid), "and a second move is refused just the same");
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(pinnedGuid), "a pin never ages out");

                SuitHolderMoveGuard.ReleaseMove(guid);
                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(guid), "control: the release is what lets the login in");
            }
            finally
            {
                SuitHolderMoveGuard.ResetForTests();
            }
        }

        [TestMethod]
        public void Guard_AFiveHourOldEnteringMark_StillRefusesAMove()
        {
            const uint guid = 0x50000963;
            SuitHolderMoveGuard.ResetForTests();
            var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
            SuitHolderMoveGuard.Clock = () => now;

            try
            {
                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(guid));   // a login whose read is stuck behind a shard backlog

                now += TimeSpan.FromHours(5);
                Assert.IsFalse(SuitHolderMoveGuard.TryClaimForMove(guid), "a slow login is still a login: its read may yet return the old row");
                Assert.IsTrue(SuitHolderMoveGuard.IsEnteringForTests(guid), "the mark is never dropped by a move");

                SuitHolderMoveGuard.EndEnterWorld(guid);
                Assert.IsTrue(SuitHolderMoveGuard.TryClaimForMove(guid), "control: EndEnterWorld is what lets the move in");
            }
            finally
            {
                SuitHolderMoveGuard.ResetForTests();
            }
        }

        [TestMethod]
        public void Service_TargetLogsOutMidMove_TheSweepReleasesTheClaim_AndTheAltCanLogIn()
        {
            SuitHolderMoveGuard.ResetForTests();
            world.UseRealGuard = true;

            try
            {
                var item = SeedHolderItem(Alt);

                var submitted = Submit(Holder(item, Alt));
                world.RunWorldQueueOnly();

                while (world.HolderReads == 0 && world.Scheduled.Count > 0)
                    world.Scheduled.Dequeue()();

                Assert.AreEqual(1, world.HolderReads, "precondition: the unit claimed the alt and queued its read");

                // The read answers on the shard worker and hands delivery to the target's action queue...
                while (world.ShardQueue.Count > 0)
                    world.ShardQueue.Dequeue()();

                // ...but the target has logged out: its action queue never runs again.
                world.Scheduled.Clear();
                world.Eligible = false;

                Assert.AreEqual(0, world.AddCalls, "precondition: nothing was delivered");
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(Alt), "while the move holds the alt, its login is refused");

                world.Now = world.Now + SuitTransferService.StallTimeout + TimeSpan.FromSeconds(1);
                service.Sweep();

                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(Alt), "the sweep released the claim, so the alt can log in");
                SuitHolderMoveGuard.EndEnterWorld(Alt);

                var line = service.GetStatus(Account, submitted.TransferId).Items.Single();
                Assert.AreEqual(SuitTransferOutcome.Refused, line.Outcome);
                Assert.IsTrue(StillWithHolder(Alt, item), "nothing was saved: the row still names the alt");
                Assert.AreEqual(0, world.SaveCalls);
            }
            finally
            {
                SuitHolderMoveGuard.ResetForTests();
            }
        }

        [TestMethod]
        public void Service_TargetLogsOutWhileTheSaveIsUnconfirmed_TheSweepPins_TheAltStaysOut()
        {
            SuitHolderMoveGuard.ResetForTests();
            world.UseRealGuard = true;

            try
            {
                var item = SeedHolderItem(Alt);
                world.StallAfterSave = true;

                var submitted = Submit(Holder(item, Alt));
                world.Pump();                               // runs up to the queued save, which is held

                Assert.AreEqual(1, world.SaveCalls, "precondition: the save was queued");
                world.Scheduled.Clear();
                world.Eligible = false;

                world.Now = world.Now + SuitTransferService.StallTimeout + TimeSpan.FromSeconds(1);
                service.Sweep();

                Assert.AreEqual(SuitTransferOutcome.Threw, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(Alt), "an unconfirmed save keeps the alt out of the world");

                world.Now = world.Now + TimeSpan.FromHours(5);
                Assert.IsFalse(SuitHolderMoveGuard.TryBeginEnterWorld(Alt), "however long it takes");

                world.ShardStalled = false;
                world.Pump();                               // the late save answers true

                Assert.IsTrue(SuitHolderMoveGuard.TryBeginEnterWorld(Alt), "a late confirmed save lifts the pin");
                SuitHolderMoveGuard.EndEnterWorld(Alt);
            }
            finally
            {
                SuitHolderMoveGuard.ResetForTests();
            }
        }

        // ---- the world-heartbeat sweep (F1) ----

        /// <summary>
        /// A unit whose read is queued when the character's action queue stops (its read timeout and every later step
        /// are dropped, as a logout does): no browser polls, and the heartbeat's Sweep alone releases the claim.
        /// </summary>
        [TestMethod]
        public void Sweep_WithNoStatusPoll_AbandonsAStalledRead_AndReleasesTheClaim()
        {
            var item = SeedHolderItem(Alt);
            world.ShardStalled = true;                      // the read is queued and never answers

            var submitted = Submit(Holder(item, Alt));
            Assert.AreEqual(MarketError.None, submitted.Error);

            world.RunWorldQueueOnly();

            while (world.HolderReads == 0 && world.Scheduled.Count > 0)
                world.Scheduled.Dequeue()();

            Assert.AreEqual(1, world.HolderReads, "precondition: the unit claimed the holder and queued its read");
            Assert.IsTrue(world.Claimed.Contains(Alt), "precondition: claimed");

            world.Scheduled.Clear();                        // the action queue stops: the read timeout never runs

            world.Now = world.Now + SuitTransferService.StallTimeout + TimeSpan.FromSeconds(1);
            Assert.IsTrue(world.Claimed.Contains(Alt), "nothing has released the claim yet: time passing alone does nothing");

            service.Sweep();

            Assert.IsFalse(world.Claimed.Contains(Alt), "the sweep released the claim with no GetStatus");
            Assert.AreEqual(0, world.Pinned.Count, "a read is not a save: no pin");
            CollectionAssert.AreEqual(new[] { "claim 50000952", "release 50000952" }, world.ClaimLog);

            var status = service.GetStatus(Account, submitted.TransferId);
            Assert.AreEqual(SuitTransferState.Completed, status.State);
            Assert.AreEqual(SuitTransferOutcome.Refused, status.Items.Single().Outcome);

            world.ShardStalled = false;
            world.Pump();                                   // the late read answers: a no-op
            Assert.AreEqual(0, world.AddCalls, "a late read after the sweep delivers nothing");
            Assert.AreEqual(0, world.Claimed.Count);
        }

        [TestMethod]
        public void SweepLive_RunsTheLiveService_AtMostOncePerSweepInterval()
        {
            var item = SeedHolderItem(Alt);
            world.ShardStalled = true;

            var submitted = Submit(Holder(item, Alt));
            world.RunWorldQueueOnly();

            while (world.HolderReads == 0 && world.Scheduled.Count > 0)
                world.Scheduled.Dequeue()();

            world.Scheduled.Clear();

            var previous = SuitTransferService.Live;
            SuitTransferService.Live = service;

            try
            {
                SuitTransferService.SweepLive();            // the first call sweeps (nothing is stale yet) and arms the interval

                world.Now = world.Now + SuitTransferService.StallTimeout + TimeSpan.FromSeconds(1);
                Assert.IsTrue(world.Claimed.Contains(Alt));

                SuitTransferService.SweepLive();            // past the interval: sweeps
                Assert.IsFalse(world.Claimed.Contains(Alt), "SweepLive reached the live service's watchdog");
                Assert.AreEqual(SuitTransferOutcome.Refused, service.GetStatus(Account, submitted.TransferId).Items.Single().Outcome);
            }
            finally
            {
                SuitTransferService.Live = previous;
            }

            SuitTransferService.SweepLive();                // nothing wired is a no-op, never a throw
        }

        // ---- replay before the holder checks (F4) ----

        [TestMethod]
        public void Replay_AfterTheKillSwitchFlips_AnswersTheOriginalId()
        {
            var item = SeedHolderItem(Alt);
            var request = new SuitTransferRequest(Account, Character, "replay-key", new[] { Holder(item, Alt) }, false);

            var first = service.Submit(request);
            Assert.AreEqual(MarketError.None, first.Error);
            world.Pump();                                   // finished, so the control below is not refused as in-progress

            world.CharacterMoves = false;

            var replay = service.Submit(request);
            Assert.AreEqual(MarketError.None, replay.Error, "a replay is answered before the holder checks");
            Assert.AreEqual(first.TransferId, replay.TransferId);

            // Control: the same lines under a NEW key are judged, and the switch refuses them.
            var fresh = service.Submit(new SuitTransferRequest(Account, Character, "another-key", new[] { Holder(item, Alt) }, false));
            Assert.AreEqual(MarketError.InvalidTransfer, fresh.Error);
        }

        [TestMethod]
        public void Replay_AfterTheHolderLeavesTheRoster_AnswersTheOriginalId()
        {
            var item = SeedHolderItem(Alt);
            var request = new SuitTransferRequest(Account, Character, "replay-roster", new[] { Holder(item, Alt) }, false);

            var first = service.Submit(request);
            Assert.AreEqual(MarketError.None, first.Error);
            var rosterReads = world.RosterReads;

            world.Roster.Remove((Account, Alt));

            var replay = service.Submit(request);
            Assert.AreEqual(MarketError.None, replay.Error);
            Assert.AreEqual(first.TransferId, replay.TransferId);
            Assert.AreEqual(rosterReads, world.RosterReads, "a replay reads no roster");
        }

        // ---- holder-only gate wording (F5) ----

        private static SuitTransferGateState OutsideTheArea =>
            new SuitTransferGateState(false, false, false, false, false, false, false, "the Market Square");

        [TestMethod]
        public void HolderOnly_OutsideTheArea_IsRefused_InCharacterMoveWords_ThatNameTheArea()
        {
            var item = SeedHolderItem(Alt);
            world.GateState = OutsideTheArea;

            var status = SubmitAndRun(Holder(item, Alt));

            Assert.AreEqual(SuitTransferState.Rejected, status.State);
            Assert.AreEqual(MarketError.NotInVaultArea, status.Reason);
            Assert.AreEqual(0, world.HolderReads, "nothing was read");
            Assert.AreEqual(0, world.Claimed.Count + world.Pinned.Count);
            Assert.IsTrue(world.GateCharacterMoves.Count > 0 && world.GateCharacterMoves.All(m => m), "every gate check of a holder-only transfer asks for the character-move wording");

            var said = world.Told.Single(t => t.Contains("the Market Square"));
            Assert.AreEqual("You can only move items between your characters while you are in the Market Square. Go there and try again.", said);
            Assert.IsFalse(world.Told.Any(t => t.IndexOf("vault", StringComparison.OrdinalIgnoreCase) >= 0), "a holder-only transfer never mentions the vault: " + string.Join(" | ", world.Told));
        }

        [TestMethod]
        public void Decide_CharacterMoveWording_NeverSaysVault_AndIsPlainAscii()
        {
            var states = new[]
            {
                new SuitTransferGateState(false, false, false, false, false, false, true, null, inPvpMatch: true),
                new SuitTransferGateState(false, false, false, false, false, false, true, null, inArenaQueue: true),
                new SuitTransferGateState(true, false, false, false, false, false, true, null),
                new SuitTransferGateState(false, false, false, false, true, false, true, null),
                new SuitTransferGateState(false, false, false, false, false, false, false, "the Market Square"),
                new SuitTransferGateState(false, false, false, false, false, false, false, null),
            };

            foreach (var state in states)
            {
                var vaultError = SuitTransferGates.Decide(state, out var vaultText);
                var moveError = SuitTransferGates.Decide(state, out var moveText, characterMove: true);

                Assert.AreEqual(vaultError, moveError, "the gates are the same; only the words differ");
                Assert.AreNotEqual(MarketError.None, moveError);
                StringAssert.Contains(vaultText, "vault");
                Assert.IsFalse(moveText.IndexOf("vault", StringComparison.OrdinalIgnoreCase) >= 0, moveText);
                Assert.IsTrue(moveText.All(c => c >= 0x20 && c < 0x7F), "plain ASCII only: " + moveText);
            }

            SuitTransferGates.Decide(states[4], out var area, characterMove: true);
            StringAssert.Contains(area, "the Market Square");
            SuitTransferGates.Decide(states[5], out var unnamed, characterMove: true);
            StringAssert.Contains(unnamed, "the permitted area");
        }
    }
}
