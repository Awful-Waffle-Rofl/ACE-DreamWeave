using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The Character Details view (Docs/Market/market-api-v1.yaml, GET /v1/accounts/me/characters/{guid}/details): the
    /// in-game web panel's "Character" tab polls it every 10 seconds. Owner-only, readable from any session on the
    /// account, online only. It runs through the class ability panel's guard as a READ: the panel's own in-flight gate
    /// and its per-account read bucket, never the market's. details_disabled when character_details_web_enabled is off.
    /// </summary>
    public static partial class MarketApiHost
    {
        /// <summary>The timeout sentence: a details read that timed out is simply retried by the next poll.</summary>
        internal const string CharacterDetailsTimeoutMessage = "The server is busy; character details will refresh on the next update.";

        private static void MapCharacterDetailsRoutes(WebApplication a)
        {
            a.MapGet("/v1/accounts/me/characters/{guid:long}/details", (HttpContext ctx, long guid) =>
                PanelGuard(ctx, isWrite: false, handler: (accountId, boundCharacterGuid) =>
                {
                    if (options.CharacterDetailsEnabled?.Invoke() != true)
                        return PanelError(MarketError.DetailsDisabled);

                    if (guid <= 0 || guid > uint.MaxValue || !TryResolveCharacter(accountId, (uint)guid, out _))
                        return PanelError(MarketError.NotOwner);

                    var service = options.CharacterDetails;
                    if (service == null)
                        return PanelError(MarketError.ServerError);

                    var characterGuid = (uint)guid;

                    return PanelWithTimeout(CharacterDetailsTimeoutMessage, () =>
                    {
                        var result = service.Read(accountId, characterGuid);

                        if (result.Error == MarketError.Timeout)
                            return PanelError(MarketError.Timeout, CharacterDetailsTimeoutMessage);

                        if (result.Error != MarketError.None || result.Details == null)
                            return PanelError(result.Error == MarketError.None ? MarketError.ServerError : result.Error);

                        return Results.Json(result.Details, Json);
                    });
                }));
        }
    }
}
