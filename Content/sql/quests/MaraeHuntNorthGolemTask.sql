-- @unit: quests/MaraeHuntNorthGolemTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGolemTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGolemTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGolemTask', 0, -1, 'Active Golem hunt from Hea Hineroa the Golem Hunter (value = which species).', '2026-11-09 00:00:00');
