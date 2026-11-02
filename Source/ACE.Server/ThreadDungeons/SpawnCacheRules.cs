namespace ACE.Server.ThreadDungeons
{
    public enum SpawnCacheRefusal
    {
        None,
        NotInside,
        NotComplete,
        Cooldown,
        Forming,
        Empty,
    }

    /// <summary>
    /// The /spawncache refusal table (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 6), as a pure function so
    /// the order is testable without a Player. The command handler samples the world and passes plain values.
    /// </summary>
    public static class SpawnCacheRules
    {
        public const string NotInsideMessage = "You must be inside your Thread to use this.";
        public const string NotCompleteMessage = "Your Thread is not complete yet.";
        public const string CooldownMessage = "Please wait before using /spawncache again.";
        // The placement latch is per run, so in a group it may be another member's cache that is forming.
        public const string FormingMessage = "A Thread Cache is already forming. Try again shortly.";
        public const string EmptyMessage = "Your Thread Cache is empty.";

        public static SpawnCacheRefusal FirstRefusal(bool ownsLiveRun, bool insideRunInstance, ThreadDungeonRunState state,
            bool cooldownElapsed, bool placementInProgress, bool hasUnclaimedLoot, bool anyCacheHoldsItems)
        {
            if (!ownsLiveRun || !insideRunInstance)
                return SpawnCacheRefusal.NotInside;

            if (state != ThreadDungeonRunState.Cleared)
                return SpawnCacheRefusal.NotComplete;

            if (!cooldownElapsed)
                return SpawnCacheRefusal.Cooldown;

            if (placementInProgress)
                return SpawnCacheRefusal.Forming;

            if (!hasUnclaimedLoot && !anyCacheHoldsItems)
                return SpawnCacheRefusal.Empty;

            return SpawnCacheRefusal.None;
        }

        /// <summary>The 5 s cooldown starts once checks 1-3 have passed, whatever checks 4-5 then decide.</summary>
        public static bool StampsCooldown(SpawnCacheRefusal refusal)
            => refusal != SpawnCacheRefusal.NotInside && refusal != SpawnCacheRefusal.NotComplete && refusal != SpawnCacheRefusal.Cooldown;

        public static string MessageFor(SpawnCacheRefusal refusal)
        {
            switch (refusal)
            {
                case SpawnCacheRefusal.NotInside: return NotInsideMessage;
                case SpawnCacheRefusal.NotComplete: return NotCompleteMessage;
                case SpawnCacheRefusal.Cooldown: return CooldownMessage;
                case SpawnCacheRefusal.Forming: return FormingMessage;
                case SpawnCacheRefusal.Empty: return EmptyMessage;
                default: return null;
            }
        }

        /// <summary>
        /// Unclaimed loot: Summon, which moves the placed caches to the player AND forms new caches for the unclaimed loot.
        /// Otherwise: Move, which only moves the placed caches.
        /// </summary>
        public static CacheRequestMode ModeFor(bool hasUnclaimedLoot)
            => hasUnclaimedLoot ? CacheRequestMode.Summon : CacheRequestMode.Move;
    }
}
