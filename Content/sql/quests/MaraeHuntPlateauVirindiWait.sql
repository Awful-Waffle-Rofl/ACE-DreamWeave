-- @unit: quests/MaraeHuntPlateauVirindiWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauVirindiWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauVirindiWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauVirindiWait', 72000, -1, 'Claimed today''s Virindi hunt from Hea Ruatane the Virindi Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
