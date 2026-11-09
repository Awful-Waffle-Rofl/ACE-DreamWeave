-- @unit: quests/MaraeHuntPlateauZefirTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauZefirTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauZefirTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauZefirTask', 0, -1, 'Active Zefir hunt from Hea Rukenua the Zefir Hunter (value = which species).', '2026-11-09 00:00:00');
