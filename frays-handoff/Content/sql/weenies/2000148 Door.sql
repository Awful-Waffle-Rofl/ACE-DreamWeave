-- @owner: frays
-- @owns: wcid:2000148
-- @requires-restart: no
--
-- Frays: retail 25669's secret door, a slab of dark grey stone brick (setup 0x02000FF1), locked shut with no key, sealing the doorways of the Fray village's stone watchtowers and wall pieces (FraysHouses.cs, the "stone" door style;.

DELETE FROM `weenie` WHERE `class_Id` = 2000148;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000148, 'dw-fray-town-door-stone', 19, '2026-11-02 00:46:02') /* Door */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000148,   1,        128) /* ItemType - Misc */
     , (2000148,   8,        500) /* Mass */
     , (2000148,  16,          1) /* ItemUseable - No */
     , (2000148,  19,          0) /* Value */
     , (2000148,  83,          2) /* ActivationResponse - Use */
     , (2000148,  93,         24) /* PhysicsState - ReportCollisions, IgnoreCollisions */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000148,   1, True ) /* Stuck */
     , (2000148,   2, False) /* Open */
     , (2000148,   3, True ) /* Locked */
     , (2000148,  12, True ) /* ReportCollisions */
     , (2000148,  13, False) /* Ethereal */
     , (2000148,  14, False) /* GravityStatus */
     , (2000148,  33, False) /* ResetMessagePending */
     , (2000148,  34, False) /* DefaultOpen */
     , (2000148,  35, True ) /* DefaultLocked */
     , (2000148, 9069, True ) /* SpawnAtExactPosition */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000148,  11,     300) /* ResetInterval */
     , (2000148,  54,       2) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000148,   1, 'Door') /* Name */
     , (2000148,  14, 'This door cannot be activated from here.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000148,   1, 0x02000FF1) /* Setup */
     , (2000148,   2, 0x0900013B) /* MotionTable */
     , (2000148,   3, 0x2000008D) /* SoundTable */
     , (2000148,   8, 0x06001317) /* Icon */
     , (2000148,  22, 0x3400002B) /* PhysicsEffectTable */;
