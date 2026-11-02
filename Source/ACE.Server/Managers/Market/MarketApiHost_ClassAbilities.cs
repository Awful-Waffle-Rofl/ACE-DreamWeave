using System;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The class ability planner's owner-scoped game read: one of the session account's own characters, projected
    /// for the planner's "Import character" button. Registered like every account route (Guard: key, session,
    /// in-flight gate) and a READ, so it takes no write bucket. Deliberately not behind a kill switch: it reads
    /// the player's own character and the planner stays usable without it.
    ///
    /// Handlers are thin: the build is CharacterSheetService.GetOwnerProfile (which owns the offline-build cap)
    /// and the projection is ClassAbilityImportProjection (pure, unit-tested).
    /// </summary>
    public static partial class MarketApiHost
    {
        private static void MapClassAbilityRoutes(WebApplication a)
        {
            // TryResolveCharacter FAILS CLOSED, exactly as on the suit-profile route. The quest query parameter
            // (repeated, max 32 values, each ^[A-Za-z0-9_]{1,64}$) selects which CAP quest stamps to report;
            // excess and malformed values are ignored, never an error.
            a.MapGet("/v1/accounts/me/characters/{guid:long}/class-abilities", (HttpContext ctx, long guid) =>
                Guard(ctx, requireSession: true, isWrite: false, handler: accountId =>
                {
                    if (guid <= 0 || guid > uint.MaxValue || !TryResolveCharacter(accountId, (uint)guid, out _))
                        return Error(MarketError.NotOwner);

                    if (options.CharacterSheets == null)
                        return Error(MarketError.SheetsDisabled);

                    var requested = ctx.Request.Query["quest"].ToArray();

                    return WithTimeout(() =>
                    {
                        var result = options.CharacterSheets.GetOwnerProfile((uint)guid);

                        if (result.Outcome != SheetOutcome.Ok || result.Profile == null)
                            return Error(SheetError(result.Outcome));

                        var p = result.Profile;
                        // A missing block is a failed projection, never an empty build: answer the same server_error
                        // SheetError maps Failed to, so the planner cannot import zeros as a real character.
                        var block = p.ClassAbilityImport;
                        if (block == null)
                            return Error(SheetError(SheetOutcome.Failed));

                        return Results.Json(new
                        {
                            character_guid = p.CharacterGuid,
                            name = p.Name,
                            level = p.Level,
                            online = p.Online,
                            quests_completed = ClassAbilityImportProjection.FilterCompleted(block, requested),
                            lum_points_bought = block.LumPointsBought,
                            xp_points_bought = block.XpPointsBought,
                            ranks = block.Ranks,
                            retired_ranks = block.RetiredRanks,
                            points_earned = block.PointsEarned,
                            points_available = block.PointsAvailable,
                            respec_pending = block.RespecPending,
                        }, Json);
                    });
                }));
        }
    }
}
