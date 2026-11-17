-- @owner: frays
-- @owns: wcid:2000123
-- @requires-restart: no
--
-- Frays: Arwic's own door (retail 412), locked shut with no key, in the doorways of the Fray town's look-alike buildings round the Temple of Xik Minru so players do not see into their empty shells.

DELETE FROM `weenie` WHERE `class_Id` = 2000123;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000123, 'dw-fray-town-door', 19, '2026-11-01 15:02:28') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000123,   1,        128) /* ItemType - Misc */
     , (2000123,   8,        500) /* Mass */
     , (2000123,  16,         32) /* ItemUseable - Remote */
     , (2000123,  19,          0) /* Value */
     , (2000123,  38,         50) /* ResistLockpick */
     , (2000123,  93,          8) /* PhysicsState - ReportCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000123,   1, True ) /* Stuck */
     , (2000123,   2, False) /* Open */
     , (2000123,   3, True ) /* Locked */
     , (2000123,  11, False) /* IgnoreCollisions */
     , (2000123,  12, True ) /* ReportCollisions */
     , (2000123,  13, False) /* Ethereal */
     , (2000123,  14, False) /* GravityStatus */
     , (2000123,  33, False) /* ResetMessagePending */
     , (2000123,  34, False) /* DefaultOpen */
     , (2000123,  35, True ) /* DefaultLocked */
     , (2000123, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000123,  11,     300) /* ResetInterval */
     , (2000123,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000123,   1, 'Door') /* Name */
     , (2000123,  14, 'Use this item to open it.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000123,   1, 0x020019FF) /* Setup */
     , (2000123,   2, 0x09000202) /* MotionTable */
     , (2000123,   3, 0x20000023) /* SoundTable */
     , (2000123,   8, 0x06001317) /* Icon */
     , (2000123,  22, 0x3400002B) /* PhysicsEffectTable */;
