/* ML digsite endless payout: retire two config keys.

   ml_digsite_waves (the fixed wave count) and ml_digsite_abandon_grace_seconds (the abandon test)
   were removed from PropertyManager when Waves became endless and the abandon test became the
   wipe test (ml_digsite_wipe_grace_seconds). No code reads either key any more, but
   PropertyManager's periodic flush had already persisted each as a row on every shard that booted
   with the old code, and a leftover row would surface in /listproperties and the admin panel as a
   live-looking setting that does nothing.

   GUARDED BY KEY, NOT BY VALUE. Unlike a changed-default migration, there is no tuned value worth
   preserving here: a retired key has no reader, so an operator-tuned row is as dead as a default
   one. The statement touches only these two exact keys.

   RE-RUNNING IS A NO-OP: once the rows are gone the WHERE clause matches nothing, and a shard that
   never had the keys matches nothing either. */

DELETE FROM `config_properties_long` WHERE `key` IN ('ml_digsite_waves', 'ml_digsite_abandon_grace_seconds');