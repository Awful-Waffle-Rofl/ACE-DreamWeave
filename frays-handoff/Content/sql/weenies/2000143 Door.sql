-- @owner: frays
-- @owns: wcid:2000143
-- @requires-restart: no
--
-- Frays: retail 1148's Gharu double door, locked shut with no key, in the doorways of the Fray town's Gharu-style houses (Town House 9).

DELETE FROM `weenie` WHERE `class_Id` = 2000143;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000143, 'dw-fray-town-door-gharu', 19, '2026-11-01 17:01:02') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000143,   1,        128) /* ItemType - Misc */
     , (2000143,   8,        500) /* Mass */
     , (2000143,  16,         32) /* ItemUseable - Remote */
     , (2000143,  19,          0) /* Value */
     , (2000143,  38,         50) /* ResistLockpick */
     , (2000143,  93,          8) /* PhysicsState - ReportCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000143,   1, True ) /* Stuck */
     , (2000143,   2, False) /* Open */
     , (2000143,   3, True ) /* Locked */
     , (2000143,  11, False) /* IgnoreCollisions */
     , (2000143,  12, True ) /* ReportCollisions */
     , (2000143,  13, False) /* Ethereal */
     , (2000143,  14, False) /* GravityStatus */
     , (2000143,  33, False) /* ResetMessagePending */
     , (2000143,  34, False) /* DefaultOpen */
     , (2000143,  35, True ) /* DefaultLocked */
     , (2000143, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000143,  11,     300) /* ResetInterval */
     , (2000143,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000143,   1, 'Door') /* Name */
     , (2000143,  14, 'Use this item to open it.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000143,   1, 0x0200037E) /* Setup */
     , (2000143,   2, 0x0900002F) /* MotionTable */
     , (2000143,   3, 0x20000023) /* SoundTable */
     , (2000143,   8, 0x06001317) /* Icon */
     , (2000143,  22, 0x3400002B) /* PhysicsEffectTable */;
