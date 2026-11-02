using System.Collections.Generic;

namespace ACE.Server.HazardMechanics
{
    /// <summary>How a run ended. Exactly one is reported to <see cref="IHazardHost.OnEnded"/>, exactly once.</summary>
    public enum HazardOutcome
    {
        /// <summary>The mechanic finished its script (all cycles run, the memory sequence answered).</summary>
        Completed,

        /// <summary>The mechanic decided the PLAYERS failed (a wrong memory answer, a timeout). Never a mechanic fault: that is <see cref="Aborted"/>.</summary>
        Failed,

        /// <summary>Someone called Stop: /hazard stop, a host, the kill switch.</summary>
        Stopped,

        /// <summary>The registry found the run dead: its landblock unloaded or its driver stopped ticking.</summary>
        Reaped,

        /// <summary>The mechanic could not continue: content lost, no room, no routes, or it threw. Not the players' fault.</summary>
        Aborted,
    }

    public enum HazardRunState
    {
        /// <summary>Registered; the driver has not called Start yet.</summary>
        Pending,

        /// <summary>Start has been called; Tick runs every driver tick.</summary>
        Running,

        /// <summary>Ended (any outcome). Spawns are refused; the teardown is queued on the landblock.</summary>
        Ended,
    }

    /// <summary>
    /// One hazard mechanic (wisps, balls, beams, memory, and the /hazard tools). THIS INTERFACE IS THE CONTRACT the
    /// mechanic packages build to.
    ///
    /// LIFETIME. <see cref="HazardMechanicRegistry"/> creates a FRESH instance per run, so a mechanic may keep its
    /// own per-run state in fields. <see cref="Validate"/> and <see cref="RequiredWcids"/> are also called on a
    /// fresh, never-started instance on the caller's thread, so they must not depend on that state.
    ///
    /// THREADING. <see cref="Start"/>, <see cref="Tick"/>, <see cref="OnStop"/>, every <see cref="HazardContext.Schedule"/>
    /// callback, and <see cref="Describe"/>/<see cref="Reveal"/> when called by the driver all run on the region's
    /// LANDBLOCK thread. <see cref="Describe"/>/<see cref="Reveal"/> may also be called from the command thread,
    /// so they must only read simple fields.
    ///
    /// THE SHARED INVARIANTS every mechanic follows (HAZARD-PLAN.md "Invariants"; the reconciliation pass greps
    /// for violations):
    ///   1. every spawn via <see cref="HazardContext.Spawn"/> (or SpawnTimedEffect) - never WorldObjectFactory;
    ///   2. all movement via <see cref="HazardContext.MoveLeg"/>/<see cref="HazardContext.MoveLegs"/>/
    ///      <see cref="HazardContext.StopLeg"/> - unison comes from a shared leg schedule, never separate timers;
    ///   3. all damage via <see cref="HazardContext.HitPlayers"/>/HitPlayer/HitPlayerFlat (routed through ApplyHit) - never TakeDamage, never a Hotspot;
    ///   4. geometry only from <see cref="HazardContext.Region"/> - no landblock id, coordinate or z literal;
    ///   5. no PropertyManager reads - numbers from <see cref="HazardContext.Args"/>, each with a documented
    ///      default and clamp (HazardArgs' accessors force both);
    ///   6. every decision a pure static in a *Rules/*Patterns/*Formations/*Layout class taking a passed-in
    ///      Random (<see cref="HazardContext.Rng"/>), tested without Player/Session/PropertyManager;
    ///   7. no own cleanup of world objects on stop - the framework destroys everything Spawned;
    ///   8. <see cref="Validate"/> refuses parameters that leave no escape;
    ///   9. time via <see cref="HazardContext.Now"/> only (no DateTime.UtcNow, no new Random); player-facing lines
    ///      only via <see cref="HazardContext.Say"/>/<see cref="HazardContext.Tell"/>; logs via
    ///      <see cref="HazardContext.Log"/> (it adds the "[HAZARD] lb=.. inst=.. run=#n kind=.." prefix);
    ///  10. NOTHING MOVES THROUGH A SOLID (gate 2, 2026-10-07: client and server both stop a moving ethereal
    ///      hazard at environment geometry). Owner rulings: BALLS HOVER - move them with Follow = Ground and
    ///      GroundOffset = args "ball_z" (default <see cref="HazardDefaults.BallHover"/>), and spawn them at
    ///      <see cref="HazardContext.PointFor"/> with the same offset, so the sphere clears every solid top. The
    ///      framework makes every moving body a Missile (HazardMotion.PrepareMovingBody) so the engine's step-down
    ///      cannot drop a hovering body onto a platform below it, where floor friction stalls it (2026-10-18).
    ///      WISPS NEVER ENTER SOLIDS - route every wisp target through <see cref="HazardGround.PlaceWisp"/>: move
    ///      the wisp to its Stand point (the last open point before a solid on its path) and cast its pillar at
    ///      its Pillar point (the true target, raised onto the solid's top) - no safe spots.
    ///
    /// HANDLES CAN DIE UNDER YOU. /reload-landblock destroys every non-persisted object on the landblock while the
    /// Landblock object itself (and usually the run and its driver) survives, so a handle the mechanic still holds
    /// can be dead on the next tick. Check <see cref="HazardHandle.Alive"/> before using a held handle (MoveLeg,
    /// StopLeg and HitPlayers no-op on a dead one: HitPlayers also refuses a destroyed source) and decide what a lost member means for the pattern -
    /// respawn it, carry on without it, or End. A Spawn can also return null (no cell, refused entry). And never
    /// MoveLeg in the tick a handle was spawned in: HazardContext defers such a call one tick (see MoveLeg). A
    /// deferred MoveLeg returns true while handle.Path stays null until the next tick, so key follow-on timing off
    /// handle.LegEndTime once set, or schedule at duration + one tick - never at exactly Now + duration.
    /// </summary>
    public interface IHazardMechanic
    {
        /// <summary>The registry key, lower-case ("wisps").</summary>
        string Kind { get; }

