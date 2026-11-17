-- @owner: frays
-- @owns: wcid:2000169
-- @requires-restart: no
--
-- Frays village NPC for the daily hunts (FraysHunts: up to 3 kill tasks against this week's family, Embers each).

DELETE FROM `weenie` WHERE `class_Id` = 2000169;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000169, 'dw-fray-huntmaster', 10, '2026-11-03 01:29:55') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000169,   1,         16) /* ItemType - Creature */
     , (2000169,   2,         31) /* CreatureType - Human */
     , (2000169,   6,         -1) /* ItemsCapacity */
     , (2000169,   7,         -1) /* ContainersCapacity */
     , (2000169,   8,        120) /* Mass */
     , (2000169,  16,         32) /* ItemUseable - Remote */
     , (2000169,  25,         15) /* Level */
     , (2000169,  27,          0) /* ArmorType - None */
     , (2000169,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000169,  95,          8) /* RadarBlipColor - Yellow */
     , (2000169, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000169, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000169,   1, True ) /* Stuck */
     , (2000169,  12, True ) /* ReportCollisions */
     , (2000169,  13, False) /* Ethereal */
     , (2000169,  19, False) /* Attackable */
     , (2000169,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000169,  42, True ) /* AllowEdgeSlide */
     , (2000169,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000169,   1,       5) /* HeartbeatInterval */
     , (2000169,   2,       0) /* HeartbeatTimestamp */
     , (2000169,   3,    0.16) /* HealthRate */
     , (2000169,   4,       5) /* StaminaRate */
     , (2000169,   5,       1) /* ManaRate */
     , (2000169,  13,     0.9) /* ArmorModVsSlash */
     , (2000169,  14,       1) /* ArmorModVsPierce */
     , (2000169,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000169,  16,     0.4) /* ArmorModVsCold */
     , (2000169,  17,     0.4) /* ArmorModVsFire */
     , (2000169,  18,       1) /* ArmorModVsAcid */
     , (2000169,  19,     0.6) /* ArmorModVsElectric */
     , (2000169,  54,       3) /* UseRadius */
     , (2000169,  64,       1) /* ResistSlash */
     , (2000169,  65,       1) /* ResistPierce */
     , (2000169,  66,       1) /* ResistBludgeon */
     , (2000169,  67,       1) /* ResistFire */
     , (2000169,  68,       1) /* ResistCold */
     , (2000169,  69,       1) /* ResistAcid */
     , (2000169,  70,       1) /* ResistElectric */
     , (2000169,  71,       1) /* ResistHealthBoost */
     , (2000169,  72,       1) /* ResistStaminaDrain */
     , (2000169,  73,       1) /* ResistStaminaBoost */
     , (2000169,  74,       1) /* ResistManaDrain */
     , (2000169,  75,       1) /* ResistManaBoost */
     , (2000169, 104,      10) /* ObviousRadarRange */
     , (2000169, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000169,   1, 'Fray Huntmaster') /* Name */
     , (2000169,   3, 'Male') /* Sex */
     , (2000169,   4, 'Aluvian') /* HeritageGroup */
     , (2000169,   5, 'Huntmaster') /* Template */
     , (2000169,  14, 'Speak with the Huntmaster to take the day''s hunts in the Frays.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000169,   1, 0x02000001) /* Setup */
     , (2000169,   2, 0x09000001) /* MotionTable */
     , (2000169,   3, 0x20000001) /* SoundTable */
     , (2000169,   4, 0x30000000) /* CombatTable */
     , (2000169,   8, 0x06001036) /* Icon */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000169,   1,  85, 0, 0) /* Strength */
     , (2000169,   2,  75, 0, 0) /* Endurance */
     , (2000169,   3, 100, 0, 0) /* Quickness */
     , (2000169,   4,  50, 0, 0) /* Coordination */
     , (2000169,   5, 160, 0, 0) /* Focus */
     , (2000169,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000169,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000169,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000169,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000169,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000169,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000169, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000169, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000169,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000169,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000169,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000169,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000169,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000169,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000169,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000169,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000169,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000169, 2,   124,  0, 8, 0.7, False) /* Create Jerkin (124) for Wield */
     , (2000169, 2,    45,  0, 4, 0.7, False) /* Create Leather Cap (45) for Wield */
     , (2000169, 2,   127,  0, 7, 0.7, False) /* Create Pants (127) for Wield */
     , (2000169, 2,  2606,  0, 6, 0.6, False) /* Create Boots (2606) for Wield */
     , (2000169, 2,   306,  0, 0, 0, False) /* Create Longbow (306) for Wield */;
