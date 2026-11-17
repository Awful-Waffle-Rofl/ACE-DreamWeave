-- @owner: frays
-- @owns: wcid:2000193
-- @requires-restart: no
--
-- Frays: a Snarl cache prize (frays_snarl_shard_chance).

DELETE FROM `weenie` WHERE `class_Id` = 2000193;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000193, 'dw-sigil-shard', 51, '2026-11-09 01:21:25') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000193,   1,        128) /* ItemType - Misc */
     , (2000193,   5,          0) /* EncumbranceVal */
     , (2000193,  11,        100) /* MaxStackSize */
     , (2000193,  12,          1) /* StackSize */
     , (2000193,  13,          0) /* StackUnitEncumbrance */
     , (2000193,  15,          0) /* StackUnitValue */
     , (2000193,  16,          8) /* ItemUseable - Contained */
     , (2000193,  19,          0) /* Value */
     , (2000193,  33,          1) /* Bonded - Bonded */
     , (2000193,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000193, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000193,  23, True ) /* DestroyOnSell */
     , (2000193,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000193,   1, 'Sigil Shard') /* Name */
     , (2000193,  14, 'Use a stack of 10 Sigil Shards to join them into a Lesser Sigil.') /* Use */
     , (2000193,  16, 'A splinter of a boon Sigil, dropped by a Snarl. Ten of them make a Lesser Sigil.') /* LongDesc */
     , (2000193,  20, 'Sigil Shards') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000193,   1, 0x020010E7) /* Setup */
     , (2000193,   3, 0x20000014) /* SoundTable */
     , (2000193,   8, 0x06001B13) /* Icon */
     , (2000193,  22, 0x3400002B) /* PhysicsEffectTable */;
