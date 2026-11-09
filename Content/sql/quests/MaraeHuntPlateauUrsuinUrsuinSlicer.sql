-- @unit: quests/MaraeHuntPlateauUrsuinUrsuinSlicer
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauUrsuinUrsuinSlicer
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauUrsuinUrsuinSlicer';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauUrsuinUrsuinSlicer', 0, 50, 'Hunting Drumtaken Ursuin Slicer for Hea Tamarau the Ursuin Hunter on Marae Lassel.', '2026-11-09 00:00:00');
