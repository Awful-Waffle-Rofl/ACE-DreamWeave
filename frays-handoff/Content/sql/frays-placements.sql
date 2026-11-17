-- @owner: frays
-- @requires-restart: no (placed when landblock 0x0007 next loads in realm 1)
--
-- The one fixed placement the Frays need: the Ember Isles portal (2000075) in the Drift Network hall, realm 1,
-- left of Drift Marae Lassel. Everything else in the village is placed by the server from frays_village_layout.

DELETE FROM `landblock_instance_realm` WHERE `realm_id` = 1 AND `guid` = 1879080960;
INSERT INTO `landblock_instance_realm` (`realm_id`, `guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`, `last_Modified`)
VALUES (1, 1879080960, 2000075, 0x0007012D, 60, -4.08, 0.005, 0, 0, 0, -1, 0, NOW()); /* 0x0007012D [60 -4.08 0.005], facing south */
