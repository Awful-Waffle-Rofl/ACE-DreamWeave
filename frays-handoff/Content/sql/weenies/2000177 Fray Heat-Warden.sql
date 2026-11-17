-- @owner: frays
-- @owns: wcid:2000177
-- @requires-restart: no
--
-- Frays task-giver: as 2000176, offering the day's Heat tasks (climb to Heat 10/15/20 for Vault Keys, minutes at Heat 8/12).

DELETE FROM `weenie` WHERE `class_Id` = 2000177;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000177, 'dw-fray-heat-warden', 10, '2026-11-05 00:35:36') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000177,   1,         16) /* ItemType - Creature */
     , (2000177,   2,         31) /* CreatureType - Human */
     , (2000177,   6,         -1) /* ItemsCapacity */
     , (2000177,   7,         -1) /* ContainersCapacity */
     , (2000177,   8,        120) /* Mass */
     , (2000177,  16,         32) /* ItemUseable - Remote */
     , (2000177,  25,         15) /* Level */
     , (2000177,  27,          0) /* ArmorType - None */
     , (2000177,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000177,  95,          8) /* RadarBlipColor - Yellow */
     , (2000177, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000177, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000177,   1, True ) /* Stuck */
     , (2000177,  12, True ) /* ReportCollisions */
     , (2000177,  13, False) /* Ethereal */
     , (2000177,  19, False) /* Attackable */
     , (2000177,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000177,  42, True ) /* AllowEdgeSlide */
     , (2000177,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000177,   1,       5) /* HeartbeatInterval */
     , (2000177,   2,       0) /* HeartbeatTimestamp */
     , (2000177,   3,    0.16) /* HealthRate */
     , (2000177,   4,       5) /* StaminaRate */
     , (2000177,   5,       1) /* ManaRate */
     , (2000177,  13,     0.9) /* ArmorModVsSlash */
     , (2000177,  14,       1) /* ArmorModVsPierce */
     , (2000177,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000177,  16,     0.4) /* ArmorModVsCold */
     , (2000177,  17,     0.4) /* ArmorModVsFire */
     , (2000177,  18,       1) /* ArmorModVsAcid */
     , (2000177,  19,     0.6) /* ArmorModVsElectric */
     , (2000177,  54,       3) /* UseRadius */
     , (2000177,  64,       1) /* ResistSlash */
     , (2000177,  65,       1) /* ResistPierce */
     , (2000177,  66,       1) /* ResistBludgeon */
     , (2000177,  67,       1) /* ResistFire */
     , (2000177,  68,       1) /* ResistCold */
     , (2000177,  69,       1) /* ResistAcid */
     , (2000177,  70,       1) /* ResistElectric */
     , (2000177,  71,       1) /* ResistHealthBoost */
     , (2000177,  72,       1) /* ResistStaminaDrain */
     , (2000177,  73,       1) /* ResistStaminaBoost */
     , (2000177,  74,       1) /* ResistManaDrain */
     , (2000177,  75,       1) /* ResistManaBoost */
     , (2000177, 104,      10) /* ObviousRadarRange */
     , (2000177, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000177,   1, 'Fray Heat-Warden') /* Name */
     , (2000177,   3, 'Male') /* Sex */
     , (2000177,   4, 'Sho') /* HeritageGroup */
     , (2000177,   5, 'Heat-Warden') /* Template */
     , (2000177,  14, 'Speak with the Heat-Warden to take the day''s Heat tasks in the Frays.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000177,   1, 0x02000001) /* Setup */
     , (2000177,   2, 0x09000001) /* MotionTable */
     , (2000177,   3, 0x20000001) /* SoundTable */
     , (2000177,   4, 0x30000000) /* CombatTable */
     , (2000177,   8, 0x06001036) /* Icon */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000177,   1,  85, 0, 0) /* Strength */
     , (2000177,   2,  75, 0, 0) /* Endurance */
     , (2000177,   3, 100, 0, 0) /* Quickness */
     , (2000177,   4,  50, 0, 0) /* Coordination */
     , (2000177,   5, 160, 0, 0) /* Focus */
     , (2000177,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000177,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000177,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000177,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000177,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000177,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000177, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000177, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000177,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000177,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000177,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000177,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000177,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000177,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000177,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000177,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000177,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000177, 2,  2587,  0, 11, 0.6, False) /* Create Shirt (2587) for Wield */
     , (2000177, 2,  2601,  0, 11, 0.6, False) /* Create Loose Pants (2601) for Wield */
     , (2000177, 2,    43,  0, 14, 0.8, False) /* Create Yoroi Breastplate (43) for Wield */
     , (2000177, 2,    77,  0, 14, 0.8, False) /* Create Kabuton (77) for Wield */
     , (2000177, 2,  2606,  0, 6, 0.6, False) /* Create Boots (2606) for Wield */
     , (2000177, 2,   301,  0, 0, 0, False) /* Create Battle Axe (301) for Wield */;
