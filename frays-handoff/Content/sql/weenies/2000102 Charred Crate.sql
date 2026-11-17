-- @owner: frays
-- @owns: wcid:2000102
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000102;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000102, 'dw-fray-charred-crate', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000102,   1,        128) /* ItemType - Misc */
     , (2000102,   8,       3000) /* Mass */
     , (2000102,  16,          1) /* ItemUseable - No */
     , (2000102,  19,        200) /* Value */
     , (2000102,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000102,   1, True ) /* Stuck */
     , (2000102,  12, True ) /* ReportCollisions */
     , (2000102,  13, False) /* Ethereal */
     , (2000102,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000102,   1, 'Charred Crate') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000102,   1, 0x0200011E) /* Setup */
     , (2000102,   3, 0x20000014) /* SoundTable */
     , (2000102,   8, 0x060012F8) /* Icon */
     , (2000102,  22, 0x3400002B) /* PhysicsEffectTable */;
