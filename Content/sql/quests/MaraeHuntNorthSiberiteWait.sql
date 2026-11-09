-- @unit: quests/MaraeHuntNorthSiberiteWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiberiteWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiberiteWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiberiteWait', 72000, -1, 'Claimed today''s Siberite hunt from Hea Zanuah the Siberite Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
