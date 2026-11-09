-- @unit: quests/MaraeHuntNorthGromnieEbonGromnie
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGromnieEbonGromnie
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGromnieEbonGromnie';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGromnieEbonGromnie', 0, 50, 'Hunting Drumtaken Ebon Gromnie for Hea Pukerua the Gromnie Hunter on Marae Lassel.', '2026-11-09 00:00:00');
