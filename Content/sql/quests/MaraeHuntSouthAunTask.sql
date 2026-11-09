-- @unit: quests/MaraeHuntSouthAunTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunTask', 0, -1, 'Active Aun hunt from Hea Wairena the Aun Hunter (value = which species).', '2026-11-09 00:00:00');
