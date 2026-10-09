using System;
using System.Text;

using ACE.Common;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// WaffleACE: the world-thread end of the slow-tick capture. Called once per world-loop iteration from
    /// WorldManager.UpdateWorld, straight after WorldTickProfile.EndIteration and before the idle sleep, so
    /// its own cost and the sleep are both outside the measured total.
    ///
    /// Every iteration: records the per-phase tick metrics (ServerMetrics.RecordTickPhases), refreshes the
    /// world_tick_slow_log_* tunables at most every <see cref="SettingsRefreshMs"/>, mirrors the enable flag
    /// onto <see cref="InboundOpcodeProfile"/> (which has no tunable of its own), and asks the
    /// <see cref="SlowTickDetector"/> whether the iteration was slow. Only on an Emit verdict does it gather
    /// anything else - GC deltas, online players, shard queue depth, a walk of the loaded landblocks and
    /// groups - and write ONE logfmt WARN line (format: <see cref="SlowTickLine"/>). Independent of
    /// ServerPerformanceMonitor, which records nothing unless it has been started.
    ///
    /// The whole body is contained: a diagnostic must never be what stops the world thread, so any throw
    /// is logged (at most once a minute) and swallowed.
    ///
    /// Opcode attribution: <see cref="InboundOpcodeProfile"/> aggregates the inbound-message phase per opcode
    /// while it runs; the emit only copies that aggregate out, and does so on every emit, because that phase
    /// runs whether or not the world updated. <see cref="SellPhaseProfile"/> is copied out the same way and
    /// on the same terms - it needs no enable flag written here, because it reads
    /// InboundOpcodeProfile.Enabled directly rather than keeping a second mirror of the same tunable.
    ///
    /// Landblock attribution: each landblock stamps its own serial tick time and the iteration number at
    /// the end of Landblock.TickSingleThreadedWork; only landblocks stamped with THIS iteration are counted.
    /// lbN_ms is serial per-landblock time and lb_sum_ms can exceed wall time, because groups tick in
    /// parallel; the critical path is grp_*_max_ms against ph_lb_physics / ph_lb_multithreaded.
    ///
    /// Section attribution: at the same stamp each landblock snapshots its per-section ms for that tick
    /// (LandblockTickTimer), and the reporter names the top two for every landblock that wins a top slot
    /// (lbN_s1 / lbN_s2, see <see cref="SlowTickLine"/>). The sections are captured whether or not
    /// ServerPerformanceMonitor is running - they are the same stopwatch readings it is handed, kept locally.
    /// </summary>
    public static class SlowTickReporter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const long SettingsRefreshMs = 5000;

        private const long ErrorLogIntervalMs = 60000;

        private static readonly SlowTickDetector detector = new SlowTickDetector();

        private static readonly SlowTickSnapshot snapshot = new SlowTickSnapshot();

        private static readonly StringBuilder lineBuilder = new StringBuilder(1024);

        private static SlowTickSettings settings;

        private static bool settingsLoaded;
        private static long settingsLoadedAtMs;

        private static bool errorLogged;
        private static long lastErrorLogMs;

        public static void OnIterationEnd(bool worldUpdated)
        {
            try
            {
                var profile = WorldTickProfile.Accumulator;

                var nowMs = Environment.TickCount64;

                if (!settingsLoaded || nowMs - settingsLoadedAtMs >= SettingsRefreshMs)
                {
                    // stamped BEFORE the read, so a read that throws is retried every SettingsRefreshMs rather
                    // than on every iteration; until one succeeds the settings stay default (disabled)
                    settingsLoaded = true;
                    settingsLoadedAtMs = nowMs;
                    settings = ReadSettings();
                }

                // Gated on the same switch as the log line itself, with no tunable of its own. Written every
                // iteration (one static bool store) rather than only on a settings refresh, so the capture can
                // never be left on by a refresh that threw. It takes effect from the NEXT iteration, because
                // this runs after that iteration's inbound-message phase.
                InboundOpcodeProfile.Enabled = settings.Enabled;

                var result = Process(detector, profile, worldUpdated, nowMs, in settings);

                if (result.Verdict == SlowTickVerdict.Emit)
                    Emit(profile, worldUpdated, in result);
            }
            catch (Exception ex)
            {
                var nowMs = Environment.TickCount64;

                if (!errorLogged || nowMs - lastErrorLogMs >= ErrorLogIntervalMs)
                {
                    errorLogged = true;
                    lastErrorLogMs = nowMs;
                    log.Error("SlowTickReporter.OnIterationEnd threw; slow-tick diagnostics skipped this iteration (logged at most once a minute)", ex);
                }
            }
        }

        /// <summary>
        /// The every-iteration path, minus the settings refresh and the emit: records the phase metrics, then,
        /// only while the slow-tick log is enabled, judges the iteration and feeds the median ring. Internal and
        /// parameterised (rather than reading the statics) so a test can drive the exact production code path
        /// with its own detector and settings; no PropertyManager read happens in here.
        ///
        /// Kill switch: with settings.Enabled false the detector is not touched at all - no evaluate, no
        /// median, no sample - so the only slow-log cost left is the cached bool check. The phase metrics are
        /// NOT behind that switch; they have no switch of their own.
        /// </summary>
        internal static SlowTickResult Process(SlowTickDetector detector, TickPhaseAccumulator profile, bool worldUpdated, long nowMs, in SlowTickSettings settings)
        {
            ServerMetrics.RecordTickPhases(profile.PhaseMs, worldUpdated);

            if (!settings.Enabled)
                return default;

            var totalMs = profile.TotalMs;

            // Evaluate BEFORE the sample is added, so a spike is judged against a median it has not moved.
            // Every iteration is judged (a slow action queue on an iteration the rate limiter skipped is still
            // worth explaining), but only updated iterations feed the median, which is therefore the baseline
            // of real world ticks rather than of the idle spin between them.
            var result = detector.Evaluate(totalMs, nowMs, in settings);

            if (worldUpdated)
                detector.AddSample(totalMs);

            return result;
        }

        private static SlowTickSettings ReadSettings()
        {
            var enabled = PropertyManager.GetBool("world_tick_slow_log_enabled").Item;
            var thresholdMs = PropertyManager.GetLong("world_tick_slow_log_threshold_ms").Item;
            var multiplier = PropertyManager.GetDouble("world_tick_slow_log_median_multiplier").Item;
            var intervalSeconds = PropertyManager.GetLong("world_tick_slow_log_min_interval_seconds").Item;

            // clamp before multiplying so an absurd value cannot overflow into a negative interval
            var intervalMs = intervalSeconds <= 0 ? 0 : Math.Min(intervalSeconds, long.MaxValue / 1000) * 1000;

            return new SlowTickSettings(enabled, thresholdMs, multiplier, intervalMs);
        }

        private static void Emit(TickPhaseAccumulator profile, bool worldUpdated, in SlowTickResult result)
        {
            var s = snapshot;

            s.Iteration = WorldTickProfile.Iteration;
            s.TotalMs = profile.TotalMs;
            s.WorldMs = profile.WorldMs;
            s.MedianMs = result.MedianMs;
            s.Suppressed = result.SuppressedCount;
            s.SuppressedMaxMs = result.SuppressedMaxMs;

            Array.Copy(profile.PhaseMs, s.PhaseMs, WorldTickPhases.Count);

            // GC activity since BeginIteration. Read now rather than at EndIteration: the gap is this method's
            // own few microseconds, and it keeps the every-iteration path to the Begin-side reads only.
            s.Gc0 = GC.CollectionCount(0) - WorldTickProfile.Gc0AtBegin;
            s.Gc1 = GC.CollectionCount(1) - WorldTickProfile.Gc1AtBegin;
            s.Gc2 = GC.CollectionCount(2) - WorldTickProfile.Gc2AtBegin;
            s.GcPauseMs = (GC.GetTotalPauseDuration() - WorldTickProfile.GcPauseAtBegin).TotalMilliseconds;

            // Same read-now rationale as the GC deltas; process-wide, see SlowTickSnapshot.WeenieMiss.
            s.WeenieMiss = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissTotal - WorldTickProfile.WeenieMissAtBegin;

            s.Players = PlayerManager.GetOnlineCount();
            s.ShardQueue = ACE.Database.DatabaseManager.Shard?.QueueCount ?? 0;

            s.WorldUpdated = worldUpdated;
            s.ResetLandblocks();

            if (worldUpdated)
                GatherLandblocks(s);

            GatherOpcodes(s);
            GatherSell(s);
            GatherVaultDeposit(s);

            var sb = lineBuilder;
            sb.Clear();

            SlowTickLine.Append(sb, s);

            log.Warn(sb.ToString());
        }

        /// <summary>
        /// Copies the inbound-message per-opcode aggregate of this iteration into the snapshot. Runs on every
        /// emit, updated iteration or not, because the inbound-message phase runs every iteration. With the
        /// capture off (or before the first iteration that saw it on) the aggregate is empty and the line
        /// reports op_calls=0 - which is honest, not stale: InboundOpcodeProfile.BeginPhase clears it
        /// unconditionally.
        /// </summary>
        private static void GatherOpcodes(SlowTickSnapshot s)
        {
            s.ResetOpcodes();

            for (var i = 0; i < InboundOpcodeProfile.FilledCount; i++)
            {
                s.OfferOpcode(InboundOpcodeProfile.KeyAt(i), InboundOpcodeProfile.CallsAt(i),
                    InboundOpcodeProfile.TotalMsAt(i), InboundOpcodeProfile.MaxMsAt(i));
            }

            s.AddOpcodeOverflow(InboundOpcodeProfile.OverflowCalls, InboundOpcodeProfile.OverflowMs);
        }

        /// <summary>
        /// Copies the sell-handler per-phase aggregate of this iteration into the snapshot. Same contract as
        /// <see cref="GatherOpcodes"/>: SellPhaseProfile.BeginPhase clears it unconditionally, so an iteration
        /// with no sale in it reports HandlerCalls 0 and SlowTickLine omits the whole block rather than
        /// printing the previous sale's numbers. The remainder is read from the instrument rather than
        /// recomputed here, so there is exactly one definition of it.
        /// </summary>
        private static void GatherSell(SlowTickSnapshot s)
        {
            s.ResetSell();

            s.SellCalls = SellPhaseProfile.HandlerCalls;

            if (s.SellCalls == 0)
                return;

            s.SellMs = SellPhaseProfile.HandlerMs;
            s.SellRestMs = SellPhaseProfile.RemainderMs;
            s.SellDepositUnmeasured = SellPhaseProfile.DepositUnmeasured;

            for (var i = 0; i < SellPhases.Count; i++)
            {
                s.SellPhaseCalls[i] = SellPhaseProfile.CallsAt((SellPhase)i);
                s.SellPhaseMs[i] = SellPhaseProfile.TotalMsAt((SellPhase)i);
            }
        }

        /// <summary>
        /// Copies the vault-deposit per-phase aggregate of this iteration into the snapshot. Same contract as
        /// <see cref="GatherSell"/> in every respect: VaultDepositPhaseProfile.BeginPhase clears it
        /// unconditionally on the same line of the world loop, so an iteration with no deposit in it reports
        /// DepositCalls 0 and SlowTickLine omits the whole block rather than printing the previous tick's
        /// numbers, and the remainder is read from the instrument rather than recomputed here so that there is
        /// exactly one definition of it.
        /// </summary>
        private static void GatherVaultDeposit(SlowTickSnapshot s)
        {
            s.ResetVaultDeposit();

            s.VaultDepositCalls = VaultDepositPhaseProfile.DepositCalls;

            if (s.VaultDepositCalls == 0)
                return;

            s.VaultDepositMs = VaultDepositPhaseProfile.DepositMs;
            s.VaultDepositRestMs = VaultDepositPhaseProfile.RemainderMs;

            for (var i = 0; i < VaultDepositPhases.Count; i++)
            {
                s.VaultDepositPhaseCalls[i] = VaultDepositPhaseProfile.CallsAt((VaultDepositPhase)i);
                s.VaultDepositPhaseMs[i] = VaultDepositPhaseProfile.TotalMsAt((VaultDepositPhase)i);
            }
        }

        private static void GatherLandblocks(SlowTickSnapshot s)
        {
            var iteration = WorldTickProfile.Iteration;

            foreach (var landblock in LandblockManager.GetLoadedLandblocks())
            {
                if (landblock.LastTickIteration != iteration)
                    continue;

                s.OfferLandblock(landblock.Id.Raw, landblock.Instance, landblock.LastTickMs,
                    landblock.PlayerCountForDiagnostics, landblock.WorldObjectCountForDiagnostics,
                    landblock.LastTickSectionMs);
            }

            var groups = LandblockManager.GetLoadedLandblockGroups();

            s.Groups = groups.Count;

            var threading = ConfigManager.Config.Server.Threading;

            // A group's tracker is written only by the multi-threaded branch of its tick method
            // (LandblockManager.TickPhysics / TickMultiThreadedWork), so with that option off the value is
            // stale or zero and the field is omitted rather than reported as a misleading 0.
            if (threading.MultiThreadedLandblockGroupPhysicsTicking)
            {
                var max = 0.0;

                foreach (var group in groups)
                {
                    if (group.TickPhysicsTracker.LastAmount > max)
                        max = group.TickPhysicsTracker.LastAmount;
                }

                s.GroupPhysicsMaxMs = max * 1000;
            }

            if (threading.MultiThreadedLandblockGroupTicking)
            {
                var max = 0.0;

                foreach (var group in groups)
                {
                    if (group.TickMultiThreadedWorkTracker.LastAmount > max)
                        max = group.TickMultiThreadedWorkTracker.LastAmount;
                }

                s.GroupMultiMaxMs = max * 1000;
            }
        }
    }
}
