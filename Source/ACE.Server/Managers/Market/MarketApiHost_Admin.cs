using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Server.Entity.Actions;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The /v1/admin/* route family (PLAN-P1.md sections 1-2, PLAN-P2.md sections 1-2, PLAN-P3.md
    /// sections 1-2). Naming follows MarketManager_Orders.cs - a partial-class file split by feature
    /// area, not a separate type.
    /// </summary>
    public static partial class MarketApiHost
    {
        private static readonly Regex SettingKeyPattern = new Regex("^[a-z0-9_]{1,128}$", RegexOptions.None);

        /// <summary>
        /// The authorization check shared by MapAdmin and MapAdminPost, per the published status order
        /// (PLAN-P1.md section 1, PLAN-P2.md section 1): 403 not_admin / 500 server_error (account read
        /// threw), then 503 admin_disabled checked LAST so a non-Admin caller can never learn the
        /// switch's state.
        /// </summary>
        private static IResult AdminGate(HttpContext ctx, uint accountId, Func<AdminPrincipal, IResult> handler)
        {
            var check = adminAuthorizer.Check(accountId, out var principal);

            if (check == AdminCheck.NotAdmin)
                return Error(MarketError.NotAdmin);

            if (check != AdminCheck.Admin)
                return Error(MarketError.ServerError);

            if (options.AdminWebEnabled?.Invoke() != true)
                return Error(MarketError.AdminDisabled);

            ctx.Response.Headers.CacheControl = "no-store";

            return handler(principal);
        }

        /// <summary>
        /// Wraps the existing Guard with AdminGate. Every GET /v1/admin/* route MUST be registered
        /// through this helper and never through the bare Guard - AdminRouteRegistrationTests enforces
        /// that mechanically.
        /// </summary>
        private static void MapAdmin(WebApplication a, string pattern, Func<HttpContext, AdminPrincipal, IResult> handler)
            => a.MapGet(pattern, (HttpContext ctx) => Guard(ctx, requireSession: true, isWrite: false,
                handler: accountId => AdminGate(ctx, accountId, principal => handler(ctx, principal))));

        /// <summary>
        /// The write counterpart of MapAdmin (PLAN-P2.md section 2, ruling 10). The body is read after
        /// the key check and before the bearer check, like every other body-carrying POST. isWrite is
        /// always true, so the per-account write bucket (429) runs BEFORE authorization - a non-Admin
        /// can learn it is rate limited, never whether the panel is on. Every write /v1/admin/* route
        /// MUST be registered through this helper - AdminRouteRegistrationTests' widened pattern
        /// (MapAdmin(Post)?\() enforces that mechanically, and this is deliberately non-generic so a
        /// generic MapAdminPost&lt;T&gt;( could never slip past that regex.
        /// </summary>
        private static void MapAdminPost(WebApplication a, string pattern, Func<HttpContext, AdminPrincipal, string, IResult> writeHandler)
            => a.MapPost(pattern, async (HttpContext ctx) =>
            {
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var rawBody = await ReadBodyText(ctx, MaxAdminBodyBytes);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                    AdminGate(ctx, accountId, principal => writeHandler(ctx, principal, rawBody)));
            });

        private static void MapAdminRoutes(WebApplication a)
        {
            MapAdmin(a, "/v1/admin/me", (ctx, principal) =>
                Results.Json(new
                {
                    account_id = principal.AccountId,
                    account_name = principal.AccountName,
                }, Json));

            MapAdmin(a, "/v1/admin/settings", (ctx, principal) =>
                Results.Json(BuildAdminSettingsResponse(), Json));

            MapAdminPost(a, "/v1/admin/settings/{key}", (ctx, principal, rawBody) => EditSetting(ctx, principal, rawBody));

            MapAdmin(a, "/v1/admin/chat", (ctx, principal) =>
            {
                var q = ctx.Request.Query;
                long? since = long.TryParse(q["since"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
                int? limit = int.TryParse(q["limit"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null;
                var page = (options.ChatFeed ?? AdminChatFeed.Shared).Read(since, q["generation"].ToString(), limit);

                return Results.Json(new
                {
                    generation = page.Generation,
                    head_seq = page.HeadSeq,
                    oldest_seq = page.OldestSeq,
                    gap = page.Gap,
                    has_more = page.HasMore,
                    next_since = page.NextSince,
                    lines = page.Lines.Select(x => new
                    {
                        seq = x.Seq,
                        at = x.At,
                        channel = x.Channel,
                        sender = x.Sender,
                        text = x.Text,
                        truncated = x.Truncated,
                    }).ToList(),
                }, Json);
            });

            MapAdminPost(a, "/v1/admin/announce", (ctx, principal, rawBody) =>
            {
                // rawBody is null above MaxAdminBodyBytes already (MapAdminPost's ReadBodyText), so an
                // over-size body never reaches here - a second byte-count check on it would be dead
                // code (code review, 2026-09-15). Only the null check is reachable.
                AdminAnnounceRequest body = null;
                if (rawBody != null)
                {
                    try { body = JsonSerializer.Deserialize<AdminAnnounceRequest>(rawBody, Json); }
                    catch (Exception) { }
                }

                if (!AdminAnnouncement.TryNormalize(body?.Text, out var text))
                    return Error(MarketError.InvalidAnnouncement);

                if (!announceLimiter.TryWrite(principal.AccountId))
                    return Error(MarketError.RateLimited);

                if (options.WriteAdminAudit == null || options.SendAnnouncement == null)
                    return Error(MarketError.ServerError);

                var actorLabel = principal.AccountName;

                // BEFORE the broadcast (PLAN-P3.md invariant 12): a broadcast without its audit line is
                // the worse failure. A throw here (Guard's catch) sends nothing.
                options.WriteAdminAudit(actorLabel, AdminAnnouncement.AuditMessage(actorLabel, text));

                var recipients = options.SendAnnouncement(text);

                return Results.Json(new
                {
                    text = Gamecast.Format("System", text),
                    recipients,
                    sent_at = DateTime.UtcNow,
                }, Json);
            });

            MapAdmin(a, "/v1/admin/commands", (ctx, principal) => ListCommands(ctx, principal));

            MapAdminPost(a, "/v1/admin/commands/run", (ctx, principal, rawBody) => RunCommand(ctx, principal, rawBody));

            MapWorldEventAdminRoutes(a);
        }

        /// <summary>
        /// POST /v1/admin/commands/run (PLAN-P4.md section 2, P4b). MapAdminPost has already answered rows
        /// 1-6 (key, bearer, in-flight gate, write bucket, not_admin/server_error, admin_disabled) and
        /// bounded the body read at MaxAdminBodyBytes, passing null above it. commands_disabled is checked
        /// first here, in the same order as the GET route. Everything from the text rules on is the
        /// dispatcher's.
        /// </summary>
        private static IResult RunCommand(HttpContext ctx, AdminPrincipal principal, string rawBody)
        {
            if (options.AdminWebCommandsEnabled?.Invoke() != true)
                return Error(MarketError.CommandsDisabled);

            AdminCommandRunRequest body = null;
            if (rawBody != null)
            {
                try { body = JsonSerializer.Deserialize<AdminCommandRunRequest>(rawBody, Json); }
                catch (Exception) { }
            }

            if (body?.Text == null)
                return Error(MarketError.InvalidCommandText);

            if (options.CommandDispatcher == null)
                return Error(MarketError.ServerError);

            var outcome = options.CommandDispatcher.Run(principal, body.Text);

            if (outcome.Error != MarketError.None)
                return Error(outcome.Error, busyCommand: outcome.BusyCommand);

            return Results.Json(new AdminCommandRunResponse
            {
                Command = outcome.Command,
                Bucket = outcome.Bucket,
                Result = outcome.Result,
                Ran = outcome.Ran,
                CharacterName = outcome.CharacterName,
                ElapsedMs = outcome.ElapsedMs,
                Truncated = outcome.Truncated,
                Output = outcome.Output,
            }, Json);
        }

        private sealed class AdminCommandRunRequest
        {
            public string Text { get; set; }
        }

        private sealed class AdminCommandRunResponse
        {
            public string Command { get; init; }
            public string Bucket { get; init; }
            public string Result { get; init; }
            public bool Ran { get; init; }

            /// <summary>Written as an explicit null for the web bucket (section 2's example), overriding the host-wide WhenWritingNull.</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
            public string CharacterName { get; init; }

            public long ElapsedMs { get; init; }
            public bool Truncated { get; init; }
            public IReadOnlyList<ACE.Server.Command.Web.WebCommandOutputLine> Output { get; init; }
        }

        /// <summary>
        /// GET /v1/admin/commands (PLAN-P4.md section 2, P4a). MapAdmin has already answered the key,
        /// bearer, in-flight gate, not_admin/server_error and admin_disabled rows; commands_disabled is
        /// checked first here, so a non-Admin can never learn the console switch's state either. A throw
        /// (the character lookup, or the registry changing under enumeration) is Guard's server_error.
        /// </summary>
        private static IResult ListCommands(HttpContext ctx, AdminPrincipal principal)
        {
            if (options.AdminWebCommandsEnabled?.Invoke() != true)
                return Error(MarketError.CommandsDisabled);

            if (options.Commands == null)
                return Error(MarketError.ServerError);

            return Results.Json(options.Commands.List(principal), Json);
        }

        private sealed class AdminSettingEditRequest
        {
            public string Value { get; set; }
            public string ExpectedCurrent { get; set; }
        }

        private sealed class AdminAnnounceRequest
        {
            public string Text { get; set; }
        }

        /// <summary>Fails closed: no metadata loaded, or a key with no row at all, is not editable (PLAN-P2.md ruling 21). Only the write path fails closed - BuildAdminSettingsResponse keeps its P1 read behaviour for a missing row.</summary>
        private static bool IsEditable(string key)
            => settingsMetadata != null && settingsMetadata.TryGet(key, out var row) && !row.Sensitive;

        private static IResult EditSetting(HttpContext ctx, AdminPrincipal principal, string rawBody)
        {
            var key = ctx.Request.RouteValues["key"] as string;

            if (string.IsNullOrEmpty(key) || !SettingKeyPattern.IsMatch(key) || !PropertyAdminService.TryResolveType(key, out var type))
                return Error(MarketError.SettingNotFound);

            if (!IsEditable(key))
                return Error(MarketError.SettingSensitive);

            AdminSettingEditRequest body;
            try
            {
                body = rawBody == null ? null : JsonSerializer.Deserialize<AdminSettingEditRequest>(rawBody, Json);
            }
            catch (Exception)
            {
                body = null;
            }

            if (body == null || body.Value == null || body.ExpectedCurrent == null)
                return Error(MarketError.InvalidSettingValue);

            if (!PropertyAdminService.TryParseWeb(type, body.Value, out _))
                return Error(MarketError.InvalidSettingValue);

            if (options.EnqueueWorldAction == null)
                return Error(MarketError.ServerError);

            var result = PropertyAdminService.RunOnWorld(
                options.EnqueueWorldAction,
                () => PropertyAdminService.TryModify(type, key, body.Value, PropertyAdminActor.Web(principal.AccountName), body.ExpectedCurrent),
                TimeSpan.FromMilliseconds(options.RequestTimeoutMs));

            if (result == null)
                return Error(MarketError.Timeout);

            switch (result.Outcome)
            {
                case PropertyModifyOutcome.Faulted:
                    return Error(MarketError.ServerError);

                case PropertyModifyOutcome.Conflict:
                    return Error(MarketError.SettingChanged, currentValue: result.CurrentText);

                case PropertyModifyOutcome.InvalidValue:
                    return Error(MarketError.InvalidSettingValue);

                case PropertyModifyOutcome.UnknownKey:
                    return Error(MarketError.SettingNotFound);

                case PropertyModifyOutcome.Updated:
                case PropertyModifyOutcome.Unchanged:
                    return Results.Json(new
                    {
                        key,
                        type,
                        previous_value = result.PreviousText,
                        current_value = result.CurrentText,
                        changed = result.Outcome == PropertyModifyOutcome.Updated,
                    }, Json);

                default:
                    return Error(MarketError.ServerError);
            }
        }

        /// <summary>
        /// tabs in display order, settings ordered by tabs order then group (ordinal) then key
        /// (ordinal) - PLAN-P1.md section 1. A key EnumerateSettings yields with no config-metadata.tsv
        /// row (should never happen: ConfigMetadataCoverageTests guards this at build time) falls back
        /// to the server/General bucket rather than silently vanishing from the response.
        /// </summary>
        private static object BuildAdminSettingsResponse()
        {
            var metadata = settingsMetadata;
            var tabOrder = ConfigMetadata.Tabs.Select((t, i) => (t.Id, Index: i)).ToDictionary(t => t.Id, t => t.Index);

            var rawSettings = options.EnumerateSettings?.Invoke() ?? Enumerable.Empty<(string key, string type, string current, string @default, string description)>();

            var settings = rawSettings.Select(s =>
            {
                string tab = "server", group = "General", origin = "fork";
                var sensitive = false;

                if (metadata != null && metadata.TryGet(s.key, out var row))
                {
                    tab = row.Tab;
                    group = row.Group;
                    origin = row.Origin;
                    sensitive = row.Sensitive;
                }

                return new
                {
                    key = s.key,
                    type = s.type,
                    current = sensitive ? null : s.current,
                    @default = sensitive ? null : s.@default,
                    description = s.description,
                    tab,
                    group,
                    origin,
                    sensitive,
                    tabIndex = tabOrder.TryGetValue(tab, out var i) ? i : int.MaxValue,
                };
            })
            .OrderBy(s => s.tabIndex)
            .ThenBy(s => s.group, StringComparer.Ordinal)
            .ThenBy(s => s.key, StringComparer.Ordinal)
            .Select(s => (object)new
            {
                key = s.key,
                type = s.type,
                current_value = s.current,
                default_value = s.@default,
                description = s.description,
                tab = s.tab,
                group = s.group,
                origin = s.origin,
                sensitive = s.sensitive,
            })
            .ToList();

            var tabs = ConfigMetadata.Tabs.Select(t => (object)new { id = t.Id, label = t.Label }).ToList();

            return new { tabs, settings };
        }
    }
}
