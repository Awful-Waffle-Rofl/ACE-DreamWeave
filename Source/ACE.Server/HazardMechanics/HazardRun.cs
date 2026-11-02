using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Threading;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// One live hazard run: what was asked for (kind, pattern, region, merged args, seed, host) and its lifecycle.
    /// Returned by <see cref="HazardManager.Start(HazardRegion, string, HazardArgs, IHazardHost, int?)"/>; the
    /// caller keeps it to <see cref="Stop"/> the run.
    ///
    /// THREADING. Identity fields are immutable. <see cref="State"/>/<see cref="Outcome"/> change only under
    /// <see cref="Sync"/>. The owned-object list, the schedule and the hit ledger live in the run's
    /// <see cref="HazardContext"/> and are touched ONLY on the landblock thread.
    /// </summary>
    public sealed class HazardRun
    {
        internal readonly object Sync = new object();

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        internal HazardRun(int id, string kind, string pattern, HazardRegion region, HazardArgs args, int seed, IHazardHost host,
            IHazardMechanic mechanic, Landblock landblock, DateTime queuedUtc)
        {
            Id = id;
            Kind = kind;
            Pattern = pattern;
            Region = region;
            Args = args ?? HazardArgs.Empty;
            Seed = seed;
            Host = host ?? NullHazardHost.Instance;
            Mechanic = mechanic;
            Landblock = landblock;
            QueuedUtc = queuedUtc;
            Context = new HazardContext(this);
        }

        public int Id { get; }

        public string Kind { get; }

        /// <summary>The resolved pattern (a member of the mechanic's Patterns), or null when the mechanic has none.</summary>
        public string Pattern { get; }

        public HazardRegion Region { get; }

        /// <summary>The caller's args laid over the mechanic's settings string - the numbers this run actually uses.</summary>
        public HazardArgs Args { get; }

        public int Seed { get; }

        public IHazardHost Host { get; }

        public DateTime QueuedUtc { get; }

        internal IHazardMechanic Mechanic { get; }

        /// <summary>The landblock copy this run lives on. Null only for registry tests.</summary>
        internal Landblock Landblock { get; }

        internal HazardContext Context { get; }

        public HazardRunState State { get; private set; } = HazardRunState.Pending;

        public HazardOutcome? Outcome { get; private set; }

        public string EndReason { get; private set; }

        public bool IsEnded => State == HazardRunState.Ended;

        private long lastTickTicks;

        /// <summary>
        /// Wall time of the driver's last tick of this run (null before the first). Written on the landblock thread,
        /// read by the reaper on the world/command thread: stored as one long with Volatile access so the read is
        /// never torn or stale-cached.
        /// </summary>
        internal DateTime? LastTickUtc
        {
            get
            {
                var ticks = Volatile.Read(ref lastTickTicks);
                return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
            set => Volatile.Write(ref lastTickTicks, value?.Ticks ?? 0);
        }

        private int liveCount = NoLiveCount;

        private const int NoLiveCount = int.MinValue;

        /// <summary>
        /// A caller-driven count the mechanic reads at its own decision points (a world event ramps it over the
        /// event). Null, the default, means the mechanic uses its args exactly as before. Volatile-backed: written
        /// by <see cref="SetLiveCount"/> from any thread, read on the landblock thread.
        /// </summary>
        public int? LiveCount
        {
            get
            {
                var v = Volatile.Read(ref liveCount);
                return v == NoLiveCount ? (int?)null : v;
            }
        }

        /// <summary>Sets (or, with null, clears) <see cref="LiveCount"/>. Any thread.</summary>
        public void SetLiveCount(int? count) => Volatile.Write(ref liveCount, count ?? NoLiveCount);

        private readonly ConcurrentQueue<HazardControl> controls = new ConcurrentQueue<HazardControl>();

        private int pendingControls;

        /// <summary>Controls queued and not yet drained (tests and /hazard list).</summary>
        internal int PendingControlCount => Volatile.Read(ref pendingControls);

        /// <summary>
        /// Queues a runtime control (a key and a number) for the mechanic to read on its next tick. Any thread. The key
        /// is trimmed and lower-cased. False, and nothing queued, when the run has ended, the key is blank, the value is
        /// not finite, or <see cref="HazardDefaults.MaxPendingControls"/> controls are already waiting.
        /// </summary>
        public bool Control(string key, double value)
        {
            if (IsEnded || string.IsNullOrWhiteSpace(key) || !double.IsFinite(value))
                return false;

            if (Interlocked.Increment(ref pendingControls) > HazardDefaults.MaxPendingControls)
            {
                Interlocked.Decrement(ref pendingControls);
                return false;
            }

            controls.Enqueue(new HazardControl(key.Trim().ToLowerInvariant(), value));
            return true;
        }

        /// <summary>Drains the inbox in arrival order. Landblock thread (via <see cref="HazardContext.TakeControls"/>).</summary>
        internal IReadOnlyList<HazardControl> DrainControls()
        {
            if (controls.IsEmpty)
                return Array.Empty<HazardControl>();

            var taken = new List<HazardControl>();

            while (controls.TryDequeue(out var c))
            {
                taken.Add(c);
                Interlocked.Decrement(ref pendingControls);
            }

            return taken;
        }

        /// <summary>Monotonic seconds at Start; ctx.Now is measured from here.</summary>
        internal double StartedAtSeconds { get; private set; }

        internal static double MonotonicSeconds => Clock.Elapsed.TotalSeconds;

        /// <summary>Stops the run (Stopped). Idempotent; safe from any thread.</summary>
        public bool Stop(string reason = "stopped") => HazardManager.Stop(this, HazardOutcome.Stopped, reason);

        public string LogPrefix => string.Format(CultureInfo.InvariantCulture, "[HAZARD] lb=0x{0:X4} inst=0x{1:X8} run=#{2} kind={3}",
            Region?.Landblock ?? 0, Region?.Instance ?? 0, Id, Kind);

        /// <summary>Pending -> Running. False if the run already ended (a Stop raced the first tick).</summary>
        internal bool TryMarkRunning()
        {
            lock (Sync)
            {
                if (State != HazardRunState.Pending)
                    return false;

                State = HazardRunState.Running;
                StartedAtSeconds = MonotonicSeconds;
                return true;
            }
        }

        /// <summary>
        /// The ONE gate every end path passes: true for exactly one caller, which then owns the teardown and the
        /// OnEnded notification. Every later caller gets false and does nothing.
        /// </summary>
        internal bool TryClaimEnd(HazardOutcome outcome, string reason, out bool wasStarted)
        {
            lock (Sync)
            {
                wasStarted = State == HazardRunState.Running;

                if (State == HazardRunState.Ended)
                    return false;

                State = HazardRunState.Ended;
                Outcome = outcome;
                EndReason = reason;
                return true;
            }
        }

        public string Summary()
        {
            var now = Context.Now;
            var pattern = Pattern != null ? $" pattern={Pattern}" : "";
            string detail;

            try
            {
                detail = Mechanic?.Describe(Context) ?? "";
            }
            catch (Exception ex)
            {
                detail = "describe threw " + ex.GetType().Name;
            }

            return string.Format(CultureInfo.InvariantCulture, "#{0} {1}{2} {3} t={4:0.0}s objects={5} seed={6} region={7} | {8}",
                Id, Kind, pattern, State.ToString().ToLowerInvariant(), now, Context.OwnedCount, Seed, Region?.Name, detail);
        }
    }

    /// <summary>One runtime control sent to a run: a lower-case key and a finite number (<see cref="HazardRun.Control"/>).</summary>
    public readonly struct HazardControl
    {
        public HazardControl(string key, double value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }

        public double Value { get; }
    }

    /// <summary>What to spawn: <see cref="HazardContext.Spawn"/>'s one argument.</summary>
    public sealed class HazardSpawnSpec
    {
        public uint Wcid { get; set; }

        /// <summary>Landblock-local position. Raised in 0.25 m steps (up to 1.5 m) when no cell holds it.</summary>
        public Vector3 Position { get; set; }

        /// <summary>Heading in degrees (0 = the client's north, counter-clockwise). Ignored when <see cref="Rotation"/> is set.</summary>
        public float YawDeg { get; set; }

        /// <summary>A full orientation (an aimed beam host). Null: built from <see cref="YawDeg"/>.</summary>
        public Quaternion? Rotation { get; set; }

        /// <summary>Multiplier on the weenie's own scale. 1 = as authored.</summary>
        public float Scale { get; set; } = 1f;

        /// <summary>
        /// Translucency to stamp before EnterWorld (0 = opaque, 1 = invisible). Null, NaN or under 0.001 leaves the
        /// property unset (the networking layer sends nothing below that anyway).
        /// </summary>
        public float? Translucency { get; set; }

        /// <summary>VisualEffectScript to stamp before EnterWorld (replayed to every client that later creates it). 0 = none.</summary>
        public uint Script { get; set; }

        /// <summary>
        /// True for anything that will MoveLeg: the framework strips a Static physics flag, forces gravity off and
        /// ethereal/ignore-collisions before EnterWorld, so the object can move client-side.
        /// </summary>
        public bool Moving { get; set; }

        /// <summary>
        /// May the cell search raise the point (up to 1.5 m) out of a solid? Null: true for stationary objects, false
        /// for <see cref="Moving"/> ones (HazardGround.TryResolveCell's raise rule).
        /// </summary>
        public bool? AllowRaise { get; set; }

        /// <summary>
        /// The origin stands ON THE FLOOR (a floor marker or floor beam host). A grounded host whose <see cref="Rotation"/>
        /// turns its collision spheres below the origin is raised by HazardGround.TurnedHostLift before its cell is
        /// resolved, or the server refuses it. No effect for an unturned host (lift 0).
        /// </summary>
        public bool Grounded { get; set; }

        /// <summary>Hit radius the mechanic wants to remember on the handle (informational; HitPlayers takes its own radius).</summary>
        public float HitRadius { get; set; }

        /// <summary>A free-form label for logs and Describe.</summary>
        public string Tag { get; set; }
    }

    /// <summary>
    /// A spawned object owned by a run. Its POSITION FOR DAMAGE is <see cref="PositionAt"/> - the computed leg
    /// position while moving, the last snap point at rest - never the WorldObject's Location.
    /// Landblock thread only.
    /// </summary>
    public sealed class HazardHandle
    {
        internal HazardHandle(int id, WorldObject wo, HazardSpawnSpec spec, Vector3 position)
        {
            Id = id;
            Object = wo;
            Wcid = spec.Wcid;
            Tag = spec.Tag;
            HitRadius = spec.HitRadius;
            Moving = spec.Moving;
            Rest = position;
        }

        /// <summary>Unique within its run; the hit ledger's per-hazard key.</summary>
        public int Id { get; }

        public WorldObject Object { get; }

        public uint Wcid { get; }

        public string Tag { get; }

        public float HitRadius { get; }

        public bool Moving { get; }

        /// <summary>Where the hazard is when not moving (its spawn point, then each snap point).</summary>
        public Vector3 Rest { get; internal set; }

        /// <summary>The leg in flight, or null at rest.</summary>
        public HazardPath Path { get; internal set; }

        public bool IsMoving => Path != null;

        public bool Alive => Object != null && !Object.IsDestroyed;

        /// <summary>Run-relative time the current leg ends, or null at rest.</summary>
        public double? LegEndTime => Path?.EndTime;

        /// <summary>The driver tick the object was spawned on; MoveLeg in that same tick is deferred one tick.</summary>
        internal long SpawnTick { get; set; } = -1;

        /// <summary>Path is read ONCE (the command thread's /hazard list can call this while the driver swaps it).</summary>
        public Vector3 PositionAt(double now)
        {
            var path = Path;
            return path != null ? path.PositionAt(now) : Rest;
        }
    }
}
