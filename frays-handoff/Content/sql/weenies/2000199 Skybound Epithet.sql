-- @owner: frays
-- @owns: wcid:2000199
-- @requires-restart: no
--
-- Frays: an unlock token sold by the Sky Steward (50 Sky Seals).

DELETE FROM `weenie` WHERE `class_Id` = 2000199;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000199, 'dw-skybound-epithet', 51, '2026-11-09 08:41:06') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000199,   1,        128) /* ItemType - Misc */
     , (2000199,   5,          0) /* EncumbranceVal */
     , (2000199,  11,        100) /* MaxStackSize */
     , (2000199,  12,          1) /* StackSize */
     , (2000199,  13,          0) /* StackUnitEncumbrance */
     , (2000199,  15,        100) /* StackUnitValue */
     , (2000199,  16,          8) /* ItemUseable - Contained */
     , (2000199,  19,        100) /* Value */
     , (2000199,  33,          1) /* Bonded - Bonded */
     , (2000199,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000199, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000199,  23, True ) /* DestroyOnSell */
     , (2000199,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000199,   1, 'Skybound Epithet') /* Name */
     , (2000199,  14, 'Use it to add the Epithet the Skybound to your collection for good.') /* Use */
     , (2000199,  16, 'A title for those who hold the Sky Fray together.') /* LongDesc */
     , (2000199,  20, 'Skybound Epithets') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000199,   1, 0x020010E7) /* Setup */
     , (2000199,   3, 0x20000014) /* SoundTable */
     , (2000199,   8, 0x060065C2) /* Icon */
     , (2000199,  22, 0x3400002B) /* PhysicsEffectTable */;
