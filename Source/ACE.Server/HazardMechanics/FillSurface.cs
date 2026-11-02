using System;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The pure surface-Z model of the fill mechanic: where the acid/lava sheet is at any run time, given a signed
    /// rate (metres per second) between a floor and a ceiling, an optional target to stop at, and the controls that
    /// re-base it. No world, no clock of its own - every call passes <c>now</c> (the run's seconds).
    ///
    /// <see cref="Version"/> increments on every Set* call that changes the plan, so the director can tell that a control changed the plan
    /// since it last scheduled a leg.
    /// </summary>
    public sealed class FillSurface
    {
        private const double Eps = 1e-9;

        private readonly double floorZ;
        private readonly double maxZ;

        private double z0;
        private double t0;

        public FillSurface(double floorZ, double maxZ, double startZ, double rate)
        {
            this.floorZ = floorZ;
            this.maxZ = Math.Max(floorZ, maxZ);
            z0 = Clamp(startZ);
            Rate = rate;
        }

        /// <summary>Signed metres per second (positive rises).</summary>
        public double Rate { get; private set; }

        /// <summary>The z the surface stops at, or null when it runs to the floor or the top.</summary>
        public double? Target { get; private set; }

        /// <summary>Incremented by every Set* call that changes the plan (an unchanged SetRate does not).</summary>
        public int Version { get; private set; }

        private double Clamp(double z) => Math.Min(maxZ, Math.Max(floorZ, z));

        /// <summary>The surface z at run time <paramref name="now"/>.</summary>
        public double ZAt(double now)
        {
            var z = z0 + Rate * Math.Max(0.0, now - t0);

            if (Target.HasValue)
            {
                var lo = Math.Min(z0, Target.Value);
                var hi = Math.Max(z0, Target.Value);
                z = Math.Min(hi, Math.Max(lo, z));
            }

            return Clamp(z);
        }

        /// <summary>
        /// Re-bases at <paramref name="now"/> with a new signed rate and clears any target. Re-sending the rate the
        /// surface already has (with no target) changes nothing and leaves <see cref="Version"/> alone, so a host that
        /// re-asserts its rate every poll does not make the director replan every panel.
        /// </summary>
        public void SetRate(double now, double rate)
        {
            if (Target == null && rate == Rate)
                return;

            Rebase(now);
            Rate = rate;
            Target = null;
            Version++;
        }

        /// <summary>
        /// Heads for <paramref name="z"/> (clamped to the floor..top): the speed is the current |rate|, or
        /// |<paramref name="fallbackMagnitude"/>| when the surface is standing still.
        /// </summary>
        public void SetTarget(double now, double z, double fallbackMagnitude)
        {
            Rebase(now);

            var target = Clamp(z);
            var magnitude = Math.Abs(Rate) > 0.0 ? Math.Abs(Rate) : Math.Abs(fallbackMagnitude);

            Target = target;
            Rate = target > z0 ? magnitude : target < z0 ? -magnitude : Rate;
            Version++;
        }

        /// <summary>
        /// Puts the surface at <paramref name="z"/> (clamped) now, keeping the speed and any target; the rate is
        /// re-signed toward the target when there is one.
        /// </summary>
        public void SetZ(double now, double z)
        {
            t0 = now;
            z0 = Clamp(z);

            if (Target.HasValue)
            {
                var magnitude = Math.Abs(Rate);
                Rate = Target.Value > z0 ? magnitude : Target.Value < z0 ? -magnitude : Rate;
            }

            Version++;
        }

        /// <summary>
        /// Seconds from <paramref name="now"/> until the surface reaches its target or its bound; null when it is not
        /// moving or already stands at the bound it moves toward.
        /// </summary>
        public double? SecondsToNextEvent(double now)
        {
            if (Math.Abs(Rate) <= 0.0)
                return null;

            var z = ZAt(now);
            var bound = Target ?? (Rate > 0.0 ? maxZ : floorZ);
            var distance = bound - z;

            if (Math.Abs(distance) <= Eps || Math.Sign(distance) != Math.Sign(Rate))
                return null;

            return Math.Abs(distance) / Math.Abs(Rate);
        }

        private void Rebase(double now)
        {
            z0 = ZAt(now);
            t0 = now;
        }
    }
}
