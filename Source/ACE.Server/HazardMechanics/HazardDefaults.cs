using System.Collections.Generic;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Every shipped default of the hazard mechanics library, in one place. PropertyManager's defaults for the
    /// hazard_mech_* settings point at these constants (so the code default and the settings default cannot
    /// drift), HazardTunables falls back to them, and the mechanics read the per-mechanic numbers out of the
    /// merged <see cref="HazardArgs"/> with these as the documented fallback.
    ///
    /// NOTHING HERE IS READ FROM THE DATABASE. Pure constants, so tests can use them without PropertyManager.
    /// </summary>
    public static class HazardDefaults
    {
        // ---- the framework settings ------------------------------------------------------------------------

        /// <summary>hazard_mech_enabled. New server settings default ON (repo rule); this is the kill switch.</summary>
        public const bool Enabled = true;

        /// <summary>hazard_mech_tick_ms: the driver cadence. One self-requeuing ActionChain per landblock+instance.</summary>
        public const long TickMs = 100;

        /// <summary>Clamp range for hazard_mech_tick_ms.</summary>
        public const long MinTickMs = 50;
        public const long MaxTickMs = 1000;

        // ---- the per-mechanic settings strings (key=value, laid UNDER command/API args) --------------------
        //
        // Each string is OWNED by its mechanic package: the keys and numbers below are the plan's defaults and
        // the mechanic reads each one through HazardArgs with its own documented default and clamp. A key a
        // mechanic does not read is ignored, never an error.

        /// <summary>
        /// hazard_mech_wisps (WispSquadMechanic): count, scale, cycle timings, pillar radius, damage, escape speed.
        /// pillar_radius is the DAMAGE radius (the 2D hit test, plus the player's), NOT the visual size: the pillar's look is fixed
        /// by its effect script (<see cref="PillarScript"/>), so a bigger pillar_radius hurts outside the flames and a smaller one
        /// leaves flames that do not hurt. escape_speed (m/s) is how fast a player is assumed to run out of a pillar: Validate
        /// refuses a hold under 0.5 s + (pillar_radius + player_radius) / escape_speed.
        /// </summary>
        public const string Wisps = "n=8 scale=1.0 move=2.5 hold=1.5 burst=1.5 rest=1.0 pillar_radius=2.0 pct=0.60 cycles=8 escape_speed=3.0";

        /// <summary>
        /// hazard_mech_balls (RollingBallMechanic): speed m/s, lead s (telegraph before motion; raised to the fair-lead
        /// floor), gap m (row/cross), waves, wave_gap s, window s (random launches), pct. The ring/random ball
        /// count is NOT here: its default differs per pattern (<see cref="BallsRingCount"/>, <see cref="BallsRandomCount"/>),
        /// and a "count" in this string would override both.
        /// </summary>
        public const string Balls = "speed=3 lead=2 gap=6 waves=4 wave_gap=3 window=20 escape_speed=3.0 pct=0.60";

        /// <summary>hazard_mech_beams (BeamColumnMechanic): count, lifetime, placement; damage 0 = visual only.</summary>
        public const string Beams = "n=6 seconds=4 where=floor pct=0";

        /// <summary>hazard_mech_memory (MemoryMechanic): sequence lengths, pulses, answer window, pad size, gap and ring spacing. Deals no damage unless pad_pillar=1, whose fire pillars hit for pct (the context default 0.60 when absent; this default string carries none). Clamps: MemorySettings.</summary>
        public const string Memory = "len=4 max_len=8 beams_per_pulse=6 pulse_on=1.0 pulse_off=0.5 answer_timeout=20 pad_radius=1.0 pad_gap=6 pad_spacing=0.6";

        // ---- framework-level arg keys every mechanic shares ------------------------------------------------

        /// <summary>Arg key: damage per hit as a fraction of the player's MaxHealth.</summary>
        public const string PctKey = "pct";

        /// <summary>Arg key: damage element, one of fire|acid|cold|electric|bludgeon. Absent: fire.</summary>
        public const string DamageTypeKey = "damage_type";

        /// <summary>Arg key: the verb in the default hit line "The {Name} {verb} you for {n} damage!". Absent: <see cref="HitVerb"/>.</summary>
        public const string HitVerbKey = "hit_verb";

        /// <summary>The hit line's verb when <see cref="HitVerbKey"/> is absent.</summary>
        public const string HitVerb = "sears";

        /// <summary>Arg key: the player's own radius added to a hazard's radius for the 2D hit test.</summary>
        public const string PlayerRadiusKey = "player_radius";

        /// <summary>
        /// FAIRNESS ASSUMPTION, arg <see cref="EscapeSpeedKey"/> ("escape_speed"), SHARED by the balls and the wisps: a
        /// player flees at this many metres per second. Deliberately BELOW the base run speed, so a player who is only
        /// walking still gets a fair chance. It sizes the balls' lead floors (BallRules.FairLead, BallRules.FanLead) and
        /// the wisps' hold floor (WispSettings.EscapeHoldFloor); it is an assumption about players, not a measured server
        /// value, and the owner can test others with escape_speed=5. Default 3.0, clamp 1..12.
        /// </summary>
        public const string EscapeSpeedKey = "escape_speed";

        public const double EscapeSpeed = 3.0;
        public const double MinEscapeSpeed = 1.0;
        public const double MaxEscapeSpeed = 12.0;

        /// <summary>Owner ruling: ceil(0.60 x MaxHealth) per hit - "2 hits in a row without heal is death".</summary>
        public const double DamagePct = 0.60;

        public const double MinPct = 0.0;
        public const double MaxPct = 5.0;

        /// <summary>The player's radius for the 2D distance test (metres).</summary>
        public const double PlayerRadius = 0.5;

        public const double MinPlayerRadius = 0.0;
        public const double MaxPlayerRadius = 3.0;

        /// <summary>
        /// Arg key: how far effect hosts (pillars, beams, show markers) are kept inside the region edge so a wall
        /// does not cut their emitters off (owner feedback from the 2026-10-06 movement gate).
        /// </summary>
        public const string EdgeInsetKey = "edge_inset";

        public const double EdgeInset = 2.0;
        public const double MinEdgeInset = 0.0;
        public const double MaxEdgeInset = 10.0;

        // ---- motion ------------------------------------------------------------------------------------------

        /// <summary>Terrain regions split a leg into sub-legs of about this length (metres) so a hazard follows the ground.</summary>
        public const double SubLegMetres = 4.0;

        /// <summary>Hard cap on sub-legs per leg (a 192 m leg at 4 m is 48).</summary>
        public const int MaxSubLegs = 64;

        /// <summary>
        /// Arg key: a ball's hover, metres above GroundZ (HazardFollow.Ground's GroundOffset). Read by every ball
        /// user (the balls mechanic, /hazard move) through HazardArgs with <see cref="BallHover"/> and the clamp
        /// below.
        /// </summary>
        public const string BallHoverKey = "ball_z";

        /// <summary>
        /// BALLS HOVER (owner ruling 2026-10-07, after gate 2's ball stopped at a block edge): a ball floats so its
        /// collision sphere clears every solid top. The Rolling Death sphere radius at scale 2.25 is 1.80 m
        /// (0.80 x 2.25, ACE.Content.Tools cells --modelbounds=0x02000DDD), so the hover must exceed 1.80 with
        /// margin. Owner retest 2026-10-17 on the Pinnacle (ground plane -47.75, block tops -48.00): 1.8 (sphere
        /// bottom exactly on the ground plane) still collided with the block; 2.0 cleared it, and the owner set
        /// 2.0. Balls ALWAYS ride over the platforms (owner ruling 2026-10-17), so 2.0 is also the floor of the
        /// clamp: ball_z cannot drop a ball into the pit or onto a block. A larger ball needs a larger ball_z.
        /// </summary>
        public const double BallHover = 2.0;

        public const double MinBallHover = 2.0;
        public const double MaxBallHover = 10.0;

        // ---- registry / driver -------------------------------------------------------------------------------

        /// <summary>
        /// A run whose driver has not ticked for this long is reaped. A landblock unload always kills the
        /// self-requeuing driver chain (the queue is cleared and the copy is never ticked again);
        /// /reload-landblock clears the queue too but only kills the chain if its tick was queued at that instant.
        /// This is how the registry notices either.
        /// </summary>
        public const double ReapStaleSeconds = 5.0;

        /// <summary>Cadence of the global reap chain on the world thread (runs only while any run exists).</summary>
        public const double ReaperSeconds = 5.0;

        /// <summary>A driver not heard from for max(this, 3 x tick) seconds is replaced by the next Ensure.</summary>
        public const double DriverStaleFloorSeconds = 1.0;

        /// <summary>The most runtime controls (HazardRun.Control) that may wait undrained on one run.</summary>
        public const int MaxPendingControls = 64;

        // ---- content (Content/sql/weenies, block 1006950-1006999) -------------------------------------------

        /// <summary>Ember Wisp: red wisp (Setup 0x020009DB), non-Static, MOVING.</summary>
        public const uint EmberWispWcid = 1006950;

        /// <summary>Rolling Death (red): donor 21917 DIDs, PaletteTemplate 14, scale 2.25, non-Static, MOVING.</summary>
        public const uint RollingDeathRedWcid = 1006951;

        /// <summary>Hazard Effect Host: copy of 1006852 Ward Beacon; carries one VisualEffectScript and is destroyed to stop it.</summary>
        public const uint EffectHostWcid = 1006952;

        public const uint MnemonicWispRedWcid = 1006953;
        public const uint MnemonicWispBlueWcid = 1006954;
        public const uint MnemonicWispGreenWcid = 1006955;
        public const uint MnemonicWispPurpleWcid = 1006956;

        /// <summary>Caustic Wisp (acid): the existing moving wisp the fill mechanic uses for its acid pool.</summary>
        public const uint FillAcidWispWcid = 1006961;

        /// <summary>The fill mechanic's lava wisp is the Ember Wisp.</summary>
        public const uint FillLavaWispWcid = EmberWispWcid;

        /// <summary>Hazard Panel (acid green / lava orange / smoke grey): the flat 10 x 10 m panels of fill and fog.</summary>
        public const uint PanelAcidWcid = 1006967;
        public const uint PanelLavaWcid = 1006968;
        public const uint PanelSmokeWcid = 1006969;

        /// <summary>A panel's edge in metres at scale 1 (setup 0x02000E6E is a 10 x 10 m square).</summary>
        public const float PanelEdgeAtScale1 = 10f;

        // ---- model sizes (recon 2026-10-06, `cells --modelbounds`, collision sphere radius at scale 1) -----------

        /// <summary>Rolling Death setup 0x02000DDD: radius 0.80 at scale 1 (x the object's scale; 2.25 -> 1.8 m). Ball maths default.</summary>
        public const float RollingDeathRadiusAtScale1 = 0.80f;

        /// <summary>Red wisp setup 0x020009DB: radius 0.48 at scale 1 (height 1.83).</summary>
        public const float WispRadiusAtScale1 = 0.48f;

        /// <summary>
        /// Red wisp setup 0x020009DB: collision height 1.83 above its origin at scale 1 (origin at its feet; spheres r 0.48
        /// centred 0.48 and 1.35 up; `cells --modelbounds=0x020009DB`, 2026-10-18). x the wisp's scale.
        /// </summary>
        public const float WispHeightAtScale1 = 1.83f;

        /// <summary>How far under a region's ceiling the top of a wisp must stay (WispRules.Validate).</summary>
        public const float WispCeilingMargin = 0.1f;

        /// <summary>Rolling Death scale on its weenie (1006951): 0.80 x 2.25 = 1.80 m roll/hit radius. Overridable with args "ball_radius".</summary>
        public const float RollingDeathScale = 2.25f;

        public const float RollingDeathRadius = RollingDeathRadiusAtScale1 * RollingDeathScale;

        // ---- balls (HazardBallRules / RollingBallMechanic) -----------------------------------------------------

        public const double BallsSpeed = 3.0;
        public const double BallsMinSpeed = 0.5;

        /// <summary>Upper speed clamp: at the default 100 ms tick a ball moves 1.2 m per tick, well inside its 2.3 m hit reach, so it cannot tunnel through a player.</summary>
        public const double BallsMaxSpeed = 12.0;

        public const double BallsLead = 2.0;

        /// <summary>Floor 0.5 s: the first leg starts several driver ticks after the spawn (HazardContext defers a same-tick MoveLeg).</summary>
        public const double BallsMinLead = 0.5;

        public const double BallsMaxLead = 30.0;

        /// <summary>Row/cross gap: centre-to-centre distance between the two balls flanking the gap. Must be at least BallRules.MinGap.</summary>
        public const double BallsGap = 6.0;

        public const double BallsMinGapArg = 1.0;
        public const double BallsMaxGapArg = 60.0;

        /// <summary>args "gaps" upper clamp: openings per row or cross wall (row, cross). 1 is the classic single opening.</summary>
        public const int BallsMaxGaps = 6;

        /// <summary>args "no_overlap" (row, cross): the wall pitch is 2 x reach plus this many metres, so no two balls' reach discs overlap.</summary>
        public const double BallsNoOverlapMargin = 0.02;

        /// <summary>args "origin_clear" (fan, rings) upper clamp, metres.</summary>
        public const double BallsMaxOriginClear = 20.0;

        /// <summary>args "distinct_dirs": the largest in-flight cap a row (4 walls, 1 per wave) can run with and always find a free wall.</summary>
        public const int BallsDistinctRowMaxInFlight = 3;

        /// <summary>args "distinct_dirs": the largest in-flight cap a cross (2 north/south + 2 east/west walls, one of each per wave) can run with.</summary>
        public const int BallsDistinctCrossMaxInFlight = 2;

        public const int BallsRingCount = 8;
        public const int BallsRandomCount = 6;
        public const int BallsMinCount = 1;
        public const int BallsMaxCount = 64;

        /// <summary>fan: balls per volley (the boss head's W shape is 3), clamp 1..7.</summary>
        public const int BallsFanCount = 3;
        public const int BallsFanMaxCount = 7;

        /// <summary>fan: degrees across the whole volley, clamp 0..120.</summary>
        public const double BallsFanSpread = 30.0;
        public const double BallsFanMaxSpread = 120.0;

        public const int BallsWaves = 4;
        public const int BallsMaxWaves = 50;

        public const double BallsWaveGap = 3.0;
        public const double BallsMaxWaveGap = 120.0;

        /// <summary>wave_every (opt-in launch-to-launch cadence): clamp 0.5..120 s. Off unless given.</summary>
        public const double BallsMinWaveEvery = 0.5;
        public const double BallsMaxWaveEvery = 120.0;

        /// <summary>Random pattern: the launches of one wave are spread over this many seconds.</summary>
        public const double BallsWindow = 20.0;

        public const double BallsMinWindow = 2.0;
        public const double BallsMaxWindow = 300.0;


        /// <summary>Validate refuses a row/cross wave of more balls than this (a tiny ball_radius on a big region otherwise spawns thousands).</summary>
        public const int BallsMaxRoutes = 96;

        /// <summary>fan: metres between the pillars that telegraph the line of fire.</summary>
        public const double BallsFanMarkerSpacing = 6.0;

        /// <summary>Slack added to the no-escape gap: free width left between a flank ball and the player at the gap centre.</summary>
        public const double BallsGapMargin = 0.5;

        /// <summary>A ball lane shorter than this is dropped (a circle's rim tips); a lane is never zero-length.</summary>
        public const double BallsMinLane = 1.0;

        /// <summary>A ring player must find a lane (chord wide enough) no farther out than this fraction of the shortest ray.</summary>
        public const double BallsRingEscapeFraction = 0.6;

        /// <summary>Random pattern: concurrent blocked lane width may be at most this fraction of the narrowest side.</summary>
        public const double BallsRandomOccupancy = 0.75;

        /// <summary>args "occupancy" lower clamp (<see cref="BallsRandomOccupancy"/>).</summary>
        public const double BallsMinOccupancy = 0.25;

        /// <summary>args "occupancy" upper clamp: 2 lets the balls in flight block twice the narrowest side's span (balls overlap freely when they come from all sides).</summary>
        public const double BallsMaxOccupancy = 2.0;

        // ---- effect scripts -----------------------------------------------------------------------------------

        /// <summary>Fire pillar: endless emitters stacked z 0/5/10/15 (Setup 0x020015E1). Owner ruling.</summary>
        public const uint PillarScript = 0x33001028;

        /// <summary>Scattered beam, first choice (Setup 0x020006B6). Owner ruling.</summary>
        public const uint BeamScript = 0x33000791;

        /// <summary>
        /// Narrow red beam (Setup 0x020006BA; the red puzzle beam). Owner 2026-10-25: the 9-particle half of the family
        /// (0x33000796-0x3300079A) is the NARROW beam and the 5-particle half (0x33000791-0x33000795) the scattered, wide
        /// one. The Grael wisps' mark and ceiling beam use this one (GraelSettings mark_script / beam_script).
        /// </summary>
        public const uint BeamAltScript = 0x33000798;

        /// <summary>
        /// The wisps' destination mark (args mark_dest=1): a ground effect at each wisp's Pillar point from the glide's
        /// start to its burst. Deliberately NOT <see cref="PillarScript"/>, which is the burst itself: a mark that looked
        /// like the fire would read as damage. The scattered beam until the owner picks a look in game.
        /// </summary>
        public const uint WispDestMarkScript = BeamScript;

        /// <summary>
        /// The retail beam family (Setups 0x020006B3-0x020006BC: the memory beams, the puzzle lights, the World Event markers). Each one's
        /// mesh hangs BELOW its host origin (MemoryLayout.BeamOrientation; PuzzleBeamAim's default axis Down), so a host
        /// standing on the floor must be turned (MemoryLayout.BeamOrientation(true)) or its beam is drawn under the floor.
        /// </summary>
        public static readonly IReadOnlySet<uint> HangingBeamScripts = new HashSet<uint>
        {
            0x33000791, 0x33000792, 0x33000793, 0x33000794, 0x33000795, 0x33000796, 0x33000797, 0x33000798, 0x33000799, 0x3300079A,
        };

        /// <summary>True for a script in <see cref="HangingBeamScripts"/>: a floor host of it must be turned to rise.</summary>
        public static bool HangsBelowHost(uint scriptId) => HangingBeamScripts.Contains(scriptId);

        // ---- the memory game (Content/sql/weenies: the Hill Markers 1006800-1006803) -----------------------------

        /// <summary>
        /// Hill Marker weenies, one per pad colour (DefaultScale 0.35 in the world DB). The memory mechanic DEPENDS on
        /// the Hill Marker content unit (owner pvp-battlegrounds): a world without it is refused at Start by the
        /// missing-weenie check, and a rename or retirement of those wcids in that unit breaks the memory game.
        /// </summary>
        public const uint MemoryPadRedWcid = 1006800;
        public const uint MemoryPadBlueWcid = 1006801;
        public const uint MemoryPadGreenWcid = 1006802;
        public const uint MemoryPadPurpleWcid = 1006803;

        /// <summary>
        /// The memory game's playback beams, one script per colour, from the retail beam family Setups
        /// 0x020006B3-B6. CAVEAT: the hues are INFERRED from the effect catalog's flat-mean colours (red #1C0202,
        /// blue #070A10, green #071009, purple #1C0214 in Content/effects/effect_donor_catalog_v2.tsv), not seen in
        /// game. The owner verifies them live; swap a constant here if a beam shows the wrong colour.
        /// </summary>
        public const uint MemoryBeamRedScript = BeamScript;
        public const uint MemoryBeamBlueScript = 0x33000794;
        public const uint MemoryBeamGreenScript = 0x33000792;
        public const uint MemoryBeamPurpleScript = 0x33000793;

        // ---- the /hazard tools ---------------------------------------------------------------------------------

        /// <summary>/hazard show: how long the region markers stand.</summary>
        public const double ShowSeconds = 10.0;

        /// <summary>/hazard with no preset/region off a preset's landblock: an ad-hoc circle of this radius.</summary>
        public const float AdHocCircleRadius = 15.0f;

        /// <summary>Default in-region z band for ad-hoc regions: a player counts as "in" from this far below the ground...</summary>
        public const float BandBelow = 2.0f;

        /// <summary>...to this far above it.</summary>
        public const float BandAbove = 6.0f;
    }
}
