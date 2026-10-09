using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Seeds the item-CLASS storage tier's tunable into PropertyManager's static cache.
    ///
    /// Every non-pristine deposit reads account_vault_class_storage, and an UNCACHED
    /// PropertyManager.GetBool falls through to DatabaseManager.ShardConfig, which opens a
    /// ShardDbContext against a shard database no unit test has. That throw happens INSIDE
    /// AccountVaultStore.Enqueue, so the symptom is not "the tunable was missing" - it is a deposit
    /// that returns false with a null fail reason, and the test that reports it is whichever one
    /// happened to assert on the reason string.
    ///
    /// PropertyManager's caches are process-wide and MaxCpuCount is 1, so a class that skips this
    /// seed still passes whenever some earlier class in the run seeded the key, and fails when run
    /// alone. Every test class that constructs an AccountVaultStore calls this from its
    /// [TestInitialize], which is what makes each of them pass in isolation.
    ///
    /// The Assert.IsTrue call is load-bearing twice over: ModifyBool returns false for a key that is not
    /// registered in DefaultPropertyManager, so this also asserts the tunable really is in the registry.
    ///
    /// ONE TUNABLE, NOT TWO. account_vault_class_fold_budget was retired with the background fold
    /// rotation (2026-09-26): the per-pass budget is now the caller's batch size, supplied by
    /// AccountVaultFoldMigration or by a test, so there is nothing left to seed for it.
    /// </summary>
    internal static class VaultClassTestConfig
    {
        /// <summary>
        /// Seeds the class tier's kill switch. ON is the shipped configuration; a test that wants it off
        /// passes false and is responsible for restoring it.
        /// </summary>
        internal static void Seed(bool classStorageEnabled = true)
        {
            Assert.IsTrue(PropertyManager.ModifyBool("account_vault_class_storage", classStorageEnabled),
                "account_vault_class_storage is missing from DefaultBooleanProperties");
        }
    }
}
