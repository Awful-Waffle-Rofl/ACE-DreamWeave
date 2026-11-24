using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One wave of the Gambler's Cache, as the portal-wave machinery reads it: this wave's portal and per-portal counts,
    /// its timer, and the row's shared distances and scale. Also the placement view (<see cref="GambleConfig.Placement"/>),
    /// whose portals.min is the largest any wave needs.
    /// </summary>
    public sealed class GambleWaveConfig : IPortalWaveConfig
    {
        public int PortalsMin { get; init; } = 1;
        public int PortalsMax { get; init; } = 1;
        public int PerPortalMin { get; init; } = 2;
        public int PerPortalMax { get; init; } = 3;

        /// <summary>The Defense default stagger (not a config key here).</summary>
        public double StaggerMin { get; init; } = 2.0;
        public double StaggerMax { get; init; } = 4.0;

        public float PortalMinDistance { get; init; } = 8f;
        public float PortalMaxDistance { get; init; } = 25f;
        public double PortalScaleDungeon { get; init; } = 0.3;
        public double PortalScaleOutdoor { get; init; } = 0.6;

        /// <summary>Seconds this wave has to be cleared, from the Deepen.</summary>
        public double Seconds { get; init; } = 60;

        public double PortalScale(bool isOutdoor) => isOutdoor ? PortalScaleOutdoor : PortalScaleDungeon;
    }

    /// <summary>
    /// A gamble row's parsed config (encounters.json "config"). Defaults when a key is absent: the chest 1007980, portal
    /// points 8 to 25 m from it, Defense's portal scales, and the four shipped waves (<see cref="DefaultWaves"/>).
    /// </summary>
    public sealed class GambleConfig
    {
        public uint ChestWcid { get; init; } = GambleEncounterType.DefaultChestWcid;
        public float PortalMinDistance { get; init; } = 8f;
        public float PortalMaxDistance { get; init; } = 25f;
        public double PortalScaleDungeon { get; init; } = 0.3;
        public double PortalScaleOutdoor { get; init; } = 0.6;

        /// <summary>Exactly <see cref="GambleEncounterType.MaxWaves"/> waves, wave 1 first.</summary>
        public IReadOnlyList<GambleWaveConfig> Waves { get; init; } = DefaultWaves();

        public const int MinPortals = 1;
        public const int MaxPortals = 3;
        public const int MinPerPortal = 1;
        public const int MaxPerPortal = 6;
        public const int MaxTotalEnemies = 24;
        public const double MinWaveSeconds = 10;
        public const double MaxWaveSeconds = 600;
        public const float MaxPortalDistance = 60f;
        public const double MaxPortalScale = 2.0;

        /// <summary>The shipped waves: (portals, perPortal, seconds) 1/2-3/60, 1/3-4/75, 2/2/75, 2/3/105.</summary>
        public static IReadOnlyList<GambleWaveConfig> DefaultWaves() => new[]
        {
            new GambleWaveConfig { PortalsMin = 1, PortalsMax = 1, PerPortalMin = 2, PerPortalMax = 3, Seconds = 60 },
            new GambleWaveConfig { PortalsMin = 1, PortalsMax = 1, PerPortalMin = 3, PerPortalMax = 4, Seconds = 75 },
            new GambleWaveConfig { PortalsMin = 2, PortalsMax = 2, PerPortalMin = 2, PerPortalMax = 2, Seconds = 75 },
            new GambleWaveConfig { PortalsMin = 2, PortalsMax = 2, PerPortalMin = 3, PerPortalMax = 3, Seconds = 105 },
        };

        /// <summary>The most enemies the four waves can send between them: the sum of portals.max x perPortal.max. Pure.</summary>
        public static int MaxPossibleTotal(IReadOnlyList<GambleWaveConfig> waves)
            => waves?.Sum(w => w.PortalsMax * w.PerPortalMax) ?? 0;

        /// <summary>
        /// The placement view for the portal prefilter and Place's check: portals.min is the largest any wave needs (so
        /// every wave can open its portals from the points Place kept), portals.max the largest any wave takes.
        /// </summary>
        public GambleWaveConfig Placement => new GambleWaveConfig
        {
            PortalsMin = Waves.Max(w => w.PortalsMin),
            PortalsMax = Waves.Max(w => w.PortalsMax),
            PerPortalMin = 1,
            PerPortalMax = 1,
            PortalMinDistance = PortalMinDistance,
            PortalMaxDistance = PortalMaxDistance,
            PortalScaleDungeon = PortalScaleDungeon,
            PortalScaleOutdoor = PortalScaleOutdoor,
        };
    }

    /// <summary>Where a Gambler's Cache stands.</summary>
    public enum GamblePhase
    {
        /// <summary>Waiting for a choice: Deepen (the next wave) or Take (pay now). <see cref="GambleState.WavesCleared"/> waves are banked.</summary>
        Ready,

        /// <summary>A wave is pouring through the portals.</summary>
        Wave,

        /// <summary>Paid out (a Take, or the last wave cleared). Terminal.</summary>
        Paid,

        /// <summary>Lost (a wave outlasted its timer, or nobody was left alive inside). Terminal.</summary>
        Bust,
    }

    /// <summary>What a use of the chest found.</summary>
    public enum GambleUseOutcome
    {
        /// <summary>The run or its encounters are over: silent.</summary>
        RunOver,

        /// <summary>Not a roster member inside the run: the refusal line.</summary>
        NotInside,

        /// <summary>Ready: offer the Deepen confirmation.</summary>
        Offer,

        /// <summary>A wave is running: the sealed line.</summary>
        WaveInProgress,

        /// <summary>Paid or Bust: silent.</summary>
        Spent,
    }

    /// <summary>How one monitor tick of a wave reads.</summary>
    public enum GambleTickOutcome
    {
        /// <summary>Still going.</summary>
        None,

        /// <summary>The stream finished and every enemy that came through is dead or removed.</summary>
        Cleared,

        /// <summary>The wave's seconds ran out first: bust.</summary>
        BustTimer,

        /// <summary>No living online member is inside the run: bust.</summary>
        BustNobody,
    }

    /// <summary>The stuck rescue's progress mark: the target it was closing on, how far away it was, and since when.</summary>
    public readonly record struct GambleStuckMark(uint TargetGuid, double Distance, double At);

    /// <summary>One wave enemy and its stuck mark (the mark read and written only on the chest's landblock queue).</summary>
    internal sealed class GambleEnemy
    {
        public GambleEnemy(Creature creature) { Creature = creature; }

        public Creature Creature { get; }

        public GambleStuckMark? Mark;
    }

    /// <summary>
    /// The Gambler's Cache lifecycle, every transition under one lock. Ready(0) -Deepen-> Wave(1) -cleared-> Ready(1) ...
    /// Wave(4) -cleared-> Paid; Take only from Ready; Paid and Bust are terminal and inert. Every transition bumps
    /// <see cref="Token"/>, and every confirmation answer carries the token it was offered under, so a stale dialog, or
    /// a second dialog answered after the first one acted, does nothing. Player-free.
    /// </summary>
    public sealed class GambleState
    {
        private readonly object gate = new object();

        private GamblePhase phase = GamblePhase.Ready;
        private int wavesCleared;
        private int currentWave;
        private int token = 1;
        private double waveStartedAt;
        private int pending;
        private bool streamPlanned;
        private int lastWarning = int.MaxValue;

        internal GambleState(ThreadEncounterPlaceRequest request, GambleConfig config, IReadOnlyList<PortalWaveCandidate> candidates)
        {
            Request = request;
            Config = config ?? new GambleConfig();
            Candidates = candidates ?? Array.Empty<PortalWaveCandidate>();
        }

        public GambleConfig Config { get; }

        /// <summary>The curated points a wave's portals may open at (Place's PortalCandidates, chest landblock).</summary>
        public IReadOnlyList<PortalWaveCandidate> Candidates { get; }

        /// <summary>The Place request (run, the chest's landblock, context, point, seed). Null once the run ended.</summary>
        public volatile ThreadEncounterPlaceRequest Request;

        public uint ChestGuid;
        public WorldObject Chest;

        /// <summary>The running wave's enemies (the list under the lock; each one's stuck mark touched only on the chest's landblock queue).</summary>
        internal readonly List<GambleEnemy> Enemies = new List<GambleEnemy>();

        /// <summary>The running wave's portal layers (under the lock).</summary>
        internal readonly List<WorldObject> PortalObjects = new List<WorldObject>();

        internal object Gate => gate;

        public GamblePhase Phase { get { lock (gate) return phase; } }

        /// <summary>Waves cleared and banked (0 to 4).</summary>
        public int WavesCleared { get { lock (gate) return wavesCleared; } }

        /// <summary>The wave now running (1 to 4), or 0 when none is.</summary>
        public int CurrentWave { get { lock (gate) return currentWave; } }

        public int Token { get { lock (gate) return token; } }

        public bool IsTerminal { get { lock (gate) return phase == GamblePhase.Paid || phase == GamblePhase.Bust; } }

        /// <summary>
        /// A use of the chest. Order: the run is over (silent), the user is not a member inside, a wave is running, the
        /// cache is spent. From Ready: <see cref="GambleUseOutcome.Offer"/> with the banked count and the token to offer under.
        /// </summary>
        public GambleUseOutcome TryUse(bool runOver, bool userInside, out int cleared, out int offerToken)
        {
            cleared = 0;
            offerToken = 0;

            if (runOver)
                return GambleUseOutcome.RunOver;

            if (!userInside)
                return GambleUseOutcome.NotInside;

            lock (gate)
            {
                switch (phase)
                {
                    case GamblePhase.Wave:
                        return GambleUseOutcome.WaveInProgress;
                    case GamblePhase.Paid:
                    case GamblePhase.Bust:
                        return GambleUseOutcome.Spent;
                }

                cleared = wavesCleared;
                offerToken = token;
                return GambleUseOutcome.Offer;
            }
        }

        /// <summary>Still Ready under <paramref name="offerToken"/> (nothing has happened since it was offered)?</summary>
        public bool IsReadyUnder(int offerToken)
        {
            lock (gate)
                return phase == GamblePhase.Ready && token == offerToken;
        }

        /// <summary>
        /// THE Deepen latch: from Ready under <paramref name="offerToken"/> with fewer than four waves cleared, starts wave
        /// cleared + 1 at <paramref name="now"/>. False for a stale token, a wave already running, or a terminal cache.
        /// </summary>
        public bool TryDeepen(int offerToken, double now, out int wave)
        {
            lock (gate)
            {
                wave = 0;

                if (phase != GamblePhase.Ready || token != offerToken || wavesCleared >= GambleEncounterType.MaxWaves)
                    return false;

                phase = GamblePhase.Wave;
                currentWave = wavesCleared + 1;
                waveStartedAt = now;
                pending = 0;
                streamPlanned = false;
                lastWarning = int.MaxValue;
                Enemies.Clear();
                token++;
                wave = currentWave;
                return true;
            }
        }

        /// <summary>THE Take latch: from Ready under <paramref name="offerToken"/> to Paid, with the waves banked. False otherwise.</summary>
        public bool TryTake(int offerToken, out int cleared)
        {
            lock (gate)
            {
                cleared = wavesCleared;

                if (phase != GamblePhase.Ready || token != offerToken)
                    return false;

                phase = GamblePhase.Paid;
                token++;
                return true;
            }
        }

        /// <summary>Wave <paramref name="wave"/>'s stream is planned: <paramref name="count"/> enemies to come. False when that wave is not running.</summary>
        public bool BeginStream(int wave, int count)
        {
            lock (gate)
            {
                if (phase != GamblePhase.Wave || currentWave != wave || streamPlanned)
                    return false;

                pending = Math.Max(0, count);
                streamPlanned = true;
                return true;
            }
        }

        /// <summary>Is wave <paramref name="wave"/> the one running?</summary>
        public bool IsWaveRunning(int wave)
        {
            lock (gate)
                return phase == GamblePhase.Wave && currentWave == wave;
        }

        /// <summary>
        /// One planned enemy of wave <paramref name="wave"/> is done (placed as <paramref name="placed"/>, or not). False,
        /// touching nothing, when that wave is no longer running (a late spawn after a bust or a clear).
        /// </summary>
        public bool SpawnDone(int wave, Creature placed)
        {
            lock (gate)
            {
                if (phase != GamblePhase.Wave || currentWave != wave)
                    return false;

                pending = Math.Max(0, pending - 1);

                if (placed != null)
                    Enemies.Add(new GambleEnemy(placed));

                return true;
            }
        }

        /// <summary>
        /// THE warning latch: the time-left warning due now for wave <paramref name="wave"/> (<see cref="GambleEncounterType.WarningDue"/>),
        /// latched so each threshold is said at most once per wave. 0 when none is due or the wave is not running.
        /// </summary>
        public int TryLatchWarning(int wave, double elapsed, double seconds)
        {
            lock (gate)
            {
                if (phase != GamblePhase.Wave || currentWave != wave)
                    return 0;

                var due = GambleEncounterType.WarningDue(lastWarning, elapsed, seconds);

                if (due > 0)
                    lastWarning = due;

                return due;
            }
        }

        /// <summary>The running wave's read for a tick: false when <paramref name="wave"/> is not running.</summary>
        internal bool Snapshot(int wave, double now, out bool streamDone, out double elapsed)
        {
            lock (gate)
            {
                streamDone = false;
                elapsed = 0;

                if (phase != GamblePhase.Wave || currentWave != wave)
                    return false;

                streamDone = streamPlanned && pending <= 0;
                elapsed = now - waveStartedAt;
                return true;
            }
        }

        /// <summary>
        /// THE clear latch: wave <paramref name="wave"/> cleared. Banks it; the fourth clear goes straight to Paid
        /// (<paramref name="autoPay"/> true), any other back to Ready. False when that wave is not running.
        /// </summary>
        public bool TryClearWave(int wave, out bool autoPay, out int cleared)
        {
            lock (gate)
            {
                autoPay = false;
                cleared = wavesCleared;

                if (phase != GamblePhase.Wave || currentWave != wave)
                    return false;

                wavesCleared = wave;
                cleared = wave;
                currentWave = 0;
                autoPay = wavesCleared >= GambleEncounterType.MaxWaves;
                phase = autoPay ? GamblePhase.Paid : GamblePhase.Ready;
                token++;
                return true;
            }
        }

        /// <summary>THE bust latch: wave <paramref name="wave"/> lost. Terminal. False when that wave is not running.</summary>
        public bool TryBust(int wave)
        {
            lock (gate)
            {
                if (phase != GamblePhase.Wave || currentWave != wave)
                    return false;

                phase = GamblePhase.Bust;
                currentWave = 0;
                token++;
                return true;
            }
        }

        /// <summary>The wave could not open (an internal failure, never the players'): paid out with what was banked. False when <paramref name="wave"/> is not running.</summary>
        public bool TryRefund(int wave, out int cleared)
        {
            lock (gate)
            {
                cleared = wavesCleared;

                if (phase != GamblePhase.Wave || currentWave != wave)
                    return false;

                phase = GamblePhase.Paid;
                currentWave = 0;
                token++;
                return true;
            }
        }

        /// <summary>Takes the running wave's portal layers out of the state (for the collapse).</summary>
        internal List<WorldObject> TakePortals()
        {
            lock (gate)
            {
                var portals = PortalObjects.ToList();
                PortalObjects.Clear();
                return portals;
            }
        }
    }

    /// <summary>
    /// The Gambler's Cache (Docs/Threads/ENCOUNTERS.md "Gambler's Cache"), dungeon and outdoor runs alike. A chest forms
    /// at the picked point with one prize banked. A member inside the run uses it and chooses: Deepen (a wave of the run's
    /// trash pours through portals around it; clear it before its timer and the prize grows) or Take (every online member
    /// inside gets an independent prize roll, grown by the waves cleared). After the fourth wave clears it pays out by
    /// itself. A wave not cleared in time, or nobody left alive inside, busts it: nothing is paid. Wave enemies are bonus
    /// kills (ExcludedFromClear); leftovers after a bust stay as ordinary bonus mobs.
    ///
    /// Threading. Place runs on the point's landblock queue. OnUse runs on the user's thread and only reads and offers;
    /// each confirmation answer latches under the state's lock and queues the work (portals, stream, the 1 s monitor,
    /// the payout, the chest's destroy) onto the CHEST's landblock through ActionChains. OnRunEnded releases memory only.
    /// </summary>
    public sealed class GambleEncounterType : IThreadEncounterType, IThreadEncounterUsable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindName = "gamble";

        /// <summary>Content/sql/weenies/1007980 Gambler's Cache.sql (wcid-registry.d/wcid-1007980.tsv, thread-gamble).</summary>
        public const uint DefaultChestWcid = 1007980;

        /// <summary>The most Deepens; the last wave's clear pays out by itself. ThreadEncounterPrize.GambleQuantity clamps to the same four.</summary>
        public const int MaxWaves = ThreadEncounterPrize.MaxBonusWaves;

        public const double MonitorSeconds = 1.0;

        /// <summary>Seconds between an outcome and the chest's destroy.</summary>
        public const double ChestDestroyDelaySeconds = 3.0;

        /// <summary>A Deepen or Take answer counts only from a player within this many metres of the chest.</summary>
        public const double AnswerRadius = 15.0;

        /// <summary>The time-left warnings, highest first (seconds remaining), each said once per wave.</summary>
        public static readonly IReadOnlyList<int> WarningSeconds = new[] { 30, 10 };

        /// <summary>The dungeon walk ratio for a cache's portal points (Defense's is ThreadEncounterPortalWaves.WalkRatio, 1.5).</summary>
        public const float DungeonWalkRatio = 1.25f;

        /// <summary>Stuck rescue: an awake wave enemy that has not closed this many metres on its target ...</summary>
        public const double StuckCloseMetres = 1.0;

        /// <summary>... within this many seconds is moved to the chest.</summary>
        public const double StuckSeconds = 8.0;

        /// <summary>An enemy this close to its target (in melee) or to the chest is never rescued: there is nothing to close.</summary>
        public const double StuckIgnoreMetres = 2.0;

        public const string RefusedLine = "Only members of this thread can open the Gambler's Cache.";
        public const string TakePrompt = "Take the prize now and close the Gambler's Cache?";
        public const string SealedLine = "The cache is sealed until this wave is cleared.";
        public const string NotAtCacheLine = "You must be at the Gambler's Cache to answer.";
        public const string BustTimerLine = "The wave outlasted you. The Gambler's Cache seals itself and the prize is lost.";
        public const string BustNobodyLine = "No one is left to claim it. The Gambler's Cache seals itself and the prize is lost.";

        private static string Of(int wave) => wave.ToString(CultureInfo.InvariantCulture);

        private static string Secs(double seconds) => seconds.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>The Deepen prompt for wave <paramref name="wave"/> (1 to 4) with that wave's configured <paramref name="seconds"/>.</summary>
        public static string DeepenPrompt(int wave, double seconds)
            => $"Deepen the Gambler's Cache? Wave {Of(wave)} of 4 will pour through the portals. Clear it within {Secs(seconds)} seconds and the prize may grow; fall or run out of time and everything is lost. (Answer No to take the prize instead.)";

        public static string WaveStartLine(string name, int wave, double seconds)
            => $"{name} deepens the Gambler's Cache. Wave {Of(wave)} of 4 pours through the portals! Clear it within {Secs(seconds)} seconds!";

        public static string WarningLine(int secondsLeft) => $"{Of(secondsLeft)} seconds remain to clear the wave!";

        public static string WaveClearedLine(int wave) => $"Wave {Of(wave)} of 4 cleared. The prize may grow. Use the cache to deepen again or take your winnings.";

        public static string TakeLine(string name) => $"{name} takes the Gambler's Cache's prize.";

        public string Kind => KindName;

        /// <summary>Test seam: the object factory. Production is WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>Test seam: the clock (unix seconds).</summary>
        internal static Func<double> Now = () => ACE.Common.Time.GetUnixTime();

        /// <summary>Test seam: the online roster members inside the run. Production is ThreadRunPresence.OnlineMembersInside.</summary>
        internal static Func<ThreadDungeonRun, List<Player>> MembersInside = run => ThreadRunPresence.OnlineMembersInside(run);

        /// <summary>Test seam: the prize grant. Production is ThreadEncounterPrize.Grant with the waves cleared as gamble growth.</summary>
        internal static Func<ThreadDungeonRun, Player, string, int, bool> GrantPrize = ThreadEncounterPrize.Grant;

        /// <summary>The shared portal-wave machinery, logging as this type under the "gamble" label and creating through <see cref="CreateObject"/>.</summary>
        private static readonly ThreadEncounterPortalWaves Waves = new ThreadEncounterPortalWaves(log, KindName, wcid => CreateObject(wcid));

        // ---- config ------------------------------------------------------------------------------------------------

        private static readonly HashSet<string> ConfigKeys = new HashSet<string>(StringComparer.Ordinal)
            { "chestWcid", "portalMinDistance", "portalMaxDistance", "portalScale", "waves" };

        private static readonly HashSet<string> WaveKeys = new HashSet<string>(StringComparer.Ordinal) { "portals", "perPortal", "seconds" };

        private static readonly HashSet<string> RangeKeys = new HashSet<string>(StringComparer.Ordinal) { "min", "max" };

        private static readonly HashSet<string> ScaleKeys = new HashSet<string>(StringComparer.Ordinal) { "dungeon", "outdoor" };

        /// <summary>
        /// Parses a gamble row's config. An absent or empty config yields the defaults. Anything else wrong is an error
        /// (null with <paramref name="error"/>) and the type is not placed: a config that is not an object, an unknown key
        /// at any level, a bad chestWcid, bad distances or scales (Defense's bounds), or a waves list that is not exactly
        /// four rows of { portals 1..3, perPortal 1..6, seconds 10..600 } whose most possible enemies total more than 24.
        /// Pure.
        /// </summary>
        public static GambleConfig ParseConfig(JsonElement config, out string error)
        {
            error = null;

            if (config.ValueKind == JsonValueKind.Undefined || config.ValueKind == JsonValueKind.Null)
                return new GambleConfig();

            if (config.ValueKind != JsonValueKind.Object)
            {
                error = $"config must be an object, not {config.ValueKind}";
                return null;
            }

            if (!KnownKeysOnly(config, ConfigKeys, "config", out error))
                return null;

            var chest = DefaultChestWcid;

            if (config.TryGetProperty("chestWcid", out var c) && (c.ValueKind != JsonValueKind.Number || !c.TryGetUInt32(out chest) || chest == 0))
            {
                error = "config.chestWcid must be a positive whole number";
                return null;
            }

            if (!TryReadNumber(config, "portalMinDistance", 8.0, out var minDist) || !TryReadNumber(config, "portalMaxDistance", 25.0, out var maxDist)
                || minDist < 0 || maxDist <= minDist || maxDist > GambleConfig.MaxPortalDistance)
            {
                error = $"config.portalMinDistance and portalMaxDistance must be numbers with 0 <= min < max <= {GambleConfig.MaxPortalDistance.ToString(CultureInfo.InvariantCulture)}";
                return null;
            }

            double scaleDungeon = 0.3, scaleOutdoor = 0.6;

            if (config.TryGetProperty("portalScale", out var scale))
            {
                if (scale.ValueKind != JsonValueKind.Object || !KnownKeysOnly(scale, ScaleKeys, "config.portalScale", out error)
                    || !TryReadNumber(scale, "dungeon", 0.3, out scaleDungeon) || !TryReadNumber(scale, "outdoor", 0.6, out scaleOutdoor)
                    || !(scaleDungeon > 0) || !(scaleOutdoor > 0) || scaleDungeon > GambleConfig.MaxPortalScale || scaleOutdoor > GambleConfig.MaxPortalScale)
                {
                    error ??= $"config.portalScale must be {{ dungeon, outdoor }} with each above 0 and at most {GambleConfig.MaxPortalScale.ToString(CultureInfo.InvariantCulture)}";
                    return null;
                }
            }

            var defaults = GambleConfig.DefaultWaves();
            var waves = new List<GambleWaveConfig>();

            if (config.TryGetProperty("waves", out var w))
            {
                if (w.ValueKind != JsonValueKind.Array || w.GetArrayLength() != MaxWaves)
                {
                    error = $"config.waves must be an array of exactly {MaxWaves} wave rows";
                    return null;
                }

                var i = 0;

                foreach (var row in w.EnumerateArray())
                {
                    var label = $"config.waves[{i}]";
                    var def = defaults[i++];

                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        error = $"{label} must be an object";
                        return null;
                    }

                    if (!KnownKeysOnly(row, WaveKeys, label, out error))
                        return null;

                    if (!TryReadIntRange(row, label, "portals", def.PortalsMin, def.PortalsMax, GambleConfig.MinPortals, GambleConfig.MaxPortals, out var pMin, out var pMax, out error))
                        return null;

                    if (!TryReadIntRange(row, label, "perPortal", def.PerPortalMin, def.PerPortalMax, GambleConfig.MinPerPortal, GambleConfig.MaxPerPortal, out var eMin, out var eMax, out error))
                        return null;

                    if (!TryReadNumber(row, "seconds", def.Seconds, out var seconds) || seconds < GambleConfig.MinWaveSeconds || seconds > GambleConfig.MaxWaveSeconds)
                    {
                        error = $"{label}.seconds must be a number from {GambleConfig.MinWaveSeconds.ToString(CultureInfo.InvariantCulture)} to {GambleConfig.MaxWaveSeconds.ToString(CultureInfo.InvariantCulture)}";
                        return null;
                    }

                    waves.Add(Wave(pMin, pMax, eMin, eMax, seconds, minDist, maxDist, scaleDungeon, scaleOutdoor));
                }
            }
            else
            {
                foreach (var d in defaults)
                    waves.Add(Wave(d.PortalsMin, d.PortalsMax, d.PerPortalMin, d.PerPortalMax, d.Seconds, minDist, maxDist, scaleDungeon, scaleOutdoor));
            }

            var total = GambleConfig.MaxPossibleTotal(waves);

            if (total > GambleConfig.MaxTotalEnemies)
            {
                error = $"config.waves could send up to {total.ToString(CultureInfo.InvariantCulture)} enemies; at most {GambleConfig.MaxTotalEnemies.ToString(CultureInfo.InvariantCulture)} across the {MaxWaves} waves";
                return null;
            }

            return new GambleConfig
            {
                ChestWcid = chest,
                PortalMinDistance = (float)minDist,
                PortalMaxDistance = (float)maxDist,
                PortalScaleDungeon = scaleDungeon,
                PortalScaleOutdoor = scaleOutdoor,
                Waves = waves,
            };
        }

        private static GambleWaveConfig Wave(int pMin, int pMax, int eMin, int eMax, double seconds, double minDist, double maxDist, double scaleDungeon, double scaleOutdoor)
            => new GambleWaveConfig
            {
                PortalsMin = pMin,
                PortalsMax = pMax,
                PerPortalMin = eMin,
                PerPortalMax = eMax,
                Seconds = seconds,
                PortalMinDistance = (float)minDist,
                PortalMaxDistance = (float)maxDist,
                PortalScaleDungeon = scaleDungeon,
                PortalScaleOutdoor = scaleOutdoor,
            };

        private static bool KnownKeysOnly(JsonElement obj, HashSet<string> known, string label, out string error)
        {
            error = null;

            foreach (var p in obj.EnumerateObject())
            {
                if (!known.Contains(p.Name))
                {
                    error = $"{label}: unknown key '{p.Name}'";
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadIntRange(JsonElement row, string label, string name, int defMin, int defMax, int floor, int ceiling, out int min, out int max, out string error)
        {
            min = defMin;
            max = defMax;
            error = null;

            if (!row.TryGetProperty(name, out var block))
                return true;

            if (block.ValueKind != JsonValueKind.Object || !KnownKeysOnly(block, RangeKeys, $"{label}.{name}", out error)
                || !TryReadInt(block, "min", defMin, out min) || !TryReadInt(block, "max", defMax, out max)
                || min < floor || max < min || max > ceiling)
            {
                error ??= $"{label}.{name} must be {{ min, max }} with {floor} <= min <= max <= {ceiling}";
                return false;
            }

            return true;
        }

        private static bool TryReadInt(JsonElement obj, string name, int fallback, out int value)
        {
            value = fallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }

        private static bool TryReadNumber(JsonElement obj, string name, double fallback, out double value)
        {
            value = fallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value) && double.IsFinite(value);
        }

        // ---- pure rules ------------------------------------------------------------------------------------------

        /// <summary>
        /// One monitor tick of a running wave: nobody alive inside is a bust (first); then the portal-wave outcome table
        /// with no anchor (ThreadEncounterPortalWaves.Decide, anchorStanding true): cleared, or the wave's seconds run out
        /// (a bust); else none. Pure.
        /// </summary>
        public static GambleTickOutcome DecideTick(bool anyMemberAlive, bool streamDone, int enemiesAlive, double elapsed, double seconds)
        {
            if (!anyMemberAlive)
                return GambleTickOutcome.BustNobody;

            return ThreadEncounterPortalWaves.Decide(anchorStanding: true, streamDone, enemiesAlive, elapsed, seconds) switch
            {
                PortalWaveOutcome.Cleared => GambleTickOutcome.Cleared,
                PortalWaveOutcome.TimedOut => GambleTickOutcome.BustTimer,
                _ => GambleTickOutcome.None,
            };
        }

        /// <summary>
        /// The time-left warning to say now: the LOWEST of 30 / 10 that is below <paramref name="lastSent"/> (int.MaxValue
        /// before any), at or above the seconds remaining, above 0 remaining, and below the wave's own
        /// <paramref name="seconds"/> (a 25 s wave never says 30). 0 for none. A lag past both in one tick says only 10. Pure.
        /// </summary>
        public static int WarningDue(int lastSent, double elapsed, double seconds)
        {
            if (!double.IsFinite(elapsed) || !double.IsFinite(seconds))
                return 0;

            var remaining = seconds - elapsed;

            if (remaining <= 0)
                return 0;

            var result = 0;

            foreach (var w in WarningSeconds)
                if (w < lastSent && w < seconds && remaining <= w)
                    result = w;

            return result;
        }

        /// <summary>
        /// One stuck-rescue step for an awake wave enemy, at <paramref name="now"/>, <paramref name="distance"/> metres from
        /// its target <paramref name="targetGuid"/>. A new target, no mark yet, or a close of at least
        /// <see cref="StuckCloseMetres"/> since the mark starts a fresh mark here. Otherwise, once
        /// <see cref="StuckSeconds"/> have passed since the mark, it is due a rescue (a fresh mark follows) unless it is
        /// within <see cref="StuckIgnoreMetres"/> of its target or of the chest (<paramref name="distanceToChest"/>): in
        /// melee, or already where the rescue would put it. Pure.
        /// </summary>
        public static (bool Rescue, GambleStuckMark Mark) StuckStep(GambleStuckMark? mark, uint targetGuid, double distance, double distanceToChest, double now, bool attackedRecently)
        {
            var fresh = new GambleStuckMark(targetGuid, distance, now);

            // An enemy that attacked lately (melee, missile or spell) is fighting, not stuck: a ranged or caster enemy
            // never closes on its target, and must not be pulled off its range.
            if (attackedRecently)
                return (false, fresh);

            if (mark is not GambleStuckMark m || m.TargetGuid != targetGuid || distance <= m.Distance - StuckCloseMetres)
                return (false, fresh);

            if (now - m.At < StuckSeconds)
                return (false, m);

            if (distance <= StuckIgnoreMetres || distanceToChest <= StuckIgnoreMetres)
                return (false, fresh);

            return (true, fresh);
        }

        /// <summary>
        /// Did the enemy start an attack within the last <see cref="StuckSeconds"/>? <paramref name="prevAttackTime"/> is
        /// Creature.PrevAttackTime, stamped (on the Timers.RunningTime clock) at the start of every melee swing
        /// (Monster_Melee), missile shot (Monster_Missile) and spell (Monster_Magic, a little ahead for the wind-up). A
        /// value up to <see cref="StuckSeconds"/> ahead of <paramref name="runningNow"/> counts (a wind-up); one further ahead is
        /// the NeverAttack sentinel (near double.MaxValue) and does not. 0 (never attacked) does not. Pure.
        /// </summary>
        public static bool AttackedRecently(double prevAttackTime, double runningNow)
            => double.IsFinite(prevAttackTime) && double.IsFinite(runningNow) && prevAttackTime > 0
               && prevAttackTime <= runningNow + StuckSeconds && runningNow - prevAttackTime < StuckSeconds;

        /// <summary>A player's distance from the chest for the answer range: double.MaxValue when either position is unknown. Pure.</summary>
        public static double AnswerDistance(ACE.Entity.Position player, ACE.Entity.Position chest)
            => player == null || chest == null ? double.MaxValue : player.DistanceTo(chest);

        /// <summary>Is a player <paramref name="distance"/> metres from the chest close enough to answer (<see cref="AnswerRadius"/>)? Pure.</summary>
        public static bool WithinAnswerRange(double distance) => double.IsFinite(distance) && distance <= AnswerRadius;

        /// <summary>Is any of these members alive (<paramref name="isDead"/> false for one)? Pure.</summary>
        public static bool AnyAlive<T>(IEnumerable<T> members, Func<T, bool> isDead)
            => members != null && isDead != null && members.Any(m => m != null && !isDead(m));

        /// <summary>One grant per member, counting the ones that took (Defense's payout shape). Pure over <paramref name="grant"/>.</summary>
        internal static int GrantToAll<T>(IEnumerable<T> members, Func<T, bool> grant)
        {
            var granted = 0;

            foreach (var member in members ?? Enumerable.Empty<T>())
                if (grant(member))
                    granted++;

            return granted;
        }

        /// <summary>Wave <paramref name="wave"/>'s own rng seed (one per wave, from the placement's seed). Pure.</summary>
        internal static int WaveSeed(int placementSeed, int wave) => unchecked(placementSeed * 31 + 0x6A3B + wave * 7919);

        // ---- group scaling (owner ruling 2026-11-16: wave counts scale with group size) ----------------------------

        /// <summary>The most enemies ONE wave may send after group scaling (the base rows still answer to the 24 total).</summary>
        public const int MaxEnemiesPerWave = 24;

        /// <summary>
        /// The wave count factor: the run's C_actual (ThreadDungeonRun.GroupCountActual, the count multiplier the Threads
        /// health scaling divides by, H = E / C_actual) for a GROUP run, and exactly 1.0 for a solo run (guarded on
        /// IsGroup, as GroupScaling asks of every consumer). Never below 1.0 (the never-below-solo rule, R27), and 1.0 for
        /// a NaN, infinite or non-positive value. Pure.
        /// </summary>
        public static double GroupCountFactor(bool isGroup, double countActual)
            => isGroup && double.IsFinite(countActual) && countActual > 1.0 ? countActual : 1.0;

        /// <summary>A wave's scaled enemy count: round(<paramref name="baseCount"/> x <paramref name="factor"/>), halves away from zero, at most <see cref="MaxEnemiesPerWave"/>. Pure.</summary>
        public static int ScaledWaveCount(int baseCount, double factor)
        {
            if (baseCount <= 0)
                return 0;

            var f = double.IsFinite(factor) && factor > 0 ? factor : 1.0;
            var scaled = Math.Round(baseCount * f, MidpointRounding.AwayFromZero);
            return (int)Math.Min(MaxEnemiesPerWave, Math.Max(0, scaled));
        }

        /// <summary><paramref name="total"/> enemies across <paramref name="portals"/> portals as evenly as possible: each gets total / n, the first total % n one more. Pure.</summary>
        public static int[] DistributeAcrossPortals(int total, int portals)
        {
            if (portals <= 0)
                return Array.Empty<int>();

            var counts = new int[portals];
            var each = Math.Max(0, total) / portals;
            var extra = Math.Max(0, total) % portals;

            for (var p = 0; p < portals; p++)
                counts[p] = each + (p < extra ? 1 : 0);

            return counts;
        }

        /// <summary>
        /// One wave's stream, group-scaled. At <paramref name="factor"/> 1.0 (every solo run) it IS
        /// ThreadEncounterPortalWaves.PlanStream, draw for draw, so a solo wave is exactly what it always was. Otherwise:
        /// first every portal's base count is drawn (rng.Next(perPortal.min, perPortal.max + 1) per portal, in portal order),
        /// their sum is scaled (<see cref="ScaledWaveCount"/>) and spread across the portals
        /// (<see cref="DistributeAcrossPortals"/>), then each portal streams its share with the same draws PlanStream makes
        /// (the first 2 s in, each next one staggerSeconds later, one trash draw each). A group portal whose share would
        /// land its last spawn after <see cref="LastSpawnFraction"/> of <paramref name="waveSeconds"/> has its stagger
        /// compressed (<see cref="StaggerCompression"/>) so it does not; the first spawn's delay and every draw are
        /// unchanged, and a stagger is never widened. Delay order, stable. Empty with no trash. Pure over <paramref name="rng"/>.
        /// </summary>
        public static List<PortalWaveSpawn> PlanWaveStream(IReadOnlyList<DungeonSpawnPointDef> portals, IReadOnlyList<DungeonSpawnPlanEntry> planEntries,
            IPortalWaveConfig config, Random rng, double factor, double waveSeconds)
        {
            if (factor == 1.0)
                return ThreadEncounterPortalWaves.PlanStream(portals, planEntries, config, rng);

            var result = new List<PortalWaveSpawn>();

            if (portals == null || portals.Count == 0 || config == null || rng == null)
                return result;

            var pool = ThreadEncounterTrash.Pool(planEntries);

            if (pool.Count == 0)
                return result;

            var baseCount = 0;

            for (var p = 0; p < portals.Count; p++)
                baseCount += rng.Next(config.PerPortalMin, config.PerPortalMax + 1);

            var counts = DistributeAcrossPortals(ScaledWaveCount(baseCount, factor), portals.Count);

            var first = ThreadEncounterPortalWaves.FirstSpawnDelaySeconds;

            for (var p = 0; p < portals.Count; p++)
            {
                // Draw exactly as before (stagger, then trash, per spawn); only the offsets are scaled afterwards.
                // An uncompressed portal keeps the very delays it always had (the same running sum, bit for bit).
                var delays = new double[counts[p]];
                var entries = new DungeonSpawnPlanEntry[counts[p]];
                var t = first;

                for (var k = 0; k < counts[p]; k++)
                {
                    if (k > 0)
                        t += config.StaggerMin + rng.NextDouble() * (config.StaggerMax - config.StaggerMin);

                    delays[k] = t;
                    entries[k] = ThreadEncounterTrash.Draw(pool, portals[p], rng);
                }

                var squeeze = counts[p] > 0 ? StaggerCompression(first, delays[counts[p] - 1] - first, waveSeconds) : 1.0;

                for (var k = 0; k < counts[p]; k++)
                    result.Add(new PortalWaveSpawn(p, squeeze < 1.0 ? first + (delays[k] - first) * squeeze : delays[k], entries[k]));
            }

            return result.OrderBy(s => s.Delay).ToList();
        }

        /// <summary>A group portal's last spawn lands by this fraction of the wave's seconds (owner review of #2099, F1).</summary>
        public const double LastSpawnFraction = 0.6;

        /// <summary>
        /// The stagger multiplier for one group portal whose planned spawns span <paramref name="plannedSpan"/> seconds after
        /// its first at <paramref name="firstDelay"/>: min(1, (<see cref="LastSpawnFraction"/> x <paramref name="waveSeconds"/>
        /// - firstDelay) / plannedSpan), never below 0 and never above 1 (a stagger is never widened). 1.0 for no span or a
        /// non-positive or non-finite wave length. Pure.
        /// </summary>
        public static double StaggerCompression(double firstDelay, double plannedSpan, double waveSeconds)
        {
            if (!(plannedSpan > 0) || !double.IsFinite(plannedSpan) || !(waveSeconds > 0) || !double.IsFinite(waveSeconds))
                return 1.0;

            var room = LastSpawnFraction * waveSeconds - firstDelay;
            return Math.Clamp(room / plannedSpan, 0.0, 1.0);
        }

        // ---- IThreadEncounterType -------------------------------------------------------------------------------

        /// <summary>The run's encounter prize is off: there would be nothing to gamble for (dynamic_dungeons_encounter_prize_enabled, stamped on the run).</summary>
        public string RejectRun(ThreadEncounterTypeDef def, ThreadDungeonRun run)
            => run != null && !run.EncounterPrizeEnabled ? "gamble: the encounter prize is disabled for this run" : null;

        /// <summary>The Defense prefilter (ThreadEncounterPortalWaves.RejectPointAmong) with the placement view: enough portal points in reach for the wave that needs the most.</summary>
        public string RejectPointAmong(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor)
        {
            var config = ParseConfig(def?.Config ?? default, out var error);

            if (config == null)
                return "gamble: " + error;

            return Waves.RejectPointAmong(candidate, runPoints, isOutdoor, config.Placement);
        }

        /// <summary>
        /// The prefilter with Place's whole portal rule: <see cref="RejectPointAmong"/>, then Place's own candidates through
        /// the same walk rule Place applies (<see cref="DungeonWalkRatio"/>, a known walked distance required;
        /// ThreadEncounterPortalWaves.RejectPointInRun). Pure over the walk graph seam.
        /// </summary>
        public string RejectPointInRun(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, ThreadDungeonRun run)
        {
            var isOutdoor = run?.Dungeon?.IsOutdoor == true;
            var config = ParseConfig(def?.Config ?? default, out var error);

            if (config == null)
                return "gamble: " + error;

            return Waves.RejectPointInRun(run, candidate, runPoints, isOutdoor, config.Placement, "cache", DungeonWalkRatio, requireWalkDistance: true);
        }

        public bool Place(ThreadEncounterPlaceRequest request, out string failure)
        {
            failure = null;

            var run = request.Run;
            var config = ParseConfig(request.Def?.Config ?? default, out var error);

            if (config == null)
            {
                failure = error;
                return false;
            }

            if (!run.EncounterPrizeEnabled)
            {
                failure = "the encounter prize is disabled for this run";
                return false;
            }

            if (ThreadEncounterTrash.Pool(request.Context?.Plan?.Entries).Count == 0)
            {
                failure = "the run's plan has no trash to send through the portals";
                return false;
            }

            var isOutdoor = run.Dungeon?.IsOutdoor == true;
            var placement = config.Placement;
            var candidates = Waves.PortalCandidates(request, placement, isOutdoor);

            // Every wave picks its own portals from these later; refuse now if the neediest wave could not.
            if (ThreadEncounterPortalWaves.PickPortals(request.Point.Global, request.Point.Landblock, candidates, isOutdoor, placement, new Random(request.Seed), "cache", out var refusal, DungeonWalkRatio, requireWalkDistance: true) == null)
            {
                failure = refusal;
                return false;
            }

            var wo = CreateObject(config.ChestWcid);

            if (wo == null || wo is Container || wo is Creature)
            {
                try { wo?.Destroy(); } catch { }
                failure = wo == null ? $"wcid {config.ChestWcid} did not create - is its weenie in the world database?" : $"wcid {config.ChestWcid} is not a plain usable object";
                return false;
            }

            wo.Location = request.PositionOf(request.Point.Point);
            wo.TimeToRot = -1;
            wo.IsTransientSpawn = true;

            var state = new GambleState(request, config, candidates) { ChestGuid = wo.Guid.Full, Chest = wo };
            request.Instance.State = state;

            // Before EnterWorld: a use can arrive the moment it is visible.
            request.Instance.Track(wo);

            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} gamble chest EnterWorld threw at {wo.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                wo.P_ThreadEncounter = null;
                try { wo.Destroy(); } catch { }
                request.Instance.State = null;
                failure = $"wcid {config.ChestWcid} failed to enter the world at {wo.Location?.ToLOCString()}";
                return false;
            }

            run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: chest {1} ({2}), {3} portal point(s) in reach, up to {4} enemies over {5} waves",
                request.Def?.Id, config.ChestWcid, wo.Name, candidates.Count, GambleConfig.MaxPossibleTotal(config.Waves), MaxWaves));

            return true;
        }

        public void OnRunEnded(ThreadEncounterInstance instance, string why)
        {
            if (instance?.State is GambleState state)
                state.Request = null;
        }

        internal static bool RunOver(ThreadDungeonRun run, GambleState state)
            => state?.Request == null || run == null || run.State == ThreadDungeonRunState.Ended || run.EncountersEnded;

        // ---- IThreadEncounterUsable ------------------------------------------------------------------------------

        /// <summary>Is <paramref name="used"/> the chest this state belongs to (not a portal layer or an enemy)? Pure.</summary>
        internal static bool IsTheChest(GambleState state, WorldObject used)
            => state != null && used != null && state.ChestGuid != 0 && used.Guid.Full == state.ChestGuid;

        public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)
        {
            if (!(instance?.State is GambleState state) || player == null || !IsTheChest(state, used))
                return;

            var run = instance.Run;

            switch (state.TryUse(RunOver(run, state), ThreadRunPresence.IsMemberInside(run, player), out var cleared, out var token))
            {
                case GambleUseOutcome.NotInside:
                    Send(player, RefusedLine);
                    return;
                case GambleUseOutcome.WaveInProgress:
                    Send(player, SealedLine);
                    return;
                case GambleUseOutcome.Offer:
                    OfferDeepen(instance, state, player, cleared, token);
                    return;
            }
        }

        /// <summary>The Deepen confirmation: Yes starts the next wave, an explicit No offers the Take, a timeout does nothing.</summary>
        private static void OfferDeepen(ThreadEncounterInstance instance, GambleState state, Player player, int cleared, int token)
        {
            var confirmation = new Confirmation_Choice(player.Guid,
                () => Deepen(instance, state, player, token),
                () => OfferTake(instance, state, player, token),
                DateTime.UtcNow);

            var wave = Math.Clamp(cleared + 1, 1, MaxWaves);

            if (!player.ConfirmationManager.EnqueueSend(confirmation, DeepenPrompt(wave, state.Config.Waves[wave - 1].Seconds)))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// The answer gate shared by Deepen and Take (on the answering player's own thread, Confirmation_Choice's hop): the run
        /// still on, the player still a member inside (silent otherwise), and within <see cref="AnswerRadius"/> of the chest
        /// (told <see cref="NotAtCacheLine"/> otherwise).
        /// </summary>
        private static bool MayAnswer(ThreadEncounterInstance instance, GambleState state, Player player)
        {
            var run = instance.Run;

            if (RunOver(run, state) || !ThreadRunPresence.IsMemberInside(run, player))
                return false;

            var chest = state.Chest;

            // No chest to stand at (gone, or never set): silent, not the misleading range line.
            if (chest == null || chest.IsDestroyed)
                return false;

            var distance = AnswerDistance(player.Location, chest.Location);

            if (!WithinAnswerRange(distance))
            {
                Send(player, NotAtCacheLine);
                return false;
            }

            return true;
        }

        /// <summary>The Take confirmation, only while nothing has happened since the Deepen was offered. Yes pays out; No and a timeout do nothing.</summary>
        private static void OfferTake(ThreadEncounterInstance instance, GambleState state, Player player, int token)
        {
            if (RunOver(instance.Run, state) || !state.IsReadyUnder(token))
                return;

            var confirmation = new Confirmation_Choice(player.Guid, () => Take(instance, state, player, token), null, DateTime.UtcNow);

            if (!player.ConfirmationManager.EnqueueSend(confirmation, TakePrompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>A Deepen Yes: the latch, the start line, then the wave on the chest's landblock.</summary>
        private static void Deepen(ThreadEncounterInstance instance, GambleState state, Player player, int token)
        {
            var run = instance.Run;

            if (!MayAnswer(instance, state, player))
                return;

            if (!state.TryDeepen(token, Now(), out var wave))
                return;

            SendAll(run, WaveStartLine(player.Name, wave, state.Config.Waves[wave - 1].Seconds));
            run.RecordEncounterOutcome($"{instance}: wave {Of(wave)} deepened by {player.Name}");
            log.Info($"[DYNDUNGEON] {run} {instance} gamble wave {wave} deepened by {player.Name}");

            OnChestQueue(state, 0, () => StartWave(instance, state, wave));
        }

        /// <summary>A Take Yes: the latch, then the payout with the banked waves on the chest's landblock.</summary>
        private static void Take(ThreadEncounterInstance instance, GambleState state, Player player, int token)
        {
            var run = instance.Run;

            if (!MayAnswer(instance, state, player))
                return;

            if (!state.TryTake(token, out var cleared))
                return;

            SendAll(run, TakeLine(player.Name));
            run.RecordEncounterOutcome($"{instance}: taken by {player.Name} at {Of(cleared)} wave(s) cleared");

            OnChestQueue(state, 0, () => PayAndClose(instance, state, cleared, "taken"), $"{instance}: the latched payout ({Of(cleared)} wave(s), taken by {player.Name})");
        }

        private static void Send(Player player, string line)
        {
            if (!string.IsNullOrEmpty(line))
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        private static void SendAll(ThreadDungeonRun run, string line)
        {
            foreach (var recipient in ThreadRunPresence.MessageRecipients(run))
                Send(recipient, line);
        }

        // ---- the waves, on the chest's landblock queue -----------------------------------------------------------

        /// <summary>
        /// Queues <paramref name="action"/> onto the chest's landblock (the request's), after <paramref name="delay"/> seconds,
        /// behind the run guard. Never throws out of the action. <paramref name="dropped"/> names a latched payout: when the
        /// guard drops it (the run ended in between, or there is no landblock to queue on) that is logged at Warn.
        /// </summary>
        private static void OnChestQueue(GambleState state, double delay, Action action, string dropped = null)
        {
            var landblock = state.Request?.Landblock;

            if (landblock == null)
            {
                if (dropped != null)
                    log.Warn($"[DYNDUNGEON] gamble {dropped} dropped: no chest landblock to queue on (the run ended)");

                return;
            }

            var chain = new ActionChain();

            if (delay > 0)
                chain.AddDelaySeconds(delay);

            chain.AddAction(landblock, () =>
            {
                var request = state.Request;

                if (request == null || RunOver(request.Run, state))
                {
                    if (dropped != null)
                        log.Warn($"[DYNDUNGEON] gamble {dropped} dropped: the run ended before it ran");

                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {request.Run} gamble action threw", ex);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>Chest landblock queue: wave <paramref name="wave"/>'s portals, its stream at each enemy's delay, and its first monitor tick.</summary>
        private static void StartWave(ThreadEncounterInstance instance, GambleState state, int wave)
        {
            var request = state.Request;

            if (request == null || !state.IsWaveRunning(wave))
                return;

            var run = instance.Run;
            var waveConfig = state.Config.Waves[wave - 1];
            var isOutdoor = run.Dungeon?.IsOutdoor == true;
            var rng = new Random(WaveSeed(request.Seed, wave));

            // Read once, here at the wave's start: the run's group count factor (the C_actual the health scaling uses).
            var factor = GroupCountFactor(run.IsGroup, run.GroupCountActual);
            var portals = ThreadEncounterPortalWaves.PickPortals(request.Point.Global, request.Point.Landblock, state.Candidates, isOutdoor, waveConfig, rng, "cache", out var refusal, DungeonWalkRatio, requireWalkDistance: true);
            var stream = portals == null ? new List<PortalWaveSpawn>() : PlanWaveStream(portals, request.Context?.Plan?.Entries, waveConfig, rng, factor, waveConfig.Seconds);

            if (portals == null || stream.Count == 0 || !state.BeginStream(wave, stream.Count))
            {
                // Place checked both, so this is ours, never the players': pay out what was banked.
                if (state.TryRefund(wave, out var banked))
                {
                    run.RecordEncounterOutcome($"{instance}: wave {Of(wave)} could not open ({refusal ?? "no enemies planned"}); refunded at {Of(banked)} wave(s) cleared");
                    log.Warn($"[DYNDUNGEON] {run} {instance} gamble wave {wave} could not open ({refusal ?? "no enemies planned"}); refunded");
                    PayAndClose(instance, state, banked, "refund");
                }

                return;
            }

            var opened = Waves.OpenPortalLayers(instance, request, request.Point.Global, portals, waveConfig.PortalScale(isOutdoor), isOutdoor, wo =>
            {
                lock (state.Gate)
                    state.PortalObjects.Add(wo);
            });

            run.RecordEncounterOutcome($"{instance}: wave {Of(wave)}: {portals.Count} portal(s) opened ({opened} layer(s)), {stream.Count} enemy(ies) (group factor {factor.ToString("0.###", CultureInfo.InvariantCulture)}), {waveConfig.Seconds.ToString("0", CultureInfo.InvariantCulture)} s");

            foreach (var spawn in stream)
                OnChestQueue(state, spawn.Delay, () => SpawnEnemy(instance, state, wave, spawn));

            OnChestQueue(state, MonitorSeconds, () => Tick(instance, state, wave));
        }

        /// <summary>Chest landblock queue: one planned enemy of wave <paramref name="wave"/> comes through, a bonus kill, and is engaged at once.</summary>
        private static void SpawnEnemy(ThreadEncounterInstance instance, GambleState state, int wave, PortalWaveSpawn spawn)
        {
            Creature placed = null;

            try
            {
                var request = state.Request;

                // The finally counts this planned enemy as done on every path, placed or not.
                if (request == null || !state.IsWaveRunning(wave) || spawn.Entry == null)
                    return;

                // Wave enemies are bonus kills: normal xp, luminance and loot, never counted toward the clear.
                placed = Waves.TrySpawnEnemy(request, spawn.Entry, excludedFromClear: true);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} gamble enemy threw", ex);
            }
            finally
            {
                state.SpawnDone(wave, placed);
            }

            // Engaged the moment it is through (Defense's rule); a miss stays idle for the monitor to retry.
            if (placed != null)
            {
                try
                {
                    ThreadEncounterPortalWaves.EngageEnemy(placed, instance.Run, out _);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {instance.Run} {instance} gamble enemy engage threw", ex);
                }
            }
        }

        /// <summary>Chest landblock queue, once a second while wave <paramref name="wave"/> runs: engage idle enemies, then the tick outcome. Reschedules itself until one.</summary>
        private static void Tick(ThreadEncounterInstance instance, GambleState state, int wave)
        {
            var run = instance.Run;
            var landblock = state.Request?.Landblock;

            if (!state.Snapshot(wave, Now(), out var streamDone, out var elapsed))
                return;

            List<GambleEnemy> alive;

            lock (state.Gate)
                alive = state.Enemies.Where(e => ThreadEncounterPortalWaves.IsStanding(e.Creature)).ToList();

            var now = Now();
            var seconds = state.Config.Waves[wave - 1].Seconds;

            foreach (var enemy in alive)
            {
                var creature = enemy.Creature;
                var current = creature.CurrentLandblock;

                if (current == null)
                    continue;

                if (creature.MonsterState == Creature.State.Idle)
                {
                    enemy.Mark = null;

                    if (ReferenceEquals(current, landblock))
                        ThreadEncounterPortalWaves.EngageEnemy(creature, run, out _);
                    else
                        new ActionChain(creature, () => ThreadEncounterPortalWaves.EngageEnemy(creature, run, out _)).EnqueueChain();

                    continue;
                }

                // The stuck rescue: only on this queue's own landblock (FakeTeleport is same-landblock only).
                if (ReferenceEquals(current, landblock))
                    RescueIfStuck(instance, state, enemy, now);
            }

            var warning = state.TryLatchWarning(wave, elapsed, seconds);

            if (warning > 0)
                SendAll(run, WarningLine(warning));

            var anyAlive = AnyAlive(MembersInside(run), p => p.IsDead);
            var outcome = DecideTick(anyAlive, streamDone, alive.Count, elapsed, seconds);

            switch (outcome)
            {
                case GambleTickOutcome.Cleared:
                    ClearWave(instance, state, wave);
                    return;
                case GambleTickOutcome.BustTimer:
                case GambleTickOutcome.BustNobody:
                    Bust(instance, state, wave, outcome, alive.Count);
                    return;
            }

            OnChestQueue(state, MonitorSeconds, () => Tick(instance, state, wave));
        }

        /// <summary>
        /// Chest landblock queue, for an awake enemy on the chest's landblock: one <see cref="StuckStep"/> against its attack
        /// target; when due, the enemy is moved to the chest's point (Creature.FakeTeleport, same landblock only), so an enemy
        /// that cannot reach the players does not run out the wave's clock.
        /// </summary>
        private static void RescueIfStuck(ThreadEncounterInstance instance, GambleState state, GambleEnemy enemy, double now)
        {
            var creature = enemy.Creature;
            var target = creature.AttackTarget;
            var request = state.Request;

            if (target?.PhysicsObj == null || creature.PhysicsObj == null || request == null)
            {
                enemy.Mark = null;
                return;
            }

            var chestAt = request.PositionOf(request.Point.Point);
            var distance = Math.Sqrt(creature.PhysicsObj.get_distance_sq_to_object(target.PhysicsObj, true));
            var toChest = creature.Location == null ? double.MaxValue : creature.Location.DistanceTo(chestAt);
            var attacked = AttackedRecently(creature.PrevAttackTime, Timers.RunningTime);
            var (rescue, mark) = StuckStep(enemy.Mark, target.Guid.Full, distance, toChest, now, attacked);
            enemy.Mark = mark;

            if (!rescue)
                return;

            creature.FakeTeleport(chestAt);

            if (log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] {instance.Run} {instance} gamble enemy {creature.Name} 0x{creature.Guid.Full:X8} made no headway on {target.Name} in {StuckSeconds:0} s; moved to the chest");
        }

        /// <summary>Chest landblock queue: wave cleared. Collapses its portals; the fourth pays out by itself, any other says the cleared line.</summary>
        private static void ClearWave(ThreadEncounterInstance instance, GambleState state, int wave)
        {
            if (!state.TryClearWave(wave, out var autoPay, out var cleared))
                return;

            CollapsePortals(instance, state);

            var run = instance.Run;
            run.RecordEncounterOutcome($"{instance}: wave {Of(wave)} cleared");

            if (autoPay)
            {
                PayAndClose(instance, state, cleared, "every wave cleared");
                return;
            }

            SendAll(run, WaveClearedLine(wave));
        }

        /// <summary>Chest landblock queue: the bust. Nothing is paid; leftover enemies stay as ordinary bonus mobs.</summary>
        private static void Bust(ThreadEncounterInstance instance, GambleState state, int wave, GambleTickOutcome why, int enemiesLeft)
        {
            if (!state.TryBust(wave))
                return;

            CollapsePortals(instance, state);

            var run = instance.Run;
            SendAll(run, why == GambleTickOutcome.BustTimer ? BustTimerLine : BustNobodyLine);

            var reason = why == GambleTickOutcome.BustTimer ? "the timer ran out" : "nobody alive inside";
            run.RecordEncounterOutcome($"{instance}: Bust on wave {Of(wave)} ({reason}; {enemiesLeft} enemy(ies) still standing)");
            log.Info($"[DYNDUNGEON] {run} {instance} gamble bust on wave {wave} ({reason}), {enemiesLeft} enemy(ies) still standing");

            DestroyChestLater(instance, state);
        }

        /// <summary>
        /// THE payout, once per cache (behind a won Take, clear or refund latch). Every online member inside the run at
        /// this moment gets an INDEPENDENT prize roll (one ThreadEncounterPrize.Grant each, the Defense model), grown by
        /// <paramref name="wavesCleared"/> waves of gamble growth. Then the chest goes.
        /// </summary>
        internal static void PayAndClose(ThreadEncounterInstance instance, GambleState state, int wavesCleared, string how)
        {
            var run = instance.Run;
            var granted = GrantToAll(MembersInside(run), winner => GrantPrize(run, winner, KindName, wavesCleared));

            run.RecordEncounterOutcome($"{instance}: Paid ({how}, {Of(wavesCleared)} wave(s) cleared); {granted} prize(s) granted");
            log.Info($"[DYNDUNGEON] {run} {instance} gamble paid ({how}, {wavesCleared} wave(s) cleared); {granted} prize(s) granted");

            DestroyChestLater(instance, state);
        }

        private static void CollapsePortals(ThreadEncounterInstance instance, GambleState state)
        {
            foreach (var portal in state.TakePortals())
            {
                try
                {
                    if (!portal.IsDestroyed)
                        portal.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {instance.Run} {instance} portal destroy threw", ex);
                }
            }
        }

        private static void DestroyChestLater(ThreadEncounterInstance instance, GambleState state)
        {
            var chest = state.Chest;

            if (chest == null)
                return;

            OnChestQueue(state, ChestDestroyDelaySeconds, () =>
            {
                if (!chest.IsDestroyed)
                    chest.Destroy();
            });
        }
    }
}
