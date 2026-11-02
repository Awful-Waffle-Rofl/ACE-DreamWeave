/* Knot items that already exist (owner ruling 2026-10-30): bring existing Patterns, Fibers, Knots and Knotting
   Pouches in line with their weenie templates in Content/sql/weenies/1007000..1007003.

   Two template changes reach only items created FROM NOW ON, because an existing item was persisted as a biota
   with its own copy of the template's property rows, and the shard loads a biota's properties from ace_shard,
   not from ace_world:
   - #1889 rewrote the appraisal text (PropertyString 16 LongDesc on all four, 14 Use on the Pattern) for the
     Pattern-fill mechanic. Old instances still tell the player to bring Fibers to Maelis or to use a Pattern on a
     Knot.
   - 2026-10-30 gave the Pattern the Ancient Falatacot Trinket icon (PropertyDataId 8 Icon = 0x06006582) over the
     retail rare purple underlay (PropertyDataId 52 IconUnderlay = 0x06005B0C).

   Pattern function needs no migration: an old Pattern already carries its PinnacleKey, and the fill reads
   KnotFiberCount and KnotFiberLevelSum as 0 when absent. Instance names are untouched because code owns them
   ("Fiber (Level N)", "Pattern: <pinnacle>", "Knot of the <pinnacle> (Level N)").

   Idempotent with no ledger beyond the filename the updater already records: the UPDATEs set fixed values, and
   the underlay insert upserts on the (object_Id, type) primary key. Scoped to wcids 1007000..1007003 only.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's
   saves would write the old rows back. Ship the image and the weenie content together. */

UPDATE `biota_properties_d_i_d` bd INNER JOIN `biota` b ON b.`id` = bd.`object_Id`
SET bd.`value` = 0x06006582
WHERE bd.`type` = 8 AND b.`weenie_Class_Id` = 1007003;

INSERT INTO `biota_properties_d_i_d` (`object_Id`, `type`, `value`)
SELECT b.`id`, 52, 0x06005B0C FROM `biota` b WHERE b.`weenie_Class_Id` = 1007003
ON DUPLICATE KEY UPDATE `value` = 0x06005B0C;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Double-click it, then click a Fiber to weave that Fiber in. Ten Fibers complete the Pattern into a Knot of its pinnacle.'
WHERE bs.`type` = 14 AND b.`weenie_Class_Id` = 1007003;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = CASE b.`weenie_Class_Id`
    WHEN 1007000 THEN 'A strand of Weave caught from a cleared Thread. Use a Pattern on it to weave it in; ten Fibers complete the Pattern into a Knot.'
    WHEN 1007001 THEN 'Ten Fibers drawn tight around a Pattern. Use it to enter the pinnacle its Pattern named.'
    WHEN 1007002 THEN 'A pouch with ten slots, each shaped for a Fiber. It holds your Fibers until a Pattern takes them.'
    WHEN 1007003 THEN 'A pattern for tying a Knot toward one particular pinnacle. Each Fiber woven into it draws the Knot closer.'
END
WHERE bs.`type` = 16 AND b.`weenie_Class_Id` BETWEEN 1007000 AND 1007003;
