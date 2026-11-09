-- @unit: quests/MaraeHuntPlateauVirindiTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauVirindiTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauVirindiTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauVirindiTask', 0, -1, 'Active Virindi hunt from Hea Ruatane the Virindi Hunter (value = which species).', '2026-11-09 00:00:00');
