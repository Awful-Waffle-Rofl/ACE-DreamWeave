-- @unit: quests/MaraeHuntNorthUrsuinWoodlandUrsuin
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthUrsuinWoodlandUrsuin
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthUrsuinWoodlandUrsuin';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthUrsuinWoodlandUrsuin', 0, 50, 'Hunting Drumtaken Woodland Ursuin for Hea Narekua the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
