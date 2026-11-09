-- @unit: quests/MaraeHuntSouthGolemTask
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGolemTask
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGolemTask';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGolemTask', 0, -1, 'Active Golem hunt from Hea Korenuad the Golem Hunter (value = which species).', '2026-11-09 00:00:00');
