-- @unit: quests/MaraeHuntSouthCarenziCarenziStalker
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthCarenziCarenziStalker
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthCarenziCarenziStalker';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthCarenziCarenziStalker', 0, 50, 'Hunting Drumtaken Carenzi Stalker for Hea Tarekeh the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
