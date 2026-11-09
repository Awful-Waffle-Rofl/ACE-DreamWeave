-- @unit: patches/marae_hunt_killquests
-- @requires-restart: no
-- @invalidate: weenie:1002840, weenie:1002841, weenie:1003673, weenie:1003650, weenie:1003651, weenie:1002848, weenie:1003653, weenie:1002842, weenie:1002843, weenie:1002844, weenie:1002846, weenie:1003652, weenie:1002849, weenie:1002850, weenie:1002851, weenie:1003654, weenie:1002847, weenie:1002852, weenie:1002845, weenie:1002892, weenie:1002893, weenie:1002894, weenie:1002895, weenie:1002896, weenie:1002897, weenie:1002898, weenie:1002856, weenie:1002857, weenie:1002858, weenie:1002859, weenie:1003656, weenie:1003657, weenie:1002862, weenie:1002863, weenie:1002866, weenie:1003658, weenie:1002867, weenie:1002868, weenie:1002870, weenie:1003660, weenie:1002869, weenie:1003659, weenie:1002861, weenie:1002864, weenie:1003655, weenie:1002865, weenie:1002872, weenie:1002874, weenie:1002873, weenie:1002860, weenie:1002899, weenie:1002900, weenie:1002901, weenie:1002876, weenie:1002877, weenie:1002878, weenie:1002879, weenie:1002881, weenie:1002884, weenie:1002885, weenie:1002886, weenie:1002887, weenie:1002880, weenie:1002883, weenie:1002889, weenie:1002890, weenie:1003663, weenie:1003668, weenie:1003661, weenie:1003662, weenie:1002882, weenie:1002888, weenie:1003664, weenie:1003669
-- @depends-on: weenies/1002840, weenies/1002841, weenies/1003673, weenies/1003650, weenies/1003651, weenies/1002848, weenies/1003653, weenies/1002842, weenies/1002843, weenies/1002844, weenies/1002846, weenies/1003652, weenies/1002849, weenies/1002850, weenies/1002851, weenies/1003654, weenies/1002847, weenies/1002852, weenies/1002845, weenies/1002892, weenies/1002893, weenies/1002894, weenies/1002895, weenies/1002896, weenies/1002897, weenies/1002898, weenies/1002856, weenies/1002857, weenies/1002858, weenies/1002859, weenies/1003656, weenies/1003657, weenies/1002862, weenies/1002863, weenies/1002866, weenies/1003658, weenies/1002867, weenies/1002868, weenies/1002870, weenies/1003660, weenies/1002869, weenies/1003659, weenies/1002861, weenies/1002864, weenies/1003655, weenies/1002865, weenies/1002872, weenies/1002874, weenies/1002873, weenies/1002860, weenies/1002899, weenies/1002900, weenies/1002901, weenies/1002876, weenies/1002877, weenies/1002878, weenies/1002879, weenies/1002881, weenies/1002884, weenies/1002885, weenies/1002886, weenies/1002887, weenies/1002880, weenies/1002883, weenies/1002889, weenies/1002890, weenies/1003663, weenies/1003668, weenies/1003661, weenies/1003662, weenies/1002882, weenies/1002888, weenies/1003664, weenies/1003669
-- @min-server: none
-- @owner: marae-hunt
-- @owns: none

/* Marae Lassel hunter camps - kill-task tags on the 74 target species.
 *
 * Adds the per-species counter (MaraeHunt<Zone><Category><Species>) as KillQuest2 (PropertyString 49) on every
 * target, or KillQuest3 (50) where 49 is already taken (Aun Runner and Aun Hunter carry AunTumerokBountyCount
 * there). KillQuest (45) is left alone: it carries the ml-treasure-hunt <Family>BountyCount on all 74, and it is
 * also the only slot KillQuestCreatesRow (PropertyBool 9065) bootstraps - none of these creatures sets that flag,
 * and using 49/50 keeps the hunts strictly gated on the counter row a hunter NPC creates.
 *
 * A NEW FILE, NOT AN EDIT: the target weenies belong to marae-lassel-bestiary and their units open with
 * DELETE FROM weenie. Re-applying any of them drops its tag here, so this unit @depends-on every one of them and
 * must be re-applied after any of those weenie units is.
 *
 * Idempotent: the DELETE removes only this feature's own tags, then every tag is re-inserted. A plain INSERT is
 * deliberate - if another feature ever claims slot 49/50 on one of these weenies, the unique key
 * (object_Id, type) fails this unit loudly instead of silently leaving a hunt uncreditable. */

