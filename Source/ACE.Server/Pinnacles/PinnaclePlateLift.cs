namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The decision behind the plate-floor lift (LivePinnacleWorld.LiftPlayersUnderPlateFloor). Pure.
    ///
    /// Prod 2026-10-22: after two re-entries into a held Grael attempt a player's accepted Location stayed at z -47.995 for
    /// 3.5 minutes, standing on the east block's real top UNDER a floor plate (bottom -47.90) and overlapping it, unable to
    /// move, until the wisp pillars killed him. Whatever put him there, a body inside the plated rectangle below the plates'
    /// bottom is never somewhere a player can legitimately stand, so the live world puts it back on top.
    /// </summary>
    public static class PinnaclePlateLift
    {
        /// <summary>
        /// At most one lift per player per this many seconds (monotonic): a lift the client does not take is retried at this
        /// pace instead of every tick.
        /// </summary>
        public const double MinSecondsBetweenLifts = 2.0;

        /// <summary>
        /// True when (x, y, z) is inside the plates' rectangle (edges included) strictly below their bottom and strictly
        /// above <see cref="PinnaclePlateFloor.LowestZ"/>. False for a null floor.
        /// </summary>
        public static bool IsUnderPlates(PinnaclePlateFloor floor, float x, float y, float z)
            => floor != null
               && x >= floor.MinX && x <= floor.MaxX
               && y >= floor.MinY && y <= floor.MaxY
               && z < floor.BottomZ && z > floor.LowestZ;

        /// <summary>
        /// True when a player at (x, y, z) should be lifted now: under the plates, not teleporting (a teleport in flight
        /// owns the position), not dead (the death and its respawn move them), and not lifted within the last
        /// <see cref="MinSecondsBetweenLifts"/> (<paramref name="lastLiftAt"/> null = never).
        /// </summary>
        public static bool ShouldLift(PinnaclePlateFloor floor, float x, float y, float z, bool teleporting, bool dead, double? lastLiftAt, double now)
        {
            if (teleporting || dead || !IsUnderPlates(floor, x, y, z))
                return false;

            return !lastLiftAt.HasValue || now - lastLiftAt.Value >= MinSecondsBetweenLifts;
        }

        /// <summary>The escalation window (seconds, monotonic): lifts older than this no longer count toward <see cref="TeleportOnLift"/>.</summary>
        public const double EscalationWindowSeconds = 30.0;

        /// <summary>The lift (counting itself) inside <see cref="EscalationWindowSeconds"/> at which the forced position is replaced by a teleport.</summary>
        public const int TeleportOnLift = 3;

        /// <summary>
        /// True when the lift about to happen at <paramref name="now"/> should be a teleport instead of a forced position: it
        /// is at least the <see cref="TeleportOnLift"/>-th lift whose time is less than <see cref="EscalationWindowSeconds"/>
        /// before now (<paramref name="priorLifts"/> are this player's earlier lift times; null = none). Repeated lifts mean the
        /// forced position is not taking: the client may be ignoring it while the server's physics update drops its
        /// under-plate reports, which would otherwise leave Location on the plates and the lift silent.
        /// </summary>
        public static bool ShouldTeleport(System.Collections.Generic.IEnumerable<double> priorLifts, double now)
            => CountInWindow(priorLifts, now) + 1 >= TeleportOnLift;

        /// <summary>The number of <paramref name="lifts"/> strictly less than <see cref="EscalationWindowSeconds"/> before <paramref name="now"/>.</summary>
        public static int CountInWindow(System.Collections.Generic.IEnumerable<double> lifts, double now)
            => CountInWindow(lifts, now, EscalationWindowSeconds);

        /// <summary>The number of <paramref name="times"/> strictly less than <paramref name="windowSeconds"/> before <paramref name="now"/>; null = 0.</summary>
        public static int CountInWindow(System.Collections.Generic.IEnumerable<double> times, double now, double windowSeconds)
        {
            if (times == null)
                return 0;

            var n = 0;

            foreach (var t in times)
                if (now - t < windowSeconds)
                    n++;

            return n;
        }

        /// <summary>The cap's window (seconds, monotonic) for escalation teleports.</summary>
        public const double TeleportCapWindowSeconds = 60.0;

        /// <summary>At most this many escalation teleports per player inside <see cref="TeleportCapWindowSeconds"/>.</summary>
        public const int MaxTeleportsPerWindow = 2;

        /// <summary>What a due lift does.</summary>
        public enum LiftAction
        {
            /// <summary>The server-forced position (LiftZ).</summary>
            Force,

            /// <summary>The escalation teleport (TeleportZ).</summary>
            Teleport,

            /// <summary>Teleports have not taken either: stop lifting this player for the rest of the fight (logged once).</summary>
            GiveUp,
        }

        /// <summary>
        /// The action for a lift that <see cref="ShouldLift"/> has already allowed at <paramref name="now"/>:
        /// <see cref="LiftAction.Force"/> until <see cref="ShouldTeleport"/> says the forced position is not taking, then
        /// <see cref="LiftAction.Teleport"/> while fewer than <see cref="MaxTeleportsPerWindow"/> escalation teleports
        /// (<paramref name="priorTeleports"/>) fall inside <see cref="TeleportCapWindowSeconds"/>, and
        /// <see cref="LiftAction.GiveUp"/> past that cap.
        /// </summary>
        public static LiftAction Decide(System.Collections.Generic.IEnumerable<double> priorLifts, System.Collections.Generic.IEnumerable<double> priorTeleports, double now)
        {
            if (!ShouldTeleport(priorLifts, now))
                return LiftAction.Force;

            return CountInWindow(priorTeleports, now, TeleportCapWindowSeconds) < MaxTeleportsPerWindow ? LiftAction.Teleport : LiftAction.GiveUp;
        }
    }
}
