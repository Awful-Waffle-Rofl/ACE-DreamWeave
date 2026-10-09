using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the banking amount parser and alternate-currency lookup. The parser is the
    /// security-sensitive surface (crafted amounts must never overflow into a bogus/negative long, and
    /// negatives must be rejected). The account-wide pyreal pool itself (credit/debit/overflow/claim,
    /// against a fake IAccountBankBackend) is exercised in <see cref="AccountBankTests"/>; the
    /// Player-level deposit/withdraw/transfer flows still mutate inventory + DB and are exercised
    /// in-game via the test-loop rather than here.
    /// </summary>
    [TestClass]
    public class BankTests
    {
        private static long Parse(string input)
        {
            Assert.IsTrue(Player.TryParseAmount(input, out var value), $"expected '{input}' to parse");
            return value;
        }

        private static void ParseFails(string input)
        {
            Assert.IsFalse(Player.TryParseAmount(input, out var value), $"expected '{input}' to be rejected (got {value})");
        }

        [TestMethod]
        public void TryParseAmount_PlainNumbers()
        {
            Assert.AreEqual(0, Parse("0"));
            Assert.AreEqual(1000, Parse("1000"));
            Assert.AreEqual(25000, Parse("25000"));
        }

        [TestMethod]
        public void TryParseAmount_Suffixes()
        {
            Assert.AreEqual(10_000, Parse("10k"));
            Assert.AreEqual(1_000_000, Parse("1m"));
            Assert.AreEqual(2_000_000_000, Parse("2b"));
            Assert.AreEqual(1_000_000_000_000, Parse("1t"));
            Assert.AreEqual(1_000_000_000_000_000, Parse("1q"));
        }

        [TestMethod]
        public void TryParseAmount_DecimalsAndCase()
        {
            Assert.AreEqual(1_500_000, Parse("1.5m"));
            Assert.AreEqual(1_550_000, Parse("1.55m"));
            Assert.AreEqual(1_500, Parse("1.5k"));
            Assert.AreEqual(10_000, Parse("10K"));   // uppercase suffix
            Assert.AreEqual(10_000, Parse("  10k ")); // surrounding whitespace
        }

        [TestMethod]
        public void TryParseAmount_MaxLong()
        {
            Assert.AreEqual(long.MaxValue, Parse("9223372036854775807"));
        }

        [TestMethod]
        public void TryParseAmount_RejectsEmptyAndNull()
        {
            ParseFails(null);
            ParseFails("");
            ParseFails("   ");
        }

        [TestMethod]
        public void TryParseAmount_RejectsNegatives()
        {
            ParseFails("-5");
            ParseFails("-1k");
            ParseFails("-0.5m");
        }

        [TestMethod]
        public void TryParseAmount_RejectsNonNumeric()
        {
            ParseFails("abc");
            ParseFails("10x");    // x is not a magnitude suffix
            ParseFails("1kk");    // double suffix
            ParseFails("k");      // suffix with no number
            ParseFails("1.2.3");
        }

        [TestMethod]
        public void TryParseAmount_RejectsOverflow()
        {
            // These would overflow a long if multiplied blindly; must be rejected, not wrapped.
            ParseFails("999999999999q");        // 1e12 * 1e15 = 1e27
            ParseFails("9999999999999999999");  // ~1e19 > long.MaxValue
            ParseFails("9.3e18");               // just over long.MaxValue (~9.22e18)
        }

        [TestMethod]
        public void GetAlternateCurrencyName_KnownBankBackedCurrencies()
        {
            Assert.AreEqual("Promissory Notes", Player.GetAlternateCurrencyName(43901));
            // Class Ability Point currency weenie (Content/sql/weenies/1001010_classabilitypoint_currency.sql):
            // its "bank balance" is the CAP property, so the shop hook recognizes it as a named alt currency.
            Assert.AreEqual("Class Ability Points", Player.GetAlternateCurrencyName(1001010));
            // MMD (tradenote250000, Factories/Enum/WeenieClassName.cs) - backed by banked pyreals, not its own balance.
            Assert.AreEqual("Trade Notes (250,000)", Player.GetAlternateCurrencyName(20630));
            Assert.IsNull(Player.GetAlternateCurrencyName(273));    // coin
            Assert.IsNull(Player.GetAlternateCurrencyName(35810));  // Hero Token (a non-banked alt currency)
        }

        /// <summary>
        /// Pure debit core for the MMD vendor currency (wcid 20630, 250,000 pyreals/note); the live
        /// property mutation + save needs a real Player and is exercised in-game.
        /// </summary>
        [TestMethod]
        public void TryDebitBankedMmd_AffordableDebitReturnsExactPyrealCost()
        {
            // exactly 4 notes' worth banked, debit all 4 -> costs all of it
            Assert.IsTrue(Player.TryDebitBankedMmd(1_000_000, 4, out var cost));
            Assert.AreEqual(1_000_000, cost);

            // a partial debit costs only its share
            Assert.IsTrue(Player.TryDebitBankedMmd(1_000_000, 1, out cost));
            Assert.AreEqual(250_000, cost);

            // a zero debit is a no-op success
            Assert.IsTrue(Player.TryDebitBankedMmd(1_000_000, 0, out cost));
            Assert.AreEqual(0, cost);

            // a remainder left over from floor division (900,000 = 3 notes + 150,000 leftover pyreals)
            // still affords exactly 3 notes
            Assert.IsTrue(Player.TryDebitBankedMmd(900_000, 3, out cost));
            Assert.AreEqual(750_000, cost);
        }

        [TestMethod]
        public void TryDebitBankedMmd_RejectsOverdraftAndNegative_WithoutMutating()
        {
            // more notes than the bank can cover: refused, out param left at 0 (caller must not hand over goods)
            Assert.IsFalse(Player.TryDebitBankedMmd(900_000, 4, out var cost)); // 900,000 covers only 3 notes
            Assert.AreEqual(0, cost);

            // a negative amount (crafted/garbage) can never credit pyreals back
            Assert.IsFalse(Player.TryDebitBankedMmd(1_000_000, -1, out cost));
            Assert.AreEqual(0, cost);

            // zero banked pyreals can never afford even one note
            Assert.IsFalse(Player.TryDebitBankedMmd(0, 1, out cost));
            Assert.AreEqual(0, cost);
        }

        /// <summary>
        /// Credit -> read -> debit -> read cycle for the Class Ability Point currency's pure core
        /// (wcid 1001010); the live property round-trip is exercised in-game.
        /// </summary>
        [TestMethod]
        public void TryDebitClassAbilityPoints_CreditReadDebitRead()
        {
            // credit -> read: a balance of 10 is fully available
            var balance = 10;

            // debit 3 -> read: 7 left
            Assert.IsTrue(Player.TryDebitClassAbilityPoints(balance, 3, out balance));
            Assert.AreEqual(7, balance);

            // debit the exact remaining balance -> read: 0 left
            Assert.IsTrue(Player.TryDebitClassAbilityPoints(balance, 7, out balance));
            Assert.AreEqual(0, balance);

            // a zero debit is a no-op success
            Assert.IsTrue(Player.TryDebitClassAbilityPoints(balance, 0, out balance));
            Assert.AreEqual(0, balance);
        }

        [TestMethod]
        public void TryDebitClassAbilityPoints_RejectsOverdraftAndNegative_WithoutMutating()
        {
            // more than the balance: refused, balance unchanged (caller must not hand over goods)
            Assert.IsFalse(Player.TryDebitClassAbilityPoints(5, 6, out var after));
            Assert.AreEqual(5, after, "an unaffordable debit must leave the balance untouched");

            // a negative amount (crafted/garbage) can never credit points back
            Assert.IsFalse(Player.TryDebitClassAbilityPoints(5, -1, out after));
            Assert.AreEqual(5, after);

            // an int64 amount beyond int range is unaffordable, not an overflow/wrap
            Assert.IsFalse(Player.TryDebitClassAbilityPoints(5, (long)int.MaxValue + 1, out after));
            Assert.AreEqual(5, after);
        }

        /// <summary>
        /// Withdrawals hand out STACKS, not singles (/b w n used to emit one 1-count note per slot).
        /// Player.NextStackSize is that decision's pure core; the surrounding loop is exercised in-game.
        /// </summary>
        [TestMethod]
        public void NextStackSize_ClampsToTheObjectsOwnMaxStackSize()
        {
            // fits in one stack: hand out exactly what is owed
            Assert.AreEqual(500, Player.NextStackSize(500, 1000));

            // exactly one stack
            Assert.AreEqual(1000, Player.NextStackSize(1000, 1000));

            // more than one stack: this call fills a whole stack, the caller loops for the rest
            Assert.AreEqual(1000, Player.NextStackSize(2500, 1000));

            // the cap is the weenie's, not a hardcoded 1000 - the pre-patch trade note value still works
            Assert.AreEqual(250, Player.NextStackSize(2500, 250));

            // and a coinstack-sized cap is honoured just the same
            Assert.AreEqual(25000, Player.NextStackSize(1_000_000, 25000));
        }

        [TestMethod]
        public void NextStackSize_FallsBackToSinglesRatherThanOverCrediting()
        {
            // no MaxStackSize on the object at all: one unit per object, never a phantom full stack
            Assert.AreEqual(1, Player.NextStackSize(500, null));

            // a nonsense 0 cap is treated the same way
            Assert.AreEqual(1, Player.NextStackSize(500, 0));

            // an explicitly non-stackable weenie
            Assert.AreEqual(1, Player.NextStackSize(500, 1));
        }

        [TestMethod]
        public void NextStackSize_NarrowsWithoutWrapping()
        {
            // owing more than int.MaxValue units must clamp to the cap, never wrap to a negative stack size
            Assert.AreEqual(1000, Player.NextStackSize(long.MaxValue, 1000));
            Assert.AreEqual(65535, Player.NextStackSize(long.MaxValue, ushort.MaxValue));

            // degenerate "nothing owed" inputs never produce a zero or negative stack
            Assert.AreEqual(1, Player.NextStackSize(0, 1000));
            Assert.AreEqual(1, Player.NextStackSize(-5, 1000));
        }

        /// <summary>
        /// The vendor panel ("you have Np") and the client's own affordability check both read
        /// PropertyInt.CoinValue, so banked pyreals must be included in what we send - otherwise a player who
        /// banked all their coin is shown 0p and the client will not send the purchase at all. Banked pyreals
        /// are no longer a per-character property: the "banked" figure here is the account-wide pool balance,
        /// read through AccountBankManager and passed in as a plain parameter, exactly as the real caller
        /// (Player.GetSpendableCoinValue) reads AccountBankManager.GetBalance(Account.AccountId) before calling in.
        /// </summary>
        [TestMethod]
        public void CalcSpendableCoinValue_CountsBankedPyreals()
        {
            // the reported bug: everything banked, nothing carried -> vendor saw 0p
            Assert.AreEqual(10_000, Player.CalcSpendableCoinValue(0, 10_000));

            Assert.AreEqual(0, Player.CalcSpendableCoinValue(0, 0));
            Assert.AreEqual(250, Player.CalcSpendableCoinValue(250, 0));    // carried only
            Assert.AreEqual(1_250, Player.CalcSpendableCoinValue(250, 1_000)); // carried + banked
        }

        /// <summary>
        /// Peas bank as pyreals at a hardcoded face-value allowlist, not the item's own Value: pins all
        /// six pea wcids plus rejection of everything else, including the real scarabs. DepositPeas itself is exercised in-game.
        /// </summary>
        [TestMethod]
        public void TryGetFaceValue_BankablePeasCreditRetailFaceValue()
        {
            Assert.IsTrue(BankablePeas.TryGetFaceValue(8329, 1, out var lead));
            Assert.AreEqual(500, lead, "Lead Pea (peascarablead) face value");

            Assert.IsTrue(BankablePeas.TryGetFaceValue(8328, 1, out var iron));
            Assert.AreEqual(2_500, iron, "Iron Pea (peascarabiron) face value");

            Assert.IsTrue(BankablePeas.TryGetFaceValue(8326, 1, out var copper));
            Assert.AreEqual(5_000, copper, "Copper Pea (peascarabcopper) face value");

            Assert.IsTrue(BankablePeas.TryGetFaceValue(8331, 1, out var silver));
            Assert.AreEqual(12_500, silver, "Silver Pea (peascarabsilver) face value");

            Assert.IsTrue(BankablePeas.TryGetFaceValue(8327, 1, out var gold));
            Assert.AreEqual(25_000, gold, "Gold Pea (peascarabgold) face value");

            Assert.IsTrue(BankablePeas.TryGetFaceValue(8330, 1, out var pyreal));
            Assert.AreEqual(50_000, pyreal, "Pyreal Pea (peascarabpyreal) face value");
        }

        [TestMethod]
        public void TryGetFaceValue_ScalesByStackSize()
        {
            // peas stack to 100 (weenie_properties_int type 11), so a full stack is the realistic maximum
            Assert.IsTrue(BankablePeas.TryGetFaceValue(8330, 100, out var fullStack));
            Assert.AreEqual(5_000_000, fullStack);

            // a zero/negative stack size (unset or corrupt StackSize) must credit one pea, never zero or less
            Assert.IsTrue(BankablePeas.TryGetFaceValue(8330, 0, out var zeroStack));
            Assert.AreEqual(50_000, zeroStack);

            Assert.IsTrue(BankablePeas.TryGetFaceValue(8330, -5, out var negativeStack));
            Assert.AreEqual(50_000, negativeStack);
        }

        [TestMethod]
        public void TryGetFaceValue_RejectsEverythingElse()
        {
            // the real scarab components must never be swept up as peas
            Assert.IsFalse(BankablePeas.TryGetFaceValue(690, 1, out var pyrealScarab)); // Pyreal Scarab
            Assert.AreEqual(0, pyrealScarab);
            Assert.IsFalse(BankablePeas.TryGetFaceValue(687, 1, out _));            // Gold Scarab
            Assert.IsFalse(BankablePeas.TryGetFaceValue(689, 1, out _));            // Iron Scarab

            Assert.IsFalse(BankablePeas.TryGetFaceValue(273, 1, out _));            // coin
            Assert.IsFalse(BankablePeas.TryGetFaceValue(0, 1, out _));
        }

        [TestMethod]
        public void CalcSpendableCoinValue_ClampsInsteadOfOverflowing()
        {
            // A bank balance is a long: summed naively with coin it would overflow int (and even long),
            // wrapping negative and showing a rich player 0p.
            Assert.AreEqual(int.MaxValue, Player.CalcSpendableCoinValue(0, long.MaxValue));
            Assert.AreEqual(int.MaxValue, Player.CalcSpendableCoinValue(int.MaxValue, long.MaxValue));
            Assert.AreEqual(int.MaxValue, Player.CalcSpendableCoinValue(int.MaxValue, 1));

            // negatives (corrupt/unset state) must not produce a negative spendable total
            Assert.AreEqual(0, Player.CalcSpendableCoinValue(-1, -1));
            Assert.AreEqual(500, Player.CalcSpendableCoinValue(-1, 500));
        }

        /// <summary>
        /// EmoteManager's InqInt64Stat special-cases PropertyInt64.AvailableLuminance (stat id 6) so banked
        /// Luminance counts toward affordability; 290 emote rows store that literal 6, so renumbering the
        /// enum would silently unhook the fix with nothing failing to warn about it.
        /// </summary>
        [TestMethod]
        public void AvailableLuminance_KeepsTheStatIdTheWorldDataUses()
        {
            Assert.AreEqual(6, (int)PropertyInt64.AvailableLuminance);
        }

        /// <summary>
        /// Stack-splitting for the vendor auto-deposit-off (/b ad off) coin payout. Conservation is what's
        /// tested: every pyreal must appear in exactly one stack; creating/placing the objects is exercised in-game.
        /// </summary>
        [TestMethod]
        public void CalcPayoutStackSizes_ConservesTheTotal()
        {
            foreach (var amount in new[] { 1, 999, 25_000, 25_001, 1_234_567 })
            {
                var sizes = Player.CalcPayoutStackSizes(amount, 25_000);

                var total = 0;
                foreach (var size in sizes)
                    total += size;

                Assert.AreEqual(amount, total, $"payout of {amount} must be conserved across its stacks");
            }
        }

        [TestMethod]
        public void CalcPayoutStackSizes_NeverExceedsMaxStackSize()
        {
            foreach (var amount in new[] { 1, 999, 25_000, 25_001, 1_234_567 })
            {
                foreach (var size in Player.CalcPayoutStackSizes(amount, 25_000))
                {
                    Assert.IsTrue(size > 0 && size <= 25_000, $"stack size {size} out of range for a payout of {amount}");
                }
            }
        }

        [TestMethod]
        public void CalcPayoutStackSizes_ExactMultipleHasNoRemainderStack()
        {
            var sizes = Player.CalcPayoutStackSizes(75_000, 25_000);

            Assert.AreEqual(3, sizes.Count);
            foreach (var size in sizes)
                Assert.AreEqual(25_000, size);
        }

        [TestMethod]
        public void CalcPayoutStackSizes_ZeroAndNegativeAmountsPayNothing()
        {
            Assert.AreEqual(0, Player.CalcPayoutStackSizes(0, 25_000).Count);
            Assert.AreEqual(0, Player.CalcPayoutStackSizes(-1, 25_000).Count);
            Assert.AreEqual(0, Player.CalcPayoutStackSizes(int.MinValue, 25_000).Count);
        }

        [TestMethod]
        public void CalcPayoutStackSizes_RejectsNonPositiveMaxStackSize()
        {
            // a coin weenie with a missing/nonsense MaxStackSize must fail loudly rather than loop forever
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Player.CalcPayoutStackSizes(1_000, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Player.CalcPayoutStackSizes(1_000, -25_000));
        }

        [TestMethod]
        public void CalcPayoutStackSizes_RealisticSale()
        {
            // coinstack (wcid 273) carries MaxStackSize 25,000 in the world db (weenie_properties_int type 11),
            // so a 1,000,000p sale pays out as 40 full stacks with no remainder
            var sizes = Player.CalcPayoutStackSizes(1_000_000, 25_000);

            Assert.AreEqual(40, sizes.Count);
            foreach (var size in sizes)
                Assert.AreEqual(25_000, size);
        }

        [TestMethod]
        public void MarketWallet_MmdConversion_FloorsAndNeverOverdraws()
        {
            // The market prices in whole MMD; rounding instead of flooring would let a buyer spend a note they lack.
            Assert.IsTrue(Player.TryDebitBankedMmd(250_000L * 4, 4, out var exact));
            Assert.AreEqual(250_000L * 4, exact);

            // 3.999 notes' worth of pyreals buys three notes, not four.
            Assert.IsTrue(Player.TryDebitBankedMmd(250_000L * 4 - 1, 3, out var three));
            Assert.AreEqual(250_000L * 3, three);
            Assert.IsFalse(Player.TryDebitBankedMmd(250_000L * 4 - 1, 4, out _));

            Assert.IsFalse(Player.TryDebitBankedMmd(250_000L * 4, -1, out _), "a negative debit must be refused");
            Assert.IsTrue(Player.TryDebitBankedMmd(0, 0, out var zero), "a zero debit is legal and costs nothing");
            Assert.AreEqual(0, zero);
        }

        [TestMethod]
        public void MarketWallet_MmdConstantsAreTheOnesTheBankUses()
        {
            // Market prices are meaningless if these drift from the bank's own pair.
            Assert.AreEqual(20630u, Player.MmdWcid);
            Assert.AreEqual(250_000L, Player.MmdValue);
        }

        [TestMethod]
        public void MarketWallet_CreditCore_RefusesANoteCountThatWouldWrapLong()
        {
            // Both credit helpers apply this delta to a balance, so a refusal must yield 0 and never the
            // NEGATIVE product an unguarded multiply produces, which would debit the payee.
            var maxNotes = long.MaxValue / 250_000L;

            Assert.IsTrue(Player.TryCreditBankedMmd(maxNotes, out var atMax));
            Assert.AreEqual(maxNotes * 250_000L, atMax);
            Assert.IsTrue(atMax > 0, "the largest accepted credit must still be positive");

            Assert.IsFalse(Player.TryCreditBankedMmd(maxNotes + 1, out var over), "a credit that would wrap long must be refused");
            Assert.AreEqual(0, over, "a refused credit must move the balance by nothing, not by a wrapped negative");
            Assert.IsFalse(Player.TryCreditBankedMmd(long.MaxValue, out var wrapped));
            Assert.AreEqual(0, wrapped);

            Assert.IsFalse(Player.TryCreditBankedMmd(0, out _), "a zero credit is not a credit");
            Assert.IsFalse(Player.TryCreditBankedMmd(-1, out _));
        }

    }
}
