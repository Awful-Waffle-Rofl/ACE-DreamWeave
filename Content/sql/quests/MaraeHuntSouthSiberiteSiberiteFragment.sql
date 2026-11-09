-- @unit: quests/MaraeHuntSouthSiberiteSiberiteFragment
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthSiberiteSiberiteFragment
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthSiberiteSiberiteFragment';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthSiberiteSiberiteFragment', 0, 50, 'Hunting Siberite Fragment for Hea Tuhiraki the Siberite Hunter on Marae Lassel.', '2026-11-09 00:00:00');
