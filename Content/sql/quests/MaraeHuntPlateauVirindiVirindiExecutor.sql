-- @unit: quests/MaraeHuntPlateauVirindiVirindiExecutor
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauVirindiVirindiExecutor
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauVirindiVirindiExecutor';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauVirindiVirindiExecutor', 0, 50, 'Hunting Virindi Executor for Hea Ruatane the Virindi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
