-- @unit: quests/MaraeHuntNorthOlthoiOlthoiPiercer
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthOlthoiOlthoiPiercer
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthOlthoiOlthoiPiercer';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthOlthoiOlthoiPiercer', 0, 50, 'Hunting Olthoi Piercer for Hea Hakerea the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
