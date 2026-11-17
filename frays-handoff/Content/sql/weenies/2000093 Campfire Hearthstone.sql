-- @owner: frays
-- @owns: wcid:2000093
-- @requires-restart: no
--
-- Frays travel: an unlimited-use return to the campfire you hold (the /frays recall cooldown).

DELETE FROM `weenie` WHERE `class_Id` = 2000093;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000093, 'dw-campfire-hearthstone', 38, '2026-10-31 23:43:29') /* Gem */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000093,   1,       2048) /* ItemType - Gem */
     , (2000093,   3,         82) /* PaletteTemplate - PinkPurple */
     , (2000093,   5,          0) /* EncumbranceVal */
     , (2000093,   8,         10) /* Mass */
     , (2000093,   9,          0) /* ValidLocations - None */
     , (2000093,  11,          1) /* MaxStackSize */
     , (2000093,  12,          1) /* StackSize */
     , (2000093,  13,         10) /* StackUnitEncumbrance */
     , (2000093,  14,         10) /* StackUnitMass */
     , (2000093,  15,         10) /* StackUnitValue */
     , (2000093,  16,          8) /* ItemUseable - Contained */
     , (2000093,  18,          1) /* UiEffects - Magical */
     , (2000093,  19,          0) /* Value */
     , (2000093,  33,          1) /* Bonded - Bonded */
     , (2000093,  93,       3092) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity, LightingOn */
     , (2000093,  94,         16) /* TargetType - Creature */
     , (2000093, 106,        210) /* ItemSpellcraft */
     , (2000093, 107,        250) /* ItemCurMana */
     , (2000093, 108,        250) /* ItemMaxMana */
     , (2000093, 109,         50) /* ItemDifficulty */
     , (2000093, 110,          0) /* ItemAllegianceRankLimit */
     , (2000093, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000093,  15, True ) /* LightsStatus */
     , (2000093,  22, True ) /* Inscribable */
     , (2000093,  23, True ) /* DestroyOnSell */
     , (2000093,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000093,   1, 'Campfire Hearthstone') /* Name */
     , (2000093,  16, 'A coal from a Fray campfire that never cools. Use it to return to the campfire you hold. It is never used up.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000093,   1, 0x02000921) /* Setup */
     , (2000093,   3, 0x20000014) /* SoundTable */
     , (2000093,   6, 0x04000BEF) /* PaletteBase */
     , (2000093,   7, 0x1000010B) /* ClothingBase */
     , (2000093,   8, 0x06001E11) /* Icon */
     , (2000093,  22, 0x3400002B) /* PhysicsEffectTable */
     , (2000093,  36, 0x0E000016) /* MutateFilter */;
