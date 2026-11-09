-- @unit: quests/MaraeHuntSouthUrsuinMaraeUrsuin
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthUrsuinMaraeUrsuin
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthUrsuinMaraeUrsuin';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthUrsuinMaraeUrsuin', 0, 50, 'Hunting Drumtaken Marae Ursuin for Hea Talurea the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
