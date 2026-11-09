-- @unit: quests/MaraeHuntPlateauGromnieTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGromnieTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGromnieTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGromnieTask', 0, -1, 'Active Gromnie hunt from Hea Weneria the Gromnie Hunter (value = which species).', '2026-11-09 00:00:00');
