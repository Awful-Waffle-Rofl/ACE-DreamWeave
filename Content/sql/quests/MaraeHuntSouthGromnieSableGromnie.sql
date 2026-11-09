-- @unit: quests/MaraeHuntSouthGromnieSableGromnie
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGromnieSableGromnie
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGromnieSableGromnie';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGromnieSableGromnie', 0, 50, 'Hunting Drumtaken Sable Gromnie for Hea Teruana the Gromnie Hunter on Marae Lassel.', '2026-11-09 00:00:00');
