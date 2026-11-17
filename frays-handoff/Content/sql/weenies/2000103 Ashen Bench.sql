-- @owner: frays
-- @owns: wcid:2000103
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000103;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000103, 'dw-fray-ashen-bench', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000103,   1,        128) /* ItemType - Misc */
     , (2000103,   5,         50) /* EncumbranceVal */
     , (2000103,   8,         25) /* Mass */
     , (2000103,   9,          0) /* ValidLocations - None */
     , (2000103,  16,          1) /* ItemUseable - No */
     , (2000103,  19,       3226) /* Value */
     , (2000103,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000103, 150,        103) /* HookPlacement - Hook */
     , (2000103, 151,          9) /* HookType - Floor, Yard */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000103,   1, True ) /* Stuck */
     , (2000103,  13, True ) /* Ethereal */
     , (2000103,  19, False) /* Attackable */
     , (2000103,  22, True ) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000103,   1, 'Ashen Bench') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000103,   1, 0x02000120) /* Setup */
     , (2000103,   8, 0x060012CF) /* Icon */;
