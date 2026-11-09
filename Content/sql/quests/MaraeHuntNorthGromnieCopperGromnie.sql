-- @unit: quests/MaraeHuntNorthGromnieCopperGromnie
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGromnieCopperGromnie
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGromnieCopperGromnie';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGromnieCopperGromnie', 0, 50, 'Hunting Drumtaken Copper Gromnie for Hea Pukerua the Gromnie Hunter on Marae Lassel.', '2026-11-09 00:00:00');
