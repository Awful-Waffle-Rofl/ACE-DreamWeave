-- @owner: frays
-- @owns: wcid:2000077
-- @requires-restart: no
--
-- Frays: adds a random Fray-family slayer to a weapon or caster with none (every family equally likely); a Reroll Stone then rolls it to a new random family.

DELETE FROM `weenie` WHERE `class_Id` = 2000077;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000077, 'dw-slayer-stone', 51, '2026-10-29 22:48:54') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000077,   1,        128) /* ItemType - Misc */
     , (2000077,   5,          0) /* EncumbranceVal */
     , (2000077,  11,        100) /* MaxStackSize */
     , (2000077,  12,          1) /* StackSize */
     , (2000077,  13,          0) /* StackUnitEncumbrance */
     , (2000077,  15,       8000) /* StackUnitValue */
     , (2000077,  16,     524296) /* ItemUseable - SourceContainedTargetContained */
     , (2000077,  19,       8000) /* Value */
     , (2000077,  33,          1) /* Bonded - Bonded */
     , (2000077,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000077,  94,      33025) /* TargetType - WeaponOrCaster */
     , (2000077, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000077,  23, True ) /* DestroyOnSell */
     , (2000077,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000077,   1, 'Slayer Stone') /* Name */
     , (2000077,  14, 'Use this on a weapon or caster with no slayer to give it a random Fray-family slayer.') /* Use */
     , (2000077,  16, 'Adds a random Fray-family slayer to a weapon or caster: any of the Frays creature families. A Reroll Stone then rolls it to a new random family.') /* LongDesc */
     , (2000077,  20, 'Slayer Stones') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000077,   1, 0x020009BE) /* Setup */
     , (2000077,   3, 0x20000014) /* SoundTable */
     , (2000077,   8, 0x06001B6F) /* Icon */
     , (2000077,  22, 0x3400002B) /* PhysicsEffectTable */;
