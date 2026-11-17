-- @owner: frays
-- @owns: wcid:2000133
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000133;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000133, 'dw-fray-town-house-10', 1, '2026-11-01 16:56:42') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000133,   1,        128) /* ItemType - Misc */
     , (2000133,   8,       3000) /* Mass */
     , (2000133,  16,          1) /* ItemUseable - No */
     , (2000133,  19,        200) /* Value */
     , (2000133,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000133,   1, True ) /* Stuck */
     , (2000133,  12, True ) /* ReportCollisions */
     , (2000133,  13, False) /* Ethereal */
     , (2000133,  19, False) /* Attackable */
     , (2000133, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000133,   1, 'Town House 10') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000133,   1, 0x02001790) /* Setup */
     , (2000133,   3, 0x20000014) /* SoundTable */
     , (2000133,   8, 0x060012F8) /* Icon */
     , (2000133,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000133, 0, 16779303);
