/* Toa Sigil (wcid 1005490) shipped with no Attuned row - fixed in
 * Content/sql/weenies/1005490 Toa Sigil.sql, owner ruling 2026-09-20 ("Attune the sigils so the alt
 * character cannot trade to the main"). WorldObjectFactory.CreateWorldObject
 * (Source/ACE.Server/Factories/WorldObjectFactory.cs:50-52) copies weenie properties into a new
 * biota only at creation, so the weenie-side fix attunes only sigils minted AFTER it applies. Every
 * Toa Sigil already sitting in a player's inventory or bank is a biota row that was never given an
 * Attuned property and stays fully mailable/tradeable/vault-depositable unless swept here.
 *
 * Schema, confirmed against Source/ACE.Database/Models/Shard/ShardDbContext.cs before writing this
 * statement (not guessed): `biota` (id, weenie_Class_Id, ...) identifies an instance by the wcid it
 * was created from (ShardDbContext.cs:138,144 - same columns Database/Updates/Shard/
 * 2026-09-18-04-ToaSigil-Stackable.sql already keys its own sweep of this exact wcid on).
 * `biota_properties_int` (object_Id, type, value), primary key (object_Id, type), FK object_Id ->
 * biota.id (ShardDbContext.cs:774-789) is where PropertyInt.Attuned (114) rows live for an
 * instance - the biota-side counterpart of `weenie_properties_int` on the weenie side.
 *
 * This is a STANDALONE unit, independent of the weenie-side fix: applying only this file against a
 * shard that has not yet picked up the new weenie row is harmless (existing sigils become Attuned;
 * newly-minted ones stay whatever the currently-applied weenie row says), and applying only the
 * weenie-side change without this file is also valid (new sigils are Attuned, existing stacks stay
 * grandfathered exactly as documented in the weenie file's header). Skip this file entirely if the
 * repo owner has not separately approved a shard sweep.
 *
 * INSERT ... SELECT ... ON DUPLICATE KEY UPDATE rather than a plain INSERT: a biota that was
 * resaved after some earlier partial run, or that picked up an Attuned=0 row from an unrelated
 * path, is corrected to 1 instead of raising a duplicate-key error. Idempotent: rerunning after the
 * first successful pass matches the same rows and rewrites the same value, once per object_Id per
 * run - no accumulation, no error, on a shard that already has none, some, or all rows fixed. */

INSERT INTO biota_properties_int (object_Id, type, value)
SELECT b.id, 114, 1
FROM biota b
WHERE b.weenie_Class_Id = 1005490
ON DUPLICATE KEY UPDATE value = 1;
