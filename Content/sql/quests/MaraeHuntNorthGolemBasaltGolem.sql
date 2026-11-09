-- @unit: quests/MaraeHuntNorthGolemBasaltGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGolemBasaltGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGolemBasaltGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGolemBasaltGolem', 0, 50, 'Hunting Drumtaken Basalt Golem for Hea Hineroa the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
