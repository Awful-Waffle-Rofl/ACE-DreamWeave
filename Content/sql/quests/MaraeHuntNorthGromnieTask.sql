-- @unit: quests/MaraeHuntNorthGromnieTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGromnieTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGromnieTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGromnieTask', 0, -1, 'Active Gromnie hunt from Hea Pukerua the Gromnie Hunter (value = which species).', '2026-11-09 00:00:00');
