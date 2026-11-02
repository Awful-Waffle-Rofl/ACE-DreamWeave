using System;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles
{
    /// <summary>What the core does with a hazard run's ending (or a refused start).</summary>
    public enum RetryDecision
    {
        /// <summary>
        /// The caller stopped this run itself, so its ending is not news. PinnacleFight never asks with
        /// stoppedByCore = true: it forgets a run the moment it stops it (phase change, hold, end, StopHazards), and a
        /// late ending for a forgotten token is dropped as an unknown token before it is classified.
        /// </summary>
        Ignore,

        /// <summary>Start the same run again on the next tick.</summary>
        Retry,

        /// <summary>Hand the script its final HazardEnded.</summary>
        Deliver,
    }

    /// <summary>
    /// The pure decisions under PinnacleFight: hold expiry, resume timing, the unload override and the hazard retry
    /// policy. No clock, no world: every input is a parameter.
    /// </summary>
    public static class PinnacleFightRules
    {
        /// <summary>The idle margin added on top of the hold, so the copy outlives the hold's own expiry tick.</summary>
        public static readonly TimeSpan UnloadMargin = TimeSpan.FromSeconds(60);

        /// <summary>How many starts one hazard request gets in total: the first and ONE retry.</summary>
        public const int MaxHazardAttempts = 2;

        /// <summary>True once <paramref name="holdSeconds"/> have passed since <paramref name="heldAt"/> (monotonic seconds).</summary>
        public static bool HoldExpired(double heldAt, double now, double holdSeconds)
            => now - heldAt >= holdSeconds;

        /// <summary>
        /// True when a resume has been armed (the owner re-entered through the Knot) and the owner has been present
        /// and alive on the copy, continuously, for at least <paramref name="graceSeconds"/>. presentSince is null
        /// while that is not the case.
        /// </summary>
        public static bool ResumeDue(bool armed, double? presentSince, double now, double graceSeconds)
            => armed && presentSince.HasValue && now - presentSince.Value >= graceSeconds;

        /// <summary>
        /// Held, or Won and not yet ended (the cache still owed, or delivered and being collected:
        /// <paramref name="wonKeepsCopy"/>): an idle window of <paramref name="holdSeconds"/> plus
        /// <see cref="UnloadMargin"/>. Every other state: null (the landblock's default idle window). The window is
        /// measured by the landblock from its own idle clock (the owner's last presence), so the caller passes the
        /// FULL hold, never the remaining hold (see PinnacleFight.UnloadIntervalOverride).
        /// </summary>
        public static TimeSpan? UnloadOverride(PinnacleFightState state, bool wonKeepsCopy, double holdSeconds)
        {
            if (state != PinnacleFightState.Held && !(state == PinnacleFightState.Won && wonKeepsCopy))
                return null;

            return TimeSpan.FromSeconds(Math.Max(0.0, holdSeconds)) + UnloadMargin;
        }

        /// <summary>
        /// The core's retry policy. <paramref name="attempts"/> is how many starts this request has made so far
        /// (1 after the first). A refused start is classified as <see cref="HazardOutcome.Aborted"/>.
        ///   - a run the caller stopped itself: Ignore;
        ///   - Completed / Failed: Deliver (a real result; never retried);
        ///   - Aborted / Reaped / Stopped (not by the core): Retry while attempts &lt; MaxHazardAttempts, else Deliver
        ///     (the core then delivers Aborted, see <see cref="DeliveredOutcome"/>).
        /// </summary>
        public static RetryDecision Classify(HazardOutcome o, bool stoppedByCore, int attempts)
        {
            if (stoppedByCore)
                return RetryDecision.Ignore;

            if (o == HazardOutcome.Completed || o == HazardOutcome.Failed)
                return RetryDecision.Deliver;

            return attempts < MaxHazardAttempts ? RetryDecision.Retry : RetryDecision.Deliver;
        }

        /// <summary>The outcome the script sees on Deliver: Completed and Failed as they are; every fault as Aborted.</summary>
        public static HazardOutcome DeliveredOutcome(HazardOutcome o)
            => o == HazardOutcome.Completed || o == HazardOutcome.Failed ? o : HazardOutcome.Aborted;
    }
}
