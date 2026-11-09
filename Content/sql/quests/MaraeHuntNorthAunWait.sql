-- @unit: quests/MaraeHuntNorthAunWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthAunWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthAunWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthAunWait', 72000, -1, 'Claimed today''s Aun hunt from Hea Tamaneru the Aun Hunter (North camp, Marae Lassel).', '2026-11-09 00:00:00');
