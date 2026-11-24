using System;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// POST /v1/session/launch: redeems a one-time game launch code (WebLaunchCodeStore) and answers
    /// with the same session reply as POST /v1/session, so a player already signed in to the game
    /// needs no password on the website.
    ///
    /// Key-gated like every route, but NO session, NO in-flight gate and NO MarketManager.Enabled gate,
    /// mirroring /v1/session. Gated by web_launch_enabled, checked BEFORE the code is touched so a
    /// switched-off server does not burn a code. Uses its OWN per-IP bucket, never the password-login
    /// one. The code is never logged.
    /// </summary>
    public static partial class MarketApiHost
    {
        private static void MapLaunchRoutes(WebApplication a)
        {
            a.MapPost("/v1/session/launch", async (HttpContext ctx) =>
            {
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadJson<LaunchRequest>(ctx);

                var store = options.LaunchCodes;
                var enabled = options.WebLaunchEnabled;

                if (store == null || enabled == null || !enabled())
                    return Error(MarketError.LaunchDisabled);

                if (body == null || string.IsNullOrWhiteSpace(body.Code))
                    return Error(MarketError.BadCredentials);

                var ip = SourceAddress(ctx);

                if (!launchLimiter.TryLoginIp(ip))
                    return Error(MarketError.RateLimited);

                if (!store.TryRedeem(body.Code, out var accountId, out var characterGuid))
                {
                    log.Warn($"[MARKET] launch refused (bad_credentials) from {ip}");
                    return Error(MarketError.BadCredentials);
                }

                log.Info($"[MARKET] launch redeemed for account {accountId}");

                // character_guid only when nonzero AND it names one of the account's characters.
                uint? issuing = null;

                if (characterGuid != 0)
                {
                    var roster = options.ListCharacters?.Invoke(accountId);

                    if (roster != null && roster.Any(c => c.guid == characterGuid))
                        issuing = characterGuid;
                }

                return SessionReply(accountId, issuing);
            });
        }

        private sealed class LaunchRequest
        {
            public string Code { get; set; }
        }
    }
}
