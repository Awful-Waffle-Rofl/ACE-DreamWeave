-- @unit: quests/MaraeHuntPlateauUrsuinRagingUrsuin
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauUrsuinRagingUrsuin
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauUrsuinRagingUrsuin';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauUrsuinRagingUrsuin', 0, 50, 'Hunting Drumtaken Raging Ursuin for Hea Tamarau the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
