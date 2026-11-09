-- @unit: quests/MaraeHuntPlateauGromnieBrassGromnie
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGromnieBrassGromnie
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGromnieBrassGromnie';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGromnieBrassGromnie', 0, 50, 'Hunting Drumtaken Brass Gromnie for Hea Weneria the Gromnie Hunter on Marae Lassel.', '2026-11-09 00:00:00');
