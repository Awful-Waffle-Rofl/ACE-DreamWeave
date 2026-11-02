using ACE.Server.Network.Structure;

namespace ACE.Server.Entity
{
    /// <summary>What completed a pending teleport window (Player.Teleporting cleared by Player.OnTeleportComplete).</summary>
    public enum TeleportCompletionSource
    {
        /// <summary>The client's own GameActionLoginComplete - the only source before the server-side fallback existed.</summary>
        Client,

        /// <summary>Forced: the first movement input the client sent from the destination landblock while still Teleporting.</summary>
        Movement,

        /// <summary>Forced: still Teleporting teleport_forced_complete_seconds after the window opened.</summary>
        Timer,
    }

    /// <summary>The client inputs the movement trigger is asked about. Only one of them can trigger it - see ShouldCompleteOnMovement.</summary>
    public enum ArrivalInput
    {
        MoveToState,
        Jump,
        AutonomousPosition,
    }

    /// <summary>What the forced-completion backstop timer does when it fires.</summary>
    public enum ForcedCompleteTimerDecision
    {
        /// <summary>Nothing to do: the window is over, superseded, disabled, or the player is not connected.</summary>
        Skip,

        /// <summary>Force the arrival now.</summary>
        Fire,

        /// <summary>Still pending but the (live) limit is not reached yet: check again after the remaining time.</summary>
        Reschedule,
    }

    /// <summary>The values the movement trigger reads off one MoveToState, derived by <see cref="TeleportArrivalRules.DeriveMovementArgs"/>.</summary>
    public struct ArrivalMovementArgs
    {
        /// <summary>RawMotionState.HasMovement(): a forward, turn or sidestep command is present. False with no motion state.</summary>
        public bool HasMovement;

        /// <summary>Seconds since the window's start stamp.</summary>
        public double ElapsedSeconds;

        /// <summary>The cell the client reported, 0 with no position.</summary>
        public uint ReportedCell;

        /// <summary>The window's recorded intended destination (TeleportWatch.DestCell), 0 when that record is not this window's.</summary>
        public uint DestinationCell;

        /// <summary>The client's echoed ObjectTeleport sequence (MoveToState.TeleportSequence). Logged only, not enforced.</summary>
        public ushort ReportedTeleportSequence;
    }

    /// <summary>
    /// Pure decision logic for the server-side arrival fallback: completing a teleport, portal or login whose client never
    /// sends GameActionLoginComplete. Before this, the only fallback was Player_Tick's 5 minute MaximumTeleportTime logoff,
    /// which left a client that was still connected and acking held as "pink bubbles" in its destination until then.
    /// No PropertyManager reads and no Player, so every rule is unit tested directly; Player_TeleportArrival.cs feeds it.
    /// </summary>
    public static class TeleportArrivalRules
    {
        /// <summary>
        /// Movement input reported earlier than this after the teleport started is ignored: it can be stale input from before
        /// the teleport that crossed the boundary in flight. KNOWN GAP: a stale report from a teleport that stays inside one
        /// landblock passes the landblock test, so after the grace this window is the only guard against it.
        /// </summary>
        public const double MovementGraceSeconds = 2.0;

        /// <summary>Smallest delay a rescheduled backstop check waits, so a clock that fires a hair early cannot spin.</summary>
        public const double MinRescheduleSeconds = 0.1;

        /// <summary>
        /// Reads the trigger's inputs off one MoveToState. The destination is the window's RECORDED intended destination
        /// (TeleportWatch.DestCell, written when the window opened), never Player.Location: when Teleport()'s
        /// UpdatePlayerPosition fails, Location can still be the origin, and comparing against it would accept a stale
        /// report from the place the player left. A record that is not this window's yields destination 0, which never matches.
        /// </summary>
        public static ArrivalMovementArgs DeriveMovementArgs(MoveToState moveToState, double teleportStart, double now, bool watchIsThisWindow, uint watchDestCell)
        {
            return new ArrivalMovementArgs
            {
                HasMovement = moveToState?.RawMotionState?.HasMovement() == true,
                ElapsedSeconds = now - teleportStart,
                ReportedCell = moveToState?.Position?.Cell ?? 0,
                DestinationCell = watchIsThisWindow ? watchDestCell : 0,
                ReportedTeleportSequence = moveToState?.TeleportSequence ?? 0,
            };
        }

