using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The suit builder's transfer routes: move items from the session account's vault into one of its
    /// ONLINE characters' pack. POST is a WRITE (write bucket); the status GET is a read. Both are gated by
    /// suit_builder_enabled only, checked after the session like the other suit routes.
    ///
    /// Handlers are thin: every rule (shape, cap, idempotency, single-flight, the online lookup and all of
    /// the world-side gates) lives in <see cref="SuitTransferService"/>. The POST answers 202 as soon as the
    /// transfer is recorded; the world thread does the moving, and the caller polls the status route.
    /// </summary>
    public static partial class MarketApiHost
    {
        private sealed class SuitTransferBody
        {
            public long? CharacterGuid { get; set; }
            public string IdempotencyKey { get; set; }
            public List<SuitTransferBodyItem> Items { get; set; }
            public bool? AllowListed { get; set; }
        }

        private sealed class SuitTransferBodyItem
        {
            public long? ItemGuid { get; set; }
            public long? Wcid { get; set; }
            public string ClassKey { get; set; }
            public long? Count { get; set; }
        }

        /// <summary>The transfer POST's body cap: 24 lines need a few KB at most, so 64 KiB is generous and still bounded.</summary>
        internal const int MaxSuitTransferBodyBytes = 64 * 1024;

        private static async Task<SuitTransferBody> ReadSuitTransferBody(HttpContext ctx)
        {
            var text = await ReadBodyText(ctx, MaxSuitTransferBodyBytes);

            if (text == null)
                return null;

            // ReadBodyText decodes the raw bytes, so a UTF-8 byte order mark survives as U+FEFF, which deserializing
            // from the string rejects (SuitTransferRoutesTests.Post_WithAByteOrderMark_Is202 fails without this).
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);

            try
            {
                return JsonSerializer.Deserialize<SuitTransferBody>(text, Json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void MapSuitTransferRoutes(WebApplication a)
        {
            a.MapPost("/v1/accounts/me/suit/transfers", async (HttpContext ctx) =>
            {
                // Key first: an unauthenticated caller must not make the process deserialize a body.
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                // Bounded read: no Kestrel request limit is configured for the market host (see MaxAdminBodyBytes), so
                // this cap is the only one. An oversized or malformed body reads as null and answers invalid_transfer.
                var body = await ReadSuitTransferBody(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                {
                    if (options.SuitBuilderEnabled?.Invoke() != true)
                        return Error(MarketError.SuitBuilderDisabled);

                    if (body == null || body.CharacterGuid == null)
                        return Error(MarketError.InvalidTransfer);

                    // The line cap, before anything is built from the list.
                    if (body.Items != null && body.Items.Count > SuitTransferService.MaxObjects)
                        return Error(MarketError.TooManyItems);

                    // The same fail-closed roster check every account route uses for a posted character.
                    if (body.CharacterGuid.Value <= 0 || body.CharacterGuid.Value > uint.MaxValue || !TryResolveCharacter(accountId, (uint)body.CharacterGuid.Value, out _))
                        return Error(MarketError.NotOwner);

                    if (!TryBuildSuitTransferLines(body.Items, out var lines))
                        return Error(MarketError.InvalidTransfer);

                    if (options.SuitTransfers == null)
                        return Error(MarketError.ServerError);

                    var request = new SuitTransferRequest(accountId, (uint)body.CharacterGuid.Value, body.IdempotencyKey, lines, body.AllowListed == true);

                    return WithTimeout(() =>
                    {
                        var result = options.SuitTransfers.Submit(request);

                        if (result.Error != MarketError.None)
                            return Error(result.Error);

                        return Results.Json(new { transfer_id = result.TransferId }, Json, statusCode: StatusCodes.Status202Accepted);
                    });
                });
            });

            a.MapGet("/v1/accounts/me/suit/transfers/{transferId}", (HttpContext ctx, string transferId) =>
                Guard(ctx, requireSession: true, isWrite: false, handler: accountId =>
                {
                    if (options.SuitBuilderEnabled?.Invoke() != true)
                        return Error(MarketError.SuitBuilderDisabled);

                    if (options.SuitTransfers == null)
                        return Error(MarketError.ServerError);

                    var status = options.SuitTransfers.GetStatus(accountId, transferId);

                    if (status == null)
                        return Error(MarketError.TransferNotFound);

                    return Results.Json(new
                    {
                        transfer_id = status.TransferId,
                        state = status.State,
                        reason = status.Reason == MarketError.None ? null : MarketErrorCodes.ToCode(status.Reason),
                        items = status.Items.Select(i => new
                        {
                            item_guid = i.ItemGuid,
                            wcid = i.Wcid,
                            class_key = i.ClassKey,
                            count = i.Count,
                            outcome = i.Outcome,
                            message = i.Message,
                        }).ToList(),
                    }, Json);
                }));
        }

        /// <summary>
        /// The wire lines to service lines. False for anything the service could not even represent (a missing
        /// wcid or count, a value out of range); the service's own Validate then judges the rest.
        /// </summary>
        private static bool TryBuildSuitTransferLines(List<SuitTransferBodyItem> items, out List<SuitTransferLine> lines)
        {
            lines = new List<SuitTransferLine>();

            if (items == null)
                return false;

            foreach (var item in items)
            {
                if (item == null || item.Wcid == null || item.Count == null)
                    return false;

                if (item.Wcid.Value < 0 || item.Wcid.Value > uint.MaxValue)
                    return false;

                if (item.ItemGuid != null && (item.ItemGuid.Value <= 0 || item.ItemGuid.Value > uint.MaxValue))
                    return false;

                if (item.Count.Value < 1 || item.Count.Value > int.MaxValue)
                    return false;

                lines.Add(new SuitTransferLine(item.ItemGuid == null ? (uint?)null : (uint)item.ItemGuid.Value, (uint)item.Wcid.Value, item.ClassKey, (int)item.Count.Value));
            }

            return true;
        }
    }
}
