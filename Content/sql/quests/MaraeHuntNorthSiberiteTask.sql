-- @unit: quests/MaraeHuntNorthSiberiteTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiberiteTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiberiteTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiberiteTask', 0, -1, 'Active Siberite hunt from Hea Zanuah the Siberite Hunter (value = which species).', '2026-11-09 00:00:00');
