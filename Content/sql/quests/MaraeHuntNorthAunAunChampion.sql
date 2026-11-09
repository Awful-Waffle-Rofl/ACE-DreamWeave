-- @unit: quests/MaraeHuntNorthAunAunChampion
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthAunAunChampion
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthAunAunChampion';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthAunAunChampion', 0, 50, 'Hunting Aun Champion for Hea Tamaneru the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
