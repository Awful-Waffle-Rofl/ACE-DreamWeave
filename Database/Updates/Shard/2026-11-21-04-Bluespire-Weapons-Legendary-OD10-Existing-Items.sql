/* Bluespire base weapons (wcids 1005450-1005457) and their three binding stones (1005460 Blood-Thirst Stone,
   1005461 Spirit-Thirst Stone, 1005493 Defender Stone) move from Epic to Legendary, and the weapons take OD+10
   plus ten damage tinkers baked in (they are TinkerLocked, so players can never apply those tinkers). The same
   PR changes the weenie templates in Content/sql/weenies and the stone recipes in Content/sql/recipes, which
   fixes every item created FROM NOW ON. An item that already exists was persisted as a biota with its own copy
   of those rows, and the shard loads a biota from the shard database, not from the world database, so existing
   items would keep the old values until the rows below are moved.

   TEMPLATE VALUES THIS FILE CARRIES (each one equals the new weenie row; pinned by
   Source/ACE.Server.Tests/BluespireLegendaryOd10MigrationTests.cs):
     Damage (PropertyInt 44):               1005454 Waaika 66, 1005455 Tewhate 84, 1005456 Hoeroa 67, 1005457 Taiaha 55
     ElementalDamageBonus (PropertyInt 204): 1005450 Elari Bow 22
     DamageMod (PropertyFloat 63):          1005450 Elari Bow 2.8
     ElementalDamageMod (PropertyFloat 152): 1005451, 1005452, 1005453 casters 1.31 (template change in 4c5060ae2)
     DamageType (PropertyInt 45):           1005453 Awakener 16 Fire -> 128 Health (template change in 4c5060ae2)
   The numeric updates do NOT guard on an exact old value: a weapon may carry tinker gains from before TinkerLock
   (2026-09-27) or a value from an older template, so each sets the new template value only WHERE the current
   value is below it (floats with a 1e-6 epsilon). A value already at or above the new one is left alone, and a
   second run finds nothing to change.

   SPELL MAP (old -> new; ids, power and magnitude read this session from the world database's spell table and
   the client_portal.dat SpellTable through ACE.DatLoader; every pair shares its SpellCategory, its
   stat_Mod_Type and stat_Mod_Key, its MetaSpellType and its degrade fields, so only the id, the power level and
   the magnitude differ):
     3963 Epic Coordination  -> 6103 Legendary Coordination   CoordinationIncrease 267, power 25 -> 35, +25 -> +35
     3964 Epic Focus         -> 6105 Legendary Focus          FocusIncrease 269,        power 25 -> 35, +25 -> +35
     3965 Epic Strength      -> 6107 Legendary Strength       StrengthIncrease 261,     power 25 -> 35, +25 -> +35
     4019 Epic Quickness     -> 6106 Legendary Quickness      QuicknessIncrease 265,    power 25 -> 35, +25 -> +35
     4227 Epic Willpower     -> 6101 Legendary Willpower      SelfIncrease 271,         power 25 -> 35, +25 -> +35
     4661 Epic Blood Thirst  -> 6089 Legendary Blood Thirst   MaxDamageRaising 323,     power 5 -> 10,  +7 -> +10
     4670 Epic Spirit Thirst -> 6098 Legendary Spirit Thirst  MaxDamageRaising 323,     power 4 -> 7,   +0.05 -> +0.07
     4663 Epic Defender      -> 6091 Legendary Defender       ExtraDefenseModRaising 329, power 6 -> 9, +0.07 -> +0.09
   The first five are the weapons' own template cantrips. The last three are not in any template: a stone adds
   them to the weapon's own biota spell book through recipe_mods_int AddSpell (RecipeManager.ModifyInt's AddSpell
   case calls Biota.GetOrAddKnownSpell), so they exist only on weapons that were already bound.

   STATEMENTS 1-2, SPELL BOOK. biota_properties_spell_book is keyed (object_Id, spell)
   (Source/ACE.Database/Models/Shard/ShardDbContext.cs, BiotaPropertiesSpellBook HasKey; Database/Base/
   ShardBase.sql PRIMARY KEY (`object_Id`,`spell`)). Statement 1 deletes an old-id row only where the same biota
   already holds the new id, so statement 2's in-place rename can never collide; statement 2 renames each
   remaining old-id row and keeps its probability (the Hoeroa 1005456 rows stay at probability 0).

   STATEMENTS 3-4, ACTIVE ENCHANTMENTS. Why the registry has to be rewritten rather than left for the server:
   - An equipped item's spells are cast ONCE, at equip: TryEquipObjectWithNetworking calls TryActivateSpells
     (Source/ACE.Server/WorldObjects/Player_Inventory.cs:470-471), which walks the item's CURRENT spell book
     (:540) and calls CreateItemSpell per spell (:548). Login does NOT recast: the Player constructor loads worn
     items through AddBiotasToEquippedObjects (Player.cs:194 -> Creature_Equipment.cs:142-157), which only
     registers them, and the resulting enchantments persist as -1-duration registry rows (EnchantmentManager.cs
     BuildEntry, :303-307).
   - Unequip removes ONLY the spells in the item's CURRENT spell book: TryDequipObjectWithBroadcasting iterates
     Biota.PropertiesSpellBook (Creature_Equipment.cs:573-581) and RemoveItemSpell looks each one up by
     (spell id, item guid) (Creature_Magic.cs:157). Mana depletion does the same (Player_Tick.cs:1339-1340).
     So once statement 2 renames the spell book, an Epic row cast by a worn weapon is never matched again.
   - The login audit does not catch it either: AuditItemSpells (Player_Spells.cs:571-626, called from
     Player_Networking.cs:182) dispels a -1-duration row only when its caster is NOT equipped (:585-595); it never
     compares a row against the caster's spell book. A worn weapon would therefore keep its Epic effect, and on
     unequip leave the Epic row behind until the next login's audit.
   Where the rows live (both keyed by caster_Object_Id = the weapon's guid, which is its biota id):
   - The five attribute cantrips are CreatureEnchantment, so CreateItemSpell casts them on the WEARER
     (Creature_Magic.cs:117-120): object_Id = the player, caster_Object_Id = the weapon.
   - The three stone spells are ItemEnchantment with a category outside Spell.HasItemCategory (Spell.cs:337-353)
     and are not SelfTargeted, so CreateItemSpell casts them on the ITEM (Creature_Magic.cs:123-128, then
     WorldObject_Magic.cs:374-375): object_Id = caster_Object_Id = the weapon.
   An enchantment row is built from the spell as EnchantmentCategory = MetaSpellType, SpellCategory, PowerLevel,
   DegradeModifier/Limit, StatModType (| Beneficial), StatModKey and StatModValue (EnchantmentManager.cs:275-328).
   Old and new share every one of those except the id, the power level and the magnitude, so statement 4
   rewrites exactly those three columns on each row a Bluespire weapon cast, wherever it lives. The result is
   byte-identical to the row a fresh equip of the upgraded weapon would build, which leaves every wearer with the
   Legendary effect at the next login and no orphaned Epic row, with no server code change. The registry is keyed
   (object_Id, spell_Id, caster_Object_Id, layer_Id) with a UNIQUE (object_Id, spell_Id, layer_Id)
   (ShardDbContext.cs, BiotaPropertiesEnchantmentRegistry; ShardBase.sql). Layers are unique within a category
   on one object (AddEnchantmentResult.cs:134-135, NextLayerId = top + 1), and old and new share the category,
   so a collision should not occur; statement 3 still deletes an old-id row whose renamed key would collide, so
   statement 4 cannot fail on a duplicate key and block every later script. Statement 4 is a single-table UPDATE, so its
   assignments run left to right: power_Level and stat_Mod_Value are computed from the row's old spell_Id before
   spell_Id itself is rewritten. Its WHERE selects only rows still on an old id, so the new-id branches in the
   power and magnitude CASE lists are never reached, and a row that is already on a new id is left exactly as it
   is (never normalized). A rerun finds no old-id rows and changes nothing.

   STATEMENTS 5-12, VALUES. Damage, ElementalDamageBonus, DamageMod, ElementalDamageMod and DamageType, as listed
   above, scoped by weenie_Class_Id.

   STATEMENTS 13-18, STONE TEXT. Stones still in inventories carry their own Use (14) and LongDesc (16) strings;
   each is set to the exact new template text.

   Every statement is scoped by weenie_Class_Id (of the row's owner, or of the enchantment's caster) and is
   idempotent: a second run changes nothing, so the updater's filename ledger is the only bookkeeping needed.

   Applies at the next server boot on the new image; do NOT apply it to a live shard, because a running server's
   saves would overwrite it (and a logged-in wearer's in-memory registry would write the Epic rows back). Ship
   the image and the content together (deploy all prod), or dispatch the content deploy immediately after the
   image boot; until both land, existing and newly created items disagree. */

