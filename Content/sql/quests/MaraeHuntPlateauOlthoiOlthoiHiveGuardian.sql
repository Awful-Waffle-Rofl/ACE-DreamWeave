-- @unit: quests/MaraeHuntPlateauOlthoiOlthoiHiveGuardian
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiOlthoiHiveGuardian
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiOlthoiHiveGuardian';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiOlthoiHiveGuardian', 0, 50, 'Hunting Olthoi Hive Guardian for Hea Varenkeh the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
