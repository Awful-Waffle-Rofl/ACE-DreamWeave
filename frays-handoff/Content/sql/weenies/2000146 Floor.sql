-- @owner: frays
-- @owns: wcid:2000146
-- @requires-restart: no
--
-- Frays: a 2 m square of dark wooden planks (retail model 0x01000E2A, used as the setup itself so it can be sized), solid, no gravity, to fill holes in the Fray town house floors: Town House 7's 3rd floor (25 tiles, FraysHouses.FloorPatches;.

DELETE FROM `weenie` WHERE `class_Id` = 2000146;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000146, 'dw-fray-floor', 1, '2026-11-01 21:31:09') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000146,   1,        128) /* ItemType - Misc */
     , (2000146,   8,       3000) /* Mass */
     , (2000146,  16,          1) /* ItemUseable - No */
     , (2000146,  19,        200) /* Value */
     , (2000146,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000146,   1, True ) /* Stuck */
     , (2000146,  12, True ) /* ReportCollisions */
     , (2000146,  13, False) /* Ethereal */
     , (2000146,  19, False) /* Attackable */
     , (2000146, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000146,   1, 'Floor') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000146,   1, 0x0200011E) /* Setup */
     , (2000146,   3, 0x20000014) /* SoundTable */
     , (2000146,   8, 0x060012F8) /* Icon */
     , (2000146,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000146, 0, 16780842);
