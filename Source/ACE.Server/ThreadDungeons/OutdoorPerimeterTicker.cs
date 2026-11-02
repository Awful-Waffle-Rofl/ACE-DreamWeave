using System;
using System.Collections.Generic;

using log4net;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>Player-facing perimeter lines (Open World Threads R16-R20), keyed by the rules' message keys.</summary>
    public static class OutdoorPerimeterText
    {
        public const string Warn = "The Thread thins here. Turn back toward its heart.";
        public const string FirstDamage = "The Thread unravels around you!";
        public const string SnapBack = "The Thread pulls you back to where you entered.";
        public const string Backstop = "You have strayed beyond the Thread. It pulls you back.";

        /// <summary>The line for a rules message key; null for an unknown key.</summary>
        public static string For(string messageKey)
        {
            switch (messageKey)
            {
                case OutdoorPerimeterRules.MsgWarnBand: return Warn;
                case OutdoorPerimeterRules.MsgDamage: return FirstDamage;
                case OutdoorPerimeterRules.MsgSnapBack: return SnapBack;
                case OutdoorPerimeterRules.MsgBackstop: return Backstop;
                default: return null;
            }
        }
    }

    /// <summary>
    /// One player as the ticker sees them: the observations <see cref="OutdoorPerimeterRules.Tick"/> needs, plus the
    /// primitives the ticker applies. Production wraps a Player (<see cref="PlayerPerimeterSubject"/>); tests use fakes
    /// (a Player cannot be constructed in the unit-test harness). Every primitive is called ONLY from inside an action
    /// handed to <see cref="RunOnOwnQueue"/>.
    /// </summary>
    public interface IPerimeterSubject
    {
        uint Guid { get; }
        OutdoorPos Position { get; }
        bool IsTeleporting { get; }
        bool IsDead { get; }
        bool IsLoggingOut { get; }
        double SecondsSinceArrival { get; }
        double SecondsSinceLastInbound { get; }
        long Health { get; }
        long MaxHealth { get; }

        /// <summary>Enqueues <paramref name="action"/> on the player's OWN landblock action queue (never another landblock's). False when the player has no landblock to run on.</summary>
        bool RunOnOwnQueue(Action action);

        /// <summary>Re-checked inside the queued action: still in the run's copy, alive, not teleporting, not logging out.</summary>
        bool StillApplicable { get; }

        /// <summary>
        /// A raw vital write: no DamageEvent, armour and resists ignored. <paramref name="lethal"/> is the lethal-mode intent:
        /// true writes the full amount and, when it would kill, latches the death-cause marker (an Unraveling death spends no
        /// entry); false re-reads health HERE (the verdict is a tick old) and clamps the write so the player keeps at least 1 HP.
        /// An invincible player takes no damage.
        /// </summary>
        void WriteDamage(long amount, bool lethal);

        /// <summary>Snap-back health: SETS health to 1 if it is above 1 (re-read here), never raises it.</summary>
        void SetHealthToOne();

        /// <summary>Teleports to the site's entry position in the run's own instance.</summary>
        void TeleportToDrop();

        void Say(string text);
    }

    /// <summary>One live outdoor run as the ticker sees it. <see cref="PlaceRing"/> returns true once the ring needs no further attempts.</summary>
    public sealed class PerimeterRunView
    {
        public uint RunId { get; set; }
        public OutdoorCircleDef Circle { get; set; }
        public IReadOnlyCollection<ushort> Footprint { get; set; }
        public Func<IReadOnlyList<IPerimeterSubject>> Subjects { get; set; }

        /// <summary>The per-player wisp pulse (on WarnEnteredBand). May be null.</summary>
        public Action<IPerimeterSubject> Pulse { get; set; }

        /// <summary>Attempts the ring placement; true = done (placed, or disabled). May be null.</summary>
        public Func<bool> PlaceRing { get; set; }
    }

    /// <summary>One closed cadence window: the report text and how many of its ticks exceeded the staleness limit.</summary>
    public sealed class CadenceReport
    {
        public CadenceReport(string text, int staleTicks) { Text = text; StaleTicks = staleTicks; }
        public string Text { get; }
        public int StaleTicks { get; }
    }

    /// <summary>
    /// Idle packet cadence telemetry (design note: the 2 s staleness rule is trusted only once idle players are seen
    /// to keep sending). Per player, per window: how many ticks, the largest SecondsSinceLastInbound seen and how many
    /// ticks exceeded the staleness limit. One report string per window, rate-limited by construction.
    /// </summary>
    public sealed class InboundCadenceTracker
    {
        public const double WindowSeconds = 30.0;

        private sealed class Window
        {
            public double Start;
            public int Samples;
            public int Stale;
            public double Max;
        }

        private readonly Dictionary<uint, Window> windows = new Dictionary<uint, Window>();

        /// <summary>Records a sample; returns a report line when a window just closed, else null.</summary>
        public CadenceReport Observe(uint guid, double now, double sinceInboundS)
        {
            if (!windows.TryGetValue(guid, out var w))
            {
                windows[guid] = new Window { Start = now, Samples = 1, Max = sinceInboundS, Stale = sinceInboundS > OutdoorPerimeterSettings.InboundStalenessS ? 1 : 0 };
                return null;
            }

            if (now - w.Start >= WindowSeconds)
            {
                var report = new CadenceReport($"[OWT] inbound cadence player=0x{guid:X8} window={now - w.Start:F0}s samples={w.Samples} maxSinceInboundS={w.Max:F2} staleTicks={w.Stale}", w.Stale);
                w.Start = now;
                w.Samples = 1;
                w.Max = sinceInboundS;
                w.Stale = sinceInboundS > OutdoorPerimeterSettings.InboundStalenessS ? 1 : 0;
                return report;
            }

            w.Samples++;
            if (sinceInboundS > w.Max) w.Max = sinceInboundS;
            if (sinceInboundS > OutdoorPerimeterSettings.InboundStalenessS) w.Stale++;
            return null;
        }

        public void Forget(uint guid) => windows.Remove(guid);
    }

    /// <summary>
    /// Open World Threads WP4b: drives <see cref="OutdoorPerimeterRules"/> for every live outdoor run, once per
    /// second, and applies each verdict on the player's own landblock queue. This class owns the per-(run, player)
    /// <see cref="PlayerPerimeterState"/> and never re-implements a rule.
    ///
    /// State lifecycle: created on a player's first tick in a run (always built explicitly, never default); persisted from
    /// every result; dropped when the player is no longer a subject of the run (exit, logout, left the instance) so
    /// re-entry starts fresh; dropped for every player of a run that is no longer live. A run that ended is simply
    /// absent from the next pass, which prunes it - there is no end hook.
    /// </summary>
    public sealed class OutdoorPerimeterTicker
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private sealed class Record
        {
            public PlayerPerimeterState State;
            public bool InExcursion;
            public double LastWarnAt = double.NegativeInfinity;
        }

        private readonly object gate = new object();
        private readonly Dictionary<(uint Run, uint Guid), Record> records = new Dictionary<(uint, uint), Record>();
        private readonly HashSet<uint> ringDone = new HashSet<uint>();
        private readonly InboundCadenceTracker cadence = new InboundCadenceTracker();

        /// <summary>Where cadence report lines go (debug level in production). Null drops them.</summary>
        public Action<string> CadenceLog { get; set; } = line => log.Debug(line);

        /// <summary>Where a cadence window that saw stale ticks goes (info level in production): those are the lines that matter. Null drops them.</summary>
        public Action<string> CadenceStaleLog { get; set; } = line => log.Info(line);

        /// <summary>Minimum seconds between warn lines (and pulses) for one player, so band-edge oscillation does not spam.</summary>
        public const double WarnCooldownS = 10.0;

        /// <summary>Tracked (run, player) records, for tests and the admin readout.</summary>
        public int RecordCount { get { lock (gate) return records.Count; } }

        public int RingDoneCount { get { lock (gate) return ringDone.Count; } }

        /// <summary>The production instance, ticked from ThreadDungeonManager.Tick.</summary>
        public static readonly OutdoorPerimeterTicker Live = new OutdoorPerimeterTicker();

        /// <summary>Production entry (the ONE call line in ThreadDungeonManager.Tick). Never throws.</summary>
        public static void Tick(IEnumerable<ThreadDungeonRun> runs)
        {
            try
            {
                var views = OutdoorPerimeterLive.BuildViews(runs);
                // Tunables are read once per pass, and not at all while no outdoor run is live (the common case).
                Live.TickPass(views, ACE.Server.Entity.Timers.RunningTime, views.Count > 0 ? OutdoorPerimeterSettings.FromTunables() : null);

                var liveIds = new HashSet<uint>();
                foreach (var view in views)
                    liveIds.Add(view.RunId);
                OutdoorWispRing.Prune(liveIds);
            }
            catch (Exception ex)
            {
                log.Error("[OWT] perimeter tick pass threw", ex);
            }
        }

        /// <summary>
        /// One pass over every live outdoor run. <paramref name="settings"/> is read once per pass by the caller.
        /// A run absent from <paramref name="runs"/> has its records and ring latch dropped.
        /// </summary>
        public void TickPass(IReadOnlyList<PerimeterRunView> runs, double nowSeconds, OutdoorPerimeterSettings settings)
        {
            var seen = new HashSet<uint>();

            if (runs != null)
            {
                foreach (var run in runs)
                {
                    if (run == null)
                        continue;

                    seen.Add(run.RunId);

                    try
                    {
                        TickRun(run, nowSeconds, settings);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[OWT] perimeter tick for run {run.RunId} threw", ex);
                    }
                }
            }

            Prune(seen);
        }

        private void Prune(HashSet<uint> liveRuns)
        {
            lock (gate)
            {
                List<(uint, uint)> dead = null;
                foreach (var key in records.Keys)
                {
                    if (!liveRuns.Contains(key.Run))
                        (dead ?? (dead = new List<(uint, uint)>())).Add(key);
                }

                if (dead != null)
                {
                    foreach (var key in dead)
                    {
                        records.Remove(key);
                        cadence.Forget(key.Item2);
                    }
                }

                ringDone.RemoveWhere(id => !liveRuns.Contains(id));
            }
        }

        private void TickRun(PerimeterRunView run, double now, OutdoorPerimeterSettings s)
        {
            if (run.PlaceRing != null)
            {
                bool latched;
                lock (gate) latched = ringDone.Contains(run.RunId);

                if (!latched)
                {
                    bool done;
                    try { done = run.PlaceRing(); }
                    catch (Exception ex) { log.Error($"[OWT] run {run.RunId} wisp ring placement threw; not retried", ex); done = true; }

                    if (done)
                        lock (gate) ringDone.Add(run.RunId);
                }
            }

            var subjects = run.Subjects?.Invoke() ?? Array.Empty<IPerimeterSubject>();
            var present = new HashSet<uint>();
            var centre = OutdoorSiteGeometry.Centre(run.Circle);

            foreach (var subject in subjects)
            {
                if (subject == null)
                    continue;

                present.Add(subject.Guid);

                Record rec;
                lock (gate)
                {
                    if (!records.TryGetValue((run.RunId, subject.Guid), out rec))
                    {
                        // Explicit construction: default(PlayerPerimeterState) would rate-limit a first tick at NowSeconds below 0.9.
                        rec = new Record { State = new PlayerPerimeterState(0, now, false, double.NegativeInfinity), InExcursion = false };
                        records[(run.RunId, subject.Guid)] = rec;
                    }
                }

                var dist = OutdoorSiteGeometry.HorizontalDistance(subject.Position, centre);
                var onFootprintOrBuffer = OutdoorFootprintBuffer.Contains(subject.Position.Landblock, run.Footprint);
                var zone = OutdoorPerimeterRules.Classify(dist, run.Circle.Radius, onFootprintOrBuffer, s);

                var input = new PlayerTickInput(zone, subject.IsTeleporting, subject.IsDead, subject.IsLoggingOut,
                    subject.SecondsSinceArrival, subject.SecondsSinceLastInbound, subject.Health, subject.MaxHealth, now);

                var result = OutdoorPerimeterRules.Tick(rec.State, input, s);
                rec.State = result.State;

                var report = cadence.Observe(subject.Guid, now, subject.SecondsSinceLastInbound);
                if (report != null)
                {
                    if (report.StaleTicks > 0)
                        CadenceStaleLog?.Invoke(report.Text);
                    else
                        CadenceLog?.Invoke(report.Text);
                }

                Dispatch(run, subject, rec, zone, result, s, now);
            }

            lock (gate)
            {
                List<(uint, uint)> gone = null;
                foreach (var key in records.Keys)
                {
                    if (key.Run == run.RunId && !present.Contains(key.Guid))
                        (gone ?? (gone = new List<(uint, uint)>())).Add(key);
                }

                if (gone != null)
                {
                    foreach (var key in gone)
                    {
                        records.Remove(key);
                        cadence.Forget(key.Item2);
                    }
                }
            }
        }

        private static void Dispatch(PerimeterRunView run, IPerimeterSubject subject, Record rec, PerimeterZone zone, PlayerTickResult result, OutdoorPerimeterSettings s, double now)
        {
            switch (result.Action)
            {
                case PerimeterAction.Damage:
                {
                    var first = !rec.InExcursion;
                    rec.InExcursion = true;
                    var amount = result.Damage;
                    var lethal = s.Lethal;
                    Queue(subject, "damage", () =>
                    {
                        subject.WriteDamage(amount, lethal);
                        if (first)
                            subject.Say(OutdoorPerimeterText.FirstDamage);
                    });
                    break;
                }

                case PerimeterAction.SnapBackToDrop:
                {
                    rec.InExcursion = false;
                    var amount = result.Damage;
                    var text = OutdoorPerimeterText.For(result.MessageKey);
                    Queue(subject, "snap-back", () =>
                    {
                        if (amount > 0)
                            subject.SetHealthToOne();
                        subject.TeleportToDrop();
                        if (text != null)
                            subject.Say(text);
                    });
                    break;
                }

                case PerimeterAction.WarnEnteredBand:
                {
                    // Band-edge oscillation re-arms the rules' once-per-visit warning every other second: rate-limit it here.
                    if (now - rec.LastWarnAt < WarnCooldownS)
                        break;

                    rec.LastWarnAt = now;
                    var pulse = run.Pulse;
                    Queue(subject, "warn", () =>
                    {
                        subject.Say(OutdoorPerimeterText.Warn);
                        pulse?.Invoke(subject);
                    });
                    break;
                }

                default:
                    if (zone == PerimeterZone.Inside || zone == PerimeterZone.Warning)
                        rec.InExcursion = false;
                    break;
            }
        }

        private static void Queue(IPerimeterSubject subject, string what, Action action)
        {
            subject.RunOnOwnQueue(() =>
            {
                try
                {
                    // The verdict is a second old by now: never mutate a player who died, started a teleport or left meanwhile.
                    if (!subject.StillApplicable)
                        return;

                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[OWT] perimeter {what} for 0x{subject.Guid:X8} threw", ex);
                }
            });
        }
    }
}
