using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Pinnacles;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>
    /// Server-side Knot telemetry: one `knot_event` row (ace_analytics.sql) per Knot-economy event. Every public
    /// Record* method is called AFTER the effect it describes, returns at once while analytics is disabled, and wraps
    /// its whole body in a try/catch that warns once per method: telemetry must NEVER break a drop, a weave or a
    /// fight. The row builders are pure, clip every string to its column width and take plain values, so they are
    /// tested without a Player.
    ///
    /// THE kind/result STRINGS BELOW ARE THE ONLY SPELLING. They are all lowercase, at most 16 characters (the column
    /// width) and are read by dashboard SQL, so renaming one silently splits a series. Add a value here AND to the
    /// documented matrix in ace_analytics.sql.
    /// </summary>
    public static class KnotTelemetry
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The `kind` column values.</summary>
        public static class Kind
        {
            public const string FiberRoll = "fiber_roll";
            public const string PatternRoll = "pattern_roll";
            public const string Weave = "weave";
            public const string Bind = "bind";
            public const string CopyOpen = "copy_open";
            public const string Entry = "entry";
            public const string AttemptEnd = "attempt_end";
            public const string KnotSpent = "knot_spent";
        }

        /// <summary>The `result` column values, grouped by the kind they belong to.</summary>
        public static class Result
        {
            // fiber_roll
            public const string Dropped = "dropped";
            public const string Dry = "dry";
            public const string Lost = "lost";

            // pattern_roll: Dropped, Lost, and
            public const string None = "none";

            // weave
            public const string Ok = "ok";
            public const string Partial = "partial";

            // bind
            public const string Pattern = "pattern";
            public const string Random = "random";

            // entry
            public const string Charge = "charge";
            public const string PaidReturn = "paid_return";
            public const string Seam = "seam";

            // attempt_end (Lost is shared with the rolls)
            public const string Won = "won";
            public const string Unpaid = "unpaid";
            public const string Expired = "expired";
            public const string Forfeit = "forfeit";
            public const string Reset = "reset";
            public const string Unloaded = "unloaded";
            public const string Fault = "fault";

            // knot_spent (Won and Forfeit are shared with attempt_end)
            public const string LostAtZero = "lost_at_zero";
            public const string NoEntries = "no_entries";
            public const string LastEntry = "last_entry";
        }

        /// <summary>
        /// The weave `reason` value: a full Pattern turned into a Knot. Rows written before the Pattern became the only way
        /// to make a Knot (owner ruling 2026-10-29) carry 'maelis' or 'pouch'.
        /// </summary>
        public const string WeaveReasonPattern = "pattern";

        /// <summary>
        /// The whole kind/result matrix, spelled out. A test pins this against the design's matrix and against the
        /// documented one in ace_analytics.sql, so a value added to <see cref="Result"/> but not here (or the reverse)
        /// fails.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string[]> Matrix = new Dictionary<string, string[]>
        {
            [Kind.FiberRoll] = new[] { Result.Dropped, Result.Dry, Result.Lost },
            [Kind.PatternRoll] = new[] { Result.Dropped, Result.None, Result.Lost },
            [Kind.Weave] = new[] { Result.Ok, Result.Partial },
            [Kind.Bind] = new[] { Result.Pattern, Result.Random },
            [Kind.CopyOpen] = new[] { Result.Ok },
            [Kind.Entry] = new[] { Result.Charge, Result.PaidReturn, Result.Seam },
            [Kind.AttemptEnd] = new[] { Result.Won, Result.Unpaid, Result.Expired, Result.Forfeit, Result.Reset, Result.Unloaded, Result.Fault, Result.Lost },
            [Kind.KnotSpent] = new[] { Result.Won, Result.LostAtZero, Result.Forfeit, Result.NoEntries, Result.LastEntry },
        };

        // ---- pure: derivations ----

        /// <summary>
        /// The `attempt_end` result for one PinnacleFight.End (pure). Checked in this order, first match wins: the cache
        /// was delivered (won, whatever ended it afterwards); the entry was refunded (unpaid); then the End reason's
        /// prefix: "fault in " (fault), "forfeit: " (forfeit), "reset: " (reset), "copy unloaded" (unloaded),
        /// "hold expired" (expired); otherwise a Won outcome is won and anything else is lost. Every reason literal
        /// PinnacleFight can pass to End has a row in KnotTelemetryTests, read from the source, so a new reason fails there.
        /// </summary>
        public static string AttemptResultFor(PinnacleFightState outcome, string reason, bool refund, bool cacheDelivered)
        {
            if (cacheDelivered)
                return Result.Won;

            if (refund)
                return Result.Unpaid;

            if (reason != null)
            {
                if (reason.StartsWith("fault in ", StringComparison.Ordinal))
                    return Result.Fault;

                if (reason.StartsWith("forfeit: ", StringComparison.Ordinal))
                    return Result.Forfeit;

                if (reason.StartsWith("reset: ", StringComparison.Ordinal))
                    return Result.Reset;

                if (reason.StartsWith("copy unloaded", StringComparison.Ordinal))
                    return Result.Unloaded;

                if (reason.StartsWith("hold expired", StringComparison.Ordinal))
                    return Result.Expired;
            }

            return outcome == PinnacleFightState.Won ? Result.Won : Result.Lost;
        }

        /// <summary>The `entry` result for a spend (pure): a return into a held fight on this Knot is paid_return, anything else a charge.</summary>
        internal static string EntryResultFor(bool fightOnThisKnotIsHeld) => fightOnThisKnotIsHeld ? Result.PaidReturn : Result.Charge;

        /// <summary>The Fiber roll result (pure): a drop that never reached the pile is lost.</summary>
        internal static string FiberResultFor(bool dropped, bool piled) => dropped ? (piled ? Result.Dropped : Result.Lost) : Result.Dry;

        /// <summary>The Pattern roll result (pure): none when nothing dropped, lost when it dropped and never reached the pile.</summary>
        internal static string PatternResultFor(bool dropped, string patternKey) => dropped ? (patternKey != null ? Result.Dropped : Result.Lost) : Result.None;

        /// <summary>A comma list of Fiber levels (pure); an unreadable level is written as 0.</summary>
        internal static string FiberLevelList(IEnumerable<int?> levels)
            => levels == null ? null : string.Join(",", levels.Select(l => (l ?? 0).ToString()));

        /// <summary>The Fiber levels of <paramref name="fibers"/>, read from the items themselves. Call BEFORE they are consumed.</summary>
        /// <summary>
        /// Called inline between the Knot being placed and the Fibers being consumed, so it must never throw: a throw
        /// there would leave the player with the Knot AND every Fiber. Null while analytics is disabled or on any failure.
        /// </summary>
        internal static string FiberLevelsOf(IEnumerable<WorldObject> fibers)
        {
            if (!AnalyticsManager.IsEnabled)
                return null;

            try
            {
                return FiberLevelList(fibers?.Select(f => f?.GetProperty(PropertyInt.KnotLevel)).ToList());
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(FiberLevelsOf), ex);
                return null;
            }
        }

        // ---- pure: row builders ----

        /// <summary>Who an event belongs to: the character (0 only on an unresolved log row), the name AT EVENT TIME and the account.</summary>
        internal readonly struct Who
        {
            public Who(uint characterId, string name, uint accountId, int? level)
            {
                CharacterId = characterId;
                Name = name;
                AccountId = accountId;
                Level = level;
            }

            public uint CharacterId { get; }
            public string Name { get; }
            public uint AccountId { get; }
            public int? Level { get; }
        }

        private static AnalyticsDatabase.KnotEventRow NewRow(DateTime tsUtc, string kind, string result, Who who)
            => new AnalyticsDatabase.KnotEventRow
            {
                TsUtc = tsUtc,
                Kind = kind,
                Result = result,
                Source = "live",
                CharacterId = who.CharacterId,
                Name = Clip(who.Name, AnalyticsDatabase.KnotNameMaxLength) ?? string.Empty,
                AccountId = who.AccountId,
                CharLevel = who.Level,
                Qty = 1,
            };

        /// <summary>Clips to <paramref name="max"/> characters; null stays null.</summary>
        internal static string Clip(string value, int max) => value == null || value.Length <= max ? value : value.Substring(0, max);

        /// <summary>
        /// The rows one cleared run's roll produces (pure): always a fiber_roll, plus a pattern_roll only when at least
        /// one pinnacle was available (<paramref name="enabledPinnacles"/> greater than 0), because with none the Pattern
        /// roll never ran and a row would invent a roll that did not happen.
        /// </summary>
        internal static List<AnalyticsDatabase.KnotEventRow> BuildRollRows(DateTime tsUtc, Who who, int fiberLevel, int missesBefore, int missesAfter,
            bool fiberDropped, bool fiberPiled, uint? fiberGuid, int enabledPinnacles, bool patternDropped, string patternKey, uint? patternGuid,
            string dungeonId, uint instanceId, string startGroup)
        {
            var rows = new List<AnalyticsDatabase.KnotEventRow>(2);

            var fiber = NewRow(tsUtc, Kind.FiberRoll, FiberResultFor(fiberDropped, fiberPiled), who);
            fiber.KnotLevel = fiberLevel;
            fiber.ItemGuid = fiberGuid;
            fiber.MissesBefore = missesBefore;
            fiber.MissesAfter = missesAfter;
            fiber.DungeonId = Clip(dungeonId, AnalyticsDatabase.KnotDungeonIdMaxLength);
            fiber.InstanceId = instanceId;
            fiber.StartGroup = Clip(startGroup, AnalyticsDatabase.KnotGroupIdMaxLength);
            rows.Add(fiber);

            if (enabledPinnacles > 0)
            {
                var pattern = NewRow(tsUtc, Kind.PatternRoll, PatternResultFor(patternDropped, patternKey), who);
                pattern.KnotLevel = fiberLevel;
                pattern.ItemGuid = patternGuid;
                pattern.PinnacleKey = Clip(patternKey, AnalyticsDatabase.KnotPinnacleKeyMaxLength);
                pattern.EnabledPinnacles = enabledPinnacles;
                pattern.DungeonId = fiber.DungeonId;
                pattern.InstanceId = instanceId;
                pattern.StartGroup = fiber.StartGroup;
                rows.Add(pattern);
            }

            return rows;
        }

        /// <summary>The weave row (pure). qty is the number of Fibers woven; <paramref name="fiberLevels"/> the comma list of their levels.</summary>
        internal static AnalyticsDatabase.KnotEventRow BuildWeaveRow(DateTime tsUtc, Who who, string result, string reason, int knotLevel, uint knotGuid, int qty, string fiberLevels)
        {
            var row = NewRow(tsUtc, Kind.Weave, result, who);
            row.KnotLevel = knotLevel;
            row.KnotGuid = knotGuid;
            row.Qty = qty;
            row.FiberLevels = Clip(fiberLevels, AnalyticsDatabase.KnotFiberLevelsMaxLength);
            row.Reason = Clip(reason, AnalyticsDatabase.KnotReasonMaxLength);
            return row;
        }

        /// <summary>The bind row (pure). <paramref name="patternGuid"/> is set on a Pattern bind and null on a random one.</summary>
        internal static AnalyticsDatabase.KnotEventRow BuildBindRow(DateTime tsUtc, Who who, bool viaPattern, int knotLevel, uint knotGuid, string pinnacleKey, int entriesMax, uint? patternGuid)
        {
            var row = NewRow(tsUtc, Kind.Bind, viaPattern ? Result.Pattern : Result.Random, who);
            row.KnotLevel = knotLevel;
            row.KnotGuid = knotGuid;
            row.PinnacleKey = Clip(pinnacleKey, AnalyticsDatabase.KnotPinnacleKeyMaxLength);
            row.EntriesMax = entriesMax;
            row.EntriesLeft = entriesMax;   // a bind sets Structure to the full count
            row.ItemGuid = viaPattern ? patternGuid : null;
            return row;
        }

        /// <summary>The copy_open row (pure).</summary>
        internal static AnalyticsDatabase.KnotEventRow BuildCopyOpenRow(DateTime tsUtc, Who who, string attemptId, uint knotGuid, int knotLevel, string pinnacleKey, uint instanceId)
        {
            var row = NewRow(tsUtc, Kind.CopyOpen, Result.Ok, who);
            row.AttemptId = Clip(attemptId, AnalyticsDatabase.KnotGroupIdMaxLength);
            row.KnotGuid = knotGuid;
            row.KnotLevel = knotLevel;
            row.PinnacleKey = Clip(pinnacleKey, AnalyticsDatabase.KnotPinnacleKeyMaxLength);
            row.InstanceId = instanceId;
            return row;
        }

        /// <summary>The entry row (pure): one entry spent, <paramref name="entriesLeft"/> remaining.</summary>
        internal static AnalyticsDatabase.KnotEventRow BuildEntryRow(DateTime tsUtc, Who who, string result, uint knotGuid, int? knotLevel, string pinnacleKey, int entriesLeft, string attemptId)
        {
            var row = NewRow(tsUtc, Kind.Entry, result, who);
            row.KnotGuid = knotGuid;
            row.KnotLevel = knotLevel;
            row.PinnacleKey = Clip(pinnacleKey, AnalyticsDatabase.KnotPinnacleKeyMaxLength);
            row.EntriesLeft = entriesLeft;
            row.AttemptId = Clip(attemptId, AnalyticsDatabase.KnotGroupIdMaxLength);
            return row;
        }

        /// <summary>The attempt_end row (pure). <paramref name="reason"/> is the raw End reason, kept beside the derived result.</summary>
        internal static AnalyticsDatabase.KnotEventRow BuildAttemptEndRow(DateTime tsUtc, Who who, PinnacleAttemptEnd end, string pinnacleKey, string attemptId, double? durationSecs, uint? instanceId)
        {
            var row = NewRow(tsUtc, Kind.AttemptEnd, AttemptResultFor(end.Outcome, end.Reason, end.Refund, end.CacheDelivered), who);
            row.KnotGuid = end.KnotGuid;
            row.KnotLevel = end.KnotLevel;
            row.PinnacleKey = Clip(pinnacleKey, AnalyticsDatabase.KnotPinnacleKeyMaxLength);
            row.PhaseReached = end.Phase;
            row.PhaseCount = end.PhaseCount;
            row.Charged = end.Charged;
            row.DurationSecs = durationSecs;
            row.Reason = Clip(end.Reason, AnalyticsDatabase.KnotReasonMaxLength);
            row.AttemptId = Clip(attemptId, AnalyticsDatabase.KnotGroupIdMaxLength);
            row.InstanceId = instanceId;
            return row;
        }

        /// <summary>The knot_spent row (pure): a Knot item was consumed, for <paramref name="result"/>.</summary>
        internal static AnalyticsDatabase.KnotEventRow BuildKnotSpentRow(DateTime tsUtc, Who who, string result, uint knotGuid, int? knotLevel, string pinnacleKey, int? entriesLeft)
        {
            var row = NewRow(tsUtc, Kind.KnotSpent, result, who);
            row.KnotGuid = knotGuid;
            row.KnotLevel = knotLevel;
            row.PinnacleKey = Clip(pinnacleKey, AnalyticsDatabase.KnotPinnacleKeyMaxLength);
            row.EntriesLeft = entriesLeft;
            return row;
        }

        // ---- engine-facing: the Record* hooks ----

        private static readonly ConcurrentDictionary<string, byte> warned = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>Warns once per method name for the process lifetime: a hot failing hook must not flood the log.</summary>
        private static void WarnOnce(string method, Exception ex)
        {
            if (warned.TryAdd(method, 0))
                log.Warn($"[KNOTS] telemetry {method} failed and was skipped (further failures of this hook are not logged): {ex.Message}");
        }

        /// <summary>Test seam: forgets which hooks have warned.</summary>
        internal static void ResetWarningsForTests() => warned.Clear();

        private static Who WhoIs(Player p) => new Who(p.Guid.Full, p.Name, p.Account?.AccountId ?? 0, p.Level);

        private static void Enqueue(AnalyticsDatabase.KnotEventRow row) => AnalyticsManager.RecordKnotEvent(row);

        /// <summary>
        /// Who one seat is (pure over its inputs). The name is the roster member's, falling back to the run's owner name
        /// for the owner seat; the account comes from the member (0 when unknown). The level is the online player's when
        /// that seat is online, else the run's start level for the OWNER seat only, else NULL.
        /// </summary>
        internal static Who SeatWho(uint seat, uint ownerGuid, string ownerName, int ownerLevelAtStart, string memberName, uint? memberAccountId, string onlineName, uint? onlineAccountId, int? onlineLevel)
            => new Who(seat,
                memberName ?? (seat == ownerGuid ? ownerName : onlineName) ?? string.Empty,
                memberAccountId ?? onlineAccountId ?? 0,
                onlineLevel ?? (seat == ownerGuid ? ownerLevelAtStart : (int?)null));

        /// <summary>
        /// One SEAT's roll, called from KnotDrops.RollSeat right after its summary log line: a fiber_roll and, when a
        /// pinnacle was available, a pattern_roll, both owned by the seat (a group clear writes one pair per rolling
        /// seat). <paramref name="missesNow"/> is the value the log prints as missesNow.
        /// </summary>
        public static void RecordRoll(ThreadDungeonRun run, uint seat, int fiberLevel, int missesBefore, int missesNow,
            bool fiberDropped, bool fiberPiled, uint? fiberGuid, int enabledPinnacles, bool patternDropped, string patternKey, uint? patternGuid)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                var member = run.GetMember(seat);
                var online = PlayerManager.GetOnlinePlayer(seat);
                var who = SeatWho(seat, run.OwnerGuid, run.OwnerName, run.OwnerLevelAtStart, member?.Name, member?.AccountId,
                    online?.Name, online?.Account?.AccountId, online?.Level);

                foreach (var row in BuildRollRows(DateTime.UtcNow, who, fiberLevel, missesBefore, missesNow, fiberDropped, fiberPiled, fiberGuid,
                             enabledPinnacles, patternDropped, patternKey, patternGuid, run.Dungeon?.Id, run.RunId, run.StartGroup))
                    Enqueue(row);
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordRoll), ex);
            }
        }

        /// <summary>A weave: the Knot is in the pack. <paramref name="fiberLevels"/> must be read BEFORE the Fibers are consumed.</summary>
        public static void RecordWeave(Player player, WorldObject knot, int knotLevel, string result, string reason, string fiberLevels)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                Enqueue(BuildWeaveRow(DateTime.UtcNow, WhoIs(player), result, reason, knotLevel, knot.Guid.Full, 10, fiberLevels));
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordWeave), ex);
            }
        }

        /// <summary>A Knot took its pinnacle. <paramref name="pattern"/> is the consumed Pattern on a Pattern bind, null on a random one.</summary>
        public static void RecordBind(Player player, WorldObject knot, PinnacleDef def, WorldObject pattern)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                Enqueue(BuildBindRow(DateTime.UtcNow, WhoIs(player), pattern != null, knot.GetProperty(PropertyInt.KnotLevel) ?? 0, knot.Guid.Full,
                    def?.Key, def?.Entries ?? 0, pattern?.Guid.Full));
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordBind), ex);
            }
        }

        /// <summary>A Pinnacle copy was created and registered, just before the owner is teleported in.</summary>
        public static void RecordCopyOpen(Player player, WorldObject knot, int knotLevel, PinnacleDef def, string attemptId, uint instanceId)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                Enqueue(BuildCopyOpenRow(DateTime.UtcNow, WhoIs(player), attemptId, knot.Guid.Full, knotLevel, def?.Key, instanceId));
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordCopyOpen), ex);
            }
        }

        /// <summary>
        /// An entry was spent on <paramref name="knot"/>, <paramref name="entriesLeft"/> now remaining. The result is
        /// paid_return when the owner's registered fight on this Knot is Held (a return into a held attempt), else charge;
        /// <paramref name="forcedResult"/> overrides that for the seam-charged path. A rare mislabel is possible (a return
        /// while the fight still reads Running), accepted in the design.
        /// </summary>
        public static void RecordEntry(Player owner, WorldObject knot, int entriesLeft, string forcedResult = null)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                string attemptId = null;
                var held = false;

                if (PinnacleFightManager.TryGetFight(owner.Guid.Full, out var lf) && lf != null && lf.Fight.KnotGuid == knot.Guid.Full)
                {
                    held = lf.Fight.State == PinnacleFightState.Held;
                    attemptId = lf.World?.AttemptId;
                }

                Enqueue(BuildEntryRow(DateTime.UtcNow, WhoIs(owner), forcedResult ?? EntryResultFor(held), knot.Guid.Full,
                    knot.GetProperty(PropertyInt.KnotLevel), knot.GetProperty(PropertyString.PinnacleKey), entriesLeft, attemptId));
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordEntry), ex);
            }
        }

        /// <summary>
        /// A Knot item was consumed, for <paramref name="result"/>. Reads the Knot's own properties, which survive the
        /// consume; <paramref name="entriesLeft"/> overrides the Structure read when the caller knows the count (the seam
        /// consumes before it writes the spent entry back).
        /// </summary>
        public static void RecordKnotSpent(Player owner, WorldObject knot, string result, int? entriesLeft = null)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                Enqueue(BuildKnotSpentRow(DateTime.UtcNow, WhoIs(owner), result, knot.Guid.Full, knot.GetProperty(PropertyInt.KnotLevel),
                    knot.GetProperty(PropertyString.PinnacleKey), entriesLeft ?? knot.GetProperty(PropertyInt.Structure)));
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordKnotSpent), ex);
            }
        }

        /// <summary>
        /// A Pinnacle attempt ended (PinnacleFight.End, via IPinnacleWorld.ReportEnded). Touches nothing on the
        /// landblock; the owner is described from the context captured when the copy opened, so an offline owner is
        /// fine. A null context (no copy context was supplied) writes the row without an attempt id.
        /// </summary>
        public static void RecordAttemptEnd(PinnacleAttemptEnd end, PinnacleAttemptContext context)
        {
            if (!AnalyticsManager.IsEnabled)
                return;

            try
            {
                var name = context?.OwnerName ?? PlayerManager.FindByGuid(end.OwnerGuid)?.Name;
                var who = new Who(end.OwnerGuid, name, context?.AccountId ?? 0, context?.OwnerLevel);
                var now = DateTime.UtcNow;
                double? duration = context != null ? Math.Max(0.0, (now - context.OpenedUtc).TotalSeconds) : (double?)null;

                Enqueue(BuildAttemptEndRow(now, who, end, context?.PinnacleKey, context?.AttemptId, duration, context?.InstanceId));
            }
            catch (Exception ex)
            {
                WarnOnce(nameof(RecordAttemptEnd), ex);
            }
        }
    }
}
