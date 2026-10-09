namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// How far the most recent <see cref="AccountVaultStore.TryDeposit(ACE.Server.WorldObjects.WorldObject, VaultActor, out string)"/>
    /// got past its point of no return, read through <see cref="AccountVaultStore.LastDepositCommit"/>.
    ///
    /// It exists for callers that must decide, AFTER a deposit refused or threw, whether the item is
    /// now the depositing account's. The market's delivery is the reason: a purchase that fails part
    /// way hands every undelivered item back to the seller, and handing back one that DID land is a
    /// duplicate - a destroyed ledger or class carrier re-credited to the seller, or a re-parented
    /// biota re-inserted into a second vault. The bool alone cannot say which side of the commit a
    /// refusal or a throw happened on.
    /// </summary>
    internal enum VaultDepositCommit
    {
        /// <summary>Nothing was committed: the item is provably not in this vault and is still the caller's.</summary>
        None = 0,

        /// <summary>
        /// A commit was attempted and its outcome is unknown - a ledger or class credit that reported
        /// Failed, or a throw from inside the credit itself. The item may or may not be this vault's
        /// now, so it must never be handed to anybody else.
        /// </summary>
        Unknown = 1,

        /// <summary>The item is this vault's: the credit landed, or the biota was added to a vault container.</summary>
        Committed = 2,
    }
}
