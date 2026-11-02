/* Fibers (wcid 1007000) become TRADEABLE (owner ruling 2026-10-29): the same PR removes the Attuned
   (PropertyInt 114) row from the Fiber weenie template in Content/sql/weenies. That fixes every Fiber created from now
   on, but one that already exists was persisted as a biota with its own copy of the template's
   property rows, and the shard loads a biota's properties from ace_shard, not from ace_world, so existing ones
   would stay Attuned (untradeable, undroppable) until their row is removed.

   Bonded (PropertyInt 33) is deliberately NOT touched: it only keeps the Fiber out of the death drop, and the
   template still carries it.

   Idempotent with no ledger beyond the filename the updater already records: a second run finds no 114 row left
   on this wcid and deletes nothing. Scoped to wcid 1007000 only: the Knot (1007001) and the Knotting Pouch (1007002) stay Attuned.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's
   saves would write the Attuned row back. Ship the image and the weenie content together. */

DELETE bi FROM `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
WHERE bi.`type` = 114 AND b.`weenie_Class_Id` = 1007000;
