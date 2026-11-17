-- @owner: frays
-- @owns: wcid:2000085
-- @requires-restart: no
--
-- Frays mansion decor (weekly top-3 leaderboard award): a copy of retail housing item 255 with a DreamWeave name, bonded and attuned.

DELETE FROM `weenie` WHERE `class_Id` = 2000085;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000085, 'dw-fray-tapestry', 1, '2026-10-29 20:35:10') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000085,   1,        128) /* ItemType - Misc */
     , (2000085,  16,          1) /* ItemUseable - No */
     , (2000085,  19,          0) /* Value */
     , (2000085,  33,          1) /* Bonded - Bonded */
     , (2000085,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000085, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000085,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000085,   1, 'Fray Tapestry') /* Name */
     , (2000085,  16, 'Woven for the week''s top three on a Fray leaderboard.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000085,   1, 0x02000186) /* Setup */
     , (2000085,   3, 0x20000014) /* SoundTable */
     , (2000085,   6, 0x04000C10) /* PaletteBase */
     , (2000085,   8, 0x06001300) /* Icon */
     , (2000085,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_palette` (`object_Id`, `sub_Palette_Id`, `offset`, `length`)
VALUES (2000085, 67115386, 0, 128)
     , (2000085, 67115386, 128, 128);
