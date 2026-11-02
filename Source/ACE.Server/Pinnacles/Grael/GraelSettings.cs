using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Grael
{
    /// <summary>
    /// Grael's tunables as a PURE record (no PropertyManager): the raw hazard arg strings and numbers the settings
    /// rows will carry. <see cref="Defaults"/> is built from the public const strings below; <see cref="From"/> takes
    /// whatever the rows hold (a blank or null string falls back to its default, a number is clamped to a safe floor).
    /// The accessors turn a string into the (pattern, HazardArgs) a StartHazard call takes.
    /// </summary>
    public sealed record GraelSettings(
        string P2Wisps,
        string P3Memory,
        int P3DispelCount,
        string P4Balls,
        string P4Stream,
        double P4CastSeconds,
        string P5Wisps,
        double P5RespawnSeconds,
        double P5CastSeconds)
    {
        /// <summary>The P4 row series string (pinnacle_grael_p4_rows); outside the positional list so every From caller stays as it was.</summary>
        public string P4Rows { get; init; } = DefaultP4Rows;

        /// <summary>
        /// The live hazard-pct clamp (pinnacle_hazard_pct_min / _max, PinnacleTunables.HazardPctClamp), read ONCE when the
        /// fight is created and carried here so the script stays pure. Every hazard the script starts sets its pct to
        /// HazardLevelScale.Scaled(level, ratio, min, max).
        /// </summary>
        public double HazardPctMin { get; init; } = PinnacleTunables.DefaultHazardPctMin;

        public double HazardPctMax { get; init; } = PinnacleTunables.DefaultHazardPctMax;

        // ---- authored damage ratios (owner ruling 2026-10-20): each is scaled by the Knot level, keeping its ratio to the ball ----
        /// <summary>A ball hit (every P4 ball series and the stream): the benchmark itself.</summary>
        public const double BallPct = 0.60;

        /// <summary>A wisp pillar (the P1 wisps, once P2's, and the P5 wisps): the same ratio as the ball.</summary>
        public const double WispPct = 0.60;

        /// <summary>A P3 memory pad pillar (the fire pillar on the answer pad of each shown colour): the same ratio as the ball.</summary>
        public const double MemoryPct = 0.60;

        /// <summary>The P4 corner pillars: the dwell damage at entry, ramping to <see cref="CornerPctEnd"/>.</summary>
        public const double CornerPct = 0.05;

        public const double CornerPctEnd = 0.35;

        /// <summary>
        /// The P4 corner pillars (zones / corners), a CODE constant (no settings row): one fire pillar per arena corner,
        /// radius 3 m, a hit every 1 s ramping over 5 s of dwell, reset after 1 s outside. pct / pct_end are set per level.
        /// </summary>
        public const string P4Corners = "pattern=corners radius=3 tick=1 ramp=5 reset=1";

        /// <summary>The fan and ring balls start this far out from the head centre, clear of its collision column (r 0.78 m).</summary>
        public const string OriginClear = "3";

        /// <summary>The cross in-flight cap: two crosses (each one north/south + one east/west wall) can always draw free walls.</summary>
        public const string CrossMaxInFlight = "2";

        // The follower bursts on the crew's beat (2026-10-19 playtest: on its own clock it read as a different tempo), so
        // there is no follow_every; follow_lag is how far behind its target it trails.
        public const string DefaultP2Wisps = "pattern=scatter cycles=6 move=2.5 move_end=1.5 hold=1.5 hold_end=0.5 hold_floor=0 rest=1.0 rest_end=0.5 follow=1 follow_lag=2 mark_dest=1 ceiling_beam=1.5 mark_script=0x33000798 beam_script=0x33000798";

        // The run's args are laid OVER the live hazard_mech_<kind> shard row (HazardManager.Start: HazardArgs.Merge(args,
        // settings)), and a shard row keeps whatever it was saved with. So every key whose value matters to the fight
        // is pinned here rather than left to the row underneath: beams_per_pulse in the memory string, spread in the
        // ball string (review C14). GraelSettingsTests validates these strings merged over the current HazardDefaults.
        public const string DefaultP3Memory = "len=4 beams_per_pulse=6 pulse_on=1.0 pulse_on_end=0.5 pulse_off=0.5 pulse_off_end=0.25 answer_timeout=20";

        // Round 4 playtest: twice the playback beams (6 -> 12) with one in every corner of the room, so no corner misses a
        // colour. PINNED in MemoryRoundArgs over this string and the shard row (both may still say 6); the string stays as
        // shipped so the config defaults snapshot holds.
        public const string P3BeamsPerPulse = "12";
        public const string P3BeamCorners = "1";

        // owner 2026-10-25: a fire pillar on the answer pad of each shown colour replaces the pad beams (the 2026-10-22 colour-blind
        // cue); it hits for MemoryPct scaled by the Knot level, like every other Grael hazard
        public const string P3PadPillar = "1";

        public const int DefaultP3DispelCount = 5;

        // 2026-10-19 playtest. NO pillar telegraphs in P4 (owner: the balls must not telegraph their direction with fire
        // pillars): marker=0 and ray_markers=0 are pinned, because the shard row underneath could carry either. The balls
        // still appear during their lead. CADENCE: wave_every launches the next wave 3.0 s (ramping to 1.5 s) after the
        // previous one launched, never waiting for its balls to clear, so the waves overlap; wave_gap is not used.
        // Round 3 playtest: the fan is far wider (spread=90, count 3; spread is read by the fan only, so this string
        // leaves the rings and the cross untouched). The ROW series has its own string (DefaultP4Rows).
        // LEADS are pinned in code per series (owner, 2026-10-19), whatever this string or the shard row says: the fan and
        // the rings take none (fair_lead=0 lead=0 lead_end=0, the balls move the moment they appear), the cross exactly 2 s
        // flat (fair_lead=0 lead=2 lead_end=2). The lead/lead_end here are not what any P4 series runs.
        public const string DefaultP4Balls = "waves=6 spread=90 marker=0 ray_markers=0 speed=3 speed_end=9 lead=2 lead_end=1 wave_every=3 wave_every_end=1.5";

        // Round 3 playtest, the row series alone (pinnacle_grael_p4_rows): two openings per wall (gaps=2), and the
        // waves start spaced out and end dense: the cadence is capped at 2 rows in flight early, ramping to 3 at the
        // end (max_in_flight, rounded per wave: 2, 2, 2, 3, 3, 3). A row lives about lead + roll (2 s, pinned flat in
        // P4RowArgs, + 25..35 m at 3 m/s early, at 9 m/s late), so the cap is what spaces them, and a capped schedule
        // slides (no burst after a hold). BallCadenceTests.GraelRows_* measures the launch schedule and in-flight counts.
        // The lead/lead_end in this string are overridden by that pin.
        // gap=6 is PINNED (review F1): two openings fit an east-west wall only up to about gap 6.55 m, so a balls row
        // with a wider gap would make Validate refuse the series, which would then be skipped.
        public const string DefaultP4Rows = "waves=6 gaps=2 gap=6 marker=0 ray_markers=0 speed=3 speed_end=9 lead=2 lead_end=1 wave_every=6 wave_every_end=2 max_in_flight=2 max_in_flight_end=3";
        // count, speed, speed_end and occupancy in this string (and in the live row) are overridden by the pins in P4StreamArgs
        // (60 balls at 6 -> 9 m/s, occupancy 1.25); the string stays as shipped so the config defaults snapshot holds.
        public const string DefaultP4Stream = "stream=60 count=20 speed=3 speed_end=9 marker=0";

        /// <summary>
        /// The P5 scattered balls (round 3 playtest), a CODE constant (no settings row): one random stream of 8 balls
        /// launched over 60 s at a flat 4 m/s, which the script starts again each time it completes, for as long as P5
        /// lasts. That is 0.13 balls a second, a fifth of the P4 stream's 40 per 60 s: P5 already carries the wisps, the
        /// head's casts and four adds. Flat speed because the run repeats: a ramp would fall back to its start value
        /// every minute. marker=0 (no telegraph), as in P4.
        /// </summary>
        public const string P5Balls = "stream=60 count=8 speed=4 marker=0";

        public const long DefaultP4CastSeconds = 4;

        // follow=0 is pinned: P5 has no follower, and a hazard_mech_wisps row saved with follow=1 must not add one (review R3).
        public const string DefaultP5Wisps = "pattern=scatter n=4 cycles=60 move=2.5 hold=1.5 rest=2.0 mark_dest=1 follow=0 ceiling_beam=1.5 mark_script=0x33000798 beam_script=0x33000798";

        public const long DefaultP5RespawnSeconds = 20;

        public const long DefaultP5CastSeconds = 3;

        /// <summary>The number of memory rounds the pulse ramp is spread over (L = 4, 5, 6, 7).</summary>
        public const int MemoryRounds = 4;

        /// <summary>The memory keys the SCRIPT consumes (it ramps them itself); none of them is passed through as written.</summary>
        private static readonly string[] MemoryOwnKeys = { "len", "max_len", "pulse_on", "pulse_on_end", "pulse_off", "pulse_off_end" };

        public static GraelSettings Defaults { get; } = From(null, null, DefaultP3DispelCount, null, null, DefaultP4CastSeconds, null, DefaultP5RespawnSeconds, DefaultP5CastSeconds);

        public static GraelSettings From(
            string p2Wisps,
            string p3Memory,
            long p3DispelCount,
            string p4Balls,
            string p4Stream,
            long p4CastSeconds,
            string p5Wisps,
            long p5RespawnSeconds,
            long p5CastSeconds,
            string p4Rows = null)
            => new GraelSettings(
                OrDefault(p2Wisps, DefaultP2Wisps),
                OrDefault(p3Memory, DefaultP3Memory),
                (int)Math.Clamp(p3DispelCount, 0, 50),
                OrDefault(p4Balls, DefaultP4Balls),
                OrDefault(p4Stream, DefaultP4Stream),
                Math.Max(1, p4CastSeconds),
                OrDefault(p5Wisps, DefaultP5Wisps),
                Math.Max(1, p5RespawnSeconds),
                Math.Max(1, p5CastSeconds))
            {
                P4Rows = OrDefault(p4Rows, DefaultP4Rows),
            };

        private static string OrDefault(string raw, string fallback) => string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();

        // ---- accessors ----

        /// <summary>The P1 wisps pattern and args (the pinnacle_grael_p2_wisps setting, named for the retired P2; the "pattern" token is split out).</summary>
        public (string Pattern, HazardArgs Args) P2Series() => SplitPattern(P2Wisps, "scatter");

        public (string Pattern, HazardArgs Args) P5Series() => SplitPattern(P5Wisps, "scatter");

        /// <summary>
        /// The BASE args the fan, ring and cross series are built from (P4Balls), with gaps=1 pinned, so a
        /// hazard_mech_balls row carrying gaps cannot change the cross, and fair_lead=1 pinned, so neither that row nor
        /// this string can strip the fair-lead floor. Each series then pins its own lead on top (owner, 2026-10-19:
        /// <see cref="P4FanArgs"/> and <see cref="P4RingArgs"/> take none, <see cref="P4CrossArgs"/> and
        /// <see cref="P4RowArgs"/> exactly 2 s); GraelScript never runs these base args as they are.
        /// distinct_dirs=0 and no_overlap=0 are pinned too (review 2026-10-20): a balls row carrying distinct_dirs=1 would
        /// make the fan and the rings fail Validate ("only works with the row and cross patterns"). The cross sets both
        /// back to 1 on top.
        /// </summary>
        public HazardArgs P4BallArgs() => HazardArgs.Parse(P4Balls).With("gaps", "1").With("fair_lead", "1").With("distinct_dirs", "0").With("no_overlap", "0");

        /// <summary>The lead the rows and the cross hover for (owner, 2026-10-19: "rows and cross can have 2s hover").</summary>
        public const string RowCrossLead = "2";

        /// <summary>
        /// No lead at all: fair_lead=0 lead=0 lead_end=0 PINNED, so the balls move the moment they appear. lead_end is pinned
        /// too, so a ramp in the string or the shard row can never raise a later wave's lead above 0.
        /// </summary>
        private static HazardArgs NoLead(HazardArgs args) => args.With("fair_lead", "0").With("lead", "0").With("lead_end", "0");

        /// <summary>
        /// Exactly <see cref="RowCrossLead"/> seconds, flat: fair_lead=0 lead=2 lead_end=2 PINNED. Without fair_lead=0 the
        /// fair-lead floor would RAISE it (it was 5.15 s for the rows and 6.22 s for the cross on the Pinnacle).
        /// </summary>
        private static HazardArgs FlatLead(HazardArgs args) => args.With("fair_lead", "0").With("lead", RowCrossLead).With("lead_end", RowCrossLead);

        /// <summary>The args for the P4 FAN series (owner, 2026-10-19: "no hover delay"): <see cref="P4BallArgs"/> with no lead, origin_clear=3 pinned.</summary>
        public HazardArgs P4FanArgs() => NoLead(P4BallArgs()).With("origin_clear", OriginClear);

        /// <summary>
        /// The args for the P4 CROSS series: <see cref="P4BallArgs"/> (one opening per wall) with a flat 2 s lead, and
        /// no_overlap=1 distinct_dirs=1 dirs=random max_in_flight(_end)=2 pinned (owner ruling 2026-10-20: no two crosses in
        /// flight share a wall; distinct_dirs Validate needs dirs=random, wave_every and a cap of at most 2).
        /// </summary>
        public HazardArgs P4CrossArgs()
            => FlatLead(P4BallArgs())
                .With("no_overlap", "1")
                .With("distinct_dirs", "1")
                .With("dirs", "random")
                .With("max_in_flight", CrossMaxInFlight)
                .With("max_in_flight_end", CrossMaxInFlight);

        /// <summary>
        /// The args for the P4 row series: its own string (two openings, the in-flight cap 2 -> 3), with a flat 2 s lead and
        /// no_overlap=1 distinct_dirs=1 dir=random pinned (no two rows in flight share a wall).
        /// </summary>
        public HazardArgs P4RowArgs() => FlatLead(HazardArgs.Parse(P4Rows)).With("no_overlap", "1").With("distinct_dirs", "1").With("dir", "random");

        /// <summary>
        /// The args for the ONE alternating ring series (ring-alt: out, in, out, in...): <see cref="P4BallArgs"/> with
        /// TWICE its waves, so the ring waves total what the old ring-out and ring-in series ran together (6 + 6 = 12 by
        /// default), capped at the balls' 50-wave clamp. No lead (owner, 2026-10-19: rings "should not" hover).
        /// </summary>
        public HazardArgs P4RingArgs()
        {
            var args = P4BallArgs();
            var waves = args.GetInt("waves", HazardDefaults.BallsWaves, 1, HazardDefaults.BallsMaxWaves);

            return NoLead(args.With("waves", Math.Min(HazardDefaults.BallsMaxWaves, 2 * waves).ToString(CultureInfo.InvariantCulture))).With("origin_clear", OriginClear);
        }

        /// <summary>
        /// Round 3 playtest (owner ruling): the P4 random stream sends twice the balls over the same duration, 40 instead
        /// of 20 in its 60 s. Owner ruling of 2026-10-22: the scatterball was the lightest series, so it gets 50% more balls
        /// at the same speed, 60 in its 60 s. 60 balls at 6 m/s expect about 5.9 in flight, over the 4.4 the default 0.75
        /// occupancy bound allows in this room, so <see cref="P4StreamOccupancy"/> relaxes that bound; the speeds stay 6 -> 9.
        /// </summary>
        public const string P4StreamCount = "60";

        public const string P4StreamSpeed = "6";

        public const string P4StreamSpeedEnd = "9";

        /// <summary>
        /// Owner ruling of 2026-10-22: the scatterball was the lightest series, so it gets 50% more balls at the same speed
        /// (<see cref="P4StreamCount"/> 60). The random pattern's occupancy bound (default 0.75 of the narrowest side) is
        /// relaxed to 1.25 for this stream only: that model treats every ball as a full-length parallel lane, which suits
        /// rows, but scatter balls come from all sides and block only a disc. Still a bound: 60 balls at 3 m/s are refused.
        /// </summary>
        public const string P4StreamOccupancy = "1.25";

        /// <summary>
        /// The args for the P4 stream (pattern random), with fair_lead=1 pinned and distinct_dirs=0 no_overlap=0 (row/cross
        /// only), and the owner's density PINNED (<see cref="P4StreamCount"/>, <see cref="P4StreamSpeed"/>,
        /// <see cref="P4StreamSpeedEnd"/>, <see cref="P4StreamOccupancy"/>) over whatever the pinnacle_grael_p4_stream row
        /// says: a live row saved with the old count=20 speed=3 (or occupancy=0.75) must not undo the ruling, and a row with
        /// count=60 at speed 3 would be refused by Validate.
        /// The row still sets the stream length and the marker.
        /// </summary>
        public HazardArgs P4StreamArgs()
            => HazardArgs.Parse(P4Stream)
                .With("fair_lead", "1").With("distinct_dirs", "0").With("no_overlap", "0")
                .With("count", P4StreamCount).With("speed", P4StreamSpeed).With("speed_end", P4StreamSpeedEnd).With("occupancy", P4StreamOccupancy);

        // ---- level-scaled accessors: what GraelScript actually starts ----

        /// <summary>HazardLevelScale.Scaled at <paramref name="level"/> on this fight's clamp.</summary>
        public double ScaledPct(int level, double authoredPct) => HazardLevelScale.Scaled(level, authoredPct, HazardPctMin, HazardPctMax);

        private HazardArgs WithPct(HazardArgs args, int level, double authoredPct) => args.With(HazardDefaults.PctKey, Num(ScaledPct(level, authoredPct)));

        public (string Pattern, HazardArgs Args) P2Series(int level)
        {
            var (pattern, args) = P2Series();
            return (pattern, WithPct(args, level, WispPct));
        }

        /// <summary>The wisp keys that ramp from a start value to their "_end" value over a run.</summary>
        internal static readonly string[] WispRampKeys = { "move", "hold", "rest" };

        /// <summary>
        /// Round 4 ruling: the P1 wisp series once it has run its ramp. Every ramping key is held FLAT at its hardest value:
        /// key = key_end = the end value (the start value when there is no end), both pinned so a shard row's "_end" key
        /// cannot bring a ramp back. Everything else is <see cref="P2Series(int)"/> unchanged.
        /// </summary>
        public (string Pattern, HazardArgs Args) P2SeriesFlat(int level)
        {
            var (pattern, args) = P2Series(level);

            foreach (var key in WispRampKeys)
            {
                var hardest = args.GetString(key + "_end") ?? args.GetString(key);

                if (hardest != null)
                    args = args.With(key, hardest).With(key + "_end", hardest);
            }

            return (pattern, args);
        }

        public (string Pattern, HazardArgs Args) P5Series(int level)
        {
            var (pattern, args) = P5Series();
            return (pattern, WithPct(args, level, WispPct));
        }

        /// <summary>The args of the P4 ball series <paramref name="pattern"/> (fan, ring-alt, row, cross, random), pct scaled.</summary>
        public HazardArgs P4Args(string pattern, int level)
        {
            var args = pattern == BallPatterns.Random ? P4StreamArgs()
                : pattern == BallPatterns.RingAlt ? P4RingArgs()
                : pattern == BallPatterns.Row ? P4RowArgs()
                : pattern == BallPatterns.Fan ? P4FanArgs()
                : pattern == BallPatterns.Cross ? P4CrossArgs()
                : P4BallArgs();

            return WithPct(args, level, BallPct);
        }

        /// <summary>
        /// The P5 balls (<see cref="P5Balls"/>, random pattern), with the same pins as the P4 stream (fair_lead=1, distinct_dirs=0,
        /// no_overlap=0) and speed_end pinned to speed, so a shard row carrying a speed_end cannot ramp it; pct scaled.
        /// </summary>
        public HazardArgs P5BallArgs(int level)
        {
            var args = HazardArgs.Parse(P5Balls);

            return WithPct(args.With("fair_lead", "1").With("distinct_dirs", "0").With("no_overlap", "0").With("speed_end", args.GetString("speed")), level, BallPct);
        }

        /// <summary>The P4 corner pillars: pattern and args, pct and pct_end scaled.</summary>
        public (string Pattern, HazardArgs Args) P4CornersSeries(int level)
        {
            var (pattern, args) = SplitPattern(P4Corners, ZoneSettings.Corners);
            return (pattern, WithPct(args, level, CornerPct).With("pct_end", Num(ScaledPct(level, CornerPctEnd))));
        }

        /// <summary>The opening memory length (the "len" key, 4 by default).</summary>
        public int MemoryOpeningLength => HazardArgs.Parse(P3Memory).GetInt("len", 4, 1, 16 - (MemoryRounds - 1));

        /// <summary>
        /// The args for ONE memory round of length <paramref name="length"/>: len = max_len = length, and the pulse
        /// times ramped by <see cref="HazardRamp.At"/> over <see cref="MemoryRounds"/> rounds (index = length -
        /// opening length + 1), so L=4 is the start value and L=7 the end. A pulse with no "_end" key does not ramp.
        /// Every key ending in "_end" is consumed here and not passed to the mechanic. pad_pillar is pinned on and pct is
        /// pinned to <see cref="MemoryPct"/> scaled by <paramref name="level"/>, both over the string and the shard row.
        /// </summary>
        public HazardArgs MemoryRoundArgs(int length, int level)
        {
            var asWritten = HazardArgs.Parse(P3Memory);
            var index = length - MemoryOpeningLength + 1;

            var pulseOn = HazardRamp.At(asWritten.GetDouble("pulse_on", 1.0, 0.05, 30.0), OptionalDouble(asWritten, "pulse_on_end"), index, MemoryRounds);
            var pulseOff = HazardRamp.At(asWritten.GetDouble("pulse_off", 0.5, 0.05, 30.0), OptionalDouble(asWritten, "pulse_off_end"), index, MemoryRounds);

            var rest = HazardArgs.FromTokens(Tokens(P3Memory).Where(t => !MemoryOwnKeys.Contains(KeyOf(t), StringComparer.OrdinalIgnoreCase)
                    && !KeyOf(t).EndsWith("_end", StringComparison.OrdinalIgnoreCase)));

            var args = rest
                .With("len", length.ToString(CultureInfo.InvariantCulture))
                .With("max_len", length.ToString(CultureInfo.InvariantCulture))
                .With("pulse_on", Num(pulseOn))
                .With("pulse_off", Num(pulseOff))
                .With("beams_per_pulse", P3BeamsPerPulse)
                .With("beam_corners", P3BeamCorners)
                .With("pad_pillar", P3PadPillar);

            return WithPct(args, level, MemoryPct);
        }

        private static double? OptionalDouble(HazardArgs args, string key)
            => args.Has(key) ? args.GetDouble(key, 0.0, 0.0, 30.0) : (double?)null;

        private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private static (string Pattern, HazardArgs Args) SplitPattern(string raw, string fallbackPattern)
        {
            var pattern = HazardArgs.Parse(raw).GetString("pattern") ?? fallbackPattern;
            var args = HazardArgs.FromTokens(Tokens(raw).Where(t => !string.Equals(KeyOf(t), "pattern", StringComparison.OrdinalIgnoreCase)));
            return (pattern, args);
        }

        private static IEnumerable<string> Tokens(string raw)
            => (raw ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        private static string KeyOf(string token)
        {
            var eq = token.IndexOf('=');
            return eq <= 0 ? token : token.Substring(0, eq);
        }
    }
}
