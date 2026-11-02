using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>A buyer's PvP record as the prestige gate reads it: the character's own stamps and the live rating store's figures.</summary>
    public readonly record struct PvpPrestigeRecord(int StampPeakRating, int StorePeakRating, int StampBattlegroundWins, int StoreBattlegroundWins)
    {
        /// <summary>The better of the two peak figures.</summary>
        public int PeakRating => Math.Max(StampPeakRating, StorePeakRating);

        /// <summary>The better of the two battleground win counts.</summary>
        public int BattlegroundWins => Math.Max(StampBattlegroundWins, StoreBattlegroundWins);
    }

    /// <summary>
    /// The vendor purchase gate for PvP prestige items (PropertyBool.PvpPrestigeGated). A gated item may be bought only by a character
    /// proven in the Hopeslayer's wars: a peak rating of at least pvp_prestige_min_peak_rating on any ladder, OR at least
    /// pvp_prestige_min_bg_wins battleground wins.
    ///
    /// Each figure is the better of two sources, because neither alone is complete:
    ///  - the character's stamps, PropertyInt.PvpPeakRating (9091) and PropertyInt.PvpLifetimeBattlegroundWins (9090), written by
    ///    Player.ApplyPvpDailyFacts only for matches the character was reward-eligible for;
    ///  - the live rating store (PvpMatchManager.PeakRatingAcrossLadders / BattlegroundLadderWins), which holds every rated result,
    ///    including those from before the stamps existed. It reads 0 while ratings are loading or unavailable.
    ///
    /// Called from Vendor.BuyItems_ValidateTransaction before pricing. A basket with no gated item never reads the record at all.
    /// </summary>
    public static class PvpPrestigeGate
    {
        public const string MinPeakRatingKey = "pvp_prestige_min_peak_rating";

        public const string MinBattlegroundWinsKey = "pvp_prestige_min_bg_wins";

        public const long DefaultMinPeakRating = 1700;

        public const long DefaultMinBattlegroundWins = 100;

        /// <summary>The refusal, with both thresholds filled in from the settings.</summary>
        public static string RefusalText(long minPeak, long minWins) =>
            $"Only those proven in the Hopeslayer's wars may claim his likeness. You need a peak rating of {minPeak} on any ladder or {minWins} Battleground victories.";

        /// <summary>TRUE when the record meets either threshold (each inclusive).</summary>
        public static bool Qualifies(PvpPrestigeRecord record, long minPeak, long minWins) =>
            record.PeakRating >= minPeak || record.BattlegroundWins >= minWins;

        /// <summary>TRUE when any item in the basket carries PropertyBool.PvpPrestigeGated = true.</summary>
        public static bool AnyGated(IEnumerable<WorldObject> items)
        {
            if (items == null)
                return false;

            foreach (var item in items)
            {
                if (item?.GetProperty(PropertyBool.PvpPrestigeGated) == true)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The refusal text when the basket holds a gated item and the buyer does not qualify, otherwise null (the purchase proceeds).
        /// <paramref name="readRecord"/> is called only when the basket holds a gated item. Pure apart from that callback.
        /// </summary>
        public static string Refusal(IEnumerable<WorldObject> items, Func<PvpPrestigeRecord> readRecord, long minPeak, long minWins)
        {
            if (!AnyGated(items))
                return null;

            var record = readRecord != null ? readRecord() : default;

            return Qualifies(record, minPeak, minWins) ? null : RefusalText(minPeak, minWins);
        }

        /// <summary>
        /// The live record for a buyer: its stamps and the rating store's figures for its character id. Takes a WorldObject, not a
        /// Player, so the property-to-field mapping is testable without a live Player (whose constructor reaches the auth database).
        /// </summary>
        public static PvpPrestigeRecord ReadRecord(WorldObject buyer)
        {
            if (buyer == null)
                return default;

            var id = buyer.Guid.Full;

            return new PvpPrestigeRecord(
                StampPeakRating: buyer.GetProperty(PropertyInt.PvpPeakRating) ?? 0,
                StorePeakRating: PvpMatchManager.PeakRatingAcrossLadders(id),
                StampBattlegroundWins: buyer.GetProperty(PropertyInt.PvpLifetimeBattlegroundWins) ?? 0,
                StoreBattlegroundWins: PvpMatchManager.BattlegroundLadderWins(id));
        }

        /// <summary>The live gate for a basket and a buyer, reading both thresholds from PropertyManager.</summary>
        public static string Refusal(IEnumerable<WorldObject> items, Player player)
        {
            if (!AnyGated(items))
                return null;

            var minPeak = PropertyManager.GetLong(MinPeakRatingKey, DefaultMinPeakRating).Item;
            var minWins = PropertyManager.GetLong(MinBattlegroundWinsKey, DefaultMinBattlegroundWins).Item;

            return Refusal(items, () => ReadRecord(player), minPeak, minWins);
        }
    }
}
