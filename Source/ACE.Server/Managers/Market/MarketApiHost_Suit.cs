using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The suit builder's two owner-scoped game routes. Registered like every account route
    /// (Guard: key, session, in-flight gate), and READS, so neither takes the write bucket.
    ///
    /// GATED BY suit_builder_enabled ONLY. Deliberately not by MarketManager.Enabled: the vault and the
    /// character are the player's own, and planning a suit is not trading, so a paused market must not
    /// blank the planner. The switch is checked AFTER the session, so an unauthenticated caller cannot
    /// learn its state.
    ///
    /// Handlers are thin: the vault projection lives in MarketManager.TryGetSuitInventory and the
    /// character build in CharacterSheetService.GetOwnerProfile (which owns the offline-build cap).
    /// </summary>
    public static partial class MarketApiHost
    {
        private static void MapSuitRoutes(WebApplication a)
        {
            // The session account's own vault, suit-relevant holdings only.
            a.MapGet("/v1/accounts/me/suit/inventory", (HttpContext ctx) => Guard(ctx, requireSession: true, isWrite: false, handler: accountId =>
            {
                if (options.SuitBuilderEnabled?.Invoke() != true)
                    return Error(MarketError.SuitBuilderDisabled);

                return WithTimeout(() =>
                {
                    if (!MarketManager.TryGetSuitInventory(accountId, out var items, out var error))
                        return Error(error);

                    return Results.Json(new { items }, Json);
                });
            }));

            // One of the session account's own characters, for the optimizer's skill and attribute inputs
            // and the gear it already wears. TryResolveCharacter FAILS CLOSED, exactly as on the sheet-link routes.
            a.MapGet("/v1/accounts/me/characters/{guid:long}/suit-profile", (HttpContext ctx, long guid) =>
                Guard(ctx, requireSession: true, isWrite: false, handler: accountId =>
                {
                    if (options.SuitBuilderEnabled?.Invoke() != true)
                        return Error(MarketError.SuitBuilderDisabled);

                    if (guid <= 0 || guid > uint.MaxValue || !TryResolveCharacter(accountId, (uint)guid, out _))
                        return Error(MarketError.NotOwner);

                    if (options.CharacterSheets == null)
                        return Error(MarketError.SheetsDisabled);

                    return WithTimeout(() =>
                    {
                        var result = options.CharacterSheets.GetOwnerProfile((uint)guid);

                        if (result.Outcome != SheetOutcome.Ok || result.Profile == null)
                            return Error(SheetError(result.Outcome));

                        var p = result.Profile;

                        return Results.Json(new
                        {
                            character_guid = p.CharacterGuid,
                            name = p.Name,
                            level = p.Level,
                            heritage = p.Heritage,
                            heritage_id = p.HeritageId,
                            online = p.Online,
                            attributes = p.Attributes,
                            vitals = p.Vitals,
                            skills = p.Skills,
                            equipped = p.Equipped,
                            equipped_appraisal = p.EquippedAppraisal,
                        }, Json);
                    });
                }));
        }
    }
}
