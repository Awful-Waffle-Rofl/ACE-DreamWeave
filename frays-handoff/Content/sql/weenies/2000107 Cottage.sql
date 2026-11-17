-- @owner: frays
-- @owns: wcid:2000107
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000107;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000107, 'dw-fray-arwic-cottage', 1, '2026-11-01 13:55:48') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000107,   1,        128) /* ItemType - Misc */
     , (2000107,   8,       3000) /* Mass */
     , (2000107,  16,          1) /* ItemUseable - No */
     , (2000107,  19,        200) /* Value */
     , (2000107,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000107,   1, True ) /* Stuck */
     , (2000107,  12, True ) /* ReportCollisions */
     , (2000107,  19, False) /* Attackable */
     , (2000107, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000107,   1, 'Cottage') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000107,   1, 0x02001790) /* Setup */
     , (2000107,   3, 0x20000014) /* SoundTable */
     , (2000107,   8, 0x060012F8) /* Icon */
     , (2000107,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000107, 0, 16780311);
