-- @owner: frays
-- @owns: wcid:2000197
-- @requires-restart: no
--
-- Frays: an unlock token sold by the Sky Steward (25 Sky Seals).

DELETE FROM `weenie` WHERE `class_Id` = 2000197;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000197, 'dw-skyfire-sparks', 51, '2026-11-09 08:41:06') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000197,   1,        128) /* ItemType - Misc */
     , (2000197,   5,          0) /* EncumbranceVal */
     , (2000197,  11,        100) /* MaxStackSize */
     , (2000197,  12,          1) /* StackSize */
     , (2000197,  13,          0) /* StackUnitEncumbrance */
     , (2000197,  15,         50) /* StackUnitValue */
     , (2000197,  16,          8) /* ItemUseable - Contained */
     , (2000197,  19,         50) /* Value */
     , (2000197,  33,          1) /* Bonded - Bonded */
     , (2000197,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000197, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000197,  23, True ) /* DestroyOnSell */
     , (2000197,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000197,   1, 'Skyfire Sparks') /* Name */
     , (2000197,  14, 'Use it to unlock the white sky sparks for your campfire for good. Then /fray sparks sky shows them.') /* Use */
     , (2000197,  16, 'White sparks of the high Weave, for your campfire.') /* LongDesc */
     , (2000197,  20, 'Skyfire Sparks') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000197,   1, 0x020010E7) /* Setup */
     , (2000197,   3, 0x20000014) /* SoundTable */
     , (2000197,   8, 0x060065C2) /* Icon */
     , (2000197,  22, 0x3400002B) /* PhysicsEffectTable */;
