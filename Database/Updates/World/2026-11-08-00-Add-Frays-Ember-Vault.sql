-- Frays: the Ember Vault (WCID 2000153) and its loot, treasure_death 2000153 (10 tier 8 items, a Threads boss cache).
-- The code (FraysVault) reads this row; it is copied here from the content folder so the repo carries it.
-- Requires retail 48743 (Legendary Chest) and the Vault Key (2000152, content SQL). Safe to re-run.

-- Ember Vault, the locked chest (WCID 2000153, was Dream Chest): stands outside the Great Hall in the Fray village, an
-- upgraded version of the Arwic legendary chests with better rates on good items. A copy of retail 48743 Legendary
-- Chest: locked (no lockpicking), opened by a Dream Key (2000152), relocks and refills 2 minutes after opening. Its loot is
-- treasure_death 2000153 below: Legendary's tier 8 mixed gear (profile 22) with quality 0.6 (Legendary 0.3) and
-- magic items, always 10 to match a Threads boss cache (Legendary 4 to 5). Placed by the village tool (/frays admin piece add 2000153). Requires 48743.
-- The inventory Dream Chest item (2000079) is a different thing and keeps working. Safe to re-run.

DELETE FROM `weenie` WHERE `class_Id` = 2000153; -- child tables cascade
DELETE FROM `treasure_death` WHERE `treasure_Type` = 2000153;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
SELECT 2000153, 'dw-dream-chest-locked', `type`, NOW() FROM `weenie` WHERE `class_Id` = 48743;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`) SELECT 2000153, `type`, `value` FROM `weenie_properties_int` WHERE `object_Id` = 48743;
INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`) SELECT 2000153, `type`, `value` FROM `weenie_properties_bool` WHERE `object_Id` = 48743;
INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`) SELECT 2000153, `type`, `value` FROM `weenie_properties_float` WHERE `object_Id` = 48743;
INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`) SELECT 2000153, `type`, `value` FROM `weenie_properties_d_i_d` WHERE `object_Id` = 48743;
INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`) VALUES
 (2000153, 1, 'Ember Vault'),
 (2000153, 12, 'keychestdream'), /* LockCode: the Vault Key's KeyCode */
 (2000153, 14, 'Use a Vault Key on this chest to open it.'),
 (2000153, 16, 'A chest of the finest mixed gear, dreamt up by the Frays.');

-- The loot: Legendary's row (treasure_Type 2001) with better quality and about twice the items.
INSERT INTO `treasure_death` (`treasure_Type`, `tier`, `loot_Quality_Mod`, `unknown_Chances`, `item_Chance`, `item_Min_Amount`, `item_Max_Amount`, `item_Treasure_Type_Selection_Chances`, `magic_Item_Chance`, `magic_Item_Min_Amount`, `magic_Item_Max_Amount`, `magic_Item_Treasure_Type_Selection_Chances`, `mundane_Item_Chance`, `mundane_Item_Min_Amount`, `mundane_Item_Max_Amount`, `mundane_Item_Type_Selection_Chances`, `last_Modified`)
VALUES (2000153, 8, 0.6, 19, 0, 0, 0, 0, 100, 10, 10, 22, 100, 0, 0, 0, NOW());

INSERT INTO `weenie_properties_generator` (`object_Id`, `probability`, `weenie_Class_Id`, `delay`, `init_Create`, `max_Create`, `when_Create`, `where_Create`, `stack_Size`, `palette_Id`, `shade`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000153, -1, 2000153, 0, 1, 1, 2, 72, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0); /* Contain | Treasure: rolls treasure_death 2000153 */
