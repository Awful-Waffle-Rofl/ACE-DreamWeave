/* Retire the four single-value keys the 2026-10-24 Spellsword pass left without a reader. Each was
   replaced by a per-rank _r1/_r2/_r3 trio when its ability gained (or re-scaled) ranks:
     class_ability_spellsurge_per_stack    -> class_ability_spellsurge_per_stack_r1/_r2/_r3 (0.0067/0.0133/0.02)
     class_ability_spellstorm_chance       -> class_ability_spellstorm_chance_r1/_r2/_r3 (0.05/0.10/0.15)
     class_ability_cascade_chance          -> class_ability_cascade_chance_r1/_r2/_r3 (0.05/0.10/0.15)
     class_ability_runicward_cap_fraction  -> class_ability_runicward_cap_fraction_r1/_r2/_r3 (0.10/0.20/0.30)
   All four are double keys.

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. A retired key has no reader, so an
   operator-tuned row is as dead as a default one (same shape as
   2026-10-17-02-Retire-Taunt-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_spellsurge_per_stack';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_spellstorm_chance';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_cascade_chance';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_runicward_cap_fraction';