        /// <summary>The patterns this mechanic accepts; the FIRST is the default. Empty: patterns are not used.</summary>
        IReadOnlyList<string> Patterns { get; }

        /// <summary>
        /// Every wcid this run will spawn with these args. The manager refuses to start (with a clear line, never an
        /// exception) when any of them has no weenie in the world database.
        /// </summary>
        IEnumerable<uint> RequiredWcids(HazardArgs args);

        /// <summary>
        /// PURE pre-flight on the caller's thread: null when the run can start, else a one-line reason. Must refuse
        /// any parameter set that leaves the players no escape, and anything the region cannot support (e.g. a
        /// ceiling beam in a region with no ceiling). <paramref name="pattern"/> is already resolved to a member
        /// of <see cref="Patterns"/> (or null when Patterns is empty).
        /// </summary>
        string Validate(HazardRegion region, HazardArgs args, string pattern);

        /// <summary>Called once, on the first driver tick. Spawn the opening objects and schedule the script.</summary>
        void Start(HazardContext ctx);

        /// <summary>
        /// Called every driver tick (hazard_mech_tick_ms, default 100 ms) after the framework has advanced every
        /// moving hazard and run every due Schedule callback. Call <see cref="HazardContext.End"/> to finish.
        /// </summary>
        void Tick(HazardContext ctx);

        /// <summary>
        /// Called once when the run ends (any outcome) BEFORE the framework destroys every spawned object - only
        /// for a run that was started. Spawns are refused by then; a last Say is fine. Must not throw.
        /// </summary>
        void OnStop(HazardContext ctx, HazardOutcome outcome);

        /// <summary>One short status line for /hazard list (phase, cycle, counts). Never null.</summary>
        string Describe(HazardContext ctx);

        /// <summary>The hidden answer for /hazard reveal (the memory sequence), or null when there is none.</summary>
        string Reveal(HazardContext ctx);
    }

    /// <summary>
    /// A mechanic that reads runtime controls (<see cref="HazardContext.TakeControls"/>) sent with
    /// /hazard control. It still ignores an unknown key with one Log line, never throws.
    /// </summary>
    public interface IHazardControllable
    {
        /// <summary>The lower-case control keys this mechanic understands.</summary>
        IReadOnlyList<string> ControlKeys { get; }
    }
}
