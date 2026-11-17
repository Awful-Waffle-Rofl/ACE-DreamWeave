-- @owner: frays
-- @owns: wcid:2000201
-- @requires-restart: no
--
-- Frays: a statue in the village square (retail Town Statue model 0x02000642, frays_monument_setup) on a copy of the retail Historical Marker 5042.

DELETE FROM `weenie` WHERE `class_Id` = 2000201;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000201, 'dw-snarl-monument', 1, '2026-11-09 09:10:12') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000201,   1,        128) /* ItemType - Misc */
     , (2000201,   5,       9000) /* EncumbranceVal */
     , (2000201,   8,       1800) /* Mass */
     , (2000201,  16,          1) /* ItemUseable - No */
     , (2000201,  19,        125) /* Value */
     , (2000201,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000201,   1, True ) /* Stuck */
     , (2000201,  12, True ) /* ReportCollisions */
     , (2000201,  13, False) /* Ethereal */
     , (2000201,  22, False) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000201,   1, 'Snarl Monument') /* Name */
     , (2000201,  16, 'The Snarl Monument honours those who find a Snarl Trophy.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000201,   1, 0x02000642) /* Setup */
     , (2000201,   8, 0x06001B00) /* Icon */;
