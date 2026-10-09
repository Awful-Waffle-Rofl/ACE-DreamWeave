/* Marae Lassel Doubloon (wcid 1004101) shipped as weenie type 1 (Generic) and is being corrected to
   type 51 (Stackable) in the same content unit this migration ships alongside
   (Content/sql/weenies/1004101 Marae Lassel Doubloon.sql). That fixes stacking for every doubloon
   created FROM NOW ON, but a doubloon a player is already holding was persisted with its biota's own
   weenie_Type column copied from the old Generic weenie at creation time
   (BiotaConverter.ConvertFromEntityBiota, which snapshots WeenieType onto the biota row) and does not
   pick up the new code-side default on its own - the shard loader reads weenie_Type off the biota,
   not off ace_world. Existing doubloons would stay non-stackable, sitting in separate pack slots and
   refusing to merge with newly-minted ones, until this runs.

   Scoped narrowly to wcid 1004101 rows still carrying the old type, so a shard that never held any
   (a fresh stage, or one that only ever saw the corrected weenie) matches zero rows and the migration
   is a no-op. Idempotent by construction: after this runs, no row satisfies weenie_Type = 1 for this
   wcid, so a second boot updates nothing.

   Deliberately does not touch any biota_properties_* row. StackSize itself (biota_properties_int type
   12) is left exactly as each biota already has it - a lone Generic-era item already carries
   StackSize 1, which is the correct value for a Stackable item holding one coin, so nothing there
   needs correcting. The type flip alone is what lets the client and Player_Inventory's own merge
   logic treat these as mergeable going forward. */

UPDATE `biota` SET `weenie_Type` = 51 WHERE `weenie_Class_Id` = 1004101 AND `weenie_Type` = 1;
