-- @owner: frays
-- @owns: wcid:2000202
-- @requires-restart: no
--
-- Frays: one of the stones the server rings round the Snarl Monument (frays_monument_stones, model frays_monument_stone_setup, retail Historical Marker 0x02000290).

DELETE FROM `weenie` WHERE `class_Id` = 2000202;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000202, 'dw-snarl-stone', 1, '2026-11-09 09:10:12') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000202,   1,        128) /* ItemType - Misc */
     , (2000202,   5,       9000) /* EncumbranceVal */
     , (2000202,   8,       1800) /* Mass */
     , (2000202,  16,          1) /* ItemUseable - No */
     , (2000202,  19,        125) /* Value */
     , (2000202,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000202,   1, True ) /* Stuck */
     , (2000202,  12, True ) /* ReportCollisions */
     , (2000202,  13, False) /* Ethereal */
     , (2000202,  22, False) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000202,   1, 'Snarl Stone') /* Name */
     , (2000202,  16, 'This stone waits for the next Snarl Trophy found in the Frays.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000202,   1, 0x02000290) /* Setup */
     , (2000202,   8, 0x060012D3) /* Icon */;
