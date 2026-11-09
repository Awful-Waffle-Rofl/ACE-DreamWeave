-- @unit: quests/MaraeHuntNorthSiberiteSiberiteShard
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntNorthSiberiteSiberiteShard
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntNorthSiberiteSiberiteShard';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntNorthSiberiteSiberiteShard', 0, 50, 'Hunting Siberite Shard for Hea Zanuah the Siberite Hunter on Marae Lassel.', '2026-11-09 00:00:00');
