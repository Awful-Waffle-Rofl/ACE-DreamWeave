-- @owner: frays
-- @owns: wcid:2000145
-- @requires-restart: no
--
-- Frays: retail 2181's Aluvian door, locked shut with no key, on Town House 7 where retail puts it (ground floor and balcony; FraysHouses.cs).

DELETE FROM `weenie` WHERE `class_Id` = 2000145;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000145, 'dw-fray-town-door-aluvian-tower', 19, '2026-11-01 17:13:27') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000145,   1,        128) /* ItemType - Misc */
     , (2000145,   8,        500) /* Mass */
     , (2000145,  16,          1) /* ItemUseable - No */
     , (2000145,  19,          0) /* Value */
     , (2000145,  83,          2) /* ActivationResponse - Use */
     , (2000145,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000145,   1, True ) /* Stuck */
     , (2000145,   2, False) /* Open */
     , (2000145,   3, True ) /* Locked */
     , (2000145,  12, True ) /* ReportCollisions */
     , (2000145,  13, False) /* Ethereal */
     , (2000145,  14, False) /* GravityStatus */
     , (2000145,  33, False) /* ResetMessagePending */
     , (2000145,  34, False) /* DefaultOpen */
     , (2000145,  35, True ) /* DefaultLocked */
     , (2000145, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000145,  11,     300) /* ResetInterval */
     , (2000145,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000145,   1, 'Door') /* Name */
     , (2000145,  14, 'This door cannot be activated from here.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000145,   1, 0x0200027C) /* Setup */
     , (2000145,   2, 0x09000023) /* MotionTable */
     , (2000145,   3, 0x20000023) /* SoundTable */
     , (2000145,   8, 0x06001317) /* Icon */
     , (2000145,  22, 0x3400002B) /* PhysicsEffectTable */;
