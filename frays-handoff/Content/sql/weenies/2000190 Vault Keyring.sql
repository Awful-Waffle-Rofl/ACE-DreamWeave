-- @owner: frays
-- @owns: wcid:2000190
-- @requires-restart: no
--
-- Frays: holds a player's Vault Keys (2000152) in one slot.

DELETE FROM `weenie` WHERE `class_Id` = 2000190;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000190, 'dw-vault-keyring', 22, '2026-11-07 00:48:06') /* Key */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000190,   1,      16384) /* ItemType - Key */
     , (2000190,   5,         30) /* EncumbranceVal */
     , (2000190,  16,    2097160) /* ItemUseable - SourceContainedTargetRemote */
     , (2000190,  18,         64) /* UiEffects - Lightning */
     , (2000190,  19,          0) /* Value */
     , (2000190,  33,          1) /* Bonded - Bonded */
     , (2000190,  91,       1000) /* MaxStructure */
     , (2000190,  92,          1) /* Structure */
     , (2000190,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000190,  94,        640) /* TargetType - LockableMagicTarget */
     , (2000190, 114,          1) /* Attuned - Attuned */
     , (2000190, 369,        150) /* UseRequiresLevel */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000190,  22, True ) /* Inscribable */
     , (2000190,  69, False) /* IsSellable */
     , (2000190,  99, False) /* Ivoryable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000190,   1, 'Vault Keyring') /* Name */
     , (2000190,  13, 'keychestdream') /* KeyCode */
     , (2000190,  14, 'Use this keyring on an Ember Vault by the Great Hall to open it. Each use spends one Vault Key; its uses are the keys on the ring. Vault Keys you earn go onto it by themselves.') /* Use */
     , (2000190,  16, 'A ring of smoke-dark iron. The keys on it clink faintly, warm to the touch.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000190,   1, 0x02000166) /* Setup */
     , (2000190,   3, 0x20000014) /* SoundTable */
     , (2000190,   8, 0x060029D0) /* Icon */
     , (2000190,  22, 0x3400002B) /* PhysicsEffectTable */;
