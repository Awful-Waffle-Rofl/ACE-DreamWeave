-- @owner: frays
-- @owns: wcid:2000152
-- @requires-restart: no
--
-- Frays: opens one Ember Vault (2000153).

DELETE FROM `weenie` WHERE `class_Id` = 2000152;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000152, 'dw-dream-key', 22, '2026-11-02 14:04:55') /* Key */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000152,   1,      16384) /* ItemType - Key */
     , (2000152,   5,         30) /* EncumbranceVal */
     , (2000152,  16,    2097160) /* ItemUseable - SourceContainedTargetRemote */
     , (2000152,  18,         64) /* UiEffects - Lightning */
     , (2000152,  19,       1000) /* Value */
     , (2000152,  33,          1) /* Bonded - Bonded */
     , (2000152,  91,          1) /* MaxStructure */
     , (2000152,  92,          1) /* Structure */
     , (2000152,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000152,  94,        640) /* TargetType - LockableMagicTarget */
     , (2000152, 114,          1) /* Attuned - Attuned */
     , (2000152, 369,        150) /* UseRequiresLevel */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000152,  22, True ) /* Inscribable */
     , (2000152,  69, False) /* IsSellable */
     , (2000152,  99, False) /* Ivoryable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000152,   1, 'Vault Key') /* Name */
     , (2000152,  13, 'keychestdream') /* KeyCode */
     , (2000152,  14, 'Use this key to open an Ember Vault by the Great Hall.') /* Use */
     , (2000152,  16, 'A key that smells faintly of woodsmoke and sleep.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000152,   1, 0x02000160) /* Setup */
     , (2000152,   3, 0x20000014) /* SoundTable */
     , (2000152,   8, 0x06007409) /* Icon */
     , (2000152,  22, 0x3400002B) /* PhysicsEffectTable */;
