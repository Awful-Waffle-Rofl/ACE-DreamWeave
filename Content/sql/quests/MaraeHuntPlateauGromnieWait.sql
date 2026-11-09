-- @unit: quests/MaraeHuntPlateauGromnieWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGromnieWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGromnieWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGromnieWait', 72000, -1, 'Claimed today''s Gromnie hunt from Hea Weneria the Gromnie Hunter (Plateau camp, Marae Lassel).', '2026-11-09 00:00:00');
