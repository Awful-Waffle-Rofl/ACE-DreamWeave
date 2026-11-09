-- @unit: quests/MaraeHuntPlateauSiraluunTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauSiraluunTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauSiraluunTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauSiraluunTask', 0, -1, 'Active Siraluun hunt from Hea Taraneki the Siraluun Hunter (value = which species).', '2026-11-09 00:00:00');
