-- @unit: quests/MaraeHuntPlateauCarenziSavageCarenzi
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauCarenziSavageCarenzi
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauCarenziSavageCarenzi';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauCarenziSavageCarenzi', 0, 50, 'Hunting Drumtaken Savage Carenzi for Hea Kurelan the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
