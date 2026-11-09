-- @unit: quests/MaraeHuntNorthOlthoiOlthoiEvisceratorGrub
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthOlthoiOlthoiEvisceratorGrub
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthOlthoiOlthoiEvisceratorGrub';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthOlthoiOlthoiEvisceratorGrub', 0, 50, 'Hunting Olthoi Eviscerator Grub for Hea Hakerea the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
