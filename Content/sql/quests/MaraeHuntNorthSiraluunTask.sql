-- @unit: quests/MaraeHuntNorthSiraluunTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiraluunTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiraluunTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiraluunTask', 0, -1, 'Active Siraluun hunt from Hea Kahureka the Siraluun Hunter (value = which species).', '2026-11-09 00:00:00');
