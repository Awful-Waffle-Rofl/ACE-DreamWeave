using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// World Events Hold H5: the values the spawner and the wave planner read - knight placement, the wave mix, the corrupted
    /// assault, the finale boss and the spawn ring - resolved into one immutable snapshot that rides on
    /// <see cref="WorldEventHoldTuning.Spawner"/>. Kept apart from the per-hit fields of WorldEventHoldTuning so that slices
    /// extending that record (H4's combat and heal values) and this one never touch the same lines.
    ///
    /// Each value is the resolved setting (a set property, else the goals.json value, else the row's code default); a row that
    /// resolves to "no preference" takes the fallback named on its member, which is the goals.json def's own default.
    /// THREADING: immutable, so safe from any thread.
    /// </summary>
    public sealed record WorldEventHoldSpawnerTuning
    {
        /// <summary>The snapshot with no goal and no property set.</summary>
        public static WorldEventHoldSpawnerTuning Defaults { get; } = From(WorldEventHoldSettingsResolver.Resolve(null, _ => null), null);

        // ---- knights ----

        /// <summary>knight_count, or null (protect.count / countByParticipants decide).</summary>
        public int? KnightCount { get; init; }

        /// <summary>knight_max_health_by_wcid parsed (WorldEventHoldRules.TryParseKnightHealthByWcid); empty when not set or malformed.</summary>
        public IReadOnlyDictionary<uint, int> KnightHealthByWcid { get; init; } = new Dictionary<uint, int>();

        /// <summary>shrine_count: only the first N knights get a shrine; null = every knight.</summary>
        public int? ShrineCount { get; init; }

        /// <summary>shrine_use_radius, or null for protect.shrine.useRadius.</summary>
        public double? ShrineUseRadius { get; init; }

        // ---- waves ----

        /// <summary>The goal has a protect.waveMix block: every wave but the finale is a mixed wave.</summary>
        public bool HasWaveMix { get; init; }

        /// <summary>seek_share (fallback protect.seekShare).</summary>
        public double SeekShare { get; init; } = ProtectDef.DefaultSeekShare;

        private double[] shares = new double[6];
        private int[] minimums = new int[6];
        private int[] maximums = { -1, -1, -1, -1, -1, -1 };

        /// <summary>The waveMix shares in <see cref="HoldRole"/> order (Seeker, Breach, Runner, Ranged, Hunter, Corrupted). A copy.</summary>
        public double[] Shares => (double[])shares.Clone();

        /// <summary>The per-role minimums in <see cref="HoldRole"/> order (0 = none). A copy.</summary>
        public int[] Minimums => (int[])minimums.Clone();

        /// <summary>The per-role maximums in <see cref="HoldRole"/> order (-1 = none). A copy.</summary>
        public int[] Maximums => (int[])maximums.Clone();

        /// <summary>corrupted_assault_every_waves (0 = corrupted come by share instead).</summary>
        public int AssaultEveryWaves { get; init; }

        public int AssaultFromWave { get; init; } = 2;

        public int AssaultCount { get; init; } = 2;

        /// <summary>True when the corrupted come as periodic assaults.</summary>
        public bool AssaultMode => AssaultEveryWaves > 0;

        /// <summary>caster_share_of_ranged, or null (a plain draw from the ranged pool).</summary>
        public double? CasterShareOfRanged { get; init; }

        /// <summary>spawn_radius (fallback protect.spawnRadius; 0 = the source's own geometry).</summary>
        public double SpawnRadius { get; init; }

        /// <summary>spawn_clearance (fallback ProtectDef.MinSpawnClearance).</summary>
        public double SpawnClearance { get; init; } = ProtectDef.MinSpawnClearance;

        /// <summary>portal_count, or null for protect.portals.count.</summary>
        public int? PortalCount { get; init; }

        /// <summary>portal_radius, or null for protect.portals.radius.</summary>
        public double? PortalRadius { get; init; }

        // ---- the finale boss ----

        /// <summary>finale_health_mult (fallback protect.finaleBossHealthMult).</summary>
        public double FinaleHealthMult { get; init; } = ProtectDef.DefaultFinaleBossHealthMult;

        /// <summary>finale_health: a flat max health in place of the multiplier, or null.</summary>
        public int? FinaleFlatHealth { get; init; }

        /// <summary>finale_scale (fallback protect.finaleBossScale; 0 or less = as tall as the named bosses).</summary>
        public double FinaleScale { get; init; } = ProtectDef.DefaultFinaleBossScale;

        /// <summary>finale_level_offset: the boss is picked as if the players were this many levels higher.</summary>
        public int FinaleLevelOffset { get; init; }

        /// <summary>finale_boss_wcid: a forced boss weenie, or null for the family champion.</summary>
        public uint? FinaleBossWcid { get; init; }

        /// <summary>finale_add_count: adds that join the boss every <see cref="FinaleAddIntervalSeconds"/> (0 = none).</summary>
        public int FinaleAddCount { get; init; }

        public double FinaleAddIntervalSeconds { get; init; } = 30;

        /// <summary>finale_lead_seconds (fallback protect.finaleBossLeadSeconds): the boss arrives this long before the rest of the final wave.</summary>
        public double FinaleLeadSeconds { get; init; } = ProtectDef.DefaultFinaleBossLeadSeconds;

        /// <summary>
        /// finale_fallback_scale. Resolved but NOT applied: a fixed 3.08 made every shipped finale boss x3.08 (review finding
        /// F1), so when no height can be read the boss takes the World Events boss multiplier instead
        /// (WorldEventHoldFinaleBoss.DecideScale). Kept so the registered row still resolves.
        /// </summary>
        public double FinaleFallbackScale { get; init; } = 3.08;

        public double FinaleHeightScaleMin { get; init; } = 0.5;

        public double FinaleHeightScaleMax { get; init; } = 12;

        /// <summary>Builds the snapshot (pure: no property reads). <paramref name="goal"/> may be null.</summary>
        public static WorldEventHoldSpawnerTuning From(WorldEventHoldSettings settings, GoalDef goal)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            double N(string key, double fallback) => settings.Number(key) is double d && double.IsFinite(d) ? d : fallback;
            int I(string key, int fallback) => settings.Whole(key) is long l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : fallback;

            var protect = goal?.Protect;
            var mix = protect?.WaveMix ?? new WaveMixDef();
            var corrupted = protect?.EffectiveCorrupted ?? new CorruptedDef();

            var byWcidText = settings.Text(K.KnightMaxHealthByWcid);
            IReadOnlyDictionary<uint, int> byWcid = new Dictionary<uint, int>();

            if (!string.IsNullOrWhiteSpace(byWcidText) && WorldEventHoldRules.TryParseKnightHealthByWcid(byWcidText, out var parsed, out _))
                byWcid = parsed;

            var finaleWcid = settings.Whole(K.FinaleBossWcid);
            var casterShare = settings.Number(K.CasterShareOfRanged);
            var flatHealth = settings.Whole(K.FinaleHealth);

            var tuning = new WorldEventHoldSpawnerTuning
            {
                KnightCount = settings.Whole(K.KnightCount) is long kc && kc > 0 ? (int)kc : (int?)null,
                KnightHealthByWcid = byWcid,
                ShrineCount = settings.Whole(K.ShrineCount) is long sc && sc >= 0 ? (int)sc : (int?)null,
                ShrineUseRadius = settings.Number(K.ShrineUseRadius) is double ur && double.IsFinite(ur) && ur > 0 ? ur : (double?)null,

                HasWaveMix = protect?.WaveMix != null,
                SeekShare = N(K.SeekShare, protect?.SeekShare ?? ProtectDef.DefaultSeekShare),

                AssaultEveryWaves = Math.Max(0, I(K.CorruptedAssaultEveryWaves, corrupted.AssaultEveryWaves)),
                AssaultFromWave = Math.Max(1, I(K.CorruptedAssaultFromWave, corrupted.AssaultFromWave)),
                AssaultCount = Math.Clamp(I(K.CorruptedAssaultCount, corrupted.AssaultCount), 1, 5),
                CasterShareOfRanged = casterShare is double cs && double.IsFinite(cs) && cs >= 0 ? Math.Clamp(cs, 0, 1) : (double?)null,

                SpawnRadius = Math.Max(0, N(K.SpawnRadius, protect?.SpawnRadius ?? 0)),
                SpawnClearance = Math.Max(0, N(K.SpawnClearance, ProtectDef.MinSpawnClearance)),
                PortalCount = settings.Whole(K.PortalCount) is long pc && pc > 0 ? (int)pc : (int?)null,
                PortalRadius = settings.Number(K.PortalRadius) is double pr && double.IsFinite(pr) && pr > 0 ? pr : (double?)null,

                FinaleHealthMult = N(K.FinaleHealthMult, protect?.FinaleBossHealthMult ?? ProtectDef.DefaultFinaleBossHealthMult),
                FinaleFlatHealth = flatHealth is long fh && fh > 0 ? (int)Math.Min(fh, int.MaxValue) : (int?)null,
                FinaleScale = N(K.FinaleScale, protect?.FinaleBossScale ?? ProtectDef.DefaultFinaleBossScale),
                FinaleLevelOffset = I(K.FinaleLevelOffset, 0),
                FinaleBossWcid = finaleWcid is long fw && fw > 0 && fw <= uint.MaxValue ? (uint)fw : (uint?)null,
                FinaleAddCount = Math.Max(0, I(K.FinaleAddCount, 0)),
                FinaleAddIntervalSeconds = N(K.FinaleAddIntervalSeconds, 30),
                FinaleLeadSeconds = N(K.FinaleLeadSeconds, protect?.FinaleBossLeadSeconds ?? ProtectDef.DefaultFinaleBossLeadSeconds),
                FinaleFallbackScale = N(K.FinaleFallbackScale, 3.08),
                FinaleHeightScaleMin = N(K.FinaleHeightScaleMin, 0.5),
                FinaleHeightScaleMax = N(K.FinaleHeightScaleMax, 12),
            };

            tuning.shares = new[]
            {
                N(K.ShareSeekers, mix.Seekers),
                N(K.ShareBreach, mix.Breach),
                N(K.ShareRunners, mix.Runners),
                N(K.ShareRanged, mix.Ranged),
                N(K.ShareHunters, mix.Hunters),
                N(K.ShareCorrupted, mix.Corrupted),
            };

            tuning.minimums = new[]
            {
                0,
                Math.Max(0, I(K.MinBreach, mix.MinBreach)),
                0,
                Math.Max(0, I(K.MinRanged, mix.MinRanged)),
                0,
                Math.Max(0, I(K.MinCorrupted, mix.MinCorrupted)),
            };

            tuning.maximums = new[] { -1, -1, -1, -1, -1, Math.Max(0, I(K.MaxCorrupted, mix.MaxCorrupted)) };

            return tuning;
        }
    }
}
