-- @unit: quests/MaraeHuntNorthUrsuinFerociousUrsuin
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthUrsuinFerociousUrsuin
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthUrsuinFerociousUrsuin';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthUrsuinFerociousUrsuin', 0, 50, 'Hunting Drumtaken Ferocious Ursuin for Hea Narekua the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
