-- @unit: quests/MaraeHuntPlateauOlthoiOlthoiHiveMutilator
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiOlthoiHiveMutilator
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiOlthoiHiveMutilator';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiOlthoiHiveMutilator', 0, 50, 'Hunting Olthoi Hive Mutilator for Hea Varenkeh the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
