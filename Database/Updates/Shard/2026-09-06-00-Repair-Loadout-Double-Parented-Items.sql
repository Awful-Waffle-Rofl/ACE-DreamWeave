/* Repairs items left both CONTAINED and WIELDED at once by the loadout gear restore.

   Player_Loadouts.RestoreLoadoutEquip and WithdrawAndRestoreFromVault equipped a remembered item
   through TryEquipObjectWithNetworking without first detaching it from the pack it was sitting in.
   The player-facing wield path does that detach one frame up in its caller
   (Player_Inventory.DoHandleActionGetAndWieldItem, immediately before the same call), so only the
   loadout path could produce this. The commit that fixes it is the one shipping alongside this file;
   this script cleans up what the broken build already wrote.

   The resulting row carries BOTH PropertyInstanceId.Container (2) and PropertyInstanceId.Wielder (3)
   pointing at the character. That combination is always corrupt - it is never a legitimate runtime
   state, and the server already says so in its own words: Player_Inventory.LogDuplicatePossession
   warns that "type 2 (Container) and type 3 (Wielder) must not both be set". It matters because the
   shard loader reads a character's possessions in two independent passes - ShardDatabase's
   GetInventoryInParallel by Container and GetWieldedItemsInParallel by Wielder - and Player's
   constructor feeds them to SortBiotasIntoInventory and AddBiotasToEquippedObjects respectively. An
   object carrying both ids is therefore materialised TWICE, as two live WorldObjects sharing one
   guid: one in the pack, one worn. The pack copy can then be given away, dropped, or deposited to
   the account vault while the worn copy survives, which is a duplication rather than a display bug.

   THE REPAIR KEEPS THE PACK SIDE, NOT THE WORN SIDE. Container's TryAddToInventory sets Owner and
   Container together, so an item that was moved into a pack already has a well-formed pair; stripping
   the Container side instead would leave Owner set with no container, which is a messier state than
   the one being fixed. Dropping Wielder therefore yields an ordinary pack item, and the player
   re-wields it by hand. CurrentWieldedLocation (PropertyInt 10) goes with it, since a pack item has
   no wielded location - ValidLocations (PropertyInt 9) is deliberately left alone, because that is
   the item's inherent "where can this be worn" and is correct on a pack item.

   STATEMENT ORDER IS LOAD-BEARING. The Wielder row is what identifies an affected object, so the
   CurrentWieldedLocation delete has to run FIRST. Reversed, the second statement would match nothing
   and leave stale wielded locations behind on items that are no longer wielded.

   Deliberately NOT scoped to a character or a guid list. The condition is a corruption invariant, not
   an incident: any object anywhere in the shard carrying both ids is wrong by the same argument. A
   guid list would fix one occurrence and silently miss the rest. On a shard that never ran the broken
   build - prod, or a freshly seeded stage - both statements match zero rows and the script simply
   records itself as applied.

   Idempotent by construction rather than by guard: after this runs, nothing satisfies the both-set
   condition, so a second boot deletes nothing. That self-clearing condition is also why this needs no
   stored procedure, no marker table in ace_shard_migration_marker and no IF NOT EXISTS wrapper - the
   guards other scripts here carry exist for changes that are not self-detecting, and a repair keyed on
   the corruption itself does not need one.

   MULTI-TABLE DELETE FORM, NOT A SUBQUERY, and this is load-bearing rather than stylistic. MySQL error
   1093 forbids a DELETE whose subquery selects from the table being deleted from, which the second
   statement here unavoidably does: an affected object is identified by a type=3 row and the type=3 row
   is what gets removed. The usual dodge is to bury the subquery in a derived table, but that only works
   when the derived table is MATERIALIZED, and a derived table with no aggregate, DISTINCT, GROUP BY,
   HAVING, LIMIT or UNION is a merge candidate under the derived_merge optimizer switch, which is ON by
   default in MySQL 8.0 - what stage and prod both run. The multi-table DELETE ... FROM ... JOIN form has
   no self-reference restriction at all, so it sidesteps the question rather than betting on the
   optimizer. This repo already uses that form twice for this exact shape: see
   2026-08-26-00-Backfill-Original-Gear-Ratings.sql and 2026-09-01-00-Add-Account-Bank.sql.

   Why a 1093 here would be far worse than a repair that simply did not run: Program_DbUpdates.PatchDatabase
   catches the exception, does NOT record the filename as applied, and BREAKS out of the script loop. The
   corruption would stay unrepaired, this script would fail identically on every subsequent boot, and every
   shard update script ordered after it would be silently skipped for good. A wedged update pipeline, not
   a no-op.

   Both statements drive off the type=3 (Wielder) side, which is the smaller of the two.
*/

DELETE `i` FROM `biota_properties_int` `i`
  JOIN `biota_properties_i_i_d` `w`
    ON `w`.`object_Id` = `i`.`object_Id` AND `w`.`type` = 3
  JOIN `biota_properties_i_i_d` `c`
    ON `c`.`object_Id` = `i`.`object_Id` AND `c`.`type` = 2
 WHERE `i`.`type` = 10;

DELETE `w` FROM `biota_properties_i_i_d` `w`
  JOIN `biota_properties_i_i_d` `c`
    ON `c`.`object_Id` = `w`.`object_Id` AND `c`.`type` = 2
 WHERE `w`.`type` = 3;
