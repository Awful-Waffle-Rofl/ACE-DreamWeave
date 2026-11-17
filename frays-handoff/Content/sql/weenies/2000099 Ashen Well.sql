-- @owner: frays
-- @owns: wcid:2000099
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000099;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000099, 'dw-fray-ashen-well', 26, '2026-11-01 12:38:31') /* Switch */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000099,   1,    4194304) /* ItemType - CraftCookingBase */
     , (2000099,   5,       6000) /* EncumbranceVal */
     , (2000099,   8,       3000) /* Mass */
     , (2000099,  16,         48) /* ItemUseable - ViewedRemote */
     , (2000099,  19,        200) /* Value */
     , (2000099,  83,       4096) /* ActivationResponse - CastSpell */
     , (2000099,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000099, 119,          1) /* Active */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000099,   1, True ) /* Stuck */
     , (2000099,  12, True ) /* ReportCollisions */
     , (2000099,  13, False) /* Ethereal */
     , (2000099,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000099,  11,      10) /* ResetInterval */
     , (2000099,  39,       1) /* DefaultScale */
     , (2000099,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000099,   1, 'Ashen Well') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000099,   1, 0x02000180) /* Setup */
     , (2000099,   3, 0x20000014) /* SoundTable */
     , (2000099,   8, 0x0600104A) /* Icon */
     , (2000099,  22, 0x3400002B) /* PhysicsEffectTable */
     , (2000099,  28,       1183) /* Spell - Revitalize Other I */;
