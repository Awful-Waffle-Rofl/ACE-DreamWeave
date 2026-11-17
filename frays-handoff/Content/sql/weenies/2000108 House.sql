-- @owner: frays
-- @owns: wcid:2000108
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000108;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000108, 'dw-fray-arwic-house', 1, '2026-11-01 13:55:49') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000108,   1,        128) /* ItemType - Misc */
     , (2000108,   8,       3000) /* Mass */
     , (2000108,  16,          1) /* ItemUseable - No */
     , (2000108,  19,        200) /* Value */
     , (2000108,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000108,   1, True ) /* Stuck */
     , (2000108,  12, True ) /* ReportCollisions */
     , (2000108,  19, False) /* Attackable */
     , (2000108, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000108,   1, 'House') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000108,   1, 0x02001790) /* Setup */
     , (2000108,   3, 0x20000014) /* SoundTable */
     , (2000108,   8, 0x060012F8) /* Icon */
     , (2000108,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000108, 0, 16779310);
