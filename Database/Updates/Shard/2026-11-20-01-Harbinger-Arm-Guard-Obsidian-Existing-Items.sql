/* The Arm Guard of the Harbinger (wcid 1008268) becomes Obsidian friendly: its ModSalvageAllowMask
   (PropertyInt 9095) goes from 3 (Tourmaline, Amethyst) to 7 (adds Obsidian, bit 4). The same PR
   changes the weenie template in Content/sql/weenies, which fixes every Arm Guard created FROM NOW ON,
   but an Arm Guard that already exists was persisted as a biota with its own copy of the property rows,
   and TinkerLock reads the mask from the item's own biota only, so existing Arm Guards would keep
   refusing Obsidian until the row is written here.

   The mask is a bit field, so the statement ORs bit 4 into an existing row (keeping the other bits)
   and inserts the template's value 7 where no row exists. The primary key of `biota_properties_int`
   is (object_Id, type), which is what ON DUPLICATE KEY keys on. A second run ORs the same bit again
   and changes nothing, so it is idempotent with no ledger beyond the filename the updater records.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running
   server's saves would overwrite it. Ship the image and the weenie content together (deploy all prod),
   or dispatch the content deploy immediately after the image boot; until both land, existing and
   newly created Arm Guards disagree. */

INSERT INTO `biota_properties_int` (`object_Id`, `type`, `value`)
SELECT b.`id`, 9095, 7 FROM `biota` b
WHERE b.`weenie_Class_Id` IN (1008268)
ON DUPLICATE KEY UPDATE `value` = `value` | 4;
