-- @unit: quests/MaraeHuntSouthSiberiteWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiberiteWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiberiteWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiberiteWait', 72000, -1, 'Claimed today''s Siberite hunt from Hea Tuhiraki the Siberite Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
