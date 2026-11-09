-- @unit: quests/MaraeHuntNorthCarenziFeralCarenzi
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthCarenziFeralCarenzi
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthCarenziFeralCarenzi';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthCarenziFeralCarenzi', 0, 50, 'Hunting Drumtaken Feral Carenzi for Hea Marenua the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
