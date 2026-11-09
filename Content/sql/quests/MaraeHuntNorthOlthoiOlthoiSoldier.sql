-- @unit: quests/MaraeHuntNorthOlthoiOlthoiSoldier
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthOlthoiOlthoiSoldier
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthOlthoiOlthoiSoldier';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthOlthoiOlthoiSoldier', 0, 50, 'Hunting Olthoi Soldier for Hea Hakerea the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
