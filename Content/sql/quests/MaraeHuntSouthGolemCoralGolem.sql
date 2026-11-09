-- @unit: quests/MaraeHuntSouthGolemCoralGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGolemCoralGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGolemCoralGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGolemCoralGolem', 0, 50, 'Hunting Drumtaken Coral Golem for Hea Korenuad the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
