namespace ACE.Database
{
    /// <summary>
    /// The outcome of one <see cref="ShardDatabase.TryAdjustAccountVaultStack"/> call.
    ///
    /// IT NOW SERVES BOTH LEDGERS. <see cref="ShardDatabase.TryAdjustAccountVaultClass"/> returns the
    /// same four values with the same obligations, deliberately reusing this rather than declaring a
    /// parallel enum: the contract is identical down to the reasoning below, and two copies of it
    /// would drift the moment one of them grew a fifth member. The name still says Stack because the
    /// stack ledger was first and renaming a public enum buys nothing.
    ///
    /// Fix round 2, F1. This enum exists because a bool could not tell a REFUSAL apart from a database
    /// failure, and the two demand opposite responses from every caller. The old signature returned
    /// false for both, and <see cref="ACE.Database.Models.Shard.AccountVaultStack"/>'s callers all read
    /// false as "the ledger did NOT move" - so a delta that had actually committed, followed by a
    /// failed read-back, was unwound as though nothing had happened. On the withdraw path that wrote a
    /// balancing Return audit row and delivered nothing: a silent item loss with a perfectly balanced
    /// audit trail, which is the one shape an investigation cannot resolve afterwards.
    ///
    /// The distinction is not academic. The guarded UPDATE runs through ExecuteSqlRaw, which does NOT
    /// go through MySqlRetryingExecutionStrategy, while the LINQ read-back that follows it DOES and so
    /// retries up to ten times. The two halves therefore fail independently and for different reasons,
    /// and the interesting case - update committed, read-back gone - is precisely the one a bool
    /// erased.
    /// </summary>
    public enum AccountVaultStackAdjustResult
    {
        /// <summary>
        /// The guarded UPDATE matched zero rows: an over-withdraw, or a withdraw against a row that
        /// does not exist. The ledger PROVABLY did not move, and this is the only value that means
        /// that. A caller may unwind fully on it.
        /// </summary>
        Refused,

        /// <summary>
        /// The delta committed and the resulting count was read back. newCount is that count.
        /// </summary>
        Applied,

        /// <summary>
        /// The delta committed, but the count could not be read back afterwards, so newCount is
        /// meaningless (0). The caller must treat the mutation as APPLIED - the units really did move -
        /// and must refresh its in-memory ledger from the shard rather than trusting the count it
        /// holds.
        /// </summary>
        AppliedCountUnknown,

        /// <summary>
        /// The UPDATE itself threw, so no response was received. Whether it committed is UNKNOWN: a
        /// lost response after a successful statement looks exactly like a statement that never ran.
        ///
        /// This is NOT a synonym for Refused, and reading it as one is the bug this enum exists to
        /// prevent. Every caller documents which way it fails for this value and why - see
        /// AccountVaultStore's deposit/withdraw asymmetry.
        /// </summary>
        Failed,
    }
}
