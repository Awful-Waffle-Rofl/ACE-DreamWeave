namespace ACE.Server.Knots
{
    /// <summary>The Drops track's additions to the shared Knots vocabulary (Docs/Knots/KNOTS-PLAN.md, Track A).</summary>
    public static partial class KnotItems
    {
        /// <summary>
        /// The Knotted Thread Cache's unadorned name. MUST equal the Name row of weenie
        /// <see cref="KnottedThreadCacheWcid"/> in Content/sql/weenies/1007004 Knotted Thread Cache.sql (a test
        /// asserts it): the spawner composes "Holder's Knotted Thread Cache" from this constant, never from the
        /// chest's current name, exactly as it does for the plain Thread Cache.
        /// </summary>
        public const string KnottedThreadCacheName = "Knotted Thread Cache";

        /// <summary>
        /// The Pinnacle Cache's unadorned name. MUST equal the Name row of weenie <see cref="PinnacleCacheWcid"/> in
        /// Content/sql/weenies/1007005 Pinnacle Cache.sql (a test asserts it): the spawner composes
        /// "Holder's Pinnacle Cache" from this constant, never from the chest's current name.
        /// </summary>
        public const string PinnacleCacheName = "Pinnacle Cache";
    }
}
