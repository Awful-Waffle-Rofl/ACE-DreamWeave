/* Drift Life Isparian Wand (1007427) and Drift Nether Isparian Wand (1007428) drop PropertyInt 36
   ResistMagic 9999. The value was inherited from their retail sources (46395, 46397), but
   WorldObject.IsEnchantable is (ResistMagic ?? 0) < 9999, and an unenchantable weapon never adds the
   wielder's enchantments: GetWeaponMeleeDefenseModifier skips the wielder's defense mod, and the
   mana conversion and elemental damage paths skip the wielder's Hermetic Link and Spirit Drinker. So
   these two wands lost their own Aura of Incantation of Defender / Spirit Drinker / Hermetic Link (cast
   on the wielder) plus any self-cast buffs, while the four Drift Atlan wands, which never carried the
   row, kept them.

   The same PR removes the row from the weenie templates, which fixes every wand created from now on.
   A wand that already exists is a biota with its own copy of the row, and the shard loads properties
   from ace_shard, so this delete is what fixes those. Guarded on wcid, type and the exact value; a
   second run finds nothing to delete.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a
   running server's saves would overwrite it.

   Ship the weenie content with the image (deploy all prod), or apply the content FIRST. A wand crafted
   between the image boot and the content apply is born from the old template with ResistMagic 9999,
   and this script, already recorded as applied, will not run again; delete that row by hand or add a
   follow-up migration. */

DELETE bi FROM `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
WHERE bi.`type` = 36 AND bi.`value` = 9999 AND b.`weenie_Class_Id` IN (1007427, 1007428);
