-- @owner: frays
-- @owns: wcid:2000029
-- @requires-restart: no
--
-- Frays (UCM project) prototype campfire.

DELETE FROM `weenie` WHERE `class_Id` = 2000029;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000029, 'dw-hollow-campfire', 1, '2026-10-29 14:52:25') /* Generic */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000029,   1,        128) /* ItemType - Misc */
     , (2000029,   5,          0) /* EncumbranceVal */
     , (2000029,  16,         32) /* ItemUseable - Remote */
     , (2000029,  19,          0) /* Value */
     , (2000029,  93,       3092) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity, LightingOn */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000029,   1, True ) /* Stuck */
     , (2000029,  19, False) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000029,  54,       5) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000029,   1, 'Frays Campfire') /* Name */
     , (2000029,  14, 'Double-click to light this campfire. It is yours alone: one player to a fire.') /* Use */
     , (2000029,  16, 'A campfire in the Frays. Light it and the camp is yours: the Drift''s creatures rise in this camp for you alone to fight and loot. Keep killing and your Heat climbs, for more experience, luminance, loot and Embers.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000029,   1, 0x020005AE) /* Setup */
     , (2000029,   8, 0x0600192F) /* Icon */;
