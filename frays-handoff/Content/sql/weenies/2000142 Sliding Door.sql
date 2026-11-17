-- @owner: frays
-- @owns: wcid:2000142
-- @requires-restart: no
--
-- Frays: retail 720's Sho sliding door, locked shut with no key, in the doorways of the Fray town's Sho-style houses (Town House 11, in its side walls where retail puts it).

DELETE FROM `weenie` WHERE `class_Id` = 2000142;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000142, 'dw-fray-town-door-sho', 19, '2026-11-01 17:01:02') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000142,   1,        128) /* ItemType - Misc */
     , (2000142,   8,        500) /* Mass */
     , (2000142,  16,         32) /* ItemUseable - Remote */
     , (2000142,  19,          0) /* Value */
     , (2000142,  38,         50) /* ResistLockpick */
     , (2000142,  93,          8) /* PhysicsState - ReportCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000142,   1, True ) /* Stuck */
     , (2000142,   2, False) /* Open */
     , (2000142,   3, True ) /* Locked */
     , (2000142,  11, False) /* IgnoreCollisions */
     , (2000142,  12, True ) /* ReportCollisions */
     , (2000142,  13, False) /* Ethereal */
     , (2000142,  14, False) /* GravityStatus */
     , (2000142,  33, False) /* ResetMessagePending */
     , (2000142,  34, False) /* DefaultOpen */
     , (2000142,  35, True ) /* DefaultLocked */
     , (2000142, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000142,  11,     300) /* ResetInterval */
     , (2000142,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000142,   1, 'Sliding Door') /* Name */
     , (2000142,  14, 'Use this item to open it.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000142,   1, 0x02000310) /* Setup */
     , (2000142,   2, 0x0900002B) /* MotionTable */
     , (2000142,   3, 0x20000033) /* SoundTable */
     , (2000142,   8, 0x06001317) /* Icon */
     , (2000142,  22, 0x3400002B) /* PhysicsEffectTable */;
