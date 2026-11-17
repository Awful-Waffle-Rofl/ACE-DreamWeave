-- @owner: frays
-- @owns: wcid:2000164
-- @requires-restart: no
--
-- Frays village decor: the retail town network portal platform (setup 0x020019E3, a cell-dat static under 53 Portal to Town Network portals; no retail weenie).

DELETE FROM `weenie` WHERE `class_Id` = 2000164;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000164, 'dw-fray-platform-townnet', 26, '2026-11-03 00:41:45') /* Switch */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000164,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000164,   5,       6000) /* EncumbranceVal */
     , (2000164,   8,       3000) /* Mass */
     , (2000164,  16,          1) /* ItemUseable - No */
     , (2000164,  19,        200) /* Value */
     , (2000164,  83,       4096) /* ActivationResponse - CastSpell */
     , (2000164,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000164, 119,          1) /* Active */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000164,   1, True ) /* Stuck */
     , (2000164,  12, True ) /* ReportCollisions */
     , (2000164,  13, False) /* Ethereal */
     , (2000164,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000164,  11,      10) /* ResetInterval */
     , (2000164,  39,       1) /* DefaultScale */
     , (2000164,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000164,   1, 'Town Network Platform') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000164,   1, 0x020019E3) /* Setup */
     , (2000164,   3, 0x20000014) /* SoundTable */
     , (2000164,   8, 0x0600104A) /* Icon */
     , (2000164,  22, 0x3400002B) /* PhysicsEffectTable */;
