using System.Diagnostics;

using ACE.Server.Command;

namespace ACE.Server.Managers
{
    /// <summary>
    /// WaffleACE: per-opcode attribution for the ph_inbound_messages phase of the world tick. The phase is one
    /// aggregate over every client message handler, so a spike in it names no culprit; this names one.
    ///
    /// Two-layer dispatch, charged ONCE. A client message arrives as a GameMessageOpcode and is dispatched by
    /// InboundMessageManager.HandleClientMessage, which queues a closure onto NetworkManager.InboundMessageQueue.
    /// Most of the interesting traffic arrives under the single generic GameMessageOpcode.GameAction and only
    /// fans out to a GameActionType one level deeper, inside that closure. Timing both layers would double-count;
    /// timing only the outer one would report "GameAction" for nearly everything. So there is exactly ONE timing
    /// scope, opened around the outer handler invoke with the message opcode as its provisional label, and
    /// <see cref="RelabelAsGameAction"/> overwrites that label from inside when the message turns out to be a
    /// game action. The elapsed time is charged once, to whichever label the scope ended up carrying - the
    /// GameActionType where there is one, the GameMessageOpcode otherwise.
    ///
    /// A third layer, same rule: GameActionType.Talk carries both ordinary chat and every @command typed
    /// into chat, and GameActionTalk.Handle dispatches the commands itself. <see cref="RelabelAsCommand"/>
    /// re-labels the same scope from inside that path, so a command is charged to cmd_&lt;registered name&gt;
    /// (key bit <see cref="CommandFlag"/>) and only real chat stays ga_Talk. What cmd_&lt;name&gt; covers is the
    /// handler's INLINE work on the world thread; anything it defers (an ActionChain, a delayed action, a
    /// database callback) lands in whichever phase later runs it and is not charged here.
    ///
    /// Representation: a fixed-capacity per-opcode aggregator with an overflow bucket. Opcode enums are sparse
    /// (GameMessageOpcode runs to 0xF7B0), so an array indexed by raw opcode is out; a dictionary allocates and
    /// would have to be cleared every tick. <see cref="Capacity"/> slots are appended in first-seen order and
    /// found by a linear scan bounded by the number actually filled - a handful on a normal tick - and anything
    /// past capacity is summed into one overflow bucket rather than dropped, so op_calls/op_ms stay exact even
    /// when the slots do not cover everything. Each slot keeps count, total and max, which is what separates one
    /// expensive call from many cheap calls of the same opcode.
    ///
    /// Reset is per tick, aligned to the RunActions() the phase times: WorldManager.UpdateWorld calls
    /// <see cref="BeginPhase"/> immediately before it. The reset is unconditional (three scalar stores) so a tick
    /// that runs with the capture off cannot leave stale slots behind for the next emit to read.
    ///
    /// Cost when off: one static bool read per handler invocation. Cost when on: a timestamp pair plus a scan
    /// per invocation. Allocation-free in both cases, on every path. Measured by
    /// SlowTickCostTests.ReportInboundOpcodeCost on a dev box, walking the opcodes round-robin (the worst case
    /// for the scan - never the slot it looked at last): 5 ns/invocation off, 51 ns on with 8 distinct opcodes
    /// live, 67 ns on with all 32 slots full. Per tick that is 1.0-1.4 us at 20 messages and 10-13 us at 200.
    /// The timestamp pair is the bulk of it, not the scan; a one-timestamp lap design would halve it but would
    /// charge the queue's own gaps - and any non-message action sharing the queue - to whichever handler ran
    /// next, which is the wrong trade for an instrument whose whole job is to name a culprit.
    ///
    /// Thread model: world thread only, enforced rather than assumed - <see cref="BeginMessage"/> returns 0 off
    /// the world thread and <see cref="EndInvoke"/> then does nothing, so a queued action run from anywhere else
    /// is simply not measured instead of corrupting the aggregate.
    ///
    /// What op_ms does NOT cover: the phase also runs actions that are not client message handlers at all
    /// (WebCommandDispatcher.EnqueueWorld puts admin-panel work on the same queue), plus the queue's own
    /// overhead. op_ms is therefore a lower bound on ph_inbound_messages, and a large gap between the two is
    /// itself a finding.
    /// </summary>
    public static class InboundOpcodeProfile
    {
        /// <summary>Distinct opcodes given their own slot in one tick; the rest share the overflow bucket.</summary>
        public const int Capacity = 32;

        /// <summary>Set in a key that names a GameActionType; clear in one that names a GameMessageOpcode.</summary>
        public const long ActionFlag = 1L << 32;

        /// <summary>
        /// Set in a key that names a chat @command (low 32 bits = a <see cref="CommandProfileIds"/> id). Its own
        /// bit, distinct from <see cref="ActionFlag"/>: command ids, GameActionTypes and GameMessageOpcodes all
        /// overlap numerically in the low bits, so only the flag bits keep the three namespaces apart.
        /// </summary>
        public const long CommandFlag = 1L << 33;

        private static readonly double ticksPerMs = Stopwatch.Frequency / 1000.0;

        private static readonly long[] keys = new long[Capacity];
        private static readonly int[] callCounts = new int[Capacity];
        private static readonly long[] totalTicks = new long[Capacity];
        private static readonly long[] maxTicks = new long[Capacity];

        private static int filled;

        private static int overflowCalls;
        private static long overflowTicks;

        private static long currentKey;

        private static bool enabled;

