-- @unit: quests/MaraeHuntNorthUrsuinTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthUrsuinTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthUrsuinTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthUrsuinTask', 0, -1, 'Active Ursuin hunt from Hea Narekua the Ursuin Hunter (value = which species).', '2026-11-09 00:00:00');
