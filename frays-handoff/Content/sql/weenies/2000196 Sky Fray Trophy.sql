-- @owner: frays
-- @owns: wcid:2000196
-- @requires-restart: no
--
-- Frays: mansion decor sold by the Sky Steward (40 Sky Seals, a placeholder price).

DELETE FROM `weenie` WHERE `class_Id` = 2000196;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000196, 'dw-sky-fray-trophy', 1, '2026-11-09 08:41:06') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000196,   1,        128) /* ItemType - Misc */
     , (2000196,   5,       2100) /* EncumbranceVal */
     , (2000196,   8,        200) /* Mass */
     , (2000196,   9,          0) /* ValidLocations - None */
     , (2000196,  16,          1) /* ItemUseable - No */
     , (2000196,  19,         80) /* Value */
     , (2000196,  33,          1) /* Bonded - Bonded */
     , (2000196,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000196, 114,          1) /* Attuned - Attuned */
     , (2000196, 150,        103) /* HookPlacement - Hook */
     , (2000196, 151,          2) /* HookType - Wall */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000196,  22, True ) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000196,   1, 'Sky Fray Trophy') /* Name */
     , (2000196,  16, 'A trophy of the Sky Fray, bought with Sky Seals. Hang it in your mansion.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000196,   1, 0x02000A9C) /* Setup */
     , (2000196,   3, 0x20000014) /* SoundTable */
     , (2000196,   6, 0x04001139) /* PaletteBase */
     , (2000196,   8, 0x06002126) /* Icon */;
