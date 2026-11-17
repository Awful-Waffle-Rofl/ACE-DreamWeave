-- @owner: frays
-- @owns: wcid:2000080
-- @requires-restart: no
--
-- Frays mansion decor (rare Snarl cache decor): a copy of retail housing item 11149 with a DreamWeave name, bonded and attuned.

DELETE FROM `weenie` WHERE `class_Id` = 2000080;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000080, 'dw-snarl-trophy', 1, '2026-10-29 20:35:09') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000080,   1,        128) /* ItemType - Misc */
     , (2000080,   5,       2100) /* EncumbranceVal */
     , (2000080,   8,        200) /* Mass */
     , (2000080,   9,          0) /* ValidLocations - None */
     , (2000080,  16,          1) /* ItemUseable - No */
     , (2000080,  19,          0) /* Value */
     , (2000080,  33,          1) /* Bonded - Bonded */
     , (2000080,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000080, 114,          1) /* Attuned - Attuned */
     , (2000080, 150,        103) /* HookPlacement - Hook */
     , (2000080, 151,          2) /* HookType - Wall */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000080,  22, True ) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000080,   1, 'Snarl Trophy') /* Name */
     , (2000080,  16, 'A rare trophy from a fallen Snarl. Hang it in your mansion.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000080,   1, 0x02000A9C) /* Setup */
     , (2000080,   3, 0x20000014) /* SoundTable */
     , (2000080,   6, 0x04001139) /* PaletteBase */
     , (2000080,   8, 0x06002126) /* Icon */;

INSERT INTO `weenie_properties_palette` (`object_Id`, `sub_Palette_Id`, `offset`, `length`)
VALUES (2000080, 67115386, 0, 128)
     , (2000080, 67115386, 128, 128);
