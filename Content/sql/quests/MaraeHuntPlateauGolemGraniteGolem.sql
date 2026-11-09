-- @unit: quests/MaraeHuntPlateauGolemGraniteGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGolemGraniteGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGolemGraniteGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGolemGraniteGolem', 0, 50, 'Hunting Drumtaken Granite Golem for Hea Koruani the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
