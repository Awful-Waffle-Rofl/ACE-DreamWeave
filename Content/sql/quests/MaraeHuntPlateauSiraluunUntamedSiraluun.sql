-- @unit: quests/MaraeHuntPlateauSiraluunUntamedSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauSiraluunUntamedSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauSiraluunUntamedSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauSiraluunUntamedSiraluun', 0, 50, 'Hunting Drumtaken Untamed Siraluun for Hea Taraneki the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