        /// <summary>
        /// True when this movement input should complete the arrival early.
        /// <para/>
        /// Only MoveToState with movement commands counts. AutonomousPosition is excluded on evidence: genuinely hung
        /// clients send one right after a teleport starts and then nothing (prod [TELEPORT_ABANDONED] lines show
        /// actions=+2 lastAction=AutonomousPosition), so it does not prove the client left portal space. Jump is excluded
        /// because JumpPack carries no client position, so the destination-landblock test cannot be applied to it.
        /// <para/>
        /// Keyed on materialized, not on requested: a window whose earlier request is still in (or lost from) its landblock
        /// wait can be driven again, and OnTeleportComplete's materialize latch keeps that from materializing twice.
        /// </summary>
        public static bool ShouldCompleteOnMovement(bool enabled, bool teleporting, bool loggingOut, bool arrivalMaterialized,
            double elapsedSeconds, ArrivalInput input, bool hasMovementCommands, uint reportedCell, uint destinationCell)
        {
            if (!enabled || !teleporting || loggingOut || arrivalMaterialized)
                return false;

            if (input != ArrivalInput.MoveToState)
                return false;

            if (!hasMovementCommands)
                return false;

            if (elapsedSeconds < MovementGraceSeconds)
                return false;

            if (reportedCell == 0 || destinationCell == 0)
                return false;

            return LandblockOf(reportedCell) == LandblockOf(destinationCell);
        }

        /// <summary>
        /// What the backstop does when it fires for the window it was scheduled for. Keyed on materialized, not on
        /// requested, for the same reason as ShouldCompleteOnMovement.
        /// </summary>
        /// <param name="limitSeconds">teleport_forced_complete_seconds as read NOW (so a live change applies); 0 or less disables.</param>
        /// <param name="teleporting">Player.Teleporting.</param>
        /// <param name="arrivalMaterialized">The current window already materialized.</param>
        /// <param name="sessionConnected">The player still has a live session that is not terminating.</param>
        /// <param name="loggingOut">Player.IsLoggingOut.</param>
        /// <param name="scheduledStamp">LastTeleportStartTimestamp captured when the timer was scheduled.</param>
        /// <param name="currentStamp">LastTeleportStartTimestamp now; different means a newer teleport superseded this one.</param>
        /// <param name="elapsedSeconds">Seconds since scheduledStamp.</param>
        /// <param name="remainingSeconds">For Reschedule, how long to wait before checking again; 0 otherwise.</param>
        public static ForcedCompleteTimerDecision EvaluateTimer(long limitSeconds, bool teleporting, bool arrivalMaterialized, bool sessionConnected,
            bool loggingOut, double? scheduledStamp, double? currentStamp, double elapsedSeconds, out double remainingSeconds)
        {
            remainingSeconds = 0;

            if (limitSeconds <= 0)
                return ForcedCompleteTimerDecision.Skip;

            if (scheduledStamp == null || currentStamp != scheduledStamp)
                return ForcedCompleteTimerDecision.Skip;

            if (!teleporting || arrivalMaterialized || !sessionConnected || loggingOut)
                return ForcedCompleteTimerDecision.Skip;

            if (elapsedSeconds < limitSeconds)
            {
                remainingSeconds = limitSeconds - elapsedSeconds;
                if (remainingSeconds < MinRescheduleSeconds)
                    remainingSeconds = MinRescheduleSeconds;

                return ForcedCompleteTimerDecision.Reschedule;
            }

            return ForcedCompleteTimerDecision.Fire;
        }

