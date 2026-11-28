/* Shield of the Rose (wcid 1007451): minimum wield level 100 -> 200 (owner ruling 2026-11-26).

   The same PR changes WieldDifficulty (PropertyInt 160) from 100 to 200 in the weenie template
   Content/sql/weenies/1007451, which covers every shield created from now on. A shield a player already owns is a
   biota with its own copy of the template's property rows, loaded from ace_shard, so it keeps 100 until this runs.

   Only rows that still hold the old value 100 on a Rose that requires a level (WieldRequirements 158 = 7) change; a
   value anyone has set by hand to something else is left alone. Idempotent: a second run matches nothing.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's next
   save of a loaded item writes its in-memory value back over this row. Ship the image and the weenie content
   (Content/sql/weenies/1007451) together: new drops come from the ace_world weenie, so without the content they keep 100. */

UPDATE `biota_properties_int` bi
JOIN `biota` b ON b.`id` = bi.`object_Id`
JOIN `biota_properties_int` req ON req.`object_Id` = b.`id` AND req.`type` = 158 AND req.`value` = 7
SET bi.`value` = 200
WHERE b.`weenie_Class_Id` = 1007451
  AND bi.`type` = 160
  AND bi.`value` = 100;
