using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using log4net;

using ACE.Common.Extensions;
using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Managers.Market
{
    /// <summary>Everything MarketApiHost needs, injected so the tests need no live Player and no MySQL.</summary>
    public sealed class MarketApiOptions
    {
        public string ListenUrl { get; set; } = "http://127.0.0.1:5090";
        public string SharedKey { get; set; }
        public int MaxInFlight { get; set; } = 8;

        /// <summary>
        /// A bulk vault action costs one server write per row, so at the old default of 20 a batch
        /// larger than 20 began failing partway. The in-flight gate (<see cref="MaxInFlight"/>) is
        /// the protection for the server itself, since it caps concurrent requests server-wide; this
        /// bucket only rations a single account, so raising it does not weaken that protection.
        /// </summary>
        public int WriteRatePerMinute { get; set; } = 120;
        public int RequestTimeoutMs { get; set; } = 3000;
        public int LoginRatePerMinutePerAccount { get; set; } = 5;

        /// <summary>
        /// Per source address as the server sees it. The web app does not forward the browser's
        /// address, so today this is ONE bucket shared by every player reaching it through that app.
        /// </summary>
        public int LoginRatePerMinutePerIp { get; set; } = 30;

        /// <summary>
        /// The ONLY address whose X-Forwarded-For is honoured (DESIGN section 7). Null ignores the
        /// header entirely; trusting it from anywhere lets any caller forge the per-IP login key.
        /// </summary>
        public string TrustedForwardedForSource { get; set; }

        public int SessionLifetimeMinutes { get; set; } = 720;

        public IMarketWallet Wallet { get; set; }

        /// <summary>(accountName, password) -> account id, or 0 for any failure. Never distinguishes why.</summary>
        public Func<string, string, uint> AuthenticateAccount { get; set; }

        public Func<uint, IReadOnlyList<(uint guid, string name)>> ListCharacters { get; set; }

        /// <summary>Null disables the /v1/sheets* routes entirely (answers sheets_disabled), e.g. when the market host runs without CharacterSheetService wired.</summary>
        public ICharacterSheetService CharacterSheets { get; set; }

        /// <summary>
        /// The public sheet route's OWN in-flight gate, separate from <see cref="MaxInFlight"/>, so
        /// anonymous sheet traffic can never take the market's slots.
        /// </summary>
        public int SheetMaxInFlight { get; set; } = 4;

        /// <summary>Uncached, current read of the auth account row - see AuthenticationDatabase.GetAccountById. Null disables every /v1/admin route (AdminAuthorizer.Check answers Unavailable, which the route maps to server_error).</summary>
        public Func<uint, ACE.Database.Models.Auth.Account> GetAccountById { get; set; }

        /// <summary>The suit_builder_enabled live switch. Gates the two /v1/accounts/me/suit* and suit-profile routes and NOTHING else; independent of MarketManager.Enabled. Null reads as OFF (an unwired host serves no suit route); Program.cs wires it.</summary>
        public Func<bool> SuitBuilderEnabled { get; set; }

        /// <summary>POST /v1/accounts/me/suit/transfers and its status route. Null makes both answer server_error. Internal: the service moves vault items and is not part of the public surface.</summary>
        internal ACE.Server.Entity.AccountVault.SuitTransferService SuitTransfers { get; set; }

        /// <summary>The admin_web_enabled live switch. Checked AFTER authorization, so a non-Admin cannot learn its state.</summary>
        public Func<bool> AdminWebEnabled { get; set; }

        /// <summary>Matches PropertyManager.EnumerateWithPrefix's return shape exactly, so Program.cs can wire it with a one-line lambda.</summary>
        public Func<IEnumerable<(string key, string type, string current, string @default, string description)>> EnumerateSettings { get; set; }

        /// <summary>Null means load the embedded Source/ACE.Server/config-metadata.tsv.</summary>
        public ConfigMetadata SettingsMetadata { get; set; }

        /// <summary>Null makes POST /v1/admin/settings/{key} answer server_error.</summary>
        public Action<ACE.Server.Entity.Actions.IAction> EnqueueWorldAction { get; set; }

        /// <summary>The chat feed GET /v1/admin/chat reads. Null means AdminChatFeed.Shared (PLAN-P3.md section 2).</summary>
        public AdminChatFeed ChatFeed { get; set; }

        /// <summary>Normalized text -> recipient count. Null makes POST /v1/admin/announce answer server_error.</summary>
        public Func<string, int> SendAnnouncement { get; set; }

        /// <summary>(actorLabel, message). Null makes POST /v1/admin/announce answer server_error.</summary>
        public Action<string, string> WriteAdminAudit { get; set; }

        public int AdminAnnounceRatePerMinute { get; set; } = 6;

        /// <summary>GET /v1/admin/commands (PLAN-P4.md P4a). Null makes the route answer server_error.</summary>
        public ACE.Server.Command.Web.WebCommandListService Commands { get; set; }

        /// <summary>The admin_web_commands_enabled live switch (PLAN-P4.md ruling R6). Checked after authorization and after admin_web_enabled; null or false answers commands_disabled.</summary>
        public Func<bool> AdminWebCommandsEnabled { get; set; }

        /// <summary>POST /v1/admin/commands/run (PLAN-P4.md P4b). Null makes the route answer server_error. Internal: the dispatcher runs server commands and is not part of the public surface.</summary>
        internal ACE.Server.Command.Web.WebCommandDispatcher CommandDispatcher { get; set; }

        /// <summary>
        /// How long POST /v1/admin/commands/run waits for a web command to finish, or for the world thread
        /// to start a character command, before answering timeout or command_not_started. Program.cs
        /// passes it to the WebCommandDispatcher it builds. Below RequestTimeoutMs and the market's
        /// WriteTimeoutMs (5000), so the market always hears the result.
        /// </summary>
        public int CommandRunTimeoutMs { get; set; } = 2500;

        /// <summary>The /v1/admin/world-events routes (Docs/AdminPanel/WORLD-EVENTS-START.md). Null makes every one of them answer server_error.</summary>
        public ACE.Server.WorldEvents.IWorldEventAdminService WorldEvents { get; set; }
    }

    /// <summary>
    /// The market's HTTP front end (DESIGN 4.1 and 6), started from Program.cs only when Market.Enabled.
    /// HANDLERS ARE THIN: validate, marshal into MarketManager, await with a hard timeout, serialize -
    /// no rule lives here since /market commands share the manager, and the WORLD THREAD IS NEVER BLOCKED (Kestrel pool). A wrong/missing X-Market-Key gets 401 with ZERO bytes BY DESIGN; binds to loopback by default.
    /// </summary>
    public static partial class MarketApiHost
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>snake_case with named enums; this IS the published contract (Docs/Market/market-api-v1.yaml).</summary>
        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };

        private static WebApplication app;
        private static MarketApiOptions options;
        private static MarketSessionStore sessions;
        private static MarketRateLimiter limiter;
        private static MarketRateLimiter sheetLimiter;
        private static MarketRateLimiter announceLimiter;
        private static AdminAuthorizer adminAuthorizer;
        private static ConfigMetadata settingsMetadata;

        public static bool IsRunning => app != null;

        /// <summary>The URL actually bound, which resolves an ephemeral ":0" port to the real one.</summary>
        public static string ListeningUrl { get; private set; }

        public static void Start(MarketApiOptions apiOptions)
        {
            if (app != null)
                Stop();

            if (apiOptions == null || string.IsNullOrWhiteSpace(apiOptions.SharedKey))
            {
                log.Error("[MARKET] the API host will NOT start: Market.SharedKey is empty. An unauthenticated market API is not a degraded mode, it is a hole.");
                return;
            }

            options = apiOptions;
            sessions = new MarketSessionStore(apiOptions.SessionLifetimeMinutes);
            limiter = new MarketRateLimiter(apiOptions.MaxInFlight, apiOptions.WriteRatePerMinute,
                                            apiOptions.LoginRatePerMinutePerAccount, apiOptions.LoginRatePerMinutePerIp);

            adminAuthorizer = new AdminAuthorizer(apiOptions.GetAccountById);
            settingsMetadata = apiOptions.SettingsMetadata ?? ConfigMetadata.LoadEmbedded();
            announceLimiter = new MarketRateLimiter(1, Math.Max(1, apiOptions.AdminAnnounceRatePerMinute), 1, 1);

            // Only its in-flight gate is used, so its other buckets are irrelevant; the shape matches
            // the main limiter's constructor for parity, not because logins/writes ever route through it.
            sheetLimiter = new MarketRateLimiter(Math.Max(1, apiOptions.SheetMaxInFlight), 1, 1, 1);

            var builder = WebApplication.CreateSlimBuilder();

            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(apiOptions.ListenUrl);

            app = builder.Build();

            MapRoutes(app);

            app.Start();

            ListeningUrl = app.Urls.FirstOrDefault() ?? apiOptions.ListenUrl;

            log.Info($"[MARKET] API listening on {ListeningUrl}.");
        }

        public static void Stop()
        {
            var running = app;
            app = null;

            if (running == null)
                return;

            try
            {
                running.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                running.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] error stopping the API host: {ex.GetFullMessage()}");
            }

            sessions?.Clear();
            sessions = null;
            limiter = null;
            sheetLimiter = null;
            announceLimiter = null;
            adminAuthorizer = null;
            settingsMetadata = null;
            ListeningUrl = null;

            log.Info("[MARKET] API stopped.");
        }

        // ---- routes ----

        private static void MapRoutes(WebApplication a)
        {
            // The account's characters with their live balances. SHARED by POST /v1/session and
            // GET /v1/accounts/me deliberately: the balance refresh is exactly the session response
            // minus the token, and building it twice is how the two silently drift apart. Both option
            // delegates are read here and nowhere else on either route.
            static List<object> CharacterSummaries(uint accountId)
            {
                var characters = options.ListCharacters?.Invoke(accountId) ?? new List<(uint guid, string name)>();

                return characters.Select(c => (object)new
                {
                    guid = c.guid,
                    name = c.name,
                    balance_mmd = options.Wallet?.GetBalanceMmd(c.guid) ?? 0,
                }).ToList();
            }

            // Liveness, key-gated like everything else so it does not confirm its own existence.
            a.MapGet("/v1/health", (HttpContext ctx) => Guard(ctx, requireSession: false, isWrite: false, handler: _ =>
                Results.Json(new { status = "ok", generation = MarketManager.FeedGeneration, enabled = MarketManager.Enabled }, Json)));

            a.MapPost("/v1/session", async (HttpContext ctx) =>
            {
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadJson<SessionRequest>(ctx);

                if (body == null || string.IsNullOrWhiteSpace(body.AccountName))
                    return Error(MarketError.BadCredentials);

                if (!limiter.TryLoginIp(SourceAddress(ctx)) || !limiter.TryLoginAccount(body.AccountName))
                    return Error(MarketError.RateLimited);

                var accountId = options.AuthenticateAccount?.Invoke(body.AccountName, body.Password) ?? 0;

                if (accountId == 0)
                    return Error(MarketError.BadCredentials);

                return Results.Json(new
                {
                    session_token = sessions.Issue(accountId),
                    account_id = accountId,
                    characters = CharacterSummaries(accountId),
                }, Json);
            });

            // The balance refresh: the session response minus the token, so the web app can reprice a
            // page without logging in again. A READ - it touches no vault and mutates nothing, so it
            // takes the in-flight gate but not the write bucket.
            //
            // NO MarketManager.Enabled GATE, mirroring /v1/session rather than the vault route. Both
            // of these only read the account roster and the bank, neither of which the market kill
            // switch owns; gating them would blank the web app's own header the moment the switch
            // flipped, which is exactly when a player most needs to see their balance is intact.
            a.MapGet("/v1/accounts/me", (HttpContext ctx) => Guard(ctx, requireSession: true, isWrite: false, handler: accountId =>
                Results.Json(new
                {
                    account_id = accountId,
                    characters = CharacterSummaries(accountId),
                }, Json)));

            a.MapGet("/v1/accounts/me/vault", (HttpContext ctx) => Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
            {
                if (!MarketManager.Enabled)
                    return Error(MarketError.Disabled);

                return WithTimeout(() =>
                {
                    if (!MarketManager.TryGetVaultView(accountId, out var entries, out var error))
                        return Error(error);

                    return Results.Json(new { entries }, Json);
                });
            }));

            // A VAULT operation reached through the market's door, and the route name says so. It is
            // here because this is the only HTTP surface the web app speaks.
            a.MapPost("/v1/accounts/me/vault/barrel", async (HttpContext ctx) =>
            {
                // Key first: an unauthenticated caller must not make the process deserialize a body.
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadJson<BarrelRequest>(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                {
                    if (body == null)
                        return Error(MarketError.ServerError);

                    if (!MarketManager.Enabled)
                        return Error(MarketError.Disabled);

                    // An ABSENT count is not a count of one. This endpoint DESTROYS things, so "the
                    // client did not say how many" must not silently become a number; it is refused
                    // the same way an absent price is on POST /v1/listings.
                    if (body.Count == null)
                        return Error(MarketError.CountUnavailable);

                    if (body.Count.Value < 1)
                        return Error(MarketError.CountUnavailable);

                    // The audit row records WHO destroyed it, so a character is required and must
                    // belong to this session's account - the same rule and the same helper POST
                    // /v1/listings uses for its seller. An absent guid is refused rather than filled
                    // in from the roster: naming an arbitrary character in an audit row of a
                    // destruction is worse than refusing the request.
                    if (!TryResolveCharacter(accountId, body.CharacterGuid ?? 0, out var actorName))
                        return Error(MarketError.NotOwner);

                    var actor = new MarketActor(accountId, body.CharacterGuid.Value, actorName);

                    return WithTimeout(() =>
                    {
                        // class_key present: a class LINE. The guid must be absent and the wcid is not
                        // consulted (the wire sends 0); both rules live in MarketManager.Barrel, which
                        // answers item_not_found for a malformed key or one sent with a guid.
                        var result = MarketManager.Barrel(actor, body.ItemGuid, body.Wcid, body.Count.Value, body.ClassKey);

                        if (!result.Ok)
                            return Error(result.Error);

                        // The updated vault, so the caller re-renders from one answer rather than
                        // following up with a second request that could interleave with another
                        // window's write.
                        if (!MarketManager.TryGetVaultView(accountId, out var entries, out var viewError))
                            return Error(viewError);

                        return Results.Json(new { entries }, Json);
                    });
                });
            });

            a.MapPost("/v1/listings", async (HttpContext ctx) =>
            {
                // Key first: an unauthenticated caller must not make the process deserialize a body.
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadJson<CreateListingRequest>(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                {
                    if (body == null)
                        return Error(MarketError.ServerError);

                    // An ABSENT price is not a price of zero. Zero is a giveaway, so a body that
                    // simply omits the field must be refused rather than read as one.
                    if (body.PriceMmd == null)
                        return Error(MarketError.InvalidPrice);

                    if (!TryResolveCharacter(accountId, body.SellerCharacterGuid, out var sellerName))
                        return Error(MarketError.NotOwner);

                    var actor = new MarketActor(accountId, body.SellerCharacterGuid, sellerName);

                    return WithTimeout(() =>
                    {
                        // class_key present: a class LINE; the wcid comes from the resolved line and the
                        // guid must be absent (MarketManager.List refuses otherwise, with item_not_found).
                        // Absent: exactly the pre-class call, so an old client is unaffected.
                        var result = MarketManager.List(actor, body.ItemGuid, body.Wcid, body.Count <= 0 ? 1 : body.Count, body.PriceMmd.Value,
                                                        MarketChannel.Web, body.ClassKey);

                        return result.Ok ? Results.Json(Dto(result.Value), Json) : Error(result.Error, result.CurrentPriceMmd);
                    });
                });
            });

            a.MapDelete("/v1/listings/{id:int}", (HttpContext ctx, uint id) =>
                Guard(ctx, requireSession: true, isWrite: true, handler: accountId => WithTimeout(() =>
                {
                    // Character guid 0: this route resolves an ACCOUNT, never a character, so any
                    // rejection row it produces carries an unknown actor character by construction.
                    var result = MarketManager.Delist(new MarketActor(accountId, 0, string.Empty), id, MarketChannel.Web);

                    return result.Ok ? Results.Json(Dto(result.Value), Json) : Error(result.Error);
                })));

            a.MapPost("/v1/listings/{id:int}/buy", async (HttpContext ctx, uint id) =>
            {
                // Key first: an unauthenticated caller must not make the process deserialize a body.
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadJson<BuyRequest>(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                {
                    if (body == null)
                        return Error(MarketError.ServerError);

                    // The staleness check is an exact comparison, so an absent expected price would
                    // MATCH a free listing and take it without the buyer ever agreeing to a number.
                    if (body.ExpectedPriceMmd == null)
                        return Error(MarketError.InvalidPrice);

                    if (!TryResolveCharacter(accountId, body.BuyerCharacterGuid, out var buyerName))
                        return Error(MarketError.NotOwner);

                    var buyer = new MarketActor(accountId, body.BuyerCharacterGuid, buyerName);

                    return WithTimeout(() =>
                    {
                        var result = MarketManager.Buy(buyer, id, body.Count <= 0 ? 1 : body.Count, body.ExpectedPriceMmd.Value, MarketChannel.Web);

                        return result.Ok ? Results.Json(Dto(result.Value), Json) : Error(result.Error, result.CurrentPriceMmd);
                    });
                });
            });

            a.MapGet("/v1/listings/changes", (HttpContext ctx, long? since, int? limit) =>
                Guard(ctx, requireSession: false, isWrite: false, handler: _ =>
                {
                    var take = Clamp(limit);
                    var page = MarketManager.GetListingChanges(since ?? 0, take, out var next);

                    return Results.Json(new
                    {
                        generation = MarketManager.FeedGeneration,
                        since = since ?? 0,
                        next_since = next,
                        limit = take,
                        listings = page.Select(Dto).ToList(),
                    }, Json);
                }));

            a.MapGet("/v1/history/changes", (HttpContext ctx, long? since, int? limit) =>
                Guard(ctx, requireSession: false, isWrite: false, handler: _ =>
                {
                    var take = Clamp(limit);
                    var page = MarketManager.GetHistoryChanges(since ?? 0, take, out var next);

                    return Results.Json(new
                    {
                        generation = MarketManager.FeedGeneration,
                        since = since ?? 0,
                        next_since = next,
                        limit = take,
                        transactions = page.Select(Dto).ToList(),
                    }, Json);
                }));

            // ---- Wanted buy orders (WANTED-DESIGN section 6) ----

            a.MapGet("/v1/materials", (HttpContext ctx) => Guard(ctx, requireSession: false, isWrite: false, handler: _ =>
                Results.Json(new
                {
                    materials = MarketSalvageMaterials.All().Select(m => new
                    {
                        material_type = m.MaterialType, material_name = m.MaterialName, wcid = m.Wcid, bag_name = m.BagName,
                        icon_id = m.IconId, icon_overlay_id = m.IconOverlayId, icon_underlay_id = m.IconUnderlayId,

                        // Which materials a salvage_hammer order may name. A per-material flag rather
                        // than a second route, so the web filters its own kind selector from the list
                        // it already caches; wcid/bag_name/icons stay the BAG's on every row.
                        has_hammer = m.HasHammer,
                    }).ToList(),
                }, Json)));

            a.MapPost("/v1/orders", async (HttpContext ctx) =>
            {
                if (!KeyOk(ctx)) return NoBody(ctx);
                var body = await ReadJson<PlaceOrderRequest>(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                {
                    if (body == null) return Error(MarketError.ServerError);
                    if (body.MaterialType == null) return Error(MarketError.InvalidMaterial);
                    if (!TryParseOrderKind(body.Kind, out var kind)) return Error(MarketError.InvalidMaterial);
                    if (body.Count == null) return Error(MarketError.CountUnavailable);
                    if (body.PriceMmd == null) return Error(MarketError.InvalidPrice);
                    if (!TryResolveCharacter(accountId, body.BuyerCharacterGuid ?? 0, out var buyerName)) return Error(MarketError.NotOwner);

                    var buyer = new MarketActor(accountId, body.BuyerCharacterGuid.Value, buyerName);

                    return WithTimeout(() =>
                    {
                        var result = MarketManager.PlaceOrder(buyer, body.MaterialType.Value, kind, body.Count.Value, body.PriceMmd.Value, MarketChannel.Web);
                        return result.Ok ? Results.Json(Dto(result.Value), Json) : Error(result.Error);
                    });
                });
            });

            a.MapDelete("/v1/orders/{id:int}", (HttpContext ctx, uint id) =>
                Guard(ctx, requireSession: true, isWrite: true, handler: accountId => WithTimeout(() =>
                {
                    // Character guid 0, like DELETE /v1/listings: this route resolves an ACCOUNT.
                    var result = MarketManager.CancelOrder(new MarketActor(accountId, 0, string.Empty), id, MarketChannel.Web);
                    return result.Ok ? Results.Json(Dto(result.Value), Json) : Error(result.Error);
                })));

            a.MapPost("/v1/orders/{id:int}/fill", async (HttpContext ctx, uint id) =>
            {
                if (!KeyOk(ctx)) return NoBody(ctx);
                var body = await ReadJson<FillOrderRequest>(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                {
                    if (body == null) return Error(MarketError.ServerError);
                    if (body.Count == null) return Error(MarketError.CountUnavailable);
                    if (!TryResolveCharacter(accountId, body.SellerCharacterGuid ?? 0, out var sellerName)) return Error(MarketError.NotOwner);

                    var seller = new MarketActor(accountId, body.SellerCharacterGuid.Value, sellerName);

                    return WithTimeout(() =>
                    {
                        var result = MarketManager.FillOrder(seller, id, body.Count.Value, MarketChannel.Web);
                        return result.Ok ? Results.Json(Dto(result.Value), Json) : Error(result.Error);
                    });
                });
            });

            a.MapGet("/v1/orders/changes", (HttpContext ctx, long? since, int? limit) =>
                Guard(ctx, requireSession: false, isWrite: false, handler: _ =>
                {
                    var take = Clamp(limit);
                    var page = MarketManager.GetOrderChanges(since ?? 0, take, out var next);
                    return Results.Json(new
                    {
                        generation = MarketManager.FeedGeneration, since = since ?? 0, next_since = next, limit = take,
                        orders = page.Select(Dto).ToList(),
                    }, Json);
                }));

            // ---- Character Sheet (Docs/CharacterSheet/DESIGN.md 4.1) ----

            // PUBLIC read: key only, no session, and its OWN in-flight gate so anonymous sheet traffic
            // can never take the market's slots. Not gated on MarketManager.Enabled.
            a.MapGet("/v1/sheets/{slug}", (HttpContext ctx, string slug) =>
            {
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                if (options.CharacterSheets == null)
                    return Error(MarketError.SheetsDisabled);

                if (!sheetLimiter.TryEnterInFlight())
                    return Error(MarketError.SheetBusy);

                try
                {
                    var result = options.CharacterSheets.GetSheet(slug);
                    return result.Outcome == SheetOutcome.Ok ? Results.Json(result.Sheet, Json) : Error(SheetError(result.Outcome));
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] unhandled error serving a sheet: {ex.GetFullMessage()}");
                    return Error(MarketError.ServerError);
                }
                finally
                {
                    sheetLimiter.ExitInFlight();
                }
            });

            a.MapGet("/v1/accounts/me/characters/{guid:long}/sheet-link", (HttpContext ctx, long guid) =>
                Guard(ctx, requireSession: true, isWrite: false, handler: accountId => SheetLinkReply(accountId, guid, s => s.GetLink((uint)guid))));

            a.MapPost("/v1/accounts/me/characters/{guid:long}/sheet-link", async (HttpContext ctx, long guid) =>
            {
                if (!KeyOk(ctx))
                    return NoBody(ctx);

                var body = await ReadJson<SheetLinkRequest>(ctx);

                return Guard(ctx, requireSession: true, isWrite: true, handler: accountId =>
                    SheetLinkReply(accountId, guid, s => s.EnableOrRotate((uint)guid, body?.Rotate == true)));
            });

            a.MapDelete("/v1/accounts/me/characters/{guid:long}/sheet-link", (HttpContext ctx, long guid) =>
                Guard(ctx, requireSession: true, isWrite: true, handler: accountId => SheetLinkReply(accountId, guid, s => s.Disable((uint)guid))));

            MapSuitRoutes(a);

            MapSuitTransferRoutes(a);

            MapAdminRoutes(a);
        }

        private static IResult SheetLinkReply(uint accountId, long guid, Func<ICharacterSheetService, LinkResult> op)
        {
            if (guid <= 0 || guid > uint.MaxValue || !TryResolveCharacter(accountId, (uint)guid, out _))
                return Error(MarketError.NotOwner);

            if (options.CharacterSheets == null)
                return Error(MarketError.SheetsDisabled);

            var result = op(options.CharacterSheets);
            return result.Outcome == SheetOutcome.Ok ? Results.Json(result.Link, Json) : Error(SheetError(result.Outcome));
        }

        private static MarketError SheetError(SheetOutcome outcome) => outcome switch
        {
            SheetOutcome.NotFound => MarketError.SheetNotFound,
            SheetOutcome.Disabled => MarketError.SheetsDisabled,
            SheetOutcome.Busy     => MarketError.SheetBusy,
            _                     => MarketError.ServerError,
        };

        private static int Clamp(int? limit)
        {
            var value = limit ?? MarketManager.MaxFeedRows;
            return value < 1 || value > MarketManager.MaxFeedRows ? MarketManager.MaxFeedRows : value;
        }

        // ---- the guard: key, session, in-flight gate, write bucket ----

        private static IResult Guard(HttpContext ctx, bool requireSession, bool isWrite, Func<uint, IResult> handler)
        {
            if (!KeyOk(ctx))
                return NoBody(ctx);

            uint accountId = 0;

            if (requireSession && !sessions.TryResolve(Bearer(ctx), out accountId))
                return Error(MarketError.BadCredentials);

            // Gate before bucket: the gate protects the server, the bucket only rations one account.
            if (!limiter.TryEnterInFlight())
                return Error(MarketError.RateLimited);

            try
            {
                if (isWrite && !limiter.TryWrite(accountId))
                    return Error(MarketError.RateLimited);

                return handler(accountId);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] unhandled error serving {ctx.Request.Method} {ctx.Request.Path}: {ex.GetFullMessage()}");
                return Error(MarketError.ServerError);
            }
            finally
            {
                limiter.ExitInFlight();
            }
        }

        /// <summary>
        /// Waits at most RequestTimeoutMs. On timeout the client gets 503 and the WORK IS NOT
        /// CANCELLED - it completes or rolls back on its own, which is why abandoning it is safe.
        /// </summary>
        private static IResult WithTimeout(Func<IResult> work)
        {
            var task = Task.Run(work);

            if (task.Wait(options.RequestTimeoutMs))
                return task.Result;

            log.Warn($"[MARKET] a request exceeded {options.RequestTimeoutMs} ms and was answered 503. The enqueued work is still running and will complete or roll back on its own.");

            return Error(MarketError.Timeout);
        }

        private static bool KeyOk(HttpContext ctx)
        {
            var provided = ctx.Request.Headers["X-Market-Key"].ToString();

            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(options.SharedKey))
                return false;

            // Fixed-time: a length-or-prefix compare over a shared secret is a timing oracle.
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided),
                System.Text.Encoding.UTF8.GetBytes(options.SharedKey));
        }

        /// <summary>
        /// A posted character guid must belong to the SESSION'S account: the token binds an account
        /// only, and money is debited by character guid through a global lookup, so an unchecked
        /// guid spends another player's bank. FAILS CLOSED - an unresolvable roster refuses.
        /// </summary>
        private static bool TryResolveCharacter(uint accountId, uint characterGuid, out string name)
        {
            name = string.Empty;

            if (accountId == 0 || characterGuid == 0)
                return false;

            var characters = options.ListCharacters?.Invoke(accountId);

            if (characters == null)
                return false;

            foreach (var character in characters)
            {
                if (character.guid != characterGuid)
                    continue;

                // The posted name is as unvalidated as the guid, and it lands in the transaction record.
                name = character.name ?? string.Empty;
                return true;
            }

            return false;
        }

        private static IResult NoBody(HttpContext ctx)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Results.Empty;
        }

        private static string Bearer(HttpContext ctx)
        {
            var header = ctx.Request.Headers["Authorization"].ToString();

            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header.Substring(7).Trim() : null;
        }

        /// <summary>
        /// X-Forwarded-For is honoured ONLY from the configured web container address (DESIGN
        /// section 7); trusting it from anywhere would let any caller forge the per-IP login key.
        /// </summary>
        private static string SourceAddress(HttpContext ctx)
        {
            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (string.IsNullOrWhiteSpace(options.TrustedForwardedForSource) || remote != options.TrustedForwardedForSource)
                return remote;

            var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();

            if (string.IsNullOrWhiteSpace(forwarded))
                return remote;

            return forwarded.Split(',')[0].Trim();
        }

        private static async Task<T> ReadJson<T>(HttpContext ctx) where T : class
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Every admin POST body must fit two short strings - PLAN-P2.md ruling 20. No Kestrel
        /// request limit is configured for the market host, so this reader is the only bound. It is
        /// the SINGLE source of the 8 KiB figure: internal (not private) so AdminAnnouncement and the
        /// test suite reference this constant rather than repeating the literal (PLAN-P3.md code
        /// review, 2026-09-15) - raising it here now moves every dependent check with it.
        /// </summary>
        internal const int MaxAdminBodyBytes = 8192;

        /// <summary>
        /// Reads at most <paramref name="maxBytes"/> + 1 bytes and stops as soon as that many have
        /// arrived. More than <paramref name="maxBytes"/>, or any read/decode failure, returns null
        /// WITHOUT reading the rest of the stream - every MapAdminPost route treats a null body as 400.
        /// </summary>
        private static async Task<string> ReadBodyText(HttpContext ctx, int maxBytes)
        {
            try
            {
                var buffer = new byte[maxBytes + 1];
                var total = 0;

                while (total < buffer.Length)
                {
                    var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
                    if (read == 0)
                        break;

                    total += read;
                }

                if (total > maxBytes)
                    return null;

                return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- the error envelope, exactly as published ----

        private static IResult Error(MarketError error, long currentPriceMmd = 0, string currentValue = null, string busyCommand = null,
            uint? runId = null, string reason = null)
        {
            var status = StatusFor(error);

            // The World Event refusals (WORLD-EVENTS-START.md): run_id on world_event_running and
            // world_event_run_changed, reason on invalid_location and world_event_refused. A null field is
            // omitted by the host-wide WhenWritingNull, so each code carries only its own extra.
            if (runId != null || reason != null)
                return Results.Json(new
                {
                    error = MarketErrorCodes.ToCode(error),
                    message = MarketErrorCodes.ToMessage(error),
                    run_id = runId,
                    reason,
                }, Json, statusCode: status);

            if (error == MarketError.CommandBusy)
                return Results.Json(new
                {
                    error = MarketErrorCodes.ToCode(error),
                    message = MarketErrorCodes.ToMessage(error),
                    busy_command = busyCommand,
                }, Json, statusCode: status);

            if (error == MarketError.PriceChanged)
                return Results.Json(new
                {
                    error = MarketErrorCodes.ToCode(error),
                    message = MarketErrorCodes.ToMessage(error),
                    current_price_mmd = currentPriceMmd,
                }, Json, statusCode: status);

            if (error == MarketError.SettingChanged)
                return Results.Json(new
                {
                    error = MarketErrorCodes.ToCode(error),
                    message = MarketErrorCodes.ToMessage(error),
                    current_value = currentValue,
                }, Json, statusCode: status);

            return Results.Json(new
            {
                error = MarketErrorCodes.ToCode(error),
                message = MarketErrorCodes.ToMessage(error),
            }, Json, statusCode: status);
        }

        /// <summary>
        /// The published status for each refusal. Split out of <see cref="Error"/> and made visible to
        /// ACE.Server.Tests (InternalsVisibleTo, ACE.Server.csproj:15) so the map can be asserted
        /// member by member: an Error() call renders an IResult, which cannot be read back without
        /// executing it, so before this the whole table was reachable only through a live route and
        /// most of its entries were never exercised at all. A member that falls through to the default
        /// ships as 500 to the web app, which is a bug the enum itself cannot show you.
        /// </summary>
        internal static int StatusFor(MarketError error)
        {
            return error switch
            {
                MarketError.BadCredentials    => StatusCodes.Status401Unauthorized,
                MarketError.NotOwner          => StatusCodes.Status403Forbidden,
                MarketError.ListingNotActive  => StatusCodes.Status404NotFound,
                MarketError.ItemNotFound      => StatusCodes.Status404NotFound,
                MarketError.PriceChanged      => StatusCodes.Status409Conflict,
                MarketError.AlreadyListed     => StatusCodes.Status409Conflict,
                MarketError.CountUnavailable  => StatusCodes.Status409Conflict,
                MarketError.InsufficientFunds => StatusCodes.Status402PaymentRequired,
                MarketError.InvalidPrice      => StatusCodes.Status400BadRequest,
                MarketError.RateLimited       => StatusCodes.Status429TooManyRequests,
                MarketError.VaultUnavailable  => StatusCodes.Status503ServiceUnavailable,
                MarketError.VaultFull         => StatusCodes.Status409Conflict,
                MarketError.Timeout           => StatusCodes.Status503ServiceUnavailable,
                MarketError.Disabled          => StatusCodes.Status503ServiceUnavailable,

                // 500 like ServerError, and stated rather than left to the default: a client must not
                // be given a status that reads as retryable for a payment whose state is unknown. The
                // difference between the two travels in the "ledger_unknown" code, not in the status.
                MarketError.LedgerUnknown     => StatusCodes.Status500InternalServerError,

                // A refusal the caller has to act on, not a fault: take the listing down first.
                MarketError.ItemListed        => StatusCodes.Status409Conflict,

                // Transient, and shaped like VaultUnavailable and Timeout, which is what a client
                // needs in order to know it may try again.
                MarketError.BarrelUnavailable => StatusCodes.Status503ServiceUnavailable,

                // A state conflict the seller resolves by delisting, not a rate limit that clears on
                // its own - 409, not 429.
                MarketError.ListingLimit      => StatusCodes.Status409Conflict,

                MarketError.OrderExists       => StatusCodes.Status409Conflict,
                MarketError.OrderNotActive    => StatusCodes.Status404NotFound,
                MarketError.NoMatchingItems   => StatusCodes.Status409Conflict,
                MarketError.InvalidMaterial   => StatusCodes.Status400BadRequest,
                MarketError.BuyOrdersDisabled => StatusCodes.Status503ServiceUnavailable,
                MarketError.OrderBusy         => StatusCodes.Status409Conflict,

                // A bad request, not a conflict: the material named has no Hammer and never will, so
                // there is no server state the caller could wait for or clear. 400 like invalid_material.
                MarketError.NoHammerForMaterial => StatusCodes.Status400BadRequest,

                // A malformed slug, no link row, and a missing/deleted character all answer the same
                // way, deliberately: a slug must not be probeable for WHY it resolves to nothing.
                MarketError.SheetNotFound     => StatusCodes.Status404NotFound,

                MarketError.SheetsDisabled    => StatusCodes.Status503ServiceUnavailable,

                // Transient like Timeout/VaultUnavailable: an online build not ticking, a refused or
                // timed-out offline load, or the offline build cap being full all clear on their own.
                MarketError.SheetBusy         => StatusCodes.Status503ServiceUnavailable,

                // The account is not Admin, no longer exists, or is banned - a refusal, not a fault.
                MarketError.NotAdmin          => StatusCodes.Status403Forbidden,

                // admin_web_enabled is off. Only ever answered to an Admin (checked after authorization).
                MarketError.AdminDisabled     => StatusCodes.Status503ServiceUnavailable,

                // An admin typo naming an unknown/malformed key is a client error, not a fault.
                MarketError.SettingNotFound   => StatusCodes.Status404NotFound,

                // The key's metadata row is sensitive, or has no row at all - refused before any value is read.
                MarketError.SettingSensitive  => StatusCodes.Status403Forbidden,

                MarketError.InvalidSettingValue => StatusCodes.Status400BadRequest,

                // expected_current is stale - a state conflict the caller resolves by re-reading GET.
                MarketError.SettingChanged    => StatusCodes.Status409Conflict,

                // The text is missing, empty after normalization, or too long - a client error, not a fault.
                MarketError.InvalidAnnouncement => StatusCodes.Status400BadRequest,

                // admin_web_commands_enabled is off. Only ever answered to an Admin, after admin_web_enabled.
                MarketError.CommandsDisabled  => StatusCodes.Status503ServiceUnavailable,

                // POST /v1/admin/commands/run refusals (PLAN-P4.md section 2, P4b). Each is answered only
                // before the handler could run; once it may have started the answer is 200 with a result.
                MarketError.InvalidCommandText => StatusCodes.Status400BadRequest,
                MarketError.UnknownCommand    => StatusCodes.Status404NotFound,
                MarketError.CommandNotPermitted => StatusCodes.Status403Forbidden,
                MarketError.CommandInGameOnly => StatusCodes.Status409Conflict,
                MarketError.CommandBusy       => StatusCodes.Status409Conflict,
                MarketError.NoCharacterOnline => StatusCodes.Status409Conflict,

                // Guaranteed not run (atomic start state), and transient - unlike result timeout, which
                // means started and is a 200 precisely so it never reads as retryable.
                MarketError.CommandNotStarted => StatusCodes.Status503ServiceUnavailable,

                // World Event routes (WORLD-EVENTS-START.md). Disabled is a server state like
                // admin_disabled; the three run-state conflicts are 409s the page resolves by re-reading
                // status; the rest are refusals of the request itself.
                MarketError.WorldEventsDisabled   => StatusCodes.Status503ServiceUnavailable,
                MarketError.WorldEventRunning     => StatusCodes.Status409Conflict,
                MarketError.WorldEventNotRunning  => StatusCodes.Status409Conflict,
                MarketError.WorldEventRunChanged  => StatusCodes.Status409Conflict,
                MarketError.InvalidLocation       => StatusCodes.Status400BadRequest,
                MarketError.SourceNotWebStartable => StatusCodes.Status400BadRequest,
                MarketError.WorldEventRefused     => StatusCodes.Status400BadRequest,

                // Suit builder routes. vault_loading is transient (retry); suit_builder_disabled is a
                // server state like sheets_disabled.
                MarketError.VaultLoading          => StatusCodes.Status503ServiceUnavailable,
                MarketError.SuitBuilderDisabled   => StatusCodes.Status503ServiceUnavailable,

                // Suit transfers. State conflicts the player resolves in game (log in, walk to the vault
                // area, make room, wait out the running transfer) are 409; a malformed or oversized
                // request is 400; an unknown or foreign transfer id is 404.
                MarketError.CharacterOffline      => StatusCodes.Status409Conflict,
                MarketError.NotInVaultArea        => StatusCodes.Status409Conflict,
                MarketError.PackFull              => StatusCodes.Status409Conflict,
                MarketError.TransferInProgress    => StatusCodes.Status409Conflict,
                MarketError.TooManyItems          => StatusCodes.Status400BadRequest,
                MarketError.CharacterBusy         => StatusCodes.Status409Conflict,
                MarketError.VaultPanelOpen        => StatusCodes.Status409Conflict,
                MarketError.InvalidTransfer       => StatusCodes.Status400BadRequest,
                MarketError.TransferNotFound      => StatusCodes.Status404NotFound,
                MarketError.InPvp                 => StatusCodes.Status409Conflict,

                _                            => StatusCodes.Status500InternalServerError,
            };
        }

        // ---- wire shapes ----

        private static object Dto(MarketListing l) => new
        {
            id = l.Id,
            seller_character_guid = l.SellerCharacterGuid,
            seller_character_name = l.SellerCharacterName,
            item_guid = l.ItemGuid,

            // A class listing's line id (32 lowercase hex); null for every other listing.
            class_key = l.ClassKey,
            wcid = l.Wcid,
            count = l.Count,
            price_mmd = l.PriceMmd,
            status = l.Status,
            created_at = l.CreatedAt,
            closed_at = l.ClosedAt,
            seq = l.Seq,
            snapshot = l.Snapshot,
        };

        private static object Dto(MarketTransaction t) => new
        {
            id = t.Id,
            listing_id = t.ListingId,
            buy_order_id = t.BuyOrderId,
            buyer_character_guid = t.BuyerCharacterGuid,
            buyer_character_name = t.BuyerCharacterName,
            seller_character_guid = t.SellerCharacterGuid,
            seller_character_name = t.SellerCharacterName,
            wcid = t.Wcid,
            item_name = t.ItemName,
            count = t.Count,
            price_mmd_total = t.PriceMmdTotal,
            timestamp = t.Timestamp,
            // Enums, not ToString: the converter's snake_case is the published spelling (in_game).
            channel = t.Channel,
            status = t.Status,
            seq = t.Seq,
        };

        private static object Dto(MarketBuyOrder o) => new
        {
            id = o.Id,
            buyer_character_guid = o.BuyerCharacterGuid,
            buyer_character_name = o.BuyerCharacterName,
            material_type = o.MaterialType, material_name = o.MaterialName,
            kind = o.Kind,       // enum: snake_case string on the wire, "salvage_bag" or "salvage_hammer"

            // For a hammer order these three describe the HAMMER, not the bag. bag_name keeps its
            // published name rather than gaining a sibling, so an existing client renders the right
            // thing without knowing kinds exist.
            wcid = o.Wcid, bag_name = o.BagName,
            price_mmd = o.PriceMmd, count_total = o.CountTotal, count_remaining = o.CountRemaining,
            status = o.Status,   // enum: snake_case string on the wire
            created_at = o.CreatedAt, expires_at = o.ExpiresAt, closed_at = o.ClosedAt,
            icon_id = o.IconId, icon_overlay_id = o.IconOverlayId, icon_underlay_id = o.IconUnderlayId,
            seq = o.Seq,
        };

        private sealed class SessionRequest
        {
            public string AccountName { get; set; }
            public string Password { get; set; }
        }

        // No character-name member on either: the name comes from the account's own roster, so a
        // posted one is accepted by the parser and ignored.
        private sealed class CreateListingRequest
        {
            public uint? ItemGuid { get; set; }
            public uint Wcid { get; set; }
            public int Count { get; set; }

            /// <summary>
            /// A counted CLASS line's id (VaultEntry.ClassDisplayId, from the vault view's class_key).
            /// When present item_guid must be absent and wcid may be omitted; count is items. Absent
            /// (null) is the pre-class request, unchanged.
            /// </summary>
            public string ClassKey { get; set; }

            // NULLABLE on purpose. A non-nullable long makes an ABSENT price and a chosen price of
            // zero the same value, and zero now means "give it away" - so a client that omits the
            // field would give an item away rather than get a 400. The handler refuses null.
            public long? PriceMmd { get; set; }

            public uint SellerCharacterGuid { get; set; }
        }

        /// <summary>
        /// POST /v1/accounts/me/vault/barrel. Item identity is the same shape as everywhere else:
        /// item_guid for a stored biota, or a null item_guid plus wcid for a ledger stack.
        ///
        /// count and character_guid are NULLABLE for the same reason CreateListingRequest.PriceMmd is,
        /// on the destructive side of the same problem. An absent count defaulting to 1 would destroy
        /// something the client never asked to destroy, and an absent character guid defaulting to 0
        /// would write an audit row for a destruction that names nobody. Both are refused.
        /// </summary>
        private sealed class BarrelRequest
        {
            public uint? ItemGuid { get; set; }
            public uint Wcid { get; set; }

            /// <summary>A counted CLASS line's id, as on CreateListingRequest. item_guid absent and wcid 0 when present.</summary>
            public string ClassKey { get; set; }
            public int? Count { get; set; }
            public uint? CharacterGuid { get; set; }
        }

        private sealed class BuyRequest
        {
            public uint BuyerCharacterGuid { get; set; }
            public int Count { get; set; }

            // NULLABLE for the same reason, on the other side of the same problem: the staleness
            // check is an exact comparison against the live price, so an absent field defaulting to
            // zero would silently MATCH a free listing and buy it without the buyer agreeing to
            // anything.
            public long? ExpectedPriceMmd { get; set; }
        }

        /// <summary>POST /v1/orders. Every field NULLABLE and refused when absent: an absent count or price must never become a number.</summary>
        private sealed class PlaceOrderRequest
        {
            public int? MaterialType { get; set; }

            /// <summary>
            /// "salvage_bag" or "salvage_hammer". A STRING, not the enum, for two reasons: it matches
            /// the spelling this same value goes back out on in a BuyOrder, and System.Text.Json throws
            /// on an unrecognised enum name - which ReadJson turns into a null body and therefore a 500,
            /// where a client's typo deserves a 400. <see cref="TryParseOrderKind"/> does the mapping.
            ///
            /// This is the ONE field on this request that is allowed to default when absent, which is a
            /// deliberate exception to the rule the other four follow. It defaults to salvage_bag, the
            /// only thing an order could be before this field existed, so a client written against the
            /// previous contract keeps posting exactly the orders it always did. A defaulted count or
            /// price would invent a number nobody chose; a defaulted kind reproduces the old behaviour.
            /// </summary>
            public string Kind { get; set; }

            public int? Count { get; set; }
            public long? PriceMmd { get; set; }
            public uint? BuyerCharacterGuid { get; set; }
        }

        /// <summary>
        /// Maps the wire spelling of a kind onto <see cref="MarketBuyOrderKind"/>. Absent or empty is
        /// SalvageBag (see PlaceOrderRequest.Kind); anything else the enum does not name is REFUSED
        /// rather than coerced, because coercing an unknown kind to bag would spend the buyer's escrow
        /// on something they did not ask for.
        /// </summary>
        internal static bool TryParseOrderKind(string wire, out MarketBuyOrderKind kind)
        {
            kind = MarketBuyOrderKind.SalvageBag;

            if (string.IsNullOrWhiteSpace(wire))
                return true;

            switch (wire.Trim().ToLowerInvariant())
            {
                case "salvage_bag":    kind = MarketBuyOrderKind.SalvageBag;    return true;
                case "salvage_hammer": kind = MarketBuyOrderKind.SalvageHammer; return true;
                default:                                                        return false;
            }
        }

        /// <summary>POST /v1/orders/{id}/fill. Same rule.</summary>
        private sealed class FillOrderRequest
        {
            public int? Count { get; set; }
            public uint? SellerCharacterGuid { get; set; }
        }

        /// <summary>POST /v1/accounts/me/characters/{guid}/sheet-link. Absent or false rotate enables without changing an existing slug; true always mints a fresh one.</summary>
        public sealed class SheetLinkRequest { public bool? Rotate { get; set; } }
    }
}
