-- @unit: quests/MaraeHuntNorthSiraluunKithlessSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiraluunKithlessSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiraluunKithlessSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiraluunKithlessSiraluun', 0, 50, 'Hunting Drumtaken Kithless Siraluun for Hea Kahureka the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
