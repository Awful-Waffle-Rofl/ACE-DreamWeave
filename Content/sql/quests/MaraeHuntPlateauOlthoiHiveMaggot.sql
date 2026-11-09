-- @unit: quests/MaraeHuntPlateauOlthoiHiveMaggot
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiHiveMaggot
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiHiveMaggot';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiHiveMaggot', 0, 50, 'Hunting Hive Maggot for Hea Varenkeh the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
