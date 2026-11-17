-- @owner: frays
-- @owns: wcid:2000171
-- @requires-restart: no
--
-- Frays boon collection: rank 2 to 3.

DELETE FROM `weenie` WHERE `class_Id` = 2000171;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000171, 'dw-sigil-greater', 51, '2026-11-03 01:39:12') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000171,   1,        128) /* ItemType - Misc */
     , (2000171,   5,          0) /* EncumbranceVal */
     , (2000171,  11,        100) /* MaxStackSize */
     , (2000171,  12,          1) /* StackSize */
     , (2000171,  13,          0) /* StackUnitEncumbrance */
     , (2000171,  15,       8000) /* StackUnitValue */
     , (2000171,  16,          8) /* ItemUseable - Contained */
     , (2000171,  19,       8000) /* Value */
     , (2000171,  33,          1) /* Bonded - Bonded */
     , (2000171,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000171, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000171,  23, True ) /* DestroyOnSell */
     , (2000171,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000171,   1, 'Greater Sigil') /* Name */
     , (2000171,  14, 'Type /fray sigil <boon> to raise a boon in your collection from rank 2 to rank 3. /fray collection shows your ranks.') /* Use */
     , (2000171,  16, 'Raises one boon in your Frays boon collection from rank 2 to rank 3 (each rank adds half its first value). Rare boons stop at rank 2.') /* LongDesc */
     , (2000171,  20, 'Greater Sigils') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000171,   1, 0x020010E7) /* Setup */
     , (2000171,   3, 0x20000014) /* SoundTable */
     , (2000171,   8, 0x060065C3) /* Icon */
     , (2000171,  22, 0x3400002B) /* PhysicsEffectTable */;
