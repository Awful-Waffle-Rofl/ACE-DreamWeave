-- @unit: quests/MaraeHuntSouthSiraluunLesserStrandSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunLesserStrandSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunLesserStrandSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunLesserStrandSiraluun', 0, 50, 'Hunting Drumtaken Lesser Strand Siraluun for Hea Raukawe the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
