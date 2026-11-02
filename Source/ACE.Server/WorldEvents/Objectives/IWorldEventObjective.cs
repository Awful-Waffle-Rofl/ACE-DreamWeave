using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// MVP attribution for one finished run (TECH-DESIGN 2.5, 2.6). A null/empty <see cref="Name"/> means
    /// there is no MVP - an event nobody fought in, or an objective whose rule found no clear winner.
    /// </summary>
    public readonly struct WorldEventMvp
    {
        public readonly string Name;

        /// <summary>Short human-readable reason, e.g. "most kills" or "killing blow".</summary>
        public readonly string Reason;

        public readonly int Kills;

        public WorldEventMvp(string name, string reason, int kills)
        {
            Name = name;
            Reason = reason;
            Kills = kills;
        }

        /// <summary>No MVP.</summary>
        public static readonly WorldEventMvp None = new WorldEventMvp(null, null, 0);

        public bool HasMvp => !string.IsNullOrEmpty(Name);
    }

    /// <summary>
    /// The goal a running event is trying to satisfy (TECH-DESIGN 2.5). Implementations land in WP-06:
    /// KillCountObjective, DestroySourceObjective, KillBossObjective (Hold is deferred to P2 per C6).
    ///
    /// Every method is called on the world thread from WorldEvent.
    /// </summary>
    public interface IWorldEventObjective
    {
        void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager);

        void Tick(double now);

        bool IsComplete { get; }

        /// <summary>Player-facing one-liner, e.g. "12 of 40 slain."</summary>
        string ProgressText { get; }

        /// <summary>
        /// How many more event-creature deaths this objective still needs, or NULL for "no opinion"
        /// (WP-20). The wave cadence reads it to avoid spawning a wave nobody needs to kill: with a
        /// non-null value, a wave is suppressed while the creatures already on the field can satisfy the
        /// objective by themselves.
        ///
        /// NULL MUST NEVER MEAN ZERO. An objective whose completion does not depend on trash deaths at all
        /// - destroy_source, kill_boss - returns null, and the cadence then behaves exactly as it did
        /// before this member existed. Returning 0 from those would suppress every wave for the rest of the
        /// run.
        ///
        /// Defaulted here so an implementation with no opinion (and every test fake) needs no change.
        /// </summary>
        int? RemainingKills => null;

        /// <summary>
        /// The objective's current kill target, or NULL when it has none (TECH-DESIGN 2.15). Reported by
        /// "/worldevent status" only; nothing decides anything on it, and reading it must never resolve or
        /// change a target that has not been sized yet.
        /// </summary>
        int? TargetKills => null;

        /// <summary>
        /// A fresh audience sample was taken (TECH-DESIGN 2.15). The wave cadence re-samples every wave, and
        /// an objective sized against the four people who were standing there when the run staged is the
        /// wrong size for the twenty who turned up by wave three.
        ///
        /// Implementations MUST only ever RATCHET UP on this. Letting a target fall when players leave
        /// would hand a run that is already half-killed an instant completion, and worse, would make the
        /// progress line count backwards in front of the players still fighting.
        ///
        /// Defaulted to a no-op, so an objective whose size does not depend on turnout needs no change.
        /// </summary>
        void OnAudienceResampled(int participantCount) { }

        WorldEventMvp Mvp();

        // ---- Objective hooks (World Events refactor R4) ------------------------------------------------
        //
        // Every member below is DEFAULTED, and every default reproduces the behaviour WorldEvent had before
        // the hook existed. They let a goal change how the run around it behaves without WorldEvent.cs ever
        // naming that goal (WorldEventCoreGoalAgnosticTests pins this). Each one is read on the world thread,
        // inside a try/catch at its seam: a throwing hook is logged and read as its default.

        /// <summary>
        /// True when the objective needs the anchor's eight adjacent landblocks held as well, on top of
        /// whatever the theme, the presence ring and the hazard already ask for. Read once, by
        /// WorldEvent.HoldLandblocks at Stage (reason <see cref="WorldEventHoldReason.Objective"/>).
        /// </summary>
        bool HoldsAdjacentLandblocks => false;

        /// <summary>
        /// The presence arena's radius when the objective needs its own, or NULL for the theme's rule. Passed
        /// to WorldEvent.BuildArena as its goal radius at Stage, so only a positive, finite value wins.
        /// </summary>
        float? ArenaRadius => null;

        /// <summary>
        /// True when the objective drives the wave cadence itself: WorldEvent.TickActive then skips its own
        /// wave tick entirely. The objective's replacement cadence is responsible for every later wave.
        /// </summary>
        bool ReplacesWaveCadence => false;

        /// <summary>
        /// Whether wave 0 may go out now. Asked on the Announced -> Active edge and then, while it answers
        /// false, on every normal-cadence tick; wave 0 goes out on the first true, and the cadence carries on
        /// from there (the next wave is timed from wave 0). The default, always true, sends wave 0 on the
        /// Active edge exactly as before. An objective that sets <see cref="ReplacesWaveCadence"/> runs its
        /// own waves and has no use for this.
        /// </summary>
        bool FirstWaveReady(double now) => true;

        /// <summary>
        /// The run reached Announced: the landblocks are held, the spawn anchors and the audience are known,
        /// and the ring is up. Called once, at the end of WorldEvent.Stage, never for a run whose stage aborted.
        /// </summary>
        void OnStaged(double now) { }

        /// <summary>
        /// The objective's own verdict that the run is lost (or must abort), or NULL for "not failed". Read
        /// every Active tick right after <see cref="Tick"/> and before <see cref="IsComplete"/>, so a run can
        /// never both fail and succeed on one tick; a non-null value finishes the run with that outcome.
        /// Only a failure counts: None and the success outcomes (Success, SuccessBossAbsent) are ignored with a
        /// warning, because success belongs to <see cref="IsComplete"/>.
        /// </summary>
        WorldEventOutcome? FailureOutcome => null;

        /// <summary>
        /// True when WorldEvent.ProgressText must keep reporting the line as it stood at Finish, rather than
        /// read <see cref="ProgressText"/> live while cleanup removes what the objective counts.
        /// </summary>
        bool FreezesProgressAtFinish => false;

        /// <summary>
        /// The run is finishing with <paramref name="outcome"/>. Called once, at the top of WorldEvent.Finish
        /// (after the outcome is recorded, before hazards stop, the outcome is announced or anything is
        /// cleaned up). Not called again when an abort later cuts a claim window short.
        ///
        /// Runs for EVERY finish, including a run aborted before <see cref="OnStaged"/> ever ran (an admin
        /// stop while Idle, a failed landblock hold during Stage), so OnFinish may arrive with no matching
        /// OnStaged and must not assume anything OnStaged set up.
        /// </summary>
        void OnFinish(WorldEventOutcome outcome, double now) { }

        /// <summary>
        /// The claim policy for this run's Weave Caches, read by WorldEventCacheHandler on every claim.
        /// <see cref="WorldEventClaimPolicy.Default"/> is the pre-pipeline claim exactly.
        /// </summary>
        WorldEventClaimPolicy ClaimPolicy => WorldEventClaimPolicy.Default;

        /// <summary>
        /// The presence idle window in seconds (WorldEventIdleTracker's afkSeconds): a player inside the arena who has neither
        /// moved nor dealt damage for this long, or is flagged afk, counts as outside. 0 (the default, and every goal that
        /// ships today) turns the idle rule off, so presence is exactly the arena test. Read once, by WorldEvent.Stage, right
        /// before the presence ledger is built; a non-finite or negative value reads as 0.
        /// </summary>
        double PresenceAfkSeconds => 0;

        /// <summary>
        /// The radius players count within (presence, audience, local lines) when the objective needs its own, or NULL for
        /// the source's rule. Read through WorldEvent.ObjectiveParticipantRadius; only a positive, finite value wins.
        /// </summary>
        float? ParticipantRadius => null;

        /// <summary>
        /// True when the run's in-run lines (waves, progress) go only to the players near the anchor instead of world-wide.
        /// The teaser, start, outcome and time-left lines stay world-wide either way. Read through
        /// WorldEvent.ObjectiveAnnouncesLocally.
        /// </summary>
        bool AnnouncesLocally => false;

        /// <summary>
        /// True when staff (Sentinel and up) count toward the run's audience scaling and presence count. False (the default)
        /// is the sampler's own staff rule, exactly as before. Read through WorldEvent.ObjectiveCountsStaff on every sample.
        /// </summary>
        bool CountsStaffTowardAudience => false;

        /// <summary>
        /// The outcome template (the composition's successTemplate or failTemplate) with the objective's own tokens filled in,
        /// read once by WorldEvent.Finish right after OnFinish. The default hands it back unchanged.
        /// </summary>
        string OutcomeTemplate(string template, bool success) => template;
    }

    /// <summary>
    /// A goal's rule for where a run may really start (WorldEventObjectiveFactory.StartSpotRuleFor), applied by
    /// WorldEventManager.TryStart after composing and BEFORE the candidate run is built, so a rule may move the composed
    /// anchor or refuse the start before anything is constructed or announced. Called under the manager's lock, on whatever
    /// thread asked for the start.
    /// </summary>
    public interface IWorldEventStartSpotRule
    {
        /// <summary>
        /// True to let the start go ahead (the rule may have moved <paramref name="composition"/>'s anchor in place); false to
        /// refuse it, with <paramref name="error"/> saying why.
        /// </summary>
        bool TryResolve(WorldEventComposition composition, WorldEventRequest request, out string error);
    }

    /// <summary>The start-spot rule every goal has today: the composed spot stands, unchanged.</summary>
    public sealed class WorldEventNoStartSpotRule : IWorldEventStartSpotRule
    {
        public static readonly WorldEventNoStartSpotRule Instance = new WorldEventNoStartSpotRule();

        private WorldEventNoStartSpotRule()
        {
        }

        public bool TryResolve(WorldEventComposition composition, WorldEventRequest request, out string error)
        {
            error = null;
            return true;
        }
    }
}
