-- @owner: frays
-- @owns: wcid:2000109
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000109;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000109, 'dw-fray-arwic-manor', 1, '2026-11-01 13:55:49') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000109,   1,        128) /* ItemType - Misc */
     , (2000109,   8,       3000) /* Mass */
     , (2000109,  16,          1) /* ItemUseable - No */
     , (2000109,  19,        200) /* Value */
     , (2000109,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000109,   1, True ) /* Stuck */
     , (2000109,  12, True ) /* ReportCollisions */
     , (2000109,  19, False) /* Attackable */
     , (2000109, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000109,   1, 'Manor') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000109,   1, 0x02001790) /* Setup */
     , (2000109,   3, 0x20000014) /* SoundTable */
     , (2000109,   8, 0x060012F8) /* Icon */
     , (2000109,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000109, 0, 16780318);
