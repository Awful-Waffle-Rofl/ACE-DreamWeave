-- @owner: frays
-- @owns: wcid:2000115
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000115;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000115, 'dw-fray-arwic-gate-b', 1, '2026-11-01 13:55:49') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000115,   1,        128) /* ItemType - Misc */
     , (2000115,   8,       3000) /* Mass */
     , (2000115,  16,          1) /* ItemUseable - No */
     , (2000115,  19,        200) /* Value */
     , (2000115,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000115,   1, True ) /* Stuck */
     , (2000115,  12, True ) /* ReportCollisions */
     , (2000115,  19, False) /* Attackable */
     , (2000115, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000115,   1, 'Town Gate') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000115,   1, 0x02001790) /* Setup */
     , (2000115,   3, 0x20000014) /* SoundTable */
     , (2000115,   8, 0x060012F8) /* Icon */
     , (2000115,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000115, 0, 16788773);
