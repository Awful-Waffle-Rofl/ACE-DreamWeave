using System;
using System.Globalization;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Harbinger
{
    /// <summary>
    /// The Drift Harbinger's tunables as a PURE record (no PropertyManager; only PinnacleTunables.Harbinger() reads the live
    /// hazard-pct clamp, once, at the fight's creation). The pct fields are the AUTHORED pcts: the caller scales them to the
    /// Knot level (HazardLevelScale.Scaled) and passes the result to the builders. Rates are metres per second of acid surface.
    /// </summary>
    public sealed record HarbingerSettings
    {
        // ---- acid (phase 1 fill; the P5 final runs the same fill slower and lower) ----

        /// <summary>The acid fill's floor_z: the surface a run starts from and drains to (one value for the builder and the script).</summary>
        public const double AcidFloorZ = -35.95;

        /// <summary>
        /// The acid fill's panel_size. Any value in [6.12, 7.65) gives the 5x5 sheet (FillRules.Sheet: ceil(30.6 / panel_size)
        /// tiles a side, drawn edge to edge at 6.12 m). BUG round 1 (owner, live): 10.4 gave a 3x3 sheet whose four diagonal tile
        /// centres sat at |dx| + |dy| = 20.4, outside the octagon (20), so they were refused ("no cell") and about 49 m2 of floor
        /// per corner showed no acid (tiles=5/9).
        /// </summary>
        public const double AcidPanelSize = 6.5;

        public double AcidRate { get; init; } = 0.02;

        public double AcidRateCap { get; init; } = 0.16;

        /// <summary>The acid rate doubles every this many seconds, up to <see cref="AcidRateCap"/>.</summary>
        public double AcidEscalateSeconds { get; init; } = 60;

        /// <summary>The fill's ceiling (m, world z) in a phase run: under the -11.7 region ceiling.</summary>
        public double AcidMaxZ { get; init; } = -12.0;

        /// <summary>Multiplier laid over the caller's level scale: the acid hits at 0.75 of the level scale.</summary>
        public double AcidDamageScale { get; init; } = 0.75;

        /// <summary>
        /// The acid's damage curve and submerged tier (design 4.3: "the Matron's submerged damage tier"): the Matron's own
        /// values (MatronSettings.Defaults), so the two acids hit alike at the same scale. Below <see cref="AcidHeadHeight"/> the
        /// mild curve (damage + damage_per_m x depth, capped); at or past it the submerged tier (FillRules.FlatDamage).
        /// </summary>
        public double AcidDamage { get; init; } = Matron.MatronSettings.Defaults.Damage;

        public double AcidDamagePerM { get; init; } = Matron.MatronSettings.Defaults.DamagePerM;

        public double AcidDamageMax { get; init; } = Matron.MatronSettings.Defaults.DamageMax;

        public double AcidHeadHeight { get; init; } = Matron.MatronSettings.Defaults.HeadHeight;

        public double AcidSubmergedDamage { get; init; } = Matron.MatronSettings.Defaults.SubmergedDamage;

        public double AcidSubmergedPerM { get; init; } = Matron.MatronSettings.Defaults.SubmergedPerM;

        public double AcidSubmergedMax { get; init; } = Matron.MatronSettings.Defaults.SubmergedMax;

        /// <summary>The P5 final run's rate: no escalation.</summary>
        public double FinalAcidRate { get; init; } = 0.01;

        /// <summary>The P5 final run's ceiling: the acid only reaches the chamber's knee.</summary>
        public double FinalAcidMaxZ { get; init; } = -33.5;

        /// <summary>A crystal's drain of the acid surface (m/s, negative).</summary>
        public double CrystalDrainRate { get; init; } = -2.0;

        /// <summary>
        /// The four wall strips' mode while the acid runs (HarbingerLayout.WallStrip): Matron.MatronLayout.WallFxPanels,
        /// WallFxFog or WallFxBoth (the shipped default, as the Matron's). Per-copy override key wall_fx.
        /// </summary>
        public int WallFx { get; init; } = Matron.MatronLayout.WallFxDefault;

        /// <summary>The wall strips' acid fog pick, an index into Matron.MatronLayout.AcidFogs (0 the shipped one). Per-copy override key fog_acid.</summary>
        public int FogAcid { get; init; }

        // ---- authored pcts (damage per hit as a fraction of max health, before the level scale) ----

        public double WispPct { get; init; } = 0.30;

        /// <summary>Frost hail authored pct. Owner ruling 2026-11-19: x1.5 (was 0.05).</summary>
        public double HailPct { get; init; } = 0.075;

        /// <summary>Fire ball authored pct. Owner ruling 2026-11-19: x1.5 (was 0.25).</summary>
        public double FirePct { get; init; } = 0.375;

        // ---- frost hail ----

        public int HailMaxFalling { get; init; } = 24;

        /// <summary>The hail hit's run slow, a fraction (0.25 = a quarter of the run skill removed).</summary>
        public double HailRunSlow { get; init; } = 0.25;

        /// <summary>The hail hit's attack animation speed factor (0.7 = swings run at 70% speed). Owner ruling 2026-11-19: 0.8 -> 0.7.</summary>
        public double HailAttackFactor { get; init; } = 0.7;

        public double HailSlowSeconds { get; init; } = 4;

        // ---- the boss ----

        /// <summary>P5: every element's strength is multiplied by this.</summary>
        public double FinalScale { get; init; } = 0.5;

        public double SummonSeconds { get; init; } = 20;

        public int SummonCap { get; init; } = 2;

        public int FinalSummonCap { get; init; } = 4;

        public double CastSeconds { get; init; } = 8;

        // ---- the alcove (round 2 A5, A6, A7, B1) ----

        /// <summary>
        /// The health multiplier laid over the tier health of the four crystals (PinnacleScriptData.HealthMultipliers); the head
        /// has its own, <see cref="HeadHealthMultiplier"/> (round 4); guardians and summons stay 1.0. The live value is
        /// pinnacle_harbinger_health_multiplier (PinnacleTunables.Harbinger), per-copy override key health_multiplier. 2.0 doubles
        /// today's crystal: 1.5 -> 3.0 x T(L).
        /// </summary>
        public double HealthMultiplier { get; init; } = PinnacleTunables.DefaultHarbingerHealthMultiplier;

        /// <summary>
        /// The head's own health multiplier (round 4, owner ruling 2026-11-19: head HP 10x at every level, crystals unchanged).
        /// 20.0 over the head's tier health, which was 2.0 (3.0 -> 6.0 x T(L)): 60 x T(L), ten times the old 6.0 x T(L). The live
        /// value is pinnacle_harbinger_head_health_multiplier (PinnacleTunables.Harbinger), per-copy override key head_health_multiplier.
        /// </summary>
        public double HeadHealthMultiplier { get; init; } = PinnacleTunables.DefaultHarbingerHeadHealthMultiplier;

        /// <summary>
        /// The crystal's scripted cast interval (seconds) while the owner is inside its alcove: the head's own cadence
        /// (<see cref="CastSeconds"/>, design A6 "the same cadence as the head"). The first guardian's death halves it for the
        /// rest of that alcove (the ward link). Per-copy override key crystal_cast_seconds.
        /// </summary>
        public double CrystalCastSeconds { get; init; } = 8;

        /// <summary>
        /// The attunement buff's duration (seconds): breaking a crystal grants the owner the element's Legendary Ward
        /// (<see cref="HarbingerElementDef.AttuneSpell"/>) for this long, cast by the head. 0 grants nothing. Per-copy override key attune_seconds.
        /// </summary>
        public double AttuneSeconds { get; init; } = 120;

        // ---- the alcove signatures and the doorway pressure (round 2 B1, B2) ----
        //
        // One signature per alcove, started on the owner's arrival and stopped at the crystal's death, a resume and a hold
        // (HarbingerScript). Acid: Corrosion pools; Lightning: Twin conductors; Frost: Whiteout; Fire: Ember roll. Every
        // hazard region is the alcove's preset (HarbingerLayout.RegionPreset) or a pool's ad-hoc area
        // (HarbingerLayout.PoolArea). The geometry values that the reach proof rests on (the edge insets, the hail's drop_z)
        // are constants below, not tunables; HarbingerSignatureTests proves the reach at every override bound.

        /// <summary>
        /// The doorway pressure (design B1): this long after the owner's arrival the alcove's signature turns harder, once
        /// (HarbingerScript.Escalate). Per-copy override key pressure_seconds.
        /// </summary>
        public double PressureSeconds { get; init; } = 60;

        /// <summary>
        /// Round 4 (owner playtest 2026-11-19): after the owner's arrival in an alcove its hazards wait this long (s), so a player
        /// who cannot react yet is not hit: Twin conductors' wisps, Whiteout, Ember roll, the acid drips and the pools that
        /// guardian deaths leave. The harmless film shows at once. Measured on the fight clock, which ticks once a second, so it
        /// resolves to whole ticks. The doorway pressure still counts from the arrival. 0 turns the grace off. Per-copy override
        /// key alcove_grace_seconds.
        /// </summary>
        public double AlcoveGraceSeconds { get; init; } = 10;

        /// <summary>
        /// A Corrosion pool's square edge (m), HarbingerLayout.PoolArea. 2.25 is the smallest edge for which the pool slots
        /// cover the whole alcove floor (HarbingerLayout.MinPoolSize). Per-copy override key pool_size.
        /// </summary>
        public double PoolSize { get; init; } = HarbingerLayout.MinPoolSize;

        /// <summary>
        /// The most pools alive at once in the acid alcove. 6 x 2.25 m squares are about 30 m2, under half of the alcove's
        /// 68.6 m2 floor: the room fills up and position matters, but there is always floor to stand on. A drip onto a free
        /// slot under the cap starts a pool; at the cap every drip deepens one instead. Each pool is one fill run, so the cap
        /// also bounds the runs. Per-copy override key pool_cap.
        /// </summary>
        public int PoolCap { get; init; } = 6;

        /// <summary>
        /// Round 3 (owner playtest 2026-11-19): the crystal drips a small green acid ball from near the alcove ceiling this
        /// often (s) while the owner is in the acid alcove (it replaced round 2's weeping). Each drip lands on a pool slot the
        /// script draws: a new pool there, or the pool there one <see cref="PoolStep"/> deeper. Per-copy override key drip_seconds.
        /// Round 4 (owner playtest 2026-11-19): 6 -> 2, three times as often. The fight clock is polled once per
        /// PinnacleFightManager.TickSeconds (1 s) and the timer is re-armed from the tick it fired on, so a drip can fire at most
        /// once per tick, so the script carries the remainder and fires every drip the elapsed time owes (at most
        /// HarbingerScript.MaxDripsPerTick per tick): the average cadence is the interval itself.
        /// </summary>
        public double DripSeconds { get; init; } = 2;

        /// <summary>
        /// The drip interval (s) once the doorway pressure has begun. Round 4: 3.5 -> 1.2 (about three times as often): the drip clock
        /// carries the remainder between 1 s ticks, so the average really is 1.2 s. Per-copy override key drip_pressure_seconds.
        /// </summary>
        public double DripPressureSeconds { get; init; } = 1.2;

        /// <summary>
        /// A drip's own hit on a player under it, authored pct (scaled to the Knot level like Whiteout's): Whiteout's own 0.04
        /// (orchestrator call on review of 19d358426: the owner asked for drips "similar to what we do with frost"). Per-copy
        /// override key drip_pct.
        /// </summary>
        public double DripPct { get; init; } = 0.04;

        /// <summary>How much a drip onto a pool raises its surface (m). Per-copy override key pool_step.</summary>
        public double PoolStep { get; init; } = 0.15;

        /// <summary>
        /// The deepest a pool gets (m over the alcove floor, z 0): a fresh pool stands at <see cref="PoolSurfaceZ"/> 0.2, three
        /// drips take it to 0.6. At most <see cref="PoolMaxZ"/> less 0.05, the fill's own ceiling. Per-copy override key pool_max_depth.
        /// </summary>
        public double PoolMaxDepth { get; init; } = 0.6;

        /// <summary>
        /// A pool's hit per tick (2 s) is <see cref="PoolDamage"/> + <see cref="PoolDamagePerM"/> x its depth over the floor (a
        /// player standing in it is that deep), at the Knot level scale 1.0 (level 200); the run's damage_scale is the acid's
        /// level scale (HazardLevelScale.BenchmarkPct over its anchor, the same curve ScaledPct follows), because a fill's
        /// damage is flat, not a pct. 30 + 100 x 0.2 = 50, round 2's flat hit, for a fresh pool, rising to 90 at the 0.6 m
        /// cap. Per-copy override keys pool_damage, pool_damage_per_m.
        /// </summary>
        public double PoolDamage { get; init; } = 30;

        public double PoolDamagePerM { get; init; } = 100;

        /// <summary>
        /// Round 4 (owner playtest 2026-11-19): the acid alcove's film, a thin still layer of acid over the whole floor from the
        /// moment the signature starts (<see cref="FilmArgs"/>), is a visual layer: its flat hit per tick, at the Knot level's scale,
        /// is 0 by default (FillDirector skips a flat hit of 0 or less, so nobody is touched). Per-copy override key film_damage.
        /// </summary>
        public double FilmDamage { get; init; } = 0;

        /// <summary>
        /// Twin conductors: a guardian killed while its twin stands rises again this long (s) after, unless the twin is down
        /// by then; halved once the doorway pressure has begun (orchestrator ruling). Per-copy override key twin_revive_seconds.
        /// </summary>
        public double TwinReviveSeconds { get; init; } = 10;

        /// <summary>Whiteout's authored pct (scaled to the Knot level like the floor hail's). Per-copy override key whiteout_pct.</summary>
        public double WhiteoutPct { get; init; } = 0.04;

        /// <summary>Whiteout's hailstones in the air at once, before and after the pressure. Override keys whiteout_max_falling, whiteout_pressure_max_falling.</summary>
        public int WhiteoutMaxFalling { get; init; } = 3;

        public int WhiteoutPressureMaxFalling { get; init; } = 6;

        /// <summary>Whiteout's fall speed (m/s), before and after the pressure. Override keys whiteout_speed, whiteout_pressure_speed.</summary>
        public double WhiteoutSpeed { get; init; } = 10;

        public double WhiteoutPressureSpeed { get; init; } = 14;

        /// <summary>
        /// Whiteout's burst radius (m), the floor hail's own 1.2. Its floor is 1.1: with <see cref="WhiteoutEdgeInset"/> 0.3 the
        /// impacts reach 3.7 m out, and 3.7 + 1.1 + the 0.5 m player radius is the 5.3 m out to which the alcove region counts a
        /// player. Per-copy override key whiteout_burst_radius.
        /// </summary>
        public double WhiteoutBurstRadius { get; init; } = 1.2;

        /// <summary>
        /// Ember roll's hit (round 3, owner 2026-11-19: "more numerous and more rapid, but less damage, 3-10% scaling by
        /// level"), a fraction of max health on a LINEAR clamp of the Knot level, not the authored-ratio scale the other runs
        /// use: <see cref="EmberPctAt"/> is <see cref="EmberPctMin"/> at level 200 and under, <see cref="EmberPctMax"/> at 400
        /// and over. Per-copy override keys ember_pct_min, ember_pct_max.
        /// </summary>
        public double EmberPctMin { get; init; } = 0.03;

        public double EmberPctMax { get; init; } = 0.10;

        /// <summary>Ember roll's balls per wave, before and after the pressure. Override keys ember_count, ember_pressure_count.</summary>
        public int EmberCount { get; init; } = 2;

        public int EmberPressureCount { get; init; } = 3;

        /// <summary>Ember roll launches a wave this often (s), before and after the pressure. Override keys ember_every_seconds, ember_pressure_every_seconds.</summary>
        public double EmberEverySeconds { get; init; } = 3;

        public double EmberPressureEverySeconds { get; init; } = 2;

        /// <summary>Ember roll's ball speed (m/s), before and after the pressure. Override keys ember_speed, ember_pressure_speed.</summary>
        public double EmberSpeed { get; init; } = 4;

        public double EmberPressureSpeed { get; init; } = 5;

        /// <summary>The Knot levels of Ember roll's linear clamp: <see cref="EmberPctMin"/> at and under the first, <see cref="EmberPctMax"/> at and over the second.</summary>
        public const int EmberLevelLow = 200, EmberLevelHigh = 400;

        /// <summary>
        /// Ember roll's pct at Knot level <paramref name="level"/>: clamp(min + (max - min) x (level - 200) / 200, min, max).
        /// At the defaults 0.03 at level 200 and under, 0.065 at 300, 0.10 at 400 and over.
        /// </summary>
        public double EmberPctAt(int level)
        {
            var lo = Math.Min(EmberPctMin, EmberPctMax);
            var hi = Math.Max(EmberPctMin, EmberPctMax);
            var t = (level - EmberLevelLow) / (double)(EmberLevelHigh - EmberLevelLow);

            return Math.Clamp(EmberPctMin + (EmberPctMax - EmberPctMin) * t, lo, hi);
        }

        // ---- the lightning alcove's conductor wisps (round 3, owner 2026-11-19: "a couple of lightning pillar wisps") ----

        /// <summary>The conductor wisps' authored pct (scaled to the Knot level like the floor wisps'). Per-copy override key conductor_wisp_pct.</summary>
        public double ConductorWispPct { get; init; } = 0.20;

        /// <summary>The conductor wisps in the lightning alcove, before and after the pressure. Override keys conductor_wisps, conductor_pressure_wisps.</summary>
        public int ConductorWisps { get; init; } = 2;

        public int ConductorPressureWisps { get; init; } = 3;

        /// <summary>The conductor wisps' pillar_radius (m): small, the alcove is tight.</summary>
        public const double ConductorPillarRadius = 1.0;

        /// <summary>The conductor wisps' edge_inset (m): their pillar points stay within 3.0 m of the alcove centre (the preset circle is 4.0).</summary>
        public const double ConductorEdgeInset = 1.0;

        /// <summary>The conductor wisps' rest between cycles (s), before and after the pressure.</summary>
        public const double ConductorRest = 2.0, ConductorPressureRest = 1.0;

        /// <summary>The conductor wisps' cycles per run; a Completed run is restarted while the signature is on.</summary>
        public const int ConductorCycles = 40;

        // ---- the lightning ceiling beam (round 3, owner 2026-11-19: "the laser beam indicator ... similar to Grael") ----

        /// <summary>Seconds before each lightning burst the ceiling beam falls onto the pillar point: Grael's 1.5 (GraelSettings DefaultP2Wisps).</summary>
        public const double LightningCeilingBeam = 1.5;

        /// <summary>
        /// The lightning ceiling beam's host height over the pillar's ground (wisps beam_height): the beam mesh hangs 5 m under its
        /// host (MemoryLayout.BeamOrientation), and a host under the chamber's -11.7 ceiling would end it 19 m over the -36 floor
        /// (the alcove's 9.0 ceiling, 3.7 m over its floor). 5.4 m is Grael's own geometry: his room's ceiling is 5.7 m over its
        /// floor (HazardRegionPresets.Pinnacle), the host hangs 0.3 m under it, so the beam's foot is 0.4 m over the floor.
        /// </summary>
        public const double LightningBeamHeight = 5.4;

        /// <summary>
        /// Ember roll's ball radius (m): 0.5, under a third of the floor run's (no ball_radius given, the balls default 1.8;
        /// owner: the alcove is tight). Its ceiling is 0.5: with <see cref="EmberEdgeInset"/> 0.1 a ball's centre stays within
        /// 3.9 m of the alcove centre, so its body (3.9 + 0.5) stays 0.1 m clear of the 4.5 m walls. Per-copy override key ember_ball_radius.
        /// </summary>
        public double EmberBallRadius { get; init; } = 0.5;

        /// <summary>Whiteout's edge_inset (m): the impacts fall within 3.7 m of the alcove centre (the preset circle is 4.0).</summary>
        public const double WhiteoutEdgeInset = 0.3;

        /// <summary>Whiteout's drop_z: 1.0 m under the alcove's measured ceiling face (HazardRegionPresets.HarbingerAlcoveCeilingZ, 9.0), so a 0.4 m hailstone's top (8.4) is inside the cells.</summary>
        public const double WhiteoutDropZ = 8.0;

        /// <summary>Whiteout's run length (s); a Completed run is restarted while the signature is on.</summary>
        public const double WhiteoutDuration = 300;

        /// <summary>Ember roll's edge_inset (m): its lanes run inside a 3.9 m circle (the preset circle is 4.0).</summary>
        public const double EmberEdgeInset = 0.1;

        /// <summary>Ember roll's waves per run; a Completed run is restarted while the signature is on.</summary>
        public const int EmberWaves = 30;

        // ---- the acid drips (round 3) ----

        /// <summary>A drip falls from 1.0 m under the alcove ceiling (9.0), as Whiteout's hail.</summary>
        public const double DripDropZ = WhiteoutDropZ;

        /// <summary>The drip ball's radius (m): the 1008281 Acid Drip weenie at DefaultScale 0.375 (the Rolling Death ball is 0.8 m per unit scale).</summary>
        public const double DripBallRadius = 0.3;

        /// <summary>The drip's burst radius (m): a hit on the player under it, not the room.</summary>
        public const double DripBurstRadius = 0.8;

        public const double DripSpeed = 8;

        /// <summary>The drip's hang before it falls (s). lead + fall (0.6 + 0.9625) clears RainRules' fair warning (0.933 s).</summary>
        public const double DripLead = 0.6;

        /// <summary>
        /// The drip run's launch window (s), the rain minimum. A ONE-drop run: max_falling 1 and a ball's lifetime (lead + fall,
        /// 1.56 s) longer than this window, so the launch at the run's start is the only one, and the run ends Completed at
        /// that drop's impact (RainDirector: past the window with nothing in the air). That end is the script's impact event.
        /// RESIDUAL: a drop the director could not place inside the window (no cell at drop height, or the marker or ball
        /// would not spawn) still ends the run Completed with nothing launched, so the script starts or raises a pool with no
        /// ball seen. HarbingerAlcoveRegionTests pins a cell at <see cref="DripDropZ"/> over every drip area of every alcove,
        /// so the cell case cannot arise on this map.
        /// </summary>
        public const double DripDuration = 1;

        /// <summary>The drip run's ad-hoc area (m square) round its slot: the drop lands within 0.25 m of the slot centre.</summary>
        public const double DripAreaSize = 0.5;

        /// <summary>The chance a drip is aimed at an existing pool that can still deepen, when it could also start a new one.</summary>
        public const double DripDeepenShare = 0.5;

        /// <summary>How fast a struck pool's surface rises its step (m/s): a 0.15 m step in 0.5 s.</summary>
        public const double PoolRiseRate = 0.3;

        /// <summary>A pool's acid surface (m, world z): 0.2 m over the alcove floor (z 0), so a player standing in it is under it.</summary>
        public const double PoolSurfaceZ = 0.2;

        /// <summary>
        /// The film's surface (m, world z): above its own floor_z (<see cref="FilmFloorZ"/>) and 0.12 m BELOW a fresh pool's surface
        /// (<see cref="PoolSurfaceZ"/>), so a pool's panel never shares a plane with the film's.
        /// </summary>
        public const double FilmSurfaceZ = 0.08;

        /// <summary>The film fill's floor_z (the fill default over the alcove ground, z 0).</summary>
        public const double FilmFloorZ = 0.05;

        /// <summary>A pool's fill max_z: FillRules.Validate needs it at least 0.5 m over floor_z (0.05, the default over the ground 0).</summary>
        public const double PoolMaxZ = 0.7;

        /// <summary>
        /// The reach (m) from the head's centre inside which a player counts as in melee: the 15.8 m probe from the head's origin,
        /// less the head's radius (HarbingerLayout.HeadRadius is ALREADY scaled for HeadScale; never multiply it again), less 0.5.
        /// </summary>
        public double MeleeReach { get; init; } = 15.8 - HarbingerLayout.HeadRadius - 0.5;

        /// <summary>The live hazard-pct clamp, read ONCE when the fight is created so the script stays pure (as MatronSettings).</summary>
        public double HazardPctMin { get; init; } = PinnacleTunables.DefaultHazardPctMin;

        public double HazardPctMax { get; init; } = PinnacleTunables.DefaultHazardPctMax;

        public static readonly HarbingerSettings Defaults = new HarbingerSettings();

        /// <summary>The frost hail's hit slow.</summary>
        public PinnacleHitSlow HailSlow => new PinnacleHitSlow(HailRunSlow, HailAttackFactor, HailSlowSeconds);

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private double Strength(double v, bool final) => final ? v * FinalScale : v;

        private static string U(uint v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The element identity every non-acid run carries, read from its <see cref="HarbingerElementDef"/> (the single source,
        /// as WorldEventHazards.CommonArgs): damage_type (WorldEventHazards.DamageWord) and hit_verb. Without them the hazard
        /// context reads Fire (HazardContext.DamageType).
        /// </summary>
        private static string ElementKeys(HarbingerElementDef def)
            => "damage_type=" + WorldEvents.WorldEventHazards.DamageWord(def.DamageType) + " hit_verb=" + def.Visuals.HitVerb;

        /// <summary>
        /// The acid fill's args for a run starting at <paramref name="startZ"/> rising at <paramref name="rate"/> m/s.
        /// <paramref name="damageScale"/> is the Knot level scale (x <see cref="AcidDamageScale"/>, and x <see cref="FinalScale"/> in the final).
        /// <see cref="AcidPanelSize"/> doubled panels tile the chamber's rect (plus the 0.3 m sheet margin) as a 5x5 sheet of 6.12 m
        /// tiles, 50 surface panels: the four extreme corner tiles fall outside the room and are refused, the other 21 (42 panels)
        /// cover the whole octagon floor but a 1.3 m2 sliver per sloped corner (HarbingerFillSpawnTests). The damage keys are the
        /// Matron's curve and submerged tier (<see cref="AcidDamage"/> on).
        /// </summary>
        public HazardArgs AcidArgs(double startZ, double rate, double damageScale, bool final)
            => HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "element=acid panel_size={12} doubled=1 opacity=0.5 floor_z={4} max_z={0} tick=2 start_z={1} rate={2} damage_scale={3}"
                + " damage={5} damage_per_m={6} damage_max={7} head_height={8} submerged_damage={9} submerged_per_m={10} submerged_max={11}",
                F(final ? FinalAcidMaxZ : AcidMaxZ), F(startZ), F(rate), F(Strength(damageScale * AcidDamageScale, final)), F(AcidFloorZ),
                F(AcidDamage), F(AcidDamagePerM), F(AcidDamageMax), F(AcidHeadHeight), F(AcidSubmergedDamage), F(AcidSubmergedPerM), F(AcidSubmergedMax),
                F(AcidPanelSize)));

        /// <summary>
        /// The lightning wisp squad's args (pattern mixed). cycles=4: the default 8 cycles x 13 steps is 884 s, over the 600 s limit.
        /// <paramref name="pct"/> is the level-scaled <see cref="WispPct"/>; the final run halves it. Electric, with the Storm Wisp
        /// and the lightning pillar and marker scripts of the element def. Round 3: the ceiling beam telegraph
        /// (<see cref="LightningBeamKeys"/>) in the phase run and the final alike.
        /// Owner ruling 2026-11-19: faster cycles, more formations per minute (move 2.5 -> 2.0, rest 2.5 -> 1.0; a cycle is 6.5 s, was 8.5 s).
        /// The final run carries 6 wisps (was 4); the phase run stays at 8 because the chamber clips more to the same count.
        /// </summary>
        public HazardArgs LightningArgs(double pct, bool final)
        {
            var def = HarbingerData.Element(HarbingerElement.Lightning);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "pattern=mixed mixed_pool=row,column,diagonal,ring,sweep n={0} move=2.0 hold=2.0 burst=1.5 rest=1.0 pillar_radius={1} cycles=4 pct={2} {3} wisp_wcid={4} script={5} mark_script={6} {7}",
                final ? 6 : 8, final ? "1.0" : "2.0", F(Strength(pct, final)),
                ElementKeys(def), U(def.Visuals.WispWcid), U(def.Visuals.PillarScript), U(def.Visuals.MarkerScript), LightningBeamKeys(def)));
        }

        /// <summary>
        /// The lightning ceiling beam keys (round 3): ceiling_beam <see cref="LightningCeilingBeam"/> s before each burst,
        /// beam_height <see cref="LightningBeamHeight"/> so the beam reaches the pillar in a tall room, and the look of the
        /// def's marker (<see cref="WorldEvents.WorldEventHazardVisuals.MarkerScript"/>: the narrow purple beam 0x33000797 for
        /// lightning, the same family as Grael's narrow red 0x33000798).
        /// </summary>
        private static string LightningBeamKeys(HarbingerElementDef def)
            => "ceiling_beam=" + F(LightningCeilingBeam) + " beam_height=" + F(LightningBeamHeight) + " beam_script=" + U(def.Visuals.MarkerScript);

        /// <summary>
        /// The conductor wisps' args (the lightning alcove, round 3, pattern scatter): <see cref="ConductorWisps"/> lightning
        /// pillar wisps (<see cref="ConductorPressureWisps"/> and a shorter rest once <paramref name="pressured"/>) with a
        /// <see cref="ConductorPillarRadius"/> pillar, the lightning ceiling beam (the alcove's ceiling is 9.0, so the beam
        /// height matters here too), Electric, with the Storm Wisp and the def's pillar and marker scripts.
        /// <paramref name="pct"/> is the level-scaled <see cref="ConductorWispPct"/>.
        /// </summary>
        public HazardArgs ConductorArgs(double pct, bool pressured)
        {
            var def = HarbingerData.Element(HarbingerElement.Lightning);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "pattern=scatter n={0} move=2.0 hold=1.5 burst=1.5 rest={1} pillar_radius={2} edge_inset={3} cycles={4} pct={5} {6} wisp_wcid={7} script={8} mark_script={9} {10}",
                pressured ? ConductorPressureWisps : ConductorWisps, F(pressured ? ConductorPressureRest : ConductorRest),
                F(ConductorPillarRadius), F(ConductorEdgeInset), ConductorCycles, F(pct),
                ElementKeys(def), U(def.Visuals.WispWcid), U(def.Visuals.PillarScript), U(def.Visuals.MarkerScript), LightningBeamKeys(def)));
        }

        /// <summary>
        /// The frost hail's args: stones falling from <see cref="HarbingerLayout.HailDropZ"/>. <paramref name="pct"/> is the level-scaled
        /// <see cref="HailPct"/>. Cold, the small hailstone (not the def's frost ball), and the frost marker and burst scripts of the element def.
        /// </summary>
        public HazardArgs HailArgs(double pct, bool final)
        {
            var def = HarbingerData.Element(HarbingerElement.Frost);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "drop_z={0} ball_radius=0.4 burst_radius={1} speed=12 lead=0.6 max_falling={2} ball_wcid={3} pct={4} {5} marker_script={6} burst_script={7}",
                F(HarbingerLayout.HailDropZ), final ? "0.6" : "1.2", final ? Math.Max(1, HailMaxFalling / 2) : HailMaxFalling,
                HarbingerData.HailstoneWcid, F(Strength(pct, final)),
                ElementKeys(def), U(def.Visuals.MarkerScript), U(def.Visuals.BurstScript)));
        }

        /// <summary>
        /// Whiteout's args (the frost alcove, round 2 B2): the floor hail's small hailstone, burst and scripts falling from
        /// <see cref="WhiteoutDropZ"/> inside the alcove preset, <see cref="WhiteoutEdgeInset"/> from its edge. <paramref name="pct"/>
        /// is the level-scaled <see cref="WhiteoutPct"/>; <paramref name="pressured"/> (the doorway pressure) takes the denser,
        /// faster numbers. Cold, with the frost def's marker and burst scripts. The hit slow is the floor hail's
        /// (<see cref="HailSlow"/>), passed by the script in the run's options.
        /// </summary>
        public HazardArgs WhiteoutArgs(double pct, bool pressured)
        {
            var def = HarbingerData.Element(HarbingerElement.Frost);
            var speed = pressured ? WhiteoutPressureSpeed : WhiteoutSpeed;

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "drop_z={0} ball_radius={11} burst_radius={1} speed={2} lead={12} max_falling={3} edge_inset={4} target_share=0.5 target_spread=2 duration={5} ball_wcid={6} pct={7} {8} marker_script={9} burst_script={10}",
                F(WhiteoutDropZ), F(WhiteoutBurstRadius), F(speed),
                pressured ? WhiteoutPressureMaxFalling : WhiteoutMaxFalling, F(WhiteoutEdgeInset), F(WhiteoutDuration),
                HarbingerData.HailstoneWcid, F(pct), ElementKeys(def), U(def.Visuals.MarkerScript), U(def.Visuals.BurstScript),
                F(WhiteoutBallRadius), F(WhiteoutLead(WhiteoutBurstRadius, speed))));
        }

        /// <summary>Whiteout's hailstone radius (m): the floor hail's small stone.</summary>
        public const double WhiteoutBallRadius = 0.4;

        /// <summary>Whiteout's lead before a stone falls (s), at least: RainRules raises nothing at Validate, so the builder does.</summary>
        public const double WhiteoutMinLead = 0.6;

        /// <summary>
        /// Whiteout's lead: <see cref="WhiteoutMinLead"/>, raised (plus 0.05 s) to RainRules' fair warning, 0.5 s +
        /// (burst + player radius) / escape speed, less the fall from <see cref="WhiteoutDropZ"/> to the alcove floor (z 0) at
        /// <paramref name="speed"/>. RainRules.Validate refuses a shorter one, so a wide burst or a fast fall stays startable.
        /// </summary>
        public static double WhiteoutLead(double burstRadius, double speed)
        {
            var fall = RainRules.FallSeconds(WhiteoutDropZ, HazardRegionPresets.HarbingerAlcoveGroundZ, speed, WhiteoutBallRadius);
            var floor = 0.5 + (burstRadius + HazardDefaults.PlayerRadius) / HazardDefaults.EscapeSpeed;

            return Math.Min(RainRules.MaxLead, Math.Max(WhiteoutMinLead, Math.Round(floor - fall + 0.05, 3)));
        }

        /// <summary>
        /// Ember roll's args (the fire alcove, pattern random, round 2 B2, round 3): <see cref="EmberCount"/> small balls every
        /// <see cref="EmberEverySeconds"/> (a cadence wave, its launches spread over a window as long as the wave period, so the
        /// random rate check counts the real launch rate), on a lane drawn from all four
        /// sides (axis=any, round 4: balls roll in every direction; the doorway safe lane is gone, see HazardRegionPresets.HarbingerAlcoveNorth). <paramref name="pct"/> is
        /// <see cref="EmberPctAt"/> of the Knot level; <paramref name="pressured"/> takes the larger waves, faster cadence and speed.
        /// Fire, with the fire def's ball and marker script.
        /// </summary>
        public HazardArgs EmberArgs(double pct, bool pressured)
        {
            var def = HarbingerData.Element(HarbingerElement.Fire);
            var every = pressured ? EmberPressureEverySeconds : EmberEverySeconds;

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "count={11} waves={0} wave_every={1} window={2} axis={10} speed={3} ball_radius={4} edge_inset={5} pct={6} {7} ball_wcid={8} marker_script={9}",
                EmberWaves, F(every), F(Math.Max(EmberMinWindow, every)),
                F(pressured ? EmberPressureSpeed : EmberSpeed), F(EmberBallRadius), F(EmberEdgeInset), F(pct),
                ElementKeys(def), U(def.Visuals.BallWcid), U(def.Visuals.MarkerScript),
                BallPatterns.AxisAny, pressured ? EmberPressureCount : EmberCount));
        }

        /// <summary>The balls' smallest window (HazardDefaults.BallsMinWindow): a wave period under it still spreads its launches over 2 s.</summary>
        public const double EmberMinWindow = HazardDefaults.BallsMinWindow;

        /// <summary>
        /// One Corrosion pool's args (the acid alcove, round 2 B2, round 3): a still fill (rate 0) of one single-faced
        /// <see cref="PoolSize"/> panel (sheet_margin 0, so the panel is the area HarbingerLayout.PoolArea gives the run), no
        /// wisps, its surface at <paramref name="surfaceZ"/> (a fresh pool <see cref="PoolSurfaceZ"/>; a restart keeps the depth
        /// the drips gave it), hitting <see cref="PoolDamage"/> + <see cref="PoolDamagePerM"/> x depth every 2 s, capped at the
        /// <see cref="PoolMaxDepth"/> value, x <paramref name="damageScale"/> (the Knot level scale). quiet=1: no "is rising"
        /// line per pool. Acid, through the acid def's element keys.
        /// </summary>
        public HazardArgs PoolArgs(double damageScale, double surfaceZ = PoolSurfaceZ)
        {
            var def = HarbingerData.Element(HarbingerElement.Acid);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "element=acid panel_size={0} sheet_margin=0 doubled=0 wisps=0 opacity=0.6 floor_z=0.05 start_z={1} max_z={2} rate=0 tick=2 damage={3} damage_per_m={6} damage_max={7} damage_scale={4} quiet=1 {5}",
                F(PoolSize), F(surfaceZ), F(PoolMaxZ), F(PoolDamage), F(damageScale), ElementKeys(def), F(PoolDamagePerM), F(PoolDamageMax)));
        }

        /// <summary>
        /// The acid alcove's film args (round 4): ONE still fill (rate 0) of one single-faced panel the size of the whole
        /// alcove floor (HarbingerLayout.FilmArea, a 9 m square; sheet_margin 0 makes the panel that area), no wisps, its surface at
        /// <see cref="FilmSurfaceZ"/> over its floor <see cref="FilmFloorZ"/>, the same acid keys as a pool (<see cref="PoolArgs"/>).
        /// It hits a flat <see cref="FilmDamage"/> x <paramref name="damageScale"/> per tick (0 by default: no hit at all). quiet=1.
        /// </summary>
        public HazardArgs FilmArgs(double damageScale)
        {
            var def = HarbingerData.Element(HarbingerElement.Acid);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "element=acid panel_size={0} sheet_margin=0 doubled=0 wisps=0 opacity=0.5 floor_z={1} start_z={2} max_z={3} rate=0 tick=2 damage={4} damage_per_m=0 damage_max={4} damage_scale={5} quiet=1 {6}",
                F(HarbingerLayout.FilmSize), F(FilmFloorZ), F(FilmSurfaceZ), F(PoolMaxZ), F(FilmDamage), F(damageScale), ElementKeys(def)));
        }

        /// <summary>A pool's damage cap: its curve at <see cref="PoolMaxDepth"/>.</summary>
        public double PoolDamageMax => PoolDamage + PoolDamagePerM * PoolMaxDepth;

        /// <summary>
        /// One acid drip's args (the acid alcove, round 3): ONE small green ball (<see cref="HarbingerData.AcidDripWcid"/>)
        /// falling from <see cref="DripDropZ"/> onto the drip's ad-hoc area (HarbingerLayout.PoolArea at <see cref="DripAreaSize"/>),
        /// with the acid def's green marker telegraph and acid burst. max_falling 1 and the run window <see cref="DripDuration"/>
        /// make it one drop (see there); target_share 0 and edge_inset 0, so it lands where the script chose.
        /// <paramref name="pct"/> is the level-scaled <see cref="DripPct"/>.
        /// </summary>
        public HazardArgs DripArgs(double pct)
        {
            var def = HarbingerData.Element(HarbingerElement.Acid);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "drop_z={0} ball_radius={1} burst_radius={2} speed={3} lead={4} max_falling=1 duration={5} edge_inset=0 target_share=0 ball_wcid={6} pct={7} {8} marker_script={9} burst_script={10}",
                F(DripDropZ), F(DripBallRadius), F(DripBurstRadius), F(DripSpeed), F(DripLead), F(DripDuration),
                HarbingerData.AcidDripWcid, F(pct), ElementKeys(def), U(def.Visuals.MarkerScript), U(def.Visuals.BurstScript)));
        }

        /// <summary>
        /// The fire balls' args. wave_every=6: max_in_flight is refused without it. <paramref name="pct"/> is the level-scaled
        /// <see cref="FirePct"/>. Fire, with the fire ball and marker script of the element def.
        /// </summary>
        public HazardArgs FireArgs(double pct, bool final)
        {
            var def = HarbingerData.Element(HarbingerElement.Fire);

            return HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "waves=3 wave_every=6 max_in_flight=1 speed={0} pct={1} {2} ball_wcid={3} marker_script={4}",
                final ? 3 : 4, F(Strength(pct, final)),
                ElementKeys(def), U(def.Visuals.BallWcid), U(def.Visuals.MarkerScript)));
        }
    }
}
