-- @unit: quests/MaraeHuntSouthGromnieTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGromnieTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGromnieTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGromnieTask', 0, -1, 'Active Gromnie hunt from Hea Teruana the Gromnie Hunter (value = which species).', '2026-11-09 00:00:00');
