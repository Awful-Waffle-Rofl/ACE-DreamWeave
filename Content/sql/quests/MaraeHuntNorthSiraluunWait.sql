-- @unit: quests/MaraeHuntNorthSiraluunWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiraluunWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiraluunWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiraluunWait', 72000, -1, 'Claimed today''s Siraluun hunt from Hea Kahureka the Siraluun Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
