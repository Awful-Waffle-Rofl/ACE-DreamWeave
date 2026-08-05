using System.Text;

using ACE.Dashboard;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var cfg = app.Configuration;
var connStr = cfg["Analytics:ConnectionString"] ?? "";
var user = cfg["Dashboard:Username"] ?? "";
var pass = cfg["Dashboard:Password"] ?? "";
var queries = new AnalyticsQueries(connStr);

// HTTP Basic auth from config. Runs behind a reverse proxy + TLS + firewall in production
// (see README) — Basic auth over plain HTTP is only safe on a trusted/loopback network.
app.Use(async (ctx, next) =>
{
    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Dashboard auth not configured. Set Dashboard:Username and Dashboard:Password.");
        return;
    }

    string? header = ctx.Request.Headers.Authorization;
    if (header is not null && header.StartsWith("Basic ", StringComparison.Ordinal))
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6)));
            var sep = raw.IndexOf(':');
            if (sep > 0 && raw[..sep] == user && raw[(sep + 1)..] == pass)
            {
                await next();
                return;
            }
        }
        catch { /* malformed header -> challenge below */ }
    }

    ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"ACE Dashboard\"";
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
});

IResult SafeJson(Func<object> get)
{
    try { return Results.Json(get()); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }
}

app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html; charset=utf-8"));

app.MapGet("/api/live", () => SafeJson(() => new { roster = queries.GetRoster(), blocks = queries.GetBlocks() }));
// minEarnMinutes floors the rate denominator; see GetRateLeaderboard for why it is not optional.
app.MapGet("/api/leaderboard/xp", (int? hours, int? limit, int? minEarnMinutes) => SafeJson(() => queries.GetRateLeaderboard(true, hours ?? 1, limit ?? 10, (minEarnMinutes ?? 10) * 60)));
app.MapGet("/api/leaderboard/lum", (int? hours, int? limit, int? minEarnMinutes) => SafeJson(() => queries.GetRateLeaderboard(false, hours ?? 1, limit ?? 10, (minEarnMinutes ?? 10) * 60)));
app.MapGet("/api/flows/items", (int? hours, int? limit) => SafeJson(() => queries.GetTopItemEdges(hours ?? 24, limit ?? 20)));
app.MapGet("/api/flows/bank", (int? limit) => SafeJson(() => queries.GetBankTransfers(limit ?? 30)));

app.Run();