DELETE FROM `weenie_properties_string` WHERE `type` IN (49, 50) AND `value` LIKE 'MaraeHunt%';

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (1002840, 49, 'MaraeHuntSouthSiraluunLittoralSiraluun') /* KillQuest2 - Drumtaken Littoral Siraluun (South Siraluun) */
     , (1002841, 49, 'MaraeHuntSouthSiraluunMarshSiraluun') /* KillQuest2 - Drumtaken Marsh Siraluun (South Siraluun) */
     , (1003673, 49, 'MaraeHuntSouthSiraluunTidalSiraluun') /* KillQuest2 - Drumtaken Tidal Siraluun (South Siraluun) */
     , (1003650, 49, 'MaraeHuntSouthSiraluunLesserLittoralSiraluun') /* KillQuest2 - Drumtaken Lesser Littoral Siraluun (South Siraluun) */
     , (1003651, 49, 'MaraeHuntSouthSiraluunLesserMarshSiraluun') /* KillQuest2 - Drumtaken Lesser Marsh Siraluun (South Siraluun) */
     , (1002848, 49, 'MaraeHuntSouthSiraluunStrandSiraluun') /* KillQuest2 - Drumtaken Strand Siraluun (South Siraluun) */
     , (1003653, 49, 'MaraeHuntSouthSiraluunLesserStrandSiraluun') /* KillQuest2 - Drumtaken Lesser Strand Siraluun (South Siraluun) */
     , (1002842, 49, 'MaraeHuntSouthCarenziCarenziTrampler') /* KillQuest2 - Drumtaken Carenzi Trampler (South Carenzi) */
     , (1002843, 49, 'MaraeHuntSouthCarenziCarenziSentry') /* KillQuest2 - Drumtaken Carenzi Sentry (South Carenzi) */
     , (1002844, 49, 'MaraeHuntSouthCarenziCarenziStalker') /* KillQuest2 - Drumtaken Carenzi Stalker (South Carenzi) */
     , (1002846, 49, 'MaraeHuntSouthGromnieSableGromnie') /* KillQuest2 - Drumtaken Sable Gromnie (South Gromnie) */
     , (1003652, 49, 'MaraeHuntSouthGromnieLesserSableGromnie') /* KillQuest2 - Drumtaken Lesser Sable Gromnie (South Gromnie) */
     , (1002849, 49, 'MaraeHuntSouthGolemSandGolem') /* KillQuest2 - Drumtaken Sand Golem (South Golem) */
     , (1002850, 49, 'MaraeHuntSouthGolemCoralGolem') /* KillQuest2 - Drumtaken Coral Golem (South Golem) */
     , (1002851, 49, 'MaraeHuntSouthUrsuinMaraeUrsuin') /* KillQuest2 - Drumtaken Marae Ursuin (South Ursuin) */
     , (1003654, 49, 'MaraeHuntSouthZefirKiritZefir') /* KillQuest2 - Drumtaken Kirit Zefir (South Zefir) */
     , (1002847, 49, 'MaraeHuntSouthOlthoiOlthoiDrone') /* KillQuest2 - Olthoi Drone (South Olthoi) */
     , (1002852, 49, 'MaraeHuntSouthOlthoiOlthoiServant') /* KillQuest2 - Olthoi Servant (South Olthoi) */
     , (1002845, 49, 'MaraeHuntSouthSiberiteSiberiteFragment') /* KillQuest2 - Siberite Fragment (South Siberite) */
     , (1002892, 50, 'MaraeHuntSouthAunAunRunner') /* KillQuest3 - Aun Runner (South Aun) */
     , (1002893, 50, 'MaraeHuntSouthAunAunHunter') /* KillQuest3 - Aun Hunter (South Aun) */
     , (1002894, 49, 'MaraeHuntSouthAunAunNualuan') /* KillQuest2 - Aun Nualuan (South Aun) */
     , (1002895, 49, 'MaraeHuntSouthAunAunItealuan') /* KillQuest2 - Aun Itealuan (South Aun) */
     , (1002896, 49, 'MaraeHuntSouthAunAunWarrior') /* KillQuest2 - Aun Warrior (South Aun) */
     , (1002897, 49, 'MaraeHuntSouthAunAunWindreave') /* KillQuest2 - Aun Windreave (South Aun) */
     , (1002898, 49, 'MaraeHuntSouthAunAunShaman') /* KillQuest2 - Aun Shaman (South Aun) */
     , (1002856, 49, 'MaraeHuntNorthSiraluunKithlessSiraluun') /* KillQuest2 - Drumtaken Kithless Siraluun (North Siraluun) */
     , (1002857, 49, 'MaraeHuntNorthSiraluunTimberSiraluun') /* KillQuest2 - Drumtaken Timber Siraluun (North Siraluun) */
     , (1002858, 49, 'MaraeHuntNorthCarenziRabidCarenzi') /* KillQuest2 - Drumtaken Rabid Carenzi (North Carenzi) */
     , (1002859, 49, 'MaraeHuntNorthCarenziFeralCarenzi') /* KillQuest2 - Drumtaken Feral Carenzi (North Carenzi) */
     , (1003656, 49, 'MaraeHuntNorthCarenziGreaterRabidCarenzi') /* KillQuest2 - Drumtaken Greater Rabid Carenzi (North Carenzi) */
     , (1003657, 49, 'MaraeHuntNorthCarenziGreaterFeralCarenzi') /* KillQuest2 - Drumtaken Greater Feral Carenzi (North Carenzi) */
     , (1002862, 49, 'MaraeHuntNorthGromnieEbonGromnie') /* KillQuest2 - Drumtaken Ebon Gromnie (North Gromnie) */
     , (1002863, 49, 'MaraeHuntNorthGromnieCopperGromnie') /* KillQuest2 - Drumtaken Copper Gromnie (North Gromnie) */
     , (1002866, 49, 'MaraeHuntNorthGolemElariwoodGolem') /* KillQuest2 - Drumtaken Elariwood Golem (North Golem) */
     , (1003658, 49, 'MaraeHuntNorthGolemGreaterElariwoodGolem') /* KillQuest2 - Drumtaken Greater Elariwood Golem (North Golem) */
     , (1002867, 49, 'MaraeHuntNorthGolemBasaltGolem') /* KillQuest2 - Drumtaken Basalt Golem (North Golem) */
     , (1002868, 49, 'MaraeHuntNorthGolemIronstoneGolem') /* KillQuest2 - Drumtaken Ironstone Golem (North Golem) */
     , (1002870, 49, 'MaraeHuntNorthUrsuinWoodlandUrsuin') /* KillQuest2 - Drumtaken Woodland Ursuin (North Ursuin) */
     , (1003660, 49, 'MaraeHuntNorthUrsuinGreaterWoodlandUrsuin') /* KillQuest2 - Drumtaken Greater Woodland Ursuin (North Ursuin) */
     , (1002869, 49, 'MaraeHuntNorthUrsuinFerociousUrsuin') /* KillQuest2 - Drumtaken Ferocious Ursuin (North Ursuin) */
     , (1003659, 49, 'MaraeHuntNorthUrsuinGreaterFerociousUrsuin') /* KillQuest2 - Drumtaken Greater Ferocious Ursuin (North Ursuin) */
     , (1002861, 49, 'MaraeHuntNorthZefirAzaelZefir') /* KillQuest2 - Drumtaken Azael Zefir (North Zefir) */
     , (1002864, 49, 'MaraeHuntNorthMattekarCanescentMattekar') /* KillQuest2 - Drumtaken Canescent Mattekar (North Mattekar) */
     , (1003655, 49, 'MaraeHuntNorthMattekarGreaterCanescentMattekar') /* KillQuest2 - Drumtaken Greater Canescent Mattekar (North Mattekar) */
     , (1002865, 49, 'MaraeHuntNorthOlthoiOlthoiPiercer') /* KillQuest2 - Olthoi Piercer (North Olthoi) */
     , (1002872, 49, 'MaraeHuntNorthOlthoiOlthoiSoldier') /* KillQuest2 - Olthoi Soldier (North Olthoi) */
     , (1002874, 49, 'MaraeHuntNorthOlthoiOlthoiLegionary') /* KillQuest2 - Olthoi Legionary (North Olthoi) */
     , (1002873, 49, 'MaraeHuntNorthOlthoiOlthoiEvisceratorGrub') /* KillQuest2 - Olthoi Eviscerator Grub (North Olthoi) */
     , (1002860, 49, 'MaraeHuntNorthSiberiteSiberiteShard') /* KillQuest2 - Siberite Shard (North Siberite) */
     , (1002899, 49, 'MaraeHuntNorthAunAunElderShaman') /* KillQuest2 - Aun Elder Shaman (North Aun) */
     , (1002900, 49, 'MaraeHuntNorthAunAunDrumspeaker') /* KillQuest2 - Aun Drumspeaker (North Aun) */
     , (1002901, 49, 'MaraeHuntNorthAunAunChampion') /* KillQuest2 - Aun Champion (North Aun) */
     , (1002876, 49, 'MaraeHuntPlateauSiraluunUntamedSiraluun') /* KillQuest2 - Drumtaken Untamed Siraluun (Plateau Siraluun) */
     , (1002877, 49, 'MaraeHuntPlateauSiraluunBadlandsSiraluun') /* KillQuest2 - Drumtaken Badlands Siraluun (Plateau Siraluun) */
     , (1002878, 49, 'MaraeHuntPlateauCarenziSavageCarenzi') /* KillQuest2 - Drumtaken Savage Carenzi (Plateau Carenzi) */
     , (1002879, 49, 'MaraeHuntPlateauCarenziCarnivorousCarenzi') /* KillQuest2 - Drumtaken Carnivorous Carenzi (Plateau Carenzi) */
     , (1002881, 49, 'MaraeHuntPlateauGromnieBrassGromnie') /* KillQuest2 - Drumtaken Brass Gromnie (Plateau Gromnie) */
     , (1002884, 49, 'MaraeHuntPlateauGolemVerdigrisGolem') /* KillQuest2 - Drumtaken Verdigris Golem (Plateau Golem) */
     , (1002885, 49, 'MaraeHuntPlateauGolemGraniteGolem') /* KillQuest2 - Drumtaken Granite Golem (Plateau Golem) */
     , (1002886, 49, 'MaraeHuntPlateauUrsuinUrsuinSlicer') /* KillQuest2 - Drumtaken Ursuin Slicer (Plateau Ursuin) */
     , (1002887, 49, 'MaraeHuntPlateauUrsuinRagingUrsuin') /* KillQuest2 - Drumtaken Raging Ursuin (Plateau Ursuin) */
     , (1002880, 49, 'MaraeHuntPlateauZefirEnkuZefir') /* KillQuest2 - Drumtaken Enku Zefir (Plateau Zefir) */
     , (1002883, 49, 'MaraeHuntPlateauOlthoiOlthoiLancer') /* KillQuest2 - Olthoi Lancer (Plateau Olthoi) */
     , (1002889, 49, 'MaraeHuntPlateauOlthoiOlthoiWarrior') /* KillQuest2 - Olthoi Warrior (Plateau Olthoi) */
     , (1002890, 49, 'MaraeHuntPlateauOlthoiOlthoiHiveMutilator') /* KillQuest2 - Olthoi Hive Mutilator (Plateau Olthoi) */
     , (1003663, 49, 'MaraeHuntPlateauOlthoiOlthoiHiveWarrior') /* KillQuest2 - Olthoi Hive Warrior (Plateau Olthoi) */
     , (1003668, 49, 'MaraeHuntPlateauOlthoiOlthoiHiveGuardian') /* KillQuest2 - Olthoi Hive Guardian (Plateau Olthoi) */
     , (1003661, 49, 'MaraeHuntPlateauOlthoiOlthoiHiveCrawler') /* KillQuest2 - Olthoi Hive Crawler (Plateau Olthoi) */
     , (1003662, 49, 'MaraeHuntPlateauOlthoiHiveMaggot') /* KillQuest2 - Hive Maggot (Plateau Olthoi) */
     , (1002882, 49, 'MaraeHuntPlateauVirindiVirindiObserver') /* KillQuest2 - Virindi Observer (Plateau Virindi) */
     , (1002888, 49, 'MaraeHuntPlateauVirindiVirindiExecutor') /* KillQuest2 - Virindi Executor (Plateau Virindi) */
     , (1003664, 49, 'MaraeHuntPlateauVirindiVirindiSentry') /* KillQuest2 - Virindi Sentry (Plateau Virindi) */
     , (1003669, 49, 'MaraeHuntPlateauVirindiVirindiKeeper') /* KillQuest2 - Virindi Keeper (Plateau Virindi) */;
