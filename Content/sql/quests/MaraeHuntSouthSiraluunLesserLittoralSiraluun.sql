-- @unit: quests/MaraeHuntSouthSiraluunLesserLittoralSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunLesserLittoralSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunLesserLittoralSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunLesserLittoralSiraluun', 0, 50, 'Hunting Drumtaken Lesser Littoral Siraluun for Hea Raukawe the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
