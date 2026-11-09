-- @unit: quests/MaraeHuntNorthCarenziTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthCarenziTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthCarenziTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthCarenziTask', 0, -1, 'Active Carenzi hunt from Hea Marenua the Carenzi Hunter (value = which species).', '2026-11-09 00:00:00');
