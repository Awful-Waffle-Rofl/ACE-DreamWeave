/* The 12 Harbinger weapons (wands 1008269-1008272, heavies 1008274-1008277, scythes 1008273 and
   1008278-1008280) gain the Crippling Blow imbue: ImbuedEffect (PropertyInt 179) = 2 (owner ask
   2026-11-22). The Arm Guard (1008268) is not touched.
   The same PR changes the weenie templates in Content/sql/weenies, which fixes every weapon created
   FROM NOW ON, but a weapon that already exists was persisted as a biota with its own copy of the
   property rows, so existing drops would stay un-imbued until the row is written here.

   ImbuedEffect is a bit field, so the statement ORs bit 2 into an existing row (keeping any other
   imbue) and inserts the template's value 2 where no row exists. The primary key of
   `biota_properties_int` is (object_Id, type), which is what ON DUPLICATE KEY keys on. A second run
   ORs the same bit again and changes nothing, so it is idempotent with no ledger beyond the filename
   the updater records.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running
   server's saves would overwrite it. Ship the image and the weenie content together (deploy all prod),
   or dispatch the content deploy immediately after the image boot; until both land, existing and
   newly created weapons disagree. */

INSERT INTO `biota_properties_int` (`object_Id`, `type`, `value`)
SELECT b.`id`, 179, 2 FROM `biota` b
WHERE b.`weenie_Class_Id` IN (1008269, 1008270, 1008271, 1008272, 1008273, 1008274, 1008275, 1008276, 1008277, 1008278, 1008279, 1008280)
ON DUPLICATE KEY UPDATE `value` = `value` | 2;
