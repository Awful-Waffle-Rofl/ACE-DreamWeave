-- @unit: quests/MaraeHuntSouthCarenziCarenziTrampler
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthCarenziCarenziTrampler
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthCarenziCarenziTrampler';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthCarenziCarenziTrampler', 0, 50, 'Hunting Drumtaken Carenzi Trampler for Hea Tarekeh the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
