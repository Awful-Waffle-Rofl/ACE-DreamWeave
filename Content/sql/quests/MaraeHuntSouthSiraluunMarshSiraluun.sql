-- @unit: quests/MaraeHuntSouthSiraluunMarshSiraluun
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunMarshSiraluun
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunMarshSiraluun';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunMarshSiraluun', 0, 50, 'Hunting Drumtaken Marsh Siraluun for Hea Raukawe the Siraluun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
