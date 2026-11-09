-- @unit: quests/MaraeHuntSouthCarenziWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthCarenziWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthCarenziWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthCarenziWait', 72000, -1, 'Claimed today''s Carenzi hunt from Hea Tarekeh the Carenzi Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
