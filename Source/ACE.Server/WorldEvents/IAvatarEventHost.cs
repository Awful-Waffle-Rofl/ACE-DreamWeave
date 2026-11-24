namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The private Avatar event's host (WorldEventHooks.AvatarEventHost): the Avatar side that fields the Avatar's fighter in
    /// its own event. Called only for a run with an AvatarId, on the world thread, through WorldEventHooks, which contains
    /// any throw.
    /// </summary>
    public interface IAvatarEventHost
    {
        /// <summary>The run's State changed (WorldEvent.Transition, or the forced-Done fallback).</summary>
        void OnStateChanged(WorldEvent evt, WorldEventState from, WorldEventState to);

        /// <summary>A wave was picked; <paramref name="bandTop"/> is the top level of the band it drew from.</summary>
        void OnWave(WorldEvent evt, int wave, int bandTop);
    }
}
