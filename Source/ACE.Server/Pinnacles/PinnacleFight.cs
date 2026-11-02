using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Realms;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// One Pinnacle attempt: the lifecycle (NotStarted -> Running -> Held -> Running ... -> Won | Lost), the hold,
    /// the seal, the hazard host side with its one-retry policy, phase-scoped adds and timers, and the single
    /// cleanup path. A boss script drives it through <see cref="IPinnacleFightPort"/>; the world is behind
    /// <see cref="IPinnacleWorld"/>.
    ///
    /// WON HAS TWO STAGES, both with State = Won and the copy kept by the hold (full hold + margin):
    ///   - AWAITING DELIVERY: the cache is owed (the owner was absent or dead at the kill, or placement failed). It is
    ///     retried every tick while the owner is present. Hold expiry with the cache never delivered ends the attempt
    ///     Lost WITHOUT reporting a loss and REFUNDS the entry the head talk charged: an unpaid win never costs the Knot.
    ///   - COLLECTING: the cache has been placed, and a click-to-use return portal is summoned beside the owner (a failed
    ///     summon is only logged). Using it is a walk-out like any other. The fight stays alive so a death beside the cache does not lose it:
    ///     the owner may Knot back in free, and the Knot is told the boss died only when collecting ends - when the owner
    ///     is seen alive OUTSIDE the copy without having died or logged out since they were last in it (they walked
    ///     out), or at hold expiry (measured from the delivery). Either way the end is Won and ReportBossKilled runs once.
    ///
    /// THREADING. Every member runs on the fight's landblock thread EXCEPT <see cref="ArmResume"/> (any thread; a
    /// Volatile flag) and the read-only status getters (<see cref="State"/>, <see cref="AwaitingDelivery"/>,
    /// <see cref="Collecting"/>, <see cref="IsEnded"/>, <see cref="UnloadIntervalOverride"/>), which other threads may
    /// read for routing. No lock is taken: the live adapter marshals OnHazardEnded, OwnerDied and Forfeit onto the
    /// landblock before calling in. Hold writes the armed flag BEFORE publishing State = Held, so an ArmResume made by
    /// anyone who saw Held is never erased by that hold. The cache delivery publishes Collecting BEFORE it clears
    /// AwaitingDelivery, so a router never sees a Won fight with neither flag set (which would read as ended).
    ///
    /// HOLD. Enemies stop fighting while Held (SetPeaceful on the head and every live add) and are restored only at
    /// the resume, AFTER the grace: a returning owner is not attacked while the grace runs.
    ///
    /// CLOCKS. The monotonic <c>now</c> passed to Tick drives the hold and the resume grace. The fight clock
    /// (<see cref="Now"/>) advances only while Running and drives script timers, so it is frozen while Held.
    ///
    /// EVENTS. Add deaths, final hazard outcomes and timers are queued and delivered in order on the Running tick,
    /// stamped with the phase epoch they were raised in; an event raised in a phase that has since been left is
    /// dropped. One drain delivers at most the events queued when it began, so a script that raises an event from
    /// its own handler (a respawn that fails again) waits a tick instead of spinning. Nothing is delivered while
    /// Held: the queue drains at resume, so a kill or a completion that happened during the hold stands.
    ///
    /// FAULTS. Every entry point runs inside <see cref="Guard"/>: a throw from the script or the world is logged and
    /// ends the attempt Lost ("fault in &lt;what&gt;") through the normal cleanup. A fault end never reports the
    /// attempt lost to the Knot (the entry already spent stays spent, but the Knot is never consumed by a bug).
    /// End itself runs every cleanup step in its own try/catch, so one failing step never skips the release.
    /// </summary>
    public sealed class PinnacleFight : IPinnacleFightPort, IEphemeralHold
    {
        private const string FaultPrefix = "fault in ";

        private readonly IPinnacleBossScript script;
        private readonly IPinnacleWorld world;
        private readonly PinnacleFightConfig cfg;
        private readonly Random rng;

        // ---- status (read cross-thread) ----
        private int state = (int)PinnacleFightState.NotStarted;
        private volatile bool awaitingDelivery;
        private volatile bool collecting;
        private volatile bool isEnded;
        private int armed;

        // ---- won ----
        private bool cacheDelivered;
        private bool collectReturnAllowed;   // collecting: the owner died or logged out since last seen in the copy

        // ---- clocks ----
        private double clock;          // fight clock; advances only while Running
        private double lastClockNow;   // monotonic now the fight clock was last advanced to
        private double tickNow;        // monotonic now of the latest Tick / entry point
        private double heldAt;         // Held: when the hold began; Won-awaiting: when the fight was won; collecting: when the cache was placed
        private double? presentSince;  // Held: armed and owner present since
        private double begunAt;        // monotonic now the fight began (wall-clock origin of a recorded clear)
        private double winWallSeconds; // wall seconds from Begin to the win (deaths and run-backs count)
        private double winFightSeconds; // Running-only fight-clock seconds at the win
        private bool adminJumped;      // an admin JumpToPhase touched this fight: its win is not a record-eligible clear
        private bool adminEntry;       // MarkAdminEntry: begun by an admin command, so TryBegin spends no charge

        // ---- objects ----
        private PinnacleHandle head;
        private bool headDeathReported;
        private bool headTracked;      // the head's death is watched only once the fight has begun
        private bool headSpawnFailureLogged;
        private readonly List<PinnacleHandle> seal = new List<PinnacleHandle>();
        private readonly List<TrackedAdd> adds = new List<TrackedAdd>();
        private readonly Dictionary<PinnacleHandle, int> heldHealth = new Dictionary<PinnacleHandle, int>();
        private readonly List<PinnacleHandle> pacified = new List<PinnacleHandle>();

        // Props are NOT adds: not phase-scoped, never AddDied, never held. Only End (or DestroyProp) removes one.
        private readonly Dictionary<PinnacleHandle, string> props = new Dictionary<PinnacleHandle, string>();

        // ---- hazards ----
        // A run the core stops is removed from runs at once; tokens are never reused, so its late OnEnded is an
        // unknown token and is ignored by that path alone.
        private readonly Dictionary<int, HazardRequest> runs = new Dictionary<int, HazardRequest>();
        private readonly List<HazardRequest> retries = new List<HazardRequest>();

        // ---- timers and events ----
        private readonly Dictionary<string, double> timers = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Queue<(int Epoch, PinnacleEvent Event)> events = new Queue<(int, PinnacleEvent)>();
        private int epoch;
        private bool advanceRequested;
        private bool winRequested;
        private bool begun;
        private bool charged;
        private bool bossKilledReported;

        private sealed class TrackedAdd
        {
            public PinnacleHandle Handle;
            public string Tag;
            public bool DeathReported;
        }

        private sealed class HazardRequest
        {
            public string Tag;
            public string Kind;
            public string Pattern;
            public HazardArgs Args;
            public PinnacleHandle Origin;
            public int Attempts;
            public int Epoch;
        }

        /// <summary>
        /// Callers MUST pass <paramref name="knotLevel"/> and <paramref name="seed"/> as NAMED arguments
        /// (<c>knotLevel: level, seed: seed</c>): both are ints and adjacent, so a positional swap compiles silently.
        /// </summary>
        public PinnacleFight(IPinnacleBossScript script, IPinnacleWorld world, PinnacleFightConfig cfg, uint ownerGuid, uint knotGuid, uint instance, int knotLevel, int seed)
        {
            this.script = script ?? throw new ArgumentNullException(nameof(script));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.cfg = cfg ?? new PinnacleFightConfig();
            OwnerGuid = ownerGuid;
            KnotGuid = knotGuid;
            Instance = instance;
            KnotLevel = knotLevel;
            rng = new Random(seed);
        }

        // ================================================================ status

        public PinnacleFightState State => (PinnacleFightState)Volatile.Read(ref state);

        public bool AwaitingDelivery => awaitingDelivery;

        /// <summary>Won with the cache placed, not yet ended (see the class comment). A re-entry to collect is free.</summary>
        public bool Collecting => collecting;

        public bool IsEnded => isEnded;

        public uint OwnerGuid { get; }

        public uint KnotGuid { get; }

        public uint Instance { get; }

        /// <summary>The Knot's level; the live world scales every creature it spawns for this fight to it.</summary>
        public int KnotLevel { get; }

        public IPinnacleBossScript Script => script;

        /// <summary>
        /// Held, or won (cache owed or collecting): the WHOLE hold plus a margin; otherwise null. This is an idle WINDOW, not
        /// a deadline: the landblock heartbeat measures it from its own idle clock (Landblock.ShouldQueueForUnload:
        /// lastActiveTime + window &lt; now), and lastActiveTime stops moving when the owner leaves, which is within a
        /// tick of heldAt. Passing the REMAINING hold here would shrink the window while the idle clock stands still,
        /// and the copy would unload about halfway through the hold. The fight ends itself at hold expiry and then
        /// clears the hold, so the full window never outlives the attempt.
        /// </summary>
        public TimeSpan? UnloadIntervalOverride
        {
            get
            {
                if (isEnded)
                    return null;

                var s = State;
                return PinnacleFightRules.UnloadOverride(s, awaitingDelivery || collecting, cfg.HoldSeconds);
            }
        }

        private void SetState(PinnacleFightState s) => Volatile.Write(ref state, (int)s);

        // ================================================================ entry points (fight thread)

        public void Tick(double now) => Guard("tick", () => TickCore(now));

        /// <summary>
        /// Head use. NotStarted -> Running: seal, charge, OnStart, EnterPhase(1). False (and nothing done) in any
        /// other state, or when the script has a head and it is not alive. True once the begin got past those checks,
        /// even if a fault then ended the attempt.
        /// </summary>
        public bool TryBegin(double now, bool charge = true)
        {
            var wasBegun = begun;
            Guard("begin", () => Begin(now, charge && !adminEntry, 1));
            return !wasBegun && begun;
        }

        /// <summary>
        /// The owner died in the copy. Running -> Held. Collecting: the owner may come back for the cache (their
        /// respawn elsewhere does not end the collecting). Ignored otherwise.
        /// </summary>
        public void OwnerDied(double now) => Guard("owner died", () =>
        {
            if (isEnded)
                return;

            tickNow = now;

            if (State == PinnacleFightState.Running)
                Hold(now, "owner died");
            else if (collecting)
                collectReturnAllowed = true;
        });

        /// <summary>
        /// Any thread. The owner re-entered through the Knot (called once their teleport has been issued, NOT on their
        /// arrival). Held: resume once they have been present and alive for the grace and are out of portal space. Collecting: they are on their way
        /// back, so their absence until they arrive does not end the collecting.
        /// </summary>
        public void ArmResume() => Volatile.Write(ref armed, 1);

        /// <summary>
        /// The owner could not pay to return (their Knot had no entries left): a Running or Held attempt ends Lost through
        /// the normal cleanup, reported to the Knot as lost. Ignored in every other state (a win is never forfeited).
        /// </summary>
        public void Forfeit(string reason) => Guard("forfeit", () =>
        {
            if (isEnded)
                return;

            var s = State;

            if (s == PinnacleFightState.Running || s == PinnacleFightState.Held)
                End(PinnacleFightState.Lost, "forfeit: " + (reason ?? "no entries"));
        });

        /// <summary>A hazard run of this fight ended. Fight thread.</summary>
        public void OnHazardEnded(int token, HazardOutcome outcome) => Guard("hazard ended", () => OnHazardEndedCore(token, outcome));

        /// <summary>Admin. Begins WITHOUT a charge when NotStarted; leaves a hold; refused once won or ended.</summary>
        public void JumpToPhase(int phase, double now) => Guard("jump", () => JumpToPhaseCore(phase, now));

        /// <summary>
        /// Admin entry (the playtest command): marks the fight BEFORE <see cref="TryBegin"/>. The begin then spends no charge
        /// (TryBegin's charge && !adminEntry) and the win is flagged like an admin jump, so it never reports a clear.
        /// </summary>
        public void MarkAdminEntry()
        {
            adminEntry = true;
            adminJumped = true;
        }

        /// <summary>
        /// The owner used a prop (a door). Delivered at once as ObjectUsed{Tag, Handle} while Running; ignored while Held,
        /// ended, not begun, or for a handle that is not one of this fight's props.
        /// </summary>
        public void OnObjectUsed(PinnacleHandle h) => Guard("object used", () =>
        {
            if (isEnded || h.IsNone || State != PinnacleFightState.Running)
                return;

            if (!props.TryGetValue(h, out var tag))
                return;

            Enqueue(epoch, new PinnacleEvent { Kind = PinnacleEventKind.ObjectUsed, Tag = tag, Handle = h });
            DrainAndApply();
        });

        /// <summary>Admin. Ends the attempt Lost through the normal cleanup.</summary>
        public void Reset(string reason) => Guard("reset", () => End(PinnacleFightState.Lost, "reset: " + (reason ?? "admin")));

        public string Describe(double now)
        {
            var sb = new StringBuilder();
            var s = State;
            sb.Append($"{script.Kind} state={s} phase={Phase}/{script.PhaseCount} clock={clock:0.0}s knotLevel={KnotLevel}");

            if (s == PinnacleFightState.Held)
                sb.Append($" held={now - heldAt:0}s/{cfg.HoldSeconds:0}s armed={(Volatile.Read(ref armed) != 0 ? "yes" : "no")}");

            if (awaitingDelivery)
                sb.Append($" awaitingDelivery={now - heldAt:0}s/{cfg.HoldSeconds:0}s");

            if (collecting)
                sb.Append($" collecting={now - heldAt:0}s/{cfg.HoldSeconds:0}s mayReturn={(collectReturnAllowed ? "yes" : "no")}");

            sb.Append($" head={head} adds={adds.Count(a => !a.DeathReported)}/{adds.Count} seal={seal.Count} hazards={runs.Count} retries={retries.Count}");

            if (timers.Count > 0)
                sb.Append(" timers=" + string.Join(",", timers.OrderBy(t => t.Value).Select(t => $"{t.Key}@{t.Value - clock:0.0}s")));

            string d = null;
            try { d = script.Describe(); } catch (Exception ex) { d = "describe threw " + ex.GetType().Name; }

            if (!string.IsNullOrEmpty(d))
                sb.Append(" | ").Append(d);

            return sb.ToString();
        }

        // ================================================================ IPinnacleFightPort

        public double Now => clock;

        public Random Rng => rng;

        public int Phase { get; private set; }

        public PinnacleHandle Head => head;

        public int Level => KnotLevel;

        public PinnacleHandle SpawnAdd(string tag, uint wcid, PinnaclePoint at, PinnacleTier tier)
        {
            if (isEnded)
                return PinnacleHandle.None;

            var h = world.Spawn(wcid, at, withImmuneRecord: true, tier);

            if (h.IsNone)
            {
                world.Log($"pinnacle: add '{tag}' wcid {wcid} failed to spawn; retrying once");
                h = world.Spawn(wcid, at, withImmuneRecord: true, tier);
            }

            if (h.IsNone)
            {
                // ERROR, not INFO: reporting it dead keeps the fight from soft-locking, but it also skips whatever the
                // add was for (a whole phase, 2026-10-19 playtest), so it must never pass as routine.
                world.LogError($"pinnacle: add '{tag}' wcid {wcid} at 0x{at.Cell:X8} [{at.X:0.###} {at.Y:0.###} {at.Z:0.###}] failed to spawn twice; reporting it dead so the fight goes on (the phase it belonged to is skipped)");
                Enqueue(epoch, new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = tag, Handle = PinnacleHandle.None, SpawnFailed = true });
                return PinnacleHandle.None;
            }

            adds.Add(new TrackedAdd { Handle = h, Tag = tag });
            return h;
        }

        public PinnacleHandle SpawnProp(string tag, PinnacleProp prop)
        {
            if (isEnded || prop == null)
                return PinnacleHandle.None;

            var h = world.SpawnProp(prop);

            if (h.IsNone)
            {
                world.Log($"pinnacle: prop '{tag}' wcid {prop.Wcid} failed to spawn; retrying once");
                h = world.SpawnProp(prop);
            }

            if (h.IsNone)
            {
                // unlike an add, a prop that fails twice raises nothing: no phase hangs on it
                world.LogError($"pinnacle: prop '{tag}' wcid {prop.Wcid} at 0x{prop.At.Cell:X8} [{prop.At.X:0.###} {prop.At.Y:0.###} {prop.At.Z:0.###}] failed to spawn twice");
                return PinnacleHandle.None;
            }

            props[h] = tag;
            return h;
        }

        public void DestroyProp(PinnacleHandle h)
        {
            if (isEnded || h.IsNone || !props.Remove(h))
                return;

            if (world.IsAlive(h))
                world.Destroy(h);
        }

        public void SetDoorOpen(PinnacleHandle h, bool open)
        {
            if (!isEnded && !h.IsNone)
                world.SetDoorOpen(h, open);
        }

        public void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold)
        {
            if (!isEnded && !h.IsNone)
                world.PlayMotion(h, stance, motion, hold);
        }

        public void PlayEffect(PinnacleHandle h, PlayScript script)
        {
            if (!isEnded && !h.IsNone)
                world.PlayEffect(h, script);
        }

        public void MoveTo(PinnacleHandle h, PinnaclePoint to)
        {
            if (!isEnded && !h.IsNone)
                world.MoveTo(h, to);
        }

        public double TurnTo(PinnacleHandle h, PinnaclePoint facing)
            => !isEnded && !h.IsNone ? Math.Max(0.0, world.TurnTo(h, facing)) : 0.0;

        public bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at)
        {
            at = default;
            return !isEnded && !h.IsNone && world.TryGetPosition(h, out at);
        }

        public bool TryGetOwnerPosition(out PinnaclePoint at)
        {
            at = default;
            return !isEnded && world.TryGetOwnerPosition(out at);
        }

        public void SetPeaceful(PinnacleHandle h, bool peaceful)
        {
            if (!isEnded && !h.IsNone)
                world.SetPeaceful(h, peaceful);
        }

        public void Engage(PinnacleHandle h)
        {
            if (!isEnded && !h.IsNone)
                world.Engage(h);
        }

        public void SetHealthFloor(PinnacleHandle h, double? fraction)
        {
            if (!isEnded && !h.IsNone)
                world.SetHealthFloor(h, fraction);
        }

        public bool IsAtHealthFloor(PinnacleHandle h) => !isEnded && !h.IsNone && world.IsAtHealthFloor(h);

        public void Slay(PinnacleHandle h)
        {
            if (!isEnded && !h.IsNone)
                world.Slay(h);
        }

        public bool ControlHazard(string tag, string key, double value)
            => !isEnded && TryTokenOf(tag, out var token) && world.ControlHazard(token, key, value);

        public bool TryGetHazardLevel(string tag, out double level)
        {
            level = 0;
            return !isEnded && TryTokenOf(tag, out var token) && world.TryGetHazardLevel(token, out level);
        }

        /// <summary>The first live run started under <paramref name="tag"/> (lowest token); false when there is none.</summary>
        private bool TryTokenOf(string tag, out int token)
        {
            token = 0;

            if (tag == null)
                return false;

            foreach (var kv in runs)
                if (kv.Value.Tag == tag && (token == 0 || kv.Key < token))
                    token = kv.Key;

            return token != 0;
        }

        public bool IsAlive(PinnacleHandle h) => !h.IsNone && world.IsAlive(h);

        public void SetImmune(PinnacleHandle h, bool immune)
        {
            if (!h.IsNone)
                world.SetImmune(h, immune);
        }

        public void SetAttackable(PinnacleHandle h, bool attackable)
        {
            if (!h.IsNone)
                world.SetAttackable(h, attackable);
        }

        /// <summary>
        /// Section 7 Q4: face the owner, nod, then cast - the cast at the END of the nod, never alongside it. The cast is
        /// dropped when, by the time the nod ends, the fight is no longer Running in the same phase or the caster is dead.
        /// </summary>
        public void CastAtOwner(PinnacleHandle caster, uint spellId)
        {
            if (caster.IsNone || isEnded)
                return;

            world.FaceOwner(caster);

            var castEpoch = epoch;

            world.Bob(caster, () => Guard("cast", () =>
            {
                if (isEnded || State != PinnacleFightState.Running || epoch != castEpoch || !world.IsAlive(caster))
                    return;

                world.Cast(caster, spellId);
            }));
        }

        public void StartHazard(string tag, string kind, string pattern, HazardArgs args, PinnacleHandle origin = default)
        {
            if (isEnded)
                return;

            var req = new HazardRequest { Tag = tag, Kind = kind, Pattern = pattern, Args = args ?? HazardArgs.Empty, Origin = origin, Epoch = epoch };
            Launch(req);
        }

        public void StopHazards() => StopAllHazards();

        public void StopHazard(string tag)
        {
            var tokens = runs.Where(kv => kv.Value.Tag == tag).Select(kv => kv.Key).ToList();

            foreach (var token in tokens)
                runs.Remove(token);

            retries.RemoveAll(r => r.Tag == tag);

            foreach (var token in tokens)
                world.StopHazard(token);
        }

        public void SetTimer(string tag, double seconds)
        {
            if (tag == null)
                return;

            timers[tag] = clock + Math.Max(0.0, seconds);
        }

        public void CancelTimer(string tag)
        {
            if (tag != null)
                timers.Remove(tag);
        }

        // always reaches the world, even for a count of 0: a failed round shows the dispel visual and the "nothing" line
        public int DispelOwnerBuffs(int count) => world.DispelOwnerBuffs(Math.Max(0, count), rng);

        public void Say(string line) => world.Tell(line);

        public void AdvancePhase() => advanceRequested = true;

        public void Win() => winRequested = true;

        // ================================================================ guarded bodies

        /// <summary>Runs <paramref name="a"/>; a throw is logged and ends the attempt Lost as a fault (never consuming the Knot).</summary>
        private void Guard(string what, Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                SafeLog($"pinnacle: {what} threw {ex}");
                End(PinnacleFightState.Lost, FaultPrefix + what);
            }
        }

        private void SafeLog(string line)
        {
            try { world.Log(line); } catch { /* the log itself failing must never stop cleanup */ }
        }

        private void TickCore(double now)
        {
            if (isEnded)
                return;

            tickNow = now;

            if (!world.IsLive)
            {
                End(PinnacleFightState.Lost, "copy unloaded", worldWrites: false);
                return;
            }

            // before the state machine, in every state (a held fight's returning owner is the 2026-10-22 case)
            LiftPlayersUnderPlateFloor(now);

            switch (State)
            {
                case PinnacleFightState.NotStarted:
                    EnsureHead();
                    break;

                case PinnacleFightState.Running:
                    if (!world.OwnerPresent)
                    {
                        // keeps an arm: the owner may already be on their way back through the Knot (see Hold)
                        Hold(now, "owner absent", keepArm: true);
                        return;
                    }

                    // the owner is here: an arm made while they were away and back before any hold is spent
                    Volatile.Write(ref armed, 0);

                    AdvanceClock(now);
                    PollHandles();
                    FireDueTimers();
                    RunDueRetries();
                    DrainAndApply();
                    break;

                case PinnacleFightState.Held:
                    ClampHealths();
                    PollHandles();

                    if (PinnacleFightRules.HoldExpired(heldAt, now, cfg.HoldSeconds))
                    {
                        End(PinnacleFightState.Lost, "hold expired");
                        return;
                    }

                    var isArmed = Volatile.Read(ref armed) != 0;

                    if (isArmed && world.OwnerPresent)
                        presentSince ??= now;
                    else
                        presentSince = null;

                    // the grace counts from presence (portal space included); the resume also waits for the owner to be out of it
                    if (PinnacleFightRules.ResumeDue(isArmed, presentSince, now, cfg.ResumeGraceSeconds) && world.OwnerSettled)
                        Resume(now);
                    break;
            }

            // a Win applied during this tick tries its delivery at once; a delivered one watches for the owner leaving
            if (!isEnded && State == PinnacleFightState.Won)
                TryFinishWin(now);
        }

        /// <summary>The plate-floor lift (IPinnacleWorld.LiftPlayersUnderPlateFloor). A throw is logged, never a fault: it must not end the attempt.</summary>
        private void LiftPlayersUnderPlateFloor(double now)
        {
            try
            {
                world.LiftPlayersUnderPlateFloor(now);
            }
            catch (Exception ex)
            {
                SafeLog($"pinnacle: plate-floor lift threw {ex}");
            }
        }

        private void OnHazardEndedCore(int token, HazardOutcome outcome)
        {
            if (isEnded)
                return;

            if (!runs.TryGetValue(token, out var req))
            {
                // a run the core stopped (forgotten at stop), or not one of ours
                world.Log($"pinnacle: hazard token {token} ended {outcome}; not a live run of this fight, ignored");
                return;
            }

            runs.Remove(token);

            switch (PinnacleFightRules.Classify(outcome, false, req.Attempts))
            {
                case RetryDecision.Retry:
                    world.Log($"pinnacle: hazard '{req.Tag}' ended {outcome}; retrying once next tick");
                    retries.Add(req);
                    break;

                case RetryDecision.Deliver:
                    var delivered = PinnacleFightRules.DeliveredOutcome(outcome);
                    if (delivered != outcome)
                        world.Log($"pinnacle: hazard '{req.Tag}' ended {outcome} after its retry; delivering Aborted");
                    Enqueue(req.Epoch, new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = req.Tag, Outcome = delivered });
                    break;
            }
        }

        private void JumpToPhaseCore(int phase, double now)
        {
            if (isEnded)
                return;

            tickNow = now;

            if (phase < 1 || phase > script.PhaseCount)
            {
                world.Log($"pinnacle: jump to phase {phase} refused (phases are 1..{script.PhaseCount})");
                return;
            }

            switch (State)
            {
                case PinnacleFightState.NotStarted:
                    adminJumped = true;
                    Begin(now, charge: false, phase);
                    return;

                case PinnacleFightState.Held:
                    adminJumped = true;
                    SetState(PinnacleFightState.Running);
                    Volatile.Write(ref armed, 0);
                    presentSince = null;
                    heldHealth.Clear();
                    lastClockNow = now;
                    RestorePacified();
                    break;

                case PinnacleFightState.Running:
                    adminJumped = true;
                    AdvanceClock(now);
                    break;

                default:
                    world.Log($"pinnacle: jump to phase {phase} refused in state {State}");
                    return;
            }

            world.Log($"pinnacle: admin jump to phase {phase}");
            winRequested = false;
            EnterPhaseCore(phase);
            ApplyPending();
            DrainAndApply();
        }

        // ================================================================ lifecycle

        private void Begin(double now, bool charge, int firstPhase)
        {
            if (isEnded || begun || State != PinnacleFightState.NotStarted)
                return;

            tickNow = now;

            EnsureHead();

            if (script.Data != null && script.Data.HeadWcid != 0 && (head.IsNone || !world.IsAlive(head)))
            {
                world.Log($"pinnacle: begin refused: head wcid {script.Data.HeadWcid} is not available");
                return;
            }

            begun = true;

            // A seal is not a creature, so its tier is never read; Elite is only a placeholder argument.
            foreach (var p in script.Data?.SealPoints ?? Array.Empty<PinnaclePoint>())
            {
                var h = world.Spawn(script.Data.SealWcid, p, withImmuneRecord: false, PinnacleTier.Elite);

                if (h.IsNone)
                    h = world.Spawn(script.Data.SealWcid, p, withImmuneRecord: false, PinnacleTier.Elite);

                if (h.IsNone)
                    world.Log($"pinnacle: seal wcid {script.Data.SealWcid} failed to spawn twice at {p}; the room is not sealed there");
                else
                    seal.Add(h);
            }

            if (charge)
                world.ChargeEntry();

            charged = charge;
            clock = 0;
            begunAt = now;
            lastClockNow = now;
            headTracked = true;
            SetState(PinnacleFightState.Running);

            world.Log($"pinnacle: {script.Kind} begun (charge={charge}, phase {firstPhase}, knot level {KnotLevel})");

            // OnStart and the first EnterPhase share ONE epoch and no clear runs between them, so whatever OnStart
            // spawns, starts or raises carries into the first phase.
            epoch++;
            Phase = firstPhase;
            script.OnStart(this);
            world.Log($"pinnacle: entering phase {firstPhase}");
            script.EnterPhase(firstPhase, this);
            ApplyPending();
            DrainAndApply();   // e.g. a synthetic AddDied(SpawnFailed) raised by the phase entry
        }

        /// <summary>Spawns the head when it is missing or dead (never once the fight has begun).</summary>
        private void EnsureHead()
        {
            if (headTracked || script.Data == null || script.Data.HeadWcid == 0)
                return;

            if (!head.IsNone && world.IsAlive(head))
                return;

            head = world.Spawn(script.Data.HeadWcid, script.Data.HeadPoint, withImmuneRecord: true, script.Data.HeadTier, script.Data.HeadLeavesCorpse);

            if (head.IsNone && !headSpawnFailureLogged)
            {
                headSpawnFailureLogged = true;
                world.Log($"pinnacle: head wcid {script.Data.HeadWcid} failed to spawn; retrying every tick");
            }
        }

        /// <param name="keepArm">
        /// The "owner absent" hold: an arm set while Running can only be the owner's own Knot return issued while they
        /// were outside (a Running tick with the owner present clears every older arm), so it is kept and their arrival
        /// resumes the fight. A death hold clears it: whatever was armed before the death is stale.
        /// </param>
        private void Hold(double now, string why, bool keepArm = false)
        {
            // armed is cleared BEFORE State = Held is published (see the class comment)
            if (!keepArm)
                Volatile.Write(ref armed, 0);

            presentSince = null;
            heldAt = now;

            StopAllHazards();

            heldHealth.Clear();
            if (!head.IsNone && world.IsAlive(head))
                heldHealth[head] = world.GetHealth(head);
            foreach (var a in adds)
                if (!a.DeathReported && world.IsAlive(a.Handle))
                    heldHealth[a.Handle] = world.GetHealth(a.Handle);

            // the enemies stop fighting for the hold (and through the resume grace): restored only by Resume / a jump
            foreach (var h in heldHealth.Keys)
            {
                world.SetPeaceful(h, true);
                pacified.Add(h);
            }

            SetState(PinnacleFightState.Held);
            world.Log($"pinnacle: held in phase {Phase} ({why})");
        }

        /// <summary>Gives every creature the hold pacified its own temperament back (the dead ones are skipped).</summary>
        private void RestorePacified()
        {
            foreach (var h in pacified)
                if (world.IsAlive(h))
                    world.SetPeaceful(h, false);

            pacified.Clear();
        }

        private void Resume(double now)
        {
            SetState(PinnacleFightState.Running);
            Volatile.Write(ref armed, 0);
            presentSince = null;
            heldHealth.Clear();
            lastClockNow = now;
            RestorePacified();

            world.Log($"pinnacle: resumed in phase {Phase}");

            var startEpoch = epoch;

            // queued events first: a kill or a completion during the hold stands
            DrainAndApply();
            ApplyPending();   // an AdvancePhase requested while Held

            if (!isEnded && State == PinnacleFightState.Running && epoch == startEpoch)
            {
                script.RestartPhase(this);
                ApplyPending();
                DrainAndApply();
            }
        }

        private void EnterPhaseCore(int n)
        {
            ClearPhase();
            advanceRequested = false;
            epoch++;
            Phase = n;
            world.Log($"pinnacle: entering phase {n}");
            script.EnterPhase(n, this);
        }

        /// <summary>Applies a Win or AdvancePhase the last script callback requested. Only while Running.</summary>
        private void ApplyPending()
        {
            while (!isEnded && State == PinnacleFightState.Running && (winRequested || advanceRequested))
            {
                if (winRequested)
                {
                    winRequested = false;
                    advanceRequested = false;
                    DoWin();
                    return;
                }

                advanceRequested = false;

                if (Phase >= script.PhaseCount)
                {
                    world.Log($"pinnacle: AdvancePhase in the last phase ({Phase}) ignored; a script ends the fight with Win");
                    continue;
                }

                EnterPhaseCore(Phase + 1);
            }
        }

        private void DoWin()
        {
            ClearPhase();
            DestroySeal();
            events.Clear();
            heldAt = tickNow;
            winWallSeconds = Math.Max(0, tickNow - begunAt);
            winFightSeconds = clock;
            awaitingDelivery = true;
            SetState(PinnacleFightState.Won);
            world.Log($"pinnacle: won in phase {Phase}; cache owed");
        }

        /// <summary>
        /// Won, every tick. Awaiting: deliver the cache when the owner is present (then collecting starts and the hold
        /// clock restarts); never delivered by hold expiry: End Lost, refunded, no loss reported. Collecting: see the class
        /// comment for when it ends; the Knot is told the boss died by End.
        /// </summary>
        private void TryFinishWin(double now)
        {
            if (awaitingDelivery)
            {
                if (world.OwnerPresent && world.TryDeliverCache())
                {
                    // latched at once: a throw from here on can never deliver a second cache
                    cacheDelivered = true;
                    heldAt = now;
                    collectReturnAllowed = false;
                    Volatile.Write(ref armed, 0);

                    // Collecting is published BEFORE AwaitingDelivery clears (see the class comment)
                    collecting = true;
                    awaitingDelivery = false;

                    world.Log($"pinnacle: cache delivered; collecting until the owner leaves or {cfg.HoldSeconds:0}s pass");

                    // the click-to-use way out (owner ruling 2026-10-20), after the delivery is latched and published: a
                    // failure or a throw here is logged and changes nothing about the delivered cache or the collecting
                    TrySummonReturnPortal();
                    TryReportCleared();
                    return;
                }

                if (PinnacleFightRules.HoldExpired(heldAt, now, cfg.HoldSeconds))
                    End(PinnacleFightState.Lost, "reward never collected", refund: true);

                return;
            }

            if (!collecting)
                return;

            if (world.OwnerPresent)
            {
                collectReturnAllowed = false;
                Volatile.Write(ref armed, 0);
            }
            else if (Volatile.Read(ref armed) != 0 || !world.OwnerOnline)
            {
                // on the way back through the Knot, or logged out (logout counts as death): they may come back
                collectReturnAllowed = true;
            }
            else if (!collectReturnAllowed && world.OwnerAliveElsewhere)
            {
                End(PinnacleFightState.Won, "the owner left the copy with the cache delivered");
                return;
            }

            if (PinnacleFightRules.HoldExpired(heldAt, now, cfg.HoldSeconds))
                End(PinnacleFightState.Won, "collecting closed at hold expiry");
        }

        /// <summary>
        /// Once per fight, after the return portal: reports the clear for the Knot leaderboard. Only a CHARGED fight an admin
        /// never jumped reports one (an admin-started or jumped win is not a real clear). Never throws and never ends the
        /// attempt: a failed report changes nothing about the delivered cache.
        /// </summary>
        private void TryReportCleared()
        {
            if (!charged || adminJumped)
                return;

            try
            {
                world.ReportCleared(new PinnacleClear(KnotLevel, winWallSeconds, winFightSeconds, OwnerGuid));
            }
            catch (Exception ex)
            {
                SafeLog($"pinnacle: ReportCleared threw {ex}");
            }
        }

        /// <summary>
        /// Once per fight, right after the cache delivery: the return portal. Never throws and never ends the attempt (it
        /// is NOT run through Guard): the owner still has every other way out, so a portal that could not be made is only
        /// logged.
        /// </summary>
        private void TrySummonReturnPortal()
        {
            try
            {
                if (!world.SummonReturnPortal())
                    SafeLog("pinnacle: the return portal could not be summoned; the cache stands and the owner can still recall out");
            }
            catch (Exception ex)
            {
                SafeLog($"pinnacle: the return portal summon threw {ex}; the cache stands");
            }
        }

        /// <summary>
        /// THE cleanup, exactly once for every end; every step in its own try/catch and the final state in a finally,
        /// so a failing step never skips the release or leaves the state unset. worldWrites false (the copy is gone)
        /// skips everything that touches the copy's objects or the instance, but still does the Knot bookkeeping (that
        /// is on the owner, not a write to the copy).
        ///
        /// THE KNOT IS TOLD EXACTLY ONE THING, OR NOTHING:
        ///   - the cache was delivered (whatever ended the fight afterwards, a fault or a reset included): ReportBossKilled,
        ///     once - one Knot in, one reward out;
        ///   - <paramref name="refund"/> (a win whose cache was never delivered): RefundEntry when the attempt was charged,
        ///     and no loss;
        ///   - otherwise a Lost CHARGED attempt that is not a FAULT end (reason "fault in ...") reports the loss. Never
        ///     reported lost: an attempt that was never charged (never begun, or begun by an admin jump), and a fault.
        /// </summary>
        private void End(PinnacleFightState outcome, string reason, bool worldWrites = true, bool refund = false)
        {
            if (isEnded)
                return;

            isEnded = true;
            var fault = reason != null && reason.StartsWith(FaultPrefix, StringComparison.Ordinal);

            try
            {
                if (worldWrites)
                {
                    foreach (var a in adds)
                        Step("destroy add", () => { if (world.IsAlive(a.Handle)) world.Destroy(a.Handle); });

                    foreach (var token in runs.Keys.ToList())
                        Step("stop hazard", () => world.StopHazard(token));

                    foreach (var p in props.Keys.ToList())
                        Step("destroy prop", () => { if (world.IsAlive(p)) world.Destroy(p); });

                    foreach (var h in seal)
                        Step("destroy seal", () => world.Destroy(h));

                    var headNow = head;
                    Step("destroy head", () => { if (!headNow.IsNone && world.IsAlive(headNow)) world.Destroy(headNow); });
                }

                adds.Clear();
                props.Clear();
                runs.Clear();
                retries.Clear();
                timers.Clear();
                seal.Clear();
                head = PinnacleHandle.None;
                events.Clear();
                heldHealth.Clear();
                pacified.Clear();
                advanceRequested = false;
                winRequested = false;

                if (cacheDelivered)
                {
                    if (!bossKilledReported)
                    {
                        bossKilledReported = true;
                        Step("report boss killed", () => world.ReportBossKilled());
                    }
                }
                else if (refund)
                {
                    if (charged)
                        Step("refund entry", () => world.RefundEntry());
                }
                else if (outcome == PinnacleFightState.Lost && charged && !fault)
                {
                    Step("report attempt lost", () => world.ReportAttemptLost());
                }

                if (worldWrites)
                    Step("release instance", () => world.ReleaseInstance(world.OwnerPresent));
            }
            finally
            {
                // the state first, then the flags: a router that sees neither flag sees the final state too
                SetState(outcome);
                collecting = false;
                awaitingDelivery = false;
                SafeLog($"pinnacle: ended {outcome} ({reason})");

                // Telemetry, last and on its own: a throw here must never reach the cleanup above or the caller.
                try
                {
                    world.ReportEnded(new PinnacleAttemptEnd(outcome, reason, refund, cacheDelivered, charged, Phase, script.PhaseCount, KnotLevel, KnotGuid, OwnerGuid));
                }
                catch (Exception ex)
                {
                    SafeLog($"pinnacle: ReportEnded threw {ex}");
                }
            }
        }

        private void Step(string what, Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                SafeLog($"pinnacle: cleanup step '{what}' threw {ex}");
            }
        }

        // ================================================================ phase plumbing

        /// <summary>Destroys the phase's live adds (never the head or the seal), stops every hazard, cancels every timer.</summary>
        private void ClearPhase()
        {
            foreach (var a in adds)
                if (world.IsAlive(a.Handle))
                    world.Destroy(a.Handle);

            adds.Clear();
            StopAllHazards();
            timers.Clear();
        }

        private void DestroySeal()
        {
            foreach (var h in seal)
                world.Destroy(h);

            seal.Clear();
        }

        /// <summary>Stops and FORGETS every run (and drops pending retries): a late OnEnded for one is an unknown token.</summary>
        private void StopAllHazards()
        {
            var tokens = runs.Keys.ToList();
            runs.Clear();
            retries.Clear();

            foreach (var token in tokens)
                world.StopHazard(token);
        }

        private void Launch(HazardRequest req)
        {
            req.Attempts++;

            var args = req.Args;

            if (!req.Origin.IsNone)
            {
                if (world.TryGetXY(req.Origin, out var x, out var y))
                    args = args.With("cx", x.ToString("R", CultureInfo.InvariantCulture)).With("cy", y.ToString("R", CultureInfo.InvariantCulture));
                else
                    world.Log($"pinnacle: hazard '{req.Tag}' origin {req.Origin} has no position; starting without cx/cy");
            }

            var token = world.StartHazard(req.Kind, req.Pattern, args, out var error);

            if (token > 0)
            {
                runs[token] = req;
                return;
            }

            switch (PinnacleFightRules.Classify(HazardOutcome.Aborted, false, req.Attempts))
            {
                case RetryDecision.Retry:
                    world.Log($"pinnacle: hazard '{req.Tag}' ({req.Kind}) refused: {error}; retrying once next tick");
                    retries.Add(req);
                    break;

                default:
                    world.Log($"pinnacle: hazard '{req.Tag}' ({req.Kind}) refused again: {error}; delivering Aborted");
                    Enqueue(req.Epoch, new PinnacleEvent { Kind = PinnacleEventKind.HazardEnded, Tag = req.Tag, Outcome = HazardOutcome.Aborted });
                    break;
            }
        }

        private void RunDueRetries()
        {
            if (retries.Count == 0)
                return;

            var due = retries.ToList();
            retries.Clear();

            foreach (var req in due)
            {
                if (req.Epoch != epoch)
                    continue;

                Launch(req);
            }
        }

        private void AdvanceClock(double now)
        {
            if (now > lastClockNow)
                clock += now - lastClockNow;

            lastClockNow = now;
        }

        private void FireDueTimers()
        {
            if (timers.Count == 0)
                return;

            var due = timers.Where(t => t.Value <= clock).OrderBy(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();

            foreach (var t in due)
            {
                timers.Remove(t.Key);
                Enqueue(epoch, new PinnacleEvent { Kind = PinnacleEventKind.TimerFired, Tag = t.Key });
            }
        }

        /// <summary>Queues AddDied once per add (and for the head once the fight has begun). Queue only: delivery is DrainAndApply's.</summary>
        private void PollHandles()
        {
            if (headTracked && !headDeathReported && !head.IsNone && !world.IsAlive(head))
            {
                headDeathReported = true;
                Enqueue(epoch, new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = PinnacleEvent.HeadTag, Handle = head });
            }

            foreach (var a in adds)
            {
                if (a.DeathReported || world.IsAlive(a.Handle))
                    continue;

                a.DeathReported = true;
                Enqueue(epoch, new PinnacleEvent { Kind = PinnacleEventKind.AddDied, Tag = a.Tag, Handle = a.Handle });
            }
        }

        private void ClampHealths()
        {
            foreach (var h in heldHealth.Keys.ToList())
            {
                if (!world.IsAlive(h))
                    continue;

                var cap = Math.Min(heldHealth[h], world.GetHealth(h));
                heldHealth[h] = cap;
                world.ClampHealth(h, cap);
            }
        }

        private void Enqueue(int eventEpoch, PinnacleEvent e) => events.Enqueue((eventEpoch, e));

        /// <summary>Delivers at most the events queued when it began; anything raised meanwhile waits for the next drain.</summary>
        private void DrainAndApply()
        {
            var budget = events.Count;

            while (budget-- > 0 && !isEnded && State == PinnacleFightState.Running && events.Count > 0)
            {
                var (eventEpoch, e) = events.Dequeue();

                if (eventEpoch != epoch)
                    continue;   // raised in a phase that has since been left

                script.OnEvent(e, this);
                ApplyPending();
            }
        }
    }
}
