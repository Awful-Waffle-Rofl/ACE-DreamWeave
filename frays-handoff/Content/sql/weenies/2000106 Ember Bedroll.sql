-- @owner: frays
-- @owns: wcid:2000106
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000106;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000106, 'dw-fray-ember-bedroll', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000106,   1,        128) /* ItemType - Misc */
     , (2000106,   5,        200) /* EncumbranceVal */
     , (2000106,   8,        100) /* Mass */
     , (2000106,  16,          1) /* ItemUseable - No */
     , (2000106,  19,        500) /* Value */
     , (2000106,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000106, 150,        103) /* HookPlacement - Hook */
     , (2000106, 151,          1) /* HookType - Floor */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000106,   1, True ) /* Stuck */
     , (2000106,  19, False) /* Attackable */
     , (2000106,  22, True ) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000106,   1, 'Ember Bedroll') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000106,   1, 0x02000270) /* Setup */
     , (2000106,   8, 0x060023A7) /* Icon */;
