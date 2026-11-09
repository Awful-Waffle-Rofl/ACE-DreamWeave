-- @unit: quests/MaraeHuntSouthSiraluunTidalSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunTidalSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunTidalSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunTidalSiraluun', 0, 50, 'Hunting Drumtaken Tidal Siraluun for Hea Raukawe the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
