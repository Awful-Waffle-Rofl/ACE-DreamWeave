-- @owner: frays
-- @owns: wcid:2000204
-- @requires-restart: no
--
-- Frays: sold by the Sky Steward (30 Sky Seals). Using it gives 2 Kindling.

DELETE FROM `weenie` WHERE `class_Id` = 2000204;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000204, 'dw-sky-kindling-bundle', 51, '2026-11-15 15:12:36') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000204,   1,        128) /* ItemType - Misc */
     , (2000204,   5,          0) /* EncumbranceVal */
     , (2000204,  11,        100) /* MaxStackSize */
     , (2000204,  12,          1) /* StackSize */
     , (2000204,  13,          0) /* StackUnitEncumbrance */
     , (2000204,  15,         60) /* StackUnitValue */
     , (2000204,  16,          8) /* ItemUseable - Contained */
     , (2000204,  19,         60) /* Value */
     , (2000204,  33,          1) /* Bonded - Bonded */
     , (2000204,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000204, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000204,  23, True ) /* DestroyOnSell */
     , (2000204,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000204,   1, 'Sky Kindling Bundle') /* Name */
     , (2000204,  14, 'Use it to open it: 2 Kindling for your companions.') /* Use */
     , (2000204,  16, 'Two sticks of Kindling wrapped in sky cloth, from the Sky Steward.') /* LongDesc */
     , (2000204,  20, 'Sky Kindling Bundles') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000204,   1, 0x020010E7) /* Setup */
     , (2000204,   3, 0x20000014) /* SoundTable */
     , (2000204,   8, 0x0600192F) /* Icon */
     , (2000204,  22, 0x3400002B) /* PhysicsEffectTable */;
