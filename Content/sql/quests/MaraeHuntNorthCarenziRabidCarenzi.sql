-- @unit: quests/MaraeHuntNorthCarenziRabidCarenzi
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthCarenziRabidCarenzi
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthCarenziRabidCarenzi';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthCarenziRabidCarenzi', 0, 50, 'Hunting Drumtaken Rabid Carenzi for Hea Marenua the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
