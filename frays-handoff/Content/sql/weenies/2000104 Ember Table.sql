-- @owner: frays
-- @owns: wcid:2000104
-- @requires-restart: no
--

DELETE FROM `weenie` WHERE `class_Id` = 2000104;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000104, 'dw-fray-ember-table', 1, '2026-11-01 12:38:31') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000104,   1,        128) /* ItemType - Misc */
     , (2000104,   5,        400) /* EncumbranceVal */
     , (2000104,   8,        400) /* Mass */
     , (2000104,  16,          1) /* ItemUseable - No */
     , (2000104,  19,       5000) /* Value */
     , (2000104,  93,       1052) /* PhysicsState - Ethereal, ReportCollisions, IgnoreCollisions, Gravity */
     , (2000104, 150,        103) /* HookPlacement - Hook */
     , (2000104, 151,          1) /* HookType - Floor */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000104,   1, True ) /* Stuck */
     , (2000104,  12, True ) /* ReportCollisions */
     , (2000104,  13, True ) /* Ethereal */
     , (2000104,  19, False) /* Attackable */
     , (2000104,  22, True ) /* Inscribable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000104,   1, 'Ember Table') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000104,   1, 0x02000E7D) /* Setup */
     , (2000104,   3, 0x20000014) /* SoundTable */
     , (2000104,   8, 0x06002959) /* Icon */
     , (2000104,  22, 0x3400002B) /* PhysicsEffectTable */;
