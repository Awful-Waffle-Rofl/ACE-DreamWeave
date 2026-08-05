using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the banking amount parser and alternate-currency lookup. The parser is the
    /// security-sensitive surface (crafted amounts must never overflow into a bogus/negative long, and
    /// negatives must be rejected). Deposit/withdraw/transfer flows mutate inventory + DB and are
    /// exercised in-game via the test-loop rather than here.
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
            Assert.IsNull(Player.GetAlternateCurrencyName(273));    // coin
            Assert.IsNull(Player.GetAlternateCurrencyName(35810));  // Hero Token (a non-banked alt currency)
        }

        /// <summary>
        /// The Class Ability Point vendor currency is not a bankable item - its balance IS the player's
        /// AvailableClassAbilityPoints. This covers the credit -> read -> debit -> read cycle at the pure core
        /// the instance hook (Player.DebitBankedAlternateCurrency for wcid 1001010) delegates to; the live
        /// property round-trip + SaveBiotaToDatabase is exercised in-game (it needs a real Player + shard DB).
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
        /// Withdrawals hand out STACKS, not singles (/b w n used to emit one 1-count note per pack slot).
        /// Player.NextStackSize is the pure core of that: given what is still owed and the created object's own
        /// MaxStackSize, it decides how big the next stack is. The surrounding loop and the partial-failure
        /// accounting need a live Player + inventory and are exercised in-game via the test loop.
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
        /// banked all their coin is shown 0p and the client will not send the purchase at all.
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
        /// Peas are loot-drop spell components with an outsized value-to-burden ratio, banked as pyreals at
        /// face value. The face value comes from a hardcoded allowlist rather than the item's own Value, so
        /// this pins both halves: the three bankable wcids credit exactly their retail value, and every other
        /// wcid (including the cheaper peas in the same family) is refused. The inventory sweep itself
        /// (DepositPeas) needs a live Player and is exercised in-game.
        /// </summary>
        [TestMethod]
        public void TryGetFaceValue_BankablePeasCreditRetailFaceValue()
        {
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
            // the cheaper peas in the same family are deliberately NOT bankable
            Assert.IsFalse(BankablePeas.TryGetFaceValue(8329, 1, out var lead));    // Lead Pea
            Assert.AreEqual(0, lead);
            Assert.IsFalse(BankablePeas.TryGetFaceValue(8328, 1, out _));           // Iron Pea
            Assert.IsFalse(BankablePeas.TryGetFaceValue(8326, 1, out _));           // Copper Pea

            // the real scarab components must never be swept up as peas
            Assert.IsFalse(BankablePeas.TryGetFaceValue(690, 1, out _));            // Pyreal Scarab
            Assert.IsFalse(BankablePeas.TryGetFaceValue(687, 1, out _));            // Gold Scarab

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
        /// EmoteManager's InqInt64Stat handler special-cases PropertyInt64.AvailableLuminance so the
        /// affordability precheck counts banked Luminance, matching what SpendLuminance actually spends.
        /// That special case matches on the enum value, while the 290 emote rows that drive it (across 24
        /// weenies - every Seer and Mastery object) store the literal 6 in weenie_properties_emote_action.stat.
        /// Renumbering the enum would silently unhook the fix and bring back "You do not have enough
        /// Luminance." for players holding banked Luminance, with nothing failing to warn about it.
        /// </summary>
        [TestMethod]
        public void AvailableLuminance_KeepsTheStatIdTheWorldDataUses()
        {
            Assert.AreEqual(6, (int)PropertyInt64.AvailableLuminance);
        }

        /// <summary>
        /// The stack-splitting arithmetic behind the coin payout a player gets when vendor auto-deposit is off
        /// (/b ad off). It is the only part of that path that can be tested here: creating the coin objects and
        /// placing them needs a live Player and world database, so that half is exercised in-game. Conservation
        /// is the property that matters - every pyreal the vendor owes must appear in exactly one stack, since
        /// the sell path pays out whatever these sizes say and banks only what the pack refused.
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
    }
}
