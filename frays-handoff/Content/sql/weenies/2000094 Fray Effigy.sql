-- @owner: frays
-- @owns: wcid:2000094
-- @requires-restart: no
--
-- Frays creature collection: a copy of the DreamWeave Beast Effigy (1003152), tradeable.

DELETE FROM `weenie` WHERE `class_Id` = 2000094;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000094, 'dw-fray-effigy', 38, '2026-11-02 22:58:08') /* Gem */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000094,   1,       2048) /* ItemType - Gem */
     , (2000094,   5,         10) /* EncumbranceVal */
     , (2000094,   8,         10) /* Mass */
     , (2000094,   9,          0) /* ValidLocations - None */
     , (2000094,  11,          1) /* MaxStackSize */
     , (2000094,  12,          1) /* StackSize */
     , (2000094,  13,         10) /* StackUnitEncumbrance */
     , (2000094,  14,         10) /* StackUnitMass */
     , (2000094,  15,         50) /* StackUnitValue */
     , (2000094,  16,          8) /* ItemUseable - Contained */
     , (2000094,  18,          1) /* UiEffects - Magical */
     , (2000094,  19,         50) /* Value */
     , (2000094,  91,        100) /* MaxStructure */
     , (2000094,  92,          0) /* Structure */
     , (2000094,  93,       3092) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity, LightingOn */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000094,  15, True ) /* LightsStatus */
     , (2000094,  63, True ) /* UnlimitedUse */
     , (2000094,  84, True ) /* IgnoreCloIcons */
     , (2000094, 9053, True ) /* MuleFormToken */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000094,   1, 'Fray Effigy') /* Name */
     , (2000094,  16, 'A creature of the Frays caught in a dream. Use it to record its family in your Fray Codex and to call it as a pet (/fray pet).') /* LongDesc */
     , (2000094,  20, 'Beast Effigies') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000094,   1, 0x02000921) /* Setup */
     , (2000094,   3, 0x20000014) /* SoundTable */
     , (2000094,   6, 0x04000BEF) /* PaletteBase */
     , (2000094,   7, 0x1000010B) /* ClothingBase */
     , (2000094,   8, 0x06005B6A) /* Icon */
     , (2000094,  22, 0x3400002B) /* PhysicsEffectTable */;
