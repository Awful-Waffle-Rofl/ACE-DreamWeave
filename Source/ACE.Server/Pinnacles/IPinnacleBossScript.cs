namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// One boss. Pure logic behind <see cref="IPinnacleFightPort"/>; every callback runs on the fight thread.
    /// </summary>
    public interface IPinnacleBossScript
    {
        /// <summary>Destination kind in pinnacles.json.</summary>
        string Kind { get; }

        PinnacleScriptData Data { get; }

        /// <summary>Phases are 1..PhaseCount.</summary>
        int PhaseCount { get; }

        /// <summary>
        /// The Start row (dialogue); the core has sealed and charged. Runs in the SAME phase scope as the first
        /// EnterPhase that follows it (Phase already reads the first phase): anything it spawns, starts or raises
        /// carries into that phase and is not cleared by the entry.
        /// </summary>
        void OnStart(IPinnacleFightPort port);

        /// <summary>MUST establish the phase's whole world state (the core has cleared the previous phase).</summary>
        void EnterPhase(int phase, IPinnacleFightPort port);

        void OnEvent(PinnacleEvent e, IPinnacleFightPort port);

        /// <summary>
        /// Resume after a hold: the core stopped every hazard at the hold; the script decides what restarts. Called
        /// AFTER the events queued during the hold were delivered (and only when none of them changed the phase or
        /// won), so it MUST be idempotent with respect to hazards and timers those event handlers may already have
        /// started at this resume: restart a series only if it is not already running again.
        /// </summary>
        void RestartPhase(IPinnacleFightPort port);

        string Describe();
    }
}
