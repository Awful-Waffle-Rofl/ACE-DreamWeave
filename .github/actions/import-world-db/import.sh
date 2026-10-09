#!/usr/bin/env bash
#
# import.sh - loads the Auth/Shard base schemas and imports the already-downloaded world database
# dump into ace_world, then replays Database/Updates/World/*.sql.
#
# This is the actual work behind action.yml's "Load base schema into MySQL" and "Import the real
# world database" steps, factored into a plain script so it can also be launched directly - and
# backgrounded - by build-test.yml, which cannot background a `uses:` composite step. action.yml
# calls this same script synchronously, so the two callers cannot drift apart. The
# cache/download-the-dump steps stay in action.yml (they need `actions/cache`, a JS/TS action that
# cannot run from inside a backgrounded shell process) and must run before this script - it expects
# the unzipped dump named by WORLD_DB_RELEASE_ASSET (minus .zip) to already exist in the working
# directory.
#
# Run from the repo root (relies on relative paths: Database/Base/*.sql, Database/Updates/World/*.sql).
#
# Env vars (all but WORLD_DB_RELEASE_ASSET have defaults matching build-test.yml's MySQL setup):
#   MYSQL_HOST                default 127.0.0.1
#   MYSQL_PORT                default 3306
#   MYSQL_USER                default root
#   MYSQL_PASSWORD            default '' (empty)
#   WORLD_DB_RELEASE_ASSET    required - the release asset filename (a .sql.zip); the unzipped
#                             file (same name minus .zip) must already be present.

set -euo pipefail

MYSQL_HOST="${MYSQL_HOST:-127.0.0.1}"
MYSQL_PORT="${MYSQL_PORT:-3306}"
MYSQL_USER="${MYSQL_USER:-root}"
MYSQL_PASSWORD="${MYSQL_PASSWORD:-}"
: "${WORLD_DB_RELEASE_ASSET:?WORLD_DB_RELEASE_ASSET must be set}"

mysql_cmd=(mysql -h "$MYSQL_HOST" -P "$MYSQL_PORT" -u "$MYSQL_USER")
[ -n "$MYSQL_PASSWORD" ] && mysql_cmd+=(-p"$MYSQL_PASSWORD")

# Database/Base/{Authentication,Shard}Base.sql are point-in-time dumps and are not always current
# (see Database/Updates/*/updates.txt), but they're enough for the Auth/Shard tables the gated
# tests touch.
"${mysql_cmd[@]}" < Database/Base/AuthenticationBase.sql
"${mysql_cmd[@]}" < Database/Base/ShardBase.sql

# DatabaseManager.Initialize() (Source/ACE.Database/DatabaseManager.cs:47-53) hard-gates on a real
# "human" weenie existing in ace_world before it will even assign DatabaseManager.Shard - a
# schema-only ace_world (Database/Base/WorldBase.sql has zero INSERT statements) makes it set
# InitializationFailure and bail before Shard is ever assigned, which is what made
# StartupTests.WorldManager_Initialize NRE downstream in PlayerManager.Initialize(). Import the
# same world database release README.md tells a real contributor to install. This supersedes
# WorldBase.sql entirely for ace_world - it's a full dump, not an addition on top of it.
"${mysql_cmd[@]}" -e "CREATE DATABASE IF NOT EXISTS ace_world"
"${mysql_cmd[@]}" ace_world < "${WORLD_DB_RELEASE_ASSET%.zip}"

# The fork's own structural additions (realm tables etc.) are not in the upstream dump, so they
# must be replayed AFTER it or the import wipes them (it's a full DROP+CREATE dump). Shard's own
# pending update (Database/Updates/Shard/2026-07-10-00-Add-Biota-Position-Instance.sql) is
# deliberately EXCLUDED here - do not "fix" this by adding it back. See action.yml's prior comment
# history (git blame) for why: it wraps its ALTER TABLE in a stored procedure body the mysql CLI
# cannot split on ';' without a DELIMITER change, and it is meant to run only through the server's
# own boot patcher. Nothing the MySQL-gated tests exercise needs that column.
for f in Database/Updates/World/*.sql; do [ -e "$f" ] && "${mysql_cmd[@]}" ace_world < "$f"; done
