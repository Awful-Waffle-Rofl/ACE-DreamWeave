using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The server settings a world event hazard reads, snapshotted once at Stage (HAZARDS-PLAN B1). Plain data so the
    /// planner and the tests never touch PropertyManager.
    /// </summary>
    public sealed record WorldEventHazardSettings(bool Enabled, int CountLow, int CountHigh, double Pct,
        string Wisps, string Balls, string Rain, double RainHeight)
    {
        public const bool DefaultEnabled = true;
        public const int DefaultCountLow = 4;
        public const int DefaultCountHigh = 10;
        public const double DefaultPct = 0.30;
        public const string DefaultWisps = "burst=1.5 follow=1 follow_lag=2.4 follow_target=random hold=3 mark_dest=1 move=3.5 pct=0.23 pillar_radius=5 rest=2.5";
        public const string DefaultBalls = "speed=2.5 lead=4 wave_every=6 waves=50";
        public const string DefaultRain = "burst_radius=3 duration=300 lead=0.2 speed=7 target_share=0.3 target_spread=8";
        public const double DefaultRainHeight = 30.0;

        public static readonly WorldEventHazardSettings Defaults = new WorldEventHazardSettings(
            DefaultEnabled, DefaultCountLow, DefaultCountHigh, DefaultPct, DefaultWisps, DefaultBalls, DefaultRain, DefaultRainHeight);
    }

    /// <summary>The run a world event holds: the two things it does to one (ramp its count, stop it) plus an identity to match its end against.</summary>
    public interface IWorldEventHazardRun
    {
        /// <summary>What <see cref="WorldEventHazardHost"/> reports an end under. The live adapter returns the HazardRun.</summary>
        object Key { get; }

        void SetLiveCount(int? count);

        void Stop(string reason);
    }

    /// <summary>Wraps a live <see cref="HazardRun"/>.</summary>
    public sealed class WorldEventHazardRunAdapter : IWorldEventHazardRun
    {
        private readonly HazardRun run;

        public WorldEventHazardRunAdapter(HazardRun run)
        {
            this.run = run ?? throw new ArgumentNullException(nameof(run));
        }

        public object Key => run;

        public void SetLiveCount(int? count) => run.SetLiveCount(count);

        public void Stop(string reason) => run.Stop(reason);
    }

    /// <summary>
    /// Everything one HazardManager.Start call needs, built by <see cref="WorldEventHazards.BuildStart"/>. The seam a test
    /// replaces the live start with (see WorldEvent's hazardStarter).
    /// </summary>
    public sealed class WorldEventHazardStart
    {
        public HazardRegion Region { get; init; }

        /// <summary>wisps | balls | rain (the HazardMechanicRegistry kind of THIS run).</summary>
        public string Kind { get; init; }

        public HazardArgs Args { get; init; }

        /// <summary>The world_events_hazard_&lt;kind&gt; row, laid UNDER <see cref="Args"/> (HazardManager.Start's settingsLayer).</summary>
        public HazardArgs SettingsLayer { get; init; }

        /// <summary>The mechanic pattern: wisps "mixed", balls one of the plan's pool, rain none.</summary>
        public string Pattern { get; init; }

        public WorldEventHazardElement Element { get; init; }

        public IHazardHost Host { get; init; }

        /// <summary>The held anchor landblock. Null only under a test seam.</summary>
        public Landblock Landblock { get; set; }
    }

    /// <summary>
    /// The decided hazard of one world event run, resolved once at Stage by <see cref="WorldEventHazards.Plan"/>. Immutable.
    /// The element is NOT in here when it depends on the decor style (sky_rift), because the decor is picked when the run
    /// goes Active, after Stage; <see cref="WorldEventHazards.ResolveElement"/> settles it at the first start.
    /// </summary>
    public sealed class WorldEventHazardPlan
    {
        /// <summary>The kind of the FIRST run: wisps | balls | rain.</summary>
        public string Kind { get; init; }

        /// <summary>True: each started run picks rolling balls or rain (a source or "random" that resolved to balls). False: every run is <see cref="Kind"/>.</summary>
        public bool BallsAlternateWithRain { get; init; }

        /// <summary>
        /// The kinds a run is picked from, uniformly per run, when the source lists several (hazard.kinds) and the caller did not
        /// name one. Empty: every run is <see cref="Kind"/> (or balls/rain when <see cref="BallsAlternateWithRain"/>). A picked
        /// balls still alternates with rain when <see cref="BallsAlternateWithRain"/> is set. <see cref="Kind"/> is the first entry.
        /// </summary>
        public IReadOnlyList<string> KindChoices { get; init; } = Array.Empty<string>();

        public WorldEventHazardElement? FixedElement { get; init; }

        /// <summary>Element words index-aligned with the theme's decor styles; null when the source has none.</summary>
        public IReadOnlyList<string> StyleElements { get; init; }

        /// <summary>Metres above the anchor the rain starts to fall.</summary>
        public float RainDropHeight { get; init; }

        /// <summary>The ball patterns whose planned single wave fits <see cref="CountHigh"/>. Empty for a plan that cannot roll balls.</summary>
        public IReadOnlyList<string> BallPatterns { get; init; } = Array.Empty<string>();

        public int CountLow { get; init; }

        public int CountHigh { get; init; }

        public double Pct { get; init; }

        public HazardArgs WispsLayer { get; init; } = HazardArgs.Empty;

        public HazardArgs BallsLayer { get; init; } = HazardArgs.Empty;

        public HazardArgs RainLayer { get; init; } = HazardArgs.Empty;

        public HazardRegion Region { get; init; }
    }

    /// <summary>
    /// World Event hazards (HAZARDS-PLAN B1): the planner (which hazard, which element, which ball patterns), the pure count
    /// ramp and the host that reports a run's end back to the event. No landblock, no PropertyManager (except
    /// <see cref="ReadSettings"/>), so everything else here is unit testable.
    /// </summary>
    public static class WorldEventHazards
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindWisps = "wisps";
        public const string KindBalls = "balls";
        public const string KindRain = "rain";
        public const string KindRandom = "random";
        public const string KindNone = "none";

        public const string EnabledKey = "world_events_hazards_enabled";
        public const string CountLowKey = "world_events_hazard_count_low";
        public const string CountHighKey = "world_events_hazard_count_high";
        public const string PctKey = "world_events_hazard_pct";
        public const string WispsKey = "world_events_hazard_wisps";
        public const string BallsKey = "world_events_hazard_balls";
        public const string RainKey = "world_events_hazard_rain";
        public const string RainHeightKey = "world_events_hazard_rain_height";

        /// <summary>Metres above the ground the wisps fly (the Pinnacle's value, HazardRegionPresets.PinnacleWispOffset).</summary>
        public const float HazardOffset = HazardRegionPresets.PinnacleWispOffset;

        /// <summary>Most consecutive Aborted runs (or failed starts) a world event restarts before it gives the hazard up.</summary>
        public const int MaxConsecutiveAborts = 3;

        /// <summary>Seeds tried per ball pattern when sizing its wave.</summary>
        private const int WaveSizeSeeds = 8;

        private static readonly string[] RandomKinds = { KindWisps, KindBalls, KindRain };

        /// <summary>The words a caller (admin API, /worldevent start --hazard) may name, in catalog order. Absent is the source default and is not a word.</summary>
        public static readonly IReadOnlyList<string> RequestWords = new[] { KindRandom, KindNone, KindWisps, KindBalls, KindRain };

        /// <summary>
        /// Checks a caller's hazard word (pure). Null or blank is "absent" = the source default (<paramref name="normalized"/> null,
        /// true). Otherwise the word is trimmed and lowercased and must be one of <see cref="RequestWords"/>; anything else is
        /// refused with a reason naming the allowed values.
        /// </summary>
        public static bool TryNormalizeRequest(string raw, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
                return true;

            var word = raw.Trim().ToLowerInvariant();

            if (!RequestWords.Contains(word))
            {
                error = $"hazard must be one of {string.Join(", ", RequestWords)} (or absent for the source default)";
                return false;
            }

            normalized = word;
            return true;
        }

        // ---- settings -----------------------------------------------------------------------------------------------

        /// <summary>Reads the eight world_events_hazard* settings. A read that throws falls back to the shipped defaults.</summary>
        public static WorldEventHazardSettings ReadSettings()
        {
            var d = WorldEventHazardSettings.Defaults;

            try
            {
                return new WorldEventHazardSettings(
                    PropertyManager.GetBool(EnabledKey, d.Enabled).Item,
                    (int)Math.Clamp(PropertyManager.GetLong(CountLowKey, d.CountLow).Item, 1, 64),
                    (int)Math.Clamp(PropertyManager.GetLong(CountHighKey, d.CountHigh).Item, 1, 64),
                    PropertyManager.GetDouble(PctKey, d.Pct).Item,
                    PropertyManager.GetString(WispsKey, d.Wisps).Item,
                    PropertyManager.GetString(BallsKey, d.Balls).Item,
                    PropertyManager.GetString(RainKey, d.Rain).Item,
                    PropertyManager.GetDouble(RainHeightKey, d.RainHeight).Item);
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read the hazard settings; using the shipped defaults", ex);
                return d;
            }
        }

        // ---- the ramp -----------------------------------------------------------------------------------------------

        /// <summary>
        /// The live count at <paramref name="now"/> (pure). Linear from <paramref name="low"/> over the minimum duration
        /// (measured from <paramref name="activeAt"/>), to <paramref name="high"/> when the event has no boss. With a boss it
        /// ramps to high - 1 and holds there until the boss is placed, then reads high. A minimum duration of 0 reads high at
        /// once. low is raised to at least 1 and high to at least low; the result is always within [low, high].
        /// </summary>
        public static int CountAt(double now, double activeAt, int minDurationSeconds, int low, int high, bool hasBoss, bool bossPlaced)
        {
            low = Math.Max(1, low);
            high = Math.Max(low, high);

            if (minDurationSeconds <= 0)
                return high;

            var fraction = Math.Clamp((now - activeAt) / minDurationSeconds, 0.0, 1.0);

            if (double.IsNaN(fraction))
                fraction = 0.0;

            // A placed boss lifts the count to high (it can only be placed once the minimum has passed).
            if (hasBoss && bossPlaced)
                return high;

            var target = hasBoss ? Math.Max(low, high - 1) : high;
            var value = low + (target - low) * fraction;

            return Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), low, target);
        }

        // ---- the region ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The hazard region for an arena: a circle of the arena's radius around its centre, in the anchor landblock's
        /// frame, with the absolute z band the arena itself uses. Null for an indoor centre (hazards are outdoor only).
        /// </summary>
        public static HazardRegion BuildRegion(WorldEventArena arena)
        {
            if (arena == null || arena.Centre == null || arena.Centre.Indoors)
                return null;

            var c = arena.Centre;
            var z = c.PositionZ;

            return HazardRegion.ArenaCircle("worldevent", (ushort)c.LandblockShort, c.Instance, c.PositionX, c.PositionY,
                arena.Radius, HazardOffset, z - arena.HalfHeight, z + arena.HalfHeight);
        }

        // ---- the planner --------------------------------------------------------------------------------------------

        /// <summary>
        /// Decides the hazard of a run, or null for none. <paramref name="requested"/> is WorldEventRequest.Hazard: null or
        /// empty = the source default, "random" = any kind, "none" = no hazard, else wisps | balls | rain. A source without a
        /// hazard block is "random". Null as well when the switch is off, the arena cannot hold a hazard region, or the kind is
        /// unknown (<paramref name="note"/> says why).
        /// </summary>
        public static WorldEventHazardPlan Plan(SourceThemeDef source, string requested, WorldEventHazardSettings settings,
            WorldEventArena arena, Random rng, out string note)
        {
            note = null;
            settings ??= WorldEventHazardSettings.Defaults;
            rng ??= Random.Shared;

            if (!settings.Enabled)
            {
                note = "hazards are switched off (world_events_hazards_enabled)";
                return null;
            }

            var explicitRequest = !string.IsNullOrWhiteSpace(requested);
            var word = explicitRequest ? requested.Trim().ToLowerInvariant() : (source?.Hazard?.Kind ?? KindRandom).Trim().ToLowerInvariant();

            if (word == KindNone)
            {
                note = explicitRequest ? "the request asked for no hazard" : "the source has no hazard";
                return null;
            }

            var kind = word;
            var choices = new List<string>();

            if (!explicitRequest && source?.Hazard?.Kinds != null && source.Hazard.Kinds.Count > 0)
            {
                // The source lists several kinds: one is picked per run (NextRunKind); the first stands for the plan.
                choices.AddRange(source.Hazard.Kinds);
                kind = choices[0];
            }
            else if (word == KindRandom)
                kind = RandomKinds[rng.Next(RandomKinds.Length)];
            else if (word != KindWisps && word != KindBalls && word != KindRain)
            {
                note = $"unknown hazard kind '{word}'";
                return null;
            }

            var region = BuildRegion(arena);

            if (region == null)
            {
                note = "the arena has no outdoor centre to hold a hazard region";
                return null;
            }

            var regionError = region.Validate();

            if (regionError != null)
            {
                note = "the arena cannot hold a hazard region: " + regionError;
                return null;
            }

            var low = Math.Max(1, settings.CountLow);
            var high = Math.Max(low, settings.CountHigh);
            var pct = settings.Pct;
            var ballsLayer = HazardArgs.Parse(settings.Balls);
            var pool = Array.Empty<string>() as IReadOnlyList<string>;
            var alternates = false;

            // "balls" from a source default or "random" picks rolling or rain per run; a request that names balls pins rolling.
            if (kind == KindBalls || choices.Contains(KindBalls))
            {
                pool = BallPatternPool(region, HazardArgs.Merge(CommonArgs(pct, WorldEventHazardElement.Fire, ballsLayer), ballsLayer), high);

                if (pool.Count == 0)
                {
                    // No pattern fits: a picked balls becomes rain, as a lone balls kind does.
                    note = $"no ball pattern fits a wave of {high} balls; rain only";
                    choices = choices.Select(c => c == KindBalls ? KindRain : c).Distinct().ToList();

                    if (kind == KindBalls)
                        kind = KindRain;
                }
                else
                    alternates = !(explicitRequest && word == KindBalls);
            }

            if (choices.Count == 1)
                choices.Clear(); // a single choice is just the kind

            WorldEventHazardElement? fixedElement = null;

            if (WorldEventHazardElements.TryParse(source?.Hazard?.Element, out var parsed))
                fixedElement = parsed;

            return new WorldEventHazardPlan
            {
                Kind = kind,
                BallsAlternateWithRain = alternates,
                KindChoices = choices,
                FixedElement = fixedElement,
                StyleElements = source?.Hazard?.StyleElements,
                RainDropHeight = RainHeightFor(source, settings),
                BallPatterns = pool,
                CountLow = low,
                CountHigh = high,
                Pct = pct,
                WispsLayer = HazardArgs.Parse(settings.Wisps),
                BallsLayer = ballsLayer,
                RainLayer = HazardArgs.Parse(settings.Rain),
                Region = region,
            };
        }

        /// <summary>
        /// The drop height of the rain: the lowest positive dz of the source's sky decor when it has any, else the source's
        /// hazard.rainHeight, else the world_events_hazard_rain_height setting. Pure.
        /// </summary>
        public static float RainHeightFor(SourceThemeDef source, WorldEventHazardSettings settings)
        {
            float? lowest = null;

            if (source?.Decor != null)
            {
                foreach (var d in source.Decor)
                {
                    if (d == null || d.Snap || !float.IsFinite(d.Dz) || d.Dz <= 0f)
                        continue;

                    if (lowest == null || d.Dz < lowest.Value)
                        lowest = d.Dz;
                }
            }

            if (lowest != null)
                return lowest.Value;

            var own = source?.Hazard?.RainHeight;

            if (own.HasValue && float.IsFinite(own.Value) && own.Value > 0f)
                return own.Value;

            var fallback = settings?.RainHeight ?? WorldEventHazardSettings.DefaultRainHeight;

            return (float)(double.IsFinite(fallback) && fallback > 0.0 ? fallback : WorldEventHazardSettings.DefaultRainHeight);
        }

        /// <summary>
        /// The ball patterns an event may run (pure): those that pass BallRules.Validate on <paramref name="region"/> with
        /// <paramref name="merged"/> AND whose wave, planned under the live-count cap at <paramref name="high"/>
        /// (BallRules.CapToLiveCount, the rule the mechanic applies), holds at most <paramref name="high"/> balls (the most
        /// over several seeds, because row and cross walls are drawn at random). Rings, fan and random shrink to the cap
        /// (a ring never below 3); row and cross have a size fixed by the wall geometry (29 and 58 balls at r=60), cannot
        /// shrink, and so fall out at the shipped high count of 10. Put another way: a pattern is excluded when its minimum
        /// wave size exceeds the configured high count.
        /// </summary>
        public static IReadOnlyList<string> BallPatternPool(HazardRegion region, HazardArgs merged, int high)
        {
            var pool = new List<string>();

            foreach (var pattern in BallPatterns.All)
            {
                if (BallRules.Validate(region, merged, pattern) != null)
                    continue;

                var p = BallRules.CapToLiveCount(BallRules.Read(merged, pattern), high);
                var largest = 0;

                for (var seed = 1; seed <= WaveSizeSeeds; seed++)
                {
                    var wave = BallPatterns.Plan(region, pattern, p, new Random(seed));
                    largest = Math.Max(largest, wave.Routes.Count);
                }

                if (largest > 0 && largest <= high)
                    pool.Add(pattern);
            }

            return pool;
        }

        // ---- the element --------------------------------------------------------------------------------------------

        /// <summary>
        /// The element of the event (pure): the plan's fixed element, else the style element at <paramref name="styleIndex"/>
        /// (-1 = no style was picked), else one of the five at random.
        /// </summary>
        public static WorldEventHazardElement ResolveElement(WorldEventHazardPlan plan, int styleIndex, Random rng)
        {
            if (plan?.FixedElement != null)
                return plan.FixedElement.Value;

            var styles = plan?.StyleElements;

            if (styles != null && styleIndex >= 0 && styleIndex < styles.Count
                && WorldEventHazardElements.TryParse(styles[styleIndex], out var fromStyle))
                return fromStyle;

            var all = (WorldEventHazardElement[])Enum.GetValues(typeof(WorldEventHazardElement));

            return all[(rng ?? Random.Shared).Next(all.Length)];
        }

        /// <summary>
        /// The kind of the next run (pure given rng): one of the plan's KindChoices uniformly when it has several, else the plan's
        /// kind; a balls pick (or an alternating balls plan) then picks rolling or rain.
        /// </summary>
        public static string NextRunKind(WorldEventHazardPlan plan, Random rng)
        {
            rng ??= Random.Shared;
            var kind = plan.Kind;

            if (plan.KindChoices != null && plan.KindChoices.Count > 1)
                kind = plan.KindChoices[rng.Next(plan.KindChoices.Count)];

            if (plan.BallsAlternateWithRain && kind == KindBalls)
                return rng.Next(2) == 0 ? KindBalls : KindRain;

            return kind;
        }

        // ---- the start ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Everything for one HazardManager.Start (pure given rng). Args emitted per kind:
        ///   all    damage_type, hit_verb, pct (pct only when the kind's settings layer has no pct of its own)
        ///   wisps  wisp_wcid, script, mark_script, cycles   (pattern "mixed")
        ///   balls  ball_wcid, marker_script                 (pattern: a member of the plan's pool)
        ///   rain   ball_wcid, marker_script, burst_script, drop_z
        /// The world_events_hazard_&lt;kind&gt; row travels separately as the settings layer.
        /// </summary>
        public static WorldEventHazardStart BuildStart(WorldEventHazardPlan plan, string runKind, WorldEventHazardElement element,
            float anchorZ, Random rng, IHazardHost host)
        {
            var visuals = WorldEventHazardElements.For(element);
            var args = CommonArgs(plan.Pct, element, LayerOf(plan, runKind));
            HazardArgs layer;
            string pattern = null;

            switch (runKind)
            {
                case KindWisps:
                    layer = plan.WispsLayer;
                    pattern = WispRules.Mixed;
                    args = args
                        .With("wisp_wcid", visuals.WispWcid.ToString(CultureInfo.InvariantCulture))
                        .With("script", visuals.PillarScript.ToString(CultureInfo.InvariantCulture))
                        .With("mark_script", visuals.MarkerScript.ToString(CultureInfo.InvariantCulture));

                    // Run as long as the wisp run cap allows, so a chained run is rare.
                    var cycles = WispRules.MaxCyclesWithin(plan.Region, HazardArgs.Merge(args, layer), pattern);
                    args = args.With("cycles", Math.Clamp(cycles, 1, 500).ToString(CultureInfo.InvariantCulture));
                    break;

                case KindBalls:
                    layer = plan.BallsLayer;
                    pattern = plan.BallPatterns.Count > 0 ? plan.BallPatterns[(rng ?? Random.Shared).Next(plan.BallPatterns.Count)] : null;
                    args = args
                        .With("ball_wcid", visuals.BallWcid.ToString(CultureInfo.InvariantCulture))
                        .With("marker_script", visuals.MarkerScript.ToString(CultureInfo.InvariantCulture));
                    break;

                case KindRain:
                    layer = plan.RainLayer;
                    args = args
                        .With("ball_wcid", visuals.BallWcid.ToString(CultureInfo.InvariantCulture))
                        .With("marker_script", visuals.MarkerScript.ToString(CultureInfo.InvariantCulture))
                        .With("burst_script", visuals.BurstScript.ToString(CultureInfo.InvariantCulture))
                        .With("drop_z", (anchorZ + plan.RainDropHeight).ToString("0.##", CultureInfo.InvariantCulture));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(runKind), runKind, "not a world event hazard kind");
            }

            return new WorldEventHazardStart
            {
                Region = plan.Region,
                Kind = runKind,
                Args = args,
                SettingsLayer = layer,
                Pattern = pattern,
                Element = element,
                Host = host,
            };
        }

        /// <summary>
        /// damage_type, hit_verb and pct. The pct is OMITTED when <paramref name="layer"/> (the kind's world_events_hazard_&lt;kind&gt;
        /// string) carries its own: HazardManager.Start merges the caller's args OVER the settings layer, so an emitted pct would
        /// always beat the layer's and a per-kind pct= would be dead.
        /// </summary>
        private static HazardArgs CommonArgs(double pct, WorldEventHazardElement element, HazardArgs layer)
        {
            var visuals = WorldEventHazardElements.For(element);

            var args = HazardArgs.Empty
                .With(HazardDefaults.DamageTypeKey, DamageWord(visuals.DamageType))
                .With(HazardDefaults.HitVerbKey, visuals.HitVerb);

            if (layer != null && layer.Has(HazardDefaults.PctKey))
                return args;

            return args.With(HazardDefaults.PctKey, pct.ToString("0.####", CultureInfo.InvariantCulture));
        }

        private static HazardArgs LayerOf(WorldEventHazardPlan plan, string runKind)
        {
            switch (runKind)
            {
                case KindWisps: return plan.WispsLayer;
                case KindBalls: return plan.BallsLayer;
                case KindRain: return plan.RainLayer;
                default: return null;
            }
        }

        /// <summary>The damage_type arg word of a damage type (fire|acid|cold|electric|bludgeon).</summary>
        public static string DamageWord(DamageType type)
        {
            switch (type)
            {
                case DamageType.Acid: return "acid";
                case DamageType.Cold: return "cold";
                case DamageType.Electric: return "electric";
                case DamageType.Bludgeon: return "bludgeon";
                default: return "fire";
            }
        }
    }

    /// <summary>
    /// The IHazardHost of a world event run. <see cref="PlayerFacing"/> is true (players see the hazard, so the developer
    /// channel stays silent). An end arrives on whichever thread ended the run, so it is only QUEUED here; the event's tick
    /// drains the queue on the world thread. Hazards are not adopted into the event's held list: cleanup is Stop in Finish.
    /// </summary>
    public sealed class WorldEventHazardHost : IHazardHost
    {
        private readonly ConcurrentQueue<(object Key, HazardOutcome Outcome)> ended = new ConcurrentQueue<(object, HazardOutcome)>();

        public bool PlayerFacing => true;

        // ---- per-run hit tally (telemetry only) ---------------------------------------------------------------------

        private readonly object tallyLock = new object();
        private readonly Dictionary<object, HitTally> tallies = new Dictionary<object, HitTally>();

        /// <summary>Runs whose tally was taken (their summary is written): a hit that lands after that is dropped, so no orphan entry is left behind.</summary>
        private readonly HashSet<object> closedRuns = new HashSet<object>(ReferenceEqualityComparer.Instance);

        /// <summary>Open tally entries. Test seam.</summary>
        internal int OpenTallyCount { get { lock (tallyLock) return tallies.Count; } }

        private sealed class HitTally
        {
            public int Hits;
            public readonly HashSet<uint> Victims = new HashSet<uint>();
            public double PctSum;
        }

        void IHazardHost.OnPlayerHit(HazardRun run, Player player, int amount)
        {
            if (run == null || player == null)
                return;

            RecordHit(run, player.Guid.Full, amount, player.Health.MaxValue);
        }

        /// <summary>
        /// Tallies one hit under <paramref name="key"/> (the run). Any thread (hits arrive on the landblock thread). Damage is
        /// recorded as a percent of the victim's max health. Also how a test reports a hit on a fake run.
        /// </summary>
        public void RecordHit(object key, uint playerGuid, int amount, uint maxHealth)
        {
            if (key == null || amount <= 0)
                return;

            lock (tallyLock)
            {
                if (closedRuns.Contains(key))
                    return;

                if (!tallies.TryGetValue(key, out var t))
                    tallies[key] = t = new HitTally();

                t.Hits++;
                t.Victims.Add(playerGuid);

                if (maxHealth > 0)
                    t.PctSum += 100.0 * amount / maxHealth;
            }
        }

        /// <summary>Removes and returns the tally of <paramref name="key"/> (zeros when it was never hit). Any thread.</summary>
        public (int Hits, int PlayersHit, double DamagePctSum) TakeTally(object key)
        {
            lock (tallyLock)
            {
                if (key != null)
                    closedRuns.Add(key);

                if (key == null || !tallies.Remove(key, out var t))
                    return (0, 0, 0.0);

                return (t.Hits, t.Victims.Count, t.PctSum);
            }
        }

        /// <summary>
        /// The one-line summary of a finished hazard run (pure). <paramref name="pattern"/> null or blank prints as "-";
        /// <paramref name="players"/> is the in-region player count when the run ended.
        /// </summary>
        public static string FormatSummary(uint eventRunId, string kind, string pattern, double seconds, int players,
            (int Hits, int PlayersHit, double DamagePctSum) tally)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "[WORLDEVENT] run={0} hazard summary kind={1} pattern={2} seconds={3:0} players={4} hits={5} players_hit={6} dmg_pct_sum={7:0.0}",
                eventRunId, kind, string.IsNullOrWhiteSpace(pattern) ? "-" : pattern, Math.Max(0.0, seconds), players,
                tally.Hits, tally.PlayersHit, tally.DamagePctSum);
        }

        void IHazardHost.OnEnded(HazardRun run, HazardOutcome outcome) => NotifyEnded(run, outcome);

        /// <summary>Queues an end. Any thread. Also how a test reports the end of a fake run.</summary>
        public void NotifyEnded(object key, HazardOutcome outcome) => ended.Enqueue((key, outcome));

        /// <summary>Takes the oldest queued end. World thread.</summary>
        public bool TryDequeue(out object key, out HazardOutcome outcome)
        {
            if (ended.TryDequeue(out var item))
            {
                key = item.Key;
                outcome = item.Outcome;
                return true;
            }

            key = null;
            outcome = default;
            return false;
        }
    }
}
