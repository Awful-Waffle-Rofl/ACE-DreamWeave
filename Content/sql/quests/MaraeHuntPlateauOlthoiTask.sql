-- @unit: quests/MaraeHuntPlateauOlthoiTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiTask', 0, -1, 'Active Olthoi hunt from Hea Varenkeh the Olthoi Hunter (value = which species).', '2026-11-09 00:00:00');
