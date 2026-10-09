/* ML digsite Corruption shape: retire the four meter config keys.

   ml_digsite_meter_max, ml_digsite_meter_per_tick, ml_digsite_meter_tick_seconds and
   ml_digsite_meter_damage_step were removed from PropertyManager when round 16 retired the
   Corruption meter mechanic (a climbing fill that failed the run at 100%) in favour of killing a
   fixed number of Corrupted mobs one at a time (ml_digsite_corruption_kills_required and friends).
   No code reads any of the four any more, but PropertyManager's periodic flush had already
   persisted each as a row on every shard that booted with the old code, and a leftover row would
   surface in /listproperties and the admin panel as a live-looking setting that does nothing.

   GUARDED BY KEY, NOT BY VALUE. Unlike a changed-default migration, there is no tuned value worth
   preserving here: a retired key has no reader, so an operator-tuned row is as dead as a default
   one. Each statement touches only its named keys, split by table since the four keys span both
   config_properties_long (the three meter timing/step keys) and config_properties_double
   (ml_digsite_meter_damage_step).

   RE-RUNNING IS A NO-OP: once the rows are gone the WHERE clause matches nothing, and a shard that
   never had the keys matches nothing either. */

DELETE FROM `config_properties_long` WHERE `key` IN ('ml_digsite_meter_max', 'ml_digsite_meter_per_tick', 'ml_digsite_meter_tick_seconds');
DELETE FROM `config_properties_double` WHERE `key` = 'ml_digsite_meter_damage_step';
