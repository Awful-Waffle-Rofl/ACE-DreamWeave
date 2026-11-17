-- @owner: frays
-- @owns: wcid:2000203
-- @requires-restart: no
--
-- Frays: sold by the Sky Steward (25 Sky Seals). Using it puts 5 Vault Keys on the Vault Keyring.

DELETE FROM `weenie` WHERE `class_Id` = 2000203;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000203, 'dw-sky-key-bundle', 51, '2026-11-15 15:12:36') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000203,   1,        128) /* ItemType - Misc */
     , (2000203,   5,          0) /* EncumbranceVal */
     , (2000203,  11,        100) /* MaxStackSize */
     , (2000203,  12,          1) /* StackSize */
     , (2000203,  13,          0) /* StackUnitEncumbrance */
     , (2000203,  15,         50) /* StackUnitValue */
     , (2000203,  16,          8) /* ItemUseable - Contained */
     , (2000203,  19,         50) /* Value */
     , (2000203,  33,          1) /* Bonded - Bonded */
     , (2000203,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000203, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000203,  23, True ) /* DestroyOnSell */
     , (2000203,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000203,   1, 'Sky Key Bundle') /* Name */
     , (2000203,  14, 'Use it to open it: 5 Vault Keys go onto your Vault Keyring.') /* Use */
     , (2000203,  16, 'Five Vault Keys on a sky blue cord, from the Sky Steward.') /* LongDesc */
     , (2000203,  20, 'Sky Key Bundles') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000203,   1, 0x020010E7) /* Setup */
     , (2000203,   3, 0x20000014) /* SoundTable */
     , (2000203,   8, 0x060029D0) /* Icon */
     , (2000203,  22, 0x3400002B) /* PhysicsEffectTable */;
