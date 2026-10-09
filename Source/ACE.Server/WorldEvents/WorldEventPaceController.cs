using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// One finished wave, as the pace controller sees it (TECH-DESIGN 2.15).
    /// </summary>
    public readonly struct PaceObservation
    {
        /// <summary>Wall-clock seconds from the wave landing to the last of its creatures dying.</summary>
        public readonly double ClearSeconds;

        /// <summary>
        /// True when the trash size that wave was picked at was already at its ceiling - min(cap, room) at
        /// that pick. Quantity has nothing left to give, so a speed-up must go to health instead.
        /// </summary>
        public readonly bool QuantityWasMaxed;

        public PaceObservation(double clearSeconds, bool quantityWasMaxed)
        {
            ClearSeconds = clearSeconds;
            QuantityWasMaxed = quantityWasMaxed;
        }

        public override string ToString() => $"clear={ClearSeconds:F1}s maxed={QuantityWasMaxed}";
    }

    /// <summary>
    /// The real-time difficulty controller (TECH-DESIGN 2.15). Entirely pure - no clock, no world, no
    /// spawner - so every transition is unit testable (D6). It holds two dials and nothing else:
    ///
    ///   * <see cref="QuantityBonus"/>, added to the UNCAPPED wave size before the cap/room clamp, so it
    ///     feeds both the trash size and the overflow-champion count;
    ///   * <see cref="HealthMult"/>, multiplied into the crowd-health multiplier at spawn time.
    ///
    /// The rule, evaluated once per wave pick against whatever waves have finished since the last one:
    ///
    ///   * cleared faster than pace.minWaveSeconds - push HARDER. Quantity first, because more bodies is
    ///     the cheaper and more visible knob; only when quantity is already at its ceiling does health move.
    ///   * cleared slower than pace.maxWaveSeconds, OR the oldest wave still standing at pick time is older
    ///     than that - ease OFF, in the reverse order: health comes down first, and quantity only once
    ///     health is back at 1.0. Applied AT MOST ONCE per pick however many observations say "slow", so a
    ///     run cannot collapse two or three steps in a single tick.
    /// </summary>
    public sealed class WorldEventPaceController
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventPaceController));

        private readonly PaceDef pace;
        private readonly uint runId;

        /// <summary>
        /// Latches once <see cref="Evaluate"/> first observes <see cref="PaceDef.EffectiveWindow"/> report
        /// an inverted min/max pair, so the Warn line below fires once per run rather than once per pick -
        /// a run stuck under a bad override would otherwise flood the log every single wave.
        /// </summary>
        private bool warnedInvertedWindow;

        /// <param name="pace">The theme's pace tunables. Null (a theme with no "pace" block) still gets a working controller rather than a [0, 0] window.</param>
        /// <param name="runId">For the once-per-run inverted-window Warn line only; 0 (the default) when no run context is available, e.g. a directly constructed test controller.</param>
        public WorldEventPaceController(PaceDef pace, uint runId = 0)
        {
            this.pace = pace ?? new PaceDef();
            this.runId = runId;
        }

        /// <summary>The tunables in force. Never null.</summary>
        public PaceDef Tunables => pace;

        /// <summary>Extra creatures added to the next pick's uncapped size. Never negative.</summary>
        public int QuantityBonus { get; private set; }

        /// <summary>The pace half of the spawn health multiplier. Never below 1.0.</summary>
        public double HealthMult { get; private set; } = 1.0;

        /// <summary>The most recent wave clear time observed, or null before any wave has cleared.</summary>
        public double? LastClearSeconds { get; private set; }

        /// <summary>
        /// One wave pick's worth of evidence. <paramref name="cleared"/> is every wave that finished since
        /// the previous pick (usually zero or one); <paramref name="oldestAliveWaveIsStale"/> is the
        /// "nothing has died in a long time" signal, true when the oldest wave still on the field landed
        /// more than pace.maxWaveSeconds ago.
        /// </summary>
        public void Evaluate(IReadOnlyList<PaceObservation> cleared, bool oldestAliveWaveIsStale)
        {
            var (minWaveSeconds, maxWaveSeconds) = ResolveWindow();

            var easeOff = oldestAliveWaveIsStale;

            if (cleared != null)
            {
                foreach (var observation in cleared)
                {
                    LastClearSeconds = observation.ClearSeconds;

                    if (observation.ClearSeconds < minWaveSeconds)
                        PushHarder(observation.QuantityWasMaxed);
                    else if (observation.ClearSeconds > maxWaveSeconds)
                        easeOff = true;
                }
            }

            // At most once per pick, whatever produced it - see the class remarks.
            if (easeOff)
                EaseOff();
        }

        /// <summary>
        /// <see cref="PaceDef.EffectiveWindow"/>, plus the once-per-run Warn line when the live override
        /// dials invert the pair (see <see cref="warnedInvertedWindow"/>). The ONLY place this controller
        /// reads the window - <see cref="PushHarder"/>/<see cref="EaseOff"/> only ever touch the quantity/
        /// health step dials, never the window.
        /// </summary>
        private (double Min, double Max) ResolveWindow()
        {
            var window = pace.EffectiveWindow();

            if (window.WasInverted && !warnedInvertedWindow)
            {
                warnedInvertedWindow = true;

                log.Warn($"[WORLDEVENT] run={runId} pace window overrides invert " +
                         $"(min={pace.EffectiveMinWaveSeconds:F1}s, max={pace.EffectiveMaxWaveSeconds:F1}s); " +
                         $"clamped to (min={window.Min:F1}s, max={window.Max:F1}s) for this run");
            }

            return (window.Min, window.Max);
        }

        /// <summary>
        /// Quantity first, health only when quantity is spent. Exposed for the tests; production drives it
        /// through <see cref="Evaluate"/>.
        /// </summary>
        public void PushHarder(bool quantityWasMaxed)
        {
            if (!quantityWasMaxed)
            {
                QuantityBonus += Math.Max(0, pace.EffectiveQuantityStep);
                return;
            }

            HealthMult = Math.Min(Math.Max(1.0, pace.EffectiveHealthCap), HealthMult + Math.Max(0, pace.EffectiveHealthStep));
        }

        /// <summary>
        /// Health down first, quantity only once health is back at 1.0. Exposed for the tests; production
        /// drives it through <see cref="Evaluate"/>.
        /// </summary>
        public void EaseOff()
        {
            if (HealthMult > 1.0)
            {
                HealthMult = Math.Max(1.0, HealthMult - Math.Max(0, pace.EffectiveHealthStep));
                return;
            }

            QuantityBonus = Math.Max(0, QuantityBonus - Math.Max(0, pace.EffectiveQuantityStep));
        }

        public override string ToString() => $"qbonus={QuantityBonus} health=x{HealthMult:F2}";
    }
}
