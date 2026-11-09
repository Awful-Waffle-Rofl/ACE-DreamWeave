-- @unit: quests/MaraeHuntNorthMattekarCanescentMattekar
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthMattekarCanescentMattekar
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthMattekarCanescentMattekar';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthMattekarCanescentMattekar', 0, 50, 'Hunting Drumtaken Canescent Mattekar for Hea Ngarekau the Mattekar Hunter on Marae Lassel.', '2026-11-09 00:00:00');
