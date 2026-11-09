-- @unit: quests/MaraeHuntPlateauGolemWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGolemWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGolemWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGolemWait', 72000, -1, 'Claimed today''s Golem hunt from Hea Koruani the Golem Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
