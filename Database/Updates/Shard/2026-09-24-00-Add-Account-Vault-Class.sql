/* Mule Vendor - the counted vault storage tier (item CLASSES).
 *
 * One table, owned by Docs/MuleVendor/DESIGN.md section 8 alongside `account_vault_stack`.
 *
 * `account_vault_class` is the counted ledger for items that are NOT identical to their weenie
 * template but ARE provably reproducible from it plus a small whitelist of per-instance properties
 * (VaultCollapse.TryDescribeClass). There is NO biota behind these rows, exactly as for
 * `account_vault_stack`: a classifiable item is destroyed on deposit and rebuilt on withdraw from
 * `canonical_Form`, which is what lets a few hundred salvage bags cost one row instead of a few
 * hundred biotas.
 *
 * `count` is ITEMS, not units. A class row stands for indivisible objects - a salvage bag carries
 * MaxStackSize 1 - so there is no per-item stack size to express and counting units would be a lie.
 * `account_vault_stack.count` counts UNITS; the two columns share a name and mean different things,
 * and that difference is the reason this is a separate table rather than a widened one.
 *
 * `total_Value` is the POOLED PropertyInt.Value across all `count` items, and it is bigint
 * deliberately. Value is not part of class identity in v1 (see `value_Band_Pct`), so a class row
 * carries a sum rather than one canonical value, and a withdraw hands out
 * round(remaining_total / remaining_count) per item while decrementing both. A large hoard sums far
 * past what an int holds.
 *
 * `value_Band_Pct` records the identity policy the row's key was computed under. 0 is the owner's v1
 * ruling: Value is EXCLUDED from identity and pooled instead. The band is also part of
 * `canonical_Form`, so a key computed under one policy can never be mistaken for one computed under
 * another; the column is a denormalization of that text for indexing and for cheap cross-checks.
 *
 * `canonical_Form` IS THE PAYLOAD. There is no second JSON encoding beside it: the class key is a
 * hash of exactly this text (VaultItemClass.CanonicalForm / ClassKey), and storing a second
 * serialization of the same facts is how the two drift apart and a rebuilt item comes back wrong.
 * `wcid` is likewise denormalized out of it for indexing and display only, and every read
 * cross-checks both denormalized columns against the parsed form and refuses the row on a mismatch
 * rather than trusting either side.
 *
 * No FK to `character` and none to `account_vault`, for the same reason the sibling tables have
 * none: a ledger row must survive the character being deleted and the account owning no container.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * Idempotent: safe to re-run. Shard updates are tracked by FILENAME against
 * DatabaseSetupScripts/Updates/Shard/applied_updates.txt, with no ledger table, and a script that
 * throws is not recorded AND stops every alphabetically later script that boot - which is exactly
 * why re-runnability is mandatory rather than polite. Only ever adds a table.
 */

CREATE TABLE IF NOT EXISTS `account_vault_class` (
  `id`              int unsigned  NOT NULL AUTO_INCREMENT,
  `account_Id`      int unsigned  NOT NULL,
  `class_Key`       char(32)      NOT NULL COMMENT 'VaultItemClass.ClassKey - 32 lowercase hex characters',
  `wcid`            int unsigned  NOT NULL COMMENT 'Denormalized from canonical_Form for indexing and display',
  `count`           bigint        NOT NULL COMMENT 'ITEMS held, not units. A class row counts indivisible objects',
  `total_Value`     bigint        NOT NULL COMMENT 'Pooled PropertyInt.Value across all count items',
  `value_Band_Pct`  int           NOT NULL COMMENT '0 = Value excluded from identity and pooled (the v1 ruling)',
  `canonical_Form`  text          NOT NULL COMMENT 'VaultItemClass.CanonicalForm - THE payload, hashed into class_Key',
  PRIMARY KEY (`id`),
  UNIQUE KEY `account_vault_class_uidx` (`account_Id`, `class_Key`),
  KEY `account_vault_class_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Mule Vendor - counted item-class ledger. No biota behind these rows';
