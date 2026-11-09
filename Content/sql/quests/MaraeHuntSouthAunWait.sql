-- @unit: quests/MaraeHuntSouthAunWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunWait', 72000, -1, 'Claimed today''s Aun hunt from Hea Wairena the Aun Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
