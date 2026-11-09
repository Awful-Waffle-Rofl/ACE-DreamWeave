-- @unit: quests/MaraeHuntSouthZefirWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthZefirWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthZefirWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthZefirWait', 72000, -1, 'Claimed today''s Zefir hunt from Hea Aroheka the Zefir Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
