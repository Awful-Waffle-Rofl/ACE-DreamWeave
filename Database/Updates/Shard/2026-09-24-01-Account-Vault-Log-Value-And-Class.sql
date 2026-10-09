/* Mule Vendor - two nullable columns on account_vault_log, so a value discrepancy on the counted
 * item-CLASS tier is investigable from the audit trail alone.
 *
 * WHY NOW. A class row holds a COUNT and a POOLED TOTAL and no biota. If an account's total_Value
 * ever disagrees with what went in, the only record of what moved is this log - and as shipped it
 * records wcid, item name and count, none of which can reconstruct a value. Added BEFORE the tier
 * writes any rows, so there is no era of class rows whose audit trail is missing the one column an
 * investigation needs.
 *
 *   1. `value` (bigint NULL). On a class DEPOSIT, the item's own PropertyInt.Value as it entered
 *      the pool. On a class WITHDRAW, the SUM of the pooled shares that left. NULL everywhere
 *      else, including on every row already written and on every non-class action, so nothing
 *      existing changes meaning. bigint rather than int because a withdraw row carries a SUM over
 *      up to a whole class, which overruns int long before the item count does.
 *
 *   2. `class_Key` (char(32) NULL). Which pool the row touched. `wcid` alone is ambiguous: one
 *      wcid spans as many classes as there are distinct combinations of Structure, workmanship,
 *      NumItemsInMaterial and Name, so without this the log cannot say which of an account's
 *      several salvage pools a row moved value into or out of. NULL for every non-class action.
 *
 * NEITHER COLUMN IS INDEXED. Both are read during an investigation, by an operator already
 * filtering on owner_Account_Id (which is indexed) and a time window (also indexed). An index on
 * either would be paid for on every audit write - and the audit writer is on the deposit path -
 * to speed up a query nobody runs in normal operation.
 *
 * A THIRD CHANGE LIVES IN CODE, NOT HERE: AccountVaultAction.Fold = 7. A fold pass routes through
 * AccountVaultStore.DepositToClass, which writes an audit row, so before this the background
 * migration of one large vault would write thousands of rows labelled Deposit and drown the
 * player's own activity in the same table an investigation reads. action is persisted as a plain
 * int and the enum is append-only, so no migration is needed for the new value; rows written
 * before it exists simply never carry it.
 *
 * RE-RUNNABLE, AND THAT IS REQUIRED, NOT POLITE. MySQL 8.0 has no ADD COLUMN IF NOT EXISTS, and
 * Program_DbUpdates breaks its loop on a throwing script, skipping every later migration on that
 * boot - so an unguarded ALTER here would fail on every boot after the first and take the rest of
 * the patch pass with it. Each DDL is therefore a SET / PREPARE / EXECUTE around an
 * information_schema check, the same idiom as
 * Database/Updates/Shard/2026-09-22-00-Account-Vault-Grant-Grantee-Index.sql. The `@` variables
 * rely on the patcher connection's AllowUserVariables=true, which is already set for exactly this.
 *
 * No backfill. There is nothing to backfill from: the value a pre-existing Deposit row moved is
 * not recorded anywhere else, and inventing one would be worse than leaving it NULL.
 */

SET @avl_value := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault_log' AND COLUMN_NAME = 'value');
SET @avl_value_ddl := IF(@avl_value > 0, 'DO 1',
  'ALTER TABLE `account_vault_log` ADD COLUMN `value` bigint NULL COMMENT ''Class tier only: Value that entered the pool on a deposit, or the summed share that left on a withdraw. NULL for every other action''');
PREPARE avl_value_stmt FROM @avl_value_ddl;
EXECUTE avl_value_stmt;
DEALLOCATE PREPARE avl_value_stmt;

SET @avl_class_key := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault_log' AND COLUMN_NAME = 'class_Key');
SET @avl_class_key_ddl := IF(@avl_class_key > 0, 'DO 1',
  'ALTER TABLE `account_vault_log` ADD COLUMN `class_Key` char(32) NULL COMMENT ''VaultItemClass.ClassKey of the pool this row touched - wcid alone is ambiguous. NULL for every non-class action''');
PREPARE avl_class_key_stmt FROM @avl_class_key_ddl;
EXECUTE avl_class_key_stmt;
DEALLOCATE PREPARE avl_class_key_stmt;
