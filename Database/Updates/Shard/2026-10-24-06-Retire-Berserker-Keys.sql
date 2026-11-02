/* Retire the five Berserker keys the 2026-10-24 Berserker pass left without a reader. All five are
   double keys:
   - class_ability_savageblows_percent_per_rank, class_ability_savageblows_stamina_surcharge
     (Savage Blows retired; its equipment mod row was repurposed in place as Heavy Blows)
   - class_ability_vengeance_percent_per_rank, class_ability_vengeance_window_seconds
     (Vengeance retired, replaced by Intimidate)
   - class_ability_whirlwind_stamina_surcharge
     (replaced by the per-rank class_ability_whirlwind_stamina_surcharge_r1/_r2/_r3)

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. A retired key has no reader, so an
   operator-tuned row is as dead as a default one (same shape as 2026-10-17-02-Retire-Taunt-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_savageblows_percent_per_rank';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_savageblows_stamina_surcharge';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_vengeance_percent_per_rank';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_vengeance_window_seconds';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_whirlwind_stamina_surcharge';
