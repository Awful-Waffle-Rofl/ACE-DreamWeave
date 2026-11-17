-- @owner: frays
-- @owns: wcid:2000162
-- @requires-restart: no
--
-- Frays village Sigil vendor (type 12, Embers) for the boon collection: sells 2000170-2000172.

DELETE FROM `weenie` WHERE `class_Id` = 2000162;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000162, 'dw-fray-boonwright', 12, '2026-11-03 12:26:04') /* Vendor */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000162,   1,         16) /* ItemType - Creature */
     , (2000162,   2,         31) /* CreatureType - Human */
     , (2000162,   6,         -1) /* ItemsCapacity */
     , (2000162,   7,         -1) /* ContainersCapacity */
     , (2000162,   8,        120) /* Mass */
     , (2000162,  16,         32) /* ItemUseable - Remote */
     , (2000162,  25,         15) /* Level */
     , (2000162,  27,          0) /* ArmorType - None */
     , (2000162,  74,          0) /* MerchandiseItemTypes - None */
     , (2000162,  75,          0) /* MerchandiseMinValue */
     , (2000162,  76,     100000) /* MerchandiseMaxValue */
     , (2000162,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000162,  95,          8) /* RadarBlipColor - Yellow */
     , (2000162, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000162, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000162,   1, True ) /* Stuck */
     , (2000162,  12, True ) /* ReportCollisions */
     , (2000162,  13, False) /* Ethereal */
     , (2000162,  19, False) /* Attackable */
     , (2000162,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000162,  42, True ) /* AllowEdgeSlide */
     , (2000162,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000162,   1,       5) /* HeartbeatInterval */
     , (2000162,   2,       0) /* HeartbeatTimestamp */
     , (2000162,   3,    0.16) /* HealthRate */
     , (2000162,   4,       5) /* StaminaRate */
     , (2000162,   5,       1) /* ManaRate */
     , (2000162,  13,     0.9) /* ArmorModVsSlash */
     , (2000162,  14,       1) /* ArmorModVsPierce */
     , (2000162,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000162,  16,     0.4) /* ArmorModVsCold */
     , (2000162,  17,     0.4) /* ArmorModVsFire */
     , (2000162,  18,       1) /* ArmorModVsAcid */
     , (2000162,  19,     0.6) /* ArmorModVsElectric */
     , (2000162,  37,       1) /* BuyPrice */
     , (2000162,  38,     0.5) /* SellPrice */
     , (2000162,  54,       3) /* UseRadius */
     , (2000162,  64,       1) /* ResistSlash */
     , (2000162,  65,       1) /* ResistPierce */
     , (2000162,  66,       1) /* ResistBludgeon */
     , (2000162,  67,       1) /* ResistFire */
     , (2000162,  68,       1) /* ResistCold */
     , (2000162,  69,       1) /* ResistAcid */
     , (2000162,  70,       1) /* ResistElectric */
     , (2000162,  71,       1) /* ResistHealthBoost */
     , (2000162,  72,       1) /* ResistStaminaDrain */
     , (2000162,  73,       1) /* ResistStaminaBoost */
     , (2000162,  74,       1) /* ResistManaDrain */
     , (2000162,  75,       1) /* ResistManaBoost */
     , (2000162, 104,      10) /* ObviousRadarRange */
     , (2000162, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000162,   1, 'Boonwright') /* Name */
     , (2000162,   3, 'Male') /* Sex */
     , (2000162,   4, 'Gharu''ndim') /* HeritageGroup */
     , (2000162,   5, 'Boonwright') /* Template */
     , (2000162,  14, 'The Boonwright sells Sigils that raise the boons in your collection, and Rebinding Embers that change a companion''s style. /fray collection shows your ranks.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000162,   1, 0x02000001) /* Setup */
     , (2000162,   2, 0x09000001) /* MotionTable */
     , (2000162,   3, 0x20000001) /* SoundTable */
     , (2000162,   4, 0x30000000) /* CombatTable */
     , (2000162,   8, 0x06001036) /* Icon */
     , (2000162,  57,    2000032) /* AlternateCurrency - Ember */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000162,   1,  85, 0, 0) /* Strength */
     , (2000162,   2,  75, 0, 0) /* Endurance */
     , (2000162,   3, 100, 0, 0) /* Quickness */
     , (2000162,   4,  50, 0, 0) /* Coordination */
     , (2000162,   5, 160, 0, 0) /* Focus */
     , (2000162,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000162,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000162,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000162,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000162,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000162,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000162, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000162, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000162,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000162,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000162,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000162,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000162,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000162,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000162,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000162,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000162,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000162, 2,  5852,  0, 13, 0.8, False) /* Create Dho Vest and Robe (5852) for Wield */
     , (2000162, 2,  5894,  0, 13, 0.8, False) /* Create Fez (5894) for Wield */
     , (2000162, 2,   132,  0, 4, 0.6, False) /* Create Shoes (132) for Wield */
     , (2000162, 2,   338,  0, 0, 0, False) /* Create Quarter Staff (338) for Wield */
     , (2000162, 4, 2000170, -1, 0, 0, False) /* Create Lesser Sigil (2000170) for Shop */
     , (2000162, 4, 2000171, -1, 0, 0, False) /* Create Greater Sigil (2000171) for Shop */
     , (2000162, 4, 2000172, -1, 0, 0, False) /* Create Grand Sigil (2000172) for Shop */
     , (2000162, 4, 2000174, -1, 0, 0, False) /* Create Rebinding Ember (2000174) for Shop */;
