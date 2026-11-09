-- @unit: quests/MaraeHuntSouthAunAunNualuan
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunNualuan
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunNualuan';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunNualuan', 0, 50, 'Hunting Aun Nualuan for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
