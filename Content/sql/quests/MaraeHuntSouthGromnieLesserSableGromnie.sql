-- @unit: quests/MaraeHuntSouthGromnieLesserSableGromnie
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGromnieLesserSableGromnie
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGromnieLesserSableGromnie';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGromnieLesserSableGromnie', 0, 50, 'Hunting Drumtaken Lesser Sable Gromnie for Hea Teruana the Gromnie Hunter on Marae Lassel.', '2026-11-09 00:00:00');
