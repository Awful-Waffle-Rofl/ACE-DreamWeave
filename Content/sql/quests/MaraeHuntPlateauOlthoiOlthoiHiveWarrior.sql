-- @unit: quests/MaraeHuntPlateauOlthoiOlthoiHiveWarrior
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiOlthoiHiveWarrior
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiOlthoiHiveWarrior';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiOlthoiHiveWarrior', 0, 50, 'Hunting Olthoi Hive Warrior for Hea Varenkeh the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
