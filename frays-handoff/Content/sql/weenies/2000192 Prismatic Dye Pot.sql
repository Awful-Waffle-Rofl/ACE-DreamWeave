-- @owner: frays
-- @owns: wcid:2000192
-- @requires-restart: no
--
-- Custom dye (ACE.Server.Dye, CUSTOM-DYE.md): load a colour with /dye <colour> [slot], then use the pot on your own armour or clothing (not weapons, casters or shields).

DELETE FROM `weenie` WHERE `class_Id` = 2000192;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000192, 'dw-prismatic-dye-pot', 44, '2026-11-15 16:34:30') /* CraftTool */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000192,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000192,   5,         50) /* EncumbranceVal */
     , (2000192,   8,         50) /* Mass */
     , (2000192,  11,          1) /* MaxStackSize */
     , (2000192,  12,          1) /* StackSize */
     , (2000192,  16,     786440) /* ItemUseable - 786440 */
     , (2000192,  19,       4000) /* Value */
     , (2000192,  91,         15) /* MaxStructure */
     , (2000192,  92,         15) /* Structure */
     , (2000192,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000192,  94,          6) /* TargetType - Vestements */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000192,  11, True ) /* IgnoreCollisions */
     , (2000192,  13, True ) /* Ethereal */
     , (2000192,  14, True ) /* GravityStatus */
     , (2000192,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000192,   1, 'Prismatic Dye Pot') /* Name */
     , (2000192,  14, 'Type /dye <colour> to fill the pot, then use it on armour or clothing you carry or wear. /dye list shows the colours. /dye original removes dye for free.') /* Use */
     , (2000192,  16, 'A pot of shifting, many-coloured dye. Speak a colour over it and the dye takes that hue. Its first dye locks it to that colour; it holds 15 dyes. Add a slot number to colour only part of an item, for example /dye navy 2; /dye info shows the slots of the item you last examined.') /* LongDesc */
     , (2000192,  20, 'Prismatic Dye Pots') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000192,   1, 0x02000911) /* Setup */
     , (2000192,   3, 0x20000014) /* SoundTable */
     , (2000192,   6, 0x04000BEF) /* PaletteBase */
     , (2000192,   8, 0x06005B16) /* Icon */
     , (2000192,  22, 0x3400002B) /* PhysicsEffectTable */
     , (2000192,  52, 0x0600536E) /* IconUnderlay */;
