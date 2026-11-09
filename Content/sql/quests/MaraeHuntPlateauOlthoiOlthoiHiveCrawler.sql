-- @unit: quests/MaraeHuntPlateauOlthoiOlthoiHiveCrawler
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiOlthoiHiveCrawler
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiOlthoiHiveCrawler';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiOlthoiHiveCrawler', 0, 50, 'Hunting Olthoi Hive Crawler for Hea Varenkeh the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
