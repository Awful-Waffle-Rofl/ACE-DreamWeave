-- @owner: frays
-- @owns: wcid:2000205
-- @requires-restart: no
--
-- Frays: sold by the Sky Steward (8 Sky Seals). Using it gives 5 Sigil Shards.

DELETE FROM `weenie` WHERE `class_Id` = 2000205;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000205, 'dw-sky-shard-bundle', 51, '2026-11-15 15:12:36') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000205,   1,        128) /* ItemType - Misc */
     , (2000205,   5,          0) /* EncumbranceVal */
     , (2000205,  11,        100) /* MaxStackSize */
     , (2000205,  12,          1) /* StackSize */
     , (2000205,  13,          0) /* StackUnitEncumbrance */
     , (2000205,  15,         16) /* StackUnitValue */
     , (2000205,  16,          8) /* ItemUseable - Contained */
     , (2000205,  19,         16) /* Value */
     , (2000205,  33,          1) /* Bonded - Bonded */
     , (2000205,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000205, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000205,  23, True ) /* DestroyOnSell */
     , (2000205,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000205,   1, 'Sky Shard Bundle') /* Name */
     , (2000205,  14, 'Use it to open it: 5 Sigil Shards. Ten Sigil Shards join into a Lesser Sigil.') /* Use */
     , (2000205,  16, 'Five Sigil Shards in a sky blue pouch, from the Sky Steward.') /* LongDesc */
     , (2000205,  20, 'Sky Shard Bundles') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000205,   1, 0x020010E7) /* Setup */
     , (2000205,   3, 0x20000014) /* SoundTable */
     , (2000205,   8, 0x06001B13) /* Icon */
     , (2000205,  22, 0x3400002B) /* PhysicsEffectTable */;
