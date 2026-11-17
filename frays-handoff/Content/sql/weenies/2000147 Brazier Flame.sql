-- @owner: frays
-- @owns: wcid:2000147
-- @requires-restart: no
--
-- Frays: retail brazier 239 with its setup swapped for the fireplace fire's flame (0x02000485; retail 5148 itself is a hotspot that burns), ethereal, set in each Fray village brazier's bowl (FraysDefs.Village;.

DELETE FROM `weenie` WHERE `class_Id` = 2000147;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000147, 'dw-fray-brazier-flame', 1, '2026-11-01 23:58:41') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000147,   1,       1024) /* ItemType - Useless */
     , (2000147,   5,         50) /* EncumbranceVal */
     , (2000147,   8,         25) /* Mass */
     , (2000147,   9,          0) /* ValidLocations - None */
     , (2000147,  16,          1) /* ItemUseable - No */
     , (2000147,  19,          7) /* Value */
     , (2000147,  93,       1040) /* PhysicsState - IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000147,   1, True ) /* Stuck */
     , (2000147,   2, True ) /* Open */
     , (2000147,  13, False) /* Ethereal */
     , (2000147,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000147,  39,    0.75) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000147,   1, 'Brazier Flame') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000147,   1, 0x02000485) /* Setup */
     , (2000147,   3, 0x20000014) /* SoundTable */
     , (2000147,   8, 0x060012D0) /* Icon */
     , (2000147,  22, 0x3400002B) /* PhysicsEffectTable */;
