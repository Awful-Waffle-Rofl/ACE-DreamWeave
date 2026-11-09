-- @unit: quests/MaraeHuntNorthAunAunElderShaman
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthAunAunElderShaman
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthAunAunElderShaman';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthAunAunElderShaman', 0, 50, 'Hunting Aun Elder Shaman for Hea Tamaneru the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
