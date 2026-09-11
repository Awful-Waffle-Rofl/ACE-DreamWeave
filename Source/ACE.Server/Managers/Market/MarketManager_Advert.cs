using System.Linq;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// MarketAdvertiser's one dependency on the index. Split out of MarketManager.cs rather than
    /// added there, so the periodic-post feature stays reviewable on its own.
    /// </summary>
    public static partial class MarketManager
    {
        /// <summary>
        /// The most recently created ACTIVE listing with a name, or null when there is none. A sold or
        /// delisted listing is deliberately excluded - advertising something no longer for sale would
        /// send a player straight to a dead link.
        /// </summary>
        public static MarketListing GetNewestActiveListing()
        {
            lock (indexLock)
                return listings.Values
                    .Where(l => l.Status == MarketListingStatus.Active && l.Snapshot?.Name != null)
                    .OrderByDescending(l => l.CreatedAt)
                    .ThenByDescending(l => l.Id)
                    .Select(Copy)
                    .FirstOrDefault();
        }
    }
}
