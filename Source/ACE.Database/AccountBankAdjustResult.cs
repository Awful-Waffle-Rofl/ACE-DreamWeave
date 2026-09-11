namespace ACE.Database
{
    /// <summary>
    /// The outcome of one <see cref="ShardDatabase.TryAdjustAccountBank"/> call.
    ///
    /// Deliberately a mirror of <see cref="AccountVaultStackAdjustResult"/> rather than a reuse of it.
    /// The four values mean exactly the same things and were arrived at for exactly the same reason -
    /// a bool cannot tell a REFUSAL apart from a database failure, and the two demand opposite
    /// responses from every caller - but the vault enum's members are documented in terms of vault
    /// ledger rows and item withdrawals, and naming a type "AccountVaultStackAdjustResult" in the
    /// middle of a pyreal deposit would put a reader on the wrong table. The two are independent
    /// contracts over independent tables, and coupling them would mean a change made for one had to be
    /// argued for the other.
    ///
    /// The distinction between Failed and Refused is not academic here either. The guarded UPDATE runs
    /// through ExecuteSqlRaw, which does NOT go through MySqlRetryingExecutionStrategy, while the LINQ
    /// read-back that follows it DOES and retries up to the ten attempts
    /// ShardDbContext.OnConfiguring's EnableRetryOnFailure(10) asks for. The two halves therefore fail
    /// independently, and the interesting case - update committed, read-back gone - is precisely the
    /// one a bool erases.
    /// </summary>
    public enum AccountBankAdjustResult
    {
        /// <summary>
        /// The guarded UPDATE matched zero rows: the debit would have taken the balance below zero, the
        /// credit would have taken it past long.MaxValue, or the account has no pool row and the delta
        /// was not a credit. The balance PROVABLY did not move, and this is the only value that means
        /// that. A caller may unwind fully on it.
        ///
        /// In normal play a refused debit means the in-memory cache and the ledger disagree, because
        /// every caller checks affordability against the cache first. Refuse the operation and log it.
        /// </summary>
        Refused,

        /// <summary>
        /// The delta committed and the resulting balance was read back. newBalance is that balance.
        /// </summary>
        Applied,

        /// <summary>
        /// The delta committed, but the balance could not be read back afterwards, so newBalance is
        /// meaningless (0). The caller must treat the mutation as APPLIED - the pyreals really did
        /// move - and must drop any cached balance rather than trusting the one it holds.
        /// </summary>
        AppliedCountUnknown,

        /// <summary>
        /// The UPDATE itself threw, so no response was received. Whether it committed is UNKNOWN: a
        /// lost response after a successful statement looks exactly like a statement that never ran.
        ///
        /// This is NOT a synonym for Refused, and reading it as one is the bug this enum exists to
        /// prevent. Every caller documents which way it fails for this value and why, and logs a
        /// LEDGER STATE UNKNOWN line naming the account, the delta and the operation.
        /// </summary>
        Failed,
    }
}
