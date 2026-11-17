-- @owner: frays
-- @owns: wcid:2000126
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000126;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000126, 'dw-fray-town-house-3', 1, '2026-11-01 16:56:42') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000126,   1,        128) /* ItemType - Misc */
     , (2000126,   8,       3000) /* Mass */
     , (2000126,  16,          1) /* ItemUseable - No */
     , (2000126,  19,        200) /* Value */
     , (2000126,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000126,   1, True ) /* Stuck */
     , (2000126,  12, True ) /* ReportCollisions */
     , (2000126,  13, False) /* Ethereal */
     , (2000126,  19, False) /* Attackable */
     , (2000126, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000126,   1, 'Town House 3') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000126,   1, 0x02001790) /* Setup */
     , (2000126,   3, 0x20000014) /* SoundTable */
     , (2000126,   8, 0x060012F8) /* Icon */
     , (2000126,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000126, 0, 16786126);
