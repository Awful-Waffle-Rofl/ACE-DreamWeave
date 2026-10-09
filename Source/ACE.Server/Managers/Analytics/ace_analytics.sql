-- ace_analytics schema (monitoring initiative, Docs/Monitoring/DESIGN.md §5).
-- Applied idempotently by AnalyticsDatabase at startup; safe to re-run.
-- This file is embedded in ACE.Server (see ACE.Server.csproj) and is the canonical DDL.

-- Live state, overwritten every flush (current online roster).
CREATE TABLE IF NOT EXISTS `live_roster` (
    `character_id` INT UNSIGNED  NOT NULL,
    `name`         VARCHAR(64)   NOT NULL,
    `level`        INT           NOT NULL,
    `landblock`    INT           NOT NULL,
    `updated_utc`  DATETIME      NOT NULL,
    PRIMARY KEY (`character_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Live per-landblock population (only blocks with players > 0), overwritten every flush.
CREATE TABLE IF NOT EXISTS `live_landblock_pop` (
    `landblock`   INT       NOT NULL,
    `players`     INT       NOT NULL,
    `updated_utc` DATETIME  NOT NULL,
    PRIMARY KEY (`landblock`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-1: append one row per earning character per flush interval. Rates are derived
-- (SUM(xp_gained)/SUM(secs)*3600 over a trailing window). Volume is bounded by online
-- count, not kill rate. `secs` is the ACTUAL elapsed since the last flush.
CREATE TABLE IF NOT EXISTS `char_rate_interval` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `character_id` INT UNSIGNED    NOT NULL,
    `name`         VARCHAR(64)     NOT NULL,
    `ts_utc`       DATETIME        NOT NULL,
    `secs`         DOUBLE          NOT NULL,
    `xp_gained`    BIGINT          NOT NULL,
    `lum_gained`   BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_char_ts` (`character_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-2: item transfers. One row per item moved, per direction. `value` is the item's value at
-- transfer time for value-weighted mule detection (aggregate into directed from->to edges over a
-- window) and is ALREADY the whole stack, since ACE keeps Value = StackUnitValue * StackSize -
-- never multiply it by `stack_size` again. Long-retention audit trail.
--
-- `kind` splits three different questions and they must not be mixed:
--   'trade' | 'give'     - player to player directly. This is the mule/RMT signal.
--   'buy'   | 'sell'     - player to vendor. Ordinary economy activity, NOT a transfer between
--                          players; including it in a flow total inflates the mule signal.
--   'mkt_sale'           - player to player via the market's escrow (see market_sale below).
--                          Goods DO change hands between players, so this belongs with the mule
--                          signal too, even though it is not 'trade'/'give'.
-- On a vendor row the vendor side carries the vendor's WeenieClassId rather than a guid, because
-- a vendor's guid is reallocated whenever its landblock reloads. That is safe to share with the
-- character-id columns: player guids start at ObjectGuid.PlayerMin (0x50000001) and every wcid is
-- far below it, so the two ranges cannot collide.
CREATE TABLE IF NOT EXISTS `item_flow_event` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `ts_utc`       DATETIME        NOT NULL,
    `kind`         VARCHAR(8)      NOT NULL,   -- 'trade' | 'give' | 'buy' | 'sell' | 'mkt_sale'
    `from_id`      INT UNSIGNED    NOT NULL,
    `from_name`    VARCHAR(64)     NOT NULL,
    `to_id`        INT UNSIGNED    NOT NULL,   -- 0 if unknown/NPC
    `to_name`      VARCHAR(64)     NOT NULL,
    `to_is_player` TINYINT(1)      NOT NULL,
    `item_wcid`    INT UNSIGNED    NOT NULL,
    `item_name`    VARCHAR(128)    NOT NULL,
    `stack_size`   INT             NOT NULL,
    `value`        BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_from` (`from_id`, `ts_utc`),
    KEY `ix_to` (`to_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-2: public chat. Owner ruling: all public channels plus local `say`; allegiance (and its
-- patron/vassal/monarch/co-vassal variants), fellowship, staff channels and direct messages are
-- NOT captured, so their absence here is policy, not a gap to be filled later.
--
-- `channel` is one of 'say', 'general', 'trade', 'lfg', 'roleplay', 'society', 'olthoi'. The
-- allowlist lives in exactly one place in the server (AnalyticsManager.ChatChannels) so the
-- exclusions cannot drift as channels are added.
--
-- `landblock` is where the speaker stood, and is only meaningful for 'say' (0 on global channels).
-- It is what makes "who else was standing there" answerable, which is the thing local say is
-- uniquely good for.
--
-- Only messages that were actually DELIVERED are recorded: both hooks sit inside the server's
-- own gag check, so a gagged player's attempts produce no rows.
--
-- This is the one analytics stream whose volume is NOT bounded by the online player count - it
-- scales with how much people talk - which is why it carries its own retention setting
-- (Server.AnalyticsChatRetentionDays) rather than sharing the rate tables'.
CREATE TABLE IF NOT EXISTS `chat_event` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `ts_utc`       DATETIME     NOT NULL,
    `character_id` INT UNSIGNED NOT NULL,
    `name`         VARCHAR(64)  NOT NULL,
    `channel`      VARCHAR(16)  NOT NULL,
    `landblock`    INT          NOT NULL,   -- speaker's landblock; 0 for global channels
    `message`      VARCHAR(512) NOT NULL,   -- truncated server-side, never rejected
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_char_ts` (`character_id`, `ts_utc`),
    KEY `ix_chan_ts` (`channel`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-2: currency movements. Long-retention audit trail. `kind` is 'bank_transfer' for a
-- character-to-character bank transfer, 'buy'/'sell' for the money leg of a vendor
-- transaction - the price actually paid, which is NOT the sum of the matching item rows' `value`,
-- because vendor markup and markdown sit between the two - or 'mkt_sale' for the MMD leg of a
-- market sale (RecordMarketSale also writes a matching item_flow_event row with the item leg).
-- Vendor rows carry the vendor's WeenieClassId in the counterparty column, as in item_flow_event
-- above. `currency` is 'Pyreals' or `wcid:<n>` for an alternate currency, so summing across
-- currencies is only meaningful once filtered to one.
CREATE TABLE IF NOT EXISTS `currency_flow_event` (
    `id`        BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `ts_utc`    DATETIME        NOT NULL,
    `kind`      VARCHAR(16)     NOT NULL,   -- 'bank_transfer' | 'buy' | 'sell' | 'mkt_sale'
    `from_id`   INT UNSIGNED    NOT NULL,
    `from_name` VARCHAR(64)     NOT NULL,
    `to_id`     INT UNSIGNED    NOT NULL,
    `to_name`   VARCHAR(64)     NOT NULL,
    `currency`  VARCHAR(24)     NOT NULL,
    `amount`    BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_from` (`from_id`, `ts_utc`),
    KEY `ix_to` (`to_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------------------------
-- Thread run telemetry. One parent row per FINISHED run, written from
-- ThreadDungeonManager.EndRun (the single exit path), plus two child tables keyed on the
-- parent's surrogate `id`. Parent and children are inserted in ONE transaction.
--
-- *** THIS FILE CANNOT ADD A COLUMN TO A TABLE THAT ALREADY EXISTS. READ THIS BEFORE EDITING. ***
--
-- Every statement here is CREATE TABLE IF NOT EXISTS, and AnalyticsDatabase contains no ALTER, no
-- information_schema query and no migration path of any kind - Initialize() runs this file and
-- nothing else. On a database that already has one of these tables, the CREATE is skipped WHOLE:
-- a column added below will silently never appear there, and the INSERT naming it will then fail
-- on every write, taking the whole batch with it.
--
-- Analytics is already live, so the first deploy of these three tables freezes their shape.
-- Adding a column afterwards is NOT a code change - it is hand-written ALTER TABLE DDL run
-- against every live analytics database, coordinated with the deploy that starts writing it.
--
-- That constraint is the whole justification for the RESERVED columns below (`start_attempts`,
-- `credited_kills`, `credited`). They look like dead weight and they are not: they are columns a
-- known, in-flight design will need, added now while adding them is free. Removing one to tidy up
-- costs a manual migration to put it back. Leave them, and add a column speculatively rather than
-- reluctantly.
--
-- RETENTION: these three tables are NEVER pruned, deliberately. Volume is bounded by how many
-- dungeon runs actually finish, which is orders of magnitude below the chat and item streams, and
-- the whole point of the data is season-over-season tuning comparison. There is no prune method
-- for them and no table-agnostic sweep in AnalyticsDatabase - PruneChat and PruneRates each name
-- their one table in their own DELETE.
-- ---------------------------------------------------------------------------------------------

-- One finished run.
--
-- `run_id` is the EPHEMERAL instance id. LandblockManager recycles instance ids, and every id is
-- reissued from scratch after a server restart, so run_id is NOT unique over time and must never
-- be used to join anything. `id` is the surrogate key that the two child tables reference; group
-- and join on `id` only.
--
-- `start_group` is a random 32-hex-character id minted once per gem USE and carried across every
-- attempt that use spawns. It is what separates WHAT THE PLAYER DID from WHAT THE SERVER TRIED:
--
--     COUNT(DISTINCT start_group)  - gem uses, i.e. dungeons players actually asked for
--     COUNT(*)                     - runs the server opened to satisfy them
--     COUNT(*) / COUNT(DISTINCT start_group) - the placement fault rate
--
-- It exists because a run that cannot be populated winnably is aborted and retried, and each
-- attempt goes back through TryStart, which allocates a fresh instance id and registers a NEW run.
-- One gem use that succeeds on its third attempt therefore writes THREE rows here. Filtering on
-- `entered` fixes the averages and does NOT fix the count: three rows still read as three starts,
-- and that inflation is exactly proportional to the fault rate this data exists to measure, so
-- the worse placement gets the more it looks like traffic. Nothing else ties the rows together,
-- which is why the key is minted at the use rather than reconstructed afterwards.
--
-- A GUID rather than a counter, deliberately: run_id recycles, so any key derived from it would
-- carry a time-window caveat and a query spanning a restart would silently fuse unrelated starts.
-- It is NOT the gem guid either - one gem carries several entries and reopens runs on separate
-- occasions, so that would fuse starts weeks apart.
--
-- Today nothing retries, so every run mints its own group and the two counts are equal. They
-- diverge only once the retry path lands, and by exactly the fault rate.
--
-- The aborted attempts in a group are NON-RUNS by construction: the player is never teleported in,
-- so they carry no entry, no kills and no meaningful duration, and they must be excluded from
-- every gameplay average (`entered = 1` does that). They are kept rather than suppressed because
-- their dungeon_run_placement rows are the ONLY surviving evidence of a fault the player never
-- felt - the retry hid it - and discarding them would make the feature look flawless precisely
-- when it is working hardest.
--
-- `end_state` is a closed set of exactly SEVEN strings, derived in ONE place
-- (DungeonRunTelemetry.EndStateFor) from the run's terminal state plus its end reason:
--   'cleared'                 - the run reached Cleared before it ended, however it then ended.
--   'expired'                 - the run hit its TTL.
--   'abandoned'               - the copy went away underneath it (landblock unloaded, or never
--                               loaded).
--   'aborted'                 - somebody ended it deliberately (the /dd admin command).
--   'aborted_unwinnable'      - RESERVED. A boss was planned and never placed, so the run is
--                               mathematically unwinnable. CONTENT or DATA fault.
--   'aborted_underpopulated'  - RESERVED. The trash that placed fell below the minimum population
--                               ratio. Winnable but threadbare. CURATION fault, typically a spawn
--                               file that has drifted.
--   'aborted_timeout'         - RESERVED. Population never completed inside the start timeout and
--                               the pending run was ended. Neither unwinnable nor thin, just
--                               stuck. SERVER or LANDBLOCK fault.
--
-- The last three all belong to the in-flight retry branch and none of them has a writer yet, so a
-- panel must tolerate three zero counts rather than reading them as a gap. They are three states
-- and not one because the three causes have three different OWNERS, as marked above; collapsing
-- them would destroy the distinction the data is collected to draw, and no later query could undo
-- the collapse.
--
-- Note that 'aborted_timeout' is NOT the same event as the 'landblock failed to load' reap, which
-- maps to 'abandoned'. That one ends a registered run whose landblock never finished
-- CreateWorldObjects, with the player already waiting on it; this one is the retry loop giving up
-- on POPULATION before the player is teleported in at all. Do not fuse them.
-- `end_reason` is the raw free-text reason EndRun logged, kept beside the derived state so a new
-- reason string is visible in the data before anyone teaches the mapping about it.
--
-- `entered` is ThreadDungeonRun.PlayerEverObserved: did a player ever actually materialise inside
-- the copy. A run with entered = 0 never happened from the player's point of view, whatever else
-- its counters say, so most gameplay panels want `entered = 1`.
--
-- `char_level_start` is the owner's level at the moment the run OPENED, captured once in
-- ThreadDungeonRun's constructor. This is the one that answers "was this gem level appropriate
-- for this player". `char_level` is the owner's level resolved at END time
-- (PlayerManager.FindByGuid, which works offline); 0 in either means the level was not available.
--
-- Both are recorded because the end value alone is biased, and not evenly: a player who levels
-- mid-run is recorded at their post-run level, and a clear is what banks the large reward, so the
-- error concentrates in exactly the cleared runs the analysis cares most about. The pair is also
-- the only way to see that a run levelled its owner at all.
--
-- `populate_reached` is a one-shot latch, never a count: did the run get as far as building a
-- plan and enqueueing a placement chain. It is the only thing separating the two very different
-- runs that both report spawned = 0 - a copy that never finished loading, which is a server
-- fault, and a plan that ran and placed nothing, which is a content or curation gap.
--
-- `populate_ms` is how long population took, from the start of the populate chain to
-- MarkPopulated, measured on a MONOTONIC clock (the wall clock can step). NULL - never a sentinel
-- - when population did not complete: a run that never populated has no duration, and NULL is
-- what keeps it out of AVG and percentile queries instead of dragging them toward zero, which is
-- the same class of error as counting never-entered runs in a gameplay average. It pairs with
-- populate_reached: the latch says whether population finished, this says how long it took.
--
-- It is a stored column rather than a derived one because for a SUCCESSFUL run it is not
-- derivable at all. For an aborted run, ended_utc - started_utc approximates it, since the run's
-- whole life was the populate; for a successful one the run continues for as long as the player
-- plays and the populate is buried inside a lifetime that is mostly gameplay. Successful runs are
-- exactly where this matters: a populate creeping from 2s toward a 30s abort cap shows up here
-- first, while the abort count is still zero. That is the difference between a LEADING and a
-- LAGGING indicator, and it is why this column is not redundant with the two timestamps.
--
-- `seed` is the gem's rng seed, and it is the REPRODUCTION KEY: seed plus gem_level reconstructs
-- the exact dungeon choice and the exact population plan on a dev box, which turns a placement
-- failure from an anecdote into something a developer can step through. Nothing else in this
-- schema stands in for it. It matters most for a run that was retried, because the retry path
-- RE-SEEDS the gem on each attempt - so the seed that produced a failing draw is destroyed by the
-- act of retrying, and this column is the only place it survives.
--
-- `start_attempts` is RESERVED and has NO writer: it is always 0. It belongs to a proposed but
-- unlanded design in which a start silently rerolls the gem a few times before the player is told
-- anything, and it is that reroll count. Do NOT repurpose the name for anything else - another
-- session owns that work, and once two meanings have both been written the counts are not
-- separable afterwards. See the migration note at the top of this section for why an unused
-- column is cheaper than a later one.
--
-- `planned` / `spawned` / `killed` are the run's own counters: plan entries, creatures that
-- actually entered the world, and deaths counted against the run. planned - spawned is the
-- placement shortfall that dungeon_run_placement explains.
--
-- `clear_target` is a CONVENIENCE column only. Its meaning is expected to change - a separate
-- branch redefines ClearTarget to mean trash kills assuming the boss is dead - so panels must
-- derive progress from the raw counts above and treat this column as a historical footnote.
--
-- `boss_wcid_intended` is the wcid the plan chose; `boss_wcid_placed` is the wcid that actually
-- entered the world. There are THREE outcomes, not two, and the wcid pair ALONE CANNOT SEPARATE
-- THE FIRST TWO - bucketing on `boss_wcid_placed = 0` reports a genuinely bossless dungeon as a
-- boss that failed to place, which is a mistake a panel has already made:
--
--   1. Bossless dungeon. The dungeon has no boss anchor, so no boss was ever asked for. Both wcid
--      columns are 0 and there is NO dungeon_run_placement row.
--   2. No candidate. A boss anchor exists, but neither the curated bosses.json window nor the
--      run's own family could field one, so the plan never chose a wcid. Both wcid columns are
--      ALSO 0, and there IS a dungeon_run_placement row, reason 'no_candidate', wcid 0.
--   3. Placement failure. The plan chose a boss and it never reached the world.
--      boss_wcid_intended is set, boss_wcid_placed is 0, and there is a dungeon_run_placement row
--      carrying a terminal reason and the boss's own wcid.
--
-- So the PLACEMENT ROW, not the wcid pair, is what tells case 1 from case 2. Only case 3 is
-- visible in the wcid columns alone. A correct "boss did not make it" filter is therefore
-- boss_wcid_placed = 0 AND EXISTS (a dungeon_run_placement row for this run with role = 'Boss').
--
-- `boss_health_ratio` / `boss_health_clamped` are a DIFFICULTY-TUNING signal and NOT a placement
-- signal. They come from the two boss-health warnings the spawner emits INSIDE the isBoss branch,
-- which run BEFORE EnterWorld: a boss that lands under its health target has still placed
-- perfectly well. They are deliberately never written into dungeon_run_placement, and a placement
-- panel that counted them would report failures that did not happen. ratio is achieved boss
-- Health.MaxValue over the largest non-boss Health.MaxValue in the same run (0.0 when there was no
-- pack to measure against, or when no boss was placed).
--
-- `survey_filed` is latched at the point RecordSurvey ACTUALLY files the daily survey, not at the
-- point it decides to try, so a run whose owner logged out between the clear and the file reads 0.
--
-- `credited_kills` is RESERVED and has NO writer: it is always 0. It was to hold unplaceable plan
-- entries credited as already-killed, under a proposed owner ruling that has since been
-- superseded. The column is kept so the shape does not have to change if that ever returns. Do
-- not invent a writer for it.
--
-- `xp_gained` / `lum_gained` are the XP and luminance the owner banked WHILE STANDING INSIDE this
-- run's instance, attributed in AnalyticsManager.RecordXp / RecordLuminance by resolving
-- player.Location.Instance back to the run. They therefore include everything earned in there, not
-- only run creature kills.
CREATE TABLE IF NOT EXISTS `dungeon_run` (
    `id`                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `run_id`              INT UNSIGNED NOT NULL,   -- ephemeral instance id; RECYCLES, never join on it
    `start_group`         CHAR(32) NOT NULL,       -- one gem USE; COUNT(DISTINCT) is players, COUNT(*) is attempts
    `started_utc`         DATETIME NOT NULL,
    `ended_utc`           DATETIME NOT NULL,
    `duration_secs`       DOUBLE NOT NULL,
    `end_state`           VARCHAR(24) NOT NULL,    -- closed set of five, see above
    `end_reason`          VARCHAR(64) NOT NULL,    -- raw reason text EndRun logged
    `entered`             TINYINT(1) NOT NULL,     -- did a player ever materialise inside
    `character_id`        INT UNSIGNED NOT NULL,
    `name`                VARCHAR(64) NOT NULL,
    `char_level_start`    INT NOT NULL,            -- owner's level when the run OPENED; the unbiased one
    `char_level`          INT NOT NULL,            -- owner's level at end time
    `dungeon_id`          VARCHAR(16) NOT NULL,
    `gem_level`           INT NOT NULL,
    `tier`                INT NOT NULL,
    `family`              VARCHAR(32) NOT NULL,
    `instability`         INT NOT NULL,
    `presses`             INT NOT NULL,
    `seed`                INT NOT NULL,            -- reproduction key; pair with gem_level. Retrying DESTROYS it
    `start_attempts`      INT NOT NULL,            -- RESERVED for the gem-reroll design, no writer, always 0
    `populate_reached`    TINYINT(1) NOT NULL,     -- one-shot latch: did a plan ever get built and enqueued
    `populate_ms`         INT NULL,                -- NULL when population never completed; never a sentinel
    `planned`             INT NOT NULL,
    `spawned`             INT NOT NULL,
    `killed`              INT NOT NULL,
    `clear_target`        INT NOT NULL,            -- convenience only; meaning is expected to change
    `boss_wcid_intended`  INT UNSIGNED NOT NULL,
    `boss_wcid_placed`    INT UNSIGNED NOT NULL,   -- 0 when the boss failed to place, or there was none
    `boss_killed`         TINYINT(1) NOT NULL,
    `boss_health_ratio`   DOUBLE NOT NULL,         -- difficulty tuning, NOT a placement signal
    `boss_health_clamped` TINYINT(1) NOT NULL,     -- difficulty tuning, NOT a placement signal
    `survey_filed`        TINYINT(1) NOT NULL,
    `credited_kills`      INT NOT NULL,            -- RESERVED, no writer, always 0
    `xp_gained`           BIGINT NOT NULL,
    `lum_gained`          BIGINT NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ended` (`ended_utc`),
    KEY `ix_char_ended` (`character_id`, `ended_utc`),
    KEY `ix_dungeon_ended` (`dungeon_id`, `ended_utc`),
    -- Not requested, added under the same rule as the reserved columns: an index is part of the
    -- table definition, so CREATE TABLE IF NOT EXISTS cannot add one later either, and the
    -- headline query for this table groups on start_group.
    KEY `ix_start_group` (`start_group`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The gem's modifier list for one run, one row per modifier. `run_fk` is dungeon_run.id, never
-- dungeon_run.run_id. `magnitude` is the modifier's rolled magnitude as it sat on the gem; what it
-- means is per modifier id and lives in Content/dungeons/dynamic/modifiers.json, not here.
-- A run whose gem carried no modifiers contributes no rows at all, so a modifier join must be a
-- LEFT JOIN or plain gems vanish from the population.
CREATE TABLE IF NOT EXISTS `dungeon_run_modifier` (
    `run_fk`      BIGINT UNSIGNED NOT NULL,
    `modifier_id` VARCHAR(32) NOT NULL,
    `magnitude`   DOUBLE NOT NULL,
    PRIMARY KEY (`run_fk`, `modifier_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Why a run's population did not come out the way the plan asked, aggregated per run.
--
-- `attempts` counts placement ATTEMPTS, not plan entries, and the difference is the whole reason
-- this table exists as its own shape. A single plan entry can fail at several anchors once anchor
-- fallback lands, so one entry can produce several attempts; equally, one row can cover several
-- distinct entries, because rows are aggregated per run on the (phase, reason, wcid, role) key.
-- Never read SUM(attempts) as "creatures missing" - planned minus spawned on the parent row is
-- that number.
--
-- `phase` is 'plan' or 'place': was this decided by the pure builder before any object existed, or
-- by the spawner while putting a creature into the world. It is part of the AGGREGATION KEY, not a
-- decoration, and that is load-bearing. Boss killability checking moves to plan time on an
-- in-flight branch, at which point 'not_killable' and 'not_creature' become emittable from BOTH
-- producers with the same reason string meaning different things - at plan time the content roster
-- is bad, at place time the world refused a creature - and those have different owners and
-- different fixes. Keying without phase would merge a plan-time and a place-time failure of the
-- same wcid into one row and the distinction would not be recoverable. It happens to be derivable
-- from role plus reason today; it will not be once that branch lands.
--
-- `reason` is a closed set of exactly nine codes (DungeonRunTelemetry.Reasons):
--   'create_threw'         - WorldObjectFactory.CreateNewWorldObject threw.
--   'not_creature'         - the wcid did not resolve to a Creature.
--   'not_killable'         - not attackable, or flagged PlayerKillerStatus.NPC.
--   'off_landblock'        - the spawn point is not on the run's own landblock.
--   'enter_world_refused'  - EnterWorld returned false.
--   'enter_world_threw'    - EnterWorld threw.
--   'no_candidate'         - PLAN time, not placement time: a boss was wanted (the dungeon has a
--                            boss anchor) and neither the curated bosses.json window nor the run's
--                            own family could field one. wcid is 0 on these rows, because the
--                            failure is that there was no wcid to try.
--   'anchor_fallback_used' - RESERVED, in-flight fallback branch. A RECOVERY, not a failure.
--   'fallback_exhausted'   - RESERVED, in-flight fallback branch. Every anchor was tried.
--
-- `is_failure` is 0 for 'anchor_fallback_used' and 1 for every one of the other eight. A panel
-- counting placement failures MUST filter is_failure = 1; a naive SUM(attempts) over the whole
-- table mixes a recovery in with the failures it recovered from.
--
-- `entry_placed` is 1 when a plan entry that contributed attempts to this row nonetheless ended up
-- in the world. It is permanently 0 on every TERMINAL reason - all eight is_failure = 1 codes are
-- terminal for the entry they refused, so nothing can retroactively rescue one - and is set by
-- ThreadDungeonRun.MarkEntryPlaced only on RECOVERY rows, which today means only
-- 'anchor_fallback_used'. So `is_failure = 1 AND entry_placed = 0` is the honest definition of "a
-- creature the plan wanted and the run never got".
--
-- `credited` is RESERVED and has NO writer: it is always 0. See dungeon_run.credited_kills.
--
-- `role` is 'Trash', 'Elite' or 'Boss' (DungeonRole.ToString()).
--
-- Boss health shortfalls are NOT in this table and must never be added to it - they are a
-- difficulty signal, and the boss they describe placed fine. See dungeon_run.boss_health_ratio.
CREATE TABLE IF NOT EXISTS `dungeon_run_placement` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `run_fk`       BIGINT UNSIGNED NOT NULL,
    `phase`        VARCHAR(8) NOT NULL,    -- 'plan' | 'place'; part of the aggregation key
    `reason`       VARCHAR(32) NOT NULL,   -- closed set of nine, see above
    `is_failure`   TINYINT(1) NOT NULL,    -- 0 only for 'anchor_fallback_used'
    `entry_placed` TINYINT(1) NOT NULL,    -- recovery rows only; always 0 on a terminal reason
    `credited`     TINYINT(1) NOT NULL,    -- RESERVED, no writer, always 0
    `wcid`         INT UNSIGNED NOT NULL,  -- 0 on 'no_candidate'
    `role`         VARCHAR(16) NOT NULL,
    `attempts`     INT NOT NULL,           -- ATTEMPTS, not entries
    PRIMARY KEY (`id`),
    KEY `ix_run` (`run_fk`),
    KEY `ix_reason` (`reason`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Group Threads (Docs/Threads/GROUP-THREADS-DESIGN.md), Task 3: per-character participation in one
-- finished run. Child of dungeon_run, one row per character who was on the roster at any point.
--
-- A SOLO run writes exactly ONE participant row (the owner, is_owner = 1). A pre-feature run (written
-- before this table existed) has NO participant row at all, and no dungeon_run_group row either - a
-- panel joining this table must use a LEFT JOIN and treat a missing row as "solo or pre-feature",
-- never as zero participation, and should prefer COALESCE(g.roster_size, 1) from dungeon_run_group
-- for the effective roster size.
--
-- `xp_gained` / `lum_gained` here are PER CHARACTER. dungeon_run.xp_gained / lum_gained is the RUN
-- TOTAL (everyone who stood inside, see that column's comment above) - the two are different
-- denominators and a panel must never add a participant row's value into the run total (R25).
--
-- `piles_received` / `piles_forfeited` count DISTINCT DEAL ROUNDS (R21): a round counts as received
-- if at least one item from it landed in one of this member's caches (reaching the held pile is not
-- enough), and as forfeited if it was still held, undelivered, at run end. Both are always 0 on a solo
-- row - solo has no deal rounds at all.
--
-- `entered` / `seconds_inside` / `survey_filed` / `key_granted` mirror the run-level equivalents but
-- scoped to this one character; `level_start` / `level_end` are this character's level at roster lock
-- and at run end, not the owner's (dungeon_run.char_level_start/char_level already cover the owner).
CREATE TABLE IF NOT EXISTS `dungeon_run_participant` (
    `run_fk`          BIGINT UNSIGNED NOT NULL,
    `character_id`    INT UNSIGNED    NOT NULL,
    `name`            VARCHAR(64)     NOT NULL,
    `is_owner`        TINYINT(1)      NOT NULL,
    `level_start`     INT             NOT NULL,
    `level_end`       INT             NOT NULL,
    `entered`         TINYINT(1)      NOT NULL,
    `seconds_inside`  INT             NOT NULL,
    `xp_gained`       BIGINT          NOT NULL,
    `lum_gained`      BIGINT          NOT NULL,
    `piles_received`  INT             NOT NULL,
    `piles_forfeited` INT             NOT NULL,
    `survey_filed`    TINYINT(1)      NOT NULL,
    `key_granted`     TINYINT(1)      NOT NULL,
    PRIMARY KEY (`run_fk`, `character_id`),
    KEY `ix_character` (`character_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Group Threads, Task 3: the group scaling snapshot for one finished GROUP run. One row per group
-- run, NEVER written for a solo run (dungeon_run_participant.is_owner alone already tells solo from
-- group for a run that DOES have one participant row, but the absence of a dungeon_run_group row is
-- the authoritative solo/pre-feature signal - see the comment above).
--
-- `count_mult` is C_actual (the count multiplier the run actually got, after the group-max-monsters
-- cap); `count_mult_target` is C (the uncapped target before that cap). `health_mult` is H = E /
-- C_actual, already applied to trash and elite health by the time this row is written. Values are
-- snapshotted once at roster lock (R28) and never re-read from a tunable afterward.
CREATE TABLE IF NOT EXISTS `dungeon_run_group` (
    `run_fk`              BIGINT UNSIGNED NOT NULL,
    `roster_size`         INT             NOT NULL,
    `effort`              DOUBLE          NOT NULL,
    `count_mult`          DOUBLE          NOT NULL,
    `count_mult_target`   DOUBLE          NOT NULL,
    `health_mult`         DOUBLE          NOT NULL,
    `damage_rating_bonus` INT             NOT NULL,
    `reward_bonus`        DOUBLE          NOT NULL,
    `share_total`         DOUBLE          NOT NULL,
    PRIMARY KEY (`run_fk`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The plan-time facts one run's population actually drew: which monster family it fielded, and the
-- tuning knobs (health/damage/xp/loot multipliers, boss normalization terms, the band it drew from)
-- that plan carried. 1:1 with dungeon_run and ALWAYS PRESENT - exactly one row per run, no matter
-- how the run ended, which is why `run_fk` is the primary key rather than an AUTO_INCREMENT id with
-- its own index. This is deliberately its own table rather than columns bolted onto dungeon_run:
-- dungeon_run's schema is already frozen (see the migration note above), and CREATE TABLE IF NOT
-- EXISTS has no ALTER path, so a new fact family gets a new table rather than waiting for a column
-- that can never be added.
--
-- `source` is 'live' when AnalyticsDatabase.WriteDungeonRuns wrote the row from the running server
-- (ThreadDungeonManager.RecordRunTelemetry, same transaction as the parent dungeon_run row and its
-- other children) or 'log' when Backfill/thread-run-detail-from-log.ps1 reconstructed it after the
-- fact from a saved server log. A 'log' row exists only for a run whose 'live' write predates this
-- feature; the backfill script's INSERT IGNORE means a run that already has a 'live' row can never
-- pick up a conflicting 'log' one, because `run_fk` is the primary key.
--
-- `plan_built` is ThreadDungeonRun.PlanRecord != null at EndRun time: did this run ever get as far
-- as building a plan (ThreadDungeonSpawner.TryPopulate calling DungeonPopulationBuilder.Build
-- successfully) before ThreadDungeonRun.MarkPlan latched it. EVERY plan-derived column below is
-- NULL, never a sentinel, whenever this is 0 - the run's population threw before a plan existed, so
-- there was nothing to record. Compare dungeon_run.populate_reached, which is a DIFFERENT latch (did
-- placement get as far as being ENQUEUED): a plan can build successfully and still have
-- populate_reached stay 0 if something failed between the two, so the two latches are not
-- interchangeable.
--
-- `cleared_secs` is ThreadDungeonRun.ClearedUtc - StartedUtc, in seconds - NULL when the run was
-- never cleared. This is NOT the same quantity as dungeon_run.duration_secs, which is
-- start-to-END time and keeps running for as long as a cleared player lingers inside; cleared_secs
-- is the moment the run was actually WON, and is the number a "how long does a clear take" panel
-- wants.
--
-- `family_id` is the monster family the plan actually drew (DungeonSpawnPlan.FamilyId), NOT the
-- gem's requested family - dungeon_run.family is that column, and it reads 'any' on every player
-- gem today because nothing else is shipped. This is the column that answers "what did players
-- actually fight", and it is why this table exists.
--
-- `trash_planned` / `elite_planned` / `entries_planned` / `uplifted_planned` count
-- DungeonSpawnPlan.Entries by role (Trash/Elite) and in total, and by UpliftLevel > 0 for uplifted -
-- the identical filter ThreadDungeonSpawner's plan log line uses to print "uplifted=". Boss entries
-- are excluded from trash_planned/elite_planned (DungeonRole.Boss is neither), so
-- trash_planned + elite_planned + (1 if a boss was planned else 0) = entries_planned.
--
-- `natural_band_low` / `effective_band_low` are DungeonSpawnPlan.NaturalBandLow / EffectiveBandLow:
-- the unwidened natural low edge and the low edge the run actually drew from after any widening.
-- Equal when nothing widened.
--
-- `boss_level` / `boss_normalize` / `boss_health_base` / `pool_max_base` are DungeonSpawnPlan.BossLevel,
-- BossNormalize, BossHealthBase and PoolMaxBase - the boss-normalization terms the plan log line
-- prints as "bossBase=... (poolMax ...)". boss_health_base is 0 whenever the normalized formula had
-- nothing to say and the spawner fell back to the legacy BossHealthTarget for that boss.
--
-- `health_multiplier` / `boss_health_multiplier` / `health_curve_target` / `health_normalize_ratio` /
-- `trash_health_floor` are DungeonSpawnPlan's own fields of the same name (camelCase to snake_case).
-- 0 is a genuine no-op value for several of these (HealthNormalizeRatio, TrashHealthFloor,
-- HealthCurveTarget), not a missing value - see DungeonSpawnPlan's own doc comments.
--
-- `band_standard_samples` is DungeonSpawnPlan.BandStandard.SampleCount: how many distinct wcids with
-- usable data the natural-band standard was derived from. 0 means DungeonBandStandard.Empty - no
-- standard, because nothing was ever uplifted or the sample had no usable data.
--
-- `damage_rating` / `crit_rating` / `crit_damage_rating` / `damage_resist_rating` /
-- `boss_damage_rating` / `boss_damage_resist_rating` are DungeonSpawnPlan's rating fields of the same
-- name, the exact terms the plan log line prints as dr=/cr=/cdr=/drr=/bossDr=/bossDrr=.
--
-- `run_speed_mult` / `ignore_shield` are DungeonSpawnPlan.RunSpeedMult / IgnoreShield, one value for
-- the whole run (boss included) on the plan's own convention.
--
-- `hollow_intensity` is DungeonSpawnPlan.HollowIntensity, the plan's real 0-1 hollow intensity.
-- See DungeonRunPlanRecord.HollowIntensity.
--
-- `strip_combat_traits` is DungeonSpawnPlan.StripCombatTraits, as it applied to this plan.
--
-- `xp_multiplier` / `boss_xp_multiplier` / `lum_multiplier` / `xp_scale` / `lum_scale` /
-- `loot_quantity_mult` are DungeonSpawnPlan's reward-scaling fields of the same name.
--
-- `loot_tier` is DungeonSpawnPlan.Profile.Tier - the TreasureDeath tier the plan log line prints as
-- tier=. NULL when plan_built = 0, on the same convention as everything else here.
CREATE TABLE IF NOT EXISTS `dungeon_run_detail` (
    `run_fk`                    BIGINT UNSIGNED NOT NULL,
    `source`                    VARCHAR(8)  NOT NULL,   -- 'live' | 'log'
    `plan_built`                TINYINT(1)  NOT NULL,   -- did population get as far as building a plan
    `cleared_secs`              DOUBLE      NULL,        -- NULL until cleared; ClearedUtc - StartedUtc
    `family_id`                 VARCHAR(32) NULL,        -- monster family the plan actually DREW, not the gem's request
    `trash_planned`             INT         NULL,
    `elite_planned`             INT         NULL,
    `entries_planned`           INT         NULL,
    `uplifted_planned`          INT         NULL,
    `natural_band_low`          INT         NULL,
    `effective_band_low`        INT         NULL,
    `boss_level`                INT         NULL,
    `boss_normalize`            TINYINT(1)  NULL,
    `boss_health_base`          DOUBLE      NULL,
    `pool_max_base`             INT UNSIGNED NULL,
    `health_multiplier`         DOUBLE      NULL,
    `boss_health_multiplier`    DOUBLE      NULL,
    `health_curve_target`       DOUBLE      NULL,
    `health_normalize_ratio`    DOUBLE      NULL,
    `trash_health_floor`        INT UNSIGNED NULL,
    `band_standard_samples`     INT         NULL,
    `damage_rating`             INT         NULL,
    `crit_rating`               INT         NULL,
    `crit_damage_rating`        INT         NULL,
    `damage_resist_rating`      INT         NULL,
    `boss_damage_rating`        INT         NULL,
    `boss_damage_resist_rating` INT         NULL,
    `run_speed_mult`            DOUBLE      NULL,
    `ignore_shield`             DOUBLE      NULL,
    `hollow_intensity`          DOUBLE      NULL,        -- plan's real 0-1 hollow intensity; see comment above
    `strip_combat_traits`       TINYINT(1)  NULL,
    `xp_multiplier`             DOUBLE      NULL,
    `boss_xp_multiplier`        DOUBLE      NULL,
    `lum_multiplier`            DOUBLE      NULL,
    `xp_scale`                  DOUBLE      NULL,
    `lum_scale`                 DOUBLE      NULL,
    `loot_tier`                 INT         NULL,
    `loot_quantity_mult`        DOUBLE      NULL,
    PRIMARY KEY (`run_fk`),
    KEY `ix_family` (`family_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Banked pyreals over time, written from the same flush as the roster and rate rows.
--
-- THE VALUE IS THE ACCOUNT'S POOL, NOT A PER-CHARACTER BALANCE, and this is the one way to get a
-- query here badly wrong. Banked pyreals live in a single account_bank row per account, so every
-- row sharing an `account_id` carries the SAME number - one balance, restated once per character.
-- NEVER SUM banked_pyreals across characters: a sum multiplies one account's balance by how many
-- characters it has, and an account with six alts reports six times the money it holds. To total
-- the shard, reduce to one row per account first (MAX or ANY_VALUE per account_id); to rank
-- accounts, take one representative character per account_id, which is what /top bank does.
-- The rows stay CHARACTER-keyed because that is what lets a per-character panel resolve a
-- character and what makes the history readable by name.
--
-- A flush that cannot read the pool writes NO rows at all rather than zeros, so a gap here never
-- means "everyone went broke".
--
-- CHANGE-ONLY. A row exists only where a character's balance DIFFERED from that character's previous
-- snapshot, so a balance that sits still produces no rows at all and the gaps are not missing data.
-- Any reader - a chart, a report, a diff between two dates - must carry the last value forward rather
-- than treat an absent interval as zero, and must never compute a per-interval delta by assuming the
-- previous row is one flush old.
--
-- The source is PlayerManager.GetAllPlayers(), which is the in-memory offline + online player list, so
-- OFFLINE characters are covered and the numbers here are the same ones /top bank ranks. It is not a
-- shard query: nothing in this path reads the database per player, and a raw SQL edit to a banked
-- balance stays invisible to both until the next server restart.
--
-- A server restart re-baselines: the writer's last-seen map starts empty, so the first flush after a
-- restart writes one row per known character regardless of whether anything moved. That is intended -
-- it re-anchors every balance and marks in the data where the restart happened.
--
-- Retention keeps the NEWEST row per character forever and ages out only the intermediate history
-- (AnalyticsDatabase.PruneBankSnapshots), so a "latest balance per character" read needs no time
-- filter and can never come back empty for a character that has been snapshotted at least once.
-- A wealthy account that simply stops trading keeps its balance on the board instead of vanishing.
--
-- `account_id` is 0 for a character with no account row (an orphan left behind by a deleted account).
CREATE TABLE IF NOT EXISTS `char_bank_snapshot` (
    `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `character_id`   INT UNSIGNED    NOT NULL,
    `name`           VARCHAR(64)     NOT NULL,
    `account_id`     INT UNSIGNED    NOT NULL,
    `level`          INT             NOT NULL,
    `ts_utc`         DATETIME        NOT NULL,
    `banked_pyreals` BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_char_ts` (`character_id`, `ts_utc`),
    KEY `ix_acct_ts` (`account_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- One row per CHARACTER SESSION, for the IP integrity dashboard (Docs/Monitoring/IP-INTEGRITY-DESIGN.md).
-- The column list is the design's binding section 3.1, and like every table here it is FROZEN once shipped:
-- CREATE TABLE IF NOT EXISTS will not add a column to a table that already exists and AnalyticsDatabase
-- has no ALTER, so a later column means a manual migration on every host.
--
-- `ip` is the RAW client address (VARBINARY(16): 4 bytes for IPv4, 16 for IPv6), the same address
-- IpLimitManager evaluates. Render it with INET6_NTOA(`ip`) - it handles both widths. Raw IPs are kept
-- (owner ruling 2026-09-30) under their OWN retention key, Server.AnalyticsIpRetentionDays (default 30),
-- deliberately not the rate tables' window.
--
-- `asn` / `asn_org` / `is_hosting` are resolved on the writer thread from two optional files on the host
-- (Server.AnalyticsAsnDatabasePath, Server.AnalyticsHostingAsnListPath). NULL means "lookup unavailable",
-- never "no": is_hosting is 1 (ASN on the hosting/VPN list), 0 (ASN known, not on the list) or NULL (no
-- ASN database, no list, or this address is not in the database). Read NULL as unknown, not as 0.
--
-- A session's effective end is COALESCE(`logout_utc`, `last_seen_utc`). `last_seen_utc` is advanced on
-- every analytics flush for each session whose character is still online, so a crash or an unclean
-- shutdown cannot leave a session looking open forever. On boot the writer closes every session still
-- open from the previous process by setting logout_utc = last_seen_utc. Overlap queries must use the
-- COALESCE, never treat a NULL logout_utc as open-ended.
--
-- `close_reason` says HOW a session ended, so a crash is distinguishable from a clean logout. NULL while the
-- session is open. Exactly these five values (SessionCloseReason in SessionEvents.cs; the dashboard reads them):
--   'logout'   - a FinalizeLogout event was seen; logout_utc is the real logout time.
--   'orphan'   - the per-flush reconcile found the character offline with no logout event; logout_utc is
--                last_seen_utc, so the end is approximate (up to one flush interval early).
--   'boot'     - boot cleanup closed it: the previous process died or stopped without closing it.
--   'relogin'  - the same character logged in again while this row was still open.
--   'shutdown' - the graceful-shutdown final pass closed it.
-- Only 'logout' carries an exact end time.
--
-- Volume is bounded by logins, not gameplay: the smallest stream in this database.
CREATE TABLE IF NOT EXISTS `session_event` (
    `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `account_id`     INT UNSIGNED    NOT NULL,
    `account_name`   VARCHAR(64)     NOT NULL,
    `character_id`   INT UNSIGNED    NOT NULL,
    `character_name` VARCHAR(64)     NOT NULL,
    `ip`             VARBINARY(16)   NOT NULL,
    `asn`            INT UNSIGNED    NULL,
    `asn_org`        VARCHAR(128)    NULL,
    `is_hosting`     TINYINT(1)      NULL,
    `login_utc`      DATETIME        NOT NULL,
    `last_seen_utc`  DATETIME        NOT NULL,
    `logout_utc`     DATETIME        NULL,
    `close_reason`   VARCHAR(8)      NULL,
    PRIMARY KEY (`id`),
    KEY `ix_login` (`login_utc`),
    KEY `ix_acct_login` (`account_id`, `login_utc`),
    KEY `ix_ip_login` (`ip`, `login_utc`),
    KEY `ix_char_login` (`character_id`, `login_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
