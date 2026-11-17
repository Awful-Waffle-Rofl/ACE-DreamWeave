-- @owner: frays
-- @owns: wcid:2000173
-- @requires-restart: no
--
-- Frays village decor: the Fray Tapestry (2000085) hung on the inside of the village's south wall, west of the water gate (FraysDefs.Hangings).

DELETE FROM `weenie` WHERE `class_Id` = 2000173;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000173, 'dw-fray-village-tapestry', 1, '2026-11-03 10:24:03') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000173,   1,        128) /* ItemType - Misc */
     , (2000173,  16,          1) /* ItemUseable - No */
     , (2000173,  19,          0) /* Value */
     , (2000173,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000173,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000173,   1, 'Fray Tapestry') /* Name */
     , (2000173,  16, 'Woven by the Fray village for those who burn the Drift back.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000173,   1, 0x02000186) /* Setup */
     , (2000173,   3, 0x20000014) /* SoundTable */
     , (2000173,   6, 0x04000C10) /* PaletteBase */
     , (2000173,   8, 0x06001300) /* Icon */
     , (2000173,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_palette` (`object_Id`, `sub_Palette_Id`, `offset`, `length`)
VALUES (2000173, 67115386, 0, 128)
     , (2000173, 67115386, 128, 128);
