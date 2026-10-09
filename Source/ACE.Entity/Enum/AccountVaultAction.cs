namespace ACE.Entity.Enum
{
    /// <summary>
    /// Mule Vendor audit actions. Persisted as an int in account_vault_log.action, NOT as the enum
    /// type, so adding a member here can never break deserialization of rows already written.
    /// Values are frozen once written; append only.
    /// </summary>
    public enum AccountVaultAction
    {
        Deposit  = 0,
        Withdraw = 1,
        Grant    = 2,
        Revoke   = 3,

        /// <summary>
        /// An item that was withdrawn but could not be delivered, put back into the vault. It pairs
        /// with the Withdraw row immediately above it: without this row the log shows an item leaving
        /// the vault while the item is in fact sitting back inside it, which is a shape no dupe or
        /// theft investigation can resolve from the log alone.
        /// </summary>
        Return   = 4,

        /// <summary>
        /// An item the player destroyed by feeding it to the barrel. It is a soft delete: the item
        /// is still recoverable by an administrator until retention purges it, and the
        /// account_vault_barrel row records that separately and outlives the item.
        /// </summary>
        Barrel   = 5,

        /// <summary>An administrator returned a barreled item to the account's vault.</summary>
        Restore  = 6,

        /// <summary>
        /// A stored biota collapsed into a counted item-class row by the BACKGROUND FOLD, not by a
        /// player. Mechanically it is a deposit - it routes through the same DepositToClass - but it
        /// is labelled separately on purpose: folding one large vault writes thousands of rows in a
        /// few minutes, and under the Deposit label those would bury the account's real activity in
        /// the same table an investigation reads. The actor on a Fold row is "(vault fold)", never a
        /// character.
        /// </summary>
        Fold     = 7,
    }
}
