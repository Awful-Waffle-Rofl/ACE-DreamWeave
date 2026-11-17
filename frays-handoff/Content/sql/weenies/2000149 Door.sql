-- @owner: frays
-- @owns: wcid:2000149
-- @requires-restart: no
--
-- Frays: the town door's leaf (retail 412's model 0x010044B5) on its own, built like the village buildings (retail crate 147, setup swapped for the hung 0x02001790, one part swapped for the leaf), so the client has no sphere to lift off the ground and it sits flush on rising ground.

DELETE FROM `weenie` WHERE `class_Id` = 2000149;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000149, 'dw-fray-town-door-flush', 1, '2026-11-02 09:57:24') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000149,   1,        128) /* ItemType - Misc */
     , (2000149,   8,       3000) /* Mass */
     , (2000149,  16,          1) /* ItemUseable - No */
     , (2000149,  19,        200) /* Value */
     , (2000149,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000149,   1, True ) /* Stuck */
     , (2000149,  12, True ) /* ReportCollisions */
     , (2000149,  13, False) /* Ethereal */
     , (2000149,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000149,   1, 'Door') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000149,   1, 0x02001790) /* Setup */
     , (2000149,   3, 0x20000014) /* SoundTable */
     , (2000149,   8, 0x060012F8) /* Icon */
     , (2000149,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000149, 0, 16794805);
