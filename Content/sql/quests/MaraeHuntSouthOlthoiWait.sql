-- @unit: quests/MaraeHuntSouthOlthoiWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthOlthoiWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthOlthoiWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthOlthoiWait', 72000, -1, 'Claimed today''s Olthoi hunt from Hea Mereana the Olthoi Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
