-- @unit: quests/MaraeHuntPlateauUrsuinWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauUrsuinWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauUrsuinWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauUrsuinWait', 72000, -1, 'Claimed today''s Ursuin hunt from Hea Tamarau the Ursuin Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
