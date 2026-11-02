/* Retire the two Taunt keys the 2026-10-17 Vanguard pass left without a reader:
   class_ability_taunt_radius (replaced by the per-rank class_ability_taunt_radius_r1/_r2/_r3) and
   class_ability_taunt_loyalty_bonus_cap_seconds (Taunt lost its Assess Person affinity, so the hold
   duration is flat). Both are double keys.

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. A retired key has no reader, so an
   operator-tuned row is as dead as a default one (same shape as
   2026-10-17-01-Retire-BloodMage-Buff-Pass-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_taunt_radius';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_taunt_loyalty_bonus_cap_seconds';