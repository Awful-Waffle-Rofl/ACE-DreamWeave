-- @owner: frays
-- @owns: wcid:2000138
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000138;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000138, 'dw-fray-town-house-15', 1, '2026-11-01 16:56:42') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000138,   1,        128) /* ItemType - Misc */
     , (2000138,   8,       3000) /* Mass */
     , (2000138,  16,          1) /* ItemUseable - No */
     , (2000138,  19,        200) /* Value */
     , (2000138,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000138,   1, True ) /* Stuck */
     , (2000138,  12, True ) /* ReportCollisions */
     , (2000138,  13, False) /* Ethereal */
     , (2000138,  19, False) /* Attackable */
     , (2000138, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000138,   1, 'City Wall 15') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000138,   1, 0x02001790) /* Setup */
     , (2000138,   3, 0x20000014) /* SoundTable */
     , (2000138,   8, 0x060012F8) /* Icon */
     , (2000138,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000138, 0, 16780803);