        /// <summary>
        /// Whether this source may run the once-only first-login block (FirstEnterWorldDone + SendPropertyUpdatesAndOverrides).
        /// FirstEnterWorldDone also gates GameActionSetCharacterOptions (a pink-bubble client that presses Logout sends
        /// DEFAULT options, which the flag keeps from overwriting the saved ones) and DDDHandler's boot-or-message switch,
        /// so it is set only on a real client signal:
        /// <list type="bullet">
        /// <item>Client: the LoginComplete itself, any window.</item>
        /// <item>Movement: only for a non-login window. A MoveToState proves a client live in the world, but at LOGIN the
        /// first-login handshake is the client's own LoginComplete, so a movement-forced login leaves it to that.</item>
        /// <item>Timer: never; a timer proves nothing about the client.</item>
        /// </list>
        /// <paramref name="kind"/> is the window's TeleportWatch kind, or null when the watch record is not this window's,
        /// which is treated like a login (the conservative side).
        /// </summary>
        public static bool RunsFirstEnterWorld(TeleportCompletionSource source, TeleportKind? kind)
        {
            switch (source)
            {
                case TeleportCompletionSource.Client: return true;
                case TeleportCompletionSource.Movement: return kind.HasValue && kind.Value != TeleportKind.Login;
                default: return false;
            }
        }

        /// <summary>
        /// The sequence fields of a [TELEPORT_FORCED_COMPLETE] reason=movement line: what the client echoed, this client's
        /// pinned SelfTeleportSequence (none when unpinned) and the live ObjectTeleport sequence. Mismatch compares the echo
        /// with what this client was last sent: the pinned value when there is one, else the live one. Observation only -
        /// prod will show whether a client in this state echoes the current teleport before the server enforces it.
        /// </summary>
        public static string FormatSequenceFields(ushort reported, ushort? self, ushort? live, out bool mismatch)
        {
            var expected = self ?? live;
            mismatch = expected.HasValue && expected.Value != reported;

            return "teleSeq=" + reported
                + " selfSeq=" + (self.HasValue ? self.Value.ToString() : "none")
                + " liveSeq=" + (live.HasValue ? live.Value.ToString() : "none")
                + " seqMatch=" + (expected.HasValue ? (mismatch ? "false" : "true") : "n/a");
        }

        public static string SourceTag(TeleportCompletionSource source)
        {
            switch (source)
            {
                case TeleportCompletionSource.Movement: return "movement";
                case TeleportCompletionSource.Timer: return "timer";
                default: return "client";
            }
        }

        public static bool IsForced(TeleportCompletionSource source) => source != TeleportCompletionSource.Client;

        private static uint LandblockOf(uint cell) => cell >> 16;
    }

    /// <summary>
    /// Per-player once-only bookkeeping for completing an arrival, keyed on the LastTeleportStartTimestamp value of the
    /// window (the TeleportWatch pattern), so a new teleport re-arms everything with no explicit reset.
    /// <para/>
    /// The materialize latch is the only BLOCKER: OnTeleportComplete checks it at the top and takes it at the point it
    /// materializes the player, so however many sources drive a window - and however many landblock-wait retry chains
    /// that leaves running - the arrival side effects run once. The request record is attribution only (the first
    /// source to ask owns the window, for the histogram and the late-client line); it never refuses a source, so a
    /// request that went nowhere cannot strand the window.
    /// <para/>
    /// A null stamp never latches (every call behaves as before this existed); in practice the stamp is always set,
    /// because both Teleport() and PlayerEnterWorld write it. Plain fields: written on the player's own action thread
    /// like the rest of Player's teleport state.
    /// </summary>
    public sealed class TeleportArrivalLatch
    {
        // (PreMaterializeChainGate, below, is the companion that keeps re-drives from multiplying the wait's retry chains.)
        private double? requestedStamp;
        private TeleportCompletionSource requestedSource;
        private double? materializedStamp;
        private TeleportCompletionSource materializedSource;
        private double? lateClientStamp;
        private double? forcedMovementNotedStamp;
        private double? forcedTimerNotedStamp;

        /// <summary>Records the first source to ask for this window. Attribution only: later sources are not refused.</summary>
        public void NoteRequest(double? stamp, TeleportCompletionSource source)
        {
            if (stamp == null || requestedStamp == stamp || materializedStamp == stamp)
                return;

            requestedStamp = stamp;
            requestedSource = source;
        }

        public bool IsRequested(double? stamp) => stamp.HasValue && (requestedStamp == stamp || materializedStamp == stamp);

        public bool IsMaterialized(double? stamp) => stamp.HasValue && materializedStamp == stamp;

