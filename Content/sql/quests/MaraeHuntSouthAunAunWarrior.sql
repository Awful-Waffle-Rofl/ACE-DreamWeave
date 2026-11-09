-- @unit: quests/MaraeHuntSouthAunAunWarrior
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthAunAunWarrior
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthAunAunWarrior';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthAunAunWarrior', 0, 50, 'Hunting Aun Warrior for Hea Wairena the Aun Hunter on Marae Lassel.', '2026-11-09 00:00:00');
