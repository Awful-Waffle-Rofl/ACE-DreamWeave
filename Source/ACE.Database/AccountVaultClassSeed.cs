namespace ACE.Database
{
    /// <summary>
    /// Everything a brand new <c>account_vault_class</c> row needs that a delta cannot supply: the
    /// denormalized wcid, the identity policy, and the canonical form the key was hashed from.
    ///
    /// It is passed on EVERY adjust call rather than only on the ones that create a row, because the
    /// caller cannot know which those are - the DAO tries the guarded UPDATE first and only discovers
    /// the row is absent when that matches nothing. A withdraw never reaches the create path at all
    /// (see <see cref="ShardDatabase.TryAdjustAccountVaultClass"/>), so a seed handed to one is simply
    /// never read.
    ///
    /// THE CANONICAL FORM IS THE PAYLOAD AND THE KEY IS ITS HASH, so the two must be computed from one
    /// call. A seed whose <see cref="CanonicalForm"/> does not hash to the class key it is stored
    /// under would give the account a row that can never be parsed back into the item it stands for.
    /// </summary>
    public class AccountVaultClassSeed
    {
        /// <summary>Denormalized out of <see cref="CanonicalForm"/> for indexing and display.</summary>
        public uint Wcid { get; set; }

        /// <summary>
        /// The identity policy. 0 is VaultItemClass.ValueExcluded, the v1 ruling: Value is not part of
        /// identity and is pooled in the row's total instead.
        /// </summary>
        public int ValueBandPct { get; set; }

        /// <summary>VaultItemClass.CanonicalForm - the exact text the class key is a hash of.</summary>
        public string CanonicalForm { get; set; }
    }
}
