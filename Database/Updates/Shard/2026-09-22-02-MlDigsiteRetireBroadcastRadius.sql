/* ML digsite round 17: retire ml_digsite_broadcast_radius_metres.

   The digsite's two opening lines ("[World Event] The ground gives way ..." and "[World Event] <type> -
   the earth splits near <town (coords)>.") were radius-scoped until round 17: the first to
   ml_digsite_audience_radius_metres, the second to ml_digsite_broadcast_radius_metres. Round 17 (owner
   ruling 2026-09-22) sends both to every online player in the island realm instead
   (MlDigsiteManager.AnnounceOpening -> WorldEventAnnouncer.BroadcastToRealm), so nothing reads the
   broadcast radius any more and the key was removed from PropertyManager. PropertyManager's periodic
   flush had already persisted it as a row on every shard that booted with the old code, and a leftover
   row would surface in /listproperties and the admin panel as a live-looking setting that does nothing.

   ml_digsite_audience_radius_metres is NOT retired: it still decides the audience, wave size, presence,
   payout and every in-encounter line.

   GUARDED BY KEY, NOT BY VALUE, the same as 2026-09-21-02-MlDigsiteRetireCorruptionMeter.sql: a retired
   key has no reader, so an operator-tuned row is as dead as a default one.

   RE-RUNNING IS A NO-OP: once the row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'ml_digsite_broadcast_radius_metres';