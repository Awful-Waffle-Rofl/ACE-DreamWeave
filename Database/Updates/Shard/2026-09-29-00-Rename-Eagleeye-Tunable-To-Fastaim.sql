/* Eagle Eye (class ability id 24) was redefined in place as Fast Aim on 2026-09-29 (see
   Docs/ClassAbilities/AS-BUILT.md). The tunable that governs its magnitude was renamed to match:

     class_ability_eagleeye_percent_per_rank -> class_ability_fastaim_low_end_per_rank

   Same 0.10 default; only the key and its meaning changed (a flat % of effective missile attack
   skill before, a lift to the low end of the accuracy-mod range now - see PropertyManager.cs and
   Player.GetAccuracyMod).

   WHY THIS FILE EXISTS. PropertyManager.LoadDefaultProperties calls Modify* for every code default
   on every boot and marks the entry Modified, so the first background flush (every 5 minutes)
   writes every code default to ace_shard.config_properties_double as a row on that shard. From
   then on the persisted row wins over the code default, so a shard that already has a row for the
   OLD key keeps reading it under a key the code no longer looks up at all - simply renaming the
   `new Property<double>(...)` tuple in source never reaches that shard. This file moves the
   persisted row forward to the new key.

   RE-RUNNABLE, same DELETE-then-UPDATE key-rename idiom as
   Database/Updates/Shard/2026-09-06-01-Rename-Character-Loadout-To-Character-Facet.sql:
     - The DELETE removes the OLD-key row only when a NEW-key row ALREADY exists (collision-safe:
       the already-present target row wins, the stale source row is discarded rather than fought
       over).
     - The UPDATE then renames any remaining OLD-key row to the NEW key.
   On a second run the OLD key no longer exists in either branch (it was either renamed or deleted
   by the first run), so both statements match zero rows.

   HAND-TUNED ROWS SURVIVE UNCHANGED: this is a pure key rename, not a value change, so whatever
   value an operator had set under the old key (default or hand-tuned) carries over verbatim under
   the new key. A shard that never had the old key at all is untouched (both statements match zero
   rows).

   Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
   Database/Updates/*. */

DELETE t1 FROM `config_properties_double` t1
WHERE t1.`key` = 'class_ability_eagleeye_percent_per_rank'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_double`) t2 WHERE t2.`key` = 'class_ability_fastaim_low_end_per_rank');

UPDATE `config_properties_double`
SET `key` = 'class_ability_fastaim_low_end_per_rank'
WHERE `key` = 'class_ability_eagleeye_percent_per_rank';
