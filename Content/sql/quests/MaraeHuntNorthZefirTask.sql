-- @unit: quests/MaraeHuntNorthZefirTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthZefirTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthZefirTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthZefirTask', 0, -1, 'Active Zefir hunt from Hea Orewhiti the Zefir Hunter (value = which species).', '2026-11-09 00:00:00');
