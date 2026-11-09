-- @unit: quests/MaraeHuntSouthCarenziTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthCarenziTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthCarenziTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthCarenziTask', 0, -1, 'Active Carenzi hunt from Hea Tarekeh the Carenzi Hunter (value = which species).', '2026-11-09 00:00:00');
