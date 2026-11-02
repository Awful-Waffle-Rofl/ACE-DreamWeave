using System;
using System.Collections.Generic;

namespace ACE.Server.HazardMechanics
{
    public enum WispActionKind
    {
        /// <summary>Start a step: assign the wisps, glide them to the formation in ONE shared move. Answer with <see cref="WispScript.Glided"/>.</summary>
        Glide,

        /// <summary>Wisp slot <see cref="WispAction.Slot"/> of the current step casts its pillar now.</summary>
        Burst,

        /// <summary>The previous step of a sweep is over; the next Glide follows.</summary>
        NextStep,

        /// <summary>The cycle is over; the next one starts after <see cref="WispAction.Seconds"/>.</summary>
        Rest,

        /// <summary>The run is over with <see cref="WispAction.Outcome"/>.</summary>
        End,
    }

    public readonly struct WispAction
    {
        public WispAction(WispActionKind kind, int cycle, int step, int slot, double seconds, HazardOutcome outcome, string reason, bool firstOfCycle)
        {
            Kind = kind;
            Cycle = cycle;
            Step = step;
            Slot = slot;
            Seconds = seconds;
            Outcome = outcome;
            Reason = reason;
            FirstOfCycle = firstOfCycle;
        }

        public WispActionKind Kind { get; }

        /// <summary>1-based.</summary>
        public int Cycle { get; }

        /// <summary>0-based step within the cycle.</summary>
        public int Step { get; }

        public int Slot { get; }

        /// <summary>Glide: the glide time (move for the first step, sweep_move after). Rest: the pause.</summary>
        public double Seconds { get; }

        public HazardOutcome Outcome { get; }

        public string Reason { get; }

        /// <summary>Glide: the first step of a cycle, so the plan for the cycle must be made now.</summary>
        public bool FirstOfCycle { get; }

        public override string ToString() => $"{Kind} c{Cycle} s{Step} slot{Slot} {Seconds:0.###} {Outcome} {Reason}";
    }

    /// <summary>
    /// THE CYCLE/STEP STATE MACHINE of the wisps mechanic (shared invariant 6): pure, no world, no clock. The mechanic
    /// calls <see cref="Advance"/> every driver tick with the run's time and how many wisps are alive, does what the returned
    /// actions say, and answers a Glide with <see cref="Glided"/>.
    ///
    ///   warm-up -> Glide -> (Glided) -> Bursts at glide + move + hold + offset -> the last pillar finishes ->
    ///   NextStep + Glide (a sweep's next row) | Rest -> next cycle | End(Completed).
    ///
    /// RAMPS: move, hold and rest are read per cycle through <see cref="WispSettings.ForCycle"/> (cycle 1 = the start key, the
    /// last cycle = its "_end" key), at Glide, Glided and FinishStep; a run with no end key reads the same numbers every cycle.
    ///
    /// GUARANTEES: no Burst before glide start + move + hold; End(Completed) only once the last pillar has finished
    /// burning; End(Aborted, "all wisps lost") on any tick that sees zero live wisps; one step is exactly one Glide.
    /// </summary>
    public sealed class WispScript
    {
        public const string AllWispsLost = "all wisps lost";

        private enum Phase
        {
            Warmup,
            Ready,
            AwaitGlided,
            Running,
            Resting,
            Ended,
        }

        private readonly WispSettings s;
        private readonly double startDelay;

        private Phase phase = Phase.Warmup;
        private double readyAt = double.NaN;
        private int cycle;
        private int step;
        private bool moreSteps;

        private double[] burstAt = Array.Empty<double>();
        private bool[] fired = Array.Empty<bool>();
        private double stepEnd;

        public WispScript(WispSettings settings, double startDelay = 0.5)
        {
            s = settings;
            this.startDelay = startDelay;
        }

        public int Cycle => cycle;

        public int Step => step;

        public bool Ended => phase == Phase.Ended;

