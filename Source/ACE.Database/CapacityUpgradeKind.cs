namespace ACE.Database
{
    /// <summary>
    /// Which account-wide capacity an upgrade raises. Stored as account_capacity_upgrade.upgrade_Kind
    /// (tinyint unsigned), so the numeric values are persisted and must never be renumbered.
    ///
    /// 0 is deliberately not a member: a default-initialized byte must never read as a real kind.
    /// </summary>
    public enum CapacityUpgradeKind : byte
    {
        /// <summary>/mule upgrade - extra mule vault entries.</summary>
        MuleVault = 1,

        /// <summary>/market upgrade - extra maximum active market listings.</summary>
        MarketListings = 2,
    }
}
