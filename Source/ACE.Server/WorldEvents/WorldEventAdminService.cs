using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using ACE.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// What one /v1/admin/world-events call answered: either a body to serialize (Error None) or a
    /// refusal with its optional extras. The route renders it and holds no rule of its own.
    /// </summary>
    public sealed class WorldEventAdminOutcome
    {
        public MarketError Error { get; init; }

        /// <summary>The 200 body, serialized with MarketApiHost.Json. Null on a refusal.</summary>
        public object Body { get; init; }

        /// <summary>world_event_running / world_event_run_changed: the live run's id.</summary>
        public uint? RunId { get; init; }

        /// <summary>invalid_location / world_event_refused: one human sentence saying why.</summary>
        public string Reason { get; init; }

        public static WorldEventAdminOutcome Ok(object body) => new WorldEventAdminOutcome { Body = body };

        public static WorldEventAdminOutcome Fail(MarketError error, uint? runId = null, string reason = null) =>
            new WorldEventAdminOutcome { Error = error, RunId = runId, Reason = reason };
    }

    /// <summary>The seam between the /v1/admin/world-events routes and the game, so the route tests can use a fake.</summary>
    public interface IWorldEventAdminService
    {
        /// <summary>GET catalog. Runs on the request thread: the store and catalog are immutable swapped references.</summary>
        WorldEventAdminOutcome Catalog();

        /// <summary>GET status. Runs on the request thread, reading only what StatusText already reads off-thread.</summary>
        WorldEventAdminOutcome Status();

        /// <summary>POST preview. World thread, bounded. Changes nothing and audits nothing.</summary>
        WorldEventAdminOutcome Preview(WorldEventStartBody body);

        /// <summary>POST start. World thread, bounded. One audit line on success, written inside the same world action.</summary>
        WorldEventAdminOutcome Start(WorldEventStartBody body, string accountName);

        /// <summary>POST stop. World thread, bounded. One audit line on success, written inside the same world action.</summary>
        WorldEventAdminOutcome Stop(WorldEventStopBody body, string accountName);
    }

    /// <summary>
    /// The start/preview body (Docs/AdminPanel/WORLD-EVENTS-START.md, "API"). Every member is nullable so an
    /// ABSENT field is told apart from a zero: absent teaser_seconds means "the live tunable", never 0.
    /// </summary>
    public sealed class WorldEventStartBody
    {
        public bool? Random { get; set; }
        public string SourceId { get; set; }
        public List<string> FamilyIds { get; set; }
        public string GoalId { get; set; }
        public string BossId { get; set; }
        public string RewardId { get; set; }
        public int? TeaserSeconds { get; set; }
        public int? MinDurationSeconds { get; set; }
        public WorldEventLocationBody Location { get; set; }
    }

    /// <summary>
    /// An explicit event centre. The numbers are doubles (and cell a long) on purpose: an out-of-range value
    /// then reaches validation and is refused invalid_location with a reason, instead of failing the JSON
    /// parse and arriving as an unreadable body.
    /// </summary>
    public sealed class WorldEventLocationBody
    {
        public string Label { get; set; }
        public long? RealmId { get; set; }
        public long? Cell { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public double? Qw { get; set; }
        public double? Qx { get; set; }
        public double? Qy { get; set; }
        public double? Qz { get; set; }
    }

    public sealed class WorldEventStopBody
    {
        /// <summary>"abort" (the default when absent) or "fail".</summary>
        public string Mode { get; set; }

        /// <summary>Required. The run id the caller last read; a different live run is refused world_event_run_changed.</summary>
        public long? ExpectedRunId { get; set; }
    }

    /// <summary>
    /// The game side of the web Admin panel's World Event start page (Docs/AdminPanel/WORLD-EVENTS-START.md):
    /// start, preview, watch and stop a run with NO in-game session. Nothing here reads a Session or a Player;
    /// the only live-world reads (players near a point, players online, whether a realm exists) arrive as
    /// injected delegates, so the whole class runs under the unit-test harness.
    ///
    /// Owns the server-side location validation (refuse, never clamp), webStartable enforcement, the
    /// request DTO to WorldEventRequest mapping, and the audit line. WorldEventManager stays the single
    /// authority on whether a run may start: the disabled/running checks made here first exist only to
    /// answer them without parsing a body, and TryStart repeats them under its own lock.
    /// </summary>
    public sealed class WorldEventAdminService : IWorldEventAdminService
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Metres a source's geometry radius must clear every landblock edge by (WORLD-EVENTS-START.md).</summary>
        public const float EdgeMarginMeters = 2f;

        /// <summary>The longest location label accepted. It is spliced into player-facing announcements.</summary>
        public const int LabelMaxLength = 64;

        public const string StopModeAbort = "abort";
        public const string StopModeFail = "fail";

        private readonly Action<IAction> enqueue;
        private readonly Action<string, string> writeAudit;
        private readonly TimeSpan timeout;
        private readonly Func<ushort, bool> isRealmKnown;
        private readonly Func<Position, float, int?> countPlayersNear;
        private readonly Func<int> countPlayersOnline;
        private readonly Func<int> teaserDefault;

        public WorldEventAdminService(Action<IAction> enqueue, Action<string, string> writeAudit, TimeSpan timeout,
            Func<ushort, bool> isRealmKnown, Func<Position, float, int?> countPlayersNear, Func<int> countPlayersOnline,
            Func<int> teaserDefault = null)
        {
            this.enqueue = enqueue;
            this.writeAudit = writeAudit;
            this.timeout = timeout;
            this.isRealmKnown = isRealmKnown ?? (_ => false);
            this.countPlayersNear = countPlayersNear ?? ((_, _) => null);
            this.countPlayersOnline = countPlayersOnline ?? (() => 0);
            this.teaserDefault = teaserDefault ?? WorldEvent.TunableTeaserLeadSeconds;
        }

        /// <summary>The production wiring (Program.cs): world thread, Audit channel, live realm and player reads.</summary>
        public static WorldEventAdminService CreateLive(Action<IAction> enqueue, Action<string, string> writeAudit, int timeoutMs)
        {
            return new WorldEventAdminService(enqueue, writeAudit, TimeSpan.FromMilliseconds(timeoutMs),
                realmId => realmId == 0 || RealmManager.GetRealm(realmId) != null,
                (position, radius) =>
                {
                    // CountAliveNear answers int.MaxValue when its scan throws (it must hold a run open);
                    // here that is "unknown", not "everyone".
                    var count = WorldEventAudienceSampler.CountAliveNear(position, radius);
                    return count == int.MaxValue ? null : count;
                },
                PlayerManager.GetOnlineCount);
        }

        // ---- pure validation (D6) ------------------------------------------------------------------

        /// <summary>
        /// How far a source's centre must stay from every landblock edge. A spawn outside the held landblock
        /// is dropped by WorldEventSpawner, so a geometry that crosses an edge silently loses spawns.
        ///
        /// disc and single: ClearanceRadius (geometryRadius unless the source sets clearanceRadius) +
        /// <see cref="EdgeMarginMeters"/> (disc samples INSIDE its radius and resamples rather than
        /// jittering; single is the centre itself). ring and edges: additionally
        /// <see cref="WorldEventGeometry.DefaultJitterMetres"/>, because their fixed points sit ON the
        /// radius and WorldEvent jitters each by up to that much (WorldEventGeometry.ApplyJitter).
        /// </summary>
        public static float EdgeMarginFor(SourceThemeDef source)
        {
            if (source == null)
                return EdgeMarginMeters;

            var jitter = source.GeometryKind == SourceGeometry.Ring || source.GeometryKind == SourceGeometry.Edges
                ? WorldEventGeometry.DefaultJitterMetres
                : 0f;

            return source.ClearanceRadius + EdgeMarginMeters + jitter;
        }

        /// <summary>True when the landblock-local point (x, y) is at least <paramref name="edgeMargin"/> metres from every edge.</summary>
        public static bool FitsLandblock(double x, double y, float edgeMargin)
        {
            return x >= edgeMargin && x <= Position.BlockLength - edgeMargin
                && y >= edgeMargin && y <= Position.BlockLength - edgeMargin;
        }

        /// <summary><see cref="FitsLandblock"/> against <paramref name="source"/>'s own <see cref="EdgeMarginFor"/>.</summary>
        public static bool SourceFits(double x, double y, SourceThemeDef source) => FitsLandblock(x, y, EdgeMarginFor(source));

        /// <summary>
        /// The location rules that do not depend on the chosen source: every number present and finite (also
        /// after narrowing to float), the cell an outdoor land cell (low word below 0x0100), x and y in
        /// [0, 192), the realm id one this server knows, the rotation not all zero, and the label short and
        /// free of control and invisible formatting characters. Refuses with a reason, never clamps. On
        /// success <paramref name="position"/> is a new Position whose instance is the realm's default
        /// instance.
        /// </summary>
        public static bool TryValidateLocation(WorldEventLocationBody location, Func<ushort, bool> isRealmKnown,
            out Position position, out string label, out string reason)
        {
            position = null;
            label = null;
            reason = null;

            if (location == null)
            {
                reason = "location is required";
                return false;
            }

            var numbers = new (string name, double? value)[]
            {
                ("x", location.X), ("y", location.Y), ("z", location.Z),
                ("qw", location.Qw), ("qx", location.Qx), ("qy", location.Qy), ("qz", location.Qz)
            };

            foreach (var (name, value) in numbers)
            {
                if (value == null)
                {
                    reason = $"location.{name} is required";
                    return false;
                }

                if (!double.IsFinite(value.Value) || !float.IsFinite((float)value.Value))
                {
                    reason = $"location.{name} must be a finite number";
                    return false;
                }
            }

            if (location.Cell == null || location.Cell.Value < 0 || location.Cell.Value > uint.MaxValue)
            {
                reason = "location.cell must be a 32-bit landblock cell id";
                return false;
            }

            var cell = (uint)location.Cell.Value;

            if ((cell & 0xFFFF) >= 0x0100)
            {
                reason = $"location.cell 0x{cell:X8} is not an outdoor land cell (its low word must be below 0x0100)";
                return false;
            }

            var x = location.X.Value;
            var y = location.Y.Value;

            if (x < 0 || x >= Position.BlockLength || y < 0 || y >= Position.BlockLength)
            {
                reason = $"location.x and location.y must be within [0, {Position.BlockLength}) of the landblock";
                return false;
            }

            if (location.Qw.Value == 0 && location.Qx.Value == 0 && location.Qy.Value == 0 && location.Qz.Value == 0)
            {
                reason = "the location rotation (qw, qx, qy, qz) cannot be all zero";
                return false;
            }

            if (location.RealmId == null || location.RealmId.Value < 0 || location.RealmId.Value > 0x7FFF
                || !isRealmKnown((ushort)location.RealmId.Value))
            {
                reason = $"location.realm_id {(location.RealmId?.ToString(CultureInfo.InvariantCulture) ?? "(absent)")} is not a realm this server knows";
                return false;
            }

            var rawLabel = location.Label?.Trim() ?? string.Empty;

            if (rawLabel.Length > LabelMaxLength)
            {
                reason = $"location.label is longer than {LabelMaxLength} characters";
                return false;
            }

            if (rawLabel.Any(IsForbiddenLabelChar))
            {
                reason = "location.label contains a control or invisible formatting character";
                return false;
            }

            var instance = Position.InstanceIDFromVars((ushort)location.RealmId.Value, 0, false);

            position = new Position(cell, (float)x, (float)y, (float)location.Z.Value,
                (float)location.Qx.Value, (float)location.Qy.Value, (float)location.Qz.Value, (float)location.Qw.Value, instance);

            label = rawLabel;
            return true;
        }

        private static bool IsForbiddenLabelChar(char c)
        {
            if (char.IsControl(c) || char.IsSurrogate(c))
                return true;

            var category = CharUnicodeInfo.GetUnicodeCategory(c);

            return category == UnicodeCategory.Format
                || category == UnicodeCategory.LineSeparator
                || category == UnicodeCategory.ParagraphSeparator;
        }

        /// <summary>The ids a web request may compose from at (x, y): webStartable AND its geometry fits the landblock there.</summary>
        public static List<string> FittingWebSources(WorldEventAxisStore store, double x, double y)
        {
            return (store?.Sources.Values ?? Enumerable.Empty<SourceThemeDef>())
                .Where(s => s.WebStartable && SourceFits(x, y, s))
                .Select(s => s.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Maps a start/preview body onto a <see cref="WorldEventRequest"/> against <paramref name="store"/>,
        /// or refuses. Pure apart from the injected realm probe. Rules, in order: a readable body; teaser in
        /// [0, MaxTeaserLeadSeconds]; the duration pair WorldEvent.ValidateDurations accepts; the location
        /// rules; then the source - a pinned one must be webStartable and fit, a random roll is restricted
        /// (AllowedSourceIds) to the sources that are both, so it can only ever pick a fitting source.
        /// </summary>
        public static WorldEventAdminOutcome TryBuildRequest(WorldEventStartBody body, WorldEventAxisStore store,
            Func<ushort, bool> isRealmKnown, int teaserDefaultSeconds, string invoker,
            out WorldEventRequest request, out string locationLabel)
        {
            request = null;
            locationLabel = null;

            if (body == null)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: "the request body is missing or unreadable");

            var teaser = body.TeaserSeconds ?? teaserDefaultSeconds;

            if (teaser < 0 || teaser > WorldEvent.MaxTeaserLeadSeconds)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused,
                    reason: $"teaser_seconds must be between 0 and {WorldEvent.MaxTeaserLeadSeconds}");

            var minDuration = body.MinDurationSeconds ?? WorldEvent.DefaultMinDurationSeconds;

            // The same rule WorldEvent.ValidateDurations applies inside TryStart, phrased for the web body.
            if (minDuration < 0 || minDuration >= WorldEvent.DefaultMaxDurationSeconds)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused,
                    reason: $"min_duration_seconds must be between 0 and {WorldEvent.DefaultMaxDurationSeconds - 1}");

            var random = body.Random == true;

            // The composer's own refusals for these name in-game flags ("missing --goal"); the web body is
            // checked first so its reasons name the body's fields.
            var axisRefusal = CheckAxes(body, store, random);

            if (axisRefusal != null)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: axisRefusal);

            if (!TryValidateLocation(body.Location, isRealmKnown, out var position, out var label, out var locationReason))
                return WorldEventAdminOutcome.Fail(MarketError.InvalidLocation, reason: locationReason);

            var x = body.Location.X.Value;
            var y = body.Location.Y.Value;

            var sourceId = string.IsNullOrWhiteSpace(body.SourceId) ? null : body.SourceId.Trim().ToLowerInvariant();

            if (sourceId != null && store != null && store.Sources.TryGetValue(sourceId, out var pinned))
            {
                if (!pinned.WebStartable)
                    return WorldEventAdminOutcome.Fail(MarketError.SourceNotWebStartable);

                if (!SourceFits(x, y, pinned))
                    return WorldEventAdminOutcome.Fail(MarketError.InvalidLocation,
                        reason: $"source '{sourceId}' ({Snake(pinned.GeometryKind.ToString())}, clearance_radius {pinned.ClearanceRadius:0.##}) " +
                                $"needs its centre at least {EdgeMarginFor(pinned):0.##} m from every landblock edge; ({x:0.##}, {y:0.##}) is not");
            }

            var allowed = FittingWebSources(store, x, y);

            if (sourceId == null && allowed.Count == 0)
                return WorldEventAdminOutcome.Fail(MarketError.InvalidLocation,
                    reason: $"no web-startable source fits at ({x:0.##}, {y:0.##}) with its landblock edge margin");

            locationLabel = WorldEventTownIndex.DescribeLabeled(label, position);

            request = new WorldEventRequest
            {
                Random = random,
                SourceId = sourceId,
                FamilyIds = (body.FamilyIds ?? new List<string>()).ToList(),
                GoalId = body.GoalId,
                BossId = body.BossId,
                RewardId = body.RewardId,
                AnchorPosition = position,
                AnchorLabel = locationLabel,
                PositionAnchorId = WorldEventComposer.WebAnchorId,
                AllowedSourceIds = allowed,
                Invoker = invoker,
                TeaserLeadSeconds = teaser,
                MinDurationSeconds = minDuration
            };

            return null;
        }

        /// <summary>
        /// The axis rules whose composer refusal would otherwise name an in-game flag, phrased for the web
        /// body's fields. Null when the axes may be handed to the composer. Unknown ids are left to the
        /// composer, whose "unknown X 'id' (known: ...)" text names no flag.
        /// </summary>
        private static string CheckAxes(WorldEventStartBody body, WorldEventAxisStore store, bool random)
        {
            var families = body.FamilyIds ?? new List<string>();

            if (!random && string.IsNullOrWhiteSpace(body.SourceId))
                return "source_id is required unless random is true";

            if (!random && string.IsNullOrWhiteSpace(body.GoalId))
                return "goal_id is required unless random is true";

            if (!random && families.Count == 0)
                return "family_ids needs at least one family unless random is true";

            if (families.Any(string.IsNullOrWhiteSpace))
                return "family_ids cannot contain a blank id";

            if (families.Count > WorldEventComposer.MaxComposedFamilies)
                return $"family_ids names {families.Count} families; at most {WorldEventComposer.MaxComposedFamilies} are allowed";

            if (string.IsNullOrWhiteSpace(body.RewardId) && store != null && !store.Rewards.ContainsKey(WorldEventComposer.DefaultRewardId))
                return $"reward_id is required: this server has no default '{WorldEventComposer.DefaultRewardId}' reward";

            // kill_boss with no boss: on a random request a blank boss is rolled (auto for kill_boss), so
            // only an explicit none-kind boss is refused there.
            var goalId = body.GoalId?.Trim().ToLowerInvariant();

            if (goalId != null && store != null && store.Goals.TryGetValue(goalId, out var goal) && goal.TypeKind == GoalType.KillBoss)
            {
                var bossId = string.IsNullOrWhiteSpace(body.BossId) ? null : body.BossId.Trim().ToLowerInvariant();

                var bossIsNone = bossId == null
                    ? !random
                    : store.Bosses.TryGetValue(bossId, out var boss) && boss.Kind == BossKind.None;

                if (bossIsNone)
                    return $"goal '{goalId}' needs boss_id {BossDef.FamilyChampion.Id}, {WorldEventComposer.AutoBossId} or a named boss id";
            }

            return null;
        }

        // ---- request-thread reads --------------------------------------------------------------------

        public WorldEventAdminOutcome Catalog()
        {
            var store = WorldEventManager.Store ?? WorldEventAxisStore.Empty;
            var catalog = WorldEventManager.Catalog ?? WorldEventCatalog.Empty;
            var running = RunningEvent();

            var sources = store.Sources.Values
                .Where(s => s.WebStartable)
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new
                {
                    id = s.Id,
                    display_name = s.DisplayName ?? s.Id,
                    geometry = Snake(s.GeometryKind.ToString()),
                    geometry_radius = s.GeometryRadius,
                    clearance_radius = s.ClearanceRadius,
                    edge_margin_meters = EdgeMarginFor(s),
                    reward_radius = s.RewardRadius,
                    compatible_goals = (s.CompatibleGoals ?? new List<string>()).Where(store.Goals.ContainsKey).ToList(),
                })
                .ToList();

            var goals = store.Goals.Values
                .OrderBy(g => g.Id, StringComparer.Ordinal)
                .Select(g => new
                {
                    id = g.Id,
                    display_name = g.DisplayName ?? g.Id,
                    requires_boss = g.TypeKind == GoalType.KillBoss,
                })
                .ToList();

            var families = catalog.Profiles.Values
                .Where(p => p.MemberCount > 0)
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new
                {
                    id = p.Id,
                    display_name = store.Families.TryGetValue(p.Id, out var def) && !string.IsNullOrEmpty(def.DisplayName) ? def.DisplayName : p.Id,
                    member_count = p.MemberCount,
                    min_level = p.MinLevel,
                    max_level = p.MaxLevel,
                })
                .ToList();

            var bosses = store.Bosses.Values
                .OrderBy(b => b.Kind)
                .ThenBy(b => b.Id, StringComparer.Ordinal)
                .Select(b => new
                {
                    id = b.Id,
                    display_name = b.DisplayName ?? b.Id,
                    kind = Snake(b.Kind.ToString()),
                    family_id = string.IsNullOrEmpty(b.FamilyId) ? null : b.FamilyId,
                })
                .ToList();

            bosses.Add(new
            {
                id = WorldEventComposer.AutoBossId,
                display_name = "Auto (a named boss for the families)",
                kind = WorldEventComposer.AutoBossId,
                family_id = (string)null,
            });

            var rewards = store.Rewards.Values
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new { id = r.Id, display_name = r.DisplayName ?? r.Id })
                .ToList();

            return WorldEventAdminOutcome.Ok(new
            {
                enabled = SafeEnabled(),
                running = running != null,
                run_id = running?.RunId,
                sources,
                goals,
                families,
                bosses,
                rewards,
                limits = new
                {
                    max_families = WorldEventComposer.MaxComposedFamilies,
                    teaser_min_seconds = 0,
                    teaser_max_seconds = WorldEvent.MaxTeaserLeadSeconds,
                    teaser_default_seconds = teaserDefault(),
                    min_duration_default_seconds = WorldEvent.DefaultMinDurationSeconds,
                    max_duration_seconds = WorldEvent.DefaultMaxDurationSeconds,
                    base_edge_margin_meters = EdgeMarginMeters,
                    label_max_length = LabelMaxLength,
                },
            });
        }

        public WorldEventAdminOutcome Status()
        {
            var evt = RunningEvent();
            var now = ACE.Common.Time.GetUnixTime();

            string statusText;

            try
            {
                statusText = WorldEventManager.StatusText();
            }
            catch (Exception ex)
            {
                log.Warn("[WORLDEVENT] web status could not build the status line", ex);
                statusText = null;
            }

            if (evt == null)
                return WorldEventAdminOutcome.Ok(new
                {
                    enabled = SafeEnabled(),
                    running = false,
                    state = "none",
                    status_text = statusText,
                });

            var teaserRemaining = evt.State == WorldEventState.Idle
                ? Math.Max(0, evt.TeasedAt + evt.TeaserLeadSeconds - now)
                : 0;

            var elapsed = evt.ActiveAt > 0 ? Math.Max(0, now - evt.ActiveAt) : 0;

            return WorldEventAdminOutcome.Ok(new
            {
                enabled = SafeEnabled(),
                running = true,
                state = Snake(evt.State.ToString()),
                run_id = evt.RunId,
                axis_summary = evt.Composition?.ToAxisSummary(),
                source_id = evt.Composition?.Source?.Id,
                family_ids = evt.Composition?.Families?.Select(f => f?.Id).ToList(),
                goal_id = evt.Composition?.Goal?.Id,
                boss_id = evt.Composition?.Boss?.Id,
                reward_id = evt.Composition?.Reward?.Id,
                location_label = evt.AnchorName,
                teaser_remaining_seconds = (int)Math.Ceiling(teaserRemaining),
                elapsed_seconds = (int)Math.Floor(elapsed),
                progress = string.IsNullOrEmpty(evt.ProgressText) ? null : evt.ProgressText,
                participants = evt.Participation?.Count ?? 0,
                status_text = statusText,
            });
        }

        // ---- world-thread writes ---------------------------------------------------------------------

        public WorldEventAdminOutcome Preview(WorldEventStartBody body) => OnWorld(() => PreviewCore(body));

        public WorldEventAdminOutcome Start(WorldEventStartBody body, string accountName) => OnWorld(() => StartCore(body, accountName));

        public WorldEventAdminOutcome Stop(WorldEventStopBody body, string accountName) => OnWorld(() => StopCore(body, accountName));

        private WorldEventAdminOutcome OnWorld(Func<WorldEventAdminOutcome> work)
        {
            if (enqueue == null)
                return WorldEventAdminOutcome.Fail(MarketError.ServerError);

            switch (AdminWorldCall.Run(enqueue, work, timeout, out var outcome))
            {
                case AdminWorldCall.Status.Completed:
                    return outcome ?? WorldEventAdminOutcome.Fail(MarketError.ServerError);

                case AdminWorldCall.Status.TimedOut:
                    // The work may have started: the page re-reads status, it never retries.
                    return WorldEventAdminOutcome.Fail(MarketError.Timeout);

                default:
                    return WorldEventAdminOutcome.Fail(MarketError.ServerError);
            }
        }

        internal WorldEventAdminOutcome PreviewCore(WorldEventStartBody body)
        {
            if (!WorldEventManager.Enabled)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventsDisabled);

            var refused = TryBuildRequest(body, WorldEventManager.Store, isRealmKnown, teaserDefault(), null,
                out var request, out var locationLabel);

            if (refused != null)
                return refused;

            request.DryRun = true;

            if (!WorldEventManager.TryCompose(request, out var composition, out var composeError))
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: composeError);

            var radius = composition.Source?.RewardRadius ?? 0f;
            var near = countPlayersNear(request.AnchorPosition, radius);
            var online = countPlayersOnline();
            var running = RunningEvent();

            var warnings = new List<string>();

            if (running != null)
                warnings.Add($"run {running.RunId} is in progress ({Snake(running.State.ToString())}); a start is refused until it ends");

            if (request.TeaserLeadSeconds == 0 && near == 0)
                warnings.Add($"teaser is 0 and nobody is within {radius:0} m: the run stages at once and fails with no participants after about {WorldEvent.DefaultAbandonAfterSeconds} s");

            if (online == 0)
                warnings.Add("no players are online");

            if (near == null)
                warnings.Add("the count of players near the location could not be read");

            if (request.Random)
                warnings.Add("random picks are rolled again on start; send these ids with random false to start exactly this composition");

            return WorldEventAdminOutcome.Ok(new
            {
                source_id = composition.Source?.Id,
                family_ids = composition.Families.Select(f => f?.Id).ToList(),
                goal_id = composition.Goal?.Id,
                boss_id = composition.Boss?.Id,
                reward_id = composition.Reward?.Id,
                axis_summary = composition.ToAxisSummary(),
                location_label = locationLabel,
                teaser_seconds = request.TeaserLeadSeconds ?? 0,
                min_duration_seconds = request.MinDurationSeconds,
                reward_radius = radius,
                players_near = near,
                players_online = online,
                warnings,
            });
        }

        internal WorldEventAdminOutcome StartCore(WorldEventStartBody body, string accountName)
        {
            if (!WorldEventManager.Enabled)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventsDisabled);

            var running = RunningEvent();

            if (running != null)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRunning, runId: running.RunId);

            var actor = ACE.Server.Command.Web.WebCommandDispatcher.WebActorLabel(accountName);

            var refused = TryBuildRequest(body, WorldEventManager.Store, isRealmKnown, teaserDefault(), actor,
                out var request, out var locationLabel);

            if (refused != null)
                return refused;

            if (!WorldEventManager.TryStart(request, out var evt, out var error, out var refusal, out var runningRunId))
            {
                switch (refusal)
                {
                    case WorldEventStartRefusal.Disabled:
                        return WorldEventAdminOutcome.Fail(MarketError.WorldEventsDisabled);

                    case WorldEventStartRefusal.AlreadyRunning:
                        return WorldEventAdminOutcome.Fail(MarketError.WorldEventRunning, runId: runningRunId);

                    default:
                        return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: error);
                }
            }

            if (evt == null)
                return WorldEventAdminOutcome.Fail(MarketError.ServerError);

            var summary = evt.Composition?.ToAxisSummary();

            // After the start, because the line names the run id. A failed audit write must not report a
            // started run as a failure, so it is logged rather than turned into an error.
            try
            {
                writeAudit?.Invoke(actor,
                    $"[WORLDEVENT] started world event run {evt.RunId} at {evt.AnchorName} ({summary}), " +
                    $"teaser {evt.TeaserLeadSeconds}s, min duration {evt.MinDurationSeconds}s, from the web admin panel");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} web start audit line failed", ex);
            }

            return WorldEventAdminOutcome.Ok(new
            {
                run_id = evt.RunId,
                state = Snake(evt.State.ToString()),
                axis_summary = summary,
                source_id = evt.Composition?.Source?.Id,
                family_ids = evt.Composition?.Families?.Select(f => f?.Id).ToList(),
                goal_id = evt.Composition?.Goal?.Id,
                boss_id = evt.Composition?.Boss?.Id,
                reward_id = evt.Composition?.Reward?.Id,
                location_label = evt.AnchorName,
                teaser_seconds = evt.TeaserLeadSeconds,
                min_duration_seconds = evt.MinDurationSeconds,
            });
        }

        internal WorldEventAdminOutcome StopCore(WorldEventStopBody body, string accountName)
        {
            if (body == null)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: "the request body is missing or unreadable");

            var mode = string.IsNullOrWhiteSpace(body.Mode) ? StopModeAbort : body.Mode.Trim().ToLowerInvariant();

            if (mode != StopModeAbort && mode != StopModeFail)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: $"mode must be '{StopModeAbort}' or '{StopModeFail}'");

            if (body.ExpectedRunId == null || body.ExpectedRunId.Value < 1 || body.ExpectedRunId.Value > uint.MaxValue)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventRefused, reason: "expected_run_id is required");

            if (!WorldEventManager.Enabled)
                return WorldEventAdminOutcome.Fail(MarketError.WorldEventsDisabled);

            // "fail" is the in-game stop's FailedTimeout (WorldEventCommands.HandleStop): it pays the
            // consolation reward, where "abort" ends the run with nothing paid.
            var outcome = mode == StopModeFail ? WorldEventOutcome.FailedTimeout : WorldEventOutcome.AbortedAdmin;
            var actor = ACE.Server.Command.Web.WebCommandDispatcher.WebActorLabel(accountName);

            if (!WorldEventManager.TryStopCurrent((uint)body.ExpectedRunId.Value, outcome, actor,
                    out var refusal, out var runId, out var state, out var finalOutcome))
            {
                switch (refusal)
                {
                    case WorldEventStopRefusal.RunChanged:
                        return WorldEventAdminOutcome.Fail(MarketError.WorldEventRunChanged, runId: runId);

                    case WorldEventStopRefusal.Finished:
                        // Resolved/Rewarding/Cleanup: the run already has its outcome. A "fail" here would be a
                        // silent no-op and an "abort" would destroy caches players have not claimed yet.
                        return WorldEventAdminOutcome.Fail(MarketError.WorldEventNotRunning,
                            reason: $"run {runId} has already finished ({Snake(finalOutcome.ToString())}); its claim window may still be open, so it is not stopped");

                    default:
                        return WorldEventAdminOutcome.Fail(MarketError.WorldEventNotRunning);
                }
            }

            // What the run actually ended as, read back after the stop - never the requested mode.
            try
            {
                writeAudit?.Invoke(actor,
                    $"[WORLDEVENT] stopped world event run {runId} (requested {mode}, outcome={finalOutcome}, state={state}) from the web admin panel");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={runId} web stop audit line failed", ex);
            }

            return WorldEventAdminOutcome.Ok(new
            {
                run_id = runId,
                mode,
                outcome = Snake(finalOutcome.ToString()),
                state = Snake(state.ToString()),
            });
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private static WorldEvent RunningEvent()
        {
            var evt = WorldEventManager.Current;
            return evt != null && evt.State != WorldEventState.Done ? evt : null;
        }

        private static bool SafeEnabled()
        {
            try
            {
                return WorldEventManager.Enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
    }
}
