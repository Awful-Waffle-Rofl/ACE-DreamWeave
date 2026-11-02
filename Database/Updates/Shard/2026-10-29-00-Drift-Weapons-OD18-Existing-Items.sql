/* Drift Atlan weapons (wcids 1007403-1007428) move from OD+10 to OD+18: the same PR raises the weenie
   templates in Content/sql/weenies/1007403..1007428 by 8 points (melee PropertyInt 44 Damage: Sword 71 ->
   79, Mace 56 -> 64, Axe 61 -> 69, Two Handed Sword 45 -> 53; bow PropertyInt 204 ElementalDamageBonus 22
   -> 30; wands PropertyFloat 152 ElementalDamageMod 1.21 -> 1.29). That fixes every weapon created FROM
   NOW ON, but a weapon that already exists was persisted as a biota with its own copy of those property
   rows, and the shard loads a biota's properties from ace_shard, not from ace_world, so existing weapons
   would keep the old values until these rows are moved.

   Each update is guarded on the exact OLD value and on the wcid range of its weapon family (Sword
   1007403-06, Mace 07-10, Axe 11-14, Two Handed Sword 15-18, Bow 19-22, wands 23-28), so a second run
   finds nothing left to change and is idempotent with no ledger beyond the filename the updater already
   records. The melee update is range-scoped because the bows also carry PropertyInt 44 = 8, which must
   not be touched. Mana and Resistance Cleaving are deliberately not part of this change.

   Safe ONLY because the weenie values change in the same PR: if the templates still held the old
   numbers, new drops would sit below their upgraded siblings. Applies at the next server boot on the
   new image; do NOT apply it to a live shard, because a running server's saves would overwrite it.

   Ship the image and the weenie content together (deploy all prod), or dispatch the content deploy
   immediately after the image boot; until both land, existing and newly created Drift weapons disagree. */

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = bi.`value` + 8
WHERE bi.`type` = 44 AND (
     (b.`weenie_Class_Id` BETWEEN 1007403 AND 1007406 AND bi.`value` = 71)
  OR (b.`weenie_Class_Id` BETWEEN 1007407 AND 1007410 AND bi.`value` = 56)
  OR (b.`weenie_Class_Id` BETWEEN 1007411 AND 1007414 AND bi.`value` = 61)
  OR (b.`weenie_Class_Id` BETWEEN 1007415 AND 1007418 AND bi.`value` = 45));

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 30
WHERE bi.`type` = 204 AND b.`weenie_Class_Id` BETWEEN 1007419 AND 1007422 AND bi.`value` = 22;

UPDATE `biota_properties_float` bf INNER JOIN `biota` b ON b.`id` = bf.`object_Id`
SET bf.`value` = 1.29
WHERE bf.`type` = 152 AND b.`weenie_Class_Id` BETWEEN 1007423 AND 1007428 AND ABS(bf.`value` - 1.21) < 1e-6;