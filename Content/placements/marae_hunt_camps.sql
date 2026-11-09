-- @unit: placements/marae_hunt_camps
-- @requires-restart: no
-- @invalidate: realms, landblock:21B0, landblock:1BC2, landblock:21B7
-- @depends-on: realms/realm1_registry, weenies/1007900, weenies/1007901, weenies/1007902, weenies/1007903, weenies/1007904, weenies/1007905, weenies/1007906, weenies/1007907, weenies/1007908, weenies/1007909, weenies/1007910, weenies/1007911, weenies/1007912, weenies/1007913, weenies/1007914, weenies/1007915, weenies/1007916, weenies/1007917, weenies/1007918, weenies/1007919, weenies/1007920, weenies/1007921, weenies/1007922, weenies/1007923, weenies/1007924, weenies/1007925, weenies/1007926
-- @min-server: none
-- @owner: marae-hunt
-- @owns: guid:1:0x721B0FC0-0x721B0FCF, guid:1:0x71BC2FC0-0x71BC2FCF, guid:1:0x721B7FC0-0x721B7FCF

/* Marae Lassel hunter camps - three campfires with two Tumerok log chairs each and a ring of Hea hunters.
 *
 * REALM 1, REALM TABLE: all three landblocks already carry realm-1 rows (bestiary placements and the realm-1
 * lifestones), so realm content replaces base content there and these rows belong in landblock_instance_realm.
 * The campfire positions are the owner's /locs verbatim (2026-11-09). Hunters stand on a 5.0 m ring around the
 * fire, evenly spaced, each facing the fire, 0.01 m lower than the fire's Z (owner request: very slightly lower).
 * The two log chairs sit 2.6 m out on either side of the fire's own heading, between two hunters, facing in.
 * Ring spacing is 3.1-3.9 m between hunters (well clear of the repo's 0.68 m body-radius standard). Every cell id is derived from its own
 * origin: cell = floor(x/24)*8 + floor(y/24) + 1. Terrain was not measured (no `cells` access from the authoring
 * side) - the owner reports all three spots as near-flat; re-check in game and nudge any row that floats/sinks.
 *
 * GUIDS: slot 0xFC0-0xFCF on each block (registry fragments guid-0x721B0FC0-r1, guid-0x71BC2FC0-r1,
 * guid-0x721B7FC0-r1). On 0x21B0 that is the untouched gap between the lifestone claim (0xFB0-0xFBF) and the
 * retired sky-rift band (0xFEB-0xFEF); on 0x1BC2 and 0x21B7 the only other realm-1 claims are the bestiary
 * band (0xFA6-0xFAF) and the lifestones (0xFB0). Deleted by explicit guid list, never by weenie or range. */

DELETE FROM `landblock_instance_realm` WHERE `realm_id` = 1 AND `guid` IN (0x721B0FC0,0x721B0FC1,0x721B0FC2,0x721B0FC3,0x721B0FC4,0x721B0FC5,0x721B0FC6,0x721B0FC7,0x721B0FC8,0x721B0FC9,0x721B0FCA,0x721B0FCB,0x71BC2FC0,0x71BC2FC1,0x71BC2FC2,0x71BC2FC3,0x71BC2FC4,0x71BC2FC5,0x71BC2FC6,0x71BC2FC7,0x71BC2FC8,0x71BC2FC9,0x71BC2FCA,0x71BC2FCB,0x71BC2FCC,0x721B7FC0,0x721B7FC1,0x721B7FC2,0x721B7FC3,0x721B7FC4,0x721B7FC5,0x721B7FC6,0x721B7FC7,0x721B7FC8,0x721B7FC9,0x721B7FCA);

INSERT INTO `landblock_instance_realm`
  (`realm_id`, `guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`)
