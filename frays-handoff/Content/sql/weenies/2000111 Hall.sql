-- @owner: frays
-- @owns: wcid:2000111
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000111;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000111, 'dw-fray-arwic-hall', 1, '2026-11-01 13:55:49') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000111,   1,        128) /* ItemType - Misc */
     , (2000111,   8,       3000) /* Mass */
     , (2000111,  16,          1) /* ItemUseable - No */
     , (2000111,  19,        200) /* Value */
     , (2000111,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000111,   1, True ) /* Stuck */
     , (2000111,  12, True ) /* ReportCollisions */
     , (2000111,  19, False) /* Attackable */
     , (2000111, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000111,   1, 'Hall') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000111,   1, 0x02001790) /* Setup */
     , (2000111,   3, 0x20000014) /* SoundTable */
     , (2000111,   8, 0x060012F8) /* Icon */
     , (2000111,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000111, 0, 16779303);
