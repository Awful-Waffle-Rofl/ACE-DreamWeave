/* Retire the two Elemental Rend proc-chance keys. Elemental Rend (Archmage T3) became Elemental Proficiency,
   which applies its element's effect on every landed damaging spell with no roll, so neither key has a
   meaning any more. Both are double keys:
   - class_ability_elementalrend_chance_base
   - class_ability_elementalrend_chance_step

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. A retired key has no reader, so an
   operator-tuned row is as dead as a default one (same shape as 2026-10-24-11-Retire-Archmage-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_elementalrend_chance_base';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_elementalrend_chance_step';
