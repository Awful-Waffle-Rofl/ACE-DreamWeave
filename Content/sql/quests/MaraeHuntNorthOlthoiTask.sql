-- @unit: quests/MaraeHuntNorthOlthoiTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthOlthoiTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthOlthoiTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthOlthoiTask', 0, -1, 'Active Olthoi hunt from Hea Hakerea the Olthoi Hunter (value = which species).', '2026-11-09 00:00:00');
