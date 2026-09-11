namespace ACE.Server.Entity
{
    public class ItemProfile
    {
        // original data struct
        public int Amount;      // sent as int, not as uint -- needs to be verified > 0
        public uint ObjectGuid;

        // extended server data
        public uint WeenieClassId;
        public int? Palette;
        public double? Shade;

        /// <summary>
        /// Ceiling on how many item profiles one Buy or Sell message may carry.
        ///
        /// numItems arrives as an unvalidated uint and is used directly as a loop bound in both
        /// GameActionBuyItems and GameActionSellItems, and the per-profile work is not free: against a
        /// PersonalVendor an unauthorized actor's profile is refused before it can populate the
        /// duplicate guard, so each repetition costs its own shard round trip on the world thread.
        ///
        /// 512 is far above any legitimate client - a player's whole inventory is bounded by pack
        /// slots - so this refuses hostile traffic without ever constraining play.
        /// </summary>
        public const int MaxProfilesPerTransaction = 512;

        /// <summary>
        /// If false, should be rejected as early as possible
        /// </summary>
        public bool IsValidAmount => Amount > 0;

        public ItemProfile(int amount, uint objectGuid)
        {
            Amount = amount;
            ObjectGuid = objectGuid;
        }
    }
}
