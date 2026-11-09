-- @unit: quests/MaraeHuntSouthOlthoiOlthoiDrone
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthOlthoiOlthoiDrone
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthOlthoiOlthoiDrone';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthOlthoiOlthoiDrone', 0, 50, 'Hunting Olthoi Drone for Hea Mereana the Olthoi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
