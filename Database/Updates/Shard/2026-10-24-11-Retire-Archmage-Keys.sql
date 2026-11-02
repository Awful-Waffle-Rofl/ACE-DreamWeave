/* Retire the two Archmage keys the 2026-10-24 Archmage pass left without a reader. Both are double keys:
   - class_ability_quickenedcasting_percent_per_rank
     (Quickened Casting retired, replaced by Elemental Inversion)
   - class_ability_flatcastspeed_percent_per_rank
     (Flat Cast Speed retired, replaced by Arcane Hollow)

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. A retired key has no reader, so an
   operator-tuned row is as dead as a default one (same shape as 2026-10-24-06-Retire-Berserker-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_quickenedcasting_percent_per_rank';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_flatcastspeed_percent_per_rank';
