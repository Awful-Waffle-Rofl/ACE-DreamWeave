-- @unit: quests/MaraeHuntSouthCarenziCarenziSentry
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthCarenziCarenziSentry
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthCarenziCarenziSentry';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthCarenziCarenziSentry', 0, 50, 'Hunting Drumtaken Carenzi Sentry for Hea Tarekeh the Carenzi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
