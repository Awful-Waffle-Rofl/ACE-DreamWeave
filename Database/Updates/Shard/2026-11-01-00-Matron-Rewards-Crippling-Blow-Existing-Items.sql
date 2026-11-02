/* Three Drift Matron reward weapons gain the Crippling Blow imbue (PropertyInt 179 ImbuedEffect, bit 2):
   the Olthoi Voidclaw (wcid 1007600), the Hive Stinger Baton (1007603) and the Hive Nether Baton
   (1007604). The same PR adds a PropertyInt 179 (ImbuedEffect) = 2 row (CripplingBlow) to their weenie
   templates in Content/sql/weenies. That fixes every weapon created FROM NOW ON, but a weapon that
   already exists was persisted as a biota with its own copy of the property rows, and the shard loads
   a biota's properties from ace_shard, not from ace_world, so existing weapons would stay without the
   imbue until the row is written here.

   ImbuedEffect is a bit field, so the statement ORs bit 2 into an existing row (keeping any other
   imbue bits the item already carries) and inserts a fresh row where none exists. The primary key of
   `biota_properties_int` is (object_Id, type), which is what ON DUPLICATE KEY keys on. A second run
   ORs the same bit again and changes nothing, so it is idempotent with no ledger beyond the filename
   the updater already records.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running
   server's saves would overwrite it. Ship the image and the weenie content together (deploy all prod),
   or dispatch the content deploy immediately after the image boot; until both land, existing and
   newly created weapons disagree. */

INSERT INTO `biota_properties_int` (`object_Id`, `type`, `value`)
SELECT b.`id`, 179, 2 FROM `biota` b
WHERE b.`weenie_Class_Id` IN (1007600, 1007603, 1007604)
ON DUPLICATE KEY UPDATE `value` = `value` | 2;
