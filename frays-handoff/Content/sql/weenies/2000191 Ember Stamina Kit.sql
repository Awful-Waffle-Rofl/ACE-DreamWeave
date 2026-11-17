-- @owner: frays
-- @owns: wcid:2000191
-- @requires-restart: no
--
-- Frays: an infinite stamina kit, sold by the Ember Trader (2000031) for 10000 Embers (Value 20000 at SellPrice 0.5).

DELETE FROM `weenie` WHERE `class_Id` = 2000191;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000191, 'dw-ember-stamina-kit', 28, '2026-11-08 15:08:39') /* Healer */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000191,   1,        128) /* ItemType - Misc */
     , (2000191,   5,          5) /* EncumbranceVal */
     , (2000191,   8,          5) /* Mass */
     , (2000191,  16,    2228232) /* ItemUseable - SourceContainedTargetRemoteOrSelf */
     , (2000191,  19,      20000) /* Value */
     , (2000191,  33,         -1) /* Bonded - Slippery */
     , (2000191,  89,          4) /* BoosterEnum - Stamina */
     , (2000191,  90,        100) /* BoostValue */
     , (2000191,  92,         -1) /* Structure */
     , (2000191,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000191,  94,         16) /* TargetType - Creature */
     , (2000191, 151,          2) /* HookType - Wall */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000191,  11, True ) /* IgnoreCollisions */
     , (2000191,  13, True ) /* Ethereal */
     , (2000191,  14, True ) /* GravityStatus */
     , (2000191,  19, True ) /* Attackable */
     , (2000191,  22, True ) /* Inscribable */
     , (2000191,  63, True ) /* UnlimitedUse */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000191, 100,     1.6) /* HealkitMod */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000191,   1, 'Ember Stamina Kit') /* Name */
     , (2000191,  16, 'Use this item to recover your Stamina. It will never run out of uses. A warm coal glows in its clasp.') /* LongDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000191,   1, 0x020002FA) /* Setup */
     , (2000191,   6, 0x040008B4) /* PaletteBase */
     , (2000191,   7, 0x10000416) /* ClothingBase */
     , (2000191,   8, 0x06005B6D) /* Icon */;
