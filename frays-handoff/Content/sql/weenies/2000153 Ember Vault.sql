-- @owner: frays
-- @owns: wcid:2000153
-- @requires-restart: no
--
-- Frays: a locked chest by the Great Hall, opened by a Dream Key.

DELETE FROM `weenie` WHERE `class_Id` = 2000153;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000153, 'dw-dream-chest-locked', 20, '2026-11-09 18:08:49') /* Chest */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000153,   1,        512) /* ItemType - Container */
     , (2000153,   5,       9000) /* EncumbranceVal */
     , (2000153,   6,        120) /* ItemsCapacity */
     , (2000153,   7,         10) /* ContainersCapacity */
     , (2000153,   8,       3000) /* Mass */
     , (2000153,  16,         48) /* ItemUseable - ViewedRemote */
     , (2000153,  19,       2500) /* Value */
     , (2000153,  37,        240) /* ResistItemAppraisal */
     , (2000153,  38,       9999) /* ResistLockpick */
     , (2000153,  81,          5) /* MaxGeneratedObjects */
     , (2000153,  82,          5) /* InitGeneratedObjects */
     , (2000153,  83,          2) /* ActivationResponse - Use */
     , (2000153,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */
     , (2000153,  96,        500) /* EncumbranceCapacity */
     , (2000153, 100,          1) /* GeneratorType - Relative */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000153,   1, True ) /* Stuck */
     , (2000153,   2, False) /* Open */
     , (2000153,   3, True ) /* Locked */
     , (2000153,  12, True ) /* ReportCollisions */
     , (2000153,  13, False) /* Ethereal */
     , (2000153,  33, False) /* ResetMessagePending */
     , (2000153,  34, False) /* DefaultOpen */
     , (2000153,  35, True ) /* DefaultLocked */
     , (2000153,  86, True ) /* ChestRegenOnClose */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000153,  39,     1.1) /* DefaultScale */
     , (2000153,  54,       1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000153,   1, 'Ember Vault') /* Name */
     , (2000153,  12, 'keychestdream') /* LockCode */
     , (2000153,  14, 'Use a Vault Key on this chest to open it.') /* Use */
     , (2000153,  16, 'A chest of the finest mixed gear, dreamt up by the Frays.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000153,   1, 0x02000F34) /* Setup */
     , (2000153,   2, 0x09000123) /* MotionTable */
     , (2000153,   3, 0x20000021) /* SoundTable */
     , (2000153,   8, 0x06002AD0) /* Icon */
     , (2000153,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_generator` (`object_Id`, `probability`, `weenie_Class_Id`, `delay`, `init_Create`, `max_Create`, `when_Create`, `where_Create`, `stack_Size`, `palette_Id`, `shade`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000153, -1, 2000153, 0, 1, 1, 2, 72, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0) /* Generate RANDOMLY GENERATED TREASURE from Loot Tier 8 from Death Treasure Table id: 2000153 (x1 up to max of 1) - Regenerate upon PickUp - Location to (re)Generate: ContainTreasure */;
