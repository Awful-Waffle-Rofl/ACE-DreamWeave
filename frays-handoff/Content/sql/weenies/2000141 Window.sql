-- @owner: frays
-- @owns: wcid:2000141
-- @requires-restart: no
--
-- Frays: the town door 2000123 with its face repainted (texture map 0x05002F96 to 0x0500287E) as the lattice window the town houses use, placed in every town house window, sized to it.

DELETE FROM `weenie` WHERE `class_Id` = 2000141;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000141, 'dw-fray-window', 19, '2026-11-01 16:56:43') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000141,   1,        128) /* ItemType - Misc */
     , (2000141,   8,        500) /* Mass */
     , (2000141,  16,         32) /* ItemUseable - Remote */
     , (2000141,  19,          0) /* Value */
     , (2000141,  38,         50) /* ResistLockpick */
     , (2000141,  93,          8) /* PhysicsState - ReportCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000141,   1, True ) /* Stuck */
     , (2000141,   2, False) /* Open */
     , (2000141,   3, True ) /* Locked */
     , (2000141,  11, False) /* IgnoreCollisions */
     , (2000141,  12, True ) /* ReportCollisions */
     , (2000141,  13, False) /* Ethereal */
     , (2000141,  14, False) /* GravityStatus */
     , (2000141,  33, False) /* ResetMessagePending */
     , (2000141,  34, False) /* DefaultOpen */
     , (2000141,  35, True ) /* DefaultLocked */
     , (2000141, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000141,  11,     300) /* ResetInterval */
     , (2000141,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000141,   1, 'Window') /* Name */
     , (2000141,  14, 'Use this item to open it.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000141,   1, 0x020019FF) /* Setup */
     , (2000141,   2, 0x09000202) /* MotionTable */
     , (2000141,   3, 0x20000023) /* SoundTable */
     , (2000141,   8, 0x06001317) /* Icon */
     , (2000141,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_texture_map` (`object_Id`, `index`, `old_Id`, `new_Id`)
VALUES (2000141, 0, 83898262, 83896446);
