-- @unit: quests/MaraeHuntSouthUrsuinWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthUrsuinWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthUrsuinWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthUrsuinWait', 72000, -1, 'Claimed today''s Ursuin hunt from Hea Talurea the Ursuin Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
