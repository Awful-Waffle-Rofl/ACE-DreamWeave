-- @unit: quests/MaraeHuntNorthAunTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthAunTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthAunTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthAunTask', 0, -1, 'Active Aun hunt from Hea Tamaneru the Aun Hunter (value = which species).', '2026-11-09 00:00:00');
