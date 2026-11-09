-- @unit: quests/MaraeHuntSouthGromnieWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGromnieWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGromnieWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGromnieWait', 72000, -1, 'Claimed today''s Gromnie hunt from Hea Teruana the Gromnie Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
