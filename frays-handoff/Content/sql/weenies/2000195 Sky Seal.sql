-- @owner: frays
-- @owns: wcid:2000195
-- @requires-restart: no
--
-- Frays: a bonded currency.

DELETE FROM `weenie` WHERE `class_Id` = 2000195;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000195, 'dw-sky-seal', 51, '2026-11-09 08:41:06') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000195,   1,        128) /* ItemType - Misc */
     , (2000195,   5,          0) /* EncumbranceVal */
     , (2000195,  11,      25000) /* MaxStackSize */
     , (2000195,  12,          1) /* StackSize */
     , (2000195,  13,          0) /* StackUnitEncumbrance */
     , (2000195,  15,          0) /* StackUnitValue */
     , (2000195,  16,          1) /* ItemUseable - No */
     , (2000195,  19,          0) /* Value */
     , (2000195,  33,          1) /* Bonded - Bonded */
     , (2000195,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000195, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000195,  23, True ) /* DestroyOnSell */
     , (2000195,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000195,  39,     2.5) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000195,   1, 'Sky Seal') /* Name */
     , (2000195,  14, 'Spend these with the Sky Steward on the Sky Fray plateau, for fire looks, dye colours, titles and trophies.') /* Use */
     , (2000195,  15, 'A warm, glowing ember carried out of a Fray campfire.') /* ShortDesc */
     , (2000195,  16, 'A seal of the high Weave, given to every member of a group camp when its Snarl falls.') /* LongDesc */
     , (2000195,  20, 'Sky Seals') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000195,   1, 0x02000B8B) /* Setup */
     , (2000195,   3, 0x20000014) /* SoundTable */
     , (2000195,   8, 0x060023E7) /* Icon */
     , (2000195,  22, 0x3400002B) /* PhysicsEffectTable */;
