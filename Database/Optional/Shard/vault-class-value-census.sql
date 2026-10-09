-- ===========================================================================================
-- Mule Vendor: PER-ACCOUNT VALUE CENSUS for the counted item-class storage tier.
--
-- WHAT THIS IS FOR
--
-- The class tier destroys a stored item's biota and replaces it with a COUNT and a POOLED
-- TOTAL on account_vault_class. The invariant that has to hold across that migration is that
-- no Value is created or destroyed: whatever an account's vault was worth before the fold, it
-- must be worth exactly the same after it, with the value simply having moved from item rows
-- into pooled row totals.
--
-- This file is that check, at full scale against real data. Run it BEFORE the fold, run it
-- AFTER the fold, and compare `total_Value` per account. Nothing else needs to be compared.
--
-- WHY total_Value AND NOT THE INDIVIDUAL COLUMNS. `stored_Value` legitimately FALLS and
-- `class_Value` legitimately RISES - that is the migration happening. Only their sum is
-- invariant, and it is invariant whatever the predicate did or did not accept, which is why
-- this census deliberately covers EVERY stored biota and not just the salvage the fold cares
-- about. It needs to know nothing about the predicate to be a valid check.
--
-- ONE ROW PER ACCOUNT, NEVER A FLEET TOTAL. A grand total conceals exactly the failure this
-- exists to catch: value leaking off one account and being created on another nets to zero.
--
-- ===========================================================================================
-- RUN IT ON A QUIESCED SHARD, WITH NOBODY CONNECTED.
--
-- THIS CENSUS IS ONLY EXACT IF NOTHING CHANGES BETWEEN THE TWO RUNS. A deposit, a withdraw, a
-- sale, a barrel or a restore between the PRE run and the POST run legitimately changes an
-- account's total, and the comparison will then report a difference that is not a bug. There
-- is no way for the query to tell the two apart.
--
-- So: stop the world, or run it on a shard copy nobody is logged into. If a player was online
-- during either run, the result is not evidence and must not be reported as a pass or a fail.
-- ===========================================================================================
--
-- READ ONLY. Every statement in this file is a SELECT. It creates nothing, writes nothing,
-- drops nothing, and leaves no temporary table behind. It is safe to run against a live shard
-- (the QUIESCE requirement above is about the ANSWER being meaningful, not about safety).
--
-- ===========================================================================================
-- THE EXACT TWO COMMANDS
--
--   mysql -u <user> -p <shard_db> < vault-class-value-census.sql > census-pre.tsv
--   ... run the fold to completion (see below) ...
--   mysql -u <user> -p <shard_db> < vault-class-value-census.sql > census-post.tsv
--
-- Then compare. On Windows PowerShell:
--
--   Compare-Object (Get-Content census-pre.tsv) (Get-Content census-post.tsv)
--
-- ...will show every changed line, which is expected (stored_Value moves into class_Value).
-- What matters is the LAST column. To compare only that:
--
--   $pre  = Get-Content census-pre.tsv  | Select-Object -Skip 1 | ForEach-Object { $f = $_ -split "`t"; "$($f[0]) $($f[-1])" }
--   $post = Get-Content census-post.tsv | Select-Object -Skip 1 | ForEach-Object { $f = $_ -split "`t"; "$($f[0]) $($f[-1])" }
--   Compare-Object $pre $post
--
-- PASS  = that last Compare-Object prints NOTHING. Every account's account_Id and total_Value
--         are byte-identical between the two runs. (The middle columns WILL have changed; that
--         is the migration working.)
--
-- FAIL  = any line at all. Each one names an account whose vault changed total value across
--         the fold. Note both sides: a `<=` line is the PRE value and a `=>` line is the POST
--         value for the same account, so the difference is the amount leaked (POST lower) or
--         printed (POST higher). Capture both files and the account ids before doing anything
--         else - the class rows still hold the evidence, and a later fold pass will overwrite
--         the picture.
--
-- ALSO A FAIL: an account that appears in one file and not the other. That means its whole
-- vault stopped being counted, which is a bigger version of the same fault.
--
-- ===========================================================================================
-- INVESTIGATING A FAIL: the audit trail, not this query
--
-- This census says an account's total moved. account_vault_log says what moved it. Since
-- Database/Updates/Shard/2026-09-24-01-Account-Vault-Log-Value-And-Class.sql it carries the two
-- columns that make that reconstructable: `value` (the Value that entered the pool on a deposit
-- or a fold, or the summed share that left on a withdraw) and `class_Key` (WHICH pool - wcid
-- alone is ambiguous, since one wcid spans as many classes as there are distinct property
-- combinations). Both are NULL on every non-class action, and NULL is not 0.
--
--   SELECT `timestamp`, `action`, `actor_Character_Name`, `wcid`, `class_Key`, `count`, `value`
--   FROM `account_vault_log`
--   WHERE `owner_Account_Id` = <the account the census flagged> AND `class_Key` IS NOT NULL
--   ORDER BY `id`;
--
-- action: 0 Deposit, 1 Withdraw, 4 Return, 7 Fold (ACE.Entity.Enum.AccountVaultAction). The
-- migration is action 7 with actor "(vault fold)", deliberately separable from the player's own
-- deposits - folding one large vault writes thousands of rows in minutes.
--
-- Per class key, SUM(value) over actions 0, 4 and 7 minus SUM(value) over action 1 should equal
-- that row's current total_Value. Where it does not is where to look.
--
-- Running the fold to completion: /vaultclassfold all, and read its COMPLETE / INCOMPLETE
-- headline. There is no background fold any more - the heartbeat rotation that used to drift one
-- store per second was retired with the prod migration on 2026-09-26, along with its tunable
-- account_vault_class_fold_budget - so nothing folds unless an operator runs that command. Do NOT
-- infer completion from log silence: Runbook section 16 is the authority, and it is the ordered
-- procedure this file's two runs belong to.
--
-- ===========================================================================================
-- SCOPE DECISIONS, stated so the two runs cannot disagree about them
--
-- 1. THE BARREL IS EXCLUDED, on BOTH sides, via `account_vault`.`kind` = 0.
--
--    account_vault.kind is 0 for an ordinary vault container and 1 for the barrel (see
--    Database/Updates/Shard/2026-09-04-00-Add-Account-Vault-Barrel.sql). The barrel holds
--    items the player has already destroyed, pending an admin restore, and the fold CANNOT
--    touch it: AccountVaultStore.FoldSomeStoredItemsOnQueue walks EnumerateStoredItemsLocked,
--    which iterates the `vaults` list only, and the barrel is held in a separate field. A barrel
--    RESTORE puts the item back into a vault, which is one of the reasons /vaultclassfold stays
--    re-runnable.
--
--    It is excluded rather than included because the barrel retention reaper purges rows on
--    its own schedule, so including it would let an unrelated background sweep show up as a
--    census difference. Excluding it costs nothing: value cannot move between the barrel and
--    a class row in either direction.
--
-- 2. account_vault_stack (the PRISTINE ledger) IS EXCLUDED, on BOTH sides.
--
--    Those rows carry a wcid and a unit count and no Value at all - a pristine item's value
--    comes from its weenie when it is rebuilt, so there is nothing stored to sum. The fold
--    does not touch that table. Including it would mean inventing a value per unit, which is
--    the opposite of a census.
--
-- 3. EVERY stored biota is counted, not just classifiable ones. See above.
--
-- ===========================================================================================
-- SCHEMA NOTES (verified against Source/ACE.Database/Models/Shard/ShardDbContext.cs)
--
--   biota_properties_i_i_d   -- InstanceId properties. NOT biota_properties_iid.
--                               ShardDbContext.cs:749. Columns: object_Id, type, value.
--   biota_properties_int     -- Int properties. ShardDbContext.cs:774. Same three columns.
--
--   PropertyInstanceId.Container = 2   (Source/ACE.Entity/Enum/Properties/PropertyInstanceId.cs:12)
--   PropertyInt.Value            = 19  (Source/ACE.Entity/Enum/Properties/PropertyInt.cs:36)
--
--   An item stored in a vault is a biota carrying InstanceId 2 = that vault's container_Guid.
--   Its Value is Int 19 on the same object_Id. A biota with no Int 19 row has no Value and
--   contributes 0, which is why the join to biota_properties_int is a LEFT JOIN - an INNER
--   join there would silently drop valueless items from the biota COUNT and make a census
--   difference look like a missing item.
-- ===========================================================================================

