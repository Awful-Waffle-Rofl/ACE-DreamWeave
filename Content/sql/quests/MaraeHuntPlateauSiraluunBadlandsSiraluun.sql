-- @unit: quests/MaraeHuntPlateauSiraluunBadlandsSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauSiraluunBadlandsSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauSiraluunBadlandsSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauSiraluunBadlandsSiraluun', 0, 50, 'Hunting Drumtaken Badlands Siraluun for Hea Taraneki the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
