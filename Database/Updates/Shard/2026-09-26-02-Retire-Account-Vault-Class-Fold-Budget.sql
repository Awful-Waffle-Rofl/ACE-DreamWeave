/* Retire account_vault_class_fold_budget.

   The key bounded ONE pass of the background vault fold rotation: how many already-stored biotas a
   single pass would try to collapse into counted account_vault_class rows. The rotation ran on the
   world heartbeat, one store per second server-wide, and it was removed once the migration it existed
   to perform had finished on prod (2026-09-26: whole fleet, 132 of 132 accounts exhausted, 15,901
   items folded, candidates down from 15,423 to 523). From then on it was permanent world-loop cost for
   work that cannot come back, because the deposit path collapses a classifiable item on arrival and a
   failed class withdraw refunds the class row rather than materializing a biota.

   /vaultclassfold is the migration route from now on. It supplies its own per-batch size (--batch,
   default 25, AccountVaultFoldMigration.DefaultBatchSize) and never read this key, so nothing about
   the operator path changes here.

   THIS IS ADMIN-PANEL HYGIENE, NOT A CORRECTNESS FIX, and that distinction is worth stating because an
   orphan row looks alarming and is not. PropertyManager.LoadPropertiesFromDB caches every row it finds
   whether or not any code default names the key, so a leftover row simply sits in the cache with no
   reader; nothing consults account_vault_class_fold_budget once the code is gone. What the row WOULD do
   is surface in /listproperties and in the web admin panel as a live-looking setting that does nothing,
   which is what this file removes.

   account_vault_class_storage is NOT retired: it is still the kill switch for the counted tier, read by
   every non-pristine deposit and re-checked by every fold batch.

   GUARDED BY KEY, NOT BY VALUE, the same as 2026-09-22-02-MlDigsiteRetireBroadcastRadius.sql: a retired
   key has no reader, so an operator-tuned row is as dead as a default one.

   RE-RUNNING IS A NO-OP: once the row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_long` WHERE `key` = 'account_vault_class_fold_budget';
