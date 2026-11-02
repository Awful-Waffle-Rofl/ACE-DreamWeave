using ACE.Server.WorldObjects;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Whoever started a run: the /hazard command's admin host, or (later) a world event or a boss script. The seam
    /// a future consumer plugs into; nothing in the framework depends on what the host is.
    ///
    /// <see cref="OnEnded"/> fires EXACTLY ONCE per run, for every outcome, OUTSIDE every framework lock, on
    /// whichever thread ended the run (the command thread for /hazard stop, the landblock thread for a mechanic's
    /// own End, the reaping thread for Reaped). A host must therefore not assume a thread.
    /// </summary>
    public interface IHazardHost
    {
        void OnEnded(HazardRun run, HazardOutcome outcome);

        /// <summary>A player took a hazard hit (landblock thread). <paramref name="amount"/> is the damage actually taken.</summary>
        void OnPlayerHit(HazardRun run, Player player, int amount) { }

        /// <summary>
        /// Every object the run spawns, BEFORE it enters the world (landblock thread). A World Event host feeds this
        /// to its own held list (WorldEvent.AddHeld) so its cleanup also covers hazards. The framework still owns
        /// and destroys the object when the run ends.
        /// </summary>
        void Adopt(HazardRun run, WorldObject wo) { }

        /// <summary>
        /// False (the default, and the /hazard admin host): the run talks on the developer channel - every telegraph and
        /// announce is a "[hazard #N] ..." line (HazardContext.Say). True: a host that puts players in front of the run
        /// (a boss fight). The developer channel is then silent and a mechanic says only its short player lines
        /// (HazardContext.SayPlayer), or nothing; the host names the run itself where it needs to (Grael's memory rounds).
        /// </summary>
        bool PlayerFacing => false;
    }

    /// <summary>A host that listens to nothing (a caller that passed null).</summary>
    public sealed class NullHazardHost : IHazardHost
    {
        public static readonly NullHazardHost Instance = new NullHazardHost();

        private NullHazardHost() { }

        public void OnEnded(HazardRun run, HazardOutcome outcome) { }
    }
}
