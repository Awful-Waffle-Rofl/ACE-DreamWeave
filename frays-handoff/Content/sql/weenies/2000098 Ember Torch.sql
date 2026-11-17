-- @owner: frays
-- @owns: wcid:2000098
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000098;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000098, 'dw-fray-ember-torch', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000098,   1,       1024) /* ItemType - Useless */
     , (2000098,   5,        100) /* EncumbranceVal */
     , (2000098,   8,        100) /* Mass */
     , (2000098,   9,          0) /* ValidLocations - None */
     , (2000098,  16,          1) /* ItemUseable - No */
     , (2000098,  19,        500) /* Value */
     , (2000098,  93,       3092) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity, LightingOn */
     , (2000098, 150,        103) /* HookPlacement - Hook */
     , (2000098, 151,          9) /* HookType - Floor, Yard */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000098,   1, True ) /* Stuck */
     , (2000098,  13, True ) /* Ethereal */
     , (2000098,  15, True ) /* LightsStatus */
     , (2000098,  19, False) /* Attackable */
     , (2000098,  22, True ) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000098,   1, 'Ember Torch') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000098,   1, 0x020009A3) /* Setup */
     , (2000098,   3, 0x20000014) /* SoundTable */
     , (2000098,   8, 0x0600297E) /* Icon */
     , (2000098,  22, 0x3400002B) /* PhysicsEffectTable */;
