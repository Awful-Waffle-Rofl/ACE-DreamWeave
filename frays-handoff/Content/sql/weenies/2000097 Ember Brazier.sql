-- @owner: frays
-- @owns: wcid:2000097
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000097;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000097, 'dw-fray-ember-brazier', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000097,   1,       1024) /* ItemType - Useless */
     , (2000097,   5,         50) /* EncumbranceVal */
     , (2000097,   8,         25) /* Mass */
     , (2000097,   9,          0) /* ValidLocations - None */
     , (2000097,  16,          1) /* ItemUseable - No */
     , (2000097,  19,          7) /* Value */
     , (2000097,  93,       1040) /* PhysicsState - IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000097,   1, True ) /* Stuck */
     , (2000097,  13, False) /* Ethereal */
     , (2000097,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000097,  39,    0.75) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000097,   1, 'Ember Brazier') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000097,   1, 0x02000104) /* Setup */
     , (2000097,   3, 0x20000014) /* SoundTable */
     , (2000097,   8, 0x060012D0) /* Icon */
     , (2000097,  22, 0x3400002B) /* PhysicsEffectTable */;
