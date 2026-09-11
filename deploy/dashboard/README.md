# Deploy ACE.Dashboard (game host)

Runs the analytics dashboard as a container on the game host, reading `ace_analytics` through a
**read-only** MySQL user. The image is built + pushed to GHCR by
`.github/workflows/publish-dashboard.yml` (on pushes touching `Source/ACE.Dashboard/**`, or via
`workflow_dispatch`).

**Prereq:** the server must have analytics enabled and populating `ace_analytics` (see
Docs/Monitoring/DESIGN.md / the stage-enable steps).

## 1. Create the read-only DB user  — full packaged command

Run on the game host. `read -s` keeps the password out of your shell history; `mysql -uroot -p`
prompts for the root password so it stays out of the logs too. Use the **same** password you'll put
in `.env` as `DASHBOARD_DB_PASSWORD`.

```bash
read -s -p "New dashboard_ro password: " DASH_DB_PW; echo

docker exec -i ace-stage-db mysql -uroot -p <<SQL
CREATE USER IF NOT EXISTS 'dashboard_ro'@'%' IDENTIFIED BY '${DASH_DB_PW}';
ALTER USER 'dashboard_ro'@'%' IDENTIFIED BY '${DASH_DB_PW}';
GRANT SELECT ON ace_analytics.* TO 'dashboard_ro'@'%';
REVOKE ALL PRIVILEGES ON *.* FROM 'dashboard_ro'@'%';
GRANT SELECT ON ace_analytics.* TO 'dashboard_ro'@'%';
FLUSH PRIVILEGES;
SQL

unset DASH_DB_PW
```

(The REVOKE-then-GRANT pair guarantees `dashboard_ro` has **only** `SELECT` on `ace_analytics`,
nothing else, even if the user already existed with wider grants.)

Verify it's SELECT-only:
```bash
docker exec -i ace-stage-db mysql -uroot -p -e "SHOW GRANTS FOR 'dashboard_ro'@'%';"
```

## 2. Deploy

Copy this directory (`deploy/dashboard`) to the game host, then:

```bash
cp .env.template .env
$EDITOR .env
#   DASHBOARD_PASSWORD     -> Basic-auth password for the UI (empty => app stays locked at 503)
#   DASHBOARD_DB_PASSWORD  -> the dashboard_ro password from step 1
#   DASHBOARD_IMAGE        -> leave :latest, or pin :sha-xxxxxxxxxxxx

docker network ls | grep stage            # confirm dreamweave-stage_default (else edit compose)
docker compose pull
docker compose up -d
```

## 3. Access + firewall

The container is bound to **loopback** (`127.0.0.1:5080`) — nothing is exposed publicly. Reach it via
an SSH tunnel from your workstation:

```bash
ssh -L 5080:127.0.0.1:5080 root@<GAME_HOST>
# then browse http://localhost:5080  (log in: admin / DASHBOARD_PASSWORD)
```

Or, to serve it to admins without a tunnel, front it with your reverse proxy + TLS and restrict the
public port with a firewall to your admin IP — the same discipline as Grafana. Never expose port
5080/8080 unauthenticated on a public interface.

## 4. Verify

```bash
# on the game host:
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5080/                 # 401 (no creds)
curl -s -o /dev/null -w '%{http_code}\n' -u admin:$PW http://127.0.0.1:5080/     # 200 (page)
curl -s -u admin:$PW 'http://127.0.0.1:5080/api/live' | head -c 300              # JSON: roster + blocks
```

Then open the tunnelled URL — you should see players online, the xp/hr & lum/hr leaderboards, the
item-flow board (one-way flows flagged), and recent bank transfers.

## Updating

Push a change under `Source/ACE.Dashboard/**` and `publish-dashboard.yml` publishes a new
`:latest` (and a `:sha-…`). **Publishing is not deploying** - nothing pulls on its own.

**Run the `deploy dashboard` workflow** (Actions ▸ deploy dashboard ▸ Run workflow). It runs on
the self-hosted runner, pulls, recreates the container, and then checks that the running container
is the image it just pulled and that the app answers on `127.0.0.1:5080`. Leave the `image` input
blank for `:latest`, or pass a `:sha-…` ref to deploy or roll back to an explicit build.

**Do not reach for `docker compose pull` on the host.** It fails: the host holds no GHCR
credentials, the package is private, and an anonymous pull is refused (verified 2026-08-23 -
`docker login` there would need a classic PAT minted and rotated by hand, and a fine-grained token
is not accepted by the container registry). The workflow needs none of that, because the runner
authenticates with its own `GITHUB_TOKEN` exactly as the server deploy does. `docker/login-action`
logs out at the end of each job, which is why `/root/.docker/config.json` reads `{"auths":{}}`
between runs - that is expected, not a broken login.

This gap was real and cost five weeks: the container ran a 2026-07-17 image until 2026-08-23 while
three successful publishes sat unused in GHCR, because the documented route was a host command that
could not work.

Pin `DASHBOARD_IMAGE` in `.env` to a `:sha-…` tag if you want the deployed build to be explicit
rather than tracking `:latest`.
