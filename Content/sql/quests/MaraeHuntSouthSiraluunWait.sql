-- @unit: quests/MaraeHuntSouthSiraluunWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiraluunWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiraluunWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiraluunWait', 72000, -1, 'Claimed today''s Siraluun hunt from Hea Raukawe the Siraluun Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
