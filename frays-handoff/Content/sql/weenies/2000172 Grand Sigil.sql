-- @owner: frays
-- @owns: wcid:2000172
-- @requires-restart: no
--
-- Frays boon collection: rank 3 to 4.

DELETE FROM `weenie` WHERE `class_Id` = 2000172;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000172, 'dw-sigil-grand', 51, '2026-11-03 01:39:12') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000172,   1,        128) /* ItemType - Misc */
     , (2000172,   5,          0) /* EncumbranceVal */
     , (2000172,  11,        100) /* MaxStackSize */
     , (2000172,  12,          1) /* StackSize */
     , (2000172,  13,          0) /* StackUnitEncumbrance */
     , (2000172,  15,      20000) /* StackUnitValue */
     , (2000172,  16,          8) /* ItemUseable - Contained */
     , (2000172,  19,      20000) /* Value */
     , (2000172,  33,          1) /* Bonded - Bonded */
     , (2000172,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000172, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000172,  23, True ) /* DestroyOnSell */
     , (2000172,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000172,   1, 'Grand Sigil') /* Name */
     , (2000172,  14, 'Type /fray sigil <boon> to raise a boon in your collection from rank 3 to rank 4. /fray collection shows your ranks.') /* Use */
     , (2000172,  16, 'Raises one boon in your Frays boon collection from rank 3 to rank 4 (each rank adds half its first value). Rare boons stop at rank 2.') /* LongDesc */
     , (2000172,  20, 'Grand Sigils') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000172,   1, 0x020010E7) /* Setup */
     , (2000172,   3, 0x20000014) /* SoundTable */
     , (2000172,   8, 0x060065C9) /* Icon */
     , (2000172,  22, 0x3400002B) /* PhysicsEffectTable */;
