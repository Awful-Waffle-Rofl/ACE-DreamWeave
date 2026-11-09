-- @unit: quests/MaraeHuntPlateauSiraluunWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauSiraluunWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauSiraluunWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauSiraluunWait', 72000, -1, 'Claimed today''s Siraluun hunt from Hea Taraneki the Siraluun Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
