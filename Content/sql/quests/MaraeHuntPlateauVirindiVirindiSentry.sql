-- @unit: quests/MaraeHuntPlateauVirindiVirindiSentry
-- @requires-restart: yes
-- @invalidate: quest:MaraeHuntPlateauVirindiVirindiSentry
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

DELETE FROM `quest` WHERE `name` = 'MaraeHuntPlateauVirindiVirindiSentry';

INSERT INTO `quest` (`name`, `min_Delta`, `max_Solves`, `message`, `last_Modified`)
VALUES ('MaraeHuntPlateauVirindiVirindiSentry', 0, 50, 'Hunting Virindi Sentry for Hea Ruatane the Virindi Hunter on Marae Lassel.', '2026-11-09 00:00:00');
