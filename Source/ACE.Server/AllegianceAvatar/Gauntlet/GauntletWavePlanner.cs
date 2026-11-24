using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// One Gauntlet wave, planned and not yet spawned: the gem spec and the live Thread plan it was built from, the group the
/// wave scales with, the portals it streams through and every planned spawn in delay order. Immutable once built.
/// </summary>
internal sealed class GauntletWavePlan
{
    /// <summary>The wave number, 1-based.</summary>
    public int Wave { get; init; }

    /// <summary>L(n) (GauntletRules.LevelOf).</summary>
    public int Level { get; init; }

    public bool IsBoss { get; init; }

    /// <summary>The headcount the wave scales with, already clamped (GauntletRules.ClampHeadcount).</summary>
    public int Headcount { get; init; }

    /// <summary>The live Thread group for <see cref="Headcount"/> (ThreadDungeonManager.ReadGroupScaling).</summary>
    public GroupScaling Group { get; init; }

    /// <summary>The spec handed to BuildLivePlan. Built by the constructor only; never serialized (the codec refuses levels above 500).</summary>
    public DungeonGemSpec Spec { get; init; }

    public DungeonSpawnPlan Plan { get; init; }

    /// <summary>What BuildLivePlan measured and the limits it saw (after the Gauntlet curve adjustment).</summary>
    public ThreadDungeonSpawner.LivePlanBuildStats Stats { get; init; }

    /// <summary>The trash count the wave asks for (GauntletRules.TrashCount). The stream averages it.</summary>
    public int TrashTarget { get; init; }

    /// <summary>GauntletRules.TimeLimitSeconds for this wave.</summary>
    public int TimeLimitSeconds { get; init; }

    /// <summary>The stream config (GauntletRules.PortalConfig over the target and the portals used).</summary>
    public IPortalWaveConfig StreamConfig { get; init; }

    /// <summary>The portals the wave streams through (PickPortals' picks, trimmed to the stream's portal count). Empty on a refusal.</summary>
    public IReadOnlyList<DungeonSpawnPointDef> Portals { get; init; } = Array.Empty<DungeonSpawnPointDef>();

    /// <summary>Index into <see cref="Portals"/> of the one furthest from the centre (the boss portal), or -1 with no portals.</summary>
    public int BossPortal { get; init; } = -1;

    /// <summary>Every planned spawn, trash and boss (or its stand-in elites), in delay order. Each entry stands on its portal's point.</summary>
    public IReadOnlyList<PortalWaveSpawn> Spawns { get; init; } = Array.Empty<PortalWaveSpawn>();

    /// <summary>
    /// The health a planned creature of a role counts in the score denominator when no creature of that role has spawned:
    /// plan.HealthCurveTarget times the plan's multiplier for the role (BossHealthMultiplier for the boss, HealthMultiplier
    /// otherwise), held to max_creature_health. A role with spawned creatures uses their mean spawn max instead (Task 7).
    /// </summary>
    public IReadOnlyDictionary<DungeonRole, double> UnspawnedHealthEstimate { get; init; } = new Dictionary<DungeonRole, double>();

    /// <summary>Why the wave has no portals (PickPortals' refusal), or null.</summary>
    public string Refusal { get; init; }

    /// <summary>One line per noteworthy event while planning (a missing boss candidate and its stand-ins).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Whole planning wall time, the build included.</summary>
    public double TotalMs { get; init; }
}

