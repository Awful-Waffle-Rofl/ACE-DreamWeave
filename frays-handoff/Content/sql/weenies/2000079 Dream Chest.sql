-- @owner: frays
-- @owns: wcid:2000079
-- @requires-restart: no
--
-- Frays: an Ember-bought chest with a prize table (stones, Stitches, Needles, Kindling, Embers, salvage, mana stones, decor, rarely a T8 item or a Snarl key).

DELETE FROM `weenie` WHERE `class_Id` = 2000079;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000079, 'dw-dream-chest', 51, '2026-10-29 20:35:09') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000079,   1,        128) /* ItemType - Misc */
     , (2000079,   5,          0) /* EncumbranceVal */
     , (2000079,  11,        100) /* MaxStackSize */
     , (2000079,  12,          1) /* StackSize */
     , (2000079,  13,          0) /* StackUnitEncumbrance */
     , (2000079,  15,       4000) /* StackUnitValue */
     , (2000079,  16,          8) /* ItemUseable - Contained */
     , (2000079,  19,       4000) /* Value */
     , (2000079,  33,          1) /* Bonded - Bonded */
     , (2000079,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000079, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000079,  23, True ) /* DestroyOnSell */
     , (2000079,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000079,   1, 'Dream Chest') /* Name */
     , (2000079,  14, 'Use it to open it.') /* Use */
     , (2000079,  16, 'A chest of dreams: stones, Stitches, Needles, Kindling, decor, salvage, mana stones and, rarely, a T8 item or a Snarl cache key.') /* LongDesc */
     , (2000079,  20, 'Dream Chests') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000079,   1, 0x0200007C) /* Setup */
     , (2000079,   3, 0x20000014) /* SoundTable */
     , (2000079,   8, 0x06001020) /* Icon */
     , (2000079,  22, 0x3400002B) /* PhysicsEffectTable */;
