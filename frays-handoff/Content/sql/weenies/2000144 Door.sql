-- @owner: frays
-- @owns: wcid:2000144
-- @requires-restart: no
--
-- Frays: retail 722's Sho door, locked shut with no key, in Town House 11's doorways where retail puts it (FraysHouses.cs).

DELETE FROM `weenie` WHERE `class_Id` = 2000144;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000144, 'dw-fray-town-door-sho-right', 19, '2026-11-01 17:13:27') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000144,   1,        128) /* ItemType - Misc */
     , (2000144,   8,        500) /* Mass */
     , (2000144,  16,         32) /* ItemUseable - Remote */
     , (2000144,  19,          0) /* Value */
     , (2000144,  38,         50) /* ResistLockpick */
     , (2000144,  93,          8) /* PhysicsState - ReportCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000144,   1, True ) /* Stuck */
     , (2000144,   2, False) /* Open */
     , (2000144,   3, True ) /* Locked */
     , (2000144,  11, False) /* IgnoreCollisions */
     , (2000144,  12, True ) /* ReportCollisions */
     , (2000144,  13, False) /* Ethereal */
     , (2000144,  14, False) /* GravityStatus */
     , (2000144,  33, False) /* ResetMessagePending */
     , (2000144,  34, False) /* DefaultOpen */
     , (2000144,  35, True ) /* DefaultLocked */
     , (2000144, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000144,  11,     300) /* ResetInterval */
     , (2000144,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000144,   1, 'Door') /* Name */
     , (2000144,  14, 'Use this item to open it.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000144,   1, 0x02000313) /* Setup */
     , (2000144,   2, 0x09000029) /* MotionTable */
     , (2000144,   3, 0x20000033) /* SoundTable */
     , (2000144,   8, 0x06001317) /* Icon */
     , (2000144,  22, 0x3400002B) /* PhysicsEffectTable */;
