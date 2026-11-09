-- @unit: quests/MaraeHuntNorthMattekarTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthMattekarTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthMattekarTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthMattekarTask', 0, -1, 'Active Mattekar hunt from Hea Ngarekau the Mattekar Hunter (value = which species).', '2026-11-09 00:00:00');