        /// <summary>
        /// Mirrors the slow-tick log's own enable flag; SlowTickReporter writes it once per iteration from the
        /// settings it already refreshes. There is no tunable of its own.
        /// </summary>
        public static bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        /// <summary>Slots filled this tick, 0..<see cref="Capacity"/>, in first-seen order.</summary>
        public static int FilledCount => filled;

        /// <summary>Invocations charged to the overflow bucket this tick.</summary>
        public static int OverflowCalls => overflowCalls;

        /// <summary>Milliseconds charged to the overflow bucket this tick.</summary>
        public static double OverflowMs => overflowTicks / ticksPerMs;

        public static long KeyAt(int index) => keys[index];

        public static int CallsAt(int index) => callCounts[index];

        public static double TotalMsAt(int index) => totalTicks[index] / ticksPerMs;

        public static double MaxMsAt(int index) => maxTicks[index] / ticksPerMs;

        /// <summary>The key that names a GameMessageOpcode.</summary>
        public static long MessageKey(int opcode) => (uint)opcode;

        /// <summary>The key that names a GameActionType.</summary>
        public static long ActionKey(int opcode) => ActionFlag | (uint)opcode;

        /// <summary>The key that names a chat @command by its <see cref="CommandProfileIds"/> id.</summary>
        public static long CommandKey(int commandId) => CommandFlag | (uint)commandId;

        /// <summary>
        /// Clears the per-tick aggregate. Called immediately before the InboundMessageQueue.RunActions() that
        /// WorldTickPhase.InboundMessages times, so the aggregate covers exactly that phase. Unconditional and
        /// O(1): the slot arrays are bounded by <see cref="filled"/> and are overwritten on first use, so only
        /// the counters need clearing.
        /// </summary>
        public static void BeginPhase()
        {
            filled = 0;
            overflowCalls = 0;
            overflowTicks = 0;
            currentKey = 0;
        }

        /// <summary>
        /// Opens the timing scope for one queued client message, labelled with its GameMessageOpcode. Returns
        /// the start timestamp, or 0 when the capture is off or this is not the world thread - in which case
        /// <see cref="EndInvoke"/> does nothing. Every caller must pair this with EndInvoke in a finally.
        /// </summary>
        public static long BeginMessage(int opcode)
        {
            if (!enabled || !WorldTickProfile.IsWorldThread)
                return 0;

            currentKey = MessageKey(opcode);

            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Re-labels the open scope with the GameActionType the message turned out to carry. Called from
        /// InboundMessageManager.HandleGameAction, which runs inside the scope opened by BeginMessage - so this
        /// replaces the generic GameAction label rather than starting a second charge.
        ///
        /// Carries the same world-thread guard as <see cref="BeginMessage"/>, and for the same reason: this
        /// writes the shared current-label field, so an off-thread call would mislabel whatever charge the
        /// world thread closes next - silently, and as a plausible-looking opcode. Under today's call graph
        /// game-action dispatch is only ever reached nested inside the world-thread closure, so the guard is
        /// latent; it is here so that a refactor moving that dispatch off the world thread loses the
        /// measurement rather than corrupting it, which is the contract this class states.
        /// </summary>
        public static void RelabelAsGameAction(int opcode)
        {
            if (!enabled || !WorldTickProfile.IsWorldThread)
                return;

            currentKey = ActionKey(opcode);
        }

        /// <summary>
        /// Re-labels the open scope as a chat @command. Called from GameActionTalk.Handle, which runs inside the
        /// scope BeginMessage opened and HandleGameAction already re-labelled as ga_Talk - so, like
        /// <see cref="RelabelAsGameAction"/>, this replaces the label and never starts a second charge.
        ///
        /// <paramref name="resolved"/> is the handler that is about to run, or null for an @-message that will
        /// not run one (cmd_unknown). The label is the handler's REGISTERED name via
        /// <see cref="CommandProfileIds"/>, never the typed text, so its cardinality is bounded by the command
        /// registry. Same world-thread guard as RelabelAsGameAction and for the same reason. Allocation-free
        /// once each command has been charged once.
        /// </summary>
        public static void RelabelAsCommand(CommandHandlerInfo resolved)
        {
            if (!enabled || !WorldTickProfile.IsWorldThread)
                return;

            currentKey = CommandKey(CommandProfileIds.IdFor(resolved));
        }

        /// <summary>Closes the scope opened by <see cref="BeginMessage"/> and charges its time to the current label.</summary>
        public static void EndInvoke(long started)
        {
            if (started == 0)
                return;

            Charge(currentKey, Stopwatch.GetTimestamp() - started);
        }

        /// <summary>
        /// Charges one invocation to a key. Internal rather than private only so ACE.Server.Tests can drive the
        /// aggregation with synthetic tick amounts (InternalsVisibleTo, ACE.Server.csproj:15), the same seam
        /// TickPhaseAccumulator gets from taking its timestamps as parameters; production reaches it only
        /// through <see cref="EndInvoke"/>.
        /// </summary>
        internal static void Charge(long key, long elapsedTicks)
        {
            if (elapsedTicks < 0)
                elapsedTicks = 0;

            for (var i = 0; i < filled; i++)
            {
                if (keys[i] != key)
                    continue;

                callCounts[i]++;
                totalTicks[i] += elapsedTicks;

                if (elapsedTicks > maxTicks[i])
                    maxTicks[i] = elapsedTicks;

                return;
            }

            if (filled >= Capacity)
            {
                overflowCalls++;
                overflowTicks += elapsedTicks;

                return;
            }

            keys[filled] = key;
            callCounts[filled] = 1;
            totalTicks[filled] = elapsedTicks;
            maxTicks[filled] = elapsedTicks;

            filled++;
        }
    }
}
