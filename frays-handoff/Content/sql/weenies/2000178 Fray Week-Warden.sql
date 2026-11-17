-- @owner: frays
-- @owns: wcid:2000178
-- @requires-restart: no
--
-- Frays task-giver: as 2000176, offering the week's one task (a kill task: 6,500 of the week's creatures or 15 Snarls).

DELETE FROM `weenie` WHERE `class_Id` = 2000178;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000178, 'dw-fray-week-warden', 10, '2026-11-05 00:35:36') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000178,   1,         16) /* ItemType - Creature */
     , (2000178,   2,         31) /* CreatureType - Human */
     , (2000178,   6,         -1) /* ItemsCapacity */
     , (2000178,   7,         -1) /* ContainersCapacity */
     , (2000178,   8,        120) /* Mass */
     , (2000178,  16,         32) /* ItemUseable - Remote */
     , (2000178,  25,         15) /* Level */
     , (2000178,  27,          0) /* ArmorType - None */
     , (2000178,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000178,  95,          8) /* RadarBlipColor - Yellow */
     , (2000178, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000178, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000178,   1, True ) /* Stuck */
     , (2000178,  12, True ) /* ReportCollisions */
     , (2000178,  13, False) /* Ethereal */
     , (2000178,  19, False) /* Attackable */
     , (2000178,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000178,  42, True ) /* AllowEdgeSlide */
     , (2000178,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000178,   1,       5) /* HeartbeatInterval */
     , (2000178,   2,       0) /* HeartbeatTimestamp */
     , (2000178,   3,    0.16) /* HealthRate */
     , (2000178,   4,       5) /* StaminaRate */
     , (2000178,   5,       1) /* ManaRate */
     , (2000178,  13,     0.9) /* ArmorModVsSlash */
     , (2000178,  14,       1) /* ArmorModVsPierce */
     , (2000178,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000178,  16,     0.4) /* ArmorModVsCold */
     , (2000178,  17,     0.4) /* ArmorModVsFire */
     , (2000178,  18,       1) /* ArmorModVsAcid */
     , (2000178,  19,     0.6) /* ArmorModVsElectric */
     , (2000178,  54,       3) /* UseRadius */
     , (2000178,  64,       1) /* ResistSlash */
     , (2000178,  65,       1) /* ResistPierce */
     , (2000178,  66,       1) /* ResistBludgeon */
     , (2000178,  67,       1) /* ResistFire */
     , (2000178,  68,       1) /* ResistCold */
     , (2000178,  69,       1) /* ResistAcid */
     , (2000178,  70,       1) /* ResistElectric */
     , (2000178,  71,       1) /* ResistHealthBoost */
     , (2000178,  72,       1) /* ResistStaminaDrain */
     , (2000178,  73,       1) /* ResistStaminaBoost */
     , (2000178,  74,       1) /* ResistManaDrain */
     , (2000178,  75,       1) /* ResistManaBoost */
     , (2000178, 104,      10) /* ObviousRadarRange */
     , (2000178, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000178,   1, 'Fray Week-Warden') /* Name */
     , (2000178,   3, 'Female') /* Sex */
     , (2000178,   4, 'Gharu''ndim') /* HeritageGroup */
     , (2000178,   5, 'Week-Warden') /* Template */
     , (2000178,  14, 'Speak with the Week-Warden to take the week''s task in the Frays.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000178,   1, 0x0200004E) /* Setup */
     , (2000178,   2, 0x09000001) /* MotionTable */
     , (2000178,   3, 0x20000001) /* SoundTable */
     , (2000178,   4, 0x30000000) /* CombatTable */
     , (2000178,   8, 0x06001036) /* Icon */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000178,   1,  85, 0, 0) /* Strength */
     , (2000178,   2,  75, 0, 0) /* Endurance */
     , (2000178,   3, 100, 0, 0) /* Quickness */
     , (2000178,   4,  50, 0, 0) /* Coordination */
     , (2000178,   5, 160, 0, 0) /* Focus */
     , (2000178,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000178,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000178,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000178,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000178,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000178,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000178, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000178, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000178,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000178,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000178,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000178,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000178,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000178,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000178,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000178,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000178,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000178, 2,  2587,  0, 20, 0.6, False) /* Create Shirt (2587) for Wield */
     , (2000178, 2,  6046,  0, 9, 0.8, False) /* Create Amuli Coat (6046) for Wield */
     , (2000178, 2,   135,  0, 20, 0.8, False) /* Create Turban (135) for Wield */
     , (2000178, 2,  2598,  0, 12, 0.7, False) /* Create Baggy Pants (2598) for Wield */
     , (2000178, 2,   133,  0, 12, 0.6, False) /* Create Slippers (133) for Wield */
     , (2000178, 2,   331,  0, 0, 0, False) /* Create Mace (331) for Wield */;
