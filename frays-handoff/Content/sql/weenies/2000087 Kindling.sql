-- @owner: frays
-- @owns: wcid:2000087
-- @requires-restart: no
--
-- Frays: levels your active companion (250 experience; each collected effigy form is its own companion, FraysCompanions).

DELETE FROM `weenie` WHERE `class_Id` = 2000087;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000087, 'dw-kindling', 51, '2026-10-29 20:35:10') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000087,   1,        128) /* ItemType - Misc */
     , (2000087,   5,          0) /* EncumbranceVal */
     , (2000087,  11,        100) /* MaxStackSize */
     , (2000087,  12,          1) /* StackSize */
     , (2000087,  13,          0) /* StackUnitEncumbrance */
     , (2000087,  15,       1000) /* StackUnitValue */
     , (2000087,  16,          8) /* ItemUseable - Contained */
     , (2000087,  19,       1000) /* Value */
     , (2000087,  33,          1) /* Bonded - Bonded */
     , (2000087,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000087, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000087,  23, True ) /* DestroyOnSell */
     , (2000087,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000087,   1, 'Kindling') /* Name */
     , (2000087,  14, 'Feed it to your companion (use it) for 250 companion experience.') /* Use */
     , (2000087,  16, 'Dry wood that wakes a dream companion. Use it to feed your companion.') /* LongDesc */
     , (2000087,  20, 'Kindling') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000087,   1, 0x02000B8B) /* Setup */
     , (2000087,   3, 0x20000014) /* SoundTable */
     , (2000087,   8, 0x0600192F) /* Icon */
     , (2000087,  22, 0x3400002B) /* PhysicsEffectTable */;
