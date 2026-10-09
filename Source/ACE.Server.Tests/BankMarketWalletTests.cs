using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for BankMarketWallet against the account-wide pool, using the same
    /// FakeAccountBankBackend the account-bank tests use (AccountBankFakes.cs) plus a recording
    /// IPlayer stand-in for the character lookup.
    ///
    /// WHY THIS FILE EXISTS. MarketApiTests and MarketPurchaseTests drive a FAKE wallet
    /// (MarketFakes.cs), so nothing in them touches Player_Bank at all - which is exactly why the
    /// account-bank refactor could turn every web buy into a 500 without a single test going red.
    /// These tests exercise the real wallet.
    ///
    /// THE ONLINE PATH IS NOT COVERED HERE and cannot be: BankMarketWallet routes an online character
    /// through Player.TryModifyBankedPyreals, and constructing a Player needs a Session, a Biota and a
    /// live landblock. What that branch does is a three-line normalisation of TryModifyBankedPyreals'
    /// (applied, stateUnknown) pair back into the same AccountBankAdjustResult the offline branch gets
    /// from AccountBankManager.TryAdjust (BankMarketWallet.Adjust), after which BOTH branches run the
    /// identical switch tested below. TryModifyBankedPyreals itself is Player_Bank.cs:268 and forwards
    /// to the same AccountBankManager.TryAdjust these tests drive directly.
    /// </summary>
    [TestClass]
    public class BankMarketWalletTests
    {
        private const uint AccountId = 7101;
        private const uint CharacterGuid = 0x50000101;

        private FakeAccountBankBackend fake;
        private RecordingWalletPlayer character;
        private BankMarketWallet wallet;

        [TestInitialize]
        public void Setup()
        {
            fake = new FakeAccountBankBackend();
            AccountBankManager.ResetForTesting(fake);

            character = new RecordingWalletPlayer(CharacterGuid, AccountId);
            wallet = new BankMarketWallet();

            BankMarketWallet.ResolveCharacter = guid => guid == CharacterGuid ? character : null;
        }

        [TestCleanup]
        public void Teardown()
        {
            BankMarketWallet.ResolveCharacter = BankMarketWallet.DefaultResolveCharacter;
            AccountBankManager.ResetForTesting(null);
        }

        private static long Pyreals(long notes) => notes * Player.MmdValue;

        // ---- balance ----

        [TestMethod]
        public void GetBalanceMmd_UnknownCharacter_IsZeroAndDoesNotThrow()
        {
            Assert.AreEqual(0, wallet.GetBalanceMmd(0xDEADBEEF));
            Assert.AreEqual(0, fake.BalanceReadCalls, "an unknown character must not reach the ledger at all");
        }

        [TestMethod]
        public void GetBalanceMmd_ReadsTheAccountPool_FlooredToWholeNotes()
        {
            fake.Balances[AccountId] = Pyreals(3) + 1;

            Assert.AreEqual(3, wallet.GetBalanceMmd(CharacterGuid), "a part-note remainder is floored, not rounded");
        }

        [TestMethod]
        public void GetBalanceMmd_CharacterWithNoAccount_IsZero()
        {
            character.ClearAccount();

            Assert.AreEqual(0, wallet.GetBalanceMmd(CharacterGuid));
            Assert.AreEqual(0, fake.BalanceReadCalls);
        }

        [TestMethod]
        public void GetBalanceMmd_UnreadableBalance_IsZeroRatherThanAThrow()
        {
            fake.Balances[AccountId] = Pyreals(9);
            fake.FailBalanceRead = true;

            Assert.AreEqual(0, wallet.GetBalanceMmd(CharacterGuid), "an unavailable balance displays as 0; the mutation's WHERE guard is the real adjudicator");
        }

        // ---- debit ----

        [TestMethod]
        public void TryDebit_TakesTheNotesFromTheAccountPool()
        {
            fake.Balances[AccountId] = Pyreals(10);

            Assert.IsTrue(wallet.TryDebit(CharacterGuid, 4, out var error));
            Assert.AreEqual(MarketError.None, error);
            Assert.AreEqual(Pyreals(6), fake.Balances[AccountId]);
        }

        [TestMethod]
        public void TryDebit_ZeroIsASuccessThatMovesNothing()
        {
            fake.Balances[AccountId] = Pyreals(10);

            Assert.IsTrue(wallet.TryDebit(CharacterGuid, 0, out var error));
            Assert.AreEqual(MarketError.None, error);
            Assert.AreEqual(0, fake.AdjustCalls, "a zero debit must not issue a ledger statement");
        }

        [TestMethod]
        public void TryDebit_NegativeAmount_IsAServerErrorNotAStealthCredit()
        {
            fake.Balances[AccountId] = Pyreals(10);

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, -5, out var error));
            Assert.AreEqual(MarketError.ServerError, error);
            Assert.AreEqual(Pyreals(10), fake.Balances[AccountId]);
            Assert.AreEqual(0, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryDebit_UnknownCharacter_IsBadCredentials()
        {
            Assert.IsFalse(wallet.TryDebit(0xDEADBEEF, 1, out var error));
            Assert.AreEqual(MarketError.BadCredentials, error);
            Assert.AreEqual(0, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryDebit_CharacterWithNoAccount_IsAServerError()
        {
            character.ClearAccount();

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 1, out var error));
            Assert.AreEqual(MarketError.ServerError, error);
            Assert.AreEqual(0, fake.AdjustCalls, "there is no pool to draw on, so nothing may be attempted");
        }

        [TestMethod]
        public void TryDebit_ShortBalance_IsInsufficientFundsAndMovesNothing()
        {
            fake.Balances[AccountId] = Pyreals(1);

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 2, out var error));
            Assert.AreEqual(MarketError.InsufficientFunds, error);
            Assert.AreEqual(Pyreals(1), fake.Balances[AccountId]);
            Assert.AreEqual(0, fake.AdjustCalls, "the pre-check refuses before the ledger is asked");
        }

        [TestMethod]
        public void TryDebit_LedgerRefusesEvenThoughTheCachedBalanceLookedSufficient_IsInsufficientFunds()
        {
            // The cache is warmed at 10 notes and the ledger is then moved behind its back, which is the
            // shape of a sibling character spending from the same pool between the pre-check and the
            // mutation. The guarded UPDATE is the adjudicator and its refusal must reach the buyer as
            // insufficient funds, not as a server error.
            fake.Balances[AccountId] = Pyreals(10);
            Assert.AreEqual(10, wallet.GetBalanceMmd(CharacterGuid));

            fake.Balances[AccountId] = Pyreals(1);

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 5, out var error));
            Assert.AreEqual(MarketError.InsufficientFunds, error);
            Assert.AreEqual(1, fake.AdjustCalls, "the pre-check passed on the stale cache, so the ledger was asked");
            Assert.AreEqual(Pyreals(1), fake.Balances[AccountId]);
        }

        [TestMethod]
        public void TryDebit_UnreadableBalance_IsAServerErrorNotInsufficientFunds()
        {
            fake.Balances[AccountId] = Pyreals(10);
            fake.FailBalanceRead = true;

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 1, out var error));
            Assert.AreEqual(MarketError.ServerError, error, "a failed SELECT must never be reported to a solvent buyer as insufficient funds");
            Assert.AreEqual(0, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryDebit_LedgerFailure_IsLedgerUnknownAndNeverASuccess()
        {
            fake.Balances[AccountId] = Pyreals(10);
            Assert.AreEqual(10, wallet.GetBalanceMmd(CharacterGuid));

            fake.FailAdjust = true;

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 4, out var error));
            Assert.AreEqual(MarketError.LedgerUnknown, error,
                "distinct from ServerError so MarketManager.Buy can resolve the row to DebitLedgerUnknown rather than Failed");
            Assert.AreEqual(1, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryDebit_UnreadableBalanceAndLedgerFailure_AreDifferentErrors()
        {
            // The discriminator: nothing was attempted when the balance could not be read, so that is a
            // plain ServerError and the row may be resolved Failed. Only a ledger whose state is
            // unknown may claim LedgerUnknown, or the distinguishing status stops distinguishing.
            fake.Balances[AccountId] = Pyreals(10);
            fake.FailBalanceRead = true;

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 1, out var unreadable));
            Assert.AreEqual(MarketError.ServerError, unreadable);

            fake.FailBalanceRead = false;
            Assert.AreEqual(10, wallet.GetBalanceMmd(CharacterGuid));
            fake.FailAdjust = true;

            Assert.IsFalse(wallet.TryDebit(CharacterGuid, 1, out var unknown));
            Assert.AreEqual(MarketError.LedgerUnknown, unknown);
        }

        [TestMethod]
        public void TryDebit_AppliedCountUnknown_IsASuccessBecauseThePyrealsProvablyMoved()
        {
            fake.Balances[AccountId] = Pyreals(10);
            fake.NextAdjustCountUnknown = true;

            Assert.IsTrue(wallet.TryDebit(CharacterGuid, 4, out var error), "only the read-back of the new balance was lost; failing the buy here would charge the buyer for nothing");
            Assert.AreEqual(MarketError.None, error);
            Assert.AreEqual(Pyreals(6), fake.Balances[AccountId]);
        }

        // ---- credit ----

        [TestMethod]
        public void TryCredit_AddsTheNotesToTheAccountPool()
        {
            fake.Balances[AccountId] = Pyreals(2);

            Assert.IsTrue(wallet.TryCredit(CharacterGuid, 3));
            Assert.AreEqual(Pyreals(5), fake.Balances[AccountId]);
        }

        [TestMethod]
        public void TryCredit_CreatesThePoolRowForAnAccountThatHasNone()
        {
            Assert.IsTrue(wallet.TryCredit(CharacterGuid, 3));
            Assert.AreEqual(Pyreals(3), fake.Balances[AccountId], "a credit against a missing row creates it, so a seller is never unpayable for want of a row");
        }

        [TestMethod]
        public void TryCredit_NonPositiveIsANoOpSuccess()
        {
            Assert.IsTrue(wallet.TryCredit(CharacterGuid, 0));
            Assert.IsTrue(wallet.TryCredit(CharacterGuid, -1));
            Assert.AreEqual(0, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryCredit_AmountBeyondTheRepresentablePyrealRange_IsRefusedBeforeAnythingMoves()
        {
            var overflowing = (long.MaxValue / Player.MmdValue) + 1;

            Assert.IsFalse(wallet.TryCredit(CharacterGuid, overflowing), "the pyreal value would wrap long and land negative, silently debiting the payee");
            Assert.AreEqual(0, fake.AdjustCalls);
            Assert.IsFalse(fake.Balances.ContainsKey(AccountId));
        }

        [TestMethod]
        public void TryCredit_UnknownCharacter_IsFalseSoTheCallerLogsTheLostCredit()
        {
            Assert.IsFalse(wallet.TryCredit(0xDEADBEEF, 5));
            Assert.AreEqual(0, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryCredit_CharacterWithNoAccount_IsFalse()
        {
            character.ClearAccount();

            Assert.IsFalse(wallet.TryCredit(CharacterGuid, 5));
            Assert.AreEqual(0, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryCredit_LedgerFailure_IsFalse()
        {
            fake.FailAdjust = true;

            Assert.IsFalse(wallet.TryCredit(CharacterGuid, 5), "an unknown outcome is never reported as a paid seller");
            Assert.AreEqual(1, fake.AdjustCalls);
        }

        [TestMethod]
        public void TryCredit_LedgerRefusal_IsFalseAndMovesNothing()
        {
            // A pool one pyreal short of long.MaxValue cannot take another note, and the fake's guarded
            // WHERE refuses it the way the real one does.
            fake.Balances[AccountId] = long.MaxValue - 1;

            Assert.IsFalse(wallet.TryCredit(CharacterGuid, 1));
            Assert.AreEqual(long.MaxValue - 1, fake.Balances[AccountId]);
        }

        [TestMethod]
        public void TryCredit_AppliedCountUnknown_IsASuccess()
        {
            fake.Balances[AccountId] = Pyreals(2);
            fake.NextAdjustCountUnknown = true;

            Assert.IsTrue(wallet.TryCredit(CharacterGuid, 3));
            Assert.AreEqual(Pyreals(5), fake.Balances[AccountId]);
        }

        // ---- the regression this file was written for ----

        [TestMethod]
        public void WalletPaths_NeverTouchTheLegacyPerCharacterBankedPyrealsProperty()
        {
            fake.Balances[AccountId] = Pyreals(10);

            wallet.GetBalanceMmd(CharacterGuid);
            wallet.TryDebit(CharacterGuid, 4, out _);
            wallet.TryCredit(CharacterGuid, 2);

            fake.FailAdjust = true;
            wallet.TryDebit(CharacterGuid, 1, out _);
            wallet.TryCredit(CharacterGuid, 1);

            CollectionAssert.AreEqual(
                new List<PropertyInt64>(),
                character.Int64Writes,
                "banked pyreals are account-wide; a write to PropertyInt64.BankedPyreals (9004) is money the player will never see again");

            CollectionAssert.AreEqual(
                new List<PropertyInt64>(),
                character.Int64Reads,
                "reading 9004 would report a folded-away legacy balance as spendable");

            Assert.AreEqual(0, character.SaveCalls, "the pool is a database row, so no biota save is owed by a wallet move");
        }
    }

    /// <summary>
    /// The smallest IPlayer that BankMarketWallet's offline branch needs: an Account to resolve, and a
    /// recorder for every property read and write so a test can assert the legacy 9004 property is
    /// never touched. Everything else throws nothing and returns default - the wallet reaches none of
    /// it, and a member that silently starts being used will show up as a null or a zero, not as a
    /// plausible answer.
    /// </summary>
    internal class RecordingWalletPlayer : IPlayer
    {
        public readonly List<PropertyInt64> Int64Writes = new List<PropertyInt64>();
        public readonly List<PropertyInt64> Int64Reads = new List<PropertyInt64>();

        public int SaveCalls;

        private Account account;

        public RecordingWalletPlayer(uint guid, uint accountId)
        {
            Guid = new ObjectGuid(guid);
            account = new Account { AccountId = accountId, AccountName = "walletfake" };
        }

        public void ClearAccount() => account = null;

        public ObjectGuid Guid { get; }

        public Account Account => account;

        public string Name => "WalletFake";

        public bool? GetProperty(PropertyBool property) => null;
        public uint? GetProperty(PropertyDataId property) => null;
        public double? GetProperty(PropertyFloat property) => null;
        public uint? GetProperty(PropertyInstanceId property) => null;
        public int? GetProperty(PropertyInt property) => null;

        public long? GetProperty(PropertyInt64 property)
        {
            Int64Reads.Add(property);
            return null;
        }

        public string GetProperty(PropertyString property) => null;

        public void SetProperty(PropertyBool property, bool value) { }
        public void SetProperty(PropertyDataId property, uint value) { }
        public void SetProperty(PropertyFloat property, double value) { }
        public void SetProperty(PropertyInstanceId property, uint value) { }
        public void SetProperty(PropertyInt property, int value) { }

        public void SetProperty(PropertyInt64 property, long value) => Int64Writes.Add(property);

        public void SetProperty(PropertyString property, string value) { }

        public void RemoveProperty(PropertyBool property) { }
        public void RemoveProperty(PropertyDataId property) { }
        public void RemoveProperty(PropertyFloat property) { }
        public void RemoveProperty(PropertyInstanceId property) { }
        public void RemoveProperty(PropertyInt property) { }
        public void RemoveProperty(PropertyInt64 property) => Int64Writes.Add(property);
        public void RemoveProperty(PropertyString property) { }

        public int? Level => null;
        public int? Heritage => null;
        public int? Gender => null;

        public bool IsDeleted => false;
        public bool IsPendingDeletion => false;

        public uint? MonarchId { get; set; }
        public uint? PatronId { get; set; }
        public ulong AllegianceXPCached { get; set; }
        public ulong AllegianceXPGenerated { get; set; }
        public int? AllegianceRank { get; set; }
        public int? AllegianceOfficerRank { get; set; }
        public bool ExistedBeforeAllegianceXpChanges { get; set; }
        public uint? HouseId { get; set; }
        public uint? HouseInstance { get; set; }
        public int? HousePurchaseTimestamp { get; set; }
        public int? HouseRentTimestamp { get; set; }

        public uint GetCurrentLoyalty() => 0;
        public uint GetCurrentLeadership() => 0;

        public Allegiance Allegiance { get; set; }
        public AllegianceNode AllegianceNode { get; set; }

        public void SaveBiotaToDatabase(bool enqueueSave = true) => SaveCalls++;

        public void UpdateProperty(PropertyInstanceId prop, uint? value, bool broadcast = false) { }
    }
}
