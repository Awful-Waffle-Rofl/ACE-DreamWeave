-- @unit: quests/MaraeHuntPlateauCarenziTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauCarenziTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauCarenziTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauCarenziTask', 0, -1, 'Active Carenzi hunt from Hea Kurelan the Carenzi Hunter (value = which species).', '2026-11-09 00:00:00');
