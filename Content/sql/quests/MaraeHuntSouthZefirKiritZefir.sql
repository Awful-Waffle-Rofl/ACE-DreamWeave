-- @unit: quests/MaraeHuntSouthZefirKiritZefir
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthZefirKiritZefir
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthZefirKiritZefir';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthZefirKiritZefir', 0, 50, 'Hunting Drumtaken Kirit Zefir for Hea Aroheka the Zefir Hunter on Marae Lassel.', '2026-11-09 00:00:00');
