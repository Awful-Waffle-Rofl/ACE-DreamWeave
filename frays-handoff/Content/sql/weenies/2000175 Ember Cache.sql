-- @owner: frays
-- @owns: wcid:2000175
-- @requires-restart: no
--
-- Retired (the Ember Vault is the one chest): a full key bar now gives a Vault Key (2000152), and using a held Ember Cache turns the stack into as many Vault Keys.

DELETE FROM `weenie` WHERE `class_Id` = 2000175;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000175, 'dw-ember-cache', 51, '2026-11-04 10:58:38') /* Stackable */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000175,   1,        128) /* ItemType - Misc */
     , (2000175,   5,          0) /* EncumbranceVal */
     , (2000175,  11,        100) /* MaxStackSize */
     , (2000175,  12,          1) /* StackSize */
     , (2000175,  13,          0) /* StackUnitEncumbrance */
     , (2000175,  15,          0) /* StackUnitValue */
     , (2000175,  16,          8) /* ItemUseable - Contained */
     , (2000175,  19,          0) /* Value */
     , (2000175,  33,          1) /* Bonded - Bonded */
     , (2000175,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000175, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000175,  23, True ) /* DestroyOnSell */
     , (2000175,  69, False) /* IsSellable */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000175,   1, 'Ember Cache') /* Name */
     , (2000175,  14, 'Use it to open it into your pack (it needs 12 free pack slots).') /* Use */
     , (2000175,  16, 'A cache of the finest mixed gear from the Frays: the Ember Vault''s loot, opened anywhere. Your key bar fills as you camp; each full bar gives one.') /* LongDesc */
     , (2000175,  20, 'Ember Caches') /* PluralName */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000175,   1, 0x0200007C) /* Setup */
     , (2000175,   3, 0x20000014) /* SoundTable */
     , (2000175,   8, 0x06001020) /* Icon */
     , (2000175,  22, 0x3400002B) /* PhysicsEffectTable */;
