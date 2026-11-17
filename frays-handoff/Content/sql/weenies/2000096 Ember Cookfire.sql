-- @owner: frays
-- @owns: wcid:2000096
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000096;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000096, 'dw-fray-ember-cookfire', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000096,   1,        128) /* ItemType - Misc */
     , (2000096,   5,         50) /* EncumbranceVal */
     , (2000096,   8,         20) /* Mass */
     , (2000096,   9,          0) /* ValidLocations - None */
     , (2000096,  16,          1) /* ItemUseable - No */
     , (2000096,  19,          0) /* Value */
     , (2000096,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000096,   1, True ) /* Stuck */
     , (2000096,  14, True ) /* GravityStatus */
     , (2000096,  19, False) /* Attackable */
     , (2000096,  24, True ) /* UiHidden */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000096,   1, 'Ember Cookfire') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000096,   1, 0x02000351) /* Setup */
     , (2000096,   8, 0x06001066) /* Icon */;
