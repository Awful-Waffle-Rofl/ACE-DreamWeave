-- @unit: quests/MaraeHuntNorthMattekarGreaterCanescentMattekar
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthMattekarGreaterCanescentMattekar
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthMattekarGreaterCanescentMattekar';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthMattekarGreaterCanescentMattekar', 0, 50, 'Hunting Drumtaken Greater Canescent Mattekar for Hea Ngarekau the Mattekar Hunter on Marae Lassel.', '2026-11-09 00:00:00');
