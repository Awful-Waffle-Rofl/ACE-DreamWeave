-- @unit: quests/MaraeHuntSouthZefirTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthZefirTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthZefirTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthZefirTask', 0, -1, 'Active Zefir hunt from Hea Aroheka the Zefir Hunter (value = which species).', '2026-11-09 00:00:00');
