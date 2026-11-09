-- @unit: quests/MaraeHuntSouthAunAunShaman
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunShaman
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunShaman';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunShaman', 0, 50, 'Hunting Aun Shaman for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
