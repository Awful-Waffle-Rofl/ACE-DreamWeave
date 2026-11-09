-- @unit: quests/MaraeHuntSouthAunAunItealuan
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunItealuan
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunItealuan';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunItealuan', 0, 50, 'Hunting Aun Itealuan for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
