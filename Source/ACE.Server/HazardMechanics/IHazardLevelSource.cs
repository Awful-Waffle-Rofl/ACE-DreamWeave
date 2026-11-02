namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// A hazard mechanic that can report the current height of its moving surface to its host.
    /// Landblock thread only: implementations read the run clock and the director without locking.
    /// </summary>
    public interface IHazardLevelSource
    {
        /// <summary>
        /// False with <paramref name="level"/> 0.0 when no run is live (before Start, or after the run ended);
        /// otherwise true with the surface z at the current run time.
        /// </summary>
        bool TryGetLevel(out double level);
    }
}
