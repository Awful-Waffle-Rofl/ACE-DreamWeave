-- @unit: quests/MaraeHuntPlateauOlthoiOlthoiLancer
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiOlthoiLancer
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiOlthoiLancer';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiOlthoiLancer', 0, 50, 'Hunting Olthoi Lancer for Hea Varenkeh the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
