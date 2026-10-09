using System;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Server.WorldEvents;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The /v1/admin/world-events routes (Docs/AdminPanel/WORLD-EVENTS-START.md, "API"): start, preview,
    /// watch and stop a World Event with no in-game session. Registered through MapAdmin / MapAdminPost
    /// like every other admin route, so key, bearer, in-flight gate, write bucket, not_admin (the account's
    /// access level re-read on every call) and admin_disabled are all answered before anything here runs.
    /// The handlers are thin: parse, call options.WorldEvents, render. Every rule lives in
    /// WorldEventAdminService.
    /// </summary>
    public static partial class MarketApiHost
    {
        private static void MapWorldEventAdminRoutes(WebApplication a)
        {
            MapAdmin(a, "/v1/admin/world-events/catalog", (ctx, principal) =>
                WorldEventReply(service => service.Catalog()));

            MapAdmin(a, "/v1/admin/world-events/status", (ctx, principal) =>
                WorldEventReply(service => service.Status()));

            MapAdminPost(a, "/v1/admin/world-events/preview", (ctx, principal, rawBody) =>
                WorldEventReply(service => service.Preview(ParseWorldEventBody<WorldEventStartBody>(rawBody))));

            MapAdminPost(a, "/v1/admin/world-events/start", (ctx, principal, rawBody) =>
                WorldEventReply(service => service.Start(ParseWorldEventBody<WorldEventStartBody>(rawBody), principal.AccountName)));

            MapAdminPost(a, "/v1/admin/world-events/stop", (ctx, principal, rawBody) =>
                WorldEventReply(service => service.Stop(ParseWorldEventBody<WorldEventStopBody>(rawBody), principal.AccountName)));
        }

        /// <summary>
        /// Null for an absent, oversize (MapAdminPost already passes null above MaxAdminBodyBytes) or
        /// malformed body; the service answers world_event_refused for a null body.
        /// </summary>
        private static T ParseWorldEventBody<T>(string rawBody) where T : class
        {
            if (string.IsNullOrWhiteSpace(rawBody))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(rawBody, Json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IResult WorldEventReply(Func<IWorldEventAdminService, WorldEventAdminOutcome> call)
        {
            if (options.WorldEvents == null)
                return Error(MarketError.ServerError);

            var outcome = call(options.WorldEvents);

            if (outcome == null)
                return Error(MarketError.ServerError);

            if (outcome.Error != MarketError.None)
                return Error(outcome.Error, runId: outcome.RunId, reason: outcome.Reason);

            return Results.Json(outcome.Body, Json);
        }
    }
}
