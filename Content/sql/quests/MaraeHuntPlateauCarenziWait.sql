-- @unit: quests/MaraeHuntPlateauCarenziWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauCarenziWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauCarenziWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauCarenziWait', 72000, -1, 'Claimed today''s Carenzi hunt from Hea Kurelan the Carenzi Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
