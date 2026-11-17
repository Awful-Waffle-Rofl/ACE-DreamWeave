-- @owner: frays
-- @owns: wcid:2000166
-- @requires-restart: no
--
-- Frays village decor: setup 0x020007C5, the static about 2.5 m around the old town portals.

DELETE FROM `weenie` WHERE `class_Id` = 2000166;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000166, 'dw-fray-platform-oldpillar', 26, '2026-11-03 00:41:45') /* Switch */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000166,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000166,   5,       6000) /* EncumbranceVal */
     , (2000166,   8,       3000) /* Mass */
     , (2000166,  16,          1) /* ItemUseable - No */
     , (2000166,  19,        200) /* Value */
     , (2000166,  83,       4096) /* ActivationResponse - CastSpell */
     , (2000166,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000166, 119,          1) /* Active */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000166,   1, True ) /* Stuck */
     , (2000166,  12, True ) /* ReportCollisions */
     , (2000166,  13, False) /* Ethereal */
     , (2000166,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000166,  11,      10) /* ResetInterval */
     , (2000166,  39,       1) /* DefaultScale */
     , (2000166,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000166,   1, 'Old Portal Pillar') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000166,   1, 0x020007C5) /* Setup */
     , (2000166,   3, 0x20000014) /* SoundTable */
     , (2000166,   8, 0x0600104A) /* Icon */
     , (2000166,  22, 0x3400002B) /* PhysicsEffectTable */;
