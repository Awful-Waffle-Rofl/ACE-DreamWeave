/* The three Drift Atlan Infusions (wcids 1007400 Elemental, 1007401 Armor, 1007402 Crippling) and Drift Matron
   Spines (wcid 1007602) become TRADEABLE (owner ruling 2026-10-31): the same PR removes the Attuned
   (PropertyInt 114) row from their weenie templates in Content/sql/weenies. That fixes every copy created from
   now on, but one that already exists was persisted as a biota with its own copy of the template's property
   rows, and the shard loads a biota's properties from ace_shard, not from ace_world, so existing ones would
   stay Attuned (untradeable, undroppable) until their row is removed.

   Bonded (PropertyInt 33) is deliberately NOT touched: it only keeps the item out of the death drop, and the
   templates still carry it.

   Idempotent with no ledger beyond the filename the updater already records: a second run finds no 114 row left
   on these wcids and deletes nothing. Scoped to these four wcids only.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's
   saves would write the Attuned row back. Ship the image and the weenie content together. */

DELETE bi FROM `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
WHERE bi.`type` = 114 AND b.`weenie_Class_Id` IN (1007400, 1007401, 1007402, 1007602);
