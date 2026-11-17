-- @owner: frays
-- @owns: wcid:2000206
-- @requires-restart: no
--
-- Frays: sold by the Sky Steward (15 Sky Seals). Raised in a group camp: +10% Embers and key bar for every member for 60 minutes.

DELETE FROM `weenie` WHERE `class_Id` = 2000206;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000206, 'dw-sky-banner', 51, '2026-11-15 15:12:36') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000206,   1,        128) /* ItemType - Misc */
     , (2000206,   5,          0) /* EncumbranceVal */
     , (2000206,  11,        100) /* MaxStackSize */
     , (2000206,  12,          1) /* StackSize */
     , (2000206,  13,          0) /* StackUnitEncumbrance */
     , (2000206,  15,         30) /* StackUnitValue */
     , (2000206,  16,          8) /* ItemUseable - Contained */
     , (2000206,  19,         30) /* Value */
     , (2000206,  33,          1) /* Bonded - Bonded */
     , (2000206,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000206, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000206,  23, True ) /* DestroyOnSell */
     , (2000206,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000206,   1, 'Sky Banner') /* Name */
     , (2000206,  14, 'Use it while you hold a group camp on the Sky Fray: every member gets 10% more Embers and a 10% faster key bar for 60 minutes. Banners do not stack.') /* Use */
     , (2000206,  16, 'A banner of the Sky Fray, raised over a camp held together.') /* LongDesc */
     , (2000206,  20, 'Sky Banners') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000206,   1, 0x020010E7) /* Setup */
     , (2000206,   3, 0x20000014) /* SoundTable */
     , (2000206,   8, 0x060065C2) /* Icon */
     , (2000206,  22, 0x3400002B) /* PhysicsEffectTable */;
