-- @owner: frays
-- @owns: wcid:2000168
-- @requires-restart: no
--
-- Frays village decor: setup 0x0200004F, under many dungeon portals.

DELETE FROM `weenie` WHERE `class_Id` = 2000168;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000168, 'dw-fray-platform-ring', 26, '2026-11-03 00:41:45') /* Switch */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000168,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000168,   5,       6000) /* EncumbranceVal */
     , (2000168,   8,       3000) /* Mass */
     , (2000168,  16,          1) /* ItemUseable - No */
     , (2000168,  19,        200) /* Value */
     , (2000168,  83,       4096) /* ActivationResponse - CastSpell */
     , (2000168,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000168, 119,          1) /* Active */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000168,   1, True ) /* Stuck */
     , (2000168,  12, True ) /* ReportCollisions */
     , (2000168,  13, False) /* Ethereal */
     , (2000168,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000168,  11,      10) /* ResetInterval */
     , (2000168,  39,       1) /* DefaultScale */
     , (2000168,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000168,   1, 'Portal Ring') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000168,   1, 0x0200004F) /* Setup */
     , (2000168,   3, 0x20000014) /* SoundTable */
     , (2000168,   8, 0x0600104A) /* Icon */
     , (2000168,  22, 0x3400002B) /* PhysicsEffectTable */;
