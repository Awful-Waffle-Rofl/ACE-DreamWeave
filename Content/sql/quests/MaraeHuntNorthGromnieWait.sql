-- @unit: quests/MaraeHuntNorthGromnieWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthGromnieWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthGromnieWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthGromnieWait', 72000, -1, 'Claimed today''s Gromnie hunt from Hea Pukerua the Gromnie Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
