-- @unit: quests/MaraeHuntNorthUrsuinGreaterFerociousUrsuin
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthUrsuinGreaterFerociousUrsuin
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthUrsuinGreaterFerociousUrsuin';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthUrsuinGreaterFerociousUrsuin', 0, 50, 'Hunting Drumtaken Greater Ferocious Ursuin for Hea Narekua the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
