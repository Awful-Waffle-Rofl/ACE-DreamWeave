-- @unit: quests/MaraeHuntPlateauZefirEnkuZefir
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauZefirEnkuZefir
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauZefirEnkuZefir';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauZefirEnkuZefir', 0, 50, 'Hunting Drumtaken Enku Zefir for Hea Rukenua the Zefir Hunter on Marae Lassel.', '2026-11-09 00:00:00');
