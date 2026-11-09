-- @unit: quests/MaraeHuntNorthGolemIronstoneGolem
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGolemIronstoneGolem
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGolemIronstoneGolem';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGolemIronstoneGolem', 0, 50, 'Hunting Drumtaken Ironstone Golem for Hea Hineroa the Golem Hunter on Marae Lassel.', '2026-11-09 00:00:00');
