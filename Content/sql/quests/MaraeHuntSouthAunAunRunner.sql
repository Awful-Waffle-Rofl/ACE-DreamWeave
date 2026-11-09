-- @unit: quests/MaraeHuntSouthAunAunRunner
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunRunner
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunRunner';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunRunner', 0, 50, 'Hunting Aun Runner for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
