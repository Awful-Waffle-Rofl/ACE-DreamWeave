-- @owner: frays
-- @owns: wcid:2000095
-- @requires-restart: no
--
-- Frays: a readable guide sign beside the Fray-Warden at each Fray's arrival point, a copy of the DreamWeave Marketplace sign 1006561 (same signpost model and reading panel) with six pages: start here, Heat, Snarls, your camp, rewards, commands.

DELETE FROM `weenie` WHERE `class_Id` = 2000095;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000095, 'dw-fray-guide-sign', 8, '2026-11-09 23:40:24') /* Book */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000095,   1,       8192) /* ItemType - Writable */
     , (2000095,   5,       9000) /* EncumbranceVal */
     , (2000095,   8,       1800) /* Mass */
     , (2000095,  16,         48) /* ItemUseable - ViewedRemote */
     , (2000095,  19,        125) /* Value */
     , (2000095,  93,       1048) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000095,   1, True ) /* Stuck */
     , (2000095,  12, True ) /* ReportCollisions */
     , (2000095,  13, False) /* Ethereal */
     , (2000095,  22, False) /* Inscribable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000095,  54,       5) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000095,   1, 'THE FRAYS - START HERE') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000095,   1, 0x0200062E) /* Setup */
     , (2000095,   8, 0x060012D3) /* Icon */;

INSERT INTO `weenie_properties_book` (`object_Id`, `max_Num_Pages`, `max_Num_Chars_Per_Page`)
VALUES (2000095, 6, 1000);

INSERT INTO `weenie_properties_book_page_data` (`object_Id`, `page_Id`, `author_Id`, `author_Name`, `author_Account`, `ignore_Author`, `page_Text`)
VALUES (2000095, 0, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'THE FRAYS - START HERE

1. Type /fc to see every campfire and its coords.
2. Walk to a free one and use it to light it. It is yours.
3. Kill the creatures inside the ring of wisps around it.
4. Every kill adds Heat. More Heat: tougher creatures, more XP, more Embers, better loot.
5. Type /fp any time to see your Heat, Snarl and Great Snarl bars.

Next page: Heat')
     , (2000095, 1, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'HEAT

Heat goes up as you kill. Stop killing for 2 and a half minutes and it starts to cool. Dying costs Heat.

Every 3 Heat you are offered an Ember Boon. Pick with /fp 1, /fp 2 or /fp 3, or your boon plan picks for you after 30 seconds.

Next page: Snarls')
     , (2000095, 2, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'SNARLS

Your kills fill your Snarl bar. When it is full the Snarl comes: a big red-glowing creature. Kill it for a cache of loot and a small chance at a Fray Effigy (a pet).

Everyone in the Fray fills the Great Snarl bar together. When it is full the whole Fray is told where it comes. It glows purple. Kill it for a blessing and a cache of loot.

Your camp keeps while you go to fight it.

Next page: Your camp')
     , (2000095, 3, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'YOUR CAMP

Every week the Gilded creatures carry one ward, such as Fireward (resists fire, weak to its opposite). Its name shows the ward, so bring the gear for it.

Your Snarl always comes when your bar fills.

/fray setup - your loadout
/fray boons aoe sustain - what your boon plan picks

Next page: Rewards')
     , (2000095, 4, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'REWARDS

XP and luminance come from kills and from every minute you fight. Embers come from kills and from standing by your fire.

Spend Embers at the Ember Trader. Upgrade your campfire for perk points and bigger flames.

/fray codex - your collection and achievements
/fray pet - call a collected creature
/frays recall - back to your fire

Next page: Commands')
     , (2000095, 5, 0xFFFFFFFF, 'Fray-Warden', 'prewritten', False, 'COMMANDS

/fc - campfires and coords
/fp - your bars
/fp 1, 2, 3 - pick a boon
/fray setup - your loadout
/fray codex - your collection
/fray name - name your fire
/fray sparks - spark colour
/fray help - every /fray command

Talk to the Fray-Warden for more.');
