using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using ACE.Server.Entity;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// One object the live layer must spawn. Carries a ROLE, never a wcid. LocalPosition is in the anchor
    /// frame (x right, y forward, z up); Position is the world position. Script 0 = no visual effect.
    /// </summary>
    public readonly record struct PuzzleObjectSpec(
        PuzzleRole Role,
        int Slot,
        Vector3 LocalPosition,
        Vector3 Position,
        Quaternion Orientation,
        float Scale,
        uint Script);

    /// <summary>One round: everything answer-bearing to (re)spawn, plus the answer. Answer is admin-only data.</summary>
    public sealed class PuzzlePlan
    {
        public PuzzleGateType Type { get; init; }

        /// <summary>1-based index of this plan in the generator's stream.</summary>
        public int RoundIndex { get; init; }

        /// <summary>The gate. Identical every round; the live layer spawns it once and never respawns it.</summary>
        public PuzzleObjectSpec Gate { get; init; }

        /// <summary>
        /// Every answer-bearing object (candidates, lights, indicator), candidates in slot (spatial) order.
        /// A reshuffle destroys and recreates all of them.
        /// </summary>
        public IReadOnlyList<PuzzleObjectSpec> Specs { get; init; }

        /// <summary>Slot of the correct candidate (a spot index for shuffle).</summary>
        public int AnswerSlot { get; init; }

        /// <summary>Palette index of the answer colour, or -1 when the type has none.</summary>
        public int AnswerColourIndex { get; init; } = -1;

        /// <summary>The odd type's differing channel; Auto for every other type.</summary>
        public PuzzleOddChannel OddChannel { get; init; }

        /// <summary>Beam only: the lever slot each beam (light spec j) points at, so a diagnostic can say what every beam showed. Null for other types.</summary>
        public IReadOnlyList<int> BeamTargets { get; init; }

        public string Describe()
        {
            var colour = AnswerColourIndex >= 0 ? " colour=" + PuzzleGateColours.ByIndex(AnswerColourIndex).Name : "";
            var channel = Type == PuzzleGateType.Odd ? " diff=" + OddChannel.ToString().ToLowerInvariant() : "";
            return string.Format(CultureInfo.InvariantCulture, "round {0} answer slot={1}{2}{3}", RoundIndex, AnswerSlot + 1, colour, channel);
        }
    }

    public sealed class PuzzleGateInput
    {
        public Vector3 AnchorPosition { get; init; }

        /// <summary>Rotation about +Z in degrees (the SkyDecorLayout convention); forward is (-sin, cos).</summary>
        public float AnchorYawDeg { get; init; }

        public PuzzleGateOptions Options { get; init; }

        public int Seed { get; init; }

        /// <summary>Per-site layout (gate distance and heights). Null = <see cref="PuzzleLayoutParams.Default"/>, the admin constants.</summary>
        public PuzzleLayoutParams Layout { get; init; }

        /// <summary>Shuffle only: recorded spot world positions. Fewer than 2 falls back to a ring.</summary>
        public IReadOnlyList<Vector3> RecordedSpots { get; init; }

        /// <summary>Shuffle ring fallback only: false rejects a world point (e.g. outside every cell).</summary>
        public Func<Vector3, bool> SpotValidator { get; init; }
    }

    /// <summary>
    /// Pure layout: anchor + options + seed -> <see cref="PuzzlePlan"/>, one per round, from one RNG stream.
    /// Every round consumes the SAME fixed block of draws regardless of type or options (the
    /// SkyDecorLayout house rule), so changing an option never moves the stream.
    /// </summary>
    public sealed class PuzzleGateGenerator
    {
        private readonly PuzzleGateInput _input;
        private readonly PuzzleGateOptions _o;
        private readonly PuzzleLayoutParams _layout;
        private readonly Vector3[] _spots;
        // MUST stay non-readonly: SkyDecorRandom is a mutable struct, and a readonly field would make every
        // NextUInt() advance a defensive copy, so the stream would silently repeat its first draw forever.
        private SkyDecorRandom _rng;
        private int _round;
        private int _previousSpot = -1;

        public int Seed => _input.Seed;

        /// <summary>Number of plans produced so far.</summary>
        public int Round => _round;

        /// <summary>Shuffle spot world positions in use (empty for the other types).</summary>
        public IReadOnlyList<Vector3> Spots => _spots;

        private PuzzleGateGenerator(PuzzleGateInput input, Vector3[] spots)
        {
            _input = input;
            _o = input.Options;
            _layout = input.Layout ?? PuzzleLayoutParams.Default;
            _spots = spots;

            // A raw small seed starves xorshift's first outputs; mix, then discard a few draws.
            unchecked
            {
                _rng = new SkyDecorRandom((uint)input.Seed * 2654435761u ^ 0x9E3779B9u);
            }

            for (var i = 0; i < 4; i++)
                _rng.NextUInt();

            // Per-placement lever count: drawn ALWAYS, for every type and whether or not n= was given, so the
            // per-round stream below starts at the same point regardless of options (the fixed-order rule).
            var nRoll = _rng.NextUInt();

            N = PuzzleGateOptions.IsPickType(_o.Type) && !_o.NExplicit
                ? PuzzleGateTunables.MinPickN + (int)(nRoll % (uint)(PuzzleGateTunables.MaxPickN - PuzzleGateTunables.MinPickN + 1))
                : _o.N;
        }

        /// <summary>
        /// The lever count in effect for this placement: the explicit n=, or for sigil and odd with n= omitted
        /// the 4-or-5 drawn from the seed. Constant across rounds.
        /// </summary>
        public int N { get; }

        public static bool TryCreate(PuzzleGateInput input, out PuzzleGateGenerator generator, out string error)
        {
            generator = null;
            error = null;

            if (input?.Options == null)
            {
                error = "Options are required.";
                return false;
            }

            if (input.Layout != null && !input.Layout.TryValidate(out error))
                return false;

            var spots = Array.Empty<Vector3>();

            if (input.Options.Type == PuzzleGateType.Shuffle)
            {
                spots = BuildSpots(input);

                if (spots.Length < PuzzleGateTunables.MinSpots)
                {
                    error = "Fewer than 2 usable shuffle spots (record spots or pick a clearer radius).";
                    return false;
                }
            }

            generator = new PuzzleGateGenerator(input, spots);
            return true;
        }

        // ---- frame helpers -------------------------------------------------------------------------

        public static Vector3 ToWorld(Vector3 anchor, float yawDeg, Vector3 local)
        {
            var yaw = yawDeg * Math.PI / 180.0;
            var c = (float)Math.Cos(yaw);
            var s = (float)Math.Sin(yaw);
            return new Vector3(anchor.X + local.X * c - local.Y * s, anchor.Y + local.X * s + local.Y * c, anchor.Z + local.Z);
        }

        public static Vector3 ToLocal(Vector3 anchor, float yawDeg, Vector3 world)
        {
            var yaw = yawDeg * Math.PI / 180.0;
            var c = (float)Math.Cos(yaw);
            var s = (float)Math.Sin(yaw);
            var dx = world.X - anchor.X;
            var dy = world.Y - anchor.Y;
            return new Vector3(dx * c + dy * s, -dx * s + dy * c, world.Z - anchor.Z);
        }

        /// <summary>Yaw (degrees) of a quaternion, in the same convention. For the live layer's use on a Player rotation.</summary>
        public static float YawDegFromQuaternion(Quaternion q)
        {
            var yaw = Math.Atan2(2.0 * (q.W * q.Z + q.X * q.Y), 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z));
            return (float)(yaw * 180.0 / Math.PI);
        }

        public static Quaternion YawQuaternion(float yawDeg)
        {
            SkyDecorLayout.Orientation(yawDeg, 0.0f, true, out var w, out var x, out var y, out var z);
            return new Quaternion(x, y, z, w);
        }

        /// <summary>Local position of lever <paramref name="slot"/> of <paramref name="n"/>: a row facing the anchor.</summary>
        public static Vector3 LeverLocal(int slot, int n)
        {
            return new Vector3((slot - (n - 1) / 2.0f) * PuzzleGateTunables.LeverSpacing, PuzzleGateTunables.LeverRowDistance, 0.0f);
        }

        public static Vector3 GateLocal => new Vector3(0.0f, PuzzleGateTunables.GateDistance, 0.0f);

        /// <summary>This generator's gate position in the anchor frame (the layout's gate distance).</summary>
        public Vector3 GateLocalPoint => new Vector3(0.0f, _layout.GateDistance, 0.0f);

        // ---- generation ----------------------------------------------------------------------------

        private PuzzleObjectSpec Spec(PuzzleRole role, int slot, Vector3 local, float yawDeg, float scale, uint script)
        {
            return new PuzzleObjectSpec(role, slot, local, ToWorld(_input.AnchorPosition, _input.AnchorYawDeg, local),
                YawQuaternion(yawDeg), scale, script);
        }

        private PuzzleObjectSpec SpecQ(PuzzleRole role, int slot, Vector3 local, Quaternion q, float scale, uint script)
        {
            return new PuzzleObjectSpec(role, slot, local, ToWorld(_input.AnchorPosition, _input.AnchorYawDeg, local), q, scale, script);
        }

        /// <summary>Produces the next round's plan (the first call is round 1). Deterministic per seed.</summary>
        public PuzzlePlan Next()
        {
            // ---- the fixed draw block: identical count for every type and option set ----
            var answerRoll = _rng.NextUInt();

            // Always a 5-entry shuffle (4 draws) so the stream never depends on the palette size; the
            // colour order is then the shuffled indices that exist in the palette.
            var palDraw = new[] { 0, 1, 2, 3, 4 };
            Shuffle(palDraw);
            var pal = palDraw.Where(c => c < PuzzleGateColours.Palette.Length).ToArray();

            var perm = new[] { 0, 1, 2, 3, 4 };
            Shuffle(perm);

            var oddChannelRoll = _rng.NextUInt();
            var oddSignRoll = _rng.NextUInt();
            var oddMag = _rng.NextDouble();

            var noiseScale = new double[PuzzleGateTunables.DrawSlots];
            var noiseYaw = new double[PuzzleGateTunables.DrawSlots];

            for (var i = 0; i < PuzzleGateTunables.DrawSlots; i++)
            {
                noiseScale[i] = _rng.NextDouble() * 2.0 - 1.0;
                noiseYaw[i] = _rng.NextDouble() * 2.0 - 1.0;
            }

            var shuffleRoll = _rng.NextUInt();
            // ---- end of draws ----

            _round++;

            var faceYaw = _input.AnchorYawDeg + 180.0f;
            var gate = Spec(PuzzleRole.Gate, 0, GateLocalPoint, faceYaw, 1.0f, 0);
            var specs = new List<PuzzleObjectSpec>();
            var answerSlot = 0;
            var answerColour = -1;
            var channel = PuzzleOddChannel.Auto;
            var n = N;
            IReadOnlyList<int> beamTargets = null;

            switch (_o.Type)
            {
                case PuzzleGateType.Sigil:
                {
                    answerSlot = (int)(answerRoll % (uint)n);

                    for (var i = 0; i < n; i++)
                        specs.Add(Spec(PuzzleRole.Candidate, i, LeverLocal(i, n), faceYaw, 1.0f, 0));

                    var down = PuzzleBeamAim.Aim(new Vector3(0, 0, -1), _o.Axis);

                    // The ANSWER takes the first shuffled colour and is the only lever with it; the decoys
                    // cycle through the remaining Palette.Length - 1 colours in slot order, so with more
                    // decoys than colours one decoy colour repeats but the answer colour never does. The
                    // indicator shows the answer colour, so the match stays unambiguous.
                    answerColour = pal[0];
                    var decoyColours = pal.Length - 1;
                    var decoy = 0;

                    for (var i = 0; i < n; i++)
                    {
                        var lp = LeverLocal(i, n);
                        var colour = i == answerSlot ? answerColour : pal[1 + (decoy++ % decoyColours)];
                        specs.Add(SpecQ(PuzzleRole.Light, i, lp + new Vector3(0, 0, _layout.LightHeight), down, 1.0f,
                            PuzzleGateColours.ByIndex(colour).Script));
                    }

                    AddIndicators(specs, answerColour);
                    break;
                }

                case PuzzleGateType.Beam:
                {
                    var k = _o.Beams;

                    for (var i = 0; i < n; i++)
                        specs.Add(Spec(PuzzleRole.Candidate, i, LeverLocal(i, n), faceYaw, 1.0f, 0));

                    var targets = new List<int>();

                    foreach (var p in perm)
                        if (p < n && targets.Count < k)
                            targets.Add(p);

                    var hub = new Vector3(0.0f, PuzzleGateTunables.LeverRowDistance + PuzzleGateTunables.HubForward, _layout.HubHeight);
                    var answerBeam = (int)(answerRoll % (uint)k);

                    for (var j = 0; j < k; j++)
                    {
                        var target = LeverLocal(targets[j], n) + new Vector3(0, 0, PuzzleGateTunables.BeamTargetHeight);
                        var host = PuzzleBeamAim.HostPosition(target, hub, PuzzleGateTunables.BeamLength);
                        // Aim in WORLD space: the spec orientation is a world quaternion, the anchor frame is yawed.
                        var q = PuzzleBeamAim.Aim(ToWorld(Vector3.Zero, _input.AnchorYawDeg, target - host), _o.Axis);
                        specs.Add(SpecQ(PuzzleRole.Light, j, host, q, 1.0f, PuzzleGateColours.ByIndex(pal[j]).Script));
                    }

                    answerSlot = targets[answerBeam];
                    answerColour = pal[answerBeam];
                    beamTargets = targets;

                    if (k > 1)
                        AddIndicators(specs, answerColour);

                    break;
                }

                case PuzzleGateType.Odd:
                {
                    answerSlot = (int)(answerRoll % (uint)n);

                    channel = _o.Diff != PuzzleOddChannel.Auto
                        ? _o.Diff
                        : (PuzzleOddChannel)(1 + (int)(oddChannelRoll % 3u));

                    var sign = (oddSignRoll & 1u) == 0 ? 1.0f : -1.0f;

                    for (var i = 0; i < n; i++)
                    {
                        var scale = 1.0f + (float)noiseScale[i] * PuzzleGateTunables.NoiseScale;
                        var yaw = faceYaw + (float)noiseYaw[i] * PuzzleGateTunables.NoiseYawDeg;
                        uint script = 0;

                        if (i == answerSlot)
                        {
                            switch (channel)
                            {
                                case PuzzleOddChannel.Scale:
                                    scale *= sign > 0 ? PuzzleGateTunables.OddScaleHigh : PuzzleGateTunables.OddScaleLow;
                                    break;
                                case PuzzleOddChannel.Yaw:
                                    yaw += sign * (PuzzleGateTunables.OddYawMinDeg
                                        + (float)oddMag * (PuzzleGateTunables.OddYawMaxDeg - PuzzleGateTunables.OddYawMinDeg));
                                    break;
                                case PuzzleOddChannel.Glow:
                                    script = PuzzleGateColours.GlowScript;
                                    break;
                            }
                        }

                        specs.Add(Spec(PuzzleRole.Candidate, i, LeverLocal(i, n), yaw, scale, script));
                    }

                    break;
                }

                default: // Shuffle
                {
                    var count = _spots.Length;

                    answerSlot = _previousSpot < 0
                        ? (int)(shuffleRoll % (uint)count)
                        : (_previousSpot + 1 + (int)(shuffleRoll % (uint)(count - 1))) % count;

                    _previousSpot = answerSlot;

                    var local = ToLocal(_input.AnchorPosition, _input.AnchorYawDeg, _spots[answerSlot]);
                    specs.Add(new PuzzleObjectSpec(PuzzleRole.Candidate, answerSlot, local, _spots[answerSlot],
                        YawQuaternion(faceYaw), 1.0f, 0));
                    break;
                }
            }

            return new PuzzlePlan
            {
                Type = _o.Type,
                RoundIndex = _round,
                Gate = gate,
                Specs = specs,
                AnswerSlot = answerSlot,
                AnswerColourIndex = answerColour,
                OddChannel = channel,
                BeamTargets = beamTargets,
            };
        }

        /// <summary>
        /// The indicator beams: IndicatorCount of them in front of the door (toward the lever row), spread
        /// evenly across its face, all the target colour, straight down from the same apex height.
        /// </summary>
        private void AddIndicators(List<PuzzleObjectSpec> specs, int colourIndex)
        {
            var down = PuzzleBeamAim.Aim(new Vector3(0, 0, -1), _o.Axis);

            for (var i = 0; i < PuzzleGateTunables.IndicatorCount; i++)
            {
                var x = (i - (PuzzleGateTunables.IndicatorCount - 1) / 2.0f) * PuzzleGateTunables.IndicatorSpacing;
                var local = new Vector3(x, _layout.GateDistance - PuzzleGateTunables.IndicatorForward, _layout.IndicatorHeight);
                specs.Add(SpecQ(PuzzleRole.Indicator, i, local, down, 1.0f, PuzzleGateColours.ByIndex(colourIndex).Script));
            }
        }

        /// <summary>Fisher-Yates over 5 entries: always exactly 4 draws.</summary>
        private void Shuffle(int[] a)
        {
            for (var i = a.Length - 1; i >= 1; i--)
            {
                var j = _rng.NextInt(i + 1);
                var t = a[i];
                a[i] = a[j];
                a[j] = t;
            }
        }

        private static Vector3[] BuildSpots(PuzzleGateInput input)
        {
            var o = input.Options;
            var recorded = input.RecordedSpots;

            if (recorded != null && recorded.Count >= PuzzleGateTunables.MinSpots)
            {
                var take = Math.Min(recorded.Count, o.SpotsExplicit ? o.Spots : PuzzleGateTunables.MaxSpots);
                var list = new Vector3[take];

                for (var i = 0; i < take; i++)
                    list[i] = recorded[i];

                return list;
            }

            var ring = new List<Vector3>();

            // Every ring point must stay in FRONT of the gate (local y < GateLocal.Y - clearance): a lever behind
            // the gate is unreachable in a corridor and soft-locks the puzzle. A ring that fits wholly in front
            // keeps the plain even spacing (k = 0 straight ahead); a wider one spreads the K points evenly over
            // the open arc only, endpoints excluded, so every point is strictly in front. No RNG is drawn here,
            // so the stream order is untouched.
            var limit = (input.Layout ?? PuzzleLayoutParams.Default).GateDistance - PuzzleGateTunables.ShuffleGateClearance;
            var fullCircle = o.Radius < limit;
            var a0 = fullCircle ? 0.0 : Math.Acos(Math.Clamp(limit / o.Radius, -1.0, 1.0));

            for (var k = 0; k < o.Spots; k++)
            {
                var a = fullCircle
                    ? 2.0 * Math.PI * k / o.Spots
                    : a0 + (k + 1) * (2.0 * Math.PI - 2.0 * a0) / (o.Spots + 1);
                var local = new Vector3((float)(o.Radius * Math.Sin(a)), (float)(o.Radius * Math.Cos(a)), 0.0f);
                var world = ToWorld(input.AnchorPosition, input.AnchorYawDeg, local);

                if (input.SpotValidator == null || input.SpotValidator(world))
                    ring.Add(world);
            }

            return ring.ToArray();
        }
    }
}