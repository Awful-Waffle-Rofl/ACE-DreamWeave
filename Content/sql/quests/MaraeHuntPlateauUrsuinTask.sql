-- @unit: quests/MaraeHuntPlateauUrsuinTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauUrsuinTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauUrsuinTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauUrsuinTask', 0, -1, 'Active Ursuin hunt from Hea Tamarau the Ursuin Hunter (value = which species).', '2026-11-09 00:00:00');