DELETE sb FROM `biota_properties_spell_book` sb
INNER JOIN `biota` b ON b.`id` = sb.`object_Id`
INNER JOIN `biota_properties_spell_book` keep ON keep.`object_Id` = sb.`object_Id`
  AND keep.`spell` = CASE sb.`spell` WHEN 3963 THEN 6103 WHEN 3964 THEN 6105 WHEN 3965 THEN 6107 WHEN 4019 THEN 6106 WHEN 4227 THEN 6101 WHEN 4661 THEN 6089 WHEN 4670 THEN 6098 WHEN 4663 THEN 6091 END
WHERE b.`weenie_Class_Id` BETWEEN 1005450 AND 1005457
  AND sb.`spell` IN (3963, 3964, 3965, 4019, 4227, 4661, 4670, 4663);

UPDATE `biota_properties_spell_book` sb
SET sb.`spell` = CASE sb.`spell` WHEN 3963 THEN 6103 WHEN 3964 THEN 6105 WHEN 3965 THEN 6107 WHEN 4019 THEN 6106 WHEN 4227 THEN 6101 WHEN 4661 THEN 6089 WHEN 4670 THEN 6098 WHEN 4663 THEN 6091 ELSE sb.`spell` END
WHERE sb.`spell` IN (3963, 3964, 3965, 4019, 4227, 4661, 4670, 4663)
  AND sb.`object_Id` IN (SELECT b.`id` FROM `biota` b WHERE b.`weenie_Class_Id` BETWEEN 1005450 AND 1005457);

