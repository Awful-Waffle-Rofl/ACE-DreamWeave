using System;
using System.Globalization;
using System.Text;

using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.GameMessages;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Everything one SLOW_TICK line reports. A reusable class rather than a struct: SlowTickReporter keeps
    /// one instance and refills it on each emit, so emitting allocates only the final string.
    /// </summary>
    public sealed class SlowTickSnapshot
    {
        public const int TopLandblocks = 5;

        /// <summary>Per-opcode slots the line reports, slowest first.</summary>
        public const int TopOpcodes = 5;

        public long Iteration;
        public double TotalMs;
        public double WorldMs;
        public double MedianMs;
        public int Players;
        public int ShardQueue;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public double GcPauseMs;
        public int Suppressed;
        public double SuppressedMaxMs;

        /// <summary>
        /// World-DB weenie cache misses taken during the iteration, on ANY thread (the counter is process-wide):
        /// each one is a synchronous multi-query world-DB read, so a large value on a slow landblock tick names a
        /// cold-cache stall. A background warm-up running at the same time also counts here.
        /// </summary>
        public long WeenieMiss;

        public readonly double[] PhaseMs = new double[WorldTickPhases.Count];

        /// <summary>Whether the world updated this iteration. The par/grp/lb fields are written only when it did.</summary>
        public bool WorldUpdated;

        /// <summary>Loaded landblock group count - the unit Parallel.ForEach partitions.</summary>
        public int Groups;

        /// <summary>Max group wall time in TickPhysics; NaN when MultiThreadedLandblockGroupPhysicsTicking is off (field omitted).</summary>
        public double GroupPhysicsMaxMs = double.NaN;

        /// <summary>Max group wall time in TickMultiThreadedWork; NaN when MultiThreadedLandblockGroupTicking is off (field omitted).</summary>
        public double GroupMultiMaxMs = double.NaN;

        public int LandblockCount;
        public double LandblockSumMs;

        /// <summary>Number of the Top* slots filled, 0..TopLandblocks, sorted slowest first.</summary>
        public int TopCount;
        public readonly uint[] TopId = new uint[TopLandblocks];
        public readonly uint[] TopInstance = new uint[TopLandblocks];
        public readonly double[] TopMs = new double[TopLandblocks];
        public readonly int[] TopPlayers = new int[TopLandblocks];
        public readonly int[] TopObjects = new int[TopLandblocks];

        /// <summary>
        /// Per top slot: the landblock's largest and second-largest tick section (a LandblockTickSection index,
        /// -1 when it had fewer reportable sections) and their ms. Chosen by LandblockTickSections.TopTwo only
        /// for a landblock that wins a slot.
        /// </summary>
        public readonly int[] TopS1 = new int[TopLandblocks];
        public readonly double[] TopS1Ms = new double[TopLandblocks];
        public readonly int[] TopS2 = new int[TopLandblocks];
        public readonly double[] TopS2Ms = new double[TopLandblocks];

        /// <summary>
        /// Inbound-message attribution for the iteration, from <see cref="InboundOpcodeProfile"/>. OpCalls and
        /// OpMs are the totals across EVERY opcode including the overflow bucket, so they stay exact even when
        /// the reported slots do not cover everything; OpOverCalls/OpOverMs are the overflow's share of those
        /// totals. OpTop* are the slowest <see cref="TopOpcodes"/> by total time, with the per-opcode call count
        /// and slowest single call that tell "one expensive call" apart from "many cheap calls".
        /// </summary>
        public int OpCalls;
        public double OpMs;
        public int OpOverCalls;
        public double OpOverMs;

        /// <summary>Number of the OpTop* slots filled, 0..TopOpcodes, sorted slowest first.</summary>
        public int OpTopCount;
        public readonly long[] OpTopKey = new long[TopOpcodes];
        public readonly int[] OpTopCalls = new int[TopOpcodes];
        public readonly double[] OpTopMs = new double[TopOpcodes];
        public readonly double[] OpTopMaxMs = new double[TopOpcodes];

        /// <summary>
        /// Per-phase attribution inside the player-to-vendor sell handler, from <see cref="SellPhaseProfile"/>.
        /// SellCalls/SellMs are the handler's own count and total for the iteration, measured by that
        /// instrument rather than derived from the op_* block, and SellRestMs is the part of SellMs no phase
        /// covers. THE INVARIANT the fields exist to make checkable: sum(SellPhaseMs) + SellRestMs == SellMs.
        /// The whole block is omitted when SellCalls is 0, which is every iteration with no sale in it.
        /// </summary>
        public int SellCalls;
        public double SellMs;
        public double SellRestMs;

        /// <summary>
        /// Deposits that ran but produced no Deposit phase charge, because the work item executed off the
        /// world thread (see SellPhaseProfile.NoteDepositExecuted). Non-zero means sell_deposit_ms undercounts
        /// by that many items and sell_purchase_ms carries their time; the sum invariant is unaffected. Emitted
        /// unconditionally inside the sell block, INCLUDING as a zero, because the whole reason it exists is
        /// that a reader must be able to tell a cheap deposit from an unmeasured one.
        /// </summary>
        public int SellDepositUnmeasured;

        public readonly int[] SellPhaseCalls = new int[SellPhases.Count];
        public readonly double[] SellPhaseMs = new double[SellPhases.Count];

        public void ResetSell()
        {
            SellCalls = 0;
            SellMs = 0;
            SellRestMs = 0;
            SellDepositUnmeasured = 0;

            Array.Clear(SellPhaseCalls, 0, SellPhaseCalls.Length);
            Array.Clear(SellPhaseMs, 0, SellPhaseMs.Length);
        }

        /// <summary>
        /// Per-phase attribution inside AccountVaultStore.TryDeposit, from
        /// <see cref="VaultDepositPhaseProfile"/> - one level below the sell block, which names the deposit as
        /// the culprit without saying which part of it. VaultDepositCalls/VaultDepositMs are the deposits'
        /// own count and total for the iteration, and VaultDepositRestMs is the part of VaultDepositMs no
        /// phase covers. THE INVARIANT the fields exist to make checkable:
        /// sum(VaultDepositPhaseMs) + VaultDepositRestMs == VaultDepositMs. The whole block is omitted when
        /// VaultDepositCalls is 0, which is every iteration with no vault deposit in it.
        ///
        /// SECOND, INDEPENDENT CHECK, and the reason both blocks are emitted: on a sale-only iteration
        /// VaultDepositMs should track SellPhaseMs[Deposit] closely, because the two time the same call from
        /// opposite sides. See VaultDepositPhaseProfile for the two divergences that are legitimate.
        /// </summary>
        public int VaultDepositCalls;
        public double VaultDepositMs;
        public double VaultDepositRestMs;

        public readonly int[] VaultDepositPhaseCalls = new int[VaultDepositPhases.Count];
        public readonly double[] VaultDepositPhaseMs = new double[VaultDepositPhases.Count];

        public void ResetVaultDeposit()
        {
            VaultDepositCalls = 0;
            VaultDepositMs = 0;
            VaultDepositRestMs = 0;

            Array.Clear(VaultDepositPhaseCalls, 0, VaultDepositPhaseCalls.Length);
            Array.Clear(VaultDepositPhaseMs, 0, VaultDepositPhaseMs.Length);
        }

        public void ResetOpcodes()
        {
            OpCalls = 0;
            OpMs = 0;
            OpOverCalls = 0;
            OpOverMs = 0;
            OpTopCount = 0;
        }

        /// <summary>
        /// Offers one opcode's aggregate to the top slots (slowest first) and adds it to the totals. Insertion
        /// into fixed arrays, no allocation - the same shape as <see cref="OfferLandblock"/>.
        /// </summary>
        public void OfferOpcode(long key, int calls, double ms, double maxMs)
        {
            OpCalls += calls;
            OpMs += ms;

            var at = OpTopCount;

            while (at > 0 && OpTopMs[at - 1] < ms)
                at--;

            if (at >= TopOpcodes)
                return;

            var last = OpTopCount < TopOpcodes ? OpTopCount : TopOpcodes - 1;

            for (var i = last; i > at; i--)
            {
                OpTopKey[i] = OpTopKey[i - 1];
                OpTopCalls[i] = OpTopCalls[i - 1];
                OpTopMs[i] = OpTopMs[i - 1];
                OpTopMaxMs[i] = OpTopMaxMs[i - 1];
            }

            OpTopKey[at] = key;
            OpTopCalls[at] = calls;
            OpTopMs[at] = ms;
            OpTopMaxMs[at] = maxMs;

            if (OpTopCount < TopOpcodes)
                OpTopCount++;
        }

        /// <summary>
        /// Adds the overflow bucket - every invocation of an opcode past the aggregator's slot capacity - to the
        /// totals and to its own pair of fields. Never competes for a top slot: the bucket names no single opcode.
        /// </summary>
        public void AddOpcodeOverflow(int calls, double ms)
        {
            OpCalls += calls;
            OpMs += ms;
            OpOverCalls += calls;
            OpOverMs += ms;
        }

        public void ResetLandblocks()
        {
            Groups = 0;
            GroupPhysicsMaxMs = double.NaN;
            GroupMultiMaxMs = double.NaN;
            LandblockCount = 0;
            LandblockSumMs = 0;
            TopCount = 0;
        }

        /// <summary>
        /// Offers one landblock to the top-N slots (slowest first) and adds it to the count and sum.
        /// Insertion into fixed arrays, no allocation.
        /// </summary>
        public void OfferLandblock(uint id, uint instance, double ms, int players, int objects)
        {
            OfferLandblock(id, instance, ms, players, objects, null);
        }

        /// <summary>
        /// As above, plus the landblock's per-section ms for the same tick (indexed by LandblockTickSection;
        /// null for none). The top two sections are chosen only if the landblock wins a slot, so a landblock
        /// that does not costs no more than before.
        /// </summary>
        public void OfferLandblock(uint id, uint instance, double ms, int players, int objects, double[] sectionMs)
        {
            LandblockCount++;
            LandblockSumMs += ms;

            var at = TopCount;

            while (at > 0 && TopMs[at - 1] < ms)
                at--;

            if (at >= TopLandblocks)
                return;

            var last = TopCount < TopLandblocks ? TopCount : TopLandblocks - 1;

            for (var i = last; i > at; i--)
            {
                TopId[i] = TopId[i - 1];
                TopInstance[i] = TopInstance[i - 1];
                TopMs[i] = TopMs[i - 1];
                TopPlayers[i] = TopPlayers[i - 1];
                TopObjects[i] = TopObjects[i - 1];
                TopS1[i] = TopS1[i - 1];
                TopS1Ms[i] = TopS1Ms[i - 1];
                TopS2[i] = TopS2[i - 1];
                TopS2Ms[i] = TopS2Ms[i - 1];
            }

            TopId[at] = id;
            TopInstance[at] = instance;
            TopMs[at] = ms;
            TopPlayers[at] = players;
            TopObjects[at] = objects;

            if (sectionMs != null)
            {
                LandblockTickSections.TopTwo(sectionMs, out TopS1[at], out TopS1Ms[at], out TopS2[at], out TopS2Ms[at]);
            }
            else
            {
                TopS1[at] = -1;
                TopS1Ms[at] = 0;
                TopS2[at] = -1;
                TopS2Ms[at] = 0;
            }

            if (TopCount < TopLandblocks)
                TopCount++;
        }
    }

    /// <summary>
    /// WaffleACE: renders the one-line logfmt SLOW_TICK record. Pure; invariant culture throughout, so a
    /// host whose culture writes a decimal comma cannot break the key=value parse.
    ///
    /// Fixed key order:
    ///   SLOW_TICK iter total_ms world_ms median_ms players shard_queue gc0 gc1 gc2 gc_pause_ms suppressed
    ///   suppressed_max_ms weenie_miss ph_&lt;phase&gt; (x13, WorldTickPhase order) par grp_physics_max_ms grp_multi_max_ms
    ///   lb_count lb_sum_ms lb1 lb1_inst lb1_ms lb1_players lb1_objs ... lbN_*
    ///   op_calls op_ms op_over_calls op_over_ms op1 op1_n op1_ms op1_max_ms ... opN_*
    ///   lb1_s1 lb1_s1_ms lb1_s2 lb1_s2_ms ... lbN_s*
    ///   sell_n sell_ms sell_rest_ms sell_deposit_unmeasured_n sell_&lt;phase&gt;_n sell_&lt;phase&gt;_ms (x10, SellPhase order)
    ///   vd_n vd_ms vd_rest_ms vd_&lt;phase&gt;_n vd_&lt;phase&gt;_ms (x10, VaultDepositPhase order)
    ///
    /// par through lbN_* appear only when the world updated this iteration; grp_physics_max_ms /
    /// grp_multi_max_ms only when the matching multi-threaded ticking option is on; lbN only for filled slots.
    /// The op_* block is always present, because the inbound-message phase runs on every iteration.
    /// op_over_calls / op_over_ms appear only when the per-opcode aggregator overflowed its slots.
    /// The lbN_s* block is appended AFTER the op_* block (so every earlier key keeps its position) and, like
    /// the other lb fields, only when the world updated; lbN_s1 is omitted for a slot with no reportable
    /// section and lbN_s2 for one with fewer than two.
    ///
    /// Reading it: lbN_ms is that landblock's own serial time (physics + multi-threaded + single-threaded work)
    /// this iteration, and lb_sum_ms is their sum - which CAN exceed the wall time, because groups run in
    /// parallel. The critical path is grp_*_max_ms against ph_lb_physics / ph_lb_multithreaded. For a dormant
    /// landblock (one with no players for a while) lbN_ms is multi-threaded + single-threaded work, since
    /// physics does not run; before the LandblockTickTimer fix it was single-threaded work only.
    ///
    /// lbN_s1 / lbN_s2 name that landblock's two largest tick sections in the SAME iteration, with their ms:
    /// physics, run_actions (its action queue), monster (Monster_Tick loop), gen_update (GeneratorUpdate loop),
    /// gen_regen (GeneratorRegeneration loop), lb_heartbeat (the landblock's own heartbeat: decay, dormancy and
    /// unload check), db_save (periodic SaveDB), player_tick (Player_Tick loop), wo_heartbeat (the per-object
    /// WorldObject.Heartbeat loop). See LandblockTickSection. The sections do not cover the glue between them
    /// (mostly ProcessPendingWorldObjectAdditionsAndRemovals), so they sum to a little less than lbN_ms.
    /// A section under 0.005 ms counts as zero (it would print as 0).
    ///
    /// The op_* block attributes ph_inbound_messages (see <see cref="InboundOpcodeProfile"/>). opN is the
    /// informative identifier - ga_&lt;GameActionType&gt; for a game action, gm_&lt;GameMessageOpcode&gt; for
    /// any other client message, cmd_&lt;command&gt; for an @command typed into chat (the REGISTERED command
    /// name, lowercased; cmd_unknown for one that was not run - unknown, unauthorized, wrong parameter count
    /// or not in world), and ga_0xNNNN / gm_0xNNNN / cmd_0xNNNN when the value has no name. opN_n is
    /// how many times it ran and opN_max_ms the slowest single run, so a spike of many cheap calls
    /// (large opN_n, tiny opN_max_ms) reads differently from one expensive call (opN_n=1, opN_max_ms=opN_ms).
    /// op_ms is a LOWER BOUND on ph_inbound_messages: the phase also runs queued work that is not a client
    /// message handler, so a large gap between the two is a finding in itself.
    ///
    /// The sell_* block goes one level deeper still, inside the player-to-vendor sell handler that op1=ga_Sell
    /// names (see <see cref="SellPhaseProfile"/>), and appears only on an iteration that ran one. sell_n and
    /// sell_ms are the handler's count and total as THIS instrument measured them; sell_&lt;phase&gt;_n /
    /// sell_&lt;phase&gt;_ms are the per-phase call count and accumulated total (accumulated, so a 200-item sale
    /// emits the same fields as a 1-item sale); and sell_rest_ms is the part of sell_ms that no phase covers.
    /// Read it as an identity to check, not as prose: sum of the ten sell_&lt;phase&gt;_ms plus sell_rest_ms
    /// equals sell_ms, always, because the phases are disjoint leaves and the remainder is what is left over.
    /// A large sell_rest_ms means a real cost has no phase yet - which is the answer, not a gap in the data.
    /// sell_ms is at most op1_ms for ga_Sell: the opcode charge additionally covers the payload parse and the
    /// dispatch around the handler.
    ///
    /// sell_deposit_unmeasured_n is the one place the sell block admits to a gap the remainder cannot show.
    /// The vault deposit is charged from inside a queued work item that another thread can end up running
    /// (see <see cref="SellPhaseProfile"/>), and the profile only measures the world thread, so N there means
    /// sell_deposit_ms is missing N items and sell_purchase_ms is carrying their time instead. Read
    /// sell_deposit_n + sell_deposit_unmeasured_n as the number of deposits that actually ran. It is emitted
    /// even as a zero, deliberately: a reader comparing sell_deposit_ms against an out-of-band measurement
    /// needs to know the field is complete, and an omitted key would mean "no sale ran" rather than "none".
    ///
    /// The vd_* block goes one level deeper again - the call sell_deposit names (see
    /// <see cref="VaultDepositPhaseProfile"/>) - and appears only on an iteration that ran one. Wave 3
    /// (SPEC-vault-batch-deposit.md) added a SECOND entry point: AccountVaultStore.TryDeposit (the
    /// single-item path, unchanged) still arms the deposit scope with BeginDeposit/EndDeposit, one call per
    /// item; PersonalVendor.DepositItems now also calls AccountVaultStore.TryDepositBatch DIRECTLY for a
    /// whole sale in one Enqueue, arming the scope with BeginDeposit/EndBatchDeposit instead, so vd_n still
    /// advances once per ITEM even though only one physical call ran. It is read exactly like the sell block:
    /// vd_n and vd_ms are the deposits' count and total as THIS instrument measured them, vd_&lt;phase&gt;_n /
    /// vd_&lt;phase&gt;_ms are the per-phase count and accumulated total, and the ten vd_&lt;phase&gt;_ms plus
    /// vd_rest_ms equal vd_ms, always.
    ///
    /// vd_upsert_n IS NOT AN ITEM COUNT ON THE BATCH PATH, and reading it as one understates the
    /// compression the batch buys. See <see cref="VaultDepositPhase.Upsert"/>'s own remarks: on the batch
    /// path it counts distinct (table, key) GROUPS, not statements and not items, so vd_upsert_n / vd_n
    /// is exactly the batch's compression ratio (groups over items) - a mule sale of 53 identical items
    /// should read close to 1/53 there. vd_reload_n / vd_reload_ms are new in wave 3 too: the batch's one
    /// remaining per-sale round trip (the whole-account re-read phase D replaced every per-item read-back
    /// with), named instead of falling into vd_rest_ms the way it silently did before.
    ///
    /// THE CROSS-CHECK, which is why both blocks ship: vd_ms and sell_deposit_ms measure the same call from
    /// opposite sides, so on an iteration whose only deposits came from a sale they should track each other
    /// closely, and a divergence means one of the two instruments is wrong. Two divergences are legitimate
    /// and must not be read as a defect. vd_ms EXCEEDS sell_deposit_ms when a non-sale caller deposited in the
    /// same tick (a market path does: VaultMarketItemStore.cs:425), because vd_* is armed by TryDeposit or
    /// TryDepositBatch directly rather than by the sell handler - compare vd_n against sell_deposit_n, which
    /// names how many extra deposits vd_ms is carrying. And vd_ms is always slightly SMALLER per item than
    /// sell_deposit_ms, because the sell scope additionally covers the call around the deposit and its early
    /// refusals return before any phase. A deposit that ran off the world thread is missing from BOTH (both
    /// instruments refuse off-thread) and is counted once in sell_deposit_unmeasured_n.
    /// </summary>
    public static class SlowTickLine
    {
        public const string Marker = "SLOW_TICK";

        public static void Append(StringBuilder sb, SlowTickSnapshot s)
        {
            sb.Append(Marker);

            Long(sb, "iter", s.Iteration);
            Ms(sb, "total_ms", s.TotalMs);
            Ms(sb, "world_ms", s.WorldMs);
            Ms(sb, "median_ms", s.MedianMs);
            Long(sb, "players", s.Players);
            Long(sb, "shard_queue", s.ShardQueue);
            Long(sb, "gc0", s.Gc0);
            Long(sb, "gc1", s.Gc1);
            Long(sb, "gc2", s.Gc2);
            Ms(sb, "gc_pause_ms", s.GcPauseMs);
            Long(sb, "suppressed", s.Suppressed);
            Ms(sb, "suppressed_max_ms", s.SuppressedMaxMs);
            Long(sb, "weenie_miss", s.WeenieMiss);

            for (var i = 0; i < WorldTickPhases.Count; i++)
            {
                sb.Append(" ph_").Append(WorldTickPhases.TagNames[i]).Append('=');
                sb.Append(s.PhaseMs[i].ToString("0.##", CultureInfo.InvariantCulture));
            }

            if (s.WorldUpdated)
            {
                Long(sb, "par", s.Groups);

                if (!double.IsNaN(s.GroupPhysicsMaxMs))
                    Ms(sb, "grp_physics_max_ms", s.GroupPhysicsMaxMs);

                if (!double.IsNaN(s.GroupMultiMaxMs))
                    Ms(sb, "grp_multi_max_ms", s.GroupMultiMaxMs);

                Long(sb, "lb_count", s.LandblockCount);
                Ms(sb, "lb_sum_ms", s.LandblockSumMs);

                for (var i = 0; i < s.TopCount && i < SlowTickSnapshot.TopLandblocks; i++)
                {
                    var n = (i + 1).ToString(CultureInfo.InvariantCulture);

                    sb.Append(" lb").Append(n).Append("=0x").Append(s.TopId[i].ToString("X8", CultureInfo.InvariantCulture));
                    sb.Append(" lb").Append(n).Append("_inst=0x").Append(s.TopInstance[i].ToString("X8", CultureInfo.InvariantCulture));
                    sb.Append(" lb").Append(n).Append("_ms=").Append(s.TopMs[i].ToString("0.##", CultureInfo.InvariantCulture));
                    sb.Append(" lb").Append(n).Append("_players=").Append(s.TopPlayers[i].ToString(CultureInfo.InvariantCulture));
                    sb.Append(" lb").Append(n).Append("_objs=").Append(s.TopObjects[i].ToString(CultureInfo.InvariantCulture));
                }
            }

            // Appended last so every key before it keeps the position it had; the inbound-message phase runs on
            // every iteration, so unlike the landblock block this one is not conditional on the world updating.
            Long(sb, "op_calls", s.OpCalls);
            Ms(sb, "op_ms", s.OpMs);

            if (s.OpOverCalls > 0)
            {
                Long(sb, "op_over_calls", s.OpOverCalls);
                Ms(sb, "op_over_ms", s.OpOverMs);
            }

            for (var i = 0; i < s.OpTopCount && i < SlowTickSnapshot.TopOpcodes; i++)
            {
                var n = (i + 1).ToString(CultureInfo.InvariantCulture);

                sb.Append(" op").Append(n).Append('=');
                AppendOpcode(sb, s.OpTopKey[i]);
                sb.Append(" op").Append(n).Append("_n=").Append(s.OpTopCalls[i].ToString(CultureInfo.InvariantCulture));
                sb.Append(" op").Append(n).Append("_ms=").Append(s.OpTopMs[i].ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(" op").Append(n).Append("_max_ms=").Append(s.OpTopMaxMs[i].ToString("0.##", CultureInfo.InvariantCulture));
            }

            // Appended after the op_* block for the same reason that block was appended last: no existing key
            // moves. Same condition as the lbN fields they describe.
            if (s.WorldUpdated)
            {
                for (var i = 0; i < s.TopCount && i < SlowTickSnapshot.TopLandblocks; i++)
                {
                    if (s.TopS1[i] < 0)
                        continue;

                    var n = (i + 1).ToString(CultureInfo.InvariantCulture);

                    sb.Append(" lb").Append(n).Append("_s1=").Append(LandblockTickSections.Names[s.TopS1[i]]);
                    sb.Append(" lb").Append(n).Append("_s1_ms=").Append(s.TopS1Ms[i].ToString("0.##", CultureInfo.InvariantCulture));

                    if (s.TopS2[i] < 0)
                        continue;

                    sb.Append(" lb").Append(n).Append("_s2=").Append(LandblockTickSections.Names[s.TopS2[i]]);
                    sb.Append(" lb").Append(n).Append("_s2_ms=").Append(s.TopS2Ms[i].ToString("0.##", CultureInfo.InvariantCulture));
                }
            }

            // Appended last, for the same no-key-moves reason as the two blocks above, and only on an
            // iteration that actually ran a sell handler - which is a small minority of them.
            if (s.SellCalls > 0)
            {
                Long(sb, "sell_n", s.SellCalls);
                Ms(sb, "sell_ms", s.SellMs);
                Ms(sb, "sell_rest_ms", s.SellRestMs);
                Long(sb, "sell_deposit_unmeasured_n", s.SellDepositUnmeasured);

                // EVERY phase is emitted, including one with zero calls, and that is deliberate: this
                // instrument exists because a phase that reported nothing was indistinguishable from a fast
                // one. A zero here says "that code path did not run this time" (payout on a mule deposit,
                // deposit on an ordinary vendor sale) rather than "not measured".
                for (var i = 0; i < SellPhases.Count; i++)
                {
                    var tag = SellPhases.TagNames[i];

                    sb.Append(" sell_").Append(tag).Append("_n=").Append(s.SellPhaseCalls[i].ToString(CultureInfo.InvariantCulture));
                    sb.Append(" sell_").Append(tag).Append("_ms=").Append(s.SellPhaseMs[i].ToString("0.##", CultureInfo.InvariantCulture));
                }
            }

            // Appended after the sell block, for the same no-key-moves reason every block above it was
            // appended last, and only on an iteration that actually ran a vault deposit. That is a SUPERSET
            // of the iterations that ran a sale, not a subset: a deposit can arrive from a caller that is not
            // a sale, so this block can be present while the sell block is absent.
            if (s.VaultDepositCalls > 0)
            {
                Long(sb, "vd_n", s.VaultDepositCalls);
                Ms(sb, "vd_ms", s.VaultDepositMs);
                Ms(sb, "vd_rest_ms", s.VaultDepositRestMs);

                // EVERY phase is emitted, including one with zero calls, for the reason the sell block gives:
                // a zero says "that branch did not run this time" (roundtrip and destroy on a pristine
                // deposit, findvault on anything that collapsed) rather than "not measured".
                for (var i = 0; i < VaultDepositPhases.Count; i++)
                {
                    var tag = VaultDepositPhases.TagNames[i];

                    sb.Append(" vd_").Append(tag).Append("_n=").Append(s.VaultDepositPhaseCalls[i].ToString(CultureInfo.InvariantCulture));
                    sb.Append(" vd_").Append(tag).Append("_ms=").Append(s.VaultDepositPhaseMs[i].ToString("0.##", CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// Renders one InboundOpcodeProfile key as a logfmt-safe value: ga_ for a GameActionType, gm_ for a
        /// GameMessageOpcode, then the enum's name, or 0xNNNN when the enum does not define that value (an
        /// unhandled opcode is exactly the case worth seeing, so it must not render as blank). Enum names are
        /// C# identifiers, so no value produced here can contain a space or an equals sign. Emit path only -
        /// System.Enum.GetName allocates, which is why nothing on the hot path ever resolves a name.
        ///
        /// cmd_ for a chat @command: the command's registered name from <see cref="CommandProfileIds"/>, which
        /// sanitizes it to [a-z0-9_-] at registration, "unknown" for id 0, or 0xNNNN for an id it has no name
        /// for. The command flag is tested FIRST and is its own bit, so a command id can never be read as a
        /// game action or message opcode of the same number.
        /// </summary>
        private static void AppendOpcode(StringBuilder sb, long key)
        {
            var raw = (int)(key & 0xFFFFFFFFL);

            string name;

            if ((key & InboundOpcodeProfile.CommandFlag) != 0)
            {
                sb.Append("cmd_");
                name = raw == CommandProfileIds.Unknown ? CommandProfileIds.UnknownName : CommandProfileIds.NameOf(raw);
            }
            else if ((key & InboundOpcodeProfile.ActionFlag) != 0)
            {
                sb.Append("ga_");
                name = System.Enum.GetName(typeof(GameActionType), (GameActionType)raw);
            }
            else
            {
                sb.Append("gm_");
                name = System.Enum.GetName(typeof(GameMessageOpcode), (GameMessageOpcode)raw);
            }

            if (name != null)
                sb.Append(name);
            else
                sb.Append("0x").Append(raw.ToString("X4", CultureInfo.InvariantCulture));
        }

        private static void Long(StringBuilder sb, string key, long value)
        {
            sb.Append(' ').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Ms(StringBuilder sb, string key, double value)
        {
            sb.Append(' ').Append(key).Append('=').Append(value.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }
}
