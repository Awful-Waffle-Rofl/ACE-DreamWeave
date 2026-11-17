-- @owner: frays
-- @owns: wcid:2000100
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000100;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000100, 'dw-fray-banner', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000100,   1,          4) /* ItemType - Clothing */
     , (2000100,   3,         61) /* PaletteTemplate - White */
     , (2000100,   4,          0) /* ClothingPriority - 0 */
     , (2000100,   5,         50) /* EncumbranceVal */
     , (2000100,   8,         50) /* Mass */
     , (2000100,   9,   16777216) /* ValidLocations - Held */
     , (2000100,  16,          1) /* ItemUseable - No */
     , (2000100,  19,        500) /* Value */
     , (2000100,  27,          1) /* ArmorType - Cloth */
     , (2000100,  28,          0) /* ArmorLevel */
     , (2000100,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000100, 150,        103) /* HookPlacement - Hook */
     , (2000100, 151,         24) /* HookType - Yard, Roof */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000100,   1, True ) /* Stuck */
     , (2000100,  19, False) /* Attackable */
     , (2000100,  22, True ) /* Inscribable */
     , (2000100,  23, True ) /* DestroyOnSell */
     , (2000100, 100, True ) /* Dyable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000100,  12,       0) /* Shade */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000100,   1, 'Fray Banner') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000100,   1, 0x02000CDB) /* Setup */
     , (2000100,   3, 0x20000014) /* SoundTable */
     , (2000100,   6, 0x04001379) /* PaletteBase */
     , (2000100,   7, 0x100003A7) /* ClothingBase */
     , (2000100,   8, 0x060023A8) /* Icon */
     , (2000100,  22, 0x3400002B) /* PhysicsEffectTable */;