SELECT
    t.`account_Id`                                      AS account_Id,
    SUM(t.`stored_Biotas`)                              AS stored_Biotas,
    SUM(t.`stored_Value`)                               AS stored_Value,
    SUM(t.`class_Rows`)                                 AS class_Rows,
    SUM(t.`class_Items`)                                AS class_Items,
    SUM(t.`class_Value`)                                AS class_Value,
    SUM(t.`stored_Value`) + SUM(t.`class_Value`)        AS total_Value
FROM
(
    -- Half one: every biota parented to one of this account's ORDINARY vault containers, and
    -- the sum of its PropertyInt.Value.
    SELECT
        av.`account_Id`                                 AS `account_Id`,
        COUNT(*)                                        AS `stored_Biotas`,
        COALESCE(SUM(bpi.`value`), 0)                   AS `stored_Value`,
        0                                               AS `class_Rows`,
        0                                               AS `class_Items`,
        0                                               AS `class_Value`
    FROM `account_vault` av
    JOIN `biota_properties_i_i_d` cont
        ON  cont.`type`  = 2
        AND cont.`value` = av.`container_Guid`
    LEFT JOIN `biota_properties_int` bpi
        ON  bpi.`object_Id` = cont.`object_Id`
        AND bpi.`type`      = 19
    WHERE av.`kind` = 0
    GROUP BY av.`account_Id`

    UNION ALL

    -- Half two: the counted class rows. Before the fold this half contributes nothing, which
    -- is why the SAME query serves as both the PRE and the POST census - there is no second
    -- query to keep in step with this one, and therefore no way for the two to disagree.
    SELECT
        avc.`account_Id`                                AS `account_Id`,
        0                                               AS `stored_Biotas`,
        0                                               AS `stored_Value`,
        COUNT(*)                                        AS `class_Rows`,
        COALESCE(SUM(avc.`count`), 0)                   AS `class_Items`,
        COALESCE(SUM(avc.`total_Value`), 0)             AS `class_Value`
    FROM `account_vault_class` avc
    GROUP BY avc.`account_Id`
) t
GROUP BY t.`account_Id`
ORDER BY t.`account_Id`;
