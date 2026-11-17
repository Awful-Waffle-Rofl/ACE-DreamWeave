-- @owner: frays
-- @owns: wcid:2000194
-- @requires-restart: no
--
-- Frays: a readable sign in the Great Hall listing the latest Snarl Trophies (finder, family, date).

DELETE FROM `weenie` WHERE `class_Id` = 2000194;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000194, 'dw-snarl-trophy-wall', 8, '2026-11-09 01:23:28') /* Book */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000194,   1,       8192) /* ItemType - Writable */
     , (2000194,   5,       9000) /* EncumbranceVal */
     , (2000194,   8,       1800) /* Mass */
     , (2000194,  16,         48) /* ItemUseable - ViewedRemote */
     , (2000194,  19,        125) /* Value */
     , (2000194,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000194,   1, True ) /* Stuck */
     , (2000194,  12, True ) /* ReportCollisions */
     , (2000194,  13, False) /* Ethereal */
     , (2000194,  22, False) /* Inscribable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000194,  54,       5) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000194,   1, 'SNARL TROPHY WALL') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000194,   1, 0x0200062E) /* Setup */
     , (2000194,   8, 0x060012D3) /* Icon */;

INSERT INTO `weenie_properties_book` (`object_Id`, `max_Num_Pages`, `max_Num_Chars_Per_Page`)
VALUES (2000194, 1, 1000);

INSERT INTO `weenie_properties_book_page_data` (`object_Id`, `page_Id`, `author_Id`, `author_Name`, `author_Account`, `ignore_Author`, `page_Text`)
VALUES (2000194, 0, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'SNARL TROPHY WALL

No Snarl Trophy has been found yet.');
