-- @owner: frays
-- @owns: wcid:2000158
-- @requires-restart: no
--
-- Frays: the living book.

DELETE FROM `weenie` WHERE `class_Id` = 2000158;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000158, 'dw-fray-journal', 8, '2026-11-02 22:43:28') /* Book */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000158,   1,       8192) /* ItemType - Writable */
     , (2000158,   5,         10) /* EncumbranceVal */
     , (2000158,   8,        200) /* Mass */
     , (2000158,   9,          0) /* ValidLocations - None */
     , (2000158,  16,          8) /* ItemUseable - Contained */
     , (2000158,  19,          0) /* Value */
     , (2000158,  33,          1) /* Bonded - Bonded */
     , (2000158,  93,       1044) /* PhysicsState - Ethereal, IgnoreCollisions, Gravity */
     , (2000158, 114,          1) /* Attuned - Attuned */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000158,  22, False) /* Inscribable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000158,  39,    1.22) /* DefaultScale */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000158,   1, 'Fray Journal') /* Name */
     , (2000158,  15, 'Your Frays record: companion, pets, Codex and achievements. It rewrites itself each time you open it.') /* ShortDesc */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000158,   1, 0x02000153) /* Setup */
     , (2000158,   3, 0x20000014) /* SoundTable */
     , (2000158,   8, 0x060012D5) /* Icon */
     , (2000158,  22, 0x3400002B) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_book` (`object_Id`, `max_Num_Pages`, `max_Num_Chars_Per_Page`)
VALUES (2000158, 40, 1000);
