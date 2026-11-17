-- @owner: frays
-- @owns: wcid:2000174
-- @requires-restart: no
--
-- Frays effigy companions: changes one companion's locked style (warder, mender, striker) and keeps its level, through /frays companion <form> <style> (FraysCompanions).

DELETE FROM `weenie` WHERE `class_Id` = 2000174;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000174, 'dw-rebinding-ember', 51, '2026-11-09 23:31:03') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000174,   1,        128) /* ItemType - Misc */
     , (2000174,   5,          0) /* EncumbranceVal */
     , (2000174,  11,        100) /* MaxStackSize */
     , (2000174,  12,          1) /* StackSize */
     , (2000174,  13,          0) /* StackUnitEncumbrance */
     , (2000174,  15,       5000) /* StackUnitValue */
     , (2000174,  16,          8) /* ItemUseable - Contained */
     , (2000174,  19,       5000) /* Value */
     , (2000174,  33,          1) /* Bonded - Bonded */
     , (2000174,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000174, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000174,  23, True ) /* DestroyOnSell */
     , (2000174,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000174,   1, 'Rebinding Ember') /* Name */
     , (2000174,  14, 'Type /frays companion <form> <style>, where the style is warder, mender or striker. It swaps that companion''s style and keeps its level. /frays companion lists your forms.') /* Use */
     , (2000174,  16, 'Each Frays companion has one style: warder (you take less damage), mender (it heals you over time) or striker (you deal more damage). The first style you pick is free and then stays locked. One Rebinding Ember swaps it once. The companion keeps its level and becomes your active one.') /* LongDesc */
     , (2000174,  20, 'Rebinding Embers') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000174,   1, 0x020010E7) /* Setup */
     , (2000174,   3, 0x20000014) /* SoundTable */
     , (2000174,   8, 0x060065C4) /* Icon */
     , (2000174,  22, 0x3400002B) /* PhysicsEffectTable */;
