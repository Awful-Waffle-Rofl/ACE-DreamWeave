-- @unit: quests/MaraeHuntNorthOlthoiOlthoiLegionary
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthOlthoiOlthoiLegionary
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthOlthoiOlthoiLegionary';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthOlthoiOlthoiLegionary', 0, 50, 'Hunting Olthoi Legionary for Hea Hakerea the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
