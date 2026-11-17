-- @owner: frays
-- @owns: wcid:2000112
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000112;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000112, 'dw-fray-arwic-tower', 1, '2026-11-01 13:55:49') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000112,   1,        128) /* ItemType - Misc */
     , (2000112,   8,       3000) /* Mass */
     , (2000112,  16,          1) /* ItemUseable - No */
     , (2000112,  19,        200) /* Value */
     , (2000112,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000112,   1, True ) /* Stuck */
     , (2000112,  12, True ) /* ReportCollisions */
     , (2000112,  19, False) /* Attackable */
     , (2000112, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000112,   1, 'Watchtower') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000112,   1, 0x02001790) /* Setup */
     , (2000112,   3, 0x20000014) /* SoundTable */
     , (2000112,   8, 0x060012F8) /* Icon */
     , (2000112,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000112, 0, 16788763);