        public string PhaseName
        {
            get
            {
                switch (phase)
                {
                    case Phase.Warmup: return "starting";
                    case Phase.Ready: return "ready";
                    case Phase.AwaitGlided: return "gliding";
                    case Phase.Running: return "telegraph/burst";
                    case Phase.Resting: return "resting";
                    default: return "ended";
                }
            }
        }

        public IReadOnlyList<WispAction> Advance(double now, int liveCount)
        {
            var actions = new List<WispAction>();

            if (phase == Phase.Ended)
                return actions;

            if (liveCount <= 0)
            {
                phase = Phase.Ended;
                actions.Add(new WispAction(WispActionKind.End, cycle, step, 0, 0.0, HazardOutcome.Aborted, AllWispsLost, false));
                return actions;
            }

            if (double.IsNaN(readyAt))
                readyAt = now + startDelay;

            switch (phase)
            {
                case Phase.Warmup:
                case Phase.Resting:
                    if (now >= readyAt)
                    {
                        cycle++;
                        step = 0;
                        phase = Phase.AwaitGlided;
                        actions.Add(Glide(true));
                    }

                    break;

                case Phase.Running:
                    for (var i = 0; i < burstAt.Length; i++)
                    {
                        if (!fired[i] && now >= burstAt[i])
                        {
                            fired[i] = true;
                            stepEnd = Math.Max(stepEnd, now + s.Burst); // a pillar cast on a late tick still burns its full time
                            actions.Add(new WispAction(WispActionKind.Burst, cycle, step, i, 0.0, default, null, false));
                        }
                    }

                    if (Array.TrueForAll(fired, f => f) && now >= stepEnd)
                        FinishStep(now, actions);

                    break;
            }

            return actions;
        }

        private WispAction Glide(bool first)
            => new WispAction(WispActionKind.Glide, cycle, step, 0, step == 0 ? s.ForCycle(cycle).Move : s.SweepMove, default, null, first);

        private void FinishStep(double now, List<WispAction> actions)
        {
            if (moreSteps)
            {
                step++;
                phase = Phase.AwaitGlided;
                actions.Add(new WispAction(WispActionKind.NextStep, cycle, step, 0, 0.0, default, null, false));
                actions.Add(Glide(false));
                return;
            }

            if (cycle >= s.Cycles)
            {
                phase = Phase.Ended;
                actions.Add(new WispAction(WispActionKind.End, cycle, step, 0, 0.0, HazardOutcome.Completed, "all cycles run", false));
                return;
            }

            // the rest after cycle k is cycle k's own (move, hold and rest ramp from cycle to cycle: WispSettings.ForCycle)
            var rest = s.ForCycle(cycle).Rest;

            phase = Phase.Resting;
            readyAt = now + rest;
            actions.Add(new WispAction(WispActionKind.Rest, cycle, step, 0, rest, default, null, false));
        }

        /// <summary>
        /// The mechanic's answer to a Glide, made at the same <paramref name="now"/>: each crew member's extra delay after the
        /// hold (WispRules.FireOffsets; empty when the step has nobody to move) and whether the plan has another step. The
        /// bursts are due at now + move + hold + offset; the step ends when the last of them has burned for <c>burst</c>.
        /// </summary>
        public void Glided(double now, IReadOnlyList<double> offsets, bool hasMoreSteps)
        {
            if (phase != Phase.AwaitGlided)
                return;

            moreSteps = hasMoreSteps;

            var c = s.ForCycle(cycle);
            var timeline = WispRules.Timeline(step == 0 ? c.Move : c.SweepMove, c.Hold, offsets, c.Burst);

            burstAt = new double[timeline.BurstAt.Count];
            fired = new bool[burstAt.Length];

            for (var i = 0; i < burstAt.Length; i++)
                burstAt[i] = now + timeline.BurstAt[i];

            stepEnd = burstAt.Length == 0 ? now : now + timeline.End;
            phase = Phase.Running;
        }
    }
}
