using System;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Common.Extensions;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The class ability web panel (Docs/Market/market-api-v1.yaml, /v1/accounts/me/characters/{guid}/class-abilities/panel
    /// and .../panel/changes): the page /weblogin opens in the in-game browser. The GET is readable from any session on
    /// the account; the POST only from a session POST /v1/session/launch created for that same character.
    ///
    /// Both routes run under the panel's OWN in-flight gate and its own per-account buckets (reads and writes separate), never
    /// the market's, so a stalled world thread cannot take the market's slots. Both answer panel_disabled when
    /// class_abilities_enabled or class_ability_web_panel_enabled is off.
    ///
    /// Handlers are thin: every rule runs on the world thread inside <see cref="IClassAbilityPanelService"/>, through
    /// the Player's own validators; the pure shapes are <see cref="ClassAbilityPanelModel"/>.
    /// </summary>
    public static partial class MarketApiHost
    {
        /// <summary>The change POST's body cap. Three short fields; 4 KiB is generous and still bounded.</summary>
        internal const int MaxClassAbilityPanelBodyBytes = 4096;

        private sealed class ClassAbilityPanelChangeBody
        {
            public string Ability { get; set; }
            public int? FromRank { get; set; }
            public int? ToRank { get; set; }
        }

        private sealed class ClassAbilityPanelErrorBody
        {
            public string Error { get; set; }
            public string Message { get; set; }
            public ClassAbilityPanelSnapshot Panel { get; set; }
        }

        private sealed class ClassAbilityPanelAppliedBody
        {
            public bool Applied { get; set; } = true;
            public ClassAbilityPanelSnapshot Panel { get; set; }
        }

        private static void MapClassAbilityPanelRoutes(WebApplication a)
        {
            a.MapGet("/v1/accounts/me/characters/{guid:long}/class-abilities/panel", (HttpContext ctx, long guid) =>
                PanelGuard(ctx, isWrite: false, handler: (accountId, boundCharacterGuid) =>
                {
                    if (options.ClassAbilityPanelEnabled?.Invoke() != true)
                        return PanelError(MarketError.PanelDisabled);

                    if (guid <= 0 || guid > uint.MaxValue || !TryResolveCharacter(accountId, (uint)guid, out _))
                        return PanelError(MarketError.NotOwner);

                    var service = options.ClassAbilityPanel;
                    if (service == null)
                        return PanelError(MarketError.ServerError);

                    var characterGuid = (uint)guid;

                    // The fast path: answered from the counter alone, with no world-thread visit.
                    var since = ctx.Request.Query["since"].ToString();
                    if (!string.IsNullOrEmpty(since) && since.Length <= 64 && string.Equals(service.PeekRevision(characterGuid), since, StringComparison.Ordinal))
                        return Results.Json(new ClassAbilityPanelUnchanged { Revision = since }, Json);

                    var launchBound = boundCharacterGuid != 0 && boundCharacterGuid == characterGuid;

                    return PanelWithTimeout(ClassAbilityPanelModel.ReadTimeoutMessage, () =>
                    {
                        var result = service.Read(accountId, characterGuid, launchBound);

                        if (result.Error == MarketError.Timeout)
                            return PanelError(MarketError.Timeout, ClassAbilityPanelModel.ReadTimeoutMessage);

                        if (result.Error != MarketError.None || result.Panel == null)
                            return PanelError(result.Error == MarketError.None ? MarketError.ServerError : result.Error);

                        return Results.Json(result.Panel, Json);
                    });
                }));

            a.MapPost("/v1/accounts/me/characters/{guid:long}/class-abilities/panel/changes", async (HttpContext ctx, long guid) =>
            {
                // Key first: an unauthenticated caller must not make the process read a body.
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadClassAbilityPanelBody(ctx);

                return PanelGuard(ctx, isWrite: true, handler: (accountId, boundCharacterGuid) =>
                {
                    if (options.ClassAbilityPanelEnabled?.Invoke() != true)
                        return PanelError(MarketError.PanelDisabled);

                    // 1. a launch session, launched by THIS character
                    if (boundCharacterGuid == 0 || guid <= 0 || guid > uint.MaxValue || boundCharacterGuid != (uint)guid)
                        return PanelError(MarketError.NotLaunchSession);

                    // 2. on the session account's roster (fails closed)
                    if (!TryResolveCharacter(accountId, (uint)guid, out _))
                        return PanelError(MarketError.NotOwner);

                    // the request itself: a panel ability and a to_rank the contract allows
                    if (body == null || body.FromRank == null || body.ToRank == null
                        || !ClassAbilityPanelModel.TryGetAbility(body.Ability, out var ability))
                        return PanelError(MarketError.InvalidChange);

                    var kind = ClassAbilityPanelModel.Classify(body.FromRank.Value, body.ToRank.Value);
                    if (kind == ClassAbilityPanelChangeKind.Invalid)
                        return PanelError(MarketError.InvalidChange);

                    var service = options.ClassAbilityPanel;
                    if (service == null)
                        return PanelError(MarketError.ServerError);

                    var change = new ClassAbilityPanelChange(ability, body.FromRank.Value, kind);

                    // 3 and 4 (online, location), compare-and-set and the rules all run on the world thread.
                    return PanelWithTimeout(ClassAbilityPanelModel.ChangeTimeoutMessage, () =>
                    {
                        var result = service.Apply(accountId, (uint)guid, change);

                        if (result.Error == MarketError.None)
                        {
                            if (result.Panel == null)
                                return PanelError(MarketError.ServerError);

                            return Results.Json(new ClassAbilityPanelAppliedBody { Panel = result.Panel }, Json);
                        }

                        // the panel's own sentences where the stock envelope text is about something else
                        var message = result.Message;
                        if (string.IsNullOrEmpty(message) && result.Error == MarketError.CharacterOffline)
                            message = ClassAbilityPanelModel.OfflineRefusal;
                        if (string.IsNullOrEmpty(message) && result.Error == MarketError.Timeout)
                            message = ClassAbilityPanelModel.ChangeTimeoutMessage;

                        return PanelError(result.Error, message, result.Panel);
                    });
                });
            });
        }

        /// <summary>
        /// The panel's guard: key, session (resolving its launch binding too), the panel's own in-flight gate, then the
        /// panel's own write bucket for a write or its own read bucket for a read. Shaped exactly like <see cref="Guard"/>, gate before bucket.
        /// </summary>
        private static IResult PanelGuard(HttpContext ctx, bool isWrite, Func<uint, uint, IResult> handler)
        {
            if (!KeyOk(ctx))
                return NoBody(ctx);

            if (!sessions.TryResolve(Bearer(ctx), out var accountId, out var boundCharacterGuid))
                return Error(MarketError.BadCredentials);

            if (!panelLimiter.TryEnterInFlight())
                return Error(MarketError.RateLimited);

            try
            {
                // a write takes a token from the panel write bucket, a read from the separate panel read bucket
                if (isWrite ? !panelLimiter.TryWrite(accountId) : !panelReadLimiter.TryWrite(accountId))
                    return Error(MarketError.RateLimited);

                return handler(accountId, boundCharacterGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] unhandled error serving {ctx.Request.Method} {ctx.Request.Path}: {ex.GetFullMessage()}");
                return Error(MarketError.ServerError);
            }
            finally
            {
                panelLimiter.ExitInFlight();
            }
        }

        /// <summary>
        /// <see cref="WithTimeout"/> with the panel's own timeout sentence: the generic wrapper answers the market's
        /// "the market is busy" text, which reads as retryable, and a panel change that timed out must be reloaded.
        /// </summary>
        private static IResult PanelWithTimeout(string timeoutMessage, Func<IResult> work)
        {
            var task = Task.Run(work);

            if (task.Wait(options.RequestTimeoutMs))
                return task.Result;

            log.Warn($"[MARKET] a class ability panel request exceeded {options.RequestTimeoutMs} ms and was answered 503. The enqueued work is still running and will complete or roll back on its own.");

            return PanelError(MarketError.Timeout, timeoutMessage);
        }

        /// <summary>
        /// The panel's error envelope: the published code, the server's own sentence when it has one (the location
        /// rule and rule refusals), else the stock message, and a fresh panel for stale and rule_refused.
        /// </summary>
        private static IResult PanelError(MarketError error, string message = null, ClassAbilityPanelSnapshot panel = null)
        {
            return Results.Json(new ClassAbilityPanelErrorBody
            {
                Error = MarketErrorCodes.ToCode(error),
                Message = string.IsNullOrEmpty(message) ? MarketErrorCodes.ToMessage(error) : message,
                Panel = panel,
            }, Json, statusCode: StatusFor(error));
        }

        private static async Task<ClassAbilityPanelChangeBody> ReadClassAbilityPanelBody(HttpContext ctx)
        {
            var text = await ReadBodyText(ctx, MaxClassAbilityPanelBodyBytes);

            if (text == null)
                return null;

            // ReadBodyText decodes raw bytes, so a UTF-8 byte order mark survives as U+FEFF (the suit transfer reader's fix).
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);

            try
            {
                return JsonSerializer.Deserialize<ClassAbilityPanelChangeBody>(text, Json);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
