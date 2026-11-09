-- @unit: quests/MaraeHuntNorthCarenziWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthCarenziWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthCarenziWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthCarenziWait', 72000, -1, 'Claimed today''s Carenzi hunt from Hea Marenua the Carenzi Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
