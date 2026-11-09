-- @unit: quests/MaraeHuntNorthCarenziGreaterFeralCarenzi
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthCarenziGreaterFeralCarenzi
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthCarenziGreaterFeralCarenzi';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthCarenziGreaterFeralCarenzi', 0, 50, 'Hunting Drumtaken Greater Feral Carenzi for Hea Marenua the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
