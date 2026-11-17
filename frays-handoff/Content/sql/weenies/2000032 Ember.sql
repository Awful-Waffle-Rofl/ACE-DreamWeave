-- @owner: frays
-- @owns: wcid:2000032
-- @requires-restart: no
--
-- Frays (UCM project) currency.

DELETE FROM `weenie` WHERE `class_Id` = 2000032;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000032, 'dw-ember', 51, '2026-10-29 15:47:30') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000032,   1,        128) /* ItemType - Misc */
     , (2000032,   5,          0) /* EncumbranceVal */
     , (2000032,  11,      25000) /* MaxStackSize */
     , (2000032,  12,          1) /* StackSize */
     , (2000032,  13,          0) /* StackUnitEncumbrance */
     , (2000032,  15,          0) /* StackUnitValue */
     , (2000032,  16,          1) /* ItemUseable - No */
     , (2000032,  19,          0) /* Value */
     , (2000032,  33,          1) /* Bonded - Bonded */
     , (2000032,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000032, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000032,  23, True ) /* DestroyOnSell */
     , (2000032,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000032,  39,     2.5) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000032,   1, 'Ember') /* Name */
     , (2000032,  14, 'Spend these with the Ember Trader in the Ember Isles village.') /* Use */
     , (2000032,  15, 'A warm, glowing ember carried out of a Fray campfire.') /* ShortDesc */
     , (2000032,  20, 'Embers') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000032,   1, 0x02000B8B) /* Setup */
     , (2000032,   3, 0x20000014) /* SoundTable */
     , (2000032,   8, 0x060023E7) /* Icon */
     , (2000032,  22, 0x3400002B) /* PhysicsEffectTable */;
