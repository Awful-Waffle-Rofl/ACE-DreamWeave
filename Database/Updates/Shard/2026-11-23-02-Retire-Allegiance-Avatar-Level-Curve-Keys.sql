/* Retire the three Allegiance Avatar level-curve keys: allegiance_avatar_level_max,
   allegiance_avatar_level_base_xp (config_properties_long) and allegiance_avatar_level_growth
   (config_properties_double).

   2026-11-23 OWNER RULING: the Avatar levels like a normal character, from a pool holding a tunable share
   (allegiance_avatar_level_xp_fraction, default 0.01) of every XP grant its members receive, read against
   the player chart. The geometric points curve and its cap no longer exist, so the three keys have no
   reader left.

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. This DELETE is unconditional on value - a retired key
   has no reader, so an operator-tuned row is as dead as a default one (same shape as
   2026-10-02-01-Retire-SoulJump-VoidDamage-Affinity-Rate-Keys.sql).

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_long` WHERE `key` = 'allegiance_avatar_level_max';
DELETE FROM `config_properties_long` WHERE `key` = 'allegiance_avatar_level_base_xp';
DELETE FROM `config_properties_double` WHERE `key` = 'allegiance_avatar_level_growth';
