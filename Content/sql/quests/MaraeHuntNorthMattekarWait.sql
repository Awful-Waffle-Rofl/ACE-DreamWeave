-- @unit: quests/MaraeHuntNorthMattekarWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthMattekarWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthMattekarWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthMattekarWait', 72000, -1, 'Claimed today''s Mattekar hunt from Hea Ngarekau the Mattekar Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
