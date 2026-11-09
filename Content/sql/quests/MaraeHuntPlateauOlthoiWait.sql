-- @unit: quests/MaraeHuntPlateauOlthoiWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauOlthoiWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauOlthoiWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauOlthoiWait', 72000, -1, 'Claimed today''s Olthoi hunt from Hea Varenkeh the Olthoi Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
