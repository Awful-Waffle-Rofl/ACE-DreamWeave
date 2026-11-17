-- @owner: frays
-- @owns: wcid:2000170
-- @requires-restart: no
--
-- Frays boon collection: /fray sigil <boon> raises a collected boon from rank 1 to 2 (FraysCollection).

DELETE FROM `weenie` WHERE `class_Id` = 2000170;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000170, 'dw-sigil-lesser', 51, '2026-11-03 01:39:12') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000170,   1,        128) /* ItemType - Misc */
     , (2000170,   5,          0) /* EncumbranceVal */
     , (2000170,  11,        100) /* MaxStackSize */
     , (2000170,  12,          1) /* StackSize */
     , (2000170,  13,          0) /* StackUnitEncumbrance */
     , (2000170,  15,       3000) /* StackUnitValue */
     , (2000170,  16,          8) /* ItemUseable - Contained */
     , (2000170,  19,       3000) /* Value */
     , (2000170,  33,          1) /* Bonded - Bonded */
     , (2000170,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000170, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000170,  23, True ) /* DestroyOnSell */
     , (2000170,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000170,   1, 'Lesser Sigil') /* Name */
     , (2000170,  14, 'Type /fray sigil <boon> to raise a boon in your collection from rank 1 to rank 2. /fray collection shows your ranks.') /* Use */
     , (2000170,  16, 'Raises one boon in your Frays boon collection from rank 1 to rank 2 (each rank adds half its first value). Rare boons stop at rank 2.') /* LongDesc */
     , (2000170,  20, 'Lesser Sigils') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000170,   1, 0x020010E7) /* Setup */
     , (2000170,   3, 0x20000014) /* SoundTable */
     , (2000170,   8, 0x060065C2) /* Icon */
     , (2000170,  22, 0x3400002B) /* PhysicsEffectTable */;
