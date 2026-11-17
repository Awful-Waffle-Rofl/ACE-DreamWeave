-- @owner: frays
-- @owns: wcid:2000031
-- @requires-restart: no
--
-- Frays (UCM project) Ember vendor.

DELETE FROM `weenie` WHERE `class_Id` = 2000031;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000031, 'dw-ember-quartermaster', 12, '2026-10-29 22:48:54') /* Vendor */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000031,   1,         16) /* ItemType - Creature */
     , (2000031,   2,         31) /* CreatureType - Human */
     , (2000031,   6,         -1) /* ItemsCapacity */
     , (2000031,   7,         -1) /* ContainersCapacity */
     , (2000031,   8,        120) /* Mass */
     , (2000031,  16,         32) /* ItemUseable - Remote */
     , (2000031,  25,         15) /* Level */
     , (2000031,  27,          0) /* ArmorType - None */
     , (2000031,  74,          0) /* MerchandiseItemTypes - None */
     , (2000031,  75,          0) /* MerchandiseMinValue */
     , (2000031,  76,     100000) /* MerchandiseMaxValue */
     , (2000031,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000031,  95,          8) /* RadarBlipColor - Yellow */
     , (2000031, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000031, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000031,   1, True ) /* Stuck */
     , (2000031,  12, True ) /* ReportCollisions */
     , (2000031,  13, False) /* Ethereal */
     , (2000031,  19, False) /* Attackable */
     , (2000031,  39, True ) /* DealMagicalItems */
     , (2000031,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000031,  42, True ) /* AllowEdgeSlide */
     , (2000031,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000031,   1,       5) /* HeartbeatInterval */
     , (2000031,   2,       0) /* HeartbeatTimestamp */
     , (2000031,   3,    0.16) /* HealthRate */
     , (2000031,   4,       5) /* StaminaRate */
     , (2000031,   5,       1) /* ManaRate */
     , (2000031,  13,     0.9) /* ArmorModVsSlash */
     , (2000031,  14,       1) /* ArmorModVsPierce */
     , (2000031,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000031,  16,     0.4) /* ArmorModVsCold */
     , (2000031,  17,     0.4) /* ArmorModVsFire */
     , (2000031,  18,       1) /* ArmorModVsAcid */
     , (2000031,  19,     0.6) /* ArmorModVsElectric */
     , (2000031,  37,       1) /* BuyPrice */
     , (2000031,  38,     0.5) /* SellPrice */
     , (2000031,  54,       3) /* UseRadius */
     , (2000031,  64,       1) /* ResistSlash */
     , (2000031,  65,       1) /* ResistPierce */
     , (2000031,  66,       1) /* ResistBludgeon */
     , (2000031,  67,       1) /* ResistFire */
     , (2000031,  68,       1) /* ResistCold */
     , (2000031,  69,       1) /* ResistAcid */
     , (2000031,  70,       1) /* ResistElectric */
     , (2000031,  71,       1) /* ResistHealthBoost */
     , (2000031,  72,       1) /* ResistStaminaDrain */
     , (2000031,  73,       1) /* ResistStaminaBoost */
     , (2000031,  74,       1) /* ResistManaDrain */
     , (2000031,  75,       1) /* ResistManaBoost */
     , (2000031, 104,      10) /* ObviousRadarRange */
     , (2000031, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000031,   1, 'Ember Trader') /* Name */
     , (2000031,   3, 'Female') /* Sex */
     , (2000031,   4, 'Gharu''ndim') /* HeritageGroup */
     , (2000031,   5, 'Ember Trader') /* Template */
     , (2000031,  14, 'Trade your Embers for Vault Keys, Slayer and Reroll Stones, perks and more.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000031,   1, 0x0200004E) /* Setup */
     , (2000031,   2, 0x09000001) /* MotionTable */
     , (2000031,   3, 0x20000001) /* SoundTable */
     , (2000031,   4, 0x30000000) /* CombatTable */
     , (2000031,   8, 0x06001036) /* Icon */
     , (2000031,  57,    2000032) /* AlternateCurrency - Ember */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000031,   1,  85, 0, 0) /* Strength */
     , (2000031,   2,  75, 0, 0) /* Endurance */
     , (2000031,   3, 100, 0, 0) /* Quickness */
     , (2000031,   4,  50, 0, 0) /* Coordination */
     , (2000031,   5, 160, 0, 0) /* Focus */
     , (2000031,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000031,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000031,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000031,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000031,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000031,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000031, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000031, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000031,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000031,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000031,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000031,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000031,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000031,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000031,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000031,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000031,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000031, 2,  5851,  0, 14, 0.8, False) /* Create Faran Robe with Hood (5851) for Wield */
     , (2000031, 2,   133,  0, 21, 0.6, False) /* Create Slippers (133) for Wield */
     , (2000031, 4, 2000077, -1, 0, 0, False) /* Create Slayer Stone (2000077) for Shop */
     , (2000031, 4, 2000078, -1, 0, 0, False) /* Create Reroll Stone (2000078) for Shop */
     , (2000031, 4, 2000152, -1, 0, 0, False) /* Create Vault Key (2000152) for Shop */
     , (2000031, 4,  8652, -1, 0, 0, False) /* Create Argenory Dye Pot (8652) for Shop */
     , (2000031, 4,  8045, -1, 0, 0, False) /* Create Berimphur Dye Pot (8045) for Shop */
     , (2000031, 4, 11476, -1, 0, 0, False) /* Create Colban Dye Pot (11476) for Shop */
     , (2000031, 4,  8044, -1, 0, 0, False) /* Create Hennacin Dye Pot (8044) for Shop */
     , (2000031, 4,  8650, -1, 0, 0, False) /* Create Lapyan Dye Pot (8650) for Shop */
     , (2000031, 4,  8651, -1, 0, 0, False) /* Create Minalim Dye Pot (8651) for Shop */
     , (2000031, 4, 11477, -1, 0, 0, False) /* Create Relanim Dye Pot (11477) for Shop */
     , (2000031, 4, 11475, -1, 0, 0, False) /* Create Thananim Dye Pot (11475) for Shop */
     , (2000031, 4,  8043, -1, 0, 0, False) /* Create Verdalim Dye Pot (8043) for Shop */
     , (2000031, 4, 2000191, -1, 0, 0, False) /* Create Ember Stamina Kit (2000191) for Shop */
     , (2000031, 4, 2000192, -1, 0, 0, False) /* Create Prismatic Dye Pot (2000192) for Shop */;
