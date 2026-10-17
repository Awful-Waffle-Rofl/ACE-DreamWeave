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
    /// The goal a running event is trying to satisfy (TECH-DESIGN 2.5). Implementations:
    /// KillCountObjective, DestroySourceObjective, KillBossObjective and HoldObjective.
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

        /// <summary>
        /// Non-null when the objective itself has decided the run is lost (or cannot be run at all), and
        /// with which outcome. WorldEvent.TickActive checks it right after <see cref="Tick"/>, before
        /// <see cref="IsComplete"/> and the timers, and finishes the run with it. Hold uses it for "too many
        /// targets died".
        ///
        /// Defaulted to null ("never fails on its own"), which is what every objective before Hold and every
        /// test fake means.
        /// </summary>
        WorldEventOutcome? FailureOutcome => null;

        WorldEventMvp Mvp();
    }
}
