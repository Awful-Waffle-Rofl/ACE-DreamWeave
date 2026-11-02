/* Retire the four Blood Mage keys the 2026-10-17 buff pass left without a reader:
   class_ability_crimsonharvest_max_targets (long; replaced by the per-rank
   class_ability_crimsonharvest_extra_targets_r1/_r2/_r3), and the three Hemomancy keys
   class_ability_hemomancy_tick_percent_per_rank, _heal_fraction and _drain_percent_per_rank (double;
   Hemomancy is now a heal-armed damage buff and no longer touches DoT ticks, tick healing or Drain).

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. A retired key has no reader, so an
   operator-tuned row is as dead as a default one (same shape as
   2026-10-02-01-Retire-SoulJump-VoidDamage-Affinity-Rate-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_long` WHERE `key` = 'class_ability_crimsonharvest_max_targets';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_hemomancy_tick_percent_per_rank';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_hemomancy_heal_fraction';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_hemomancy_drain_percent_per_rank';