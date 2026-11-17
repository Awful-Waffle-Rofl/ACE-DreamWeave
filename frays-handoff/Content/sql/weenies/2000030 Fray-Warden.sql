-- @owner: frays
-- @owns: wcid:2000030
-- @requires-restart: no
--
-- Frays (UCM project) explainer NPC.

DELETE FROM `weenie` WHERE `class_Id` = 2000030;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000030, 'dw-hearth-keeper', 10, '2026-10-29 20:28:03') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000030,   1,         16) /* ItemType - Creature */
     , (2000030,   2,         31) /* CreatureType - Human */
     , (2000030,   6,         -1) /* ItemsCapacity */
     , (2000030,   7,         -1) /* ContainersCapacity */
     , (2000030,   8,        120) /* Mass */
     , (2000030,  16,         32) /* ItemUseable - Remote */
     , (2000030,  25,         15) /* Level */
     , (2000030,  27,          0) /* ArmorType - None */
     , (2000030,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000030,  95,          8) /* RadarBlipColor - Yellow */
     , (2000030, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000030, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000030,   1, True ) /* Stuck */
     , (2000030,  12, True ) /* ReportCollisions */
     , (2000030,  13, False) /* Ethereal */
     , (2000030,  19, False) /* Attackable */
     , (2000030,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000030,  42, True ) /* AllowEdgeSlide */
     , (2000030,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000030,   1,       5) /* HeartbeatInterval */
     , (2000030,   2,       0) /* HeartbeatTimestamp */
     , (2000030,   3,    0.16) /* HealthRate */
     , (2000030,   4,       5) /* StaminaRate */
     , (2000030,   5,       1) /* ManaRate */
     , (2000030,  13,     0.9) /* ArmorModVsSlash */
     , (2000030,  14,       1) /* ArmorModVsPierce */
     , (2000030,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000030,  16,     0.4) /* ArmorModVsCold */
     , (2000030,  17,     0.4) /* ArmorModVsFire */
     , (2000030,  18,       1) /* ArmorModVsAcid */
     , (2000030,  19,     0.6) /* ArmorModVsElectric */
     , (2000030,  54,       3) /* UseRadius */
     , (2000030,  64,       1) /* ResistSlash */
     , (2000030,  65,       1) /* ResistPierce */
     , (2000030,  66,       1) /* ResistBludgeon */
     , (2000030,  67,       1) /* ResistFire */
     , (2000030,  68,       1) /* ResistCold */
     , (2000030,  69,       1) /* ResistAcid */
     , (2000030,  70,       1) /* ResistElectric */
     , (2000030,  71,       1) /* ResistHealthBoost */
     , (2000030,  72,       1) /* ResistStaminaDrain */
     , (2000030,  73,       1) /* ResistStaminaBoost */
     , (2000030,  74,       1) /* ResistManaDrain */
     , (2000030,  75,       1) /* ResistManaBoost */
     , (2000030, 104,      10) /* ObviousRadarRange */
     , (2000030, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000030,   1, 'Fray-Warden') /* Name */
     , (2000030,   3, 'Male') /* Sex */
     , (2000030,   4, 'Sho') /* HeritageGroup */
     , (2000030,   5, 'Fray-Warden') /* Template */
     , (2000030,  14, 'Speak with the Fray-Warden to learn how the Frays work and what this week brings.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000030,   1, 0x02000001) /* Setup */
     , (2000030,   2, 0x09000001) /* MotionTable */
     , (2000030,   3, 0x20000001) /* SoundTable */
     , (2000030,   4, 0x30000000) /* CombatTable */
     , (2000030,   8, 0x06001036) /* Icon */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000030,   1,  85, 0, 0) /* Strength */
     , (2000030,   2,  75, 0, 0) /* Endurance */
     , (2000030,   3, 100, 0, 0) /* Quickness */
     , (2000030,   4,  50, 0, 0) /* Coordination */
     , (2000030,   5, 160, 0, 0) /* Focus */
     , (2000030,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000030,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000030,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000030,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000030,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000030,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000030, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000030, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000030,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000030,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000030,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000030,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000030,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000030,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000030,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000030,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000030,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000030, 2,   351,  0, 0, 0, False) /* Create Long Sword (351) for Wield */
     , (2000030, 2,    46,  0, 8, 1, False) /* Create Metal Cap (46) for Wield */
     , (2000030, 2,  2587,  0, 4, 1, False) /* Create Shirt (2587) for Wield */
     , (2000030, 2,  2601,  0, 4, 1, False) /* Create Loose Pants (2601) for Wield */
     , (2000030, 2,   414,  0, 39, 1, False) /* Create Chainmail Breastplate (414) for Wield */
     , (2000030, 2,   415,  0, 19, 1, False) /* Create Chainmail Girth (415) for Wield */
     , (2000030, 2,    84,  0, 11, 1, False) /* Create Studded Leather Leggings (84) for Wield */
     , (2000030, 2,   122,  0, 11, 1, False) /* Create Soft Leather Gloves (122) for Wield */
     , (2000030, 2,  2606,  0, 9, 0.8, False) /* Create Boots (2606) for Wield */;