VALUES
  /* landblock 0x21B0 - South camp */
  (1, 0x721B0FC0, 35464, 0x21B0000C, 47.067131, 90.570038, 0.005000, 0.997299, 0, 0, 0.073444, 0) /* Drudge Bonfire - South camp, owner /loc verbatim */
 , (1, 0x721B0FC1, 25766, 0x21B0000C, 44.780555, 89.332471, 0.005000, 0.859066, 0, 0, -0.511866, 0) /* Tumerok Log Chair */
 , (1, 0x721B0FC2, 25766, 0x21B00014, 49.353707, 91.807605, 0.005000, 0.511866, 0, 0, 0.859066, 0) /* Tumerok Log Chair */
 , (1, 0x721B0FC3, 1007900, 0x21B0000C, 46.334674, 95.516098, -0.005000, 0.073444, 0, 0, -0.997299, 0) /* Hea Raukawe the Siraluun Hunter */
 , (1, 0x721B0FC4, 1007901, 0x21B0000C, 43.326771, 93.888125, -0.005000, 0.410111, 0, 0, -0.912035, 0) /* Hea Tarekeh the Carenzi Hunter */
 , (1, 0x721B0FC5, 1007902, 0x21B0000C, 42.069023, 90.707583, -0.005000, 0.697313, 0, 0, -0.716767, 0) /* Hea Teruana the Gromnie Hunter */
 , (1, 0x721B0FC6, 1007903, 0x21B0000C, 43.149946, 87.462682, -0.005000, 0.900409, 0, 0, -0.435045, 0) /* Hea Korenuad the Golem Hunter */
 , (1, 0x721B0FC7, 1007904, 0x21B0000C, 46.063763, 85.671747, -0.005000, 0.994902, 0, 0, -0.100851, 0) /* Hea Talurea the Ursuin Hunter */
 , (1, 0x721B0FC8, 1007905, 0x21B00014, 49.447067, 86.172777, -0.005000, 0.969395, 0, 0, 0.245507, 0) /* Hea Aroheka the Zefir Hunter */
 , (1, 0x721B0FC9, 1007906, 0x21B00014, 51.716773, 88.731334, -0.005000, 0.826965, 0, 0, 0.562254, 0) /* Hea Mereana the Olthoi Hunter */
 , (1, 0x721B0FCA, 1007907, 0x21B00014, 51.810859, 92.150241, -0.005000, 0.584790, 0, 0, 0.811185, 0) /* Hea Tuhiraki the Siberite Hunter */
 , (1, 0x721B0FCB, 1007908, 0x21B00014, 49.685302, 94.829754, -0.005000, 0.272082, 0, 0, 0.962274, 0) /* Hea Wairena the Aun Hunter */
  /* landblock 0x1BC2 - North camp */
 , (1, 0x71BC2FC0, 35464, 0x1BC2002C, 139.746597, 72.261322, 72.005005, 0.509334, 0, 0, 0.860569, 0) /* Drudge Bonfire - North camp, owner /loc verbatim */
 , (1, 0x71BC2FC1, 25766, 0x1BC2002B, 141.640705, 70.480211, 72.005005, 0.917890, 0, 0, 0.396836, 0) /* Tumerok Log Chair */
 , (1, 0x71BC2FC2, 25766, 0x1BC2002C, 137.852489, 74.042433, 72.005005, 0.396836, 0, 0, -0.917890, 0) /* Tumerok Log Chair */
 , (1, 0x71BC2FC3, 1007909, 0x1BC2002B, 135.363427, 69.855533, 71.995005, 0.860569, 0, 0, -0.509334, 0) /* Hea Kahureka the Siraluun Hunter */
 , (1, 0x71BC2FC4, 1007910, 0x1BC2002B, 137.614625, 67.738635, 71.995005, 0.975843, 0, 0, -0.218475, 0) /* Hea Marenua the Carenzi Hunter */
 , (1, 0x71BC2FC5, 1007911, 0x1BC2002B, 140.680164, 67.349250, 71.995005, 0.995594, 0, 0, 0.093770, 0) /* Hea Pukerua the Gromnie Hunter */
 , (1, 0x71BC2FC6, 1007912, 0x1BC2002B, 143.389112, 68.836109, 71.995005, 0.917890, 0, 0, 0.396836, 0) /* Hea Hineroa the Golem Hunter */
 , (1, 0x71BC2FC7, 1007913, 0x1BC20033, 144.706743, 71.631284, 71.995005, 0.750336, 0, 0, 0.661057, 0) /* Hea Narekua the Ursuin Hunter */
 , (1, 0x71BC2FC8, 1007914, 0x1BC20034, 144.129767, 74.667111, 71.995005, 0.509334, 0, 0, 0.860569, 0) /* Hea Orewhiti the Zefir Hunter */
 , (1, 0x71BC2FC9, 1007915, 0x1BC2002C, 141.878569, 76.784009, 71.995005, 0.218475, 0, 0, 0.975843, 0) /* Hea Ngarekau the Mattekar Hunter */
 , (1, 0x71BC2FCA, 1007916, 0x1BC2002C, 138.813030, 77.173394, 71.995005, 0.093770, 0, 0, -0.995594, 0) /* Hea Hakerea the Olthoi Hunter */
 , (1, 0x71BC2FCB, 1007917, 0x1BC2002C, 136.104082, 75.686535, 71.995005, 0.396836, 0, 0, -0.917890, 0) /* Hea Zanuah the Siberite Hunter */
 , (1, 0x71BC2FCC, 1007918, 0x1BC2002C, 134.786451, 72.891360, 71.995005, 0.661057, 0, 0, -0.750336, 0) /* Hea Tamaneru the Aun Hunter */
  /* landblock 0x21B7 - Plateau camp */
 , (1, 0x721B7FC0, 35464, 0x21B70027, 106.989517, 165.740433, 92.005005, 0.968210, 0, 0, -0.250139, 0) /* Drudge Bonfire - Plateau camp, owner /loc verbatim */
 , (1, 0x721B7FC1, 25766, 0x21B70027, 104.406084, 166.033475, 92.005005, 0.666067, 0, 0, -0.745892, 0) /* Tumerok Log Chair */
 , (1, 0x721B7FC2, 25766, 0x21B70027, 109.572950, 165.447391, 92.005005, 0.745892, 0, 0, 0.666067, 0) /* Tumerok Log Chair */
 , (1, 0x721B7FC3, 1007919, 0x21B70028, 109.411388, 170.114738, 91.995005, 0.250139, 0, 0, 0.968210, 0) /* Hea Taraneki the Siraluun Hunter */
 , (1, 0x721B7FC4, 1007920, 0x21B70028, 105.608937, 170.546055, 91.995005, 0.139420, 0, 0, -0.990233, 0) /* Hea Kurelan the Carenzi Hunter */
 , (1, 0x721B7FC5, 1007921, 0x21B70028, 102.615212, 168.162304, 91.995005, 0.507753, 0, 0, -0.861503, 0) /* Hea Weneria the Gromnie Hunter */
 , (1, 0x721B7FC6, 1007922, 0x21B70027, 102.183895, 164.359853, 91.995005, 0.798785, 0, 0, -0.601616, 0) /* Hea Koruani the Golem Hunter */
 , (1, 0x721B7FC7, 1007923, 0x21B70027, 104.567646, 161.366128, 91.995005, 0.968210, 0, 0, -0.250139, 0) /* Hea Tamarau the Ursuin Hunter */
 , (1, 0x721B7FC8, 1007924, 0x21B70027, 108.370097, 160.934811, 91.995005, 0.990233, 0, 0, 0.139420, 0) /* Hea Rukenua the Zefir Hunter */
 , (1, 0x721B7FC9, 1007925, 0x21B70027, 111.363822, 163.318562, 91.995005, 0.861503, 0, 0, 0.507753, 0) /* Hea Varenkeh the Olthoi Hunter */
 , (1, 0x721B7FCA, 1007926, 0x21B70027, 111.795139, 167.121013, 91.995005, 0.601616, 0, 0, 0.798785, 0) /* Hea Ruatane the Virindi Hunter */;
