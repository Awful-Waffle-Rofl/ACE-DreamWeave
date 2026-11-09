-- @unit: quests/MaraeHuntPlateauGolemTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGolemTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGolemTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGolemTask', 0, -1, 'Active Golem hunt from Hea Koruani the Golem Hunter (value = which species).', '2026-11-09 00:00:00');
