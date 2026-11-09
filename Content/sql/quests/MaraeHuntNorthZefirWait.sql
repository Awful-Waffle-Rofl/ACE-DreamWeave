-- @unit: quests/MaraeHuntNorthZefirWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthZefirWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthZefirWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthZefirWait', 72000, -1, 'Claimed today''s Zefir hunt from Hea Orewhiti the Zefir Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
