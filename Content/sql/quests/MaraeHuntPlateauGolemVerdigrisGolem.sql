-- @unit: quests/MaraeHuntPlateauGolemVerdigrisGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauGolemVerdigrisGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauGolemVerdigrisGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauGolemVerdigrisGolem', 0, 50, 'Hunting Drumtaken Verdigris Golem for Hea Koruani the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
