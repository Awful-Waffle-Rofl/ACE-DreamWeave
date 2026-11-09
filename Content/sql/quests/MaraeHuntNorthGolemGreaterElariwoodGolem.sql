-- @unit: quests/MaraeHuntNorthGolemGreaterElariwoodGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGolemGreaterElariwoodGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGolemGreaterElariwoodGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGolemGreaterElariwoodGolem', 0, 50, 'Hunting Drumtaken Greater Elariwood Golem for Hea Hineroa the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
