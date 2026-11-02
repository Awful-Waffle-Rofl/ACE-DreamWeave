using System;
using System.Globalization;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The rain mechanic's numbers (args "key=value", every one with a documented default and clamp). Read once per
    /// run by the mechanic and by <see cref="RainRules.Validate"/>; pure, so tests need no world.
    ///
    ///   speed          m/s of the fall                          default 10    clamp 2..40
    ///   lead           s a ball hangs at drop height first       default 0.2   clamp 0.2..10   (the spawn-in only; the fair-warning top-up goes to the marker standing earlier, never to hovering)
    ///   burst_radius   m, the damage radius at impact            default 4     clamp 0.5..15
    ///   duration       s of launching, then the run drains       default 60    clamp 1..600
    ///   drop_z         absolute z the balls start from           default: ground at the impact point + 30 m (<see cref="RainRules.DefaultDropHeight"/>)
    ///   max_falling    balls under way at once (the cap)         default 8     clamp 1..64     (a run's LiveCount replaces it at each launch decision)
    ///   target_share   fraction of drops aimed at a player      default 0.6   clamp 0..1      (0 = every drop uniform over the region, as before)
    ///   target_spread  m, radius of the disc round the chosen player the impact is drawn from   default 5   clamp 0..30
    ///   edge_inset     m kept inside the region edge             default 2     clamp 0..10     (shared key, HazardDefaults.EdgeInset)
    ///   marker_script  the ground marker's effect script         default <see cref="HazardDefaults.WispDestMarkScript"/> (decimal or 0x hex)
    ///   burst_script   the burst's effect script                 default <see cref="HazardDefaults.PillarScript"/>
    ///   ball_wcid      the falling ball's weenie                 default <see cref="HazardDefaults.RollingDeathRedWcid"/>
    ///   ball_wcids     comma list of ball weenies, one picked per ball (random colours); replaces ball_wcid (HazardBallWcids)
    ///   pct, damage_type, hit_verb   read through the context (HazardContext.Pct / DamageType / HitVerb)
    ///   escape_speed   m/s a player is assumed to run            default 3     clamp 1..12     (shared key)
    ///   player_radius  m, the player's own radius                default 0.5   clamp 0..3      (shared key)
    /// </summary>
    public sealed class RainSettings
    {
        public double Speed { get; private set; }

        public double Lead { get; private set; }

        public double BurstRadius { get; private set; }

        public double Duration { get; private set; }

        /// <summary>Absolute z the balls start from; null = ground at the impact point plus <see cref="RainRules.DefaultDropHeight"/>.</summary>
        public double? DropZ { get; private set; }

        public int MaxFalling { get; private set; }

        public float EdgeInset { get; private set; }

        /// <summary>Fraction of drops aimed at a player (0..1).</summary>
        public double TargetShare { get; private set; }

        /// <summary>Metres: radius of the disc round the chosen player the impact point is drawn from.</summary>
        public double TargetSpread { get; private set; }

        public uint MarkerScript { get; private set; }

        public uint BurstScript { get; private set; }

        public uint BallWcid { get; private set; }

        public double EscapeSpeed { get; private set; }

        public double PlayerRadius { get; private set; }

        public static RainSettings From(HazardArgs args)
        {
            args ??= HazardArgs.Empty;

            return new RainSettings
            {
                Speed = args.GetDouble("speed", RainRules.DefaultSpeed, RainRules.MinSpeed, RainRules.MaxSpeed),
                Lead = args.GetDouble("lead", RainRules.DefaultLead, RainRules.MinLead, RainRules.MaxLead),
                BurstRadius = args.GetDouble("burst_radius", RainRules.DefaultBurstRadius, RainRules.MinBurstRadius, RainRules.MaxBurstRadius),
                Duration = args.GetDouble("duration", RainRules.DefaultDuration, RainRules.MinDuration, RainRules.MaxDuration),
                DropZ = args.Has("drop_z") ? args.GetDouble("drop_z", 0.0, -100000.0, 100000.0) : (double?)null,
                MaxFalling = args.GetInt("max_falling", RainRules.DefaultMaxFalling, RainRules.MinFalling, RainRules.MaxFalling),
                EdgeInset = (float)args.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset),
                TargetShare = args.GetDouble("target_share", RainRules.DefaultTargetShare, RainRules.MinTargetShare, RainRules.MaxTargetShare),
                TargetSpread = args.GetDouble("target_spread", RainRules.DefaultTargetSpread, RainRules.MinTargetSpread, RainRules.MaxTargetSpread),
                MarkerScript = HazardToolRoutes.ParseScript(args.GetString("marker_script"), HazardDefaults.WispDestMarkScript),
                BurstScript = HazardToolRoutes.ParseScript(args.GetString("burst_script"), HazardDefaults.PillarScript),
                BallWcid = (uint)args.GetInt("ball_wcid", (int)HazardDefaults.RollingDeathRedWcid, 1, int.MaxValue),
                EscapeSpeed = args.GetDouble(HazardDefaults.EscapeSpeedKey, HazardDefaults.EscapeSpeed, HazardDefaults.MinEscapeSpeed, HazardDefaults.MaxEscapeSpeed),
                PlayerRadius = args.GetDouble(HazardDefaults.PlayerRadiusKey, HazardDefaults.PlayerRadius, HazardDefaults.MinPlayerRadius, HazardDefaults.MaxPlayerRadius),
            };
        }
    }

    /// <summary>
    /// Every decision of the rain that is not a world call (shared invariant 6): the numbers, the pre-flight, the fair
    /// warning, the launch cadence. Tested without a world. See <see cref="RainDirector{THandle}"/> for the run.
    /// </summary>
    public static class RainRules
    {
        public const double DefaultSpeed = 10.0;
        public const double MinSpeed = 2.0;
        public const double MaxSpeed = 40.0;

        public const double DefaultLead = 0.2;

        /// <summary>Floor 0.2 s: it only has to clear the one-tick deferral of a MoveLeg on the spawn tick (HazardContext.MoveLeg), 100 ms at the default tick.</summary>
        public const double MinLead = 0.2;
        public const double MaxLead = 10.0;

        public const double DefaultBurstRadius = 4.0;
        public const double MinBurstRadius = 0.5;
        public const double MaxBurstRadius = 15.0;

        public const double DefaultDuration = 60.0;
        public const double MinDuration = 1.0;
        public const double MaxDuration = 600.0;

        public const double DefaultTargetShare = 0.6;
        public const double MinTargetShare = 0.0;
        public const double MaxTargetShare = 1.0;

        public const double DefaultTargetSpread = 5.0;
        public const double MinTargetSpread = 0.0;
        public const double MaxTargetSpread = 30.0;

        /// <summary>An aimed drop tries this many points in its player's disc before falling back to a uniform point.</summary>
        public const int AimAttempts = 8;

        public const int DefaultMaxFalling = 8;
        public const int MinFalling = 1;
        public const int MaxFalling = 64;

        /// <summary>The drop height above the ground when drop_z is not given (the World Event's world_events_hazard_rain_height default).</summary>
        public const double DefaultDropHeight = 30.0;

        /// <summary>The falling ball's radius: the Rolling Death sphere at its weenie scale (<see cref="HazardDefaults.RollingDeathRadius"/>, 1.8 m). The ball's origin is its centre.</summary>
        public const float BallRadius = HazardDefaults.RollingDeathRadius;

        /// <summary>How long a burst effect stands.</summary>
        public const double BurstSeconds = 1.5;

        /// <summary>A drop must start at least this far above where it lands (else it would not fall).</summary>
        public const double MinFallMetres = 1.0;

        /// <summary>The gap between launch decisions is mean x clamp(Exp(1), these).</summary>
        public const double MinGapFactor = 0.1;
        public const double MaxGapFactor = 4.0;

        /// <summary>After the duration the run waits for the balls in the air; it gives up this many lifetimes (plus <see cref="DrainGraceSeconds"/>) later.</summary>
        public const double DrainLifetimes = 2.0;
        public const double DrainGraceSeconds = 5.0;

        /// <summary>This many placement failures in a row, with nothing ever launched, abort the run.</summary>
        public const int GiveUpAfterFailures = 25;

        public static readonly string[] NumberKeys =
        {
            "speed", "lead", "burst_radius", "duration", "drop_z", "target_share", "target_spread",
            HazardDefaults.EdgeInsetKey, HazardDefaults.PctKey, HazardDefaults.EscapeSpeedKey, HazardDefaults.PlayerRadiusKey,
        };

        public static readonly string[] WholeNumberKeys = { "max_falling", "ball_wcid" };

        /// <summary>The fair-warning floor: 0.5 s to react plus the time to run out of the burst, (burst_radius + player_radius) / escape_speed.</summary>
        public static double WarningFloor(RainSettings s) => 0.5 + (s.BurstRadius + s.PlayerRadius) / s.EscapeSpeed;

        /// <summary>Seconds a ball takes to fall from <paramref name="dropZ"/> to resting on the ground at <paramref name="groundZ"/>.</summary>
        public static double FallSeconds(double dropZ, double groundZ, double speed)
            => Math.Max(0.0, (dropZ - (groundZ + BallRadius)) / speed);

        /// <summary>The warning one drop gets before its fall: the arg, raised so that lead + fall meets the fair-warning floor. The director spends only the arg on the ball's hang; the rest is how much earlier the marker stands.</summary>
        public static double LeadFor(RainSettings s, double fall) => Math.Max(s.Lead, WarningFloor(s) - fall);

        /// <summary>The cap on balls under way: the run's live count when set, else max_falling, clamped to 1..64. Read ONLY at a launch decision.</summary>
        public static int CapFor(int? liveCount, RainSettings s) => Math.Clamp(liveCount ?? s.MaxFalling, MinFalling, MaxFalling);

        /// <summary>The mean spacing of launches at cap <paramref name="cap"/>: a ball's lifetime (lead + fall) over the cap, so about <paramref name="cap"/> are under way.</summary>
        public static double MeanGap(double lifetime, int cap) => lifetime / Math.Max(1, cap);

        /// <summary>One random gap (fire at will): mean x Exp(1), clamped to [0.1, 4] x mean. One draw from <paramref name="rng"/>.</summary>
        public static double NextGap(Random rng, double mean)
        {
            var u = rng.NextDouble();
            var e = -Math.Log(1.0 - Math.Min(u, 0.999999));

            return mean * Math.Clamp(e, MinGapFactor, MaxGapFactor);
        }

        /// <summary>The nominal lifetime of a ball: its lead plus its fall from the settings' drop at the region's reference ground.</summary>
        public static double NominalLifetime(HazardRegion region, RainSettings s)
        {
            var ground = region.GroundZ(region.CenterX, region.CenterY);
            var dropZ = s.DropZ ?? ground + DefaultDropHeight;
            var fall = FallSeconds(dropZ, ground, s.Speed);

            return LeadFor(s, fall) + fall;
        }

        /// <summary>
        /// The pre-flight (shared invariant 8): null when the run can start, else ONE line.
        ///   numbers     every numeric arg that is given must parse
        ///   damage_type one of fire|acid|cold|electric|bludgeon
        ///   scripts     marker_script and burst_script are script ids when given
        ///   region      a spot at least edge_inset inside the region that is not excluded
        ///   drop        the drop starts at least <see cref="MinFallMetres"/> above the reference ground (GroundZ at the region centre)
        ///               plus the ball radius, and under the region's ceiling when it has one
        ///   warning     lead + fall >= 0.5 + (burst_radius + player_radius) / escape_speed, the time to react and run out of a burst
        /// The fall is measured from the region's reference ground; on terrain each drop is checked again at its own
        /// ground and its lead is raised if the warning would fall short (the mechanic), so the floor holds everywhere.
        /// </summary>
        public static string Validate(HazardRegion region, HazardArgs args, string pattern)
        {
            if (region == null)
                return "no region";

            args ??= HazardArgs.Empty;
            var inv = CultureInfo.InvariantCulture;

            var notNumeric = args.FirstNonNumeric(NumberKeys, WholeNumberKeys);

            if (notNumeric != null)
                return notNumeric;

            var badDamage = HazardContext.BadDamageType(args);

            if (badDamage != null)
                return badDamage;

            foreach (var key in new[] { "marker_script", "burst_script" })
            {
                var script = args.GetString(key);

                if (script != null && !HazardToolRoutes.TryParseScript(script, out _))
                    return $"{key}='{script}' is not a script id (decimal or 0x hex)";
            }

            var s = RainSettings.From(args);

            if (region.InscribedRadius - s.EdgeInset <= 0f || !region.TrySamplePoint(new Random(1), out _, 256, s.EdgeInset))
                return "no spot of this region is both inside edge_inset and outside every exclusion: the rain would have nowhere to fall";

            var ground = region.GroundZ(region.CenterX, region.CenterY);
            var dropZ = s.DropZ ?? ground + DefaultDropHeight;

            if (dropZ - (ground + BallRadius) < MinFallMetres)
                return string.Format(inv, "drop_z={0:0.##} is not above the ground ({1:0.##}) by at least the ball radius {2:0.##} + {3:0.##} m; the balls would have nothing to fall", dropZ, ground, BallRadius, MinFallMetres);

            if (region.CeilingZ.HasValue && dropZ + BallRadius > region.CeilingZ.Value)
                return string.Format(inv, "the region has a ceiling at z {0:0.##}: rain cannot start from drop_z={1:0.##} (it needs open sky)", region.CeilingZ.Value, dropZ);

            var fall = FallSeconds(dropZ, ground, s.Speed);
            var floor = WarningFloor(s);

            if (s.Lead + fall < floor - 1e-9)
                return string.Format(inv,
                    "lead {0:0.###} s + fall {1:0.###} s is too short a warning: it must be at least {2:0.###} s (0.5 + (burst_radius {3:0.##} + player_radius {4:0.##}) / escape_speed {5:0.##}) for a player to react and run out of a burst; raise lead or drop_z, or lower speed or burst_radius",
                    s.Lead, fall, floor, s.BurstRadius, s.PlayerRadius, s.EscapeSpeed);

            return null;
        }
    }
}
