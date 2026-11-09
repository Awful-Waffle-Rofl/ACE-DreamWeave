-- @unit: quests/MaraeHuntSouthGolemWait
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntSouthGolemWait
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntSouthGolemWait';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntSouthGolemWait', 72000, -1, 'Claimed today''s Golem hunt from Hea Korenuad the Golem Hunter (South camp, Marae Lassel).', '2026-11-09 00:00:00');
