-- @owner: frays
-- @owns: wcid:2000165
-- @requires-restart: no
--
-- Frays village decor: setup 0x02000B6F, the static under the old town portals.

DELETE FROM `weenie` WHERE `class_Id` = 2000165;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000165, 'dw-fray-platform-oldbase', 26, '2026-11-03 00:41:45') /* Switch */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000165,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000165,   5,       6000) /* EncumbranceVal */
     , (2000165,   8,       3000) /* Mass */
     , (2000165,  16,          1) /* ItemUseable - No */
     , (2000165,  19,        200) /* Value */
     , (2000165,  83,       4096) /* ActivationResponse - CastSpell */
     , (2000165,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000165, 119,          1) /* Active */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000165,   1, True ) /* Stuck */
     , (2000165,  12, True ) /* ReportCollisions */
     , (2000165,  13, False) /* Ethereal */
     , (2000165,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000165,  11,      10) /* ResetInterval */
     , (2000165,  39,       1) /* DefaultScale */
     , (2000165,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000165,   1, 'Old Portal Base') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000165,   1, 0x02000B6F) /* Setup */
     , (2000165,   3, 0x20000014) /* SoundTable */
     , (2000165,   8, 0x0600104A) /* Icon */
     , (2000165,  22, 0x3400002B) /* PhysicsEffectTable */;
