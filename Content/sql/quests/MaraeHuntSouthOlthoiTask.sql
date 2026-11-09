-- @unit: quests/MaraeHuntSouthOlthoiTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthOlthoiTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthOlthoiTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthOlthoiTask', 0, -1, 'Active Olthoi hunt from Hea Mereana the Olthoi Hunter (value = which species).', '2026-11-09 00:00:00');