DELETE er FROM `biota_properties_enchantment_registry` er
INNER JOIN `biota` c ON c.`id` = er.`caster_Object_Id`
INNER JOIN `biota_properties_enchantment_registry` keep ON keep.`object_Id` = er.`object_Id` AND keep.`layer_Id` = er.`layer_Id`
  AND keep.`spell_Id` = CASE er.`spell_Id` WHEN 3963 THEN 6103 WHEN 3964 THEN 6105 WHEN 3965 THEN 6107 WHEN 4019 THEN 6106 WHEN 4227 THEN 6101 WHEN 4661 THEN 6089 WHEN 4670 THEN 6098 WHEN 4663 THEN 6091 END
WHERE c.`weenie_Class_Id` BETWEEN 1005450 AND 1005457
  AND er.`spell_Id` IN (3963, 3964, 3965, 4019, 4227, 4661, 4670, 4663);

UPDATE `biota_properties_enchantment_registry` er
SET er.`power_Level` = CASE er.`spell_Id` WHEN 3963 THEN 35 WHEN 6103 THEN 35 WHEN 3964 THEN 35 WHEN 6105 THEN 35 WHEN 3965 THEN 35 WHEN 6107 THEN 35 WHEN 4019 THEN 35 WHEN 6106 THEN 35 WHEN 4227 THEN 35 WHEN 6101 THEN 35 WHEN 4661 THEN 10 WHEN 6089 THEN 10 WHEN 4670 THEN 7 WHEN 6098 THEN 7 WHEN 4663 THEN 9 WHEN 6091 THEN 9 ELSE er.`power_Level` END,
    er.`stat_Mod_Value` = CASE er.`spell_Id` WHEN 3963 THEN 35 WHEN 6103 THEN 35 WHEN 3964 THEN 35 WHEN 6105 THEN 35 WHEN 3965 THEN 35 WHEN 6107 THEN 35 WHEN 4019 THEN 35 WHEN 6106 THEN 35 WHEN 4227 THEN 35 WHEN 6101 THEN 35 WHEN 4661 THEN 10 WHEN 6089 THEN 10 WHEN 4670 THEN 0.07 WHEN 6098 THEN 0.07 WHEN 4663 THEN 0.09 WHEN 6091 THEN 0.09 ELSE er.`stat_Mod_Value` END,
    er.`spell_Id` = CASE er.`spell_Id` WHEN 3963 THEN 6103 WHEN 3964 THEN 6105 WHEN 3965 THEN 6107 WHEN 4019 THEN 6106 WHEN 4227 THEN 6101 WHEN 4661 THEN 6089 WHEN 4670 THEN 6098 WHEN 4663 THEN 6091 ELSE er.`spell_Id` END
