-- @unit: quests/MaraeHuntPlateauVirindiVirindiKeeper
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauVirindiVirindiKeeper
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauVirindiVirindiKeeper';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauVirindiVirindiKeeper', 0, 50, 'Hunting Virindi Keeper for Hea Ruatane the Virindi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
