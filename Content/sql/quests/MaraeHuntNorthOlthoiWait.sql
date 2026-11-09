-- @unit: quests/MaraeHuntNorthOlthoiWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthOlthoiWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthOlthoiWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthOlthoiWait', 72000, -1, 'Claimed today''s Olthoi hunt from Hea Hakerea the Olthoi Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
