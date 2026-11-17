-- @owner: frays
-- @owns: wcid:2000101
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000101;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000101, 'dw-fray-charred-barrel', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000101,   1,        128) /* ItemType - Misc */
     , (2000101,   5,       6000) /* EncumbranceVal */
     , (2000101,   8,       3000) /* Mass */
     , (2000101,  16,          1) /* ItemUseable - No */
     , (2000101,  19,        200) /* Value */
     , (2000101,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000101,   1, True ) /* Stuck */
     , (2000101,  12, True ) /* ReportCollisions */
     , (2000101,  13, False) /* Ethereal */
     , (2000101,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000101,  39,    1.25) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000101,   1, 'Charred Barrel') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000101,   1, 0x020000A5) /* Setup */
     , (2000101,   3, 0x20000014) /* SoundTable */
     , (2000101,   8, 0x06001032) /* Icon */
     , (2000101,  22, 0x3400002B) /* PhysicsEffectTable */;
