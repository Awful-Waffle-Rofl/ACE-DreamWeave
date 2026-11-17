-- @owner: frays
-- @owns: wcid:2000105
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000105;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000105, 'dw-fray-charred-cart', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000105,   1,        128) /* ItemType - Misc */
     , (2000105,   3,          4) /* PaletteTemplate - Brown */
     , (2000105,   5,        800) /* EncumbranceVal */
     , (2000105,   8,        180) /* Mass */
     , (2000105,   9,          0) /* ValidLocations - None */
     , (2000105,  16,          1) /* ItemUseable - No */
     , (2000105,  19,          0) /* Value */
     , (2000105,  33,          1) /* Bonded - Bonded */
     , (2000105,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000105, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000105,   1, True ) /* Stuck */
     , (2000105,  19, False) /* Attackable */
     , (2000105,  22, True ) /* Inscribable */
     , (2000105,  23, True ) /* DestroyOnSell */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000105,  39,       1) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000105,   1, 'Charred Cart') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000105,   1, 0x02000710) /* Setup */
     , (2000105,   3, 0x20000014) /* SoundTable */
     , (2000105,   7, 0x10000178) /* ClothingBase */
     , (2000105,   8, 0x06003563) /* Icon */
     , (2000105,  22, 0x3400002B) /* PhysicsEffectTable */;
