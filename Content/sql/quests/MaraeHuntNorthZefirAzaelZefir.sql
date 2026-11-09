-- @unit: quests/MaraeHuntNorthZefirAzaelZefir
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthZefirAzaelZefir
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthZefirAzaelZefir';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthZefirAzaelZefir', 0, 50, 'Hunting Drumtaken Azael Zefir for Hea Orewhiti the Zefir Hunter on Marae Lassel.', '2026-11-09 00:00:00');
