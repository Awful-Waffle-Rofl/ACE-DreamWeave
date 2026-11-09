-- @unit: quests/MaraeHuntSouthGolemSandGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGolemSandGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGolemSandGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGolemSandGolem', 0, 50, 'Hunting Drumtaken Sand Golem for Hea Korenuad the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
