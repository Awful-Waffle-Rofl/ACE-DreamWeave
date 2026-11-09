-- @unit: quests/MaraeHuntSouthSiraluunLittoralSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunLittoralSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunLittoralSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunLittoralSiraluun', 0, 50, 'Hunting Drumtaken Littoral Siraluun for Hea Raukawe the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
