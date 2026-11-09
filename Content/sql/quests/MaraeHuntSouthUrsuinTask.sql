-- @unit: quests/MaraeHuntSouthUrsuinTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthUrsuinTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthUrsuinTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthUrsuinTask', 0, -1, 'Active Ursuin hunt from Hea Talurea the Ursuin Hunter (value = which species).', '2026-11-09 00:00:00');
