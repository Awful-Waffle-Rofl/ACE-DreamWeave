using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.PuzzleGates;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The fail policy's settings, as one immutable snapshot. Built on the WORLD thread
    /// (<see cref="Refresh"/>, from ThreadDungeonManager.Tick) and only read everywhere else, which is how a
    /// lever pull on a LANDBLOCK thread gets the settings and the household allowlists without a PropertyManager
    /// read there: it reads <see cref="Current"/>, a single reference swap. A changed setting therefore applies
    /// within one refresh interval (ThreadDungeonManager's reap cadence, 15 s).
    /// </summary>
    public sealed class ThreadPuzzlePolicyConfig
    {
        public const string EnabledKey = "dynamic_dungeons_puzzle_fail_policy_enabled";
        public const string ThresholdKey = "dynamic_dungeons_puzzle_fail_threshold";
        public const string WindowKey = "dynamic_dungeons_puzzle_fail_window_minutes";
        public const string LockoutKey = "dynamic_dungeons_puzzle_lockout_minutes";

        public const bool DefaultEnabled = true;
        public const long DefaultThreshold = 3;
        public const long DefaultWindowMinutes = 60;
        public const long DefaultLockoutMinutes = 120;

        /// <summary>The ledger keeps fails for its retention and no longer, so a longer window would silently read short.</summary>
        public static readonly long MaxWindowMinutes = (long)ThreadPuzzleIpLedger.DefaultRetention.TotalMinutes;

        /// <summary>A year. Only there so now + lockout can never overflow a DateTime.</summary>
        public const long MaxLockoutMinutes = 525600;

        private ThreadPuzzlePolicyConfig(bool enabled, int threshold, TimeSpan window, TimeSpan lockout, IpLimitManager.HouseholdLists household)
        {
            Enabled = enabled;
            Threshold = threshold;
            Window = window;
            Lockout = lockout;
            Household = household ?? IpLimitManager.HouseholdLists.Empty;
        }

        public bool Enabled { get; }

        /// <summary>Counted fails inside <see cref="Window"/> that trigger a lockout. At least 1.</summary>
        public int Threshold { get; }

        /// <summary>[1 minute, ledger retention].</summary>
        public TimeSpan Window { get; }

        /// <summary>[1 minute, <see cref="MaxLockoutMinutes"/>].</summary>
        public TimeSpan Lockout { get; }

        /// <summary>The household allowlists ThreadPuzzleIpKey keys per account.</summary>
        public IpLimitManager.HouseholdLists Household { get; }

        /// <summary>Pure: the clamped snapshot of raw setting values.</summary>
        public static ThreadPuzzlePolicyConfig Create(bool enabled, long threshold, long windowMinutes, long lockoutMinutes, IpLimitManager.HouseholdLists household = null)
            => new ThreadPuzzlePolicyConfig(
                enabled,
                (int)Math.Clamp(threshold, 1, int.MaxValue),
                TimeSpan.FromMinutes(Math.Clamp(windowMinutes, 1, MaxWindowMinutes)),
                TimeSpan.FromMinutes(Math.Clamp(lockoutMinutes, 1, MaxLockoutMinutes)),
                household);

        /// <summary>The shipped defaults with no household lists: what a process sees before the first refresh.</summary>
        public static ThreadPuzzlePolicyConfig Defaults()
            => Create(DefaultEnabled, DefaultThreshold, DefaultWindowMinutes, DefaultLockoutMinutes);

        private static volatile ThreadPuzzlePolicyConfig current = Defaults();

        /// <summary>The snapshot every reader uses. Settable for tests; production only changes it through <see cref="Refresh"/>.</summary>
        public static ThreadPuzzlePolicyConfig Current
        {
            get => current;
            internal set => current = value ?? Defaults();
        }

        /// <summary>
        /// WORLD THREAD ONLY: reads the four settings and the household allowlists and publishes a new
        /// <see cref="Current"/>. Explicit fallbacks equal to the shipped defaults, so an unseeded store reads as
        /// shipped rather than as off/0.
        /// </summary>
        public static void Refresh()
        {
            Current = Create(
                PropertyManager.GetBool(EnabledKey, DefaultEnabled).Item,
                PropertyManager.GetLong(ThresholdKey, DefaultThreshold).Item,
                PropertyManager.GetLong(WindowKey, DefaultWindowMinutes).Item,
                PropertyManager.GetLong(LockoutKey, DefaultLockoutMinutes).Item,
                IpLimitManager.ReadHouseholdLists());
        }
    }

    /// <summary>The policy's answer to one counted fail.</summary>
    public readonly struct PuzzleFailDecision
    {
        public PuzzleFailDecision(int count, int threshold, DateTime? lockoutUntil)
        {
            Count = count;
            Threshold = threshold;
            LockoutUntil = lockoutUntil;
        }

        /// <summary>Counted fails inside the window, this one included.</summary>
        public int Count { get; }

        public int Threshold { get; }

        /// <summary>Set when this fail reached the threshold: the lockout runs until then.</summary>
        public DateTime? LockoutUntil { get; }

        public bool Trigger => LockoutUntil.HasValue;

        /// <summary>Fails still allowed before the lockout. 0 on a trigger.</summary>
        public int Remaining => Trigger ? 0 : Math.Max(0, Threshold - Count);
    }

    /// <summary>The pure decision: fails in the window plus the settings, to a warning or a lockout.</summary>
    public static class PuzzleFailPolicy
    {
        /// <summary>
        /// <paramref name="fails"/> are the key's recorded fails, this one included. Only those inside
        /// (now - window, now] count, whatever the caller passed. Reaching the threshold triggers a lockout of
        /// config.Lockout from <paramref name="nowUtc"/>; below it, the decision carries how many remain.
        /// </summary>
        public static PuzzleFailDecision Decide(IReadOnlyList<DateTime> fails, DateTime nowUtc, ThreadPuzzlePolicyConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var cutoff = nowUtc - config.Window;
            var count = fails?.Count(t => t > cutoff && t <= nowUtc) ?? 0;

            return count >= config.Threshold
                ? new PuzzleFailDecision(count, config.Threshold, nowUtc + config.Lockout)
                : new PuzzleFailDecision(count, config.Threshold, null);
        }
    }

    /// <summary>Who pulled the lever, as plain values (a Player cannot be built in the unit-test harness).</summary>
    public readonly struct PuzzleActor
    {
        public PuzzleActor(uint guid, string name, uint accountId, string ipAddress)
        {
            Guid = guid;
            Name = name;
            AccountId = accountId;
            IpAddress = ipAddress;
        }

        public uint Guid { get; }
        public string Name { get; }
        public uint AccountId { get; }
        public string IpAddress { get; }
    }

    public enum PuzzleFailOutcomeKind
    {
        /// <summary>Policy off, or no key: nothing recorded, nothing said.</summary>
        Ignored,

        /// <summary>Recorded, below the threshold: the warning is told.</summary>
        Warned,

        /// <summary>This fail reached the threshold: the key is locked and a sweep is queued.</summary>
        LockedOut,

        /// <summary>The key was already locked (a pull racing the sweep): nothing recorded, no second sweep (the first one scans the whole key).</summary>
        AlreadyLocked,
    }

    public readonly struct PuzzleFailOutcome
    {
        public PuzzleFailOutcome(PuzzleFailOutcomeKind kind, string key, PuzzleFailDecision decision, DateTime? lockoutUntil)
        {
            Kind = kind;
            Key = key;
            Decision = decision;
            LockoutUntil = lockoutUntil;
        }

        public PuzzleFailOutcomeKind Kind { get; }
        public string Key { get; }
        public PuzzleFailDecision Decision { get; }
        public DateTime? LockoutUntil { get; }
    }

    /// <summary>A lockout waiting for the world thread to remove the key's characters from their runs.</summary>
    public sealed class LockoutSweepRequest
    {
        public LockoutSweepRequest(string key, DateTime untilUtc, ThreadDungeonRun failingRun, uint failingGuid)
        {
            Key = key;
            UntilUtc = untilUtc;
            FailingRun = failingRun;
            FailingGuid = failingGuid;
        }

        public string Key { get; }
        public DateTime UntilUtc { get; }

        /// <summary>The run the triggering pull happened in. Its puller is removed from it even if they have just stepped out.</summary>
        public ThreadDungeonRun FailingRun { get; }

        public uint FailingGuid { get; }
    }

    /// <summary>An online character on the locked key, with the run they belong to (if any) and whether they are inside it.</summary>
    public readonly struct LockoutSweepCandidate
    {
        public LockoutSweepCandidate(uint guid, string name, ThreadDungeonRun run, bool inside)
        {
            Guid = guid;
            Name = name;
            Run = run;
            Inside = inside;
        }

        public uint Guid { get; }
        public string Name { get; }
        public ThreadDungeonRun Run { get; }
        public bool Inside { get; }
    }

    /// <summary>One run's share of a sweep: who leaves it, and whether that ends it.</summary>
    public sealed class LockoutSweepAction
    {
        public LockoutSweepAction(ThreadDungeonRun run, IReadOnlyList<uint> guids, bool endRun)
        {
            Run = run;
            Guids = guids;
            EndRun = endRun;
        }

        public ThreadDungeonRun Run { get; }
        public IReadOnlyList<uint> Guids { get; }

        /// <summary>A solo run, or a group run with nobody left inside once these leave: EndRun("puzzle lockout").</summary>
        public bool EndRun { get; }
    }

    /// <summary>
    /// The Thread puzzle-gate fail policy (IP-wide). Threading, step by step:
    ///   1. A scored wrong pull reaches <see cref="ThreadPuzzleFailPolicySink"/> on the run copy's LANDBLOCK thread,
    ///      after PuzzleGateManager has released the placement lock. It reads <see cref="ThreadPuzzlePolicyConfig.Current"/>
    ///      (no PropertyManager), derives the key, records the fail in ThreadPuzzleIpLedger (per-key lock inside the
    ///      ledger), decides, and either tells the warning or applies the lockout there and then - so every check
    ///      anywhere refuses the key from that instant - and queues a <see cref="LockoutSweepRequest"/>.
    ///   2. ThreadDungeonManager.Tick (WORLD thread, every 1 s) drains the queue (<see cref="DrainSweeps"/>): it finds
    ///      every online character on the key and removes them from every live run they are on the roster of, inside
    ///      or not (EndRun for a solo run, or a group run whose last character inside leaves; otherwise the member is
    ///      marked removed on the run and teleported out if they stand in its copy).
    ///   3. The checks (gem use, group start, roster formation, TryStart) run on whatever thread those already run on,
    ///      and only read the ledger's memory and the config snapshot.
    /// Analytics are out of scope; the [PUZZLE_POLICY] log lines are the record.
    /// </summary>
    public static class ThreadPuzzleFailPolicy
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The run end reason a lockout uses. Mapped to aborted explicitly in DungeonRunTelemetry.EndStateFor.</summary>
        public const string EndReason = DungeonRunTelemetry.EndReasons.PuzzleLockout;

        /// <summary>Test seam: the ledger. Production reads ThreadPuzzleIpLedger.Shared at each use (Initialize swaps it).</summary>
        internal static Func<ThreadPuzzleIpLedger> LedgerSource = () => ThreadPuzzleIpLedger.Shared;

        private static readonly ConcurrentQueue<LockoutSweepRequest> pendingSweeps = new ConcurrentQueue<LockoutSweepRequest>();

        /// <summary>Sweeps queued and not yet drained. Diagnostic and test seam.</summary>
        internal static int PendingSweepCount => pendingSweeps.Count;

        internal static void ClearPendingSweepsForTest()
        {
            while (pendingSweeps.TryDequeue(out _)) { }
        }

        // ---- checks -------------------------------------------------------------------------------------

        /// <summary>
        /// Time left on <paramref name="key"/>'s lockout, or null (no lockout, no key, or the policy is off - the
        /// master switch is also the escape hatch, so an existing lockout is not enforced while it is off).
        /// </summary>
        public static TimeSpan? LockoutRemaining(string key, DateTime nowUtc, ThreadPuzzlePolicyConfig config, ThreadPuzzleIpLedger ledger)
        {
            if (key == null || config == null || !config.Enabled || ledger == null)
                return null;

            var until = ledger.GetLockoutUntil(key, nowUtc);
            return until.HasValue ? until.Value - nowUtc : (TimeSpan?)null;
        }

        /// <summary>The live form over a session, against the current snapshot and ledger.</summary>
        public static TimeSpan? LockoutRemaining(Session session, DateTime nowUtc)
        {
            var config = ThreadPuzzlePolicyConfig.Current;

            if (!config.Enabled)
                return null;

            return LockoutRemaining(ThreadPuzzleIpKey.For(session, config.Household), nowUtc, config, LedgerSource());
        }

        /// <summary>
        /// Pure: must <paramref name="run"/>'s copy refuse this character? Yes when the policy removed them from this
        /// run (permanent, whatever the switch says), or when their key is locked out (LockoutRemaining, so not while
        /// the policy is off). EphemeralRealm.Accepts asks this first, which covers every entry route: teleport
        /// validation, the login reroute and the gem handler's re-entry admission.
        /// </summary>
        public static bool RefusesEntry(ThreadDungeonRun run, uint guid, string key, DateTime nowUtc, ThreadPuzzlePolicyConfig config, ThreadPuzzleIpLedger ledger)
            => run != null && (run.IsPuzzleRemoved(guid) || LockoutRemaining(key, nowUtc, config, ledger).HasValue);

        /// <summary>The live form, against the published snapshot and the shared ledger (no PropertyManager read, any thread).</summary>
        public static bool RefusesEntry(ThreadDungeonRun run, Player player)
        {
            if (run == null || player == null)
                return false;

            var config = ThreadPuzzlePolicyConfig.Current;
            return RefusesEntry(run, player.Guid.Full, ThreadPuzzleIpKey.For(player.Session, config.Household), DateTime.UtcNow, config, LedgerSource());
        }

        /// <summary>The refusal line for a session that is locked out, or null.</summary>
        public static string Refusal(Session session, DateTime nowUtc)
        {
            var remaining = LockoutRemaining(session, nowUtc);
            return remaining.HasValue ? PuzzleGateText.Barred(remaining.Value) : null;
        }

        // ---- recording (landblock thread) ---------------------------------------------------------------

        /// <summary>
        /// One scored wrong pull. Records it, decides, applies a triggered lockout to the ledger at once and queues
        /// the sweep. Returns what the caller should tell the puller. <paramref name="run"/> may be null (audit and
        /// the failing-run removal are then skipped).
        /// </summary>
        internal static PuzzleFailOutcome RecordScoredWrong(string key, PuzzleActor actor, ThreadDungeonRun run, DateTime nowUtc,
            ThreadPuzzlePolicyConfig config, ThreadPuzzleIpLedger ledger, string puzzle = null)
        {
            if (config == null || !config.Enabled || key == null || ledger == null)
                return new PuzzleFailOutcome(PuzzleFailOutcomeKind.Ignored, key, default, null);

            var audit = new ThreadPuzzleAudit
            {
                AccountId = actor.AccountId == 0 ? (uint?)null : actor.AccountId,
                CharacterId = actor.Guid == 0 ? (uint?)null : actor.Guid,
                RunId = run?.RunId,
                RunStartGroup = run?.StartGroup,
                IpAddress = actor.IpAddress,
            };

            // One atomic step under the key's lock (ThreadPuzzleIpLedger.RecordFailUnlessLocked): skip if locked, else
            // record, decide (this policy's delegate), and lock + clear the consumed fails on a trigger. Exactly one
            // concurrent pull can create a lockout, so exactly one sweep is queued per lockout.
            var result = ledger.RecordFailUnlessLocked(key, nowUtc, config.Window,
                fails => PuzzleFailPolicy.Decide(fails, nowUtc, config).LockoutUntil, audit);

            if (!result.Recorded)
            {
                // A pull that raced its own key's sweep (the lockout lands here, the removal on the next world tick).
                // Not counted, and no second sweep: the one the lockout queued scans every online character on the
                // key when it drains, this puller included.
                log.Info($"[PUZZLE_POLICY] pull while locked key={key} until={result.LockedUntilBefore.Value:u} account={actor.AccountId} character={actor.Name} (0x{actor.Guid:X8}) run={run}{Tag(puzzle)}");
                return new PuzzleFailOutcome(PuzzleFailOutcomeKind.AlreadyLocked, key, default, result.LockedUntilBefore);
            }

            var decision = PuzzleFailPolicy.Decide(result.Fails, nowUtc, config);

            if (!result.LockoutApplied.HasValue)
            {
                log.Info($"[PUZZLE_POLICY] fail key={key} count={decision.Count}/{decision.Threshold} account={actor.AccountId} character={actor.Name} (0x{actor.Guid:X8}) run={run}{Tag(puzzle)}");
                return new PuzzleFailOutcome(PuzzleFailOutcomeKind.Warned, key, decision, null);
            }

            var applied = result.LockoutApplied.Value;

            pendingSweeps.Enqueue(new LockoutSweepRequest(key, applied, run, actor.Guid));

            log.Warn($"[PUZZLE_POLICY] LOCKOUT key={key} count={decision.Count}/{decision.Threshold} until={applied:u} account={actor.AccountId} character={actor.Name} (0x{actor.Guid:X8}) run={run}{Tag(puzzle)}");

            return new PuzzleFailOutcome(PuzzleFailOutcomeKind.LockedOut, key, decision, applied);
        }

        /// <summary>The puzzle fields for a policy log line (placement, site, type, slot), or empty when the pull carried none.</summary>
        internal static string Tag(string puzzle) => string.IsNullOrEmpty(puzzle) ? "" : " " + puzzle;

        // ---- the sweep (world thread) -------------------------------------------------------------------

        /// <summary>
        /// Pure: what a sweep does. Every candidate leaves every live run they are on the roster of, inside it or not
        /// (coordinator ruling, 2026-10-06: an outside roster member on the key is removed too, so they cannot file a
        /// survey or take a seat at that run's clear), and the request's own puller leaves the failing run. Per run: a
        /// SOLO run ends; a GROUP run ends only when at least one leaver is inside it and nobody else is left inside once
        /// they go (<paramref name="onlineInside"/>: the run's online members inside right now, before this sweep).
        /// A group run that only loses members standing outside it carries on, whoever is or is not inside. Runs in
        /// first-seen order, members in candidate order.
        /// </summary>
        internal static List<LockoutSweepAction> PlanSweep(LockoutSweepRequest request, IEnumerable<LockoutSweepCandidate> candidates,
            Func<ThreadDungeonRun, IReadOnlyList<uint>> onlineInside)
        {
            var order = new List<ThreadDungeonRun>();
            var leaving = new Dictionary<ThreadDungeonRun, List<uint>>();
            var leaverInside = new HashSet<ThreadDungeonRun>();

            void Add(ThreadDungeonRun run, uint guid, bool inside)
            {
                // IsPuzzleRemoved: a second sweep over the same key (or a later lockout of it) is a no-op for anyone
                // already removed, so it cannot tell, teleport or end a run twice.
                if (run == null || run.State == ThreadDungeonRunState.Ended || !run.IsRosterMember(guid) || run.IsPuzzleRemoved(guid))
                    return;

                if (!leaving.TryGetValue(run, out var list))
                {
                    list = new List<uint>();
                    leaving[run] = list;
                    order.Add(run);
                }

                if (!list.Contains(guid))
                    list.Add(guid);

                if (inside)
                    leaverInside.Add(run);
            }

            if (request != null)
                Add(request.FailingRun, request.FailingGuid, inside: false);

            foreach (var c in candidates ?? Enumerable.Empty<LockoutSweepCandidate>())
                Add(c.Run, c.Guid, c.Inside);

            var actions = new List<LockoutSweepAction>(order.Count);

            foreach (var run in order)
            {
                var guids = leaving[run];
                var endRun = !run.IsGroup;

                if (!endRun && leaverInside.Contains(run))
                {
                    var inside = onlineInside?.Invoke(run) ?? Array.Empty<uint>();
                    endRun = !inside.Any(g => !guids.Contains(g) && !run.IsPuzzleRemoved(g));
                }

                actions.Add(new LockoutSweepAction(run, guids.AsReadOnly(), endRun));
            }

            return actions;
        }

        /// <summary>Test seam: ends a run. Production is ThreadDungeonManager.EndRun.</summary>
        internal static Action<ThreadDungeonRun, string> EndRunAction = ThreadDungeonManager.EndRun;

        /// <summary>Test seam: tells one character the lockout line (no teleport). Production resolves the online player.</summary>
        internal static Action<uint, string> TellAction = TellOnline;

        /// <summary>Test seam: tells one character the lockout line (null: already told by this sweep) and, if they stand in the run's copy, teleports them out.</summary>
        internal static Action<ThreadDungeonRun, uint, string> EjectAction = EjectOnline;

        /// <summary>Test seam: the online characters on a key. Production scans PlayerManager.GetAllOnline().</summary>
        internal static Func<string, ThreadPuzzlePolicyConfig, IReadOnlyList<LockoutSweepCandidate>> CandidateSource = OnlineOnKey;

        /// <summary>Test seam: a run's online members inside. Production is ThreadRunPresence.OnlineMembersInside.</summary>
        internal static Func<ThreadDungeonRun, IReadOnlyList<uint>> OnlineInsideSource =
            run => ThreadRunPresence.OnlineMembersInside(run).Select(p => p.Guid.Full).ToList();

        /// <summary>
        /// WORLD THREAD: carries out a planned sweep. Every leaving character is marked removed on the run first (no
        /// re-entry, no clear rewards, no survey or guide credit, no deal seat), then told; an ending run then ends
        /// with <see cref="EndReason"/>, and a continuing group run sees each leaver teleported out.
        /// </summary>
        internal static void Execute(IReadOnlyList<LockoutSweepAction> actions, DateTime untilUtc, DateTime nowUtc)
        {
            var text = PuzzleGateText.LockedOut(untilUtc - nowUtc);

            // One lockout line per character, however many runs they leave.
            var told = new HashSet<uint>();

            foreach (var action in actions ?? Array.Empty<LockoutSweepAction>())
            {
                foreach (var guid in action.Guids)
                    action.Run.MarkPuzzleRemoved(guid);

                if (action.EndRun)
                {
                    foreach (var guid in action.Guids)
                    {
                        if (told.Add(guid))
                            TellAction(guid, text);
                    }

                    log.Info($"[PUZZLE_POLICY] ending {action.Run}: removed {string.Join(",", action.Guids.Select(g => $"0x{g:X8}"))} ({(action.Run.IsGroup ? "last inside" : "solo")})");
                    EndRunAction(action.Run, EndReason);
                }
                else
                {
                    foreach (var guid in action.Guids)
                    {
                        EjectAction(action.Run, guid, told.Add(guid) ? text : null);
                        log.Info($"[PUZZLE_POLICY] removed 0x{guid:X8} from {action.Run}; the group continues");
                    }
                }
            }
        }

        /// <summary>WORLD THREAD: drains every queued sweep. Called from ThreadDungeonManager.Tick.</summary>
        public static void DrainSweeps(DateTime nowUtc)
        {
            while (pendingSweeps.TryDequeue(out var request))
            {
                try
                {
                    var candidates = CandidateSource(request.Key, ThreadPuzzlePolicyConfig.Current);
                    var actions = PlanSweep(request, candidates, OnlineInsideSource);

                    log.Info($"[PUZZLE_POLICY] sweep key={request.Key} online_on_key={candidates.Count} runs={actions.Count}");

                    Execute(actions, request.UntilUtc, nowUtc);
                }
                catch (Exception ex)
                {
                    log.Error($"[PUZZLE_POLICY] sweep for key={request.Key} threw", ex);
                }
            }
        }

        private static IReadOnlyList<LockoutSweepCandidate> OnlineOnKey(string key, ThreadPuzzlePolicyConfig config)
        {
            var found = new List<LockoutSweepCandidate>();

            foreach (var player in PlayerManager.GetAllOnline())
            {
                if (player == null || !string.Equals(ThreadPuzzleIpKey.For(player.Session, config.Household), key, StringComparison.Ordinal))
                    continue;

                // Every live run the character is on the roster of, inside it or not.
                foreach (var run in ThreadDungeonManager.LiveRunsForMember(player.Guid.Full))
                    found.Add(new LockoutSweepCandidate(player.Guid.Full, player.Name, run, ThreadRunPresence.IsMemberInside(run, player)));
            }

            return found;
        }

        private static void TellOnline(uint guid, string text)
            => PlayerManager.GetOnlinePlayer(guid)?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        private static void EjectOnline(ThreadDungeonRun run, uint guid, string text)
        {
            var player = PlayerManager.GetOnlinePlayer(guid);
            if (player == null)
                return;

            if (text != null)
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            // The run's copy, by instance alone: IsMemberInside already answers false for a removed member.
            if (player.Location == null || player.Location.Instance != run.Instance)
                return;

            var exit = ThreadDungeonManager.EvictionExit(player, run);
            if (exit != null)
                WorldManager.ThreadSafeTeleport(player, new ACE.Entity.Position(exit));
        }
    }

    /// <summary>
    /// The production <see cref="IThreadPuzzleFailSink"/>: runs on the LANDBLOCK thread of the pull (see
    /// ThreadPuzzleFailPolicy for the threading). ThreadPuzzleRunHost.OnWrong forwards only SCORED wrong pulls, so a
    /// refused or unscored pull never reaches here.
    /// </summary>
    public sealed class ThreadPuzzleFailPolicySink : IThreadPuzzleFailSink
    {
        public static readonly ThreadPuzzleFailPolicySink Instance = new ThreadPuzzleFailPolicySink();

        private ThreadPuzzleFailPolicySink() { }

        public void OnScoredWrong(ThreadDungeonRun run, Player player, PuzzleGatePlacement placement)
        {
            var config = ThreadPuzzlePolicyConfig.Current;

            if (!config.Enabled || player == null)
                return;

            var session = player.Session;
            var key = ThreadPuzzleIpKey.For(session, config.Household);
            var actor = new PuzzleActor(player.Guid.Full, player.Name, session?.AccountId ?? 0, session?.EndPointC2S?.Address?.ToString());

            var outcome = ThreadPuzzleFailPolicy.RecordScoredWrong(key, actor, run, DateTime.UtcNow, config, ThreadPuzzleFailPolicy.LedgerSource(), placement?.LogTag());

            // The lockout line itself is told by the sweep, once per removed character; a warning is told here.
            if (outcome.Kind == PuzzleFailOutcomeKind.Warned)
                session?.Network.EnqueueSend(new GameMessageSystemChat(PuzzleGateText.FailWarning(outcome.Decision.Remaining), ChatMessageType.Broadcast));
        }
    }
}
