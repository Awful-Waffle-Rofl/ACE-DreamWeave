-- @unit: quests/MaraeHuntNorthUrsuinWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthUrsuinWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthUrsuinWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthUrsuinWait', 72000, -1, 'Claimed today''s Ursuin hunt from Hea Narekua the Ursuin Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
