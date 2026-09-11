using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Custom Dreamweave Augmentation broker's give-path decision rules
    /// (ACE.Server/Entity/CustomAugBroker.cs) and the account-scoped vault cap they feed
    /// (AccountVaultStore.EffectiveEntryCap).
    ///
    /// Everything here drives the Player-free half of the broker on purpose. The give path itself needs
    /// a live Player, a session and an inventory; the RULE that makes it safe does not, and pulling that
    /// rule out into CustomAugBroker.TryAfford / TryCharge is what lets the short-count case be proved
    /// rather than asserted. The pricing math itself is CustomAugmentations' and is covered in
    /// CustomAugmentationsTests; what is tested here is that the broker charges exactly that price and
    /// that it cannot reach the consume without first covering it.
    /// </summary>
    [TestClass]
    public class CustomAugBrokerTests
    {
        private const int Ceiling = 20;

        private const uint UnknownAccount = 4901;

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // AccountVaultStore.BaseEntryCap reads this through PropertyManager.GetLong, and an uncached
            // GetLong falls through to DatabaseManager.ShardConfig, which opens a ShardDbContext against a
            // shard database this test process does not have. Seeding it here is what keeps the two
            // EffectiveEntryCap tests below from depending on some other test class having run first -
            // the same reasoning AccountVaultStoreTests.Setup records for the same key.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 500),
                "account_vault_entry_cap is missing from DefaultLongProperties");
        }

        // ---------------- the price the broker charges ----------------

        /// <summary>
        /// The broker charges exactly CustomAugmentations.CostFor for the buyer's owned count. Driven
        /// through TryAfford with an unlimited purse so the affordability branch never fires and the only
        /// thing under test is the price it reports.
        /// </summary>
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(7)]
        [DataRow(11)]
        [DataRow(19)]
        [DataRow(20)]
        [DataRow(41)]
        public void TryAfford_ChargesTheCatalogPrice(int owned)
        {
            var expected = CustomAugmentations.CostFor(owned, Ceiling);

            Assert.IsTrue(CustomAugBroker.TryAfford(owned, int.MaxValue, Ceiling, out var cost, out var refusal),
                $"a buyer holding int.MaxValue gems should be able to afford {expected}");

            Assert.AreEqual(expected, cost, $"the broker's price at owned={owned} must be CustomAugmentations.CostFor");
            Assert.IsNull(refusal);
        }

        /// <summary>
        /// The worked example from the request: 4 already owned costs 5 gems.
        /// </summary>
        [TestMethod]
        public void TryAfford_FourOwned_CostsFive()
        {
            Assert.IsTrue(CustomAugBroker.TryAfford(4, 5, Ceiling, out var cost, out _));
            Assert.AreEqual(5L, cost);
        }

        // ---------------- the short count takes NOTHING ----------------

        /// <summary>
        /// THE test this file exists for. A buyer one gem short of the price is refused and the consume is
        /// never entered.
        ///
        /// Why it discriminates: Player.TryConsumeFromInventoryWithNetworking(wcid, amount) has no total
        /// pre-check (Player_Inventory.cs:223). It walks the matching items destroying each in turn, so a
        /// shortfall does not refuse - it destroys every gem the player has and returns TRUE. The
        /// have-versus-cost gate inside TryCharge is the only thing standing between a short buyer and
        /// that, so this asserts on the seam actually being untouched (consumeCalls == 0), not merely on
        /// the return value. Delete the `have &lt; cost` branch from TryAfford and the consume delegate
        /// runs, consumeCalls becomes 1, and this fails - verified by reverting the branch locally,
        /// 2026-09-05.
        /// </summary>
        [TestMethod]
        public void TryCharge_ShortCount_RefusesAndConsumesNothing()
        {
            var consumeCalls = 0;

            // 4 owned prices the next one at 5 gems; the buyer is carrying 4.
            var ok = CustomAugBroker.TryCharge(4, 4, Ceiling, amount => { consumeCalls++; return true; }, out var cost, out var consumed, out var refusal);

            // consumeCalls FIRST, deliberately: it is the assertion that carries the safety property, and
            // asserting it ahead of the return value is what makes a failing revert-check name it.
            Assert.AreEqual(0, consumeCalls, "the consume must never be entered on a short count - it destroys gems it cannot refuse");
            Assert.IsFalse(ok, "a buyer 1 gem short must be refused");
            Assert.IsFalse(consumed, "nothing was taken, so the caller must not be told to log a consume failure");
            Assert.AreEqual(5L, cost);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "5", "the refusal must name the price");
            StringAssert.Contains(refusal, "4", "the refusal must name what the buyer is carrying");
        }

        /// <summary>
        /// CONTROL for the test above: with the price exactly covered, the consume IS entered with the
        /// full cost. Without this, the short-count assertion would pass just as happily against a
        /// TryCharge that never calls its consume delegate at all.
        /// </summary>
        [TestMethod]
        public void TryCharge_ExactCount_ConsumesTheWholePrice()
        {
            var consumeCalls = 0;
            var consumedAmount = 0;

            var ok = CustomAugBroker.TryCharge(4, 5, Ceiling, amount => { consumeCalls++; consumedAmount = amount; return true; }, out var cost, out var consumed, out var refusal);

            Assert.IsTrue(ok, "5 gems covers a price of 5");
            Assert.AreEqual(1, consumeCalls);
            Assert.AreEqual(5, consumedAmount, "the whole price must be consumed in one call");
            Assert.AreEqual(5L, cost);
            Assert.IsTrue(consumed);
            Assert.IsNull(refusal);
        }

        /// <summary>
        /// A consume that fails AFTER the affordability gate passed is the "contact staff" case: gems may
        /// already be gone, so the caller is told the charge was entered and must not grant the reward.
        /// </summary>
        [TestMethod]
        public void TryCharge_ConsumeFails_ReportsThatItWasEntered()
        {
            var ok = CustomAugBroker.TryCharge(4, 5, Ceiling, amount => false, out _, out var consumed, out var refusal);

            Assert.IsFalse(ok);
            Assert.IsTrue(consumed, "the consume was entered, so the caller must log and must NOT grant");
            Assert.IsNotNull(refusal);
        }

        // ---------------- account-scoped counting ----------------

        /// <summary>
        /// An account PlayerManager knows nothing about counts zero augmentations. That is the safe
        /// default: GetAccountPlayersSnapshot returns an empty list for an unknown account, and every
        /// existing account-vault test relies on the resulting bonus being zero.
        /// </summary>
        [TestMethod]
        public void AccountAugCount_UnknownAccount_IsZero()
        {
            Assert.AreEqual(0, CustomAugBroker.AccountAugCount(UnknownAccount, PropertyInt.AugmentationMuleSpace));
        }

        /// <summary>Account id 0 is not a real account and is never scanned.</summary>
        [TestMethod]
        public void AccountAugCount_AccountZero_IsZero()
        {
            Assert.AreEqual(0, CustomAugBroker.AccountAugCount(0, PropertyInt.AugmentationMuleSpace));
        }

        // ---------------- the effective vault cap ----------------

        /// <summary>
        /// A store for an account PlayerManager knows nothing about gets the base cap and nothing more.
        /// This is the invariant the whole existing account-vault suite rests on: those tests construct
        /// stores for accounts that were never registered with PlayerManager, and every one of their cap
        /// assertions would move if an unknown account picked up a bonus.
        /// </summary>
        [TestMethod]
        public void EffectiveEntryCap_UnknownAccount_EqualsBaseEntryCap()
        {
            var store = new AccountVaultStore(UnknownAccount, backend, world);

            Assert.AreEqual(AccountVaultStore.BaseEntryCap, store.EffectiveEntryCap);
        }

        /// <summary>
        /// A non-zero account augmentation count raises the cap by 100 entries each. Driven through the
        /// internal count setter rather than through PlayerManager, because PlayerManager holds no
        /// characters in this process - the count itself is covered by AccountAugCount above, and what is
        /// under test here is the arithmetic between the count and the cap.
        /// </summary>
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(7)]
        public void EffectiveEntryCap_WithAugs_AddsAHundredEach(int augs)
        {
            var store = new AccountVaultStore(UnknownAccount, backend, world);

            store.SetMuleSpaceBonusEntries(augs);

            Assert.AreEqual(AccountVaultStore.BaseEntryCap + (augs * CustomAugBroker.MuleSpaceEntriesPerAug), store.EffectiveEntryCap);
        }

        /// <summary>
        /// The clamp holds at both ends: a nonsensical negative count cannot lower the cap below the base,
        /// and a count large enough to overflow the addition saturates at int.MaxValue instead of wrapping
        /// to a negative cap that would refuse every deposit.
        /// </summary>
        [TestMethod]
        public void EffectiveEntryCap_IsClampedAtBothEnds()
        {
            var store = new AccountVaultStore(UnknownAccount, backend, world);

            store.SetMuleSpaceBonusEntries(-5);
            Assert.AreEqual(AccountVaultStore.BaseEntryCap, store.EffectiveEntryCap, "a bonus can never subtract");

            store.SetMuleSpaceBonusEntries(int.MaxValue);
            Assert.AreEqual(int.MaxValue, store.EffectiveEntryCap, "the cap must saturate, not wrap");
        }
    }
}
