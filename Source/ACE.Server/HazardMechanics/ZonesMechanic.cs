using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>The zones mechanic's numbers: defaults and clamps, read from the merged args (no settings string of its own).</summary>
    public sealed class ZoneSettings
    {
        public const string Corners = "corners";

        public static readonly string[] NumberKeys = { "radius", "tick", "pct", "pct_end", "ramp", "reset" };

        public const double DefaultRadius = 3.0;
        public const double MinRadius = 0.5;
        public const double MaxRadius = 20.0;

        public const double DefaultTick = 1.0;
        public const double MinTick = 0.1;
        public const double MaxTick = 30.0;

        public const double DefaultPct = 0.05;
        public const double DefaultPctEnd = 0.35;

        public const double DefaultRamp = 5.0;
        public const double MinRamp = 0.0;
        public const double MaxRamp = 300.0;

        public const double DefaultReset = 1.0;
        public const double MinReset = 0.0;
        public const double MaxReset = 60.0;

        /// <summary>Metres above the ground the pillar host sits (the owner's -47.70 on a -47.75 floor).</summary>
        public const float PillarLift = 0.05f;

        /// <summary>The pillar hosts live until the run is stopped; this is only the timer's far horizon.</summary>
        public const double PillarSeconds = 86400.0;

        public double Radius { get; private set; }
        public double Tick { get; private set; }
        public double Pct { get; private set; }
        public double PctEnd { get; private set; }
        public double Ramp { get; private set; }
        public double Reset { get; private set; }

        public static ZoneSettings Read(HazardArgs args)
        {
            args ??= HazardArgs.Empty;

            return new ZoneSettings
            {
                Radius = args.GetDouble("radius", DefaultRadius, MinRadius, MaxRadius),
                Tick = args.GetDouble("tick", DefaultTick, MinTick, MaxTick),
                Pct = args.GetDouble(HazardDefaults.PctKey, DefaultPct, HazardDefaults.MinPct, HazardDefaults.MaxPct),
                PctEnd = args.GetDouble("pct_end", DefaultPctEnd, HazardDefaults.MinPct, HazardDefaults.MaxPct),
                Ramp = args.GetDouble("ramp", DefaultRamp, MinRamp, MaxRamp),
                Reset = args.GetDouble("reset", DefaultReset, MinReset, MaxReset),
            };
        }
    }

    /// <summary>
    /// Fire pillars that punish standing still: pattern <c>corners</c> raises one fire pillar (HazardDefaults.PillarScript)
    /// at each corner of a RECT region. A player within <c>radius</c> + player radius of any pillar runs a dwell clock and
    /// takes pct(t) of max health at entry and every <c>tick</c> seconds, ramping linearly from <c>pct</c> to
    /// <c>pct_end</c> over <c>ramp</c> seconds of dwell; <c>reset</c> seconds outside starts the clock over. The decisions
    /// live in <see cref="ZoneRules"/> / <see cref="ZoneClock"/>. The run never ends by itself: stop it (/hazard stop, or the
    /// boss script by tag). Damage goes through <see cref="HazardContext.HitPlayer"/>. Args: <see cref="ZoneSettings"/>.
    /// </summary>
    public sealed class ZonesMechanic : IHazardMechanic
    {
        private static readonly string[] patterns = { ZoneSettings.Corners };

        private readonly List<HazardHandle> pillars = new List<HazardHandle>();
        private readonly Dictionary<uint, ZoneClock> clocks = new Dictionary<uint, ZoneClock>();

        private ZoneSettings settings = ZoneSettings.Read(null);
        private volatile string status = "not started";

        public string Kind => HazardMechanicRegistry.Zones;

        public IReadOnlyList<string> Patterns => patterns;

        public IEnumerable<uint> RequiredWcids(HazardArgs args)
        {
            yield return HazardDefaults.EffectHostWcid;
        }

        public string Validate(HazardRegion region, HazardArgs args, string pattern)
        {
            if (region == null)
                return "no region";

            args ??= HazardArgs.Empty;

            var notNumeric = args.FirstNonNumeric(ZoneSettings.NumberKeys);

            if (notNumeric != null)
                return notNumeric;

            if (ZoneRules.RegionCorners(region).Count == 0)
                return "zones need a rectangular region (a pillar stands at each corner); this region is not a rect";

            return null;
        }

        public void Start(HazardContext ctx)
        {
            settings = ZoneSettings.Read(ctx.Args);

            var corners = ZoneRules.RegionCorners(ctx.Region);
            var missed = new List<Vector3>();

            foreach (var point in PillarPoints(ctx.Region, ctx.EdgeInset, ctx.GroundZ))
            {
                var handle = ctx.SpawnTimedEffect(point, HazardDefaults.PillarScript, Quaternion.Identity, ZoneSettings.PillarSeconds, 1f, "zone-pillar", allowRaise: false);

                if (handle != null)
                    pillars.Add(handle);
                else
                    missed.Add(point);
            }

            if (pillars.Count == 0)
            {
                ctx.End(HazardOutcome.Aborted, "no pillar could be placed");
                return;
            }

            // review 2026-10-20: a corner with no pillar is a safe spot; the run goes on, but never silently
            if (pillars.Count < corners.Count)
                ctx.LogWarn($"zones placed only {pillars.Count} of {corners.Count} corner pillar(s); missing at {string.Join(" ", missed.Select(p => $"({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})"))}");

            status = $"{pillars.Count} pillar(s), radius {settings.Radius:0.#}, {settings.Pct:0.##}->{settings.PctEnd:0.##} over {settings.Ramp:0.#}s";
            ctx.Log($"zones start pattern={ctx.Pattern ?? ZoneSettings.Corners} {status} tick={settings.Tick:0.##} reset={settings.Reset:0.##}");
        }

        /// <summary>
        /// Where the pillars stand: each region corner clamped <paramref name="edgeInset"/> inside, at the ground +
        /// <see cref="ZoneSettings.PillarLift"/>. Pure given <paramref name="groundZ"/>; PinnacleArenaGeometryTests places
        /// the real effect host at every one.
        /// </summary>
        internal static List<Vector3> PillarPoints(HazardRegion region, float edgeInset, Func<float, float, float> groundZ)
        {
            var points = new List<Vector3>();

            foreach (var corner in ZoneRules.RegionCorners(region))
            {
                var xy = region.ClampInside(corner, edgeInset);
                points.Add(new Vector3(xy.X, xy.Y, groundZ(xy.X, xy.Y) + ZoneSettings.PillarLift));
            }

            return points;
        }

        public void Tick(HazardContext ctx)
        {
            var centres = new List<Vector3>(pillars.Count);
            HazardHandle source = null;

            foreach (var p in pillars)
            {
                if (!p.Alive)
                    continue;

                centres.Add(p.Rest);
                source ??= p;
            }

            if (source == null)
            {
                ctx.End(HazardOutcome.Aborted, "every pillar was lost");
                return;
            }

            var playerRadius = ctx.PlayerRadius;
            var inside = new Dictionary<uint, (WorldObjects.Player Player, bool In)>();

            foreach (var (player, pos) in ctx.PlayersInRegion())
                inside[player.Guid.Full] = (player, ZoneRules.InAnyZone(centres, pos, settings.Radius, playerRadius));

            foreach (var (player, isIn) in inside.Values)
            {
                if (!clocks.TryGetValue(player.Guid.Full, out var clock))
                {
                    if (!isIn)
                        continue;

                    clock = new ZoneClock(settings.Tick, settings.Pct, settings.PctEnd, settings.Ramp, settings.Reset);
                    clocks[player.Guid.Full] = clock;
                }

                var pct = clock.Update(ctx.Now, isIn);

                if (pct.HasValue)
                    ctx.HitPlayer(source, player, pct.Value);
            }

            // a tracked player who left the region altogether (or died) is outside: run their clock down
            var gone = new List<uint>();

            foreach (var kv in clocks)
            {
                if (!inside.ContainsKey(kv.Key))
                    kv.Value.Update(ctx.Now, false);

                if (kv.Value.Expired)
                    gone.Add(kv.Key);
            }

            foreach (var id in gone)
                clocks.Remove(id);
        }

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
        {
            clocks.Clear();
            ctx.Log($"zones stop outcome={outcome}");
        }

        public string Describe(HazardContext ctx) => status;

        public string Reveal(HazardContext ctx) => null;
    }
}