        /// <summary>The source that owns this window: its first requester, or Client for a window nobody requested.</summary>
        public TeleportCompletionSource SourceFor(double? stamp)
        {
            if (stamp.HasValue && materializedStamp == stamp)
                return materializedSource;

            if (stamp.HasValue && requestedStamp == stamp)
                return requestedSource;

            return TeleportCompletionSource.Client;
        }

        /// <summary>
        /// True exactly once per window, at the point OnTeleportComplete materializes the player. A window reached without
        /// a request (the retry chain after a newer teleport, or a direct caller) is attributed to the client.
        /// </summary>
        public bool TryMaterialize(double? stamp, out TeleportCompletionSource source)
        {
            source = SourceFor(stamp);

            if (stamp == null)
                return true;

            if (materializedStamp == stamp)
                return false;

            materializedStamp = stamp;
            materializedSource = source;
            return true;
        }

        /// <summary>
        /// True once per window per forced source, so the [TELEPORT_FORCED_COMPLETE] line and its counter describe the
        /// decision once even when that source drives the window again during a landblock wait.
        /// </summary>
        public bool TryNoteForced(double? stamp, TeleportCompletionSource source)
        {
            if (stamp == null)
                return true;

            switch (source)
            {
                case TeleportCompletionSource.Movement:
                    if (forcedMovementNotedStamp == stamp) return false;
                    forcedMovementNotedStamp = stamp;
                    return true;

                case TeleportCompletionSource.Timer:
                    if (forcedTimerNotedStamp == stamp) return false;
                    forcedTimerNotedStamp = stamp;
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// True exactly once per window, for the client's own LoginComplete arriving after a forced source already owns the
        /// window. That is the one client ack worth a log line: it says the forced client was alive after all.
        /// </summary>
        public bool TryNoteLateClient(double? stamp)
        {
            if (stamp == null || !IsRequested(stamp) || !TeleportArrivalRules.IsForced(SourceFor(stamp)) || lateClientStamp == stamp)
                return false;

            lateClientStamp = stamp;
            return true;
        }
    }

    /// <summary>
    /// At most one live pre-materialize retry chain per window. OnTeleportComplete holds a player whose landblock has not
    /// finished populating by re-queueing itself every 0.1 s; since every arrival source re-enters OnTeleportComplete
    /// until the window materializes, each re-drive during that wait would otherwise start ANOTHER self-perpetuating
    /// chain, and a stream of MoveToStates could leave dozens ticking at 10 Hz until the landblock finished.
    /// <para/>
    /// TryClaim is asked before a chain is scheduled and grants it only when no chain is live for this stamp and
    /// landblock. The chain releases its claim as the step just before it re-enters OnTeleportComplete, so exactly one
    /// of the calls that follow (the chain's own re-entry, or a re-drive that lands first) schedules the next chain and
    /// the rest return. Clear runs where the wait ends (materialize, or the bound expiring). Keyed on the stamp and
    /// landblock so a newer window, or the same window moved to another landblock, is never blocked by an older claim.
    /// Plain fields, on the player's own action thread like the rest of the wait's state.
    /// </summary>
    public sealed class PreMaterializeChainGate
    {
        private bool live;
        private double? liveStamp;
        private ulong liveLongId;

        /// <summary>True when the caller should schedule the retry chain; false when one is already live for this window.</summary>
        public bool TryClaim(double? stamp, ulong landblockLongId)
        {
            if (live && liveStamp == stamp && liveLongId == landblockLongId)
                return false;

            live = true;
            liveStamp = stamp;
            liveLongId = landblockLongId;
            return true;
        }

        /// <summary>The live chain's step before it re-enters OnTeleportComplete: the next call may schedule a successor.</summary>
        public void Release(double? stamp, ulong landblockLongId)
        {
            // only its own claim: an older window's chain must not free a newer window's claim
            if (liveStamp == stamp && liveLongId == landblockLongId)
                live = false;
        }

        /// <summary>The wait is over (materialized or expired).</summary>
        public void Clear() => live = false;

        public bool IsLive(double? stamp, ulong landblockLongId) => live && liveStamp == stamp && liveLongId == landblockLongId;
    }
}