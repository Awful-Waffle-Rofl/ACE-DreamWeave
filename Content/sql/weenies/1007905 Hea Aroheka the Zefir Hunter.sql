-- @unit: weenies/1007905
-- @requires-restart: no
-- @invalidate: weenie:1007905
-- @depends-on: none
-- @min-server: none
-- @owner: marae-hunt
-- @owns: wcid:1007900-1007949

/* Hea Aroheka the Zefir Hunter - South camp hunter (Zefir), marae-hunt.
 * Gives one random Zefir species of the South zone, 50 kills; minimum level 100;
 * reward 400,000,000 XP (AwardNoShareXP) + 100,000 Luminance; 20 h cooldown from the claim
 * (MaraeHuntSouthZefirWait). Skin cloned from 1004147 Hea Kaimana the Roadwarden (Setup, palette, clothing, stats; never its emotes,
 * generators, books, spells, positions or hand items). Wields Chimeric Eye of the Quiddity (1004651).
 * Gender (dialogue/design only - Hea Tumerok weenies carry no gender property): Female. Skins were drawn without regard to gender; swap the template if one reads wrong in game. */

DELETE FROM `weenie` WHERE `class_Id` = 1007905;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (1007905, 'ace1007905-heaarohekathezefirhunter', 10, '0001-01-01 00:00:00') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (1007905,   1,         16) /* ItemType - Creature */
     , (1007905,   2,         58) /* CreatureType - HeaTumerok */
     , (1007905,   3,         14) /* PaletteTemplate - Red (round 17: was DarkBlue/5 with same Setup+scale as five other bounty-hub NPCs in the same hall; verified valid for ClothingBase 0x1000061E via ACE.Content.Tools paletteinfo) */
     , (1007905,   6,         -1) /* ItemsCapacity */
     , (1007905,   7,         -1) /* ContainersCapacity */
     , (1007905,  16,         32) /* ItemUseable - Remote */
     , (1007905,  25,         80) /* Level */
     , (1007905,  27,          0) /* ArmorType - None */
     , (1007905,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (1007905,  95,          8) /* RadarBlipColor - Yellow */
     , (1007905, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (1007905, 134,         16) /* PlayerKillerStatus - RubberGlue */
     , (1007905, 146,      30000) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (1007905,   1, True ) /* Stuck */
     , (1007905,   6, True ) /* AiUsesMana */
     , (1007905,  11, True ) /* IgnoreCollisions */
     , (1007905,  12, True ) /* ReportCollisions */
     , (1007905,  13, False) /* Ethereal */
     , (1007905,  14, True ) /* GravityStatus */
     , (1007905,  19, False) /* Attackable */
     , (1007905,  41, True ) /* ReportCollisionsAsEnvironment */
     , (1007905,  42, True ) /* AllowEdgeSlide */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (1007905,   1,       5) /* HeartbeatInterval */
     , (1007905,   2,       0) /* HeartbeatTimestamp */
     , (1007905,   3,     0.8) /* HealthRate */
     , (1007905,   4,     0.5) /* StaminaRate */
     , (1007905,   5,       2) /* ManaRate */
     , (1007905,  12,   0.333) /* Shade */
     , (1007905,  13,       1) /* ArmorModVsSlash */
     , (1007905,  14,       1) /* ArmorModVsPierce */
     , (1007905,  15,       1) /* ArmorModVsBludgeon */
     , (1007905,  16,       1) /* ArmorModVsCold */
     , (1007905,  17,       1) /* ArmorModVsFire */
     , (1007905,  18,       1) /* ArmorModVsAcid */
     , (1007905,  19,       1) /* ArmorModVsElectric */
     , (1007905,  31,      17) /* VisualAwarenessRange */
     , (1007905,  34,       1) /* PowerupTime */
     , (1007905,  36,       1) /* ChargeSpeed */
     , (1007905,  39,    1.05) /* DefaultScale - round 17: was 1.2, identical to the rest of the bounty hub */
     , (1007905,  64,       1) /* ResistSlash */
     , (1007905,  65,    0.85) /* ResistPierce */
     , (1007905,  66,       1) /* ResistBludgeon */
     , (1007905,  67,    0.85) /* ResistFire */
     , (1007905,  68,    0.85) /* ResistCold */
     , (1007905,  69,    0.85) /* ResistAcid */
     , (1007905,  70,    0.85) /* ResistElectric */
     , (1007905,  71,       1) /* ResistHealthBoost */
     , (1007905,  72,       1) /* ResistStaminaDrain */
     , (1007905,  73,       1) /* ResistStaminaBoost */
     , (1007905,  74,       1) /* ResistManaDrain */
     , (1007905,  75,       1) /* ResistManaBoost */
     , (1007905,  80,       3) /* AiUseMagicDelay */
     , (1007905, 104,      10) /* ObviousRadarRange */
     , (1007905, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (1007905,   1, 0x02001404) /* Setup */
     , (1007905,   2, 0x0900000A) /* MotionTable */
     , (1007905,   3, 0x20000013) /* SoundTable */
     , (1007905,   4, 0x3000000C) /* CombatTable */
     , (1007905,   6, 0x04001E51) /* PaletteBase */
     , (1007905,   7, 0x1000061E) /* ClothingBase */
     , (1007905,   8, 0x0600103C) /* Icon */
     , (1007905,  22, 0x34000026) /* PhysicsEffectTable */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (1007905,   1, 220, 0, 0) /* Strength */
     , (1007905,   2, 180, 0, 0) /* Endurance */
     , (1007905,   3, 220, 0, 0) /* Quickness */
     , (1007905,   4, 220, 0, 0) /* Coordination */
     , (1007905,   5, 145, 0, 0) /* Focus */
     , (1007905,   6, 140, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (1007905,   1,   120, 0, 0, 210) /* MaxHealth */
     , (1007905,   3,   150, 0, 0, 330) /* MaxStamina */
     , (1007905,   5,     0, 0, 0, 140) /* MaxMana */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (1007905,   1, 'Hea Aroheka the Zefir Hunter') /* Name */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (1007905, 2, 1004651,  1, 0, 0, False) /* Create Chimeric Eye of the Quiddity (1004651) for Wield */;

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905,  7 /* Use */,        1, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  12 /* TurnToTarget */, 0, 1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  1,  36 /* InqIntStat */, 0, 1, NULL, 'MaraeHuntSouthZefirLevel', NULL, 100, NULL, NULL, NULL, NULL, NULL, 25, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 23 /* TestFailure */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirLevel', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  10 /* Tell */, 0, 1, NULL, 'The hunts of the lowlands are not for you yet. Return when you have reached level 100.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 22 /* TestSuccess */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirLevel', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  21 /* InqQuest */, 0, 1, NULL, 'MaraeHuntSouthZefirWait', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 12 /* QuestSuccess */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirWait', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  10 /* Tell */, 0, 1, NULL, 'You have completed today''s hunt. Return when 20 hours have passed since your last reward claim.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  1,  10 /* Tell */, 0.5, 1, NULL, 'You may take another hunt in %tqt.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 13 /* QuestFailure */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirWait', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  30 /* InqQuestSolves */, 0, 1, NULL, 'MaraeHuntSouthZefirTask@T1', NULL, 1, 1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 12 /* QuestSuccess */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirTask@T1', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  30 /* InqQuestSolves */, 0, 1, NULL, 'MaraeHuntSouthZefirKiritZefir@Done', NULL, 50, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 13 /* QuestFailure */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirTask@T1', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  67 /* Goto */, 0, 1, NULL, 'MaraeHuntSouthZefirAssign', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 12 /* QuestSuccess */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirKiritZefir@Done', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  10 /* Tell */, 0, 1, NULL, 'You have served the Hea well. Accept this reward for your efforts.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  1,  62 /* AwardNoShareXP */, 0, 1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 400000000, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  2, 113 /* AwardLuminance */, 0, 1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 100000, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  3,  22 /* StampQuest */, 0, 1, NULL, 'MaraeHuntSouthZefirWait', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  4,  31 /* EraseQuest */, 0, 1, NULL, 'MaraeHuntSouthZefirKiritZefir', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  5,  31 /* EraseQuest */, 0, 1, NULL, 'MaraeHuntSouthZefirTask', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  6,  10 /* Tell */, 1, 1, NULL, 'Return when another day''s hunt begins.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 13 /* QuestFailure */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirKiritZefir@Done', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  10 /* Tell */, 0, 1, NULL, 'You have defeated %tqc of %tqm Drumtaken Kirit Zefir. The hunt is not yet finished.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

INSERT INTO `weenie_properties_emote` (`object_Id`, `category`, `probability`, `weenie_Class_Id`, `style`, `substyle`, `quest`, `vendor_Type`, `min_Health`, `max_Health`)
VALUES (1007905, 32 /* GotoSet */,        1, NULL, NULL, NULL, 'MaraeHuntSouthZefirAssign', NULL, NULL, NULL);

SET @parent_id = LAST_INSERT_ID();

INSERT INTO `weenie_properties_emote_action` (`emote_Id`, `order`, `type`, `delay`, `extent`, `motion`, `message`, `test_String`, `min`, `max`, `min_64`, `max_64`, `min_Dbl`, `max_Dbl`, `stat`, `display`, `amount`, `amount_64`, `hero_X_P_64`, `percent`, `spell_Id`, `wealth_Rating`, `treasure_Class`, `treasure_Type`, `p_Script`, `sound`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (@parent_id,  0,  10 /* Tell */, 0, 1, NULL, 'The Zefir grow bold in the lowlands. I have a task suited to a capable hunter.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  1,  70 /* SetQuestCompletions */, 0, 1, NULL, 'MaraeHuntSouthZefirTask', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  2,  70 /* SetQuestCompletions */, 0, 1, NULL, 'MaraeHuntSouthZefirKiritZefir', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
     , (@parent_id,  3,  10 /* Tell */, 1, 1, NULL, 'Slay 50 Drumtaken Kirit Zefir. Return when the work is done.', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
