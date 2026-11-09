-- @unit: quests/MaraeHuntSouthAunAunWindreave
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunWindreave
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunWindreave';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunWindreave', 0, 50, 'Hunting Aun Windreave for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
