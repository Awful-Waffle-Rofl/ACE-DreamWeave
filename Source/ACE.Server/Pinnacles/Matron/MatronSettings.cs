using System.Globalization;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Matron
{
    /// <summary>
    /// The Matron's tunables as a PURE record (no PropertyManager; plan key decision 7: code constants, plus the
    /// existing hazard-pct clamp carried in from PinnacleTunables). Rates are metres per second of acid surface.
    /// </summary>
    public sealed record MatronSettings(
        double FlowRate,
        double FinaleRate,
        double DrainRate,
        double LavaClearRate,
        double FlowEscalateSeconds,
        double FlowRateCap,
        double Damage,
        double DamagePerM,
        double DamageMax,
        double FinaleDamageScale,
        double MaxZ,
        double FillFloorZ)
    {
        /// <summary>The live hazard-pct clamp, read ONCE when the fight is created so the script stays pure (as GraelSettings).</summary>
        public double HazardPctMin { get; init; } = PinnacleTunables.DefaultHazardPctMin;

        public double HazardPctMax { get; init; } = PinnacleTunables.DefaultHazardPctMax;

        /// <summary>The wall strip's mode: <see cref="MatronLayout.WallFxPanels"/>, <see cref="MatronLayout.WallFxFog"/> or <see cref="MatronLayout.WallFxBoth"/>.</summary>
        public int WallFx { get; init; } = MatronLayout.WallFxDefault;

        /// <summary>The acid wall fog: an index into <see cref="MatronLayout.AcidFogs"/> (0 the shipped one).</summary>
        public int FogAcid { get; init; }

        /// <summary>The lava wall fog: an index into <see cref="MatronLayout.LavaFogs"/> (0 the shipped one).</summary>
        public int FogLava { get; init; }

        /// <summary>
        /// Seconds from a vent's door opening to its acid starting to rise (playtest round 6: the floor no longer turns to
        /// acid the instant the door opens; cut from 5 s to 2 s after that playtest). The flow's escalation counts from the
        /// delayed start. 0 starts it at once.
        /// </summary>
        public double FlowDelay { get; init; } = 2.0;

        /// <summary>
        /// The submerged tier (acid over the player's head): below <see cref="HeadHeight"/> the mild curve (10 + 10/m, max 250)
        /// applies unchanged; at or past it the flat hit is max(curve, <see cref="SubmergedDamage"/> + <see cref="SubmergedPerM"/> x
        /// (depth - head height), capped at <see cref="SubmergedMax"/>), then x the level scale. Calibration: 139 x 3.0 (level 400) x 0.24
        /// (Incantation of Acid Protection 0.32 x Legendary Acid Ward 0.75) = 100.08 per 2 s tick at 2 m; level 300 (x2.0) = 66.7 and
        /// level 200 (x1.0) = 33.4; each extra metre adds 25 x 3.0 x 0.24 = 18 at level 400.
        /// </summary>
        public double SubmergedDamage { get; init; } = 139;

        public double SubmergedPerM { get; init; } = 25;

        public double SubmergedMax { get; init; } = 500;

        /// <summary>The depth (m) at which the submerged tier starts: a character's height.</summary>
        public double HeadHeight { get; init; } = 2.0;

        /// <summary>Metres of acid over the floor at which the unused drain shows its beacon beam (owner: over the character's height, about 2 m).</summary>
        public double BeaconDepth { get; init; } = 2.0;

        /// <summary>
        /// A death and return never restarts the acid deeper than this (m over the floor). The returning player lands on the
        /// floor, so a restart over their head kills them before they can reach a platform (prod 2026-10-30: two returns at
        /// 13.4 m, each dead in two ticks). The default stays under <see cref="HeadHeight"/>, on the mild curve.
        /// </summary>
        public double ReturnMaxDepth { get; init; } = 1.0;

        /// <summary>The drain beacon beam: an index into <see cref="MatronLayout.DrainBeacons"/> (0 the shipped one).</summary>
        public int BeaconFx { get; init; }

        public static readonly MatronSettings Defaults = new MatronSettings(
            FlowRate: 0.03,
            FinaleRate: 0.12,
            DrainRate: -1.0,
            LavaClearRate: -2.0,
            FlowEscalateSeconds: 45,
            FlowRateCap: 0.24,
            Damage: 10,
            DamagePerM: 10,
            DamageMax: 250,
            FinaleDamageScale: 2.0,
            MaxZ: -44.5,
            FillFloorZ: -59.95);

        /// <summary>The damage scale for a Knot level: the benchmark ball pct over its 0.25 anchor (1.0 at level 200).</summary>
        public static double LevelScale(int level, double min, double max)
            => HazardLevelScale.BenchmarkPct(level, min, max) / HazardLevelScale.AnchorPct;

        /// <summary>
        /// The fill hazard's args for a run starting at <paramref name="startZ"/> rising at <paramref name="rate"/> m/s.
        /// 12 m doubled panels at half opacity tile the 50 x 60 m room as 30 tiles, 60 panels (the cap is 64).
        /// </summary>
        public HazardArgs FillArgs(double startZ, double rate, double damageScale)
            => HazardArgs.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "element=acid panel_size=12 doubled=1 opacity=0.5 floor_z={0} max_z={1} tick=2 damage={2} damage_per_m={3} damage_max={4} wisps=8 start_z={5} rate={6} damage_scale={7} head_height={8} submerged_damage={9} submerged_per_m={10} submerged_max={11}",
                FillFloorZ, MaxZ, Damage, DamagePerM, DamageMax, startZ, rate, damageScale, HeadHeight, SubmergedDamage, SubmergedPerM, SubmergedMax));
    }
}
