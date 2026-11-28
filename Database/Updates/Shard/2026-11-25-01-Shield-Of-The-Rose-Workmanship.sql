/* Shield of the Rose (wcid 1007451) gains ItemWorkmanship (PropertyInt 105) = 10 (owner ruling 2026-11-25: the shield
   accepts Tourmaline, Amethyst, Black Opal and Fire Opal through the tinker-locked-shield exception in
   Source/ACE.Server/Entity/TinkerLock.cs, and both the weapon-mod flows and the Black / Fire Opal imbue recipes refuse
   a target with no workmanship).

   The same PR adds the row to the weenie template in Content/sql/weenies/1007451, which fixes every shield created FROM
   NOW ON. An existing shield was persisted as a biota with its own copy of the template's property rows (a biota is
   built from a full copy of the weenie's rows, Source/ACE.Entity/Adapter/WeenieConverter.cs:33-34), and the shard loads
   a biota's properties from ace_shard, not from ace_world. So every Shield of the Rose a player already owns has no
   workmanship until the row is written here.

   Only a biota with NO type-105 row gets one: an existing workmanship value is never overwritten. The table's primary
   key is (object_Id, type), and the NOT EXISTS guard keeps the insert from ever meeting it.

   Idempotent with no ledger beyond the filename the updater already records: after the first run every Rose has a
   type-105 row, so a second run inserts nothing.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's
   next save of that item would delete the new row (BiotaUpdater removes int rows missing from the in-memory biota,
   Source/ACE.Database/Adapter/BiotaUpdater.cs:102-103). Ship the image and the weenie content together. */

INSERT INTO `biota_properties_int` (`object_Id`, `type`, `value`)
SELECT b.`id`, 105, 10
FROM `biota` b
WHERE b.`weenie_Class_Id` = 1007451
  AND NOT EXISTS (SELECT 1 FROM `biota_properties_int` bi WHERE bi.`object_Id` = b.`id` AND bi.`type` = 105);
