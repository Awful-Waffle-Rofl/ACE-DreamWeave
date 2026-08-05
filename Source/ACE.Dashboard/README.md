# ACE.Dashboard — in-game analytics UI

The read-side of the monitoring initiative's analytics half (Docs/Monitoring/DESIGN.md §7). A small
ASP.NET Core app that reads the `ace_analytics` database (written by the game server's
`AnalyticsManager`) and serves a single-page dashboard: live roster + per-block population, top
xp/hr & lum/hr leaderboards, directed item-flow edges (with one-directional "mule" flags), and
recent bank transfers.

Standalone (no reference to the game assemblies) — item names etc. are stored denormalized in
`ace_analytics`, so the dashboard only needs a read-only MySQL connection.

## Configure

`appsettings.json` (or environment variables / user-secrets):

- `Analytics:ConnectionString` — points at `ace_analytics`. Use a **read-only** MySQL user:
  ```sql
  CREATE USER 'dashboard_ro'@'%' IDENTIFIED BY '<pw>';
  GRANT SELECT ON ace_analytics.* TO 'dashboard_ro'@'%';
  ```
- `Dashboard:Username` / `Dashboard:Password` — HTTP Basic auth. **Empty password ⇒ the dashboard
  refuses all requests (503)**, so it's safe by default until you set one.
- `Kestrel:Endpoints:Http:Url` — bind address. Defaults to `http://127.0.0.1:5080` (loopback).

## Run

```bash
dotnet run -c Release            # from Source/ACE.Dashboard
# or publish and run the DLL on the game host
```

Prereq: the game server must have `Server.EnableAnalytics = true` with a working `MySql.Analytics`
connection, so `ace_analytics` exists and is being populated.

## Security

Basic auth over plain HTTP is only acceptable on a trusted/loopback network. In production:
- keep it bound to loopback and **front it with the same reverse proxy + TLS** as Grafana, or
- restrict its port with a firewall to your admin IP.

Never expose it unauthenticated on a public interface. Use the read-only DB user above so a
compromise can't write game data.

## Endpoints

`GET /` (HTML) · `GET /api/live` · `GET /api/leaderboard/xp?hours=&limit=&minEarnMinutes=` ·
`GET /api/leaderboard/lum?...` · `GET /api/flows/items?hours=&limit=` · `GET /api/flows/bank?limit=`

### Reading the rate leaderboards

Both are ordered by **rate**, not by total. `xp/hr` means XP per hour of *earning* time, not per
hour online: a character only gets a `char_rate_interval` row for a 60s flush in which it
actually gained something, so the rate already normalises away idle and offline time. That makes
it an active-farming rate, which is the right denominator for spotting an exploit and the wrong
one for "who played the most" - use the **Earning hrs** column for that.

`minEarnMinutes` (default 10) drops characters below that much accumulated earning time. It is
not optional in practice: one large one-off grant divided by a single 60s interval reads as an
astronomical hourly rate and takes the top slot. **On sparse data the floor can empty a board** -
Luminance especially, since far less content awards it. If a board reads "No data" while you
expect earners, set the header's *min earning* selector to `off` before suspecting a bug.

## Not yet wired

- Auth via `ace_auth` AccessLevel (currently config Basic auth).
- Last-login / offline-player views (would read `ace_shard`/`ace_auth`).
- Self-baseline & z-score anomaly scoring (the UI currently flags one-directional flow; rate
  outliers are surfaced by ranking — thresholds are a follow-up).
