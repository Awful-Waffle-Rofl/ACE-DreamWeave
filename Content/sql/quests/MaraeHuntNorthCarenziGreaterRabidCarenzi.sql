-- @unit: quests/MaraeHuntNorthCarenziGreaterRabidCarenzi
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthCarenziGreaterRabidCarenzi
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthCarenziGreaterRabidCarenzi';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthCarenziGreaterRabidCarenzi', 0, 50, 'Hunting Drumtaken Greater Rabid Carenzi for Hea Marenua the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
