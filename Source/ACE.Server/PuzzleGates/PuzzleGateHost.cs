using ACE.Server.WorldObjects;

namespace ACE.Server.PuzzleGates
{
    /// <summary>Which fail policy a placement's wrong answers feed. The manager only carries it; the host acts on it.</summary>
    public enum PuzzlePolicyMode
    {
        /// <summary>Wrong answers cost only the in-placement reset and lockout (admin /puzzlegate placements).</summary>
        None,

        /// <summary>Scored wrong answers are reported to the Thread fail policy (run placements).</summary>
        Run,
    }

    /// <summary>Why a placement left the registry without (or after) a solve.</summary>
    public enum PuzzleRemovalReason
    {
        /// <summary>The initial spawn failed; whatever entered the world was destroyed.</summary>
        SpawnFailed,

        /// <summary>A reshuffle could not spawn the next round; the placement was cleared.</summary>
        ReshuffleFailed,

        /// <summary>An explicit clear: /puzzlegate clear or reroll, or a run's ClearForRun at run end.</summary>
        Cleared,

        /// <summary>Reaped: the gate was destroyed from outside (landblock unload or reload), or the spawn never ran.</summary>
        Reaped,
    }

    /// <summary>
    /// The owner of a placement: who is told what happened to it and who may veto an activation. The manager
    /// moves world objects; the host decides what a solve or a wrong answer MEANS.
    /// <para/>
    /// THREADING. OnSolved, OnWrong and OnRemoved are invoked OUTSIDE the placement's Sync lock, on the
    /// placement's landblock thread or on whichever thread cleared or reaped it (a command thread, or the world
    /// thread for a run's watchdog and EndRun); a host must not assume the landblock thread there.
    /// CheckActivation is the exception: it runs on the landblock thread UNDER p.Sync, so it must be a quick read
    /// that never re-enters PuzzleGateManager (any manager call that takes p.Sync would deadlock or reorder) and
    /// takes no lock that is ever held while p.Sync is wanted. Report may run under either.
    /// </summary>
    public interface IPuzzleGateHost
    {
        PuzzlePolicyMode PolicyMode { get; }

        /// <summary>The Thread run this placement belongs to; 0 for an admin placement. ClearForRun keys on it.</summary>
        uint RunId { get; }

        /// <summary>False forces every ambush off whatever the options say (run placements: user ruling, no ambush in runs).</summary>
        bool AllowAmbush { get; }

        /// <summary>
        /// Pre-activation check, taken BEFORE the pull is scored. Null lets the pull through; a non-null line is
        /// told to the player and the pull is dropped with no reset, no lockout, no wrong count and no telemetry.
        /// Runs UNDER p.Sync on the landblock thread: read-only, and never re-enter PuzzleGateManager.
        /// </summary>
        string CheckActivation(PuzzleGatePlacement placement, Player player);

        /// <summary>The last round was answered and the gate opened or was removed.</summary>
        void OnSolved(PuzzleGatePlacement placement, Player player);

        /// <summary>
        /// A wrong lever. <paramref name="scored"/> is true for a pull the rules counted (it reset progress and
        /// started the lockout), false for a wrong lever pulled during the lockout, which the rules refused.
        /// </summary>
        void OnWrong(PuzzleGatePlacement placement, Player player, bool scored);

        /// <summary>The placement left the registry. Called exactly once per placement. <paramref name="solved"/> says whether it had been solved first.</summary>
        void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved);

        /// <summary>A status line for whoever placed it (the admin's chat; a run host logs it).</summary>
        void Report(PuzzleGatePlacement placement, string text);
    }

    /// <summary>
    /// Marker for puzzlegate-sweep's fixture host: its placements' [PUZZLE_GATE] log lines carry " sweep=1"
    /// (PuzzleGatePlacement.SweepTag) so telemetry tooling can drop them.
    /// </summary>
    public interface IPuzzleSweepHost
    {
    }

    /// <summary>
    /// The host of a /puzzlegate site placement: admin behaviour (RunId 0, so list / reveal / clear / reroll manage it,
    /// reports go to the placing admin) except that a wrong pull never summons an ambush, matching a run's placement.
    /// </summary>
    public sealed class SiteTourPuzzleGateHost : IPuzzleGateHost
    {
        public static readonly SiteTourPuzzleGateHost Instance = new SiteTourPuzzleGateHost();

        private SiteTourPuzzleGateHost() { }

        public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.None;

        public uint RunId => 0;

        public bool AllowAmbush => false;

        public string CheckActivation(PuzzleGatePlacement placement, Player player) => null;

        public void OnSolved(PuzzleGatePlacement placement, Player player) { }

        public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored) { }

        public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) { }

        public void Report(PuzzleGatePlacement placement, string text) => PuzzleGateManager.TellPlacer(placement, text);
    }

    /// <summary>
    /// The admin /puzzlegate host: today's behaviour, unchanged. No veto, no policy, ambush as the options say,
    /// reports go to the placing admin's chat.
    /// </summary>
    public sealed class AdminPuzzleGateHost : IPuzzleGateHost
    {
        public static readonly AdminPuzzleGateHost Instance = new AdminPuzzleGateHost();

        private AdminPuzzleGateHost() { }

        public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.None;

        public uint RunId => 0;

        public bool AllowAmbush => true;

        public string CheckActivation(PuzzleGatePlacement placement, Player player) => null;

        public void OnSolved(PuzzleGatePlacement placement, Player player) { }

        public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored) { }

        public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) { }

        public void Report(PuzzleGatePlacement placement, string text) => PuzzleGateManager.TellPlacer(placement, text);
    }
}
