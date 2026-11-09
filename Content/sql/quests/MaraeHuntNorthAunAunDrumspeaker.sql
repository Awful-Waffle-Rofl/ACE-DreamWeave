-- @unit: quests/MaraeHuntNorthAunAunDrumspeaker
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthAunAunDrumspeaker
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthAunAunDrumspeaker';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthAunAunDrumspeaker', 0, 50, 'Hunting Aun Drumspeaker for Hea Tamaneru the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
