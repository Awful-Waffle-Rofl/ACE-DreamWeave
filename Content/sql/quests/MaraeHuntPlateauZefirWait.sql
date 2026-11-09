-- @unit: quests/MaraeHuntPlateauZefirWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauZefirWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauZefirWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauZefirWait', 72000, -1, 'Claimed today''s Zefir hunt from Hea Rukenua the Zefir Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
