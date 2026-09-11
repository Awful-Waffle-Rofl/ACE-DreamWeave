/* Mule Vendor - one-time cleanup of mule vendor biotas written by a pre-exclusion build.
 *
 * A summoned mule is a disposable WINDOW onto an account's vault (Docs/MuleVendor/DESIGN.md section
 * 5). It must never be persisted to the shard, which is risk R9, and
 * `WorldObject.IsDynamicThatShouldPersistToShard` excludes `PersonalVendor` for exactly that reason.
 *
 * That exclusion is correct going forward and does nothing for rows already written. The method
 * returns true on `BiotaOriginatedFromOrHasBeenSavedToDatabase()` before it reaches the
 * `PersonalVendor` branch, so a mule that reached the shard under an earlier build is rebuilt by
 * `WorldObjectFactory`'s biota arm on the next landblock activation - which sets
 * `biotaOriginatedFromDatabase` - and `Landblock.SaveDB` then re-saves it on every unload. Worse,
 * `Landblock.Unload` takes the `RemoveWorldObjectInternal` arm rather than `Destroy` for a saved
 * object, so the ordinary lifecycle never removes the row at all.
 *
 * The result is a store-less zombie vendor: the factory's biota arm builds it with a null `Store`, so
 * `RebuildView` and `TryAuthorize` refuse every operation, and its only exit is `TimeToRot` reaching
 * zero. It is visible to players and it does nothing.
 *
 * Unreachable on a clean shard - a newly summoned mule never originates from the database, so the
 * exclusion fires and no row is ever written. This matters only for a shard that ran a build between
 * the vendor landing and the exclusion landing, which in practice means a development or stage shard
 * on this branch.
 *
 * SAFETY. This deletes only wcid 1003150, the mule vendor itself. It cannot reach a vault container
 * (an ordinary backpack, wcid 136, indexed by `account_vault`) and it cannot reach a stored item: a
 * mule holds neither. Stored biotas live inside the store's own vault containers, and the mule
 * references them through a private dictionary that no persistence path walks. Deleting a mule row
 * cannot lose a player item.
 *
 * One statement is sufficient and complete. Every child table of `biota` in this schema declares
 * ON DELETE CASCADE - verified against `information_schema.referential_constraints` for all 24
 * referencing tables, so no property rows are orphaned. This deliberately does NOT hand-enumerate the
 * child tables: an explicit list would be redundant today and would silently miss any table added
 * later, which is the failure mode that produced orphaned rows in 13 property tables once already.
 *
 * Idempotent by construction: a re-run deletes nothing once the rows are gone. Shard migrations here
 * are tracked by FILENAME against `applied_updates.txt` and must be safe to re-run.
 */

DELETE FROM `biota` WHERE `weenie_Class_Id` = 1003150;
