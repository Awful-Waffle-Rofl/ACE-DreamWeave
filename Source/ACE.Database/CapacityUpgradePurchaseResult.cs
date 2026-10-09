namespace ACE.Database
{
    /// <summary>
    /// The outcome of one capacity upgrade purchase (<see cref="ShardDatabase.TryPurchaseCapacityUpgrade"/>).
    ///
    /// The debit and the count increment commit or roll back together in one transaction, so only
    /// <see cref="Unknown"/> leaves the ledger in a state nobody can describe. Every other value is
    /// definitive: either both moved or neither did.
    /// </summary>
    public enum CapacityUpgradePurchaseResult
    {
        /// <summary>
        /// The pyreals were debited, the count went from the expected value to expected + 1, and the
        /// purchase row was written - or a retried attempt found its own purchase token already
        /// committed. The new balance is known.
        /// </summary>
        Applied,

        /// <summary>
        /// The account bank held fewer pyreals than the cost (or the account has no bank row). Nothing
        /// moved.
        /// </summary>
        InsufficientFunds,

        /// <summary>
        /// The account's count for this kind was not the expected value, so the quoted price was stale.
        /// The debit was rolled back; nothing moved. Re-read the count and quote again.
        /// </summary>
        PriceChanged,

        /// <summary>
        /// The request itself was malformed (account 0, a kind that is not a member of
        /// <see cref="CapacityUpgradeKind"/>, a negative expected count, a non-positive cost, or a
        /// token that is not 32 lower-case hex characters). Refused before any statement ran; nothing moved.
        /// </summary>
        InvalidRequest,

        /// <summary>
        /// Something threw. Whether the purchase committed is UNKNOWN: a lost response after a
        /// successful commit looks exactly like a transaction that never ran. Never a refusal - a
        /// caller must not tell the player the purchase failed, and must drop every cached count and
        /// balance for the account.
        /// </summary>
        Unknown,
    }
}
