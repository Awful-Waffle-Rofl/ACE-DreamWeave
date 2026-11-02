using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The head's turn and nod bookkeeping, pure (no world, no messages): which rotation each head is booked at, the
    /// client-animated turn in flight, the nod in flight, and which scheduled stop or nod frame is still current.
    /// LivePinnacleWorld is a thin caller: it asks for a plan, sends what the plan says, and asks again when a scheduled
    /// action fires. One instance per fight, used on the fight's landblock thread only. The clock is injected (seconds,
    /// monotonic enough for sub-second delays) so the overlap cases can be driven from tests.
    ///
    /// Generation: every <see cref="BeginTurn"/> of a head bumps it. A stop or a nod frame scheduled under an older
    /// generation is stale and is dropped: the newer turn owns the head.
    /// </summary>
    public sealed class PinnacleHeadTurns
    {
        /// <summary>
        /// Seconds a nod waits after a turn's stop is due, so the stop message reaches the client before the nod's first
        /// teleport-sequenced frame (both ride the same landblock's queue, but on two separate action chains).
        /// </summary>
        public const double SettleSeconds = 0.1;

        /// <summary>
        /// A turn smaller than this (radians, about 1 degree), or one the client would land as no step at all (under half
        /// a turn step), is not sent: the head already faces the owner as nearly as the client can turn it.
        /// </summary>
        public const float MinTurnRadians = 0.0175f;

        public enum TurnKind
        {
            /// <summary>Refused: the head is dead (it keeps its death pose; nothing is sent or booked).</summary>
            Refused,

            /// <summary>No motion-table turn: teleport the head to <see cref="TurnPlan.Target"/> (the FaceToward fallback).</summary>
            Snap,

            /// <summary>Already facing: nothing to start; stop the turn in flight first if <see cref="TurnPlan.StopInFlight"/>.</summary>
            Hold,

            /// <summary>Start a client turn at <see cref="TurnPlan.CommandSpeed"/> and stop it after <see cref="TurnPlan.Seconds"/>.</summary>
            Turn,
        }

        public readonly struct TurnPlan
        {
            public TurnKind Kind { get; init; }
            public int Generation { get; init; }

            /// <summary>The booked rotation: where the client lands (Turn), stays (Hold) or is put (Snap).</summary>
            public Quaternion Target { get; init; }

            /// <summary>The signed TurnRight speed to send (Turn).</summary>
            public float CommandSpeed { get; init; }

            /// <summary>When to send the stop, seconds after the start (Turn).</summary>
            public double Seconds { get; init; }

            /// <summary>Hold: a turn was in flight and must be stopped at <see cref="Target"/>.</summary>
            public bool StopInFlight { get; init; }

            /// <summary>
            /// A nod in flight was superseded after it had drawn a pitched frame: put the head back at this rest rotation
            /// (teleport-sequenced, like a nod frame) before the new turn, or the new turn rotates about a pitched axis.
            /// </summary>
            public Quaternion? RestoreRest { get; init; }
        }

        public readonly struct NodPlan
        {
            public bool Refused { get; init; }
            public int Generation { get; init; }

            /// <summary>The rotation the nod pitches from and returns to: the booked one.</summary>
            public Quaternion Rest { get; init; }

            /// <summary>Seconds to wait before the first frame: the rest of a turn in flight plus <see cref="SettleSeconds"/>, else 0.</summary>
            public double DelaySeconds { get; init; }
        }

        private sealed class Turn
        {
            public int Generation;
            public Quaternion From;
            public float Angle;
            public double StartedAt;
            public double Seconds;
        }

        private sealed class Head
        {
            public int Generation;
            public Quaternion? Booked;
            public Turn Turn;

            /// <summary>The generation of the nod in flight, and whether it has drawn a pitched frame not yet followed by its rest frame.</summary>
            public int NodGeneration = -1;
            public bool NodPitched;
            public Quaternion NodRest;
        }

        private readonly Func<double> clock;
        private readonly Dictionary<int, Head> heads = new Dictionary<int, Head>();

        public PinnacleHeadTurns(Func<double> clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        private Head For(int id)
        {
            if (!heads.TryGetValue(id, out var head))
                heads[id] = head = new Head();

            return head;
        }

        /// <summary>The current generation of <paramref name="id"/> (0 before its first turn).</summary>
        public int Generation(int id) => heads.TryGetValue(id, out var head) ? head.Generation : 0;

        /// <summary>The booked rotation, or <paramref name="spawn"/> before the first turn.</summary>
        public Quaternion Booked(int id, Quaternion spawn) => heads.TryGetValue(id, out var head) && head.Booked.HasValue ? head.Booked.Value : spawn;

        /// <summary>True while a client turn of <paramref name="id"/> is in flight (started, its stop not yet taken).</summary>
        public bool InFlight(int id) => heads.TryGetValue(id, out var head) && head.Turn != null;

        /// <summary>
        /// The rotation the client shows now: part way through a turn in flight (interpolated, so within one step of the
        /// client's stepped turn), else the booked rotation, else <paramref name="spawn"/>.
        /// </summary>
        public Quaternion Current(int id, Quaternion spawn)
        {
            if (heads.TryGetValue(id, out var head) && head.Turn != null)
            {
                var t = head.Turn;
                return PinnacleFacing.PartWay(t.From, t.Angle, t.Seconds <= 0 ? 1.0 : (clock() - t.StartedAt) / t.Seconds);
            }

            return Booked(id, spawn);
        }

        /// <summary>
        /// Plans a turn of <paramref name="id"/> to face <paramref name="to"/>. <paramref name="omegaZ"/> and
        /// <paramref name="framerate"/> are the motion table's TurnRight cycle (omega 0 = no turn: Snap). Books the
        /// result and bumps the generation, except when the head is dead (Refused, nothing changes).
        /// </summary>
        public TurnPlan BeginTurn(int id, Quaternion spawn, bool alive, Vector2 from, Vector2 to, float faceYawOffset, float omegaZ, float framerate, float speed)
        {
            if (!alive)
                return new TurnPlan { Kind = TurnKind.Refused, Generation = Generation(id), Target = Booked(id, spawn) };

            var head = For(id);
            head.Generation++;

            Quaternion? restore = head.NodGeneration >= 0 && head.NodPitched ? head.NodRest : null;
            head.NodGeneration = -1;
            head.NodPitched = false;

            if (omegaZ == 0f)
            {
                var q = PinnacleFacing.FaceToward(spawn, from, to, faceYawOffset);
                head.Booked = q;
                head.Turn = null;
                return new TurnPlan { Kind = TurnKind.Snap, Generation = head.Generation, Target = q };
            }

            var current = restore ?? Current(id, spawn);
            var inFlight = head.Turn != null;
            head.Turn = null;

            // the client turns in whole animation-frame steps (PinnacleFacing.TurnStep), so the head is booked where the
            // client lands, within half a step of facing the owner: the nod and the next turn then start from exactly
            // what the client shows, and no correction is ever drawn
            var angle = PinnacleFacing.LocalTurnToward(current, from, to, faceYawOffset);
            var landing = PinnacleFacing.TurnLanding(angle, omegaZ, framerate);

            if (Math.Abs(angle) < MinTurnRadians || Math.Abs(landing) < MinTurnRadians)
            {
                head.Booked = current;
                return new TurnPlan { Kind = TurnKind.Hold, Generation = head.Generation, Target = current, StopInFlight = inFlight, RestoreRest = restore };
            }

            var target = PinnacleFacing.RotateAboutLocalUp(current, landing);
            var seconds = PinnacleFacing.TurnSeconds(angle, omegaZ, framerate, speed);

            head.Booked = target;
            head.Turn = new Turn { Generation = head.Generation, From = current, Angle = landing, StartedAt = clock(), Seconds = seconds };

            return new TurnPlan
            {
                Kind = TurnKind.Turn,
                Generation = head.Generation,
                Target = target,
                CommandSpeed = PinnacleFacing.TurnCommandSpeed(angle, omegaZ, speed),
                Seconds = seconds,
                RestoreRest = restore,
            };
        }

        /// <summary>
        /// A scheduled stop of the turn started under <paramref name="generation"/> fired. True: send the stop (the turn is
        /// over). False: stale (a newer turn owns the head) or the head is dead (its death motion must not be replaced);
        /// a dead head's turn is still closed.
        /// </summary>
        public bool TakeStop(int id, int generation, bool alive)
        {
            if (!heads.TryGetValue(id, out var head) || head.Turn == null || head.Turn.Generation != generation)
                return false;

            head.Turn = null;
            return alive;
        }

        /// <summary>Plans a nod of <paramref name="id"/>: from its booked rotation, after any turn in flight. Refused for a dead head.</summary>
        public NodPlan BeginNod(int id, Quaternion spawn, bool alive)
        {
            if (!alive)
                return new NodPlan { Refused = true, Generation = Generation(id), Rest = Booked(id, spawn) };

            var head = For(id);
            var rest = Booked(id, spawn);
            var delay = head.Turn != null ? Math.Max(0.0, head.Turn.StartedAt + head.Turn.Seconds - clock()) + SettleSeconds : 0.0;

            head.NodGeneration = head.Generation;
            head.NodPitched = false;
            head.NodRest = rest;

            return new NodPlan { Generation = head.Generation, Rest = rest, DelaySeconds = delay };
        }

        /// <summary>
        /// A scheduled nod frame of the nod planned under <paramref name="generation"/> is due. True: draw it. False: stale
        /// (a newer turn owns the head) or the head is dead. <paramref name="isRest"/> marks the nod's last frame (pitch 0):
        /// drawing it ends the nod.
        /// </summary>
        public bool TakeNodFrame(int id, int generation, bool alive, bool isRest)
        {
            if (!alive || !heads.TryGetValue(id, out var head) || head.Generation != generation || head.NodGeneration != generation)
                return false;

            if (isRest)
            {
                head.NodGeneration = -1;
                head.NodPitched = false;
            }
            else
                head.NodPitched = true;

            return true;
        }
    }
}
