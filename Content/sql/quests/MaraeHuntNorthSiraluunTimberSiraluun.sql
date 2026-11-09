-- @unit: quests/MaraeHuntNorthSiraluunTimberSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiraluunTimberSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiraluunTimberSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiraluunTimberSiraluun', 0, 50, 'Hunting Drumtaken Timber Siraluun for Hea Kahureka the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
