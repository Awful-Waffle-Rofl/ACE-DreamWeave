-- @owner: frays
-- @owns: wcid:2000198
-- @requires-restart: no
--
-- Frays: an unlock token sold by the Sky Steward (30 Sky Seals).

DELETE FROM `weenie` WHERE `class_Id` = 2000198;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000198, 'dw-sky-dye-charter', 51, '2026-11-09 08:41:06') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000198,   1,        128) /* ItemType - Misc */
     , (2000198,   5,          0) /* EncumbranceVal */
     , (2000198,  11,        100) /* MaxStackSize */
     , (2000198,  12,          1) /* StackSize */
     , (2000198,  13,          0) /* StackUnitEncumbrance */
     , (2000198,  15,         60) /* StackUnitValue */
     , (2000198,  16,          8) /* ItemUseable - Contained */
     , (2000198,  19,         60) /* Value */
     , (2000198,  33,          1) /* Bonded - Bonded */
     , (2000198,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000198, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000198,  23, True ) /* DestroyOnSell */
     , (2000198,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000198,   1, 'Sky Dye Charter') /* Name */
     , (2000198,  14, 'Use it to unlock the Sky dye colours for your Prismatic Dye Pots for good. /dye list shows them.') /* Use */
     , (2000198,  16, 'A charter of the Sky Fray dyers: pale sky, storm and dawn colours.') /* LongDesc */
     , (2000198,  20, 'Sky Dye Charters') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000198,   1, 0x020010E7) /* Setup */
     , (2000198,   3, 0x20000014) /* SoundTable */
     , (2000198,   8, 0x060065C2) /* Icon */
     , (2000198,  22, 0x3400002B) /* PhysicsEffectTable */;
