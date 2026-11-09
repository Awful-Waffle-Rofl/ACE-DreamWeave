-- @unit: quests/MaraeHuntSouthAunAunHunter
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunHunter
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunHunter';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunHunter', 0, 50, 'Hunting Aun Hunter for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