/// <summary>
/// Plans one Gauntlet wave by the Balance Trials route: a DungeonGemSpec(site, L(n), tier 8, family "any",
/// seed = hash(runSeed, n), no modifiers) built through <see cref="ThreadDungeonSpawner.BuildLivePlan"/> with the live
/// Thread group for the headcount (<see cref="ThreadDungeonManager.ReadGroupScaling"/>) and the per-wave health curve top
/// (<see cref="CurveAdjust"/>), then delivered by the Defense portal planners, consumed unchanged:
/// <see cref="ThreadEncounterPortalWaves.PickPortals"/> over the site's curated non-interior points
/// (<see cref="GauntletSiteStore.PortalCandidates"/>) and <see cref="ThreadEncounterPortalWaves.PlanStream"/> over the
/// plan's trash. A boss wave adds the plan's boss at the portal furthest from the centre, or two elites when the plan found
/// no boss candidate.
///
/// The site is picked by id (GauntletSiteDef.Outdoor), never through OutdoorSiteTunables, which drops every row above its
/// maxLevel of 500. Nothing here creates a world object; the spawner does that.
/// </summary>
internal sealed class GauntletWavePlanner
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Tier only shapes loot, and Gauntlet loot is stripped.</summary>
    public const int Tier = 8;

    public const string Family = "any";

    /// <summary>The build seam: BuildLivePlan with the live sources in production, a capture or a fixture in a test.</summary>
    internal delegate DungeonSpawnPlan PlanBuild(DungeonGemSpec spec, DungeonEntryDef site, GroupScaling group,
        Func<DungeonPopulationLimits, DungeonPopulationLimits> adjustLimits, out ThreadDungeonSpawner.LivePlanBuildStats stats);

    private readonly GauntletTuning tuning;
    private readonly GauntletRules rules;
    private readonly PlanBuild build;
    private readonly Func<int, GroupScaling> groupFor;

    /// <param name="tuning">Sanitized here.</param>
    /// <param name="build">Null: BuildLivePlan over LivePlanSources.Live.</param>
    /// <param name="groupFor">Null: ThreadDungeonManager.ReadGroupScaling, the live Thread group dials.</param>
    internal GauntletWavePlanner(GauntletTuning tuning, PlanBuild build = null, Func<int, GroupScaling> groupFor = null)
    {
        this.tuning = GauntletTuning.Sanitized(tuning ?? throw new ArgumentNullException(nameof(tuning)));
        rules = new GauntletRules(this.tuning);
        this.build = build ?? LiveBuild;
        this.groupFor = groupFor ?? ThreadDungeonManager.ReadGroupScaling;
    }

    private static DungeonSpawnPlan LiveBuild(DungeonGemSpec spec, DungeonEntryDef site, GroupScaling group,
        Func<DungeonPopulationLimits, DungeonPopulationLimits> adjustLimits, out ThreadDungeonSpawner.LivePlanBuildStats stats)
        => ThreadDungeonSpawner.BuildLivePlan(spec, site, group, ThreadDungeonSpawner.LivePlanSources.Live, () => $"gauntlet {spec.DungeonId} L{spec.Level}", out stats, adjustLimits);

    // ---- pure pieces ---------------------------------------------------------------------------------------------

    /// <summary>hash(runSeed, wave): a non-negative seed that differs per wave and is the same for the same pair. Pure.</summary>
    public static int WaveSeed(int runSeed, int wave)
    {
        unchecked
        {
            var h = (uint)runSeed * 0x9E3779B1u;
            h ^= (uint)wave * 0x85EBCA6Bu;
            h ^= h >> 15;
            h *= 0xC2B2AE35u;
            h ^= h >> 13;
            return (int)(h & int.MaxValue);
        }
    }

    /// <summary>The portal pick and stream draw from their own stream, never the build's (new Random(spec.Seed) inside BuildLivePlan).</summary>
    public static Random DeliveryRng(int waveSeed) => new Random(unchecked(waveSeed * 31 + 17));

    /// <summary>
    /// The per-wave health curve top (orchestrator decision after Task 3): top level min(L, curveTop) with ceiling curveTop,
    /// so a Gauntlet wave at level L is priced as a Thread at L up to 500 and keeps growing to curveTop. Never a fixed top:
    /// that prices stretched picks at their mapped level above L.
    /// </summary>
    public static Func<DungeonPopulationLimits, DungeonPopulationLimits> CurveAdjust(int level, long curveTop)
    {
        var top = (int)Math.Clamp(curveTop, 1L, int.MaxValue);
        var waveTop = Math.Min(level, top);
        return limits => limits.WithHealthCurveTopLevel(waveTop, top);
    }

    /// <summary>The wave's spec, by the constructor only (never Serialize or TryParse: the codec refuses levels above 500).</summary>
    public DungeonGemSpec SpecFor(string siteId, int wave, int runSeed)
        => new DungeonGemSpec(siteId, rules.LevelOf(wave), Tier, Family, WaveSeed(runSeed, wave), Array.Empty<(string, double)>(), 0, 0);

    /// <summary>The picking config: the tunable portal count range and distances (PickPortals draws the count from it).</summary>
    public IPortalWaveConfig PickConfig() => new GauntletPortalConfig
    {
        PortalsMin = (int)Math.Min(int.MaxValue, tuning.PortalsMin),
        PortalsMax = (int)Math.Min(int.MaxValue, tuning.PortalsMax),
        PerPortalMin = 0,
        PerPortalMax = 0,
        StaggerMin = tuning.StaggerMinSeconds,
        StaggerMax = tuning.StaggerMaxSeconds,
        PortalMinDistance = (float)tuning.PortalMinDistance,
        PortalMaxDistance = (float)tuning.PortalMaxDistance,
    };

    /// <summary>See <see cref="GauntletWavePlan.UnspawnedHealthEstimate"/>. Pure.</summary>
    public static Dictionary<DungeonRole, double> UnspawnedEstimates(DungeonSpawnPlan plan, long maxCreatureHealth)
    {
        var result = new Dictionary<DungeonRole, double>();

        if (plan == null)
            return result;

        double Held(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, maxCreatureHealth) : maxCreatureHealth;

        result[DungeonRole.Trash] = Held(plan.HealthCurveTarget * plan.HealthMultiplier);
        result[DungeonRole.Elite] = Held(plan.HealthCurveTarget * plan.HealthMultiplier);
        result[DungeonRole.Boss] = Held(plan.HealthCurveTarget * plan.BossHealthMultiplier);
        return result;
    }

    // ---- the plan --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plans wave <paramref name="wave"/> of a run at <paramref name="site"/> for <paramref name="headcount"/> members.
    /// Throws when the site has no outdoor row or the build throws (the caller decides). A portal refusal is not a throw:
    /// the plan comes back with <see cref="GauntletWavePlan.Refusal"/> set and no spawns.
    /// </summary>
    public GauntletWavePlan Plan(GauntletSiteDef site, int wave, int headcount, int runSeed, string label = null)
    {
        if (site?.Outdoor == null || site.Centre == null)
            throw new ArgumentException("the site has no outdoor row or no centre", nameof(site));

        var total = Stopwatch.StartNew();
        var n = Math.Max(1, wave);
        var h = rules.ClampHeadcount(headcount);
        var level = rules.LevelOf(n);
        var boss = rules.IsBossWave(n);
        var group = groupFor(h) ?? GroupScaling.Solo;
        var spec = SpecFor(site.Id, n, runSeed);

        var plan = build(spec, site.Outdoor, group, CurveAdjust(level, tuning.HealthCurveTopLevel), out var stats)
            ?? throw new InvalidOperationException($"the build returned no plan for {site.Id} L{level}");

        var notes = new List<string>();
        var target = rules.TrashCount(h, boss);
        var rng = DeliveryRng(spec.Seed);

        var anchor = ThreadEncounterGeometry.Global(site.Centre, isOutdoor: true);
        var picked = ThreadEncounterPortalWaves.PickPortals(anchor, (ushort)(site.Centre.Cell >> 16), GauntletSiteStore.PortalCandidates(site.Outdoor),
            isOutdoor: true, PickConfig(), rng, "centre", out var refusal);

        IReadOnlyList<DungeonSpawnPointDef> portals = Array.Empty<DungeonSpawnPointDef>();
        var spawns = new List<PortalWaveSpawn>();
        var bossPortal = -1;
        IPortalWaveConfig streamConfig = null;

        if (picked != null && picked.Count > 0)
        {
            streamConfig = rules.PortalConfig(target, picked.Count);

            // PlanStream streams through every portal it is handed, so hand it exactly the config's portal count (fewer than
            // picked only when the target is smaller than the pick).
            portals = picked.Take(streamConfig.PortalsMin).ToList();

            if (target > 0)
                spawns.AddRange(ThreadEncounterPortalWaves.PlanStream(portals, plan.Entries, streamConfig, rng));

            bossPortal = FurthestFrom(anchor, portals);

            if (boss)
                AddBoss(plan, portals[bossPortal], bossPortal, spawns, notes, n);
        }
        else
        {
            refusal ??= "no portal points";
            log.Warn($"[GAUNTLET] {label} wave {n} at {site.Id}: no portals ({refusal}); the wave has no spawns");
        }

        // Delay order, stable, so the boss (added after the stream at the first delay) follows the first trash of that delay.
        var ordered = spawns.Select((s, i) => (s, i)).OrderBy(x => x.s.Delay).ThenBy(x => x.i).Select(x => x.s).ToList();

        total.Stop();

        log.Info(string.Format(CultureInfo.InvariantCulture,
            "[GAUNTLET] {0} wave {1} L{2} H{3}{4} at {5}: plan build {6:0.0} ms, total {7:0.0} ms, {8} entries, family {9}, {10} spawns through {11} portal(s), trash target {12}",
            label, n, level, h, boss ? " boss" : "", site.Id, stats.ElapsedTotalMs, total.Elapsed.TotalMilliseconds, plan.Entries.Count, plan.FamilyId,
            ordered.Count, portals.Count, target));

        return new GauntletWavePlan
        {
            Wave = n,
            Level = level,
            IsBoss = boss,
            Headcount = h,
            Group = group,
            Spec = spec,
            Plan = plan,
            Stats = stats,
            TrashTarget = target,
            TimeLimitSeconds = rules.TimeLimitSeconds(n),
            StreamConfig = streamConfig,
            Portals = portals,
            BossPortal = bossPortal,
            Spawns = ordered,
            UnspawnedHealthEstimate = UnspawnedEstimates(plan, tuning.MaxCreatureHealth),
            Refusal = picked != null && picked.Count > 0 ? null : refusal,
            Notes = notes,
            TotalMs = total.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>The index of the portal furthest (horizontally) from <paramref name="anchor"/>; ties to the earlier one. Pure.</summary>
    internal static int FurthestFrom(System.Numerics.Vector3 anchor, IReadOnlyList<DungeonSpawnPointDef> portals)
    {
        var best = -1;
        var bestDistance = float.MinValue;

        for (var i = 0; i < portals.Count; i++)
        {
            var d = ThreadEncounterGeometry.Distance(anchor, ThreadEncounterGeometry.Global(portals[i], isOutdoor: true), isOutdoor: true);

            if (d > bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The boss wave's head: the plan's Boss entry at the boss portal, or, when the plan found no boss candidate
    /// (DungeonSpawnPlan.BossNoCandidate, as TrialWavePlan.Select reads it) or carries no Boss entry, two of its elites
    /// there instead (the first elite twice when it has only one), with a note and a log line either way.
    /// </summary>
    private static void AddBoss(DungeonSpawnPlan plan, DungeonSpawnPointDef point, int portal, List<PortalWaveSpawn> spawns, List<string> notes, int wave)
    {
        var head = plan.BossNoCandidate ? null : plan.Entries.FirstOrDefault(e => e?.Role == DungeonRole.Boss);

        if (head != null)
        {
            spawns.Add(new PortalWaveSpawn(portal, ThreadEncounterPortalWaves.FirstSpawnDelaySeconds, Repoint(head, point)));
            return;
        }

        var elites = plan.Entries.Where(e => e?.Role == DungeonRole.Elite).Take(2).ToList();

        if (elites.Count == 1)
            elites.Add(elites[0]);

        foreach (var e in elites)
            spawns.Add(new PortalWaveSpawn(portal, ThreadEncounterPortalWaves.FirstSpawnDelaySeconds, Repoint(e, point)));

        var line = $"wave {wave}: the plan found no boss candidate; {elites.Count} elite(s) stand in";
        notes.Add(line);
        log.Warn($"[GAUNTLET] {line}");
    }

    /// <summary>A copy of an entry at another point, carrying uplift, stamp and stretch (DungeonSpawnPlanEntry's copy rule).</summary>
    private static DungeonSpawnPlanEntry Repoint(DungeonSpawnPlanEntry e, DungeonSpawnPointDef point)
        => new DungeonSpawnPlanEntry(e.Wcid, e.Role, point, e.UpliftLevel, e.StampLevel, e.Stretched);
}
