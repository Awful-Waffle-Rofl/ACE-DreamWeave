-- @unit: quests/MaraeHuntSouthOlthoiOlthoiServant
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthOlthoiOlthoiServant
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthOlthoiOlthoiServant';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthOlthoiOlthoiServant', 0, 50, 'Hunting Olthoi Servant for Hea Mereana the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
