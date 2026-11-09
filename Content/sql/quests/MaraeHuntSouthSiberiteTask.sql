-- @unit: quests/MaraeHuntSouthSiberiteTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiberiteTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiberiteTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiberiteTask', 0, -1, 'Active Siberite hunt from Hea Tuhiraki the Siberite Hunter (value = which species).', '2026-11-09 00:00:00');
