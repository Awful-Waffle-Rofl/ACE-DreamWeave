namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The legal-transition table for <see cref="WorldEvent"/>, kept as a pure static so it is unit
    /// testable without a live event (TECH-DESIGN 2.2, D6). Every state change in the system goes through
    /// WorldEvent.Transition, which consults this table and refuses (never throws) on an illegal edge.
    /// </summary>
    public static class WorldEventStateMachine
    {
        /// <summary>
        /// True when a transition from <paramref name="from"/> to <paramref name="to"/> is allowed.
        ///
        /// The rules, in order:
        ///   1. Done is terminal - nothing leaves it.
        ///   2. Resolved is reachable from every non-Done state. Finish() is the single exit path and must
        ///      be able to run from wherever the event happens to be when it is called (an admin stop during
        ///      the announce window, a shutdown during staging, an exception during the active phase).
        ///   3. Everything else is the linear happy path:
        ///      Idle -> Staged -> Announced -> Active -> Resolved -> (Rewarding ->) Cleanup -> Done.
        /// </summary>
        public static bool IsLegal(WorldEventState from, WorldEventState to)
        {
            if (from == WorldEventState.Done)
                return false;

            if (to == WorldEventState.Resolved)
                return true;

            switch (from)
            {
                case WorldEventState.Idle:
                    return to == WorldEventState.Staged;

                case WorldEventState.Staged:
                    return to == WorldEventState.Announced;

                case WorldEventState.Announced:
                    return to == WorldEventState.Active;

                case WorldEventState.Active:
                    return false;   // only Resolved, handled above

                case WorldEventState.Resolved:
                    return to == WorldEventState.Rewarding || to == WorldEventState.Cleanup;

                case WorldEventState.Rewarding:
                    return to == WorldEventState.Cleanup;

                case WorldEventState.Cleanup:
                    return to == WorldEventState.Done;

                default:
                    return false;
            }
        }
    }
}
