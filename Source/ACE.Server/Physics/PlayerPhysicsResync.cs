namespace ACE.Server.Physics
{
    /// <summary>
    /// Decisions that keep a FastTick player's server-side physics object anchored to the client.
    ///
    /// Player movement is client-authoritative, but a FastTick player (PK/PKLite, or
    /// any player who chose it with /fasttick or follows fast_tick_all_players) is also simulated server-side from its last MoveToState. When the
    /// client is held in place while still holding run - pinned on a spell projectile, or a network stall -
    /// it keeps reporting the same position, and UpdatePlayerPosition's same-position branch used to update
    /// orientation only. The physics copy ran on by itself, and once it was more than one landblock from the
    /// client every later position was refused by update_object_server_new for the rest of the session, with
    /// the player's server-side visibility anchored where the copy ended up. Prod logged copies 0.4 to 1.2 km
    /// from their clients (2026-09-14 to 2026-09-16), cleared only by a relog.
    ///
    /// Pure so both decisions are unit-testable without a landblock or a Player. Player.EnqueuePhysicsResync
    /// acts on them.
    /// </summary>
    public static class PlayerPhysicsResync
    {
        /// <summary>
        /// How far (m) a FastTick player's physics copy may sit from a stationary client's reported position
        /// before it is re-anchored. A stationary client should match its physics copy exactly; this only
        /// absorbs float noise and a settling slide.
        /// </summary>
        public const float StationaryReanchorThreshold = 1.0f;

        public const float StationaryReanchorThresholdSq = StationaryReanchorThreshold * StationaryReanchorThreshold;

        /// <summary>
        /// TRUE when a report that did not move the player's accepted Location should still pull the physics
        /// object back onto it. Non-FastTick players never simulate between reports, so they are never touched.
        /// </summary>
        public static bool ShouldReanchorStationary(bool fastTick, float physicsDriftSq)
        {
            return fastTick && physicsDriftSq > StationaryReanchorThresholdSq;
        }

        /// <summary>
        /// TRUE when a FastTick player's accepted-so-far report is more than one landblock from the physics
        /// copy, the distance at which update_object_server_new refuses it. The caller has already refused a
        /// move that is too fast relative to Location (UpdatePlayerPosition's MOVEMENT SPEED check), so such a
        /// report means the copy is stale, not that the client teleported. Non-FastTick players take
        /// update_object_server instead and are left to its upstream behavior, as is every non-player.
        /// </summary>
        public static bool NeedsResync(bool fastTick, uint physicsCellId, uint requestCellId)
        {
            return fastTick && PhysicsObj.GetBlockDist(physicsCellId, requestCellId) > 1;
        }

        /// <summary>
        /// TRUE when a destroyed projectile's DeleteObject must be sent straight to the player it was aimed at:
        /// that player is no longer on the projectile's known-players list, which is all the normal removal
        /// broadcast reaches, and is still in the projectile's instance. See SpellProjectile.DeleteFromUntrackedTarget.
        /// </summary>
        public static bool ShouldDeleteForUntrackedTarget(bool targetStillKnown, bool sameInstance)
        {
            return !targetStillKnown && sameInstance;
        }
    }
}
