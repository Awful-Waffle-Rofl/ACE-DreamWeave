-- @unit: quests/MaraeHuntPlateauCarenziCarnivorousCarenzi
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauCarenziCarnivorousCarenzi
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauCarenziCarnivorousCarenzi';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauCarenziCarnivorousCarenzi', 0, 50, 'Hunting Drumtaken Carnivorous Carenzi for Hea Kurelan the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
