/* Platinum Horn of Leadership (wcid 1004142, Ilo Farhollow's stock) no longer requires Leadership:
   the 2026-10-06 ML vendor price pass removed PropertyInt.UseRequiresSkill (366 = 35, Leadership) and
   PropertyInt.UseRequiresSkillLevel (367 = 225) from Content/sql/weenies/1004142 Platinum Horn of
   Leadership.sql. That fixes every horn created FROM NOW ON, but a horn a player already bought was
   persisted as a biota with its own copy of those two property rows (biota_properties_int), and the
   shard loads a biota's properties from ace_shard, not from ace_world, so already-sold horns would keep
   demanding Leadership 225 until these rows are deleted.

   Scoped to biotas of wcid 1004142 only, and only to types 366 and 367, so Virindi Consul Essence
   (wcid 1004139, its own Arcane Lore gate on the same two property types) and every other item are
   untouched. A shard that never held a horn (a fresh stage, or one that never sold one) matches zero
   rows, and a second run finds nothing left to delete, so it is idempotent and needs no ledger
   beyond the filename the updater already records. */

DELETE bint FROM `biota_properties_int` bint
INNER JOIN `biota` b ON bint.`object_Id` = b.`id`
WHERE b.`weenie_Class_Id` = 1004142 AND bint.`type` IN (366, 367);