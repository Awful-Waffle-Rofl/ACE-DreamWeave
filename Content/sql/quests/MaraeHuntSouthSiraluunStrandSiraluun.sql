-- @unit: quests/MaraeHuntSouthSiraluunStrandSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunStrandSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunStrandSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunStrandSiraluun', 0, 50, 'Hunting Drumtaken Strand Siraluun for Hea Raukawe the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
