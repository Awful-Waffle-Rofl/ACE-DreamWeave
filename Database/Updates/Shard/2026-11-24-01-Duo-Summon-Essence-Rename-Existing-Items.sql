/* Duo Summon Gem (wcid 1007741) is renamed Duo Summon Essence (owner ruling 2026-11-24: the RynthAi client bot
   treats an item as a summoning device when its name contains "Essence").

   The same PR changes the weenie template in Content/sql/weenies/1007741, which fixes every item created FROM NOW
   ON. An existing item was persisted as a biota with its own copy of the template's property rows (a biota is built
   from a full copy of the weenie's rows, Source/ACE.Entity/Adapter/WeenieConverter.cs:33-34), and the shard loads
   a biota's properties from ace_shard, not from ace_world. So every Duo Summon Gem a player already owns keeps its
   old Name (PropertyString 1) until the row is written here.

   Only a row that still carries the exact template value is rewritten: a Name, Use (14) or ShortDesc (15) a player
   or code changed is never touched. LongDesc (16) is not touched either; SummonItemDescription.Compose rewrites it
   on every absorb. The Summon Gem Hammer (1007742) keeps its name; only its Use text (14) mentioned the old name
   and is rewritten the same exact-match way.

   Idempotent with no ledger beyond the filename the updater already records: after the first run no row matches the
   old text, so a second run changes nothing.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's
   saves would write the old rows back. Ship the image and the weenie content together. */

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Duo Summon Essence'
WHERE bs.`type` = 1 AND b.`weenie_Class_Id` = 1007741 AND bs.`value` = 'Duo Summon Gem';

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Use this essence on a summon to absorb it. Absorb two and it summons both pets together.'
WHERE bs.`type` = 14 AND b.`weenie_Class_Id` = 1007741
  AND bs.`value` = 'Use this gem on a summon to absorb it. Absorb two and it summons both pets together.';

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'An empty bone-cased essence that waits to hold two summons.'
WHERE bs.`type` = 15 AND b.`weenie_Class_Id` = 1007741
  AND bs.`value` = 'An empty bone-cased gem that waits to hold two summons.';

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Use it on a bound Duo Summon Essence to break the essence and get both summons back.'
WHERE bs.`type` = 14 AND b.`weenie_Class_Id` = 1007742
  AND bs.`value` = 'Use it on a bound Duo Summon Gem to break the gem and get both summons back.';
