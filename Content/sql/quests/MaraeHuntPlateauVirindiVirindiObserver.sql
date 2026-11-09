-- @unit: quests/MaraeHuntPlateauVirindiVirindiObserver
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauVirindiVirindiObserver
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauVirindiVirindiObserver';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauVirindiVirindiObserver', 0, 50, 'Hunting Virindi Observer for Hea Ruatane the Virindi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
