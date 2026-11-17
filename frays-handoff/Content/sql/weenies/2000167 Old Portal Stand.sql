-- @owner: frays
-- @owns: wcid:2000167
-- @requires-restart: no
--
-- Frays village decor: setup 0x02000B61, under the old town portals.

DELETE FROM `weenie` WHERE `class_Id` = 2000167;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000167, 'dw-fray-platform-oldstand', 26, '2026-11-03 00:41:45') /* Switch */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000167,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000167,   5,       6000) /* EncumbranceVal */
     , (2000167,   8,       3000) /* Mass */
     , (2000167,  16,          1) /* ItemUseable - No */
     , (2000167,  19,        200) /* Value */
     , (2000167,  83,       4096) /* ActivationResponse - CastSpell */
     , (2000167,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000167, 119,          1) /* Active */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000167,   1, True ) /* Stuck */
     , (2000167,  12, True ) /* ReportCollisions */
     , (2000167,  13, False) /* Ethereal */
     , (2000167,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000167,  11,      10) /* ResetInterval */
     , (2000167,  39,       1) /* DefaultScale */
     , (2000167,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000167,   1, 'Old Portal Stand') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000167,   1, 0x02000B61) /* Setup */
     , (2000167,   3, 0x20000014) /* SoundTable */
     , (2000167,   8, 0x0600104A) /* Icon */
     , (2000167,  22, 0x3400002B) /* PhysicsEffectTable */;
