-- @unit: quests/MaraeHuntSouthSiraluunTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunTask', 0, -1, 'Active Siraluun hunt from Hea Raukawe the Siraluun Hunter (value = which species).', '2026-11-09 00:00:00');
