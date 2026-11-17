-- @owner: frays
-- @owns: wcid:2000078
-- @requires-restart: no
--
-- Frays: rolls a weapon's slayer to a random new family, never the one it has (a lottery).

DELETE FROM `weenie` WHERE `class_Id` = 2000078;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000078, 'dw-reroll-stone', 51, '2026-10-29 22:48:54') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000078,   1,        128) /* ItemType - Misc */
     , (2000078,   5,          0) /* EncumbranceVal */
     , (2000078,  11,        100) /* MaxStackSize */
     , (2000078,  12,          1) /* StackSize */
     , (2000078,  13,          0) /* StackUnitEncumbrance */
     , (2000078,  15,        600) /* StackUnitValue */
     , (2000078,  16,     524296) /* ItemUseable - SourceContainedTargetContained */
     , (2000078,  19,        600) /* Value */
     , (2000078,  33,          1) /* Bonded - Bonded */
     , (2000078,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000078,  94,      33025) /* TargetType - WeaponOrCaster */
     , (2000078, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000078,  23, True ) /* DestroyOnSell */
     , (2000078,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000078,   1, 'Reroll Stone') /* Name */
     , (2000078,  14, 'Use this on a weapon or caster that already slays to roll its slayer to a new random family.') /* Use */
     , (2000078,  16, 'Rolls a weapon''s slayer to a new random Fray family, never the one it already slays. This week''s family comes up more often (about 1 roll in 4). A lottery: keep rolling for the one you want.') /* LongDesc */
     , (2000078,  20, 'Reroll Stones') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000078,   1, 0x020009BE) /* Setup */
     , (2000078,   3, 0x20000014) /* SoundTable */
     , (2000078,   8, 0x0600223D) /* Icon */
     , (2000078,  22, 0x3400002B) /* PhysicsEffectTable */;
