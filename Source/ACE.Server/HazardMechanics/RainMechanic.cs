using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The "rain" hazard: balls fall out of the sky onto marked points and burst on impact. Registered as "rain"; NOT
    /// one of HazardMechanicRegistry.MechanicKinds (it has no hazard_mech_ settings string; its args come from the
    /// caller, the World Event). THIS CLASS ONLY ADAPTS A <see cref="HazardContext"/> TO THE DIRECTOR'S PORT; every
    /// decision lives in <see cref="RainRules"/> and the run in <see cref="RainDirector{THandle}"/>.
    ///
    /// OWNER INTENT (HAZARDS-DESIGN.md goal 4): the balls start at drop height (the Sky Rift portal height, drop_z), fall
    /// without hovering (the lead is a short spawn-in, not a telegraph), faster than rolling balls, as a random fire-at-will
    /// stream, and burst and vanish on impact. The warning is the ground marker at the impact point for the whole fall.
    /// Arenas are large, so target_share (default 0.6) of the drops are aimed: each lands within target_spread (default 5 m)
    /// of a randomly chosen player in the region; the rest fall uniformly, so empty ground still gets rain.
    ///
    /// HOW THE SHARED INVARIANTS ARE MET. Spawns: ctx.Spawn (balls) and ctx.SpawnTimedEffect (markers, bursts). Movement:
    /// ctx.MoveLeg, one straight leg per ball. Damage: ONE call per ball (ctx.HitPlayers) with ctx.DamageType and no
    /// text, radius burst_radius, window +infinity. Count: ctx.LiveCount, read only at a launch decision. Geometry:
    /// ctx.Region only (drop_z is the caller's absolute height). No PropertyManager read.
    ///
    /// A ball at drop height resolves to a land cell on an outdoor landblock: HazardGround.TryResolveCell probes
    /// Position.GetCell, which returns the outdoor cell for any point above the terrain (it only searches the indoor cells
    /// when the point is BELOW the terrain). The director also asks ctx.HasCell before every spawn.
    /// </summary>
    public sealed class RainMechanic : IHazardMechanic
    {
        private RainSettings settings = RainSettings.From(HazardArgs.Empty);
        private RainDirector<HazardHandle> director;

        public string Kind => HazardMechanicRegistry.Rain;

        public IReadOnlyList<string> Patterns => Array.Empty<string>();

        public IEnumerable<uint> RequiredWcids(HazardArgs args)
            => HazardBallWcids.Required(args, RainSettings.From(args).BallWcid, HazardDefaults.EffectHostWcid);

        public string Validate(HazardRegion region, HazardArgs args, string pattern) => RainRules.Validate(region, args, pattern) ?? HazardBallWcids.Validate(args);

        public void Start(HazardContext ctx)
        {
            settings = RainSettings.From(ctx.Args);

            director = new RainDirector<HazardHandle>(ctx.Region, settings, new ContextPort(ctx, settings));

            ctx.Log(FormattableString.Invariant(
                $"rain start speed={settings.Speed:0.##} lead>={settings.Lead:0.##} burst_radius={settings.BurstRadius:0.##} duration={settings.Duration:0.##} drop_z={(settings.DropZ.HasValue ? settings.DropZ.Value.ToString("0.##", CultureInfo.InvariantCulture) : "ground+" + RainRules.DefaultDropHeight.ToString("0.##", CultureInfo.InvariantCulture))} max_falling={settings.MaxFalling} edge_inset={settings.EdgeInset:0.##} target_share={settings.TargetShare:0.##} target_spread={settings.TargetSpread:0.##} escape_speed={settings.EscapeSpeed:0.##} damage_type={ctx.DamageType} pct={ctx.Pct:0.##}"));

            director.Start(ctx.Now);
            ctx.Say($"[hazard #{ctx.Run.Id}] Death rains from the sky. Watch the markers on the ground and get clear of them.");
        }

        public void Tick(HazardContext ctx) => director?.Tick(ctx.Now);

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
            => ctx.Log(FormattableString.Invariant($"rain stop outcome={outcome} launched={director?.Launched ?? 0} aimed={director?.Aimed ?? 0} impacts={director?.Impacts ?? 0} hits={director?.Hits ?? 0} lost={director?.Lost ?? 0}"));

        public string Describe(HazardContext ctx) => director != null ? director.Describe() : "starting";

        public string Reveal(HazardContext ctx) => null;

        /// <summary>The director's world: one HazardContext.</summary>
        private sealed class ContextPort : IRainPort<HazardHandle>
        {
            private readonly HazardContext ctx;
            private readonly RainSettings s;

            /// <summary>This run's ball colours (ball_wcids, else ball_wcid) and their own picker, never ctx.Rng (HazardBallWcids).</summary>
            private readonly IReadOnlyList<uint> ballWcids;
            private readonly HazardBallColourPicker wcidPicker;

            public ContextPort(HazardContext ctx, RainSettings s)
            {
                this.ctx = ctx;
                this.s = s;
                ballWcids = HazardBallWcids.Candidates(ctx.Args, s.BallWcid);
                wcidPicker = HazardBallWcids.Picker(ctx.Run.Seed);
            }

            public Random Rng => ctx.Rng;

            public int? LiveCount => ctx.LiveCount;

            public IReadOnlyList<Vector3> PlayerPositions()
            {
                var list = ctx.PlayersInRegion();
                var result = new List<Vector3>(list.Count);

                foreach (var entry in list)
                    result.Add(entry.Position);

                return result;
            }

            public bool Alive(HazardHandle handle) => handle != null && handle.Alive;

            public float GroundZ(float x, float y) => ctx.GroundZ(x, y);

            public bool HasCell(Vector3 point) => ctx.HasCell(point);

            public HazardHandle SpawnMarker(Vector3 point, double seconds)
                => ctx.SpawnTimedEffect(point, s.MarkerScript, MemoryLayout.FloorOrientation(s.MarkerScript), seconds, 1f, "rain-marker", grounded: true);

            public HazardHandle SpawnBall(Vector3 point)
                => ctx.Spawn(new HazardSpawnSpec
                {
                    Wcid = HazardBallWcids.Pick(ballWcids, wcidPicker),
                    Position = point,
                    Moving = true,
                    Tag = "rain-ball",
                    HitRadius = RainRules.BallRadius,
                });

            public bool Fall(HazardHandle ball, Vector3 end, double seconds)
                => ctx.MoveLeg(ball, end, seconds, new HazardMoveOptions { Follow = HazardFollow.Straight, AutoStop = false });

            public HazardHandle SpawnBurst(Vector3 point, double seconds)
                => ctx.SpawnTimedEffect(point, s.BurstScript, Quaternion.Identity, seconds, 1f, "rain-burst");

            public int Strike(HazardHandle source, Vector3 centre, float radius, double window)
                => ctx.HitPlayers(source, centre, radius, window, damageType: ctx.DamageType);

            public void Destroy(HazardHandle handle)
            {
                if (handle != null)
                    ctx.Destroy(handle);
            }

            public void Say(string line) => ctx.Say($"[hazard #{ctx.Run.Id}] {line}");

            public void Log(string line) => ctx.Log(line);

            public void End(HazardOutcome outcome, string reason) => ctx.End(outcome, reason);
        }
    }
}
