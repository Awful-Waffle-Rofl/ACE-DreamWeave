-- @unit: quests/MaraeHuntNorthUrsuinGreaterWoodlandUrsuin
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthUrsuinGreaterWoodlandUrsuin
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthUrsuinGreaterWoodlandUrsuin';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthUrsuinGreaterWoodlandUrsuin', 0, 50, 'Hunting Drumtaken Greater Woodland Ursuin for Hea Narekua the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
