-- @unit: quests/MaraeHuntNorthGolemWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGolemWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGolemWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGolemWait', 72000, -1, 'Claimed today''s Golem hunt from Hea Hineroa the Golem Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
