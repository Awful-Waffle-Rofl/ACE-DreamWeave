/* Toa Sigil (wcid 1005490) shipped as WeenieType Generic (1) instead of Stackable (51) - fixed in
 * Content/sql/weenies/1005490 Toa Sigil.sql, playability review B2 on PR #1219. WorldObject.
 * SetStackSize is a no-op unless the runtime object `is Stackable`
 * (Source/ACE.Server/WorldObjects/WorldObject_Properties.cs:3196-3200), and the runtime type is
 * fixed by WeenieType at construction - so a Generic sigil already dropped into a shard, or minted
 * from the OLD content row before this PR applies, is permanently stuck at StackSize 1 no matter
 * what BluespireLadderRewards.Pay asks for, because WorldObjectFactory reads WeenieType from the
 * BIOTA row (ace_shard), not from ace_world, once an object instance exists.
 *
 * This is the same class of bug PR #1215 fixes for the Marae Lassel Doubloon (wcid 1004101) - see
 * that PR's own migration for the identical pattern applied to a different wcid.
 *
 * Idempotent: only touches rows still sitting on the old, wrong type (1), so a shard that has
 * never seen 1005490, or one already fixed by an earlier run of this file, matches zero rows. */

UPDATE biota SET weenie_Type = 51 WHERE weenie_Class_Id = 1005490 AND weenie_Type = 1;
