-- @unit: quests/MaraeHuntNorthGolemElariwoodGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGolemElariwoodGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGolemElariwoodGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGolemElariwoodGolem', 0, 50, 'Hunting Drumtaken Elariwood Golem for Hea Hineroa the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