WHERE er.`spell_Id` IN (3963, 3964, 3965, 4019, 4227, 4661, 4670, 4663)
  AND er.`caster_Object_Id` IN (SELECT c.`id` FROM `biota` c WHERE c.`weenie_Class_Id` BETWEEN 1005450 AND 1005457);

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 66
WHERE bi.`type` = 44 AND b.`weenie_Class_Id` = 1005454 AND bi.`value` < 66;

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 84
WHERE bi.`type` = 44 AND b.`weenie_Class_Id` = 1005455 AND bi.`value` < 84;

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 67
WHERE bi.`type` = 44 AND b.`weenie_Class_Id` = 1005456 AND bi.`value` < 67;

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 55
WHERE bi.`type` = 44 AND b.`weenie_Class_Id` = 1005457 AND bi.`value` < 55;

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 22
WHERE bi.`type` = 204 AND b.`weenie_Class_Id` = 1005450 AND bi.`value` < 22;

UPDATE `biota_properties_float` bf INNER JOIN `biota` b ON b.`id` = bf.`object_Id`
SET bf.`value` = 2.8
WHERE bf.`type` = 63 AND b.`weenie_Class_Id` = 1005450 AND bf.`value` < 2.8 - 1e-6;

UPDATE `biota_properties_float` bf INNER JOIN `biota` b ON b.`id` = bf.`object_Id`
SET bf.`value` = 1.31
WHERE bf.`type` = 152 AND b.`weenie_Class_Id` BETWEEN 1005451 AND 1005453 AND bf.`value` < 1.31 - 1e-6;

UPDATE `biota_properties_int` bi INNER JOIN `biota` b ON b.`id` = bi.`object_Id`
SET bi.`value` = 128
WHERE bi.`type` = 45 AND b.`weenie_Class_Id` = 1005453 AND bi.`value` = 16;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Use this stone on your melee or missile weapon to bind the Legendary Blood Thirst into it. The stone is spent in the working, and once bound it does not come loose.'
WHERE bs.`type` = 14 AND b.`weenie_Class_Id` = 1005460;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Cut from stone the Hea drummers set aside for binding-work, marked with a single drum-line and nothing else. Bound into a melee or missile weapon, it sets the Legendary Blood Thirst into the weapon for good. The binding is permanent but fragile, and cannot be layered onto a weapon carrying another binding of its kind. It will only take to a weapon sold by Coren Ashtide.'
WHERE bs.`type` = 16 AND b.`weenie_Class_Id` = 1005460;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Use this stone on your caster implement to bind the Legendary Spirit Thirst into it. The stone is spent in the working, and once bound it does not come loose.'
WHERE bs.`type` = 14 AND b.`weenie_Class_Id` = 1005461;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Cut from stone the Hea drummers set aside for binding-work, marked with a single drum-line and nothing else. Bound into a caster implement, it sets the Legendary Spirit Thirst into the weapon for good. The binding is permanent but fragile, and cannot be layered onto a weapon carrying another binding of its kind. It will only take to a weapon sold by Coren Ashtide.'
WHERE bs.`type` = 16 AND b.`weenie_Class_Id` = 1005461;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Use this stone on your melee, missile or caster weapon to bind the Legendary Defender into it. The stone is spent in the working, and once bound it does not come loose.'
WHERE bs.`type` = 14 AND b.`weenie_Class_Id` = 1005493;

UPDATE `biota_properties_string` bs INNER JOIN `biota` b ON b.`id` = bs.`object_Id`
SET bs.`value` = 'Cut from stone the Hea drummers set aside for binding-work, marked with a single drum-line and nothing else. Bound into a weapon, it sets the Legendary Defender into the weapon for good. The binding is permanent but fragile, and cannot be layered onto a weapon carrying another binding of its kind. It will only take to a weapon sold by Coren Ashtide.'
WHERE bs.`type` = 16 AND b.`weenie_Class_Id` = 1005493;
