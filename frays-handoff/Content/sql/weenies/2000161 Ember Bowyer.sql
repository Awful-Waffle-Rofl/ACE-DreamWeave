-- @owner: frays
-- @owns: wcid:2000161
-- @requires-restart: no
--
-- Frays village bowyer.

DELETE FROM `weenie` WHERE `class_Id` = 2000161;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000161, 'dw-fray-bowyer', 12, '2026-11-02 23:54:30') /* Vendor */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000161,   1,         16) /* ItemType - Creature */
     , (2000161,   2,         31) /* CreatureType - Human */
     , (2000161,   6,         -1) /* ItemsCapacity */
     , (2000161,   7,         -1) /* ContainersCapacity */
     , (2000161,  16,         32) /* ItemUseable - Remote */
     , (2000161,  25,        275) /* Level */
     , (2000161,  74,  134480129) /* MerchandiseItemTypes - Weapon, PromissoryNote, CraftFletchingIntermediate */
     , (2000161,  75,          0) /* MerchandiseMinValue */
     , (2000161,  76,     100000) /* MerchandiseMaxValue */
     , (2000161,  93,    2098200) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment */
     , (2000161, 113,          2) /* Gender - Female */
     , (2000161, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000161, 134,         16) /* PlayerKillerStatus - RubberGlue */
     , (2000161, 188,          1) /* HeritageGroup - Aluvian */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000161,   1, True ) /* Stuck */
     , (2000161,  19, False) /* Attackable */
     , (2000161,  39, True ) /* DealMagicalItems */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000161,   1,       5) /* HeartbeatInterval */
     , (2000161,   2,       0) /* HeartbeatTimestamp */
     , (2000161,   3,    0.16) /* HealthRate */
     , (2000161,   4,       5) /* StaminaRate */
     , (2000161,   5,       1) /* ManaRate */
     , (2000161,  11,     300) /* ResetInterval */
     , (2000161,  12,     0.5) /* Shade */
     , (2000161,  13,     0.9) /* ArmorModVsSlash */
     , (2000161,  14,       1) /* ArmorModVsPierce */
     , (2000161,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000161,  16,     0.4) /* ArmorModVsCold */
     , (2000161,  17,     0.4) /* ArmorModVsFire */
     , (2000161,  18,       1) /* ArmorModVsAcid */
     , (2000161,  19,     0.6) /* ArmorModVsElectric */
     , (2000161,  37,     0.9) /* BuyPrice */
     , (2000161,  38,       1) /* SellPrice */
     , (2000161,  54,       3) /* UseRadius */
     , (2000161,  64,       1) /* ResistSlash */
     , (2000161,  65,       1) /* ResistPierce */
     , (2000161,  66,       1) /* ResistBludgeon */
     , (2000161,  67,       1) /* ResistFire */
     , (2000161,  68,       1) /* ResistCold */
     , (2000161,  69,       1) /* ResistAcid */
     , (2000161,  70,       1) /* ResistElectric */
     , (2000161,  71,       1) /* ResistHealthBoost */
     , (2000161,  72,       1) /* ResistStaminaDrain */
     , (2000161,  73,       1) /* ResistStaminaBoost */
     , (2000161,  74,       1) /* ResistManaDrain */
     , (2000161,  75,       1) /* ResistManaBoost */
     , (2000161, 104,      10) /* ObviousRadarRange */
     , (2000161, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000161,   1, 'Ember Bowyer') /* Name */
     , (2000161,   5, 'Master Fletcher') /* Template */
     , (2000161,  14, 'The village bowyer: bows, crossbows, atlatls, arrows and fletching.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000161,   1, 0x0200004E) /* Setup */
     , (2000161,   2, 0x09000001) /* MotionTable */
     , (2000161,   3, 0x20000002) /* SoundTable */
     , (2000161,   6, 0x0400007E) /* PaletteBase */
     , (2000161,   8, 0x06001036) /* Icon */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000161,   1, 200, 0, 0) /* Strength */
     , (2000161,   2, 200, 0, 0) /* Endurance */
     , (2000161,   3, 290, 0, 0) /* Quickness */
     , (2000161,   4, 290, 0, 0) /* Coordination */
     , (2000161,   5, 290, 0, 0) /* Focus */
     , (2000161,   6, 260, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000161,   1,    10, 0, 0, 296) /* MaxHealth */
     , (2000161,   3,    10, 0, 0, 396) /* MaxStamina */
     , (2000161,   5,    10, 0, 0, 456) /* MaxMana */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000161,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000161,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000161,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000161,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000161,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000161,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000161,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000161,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000161,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000161, 2, 38848,  1, 0, 0, False) /* Create Lightning Longbow (38848) for Wield */
     , (2000161, 4,   306, -1, 0, 0, False) /* Create Longbow (306) for Shop */
     , (2000161, 4,   311, -1, 0, 0, False) /* Create Heavy Crossbow (311) for Shop */
     , (2000161, 4, 12463, -1, 0, 0, False) /* Create Atlatl (12463) for Shop */
     , (2000161, 4,   300, -1, 0, 0, False) /* Create Arrow (300) for Shop */
     , (2000161, 4,   305, -1, 0, 0, False) /* Create Quarrel (305) for Shop */
     , (2000161, 4, 12464, -1, 0, 0, False) /* Create Atlatl Dart (12464) for Shop */
     , (2000161, 4, 44070, -1, 0, 0, False) /* Create Bundle of Prismatic Arrowheads (44070) for Shop */
     , (2000161, 4, 44069, -1, 0, 0, False) /* Create Bundle of Greater Prismatic Arrowheads (44069) for Shop */
     , (2000161, 4, 44068, -1, 0, 0, False) /* Create Bundle of Deadly Prismatic Arrowheads (44068) for Shop */
     , (2000161, 4, 44359, -1, 0, 0, False) /* Create Bundle of Greater Deadly Arrowheads (44359) for Shop */
     , (2000161, 4, 44360, -1, 0, 0, False) /* Create Bundle of Greater Deadly Blunt Arrowheads (44360) for Shop */
     , (2000161, 4, 44361, -1, 0, 0, False) /* Create Bundle of Greater Deadly Broad Arrowheads (44361) for Shop */
     , (2000161, 4, 44362, -1, 0, 0, False) /* Create Bundle of Greater Deadly Armor Piercing Arrowheads (44362) for Shop */
     , (2000161, 4, 44363, -1, 0, 0, False) /* Create Bundle of Greater Deadly Frog Crotch Arrowheads (44363) for Shop */
     , (2000161, 4,  4586, -1, 0, 0, False) /* Create Bundle of Arrowheads (4586) for Shop */
     , (2000161, 4,  4585, -1, 0, 0, False) /* Create Bundle of Arrowshafts (4585) for Shop */
     , (2000161, 4,  5339, -1, 0, 0, False) /* Create Bundle of Quarrelshafts (5339) for Shop */
     , (2000161, 4, 15296, -1, 0, 0, False) /* Create Bundle of Atlatl Dart Shafts (15296) for Shop */
     , (2000161, 4, 23857, -1, 0, 0, False) /* Create Bundle of Spiketails (23857) for Shop */
     , (2000161, 4,  3599, -1, 0, 0, False) /* Create Blunt Arrow (3599) for Shop */
     , (2000161, 4,  3603, -1, 0, 0, False) /* Create Blunt Quarrel (3603) for Shop */
     , (2000161, 4,  3601, -1, 0, 0, False) /* Create Frog Crotch Arrow (3601) for Shop */
     , (2000161, 4,  3605, -1, 0, 0, False) /* Create Frog Crotch Quarrel (3605) for Shop */
     , (2000161, 4,  3598, -1, 0, 0, False) /* Create Armor Piercing Arrow (3598) for Shop */
     , (2000161, 4,  3602, -1, 0, 0, False) /* Create Armor Piercing Quarrel (3602) for Shop */
     , (2000161, 4, 44071, -1, 0, 0, False) /* Create Wrapped Bundle of Prismatic Arrowheads (44071) for Shop */
     , (2000161, 4, 44073, -1, 0, 0, False) /* Create Wrapped Bundle of Greater Prismatic Arrowheads (44073) for Shop */
     , (2000161, 4, 44072, -1, 0, 0, False) /* Create Wrapped Bundle of Deadly Prismatic Arrowheads (44072) for Shop */
     , (2000161, 4, 44364, -1, 0, 0, False) /* Create Wrapped Bundle of Greater Deadly Arrowheads (44364) for Shop */
     , (2000161, 4, 44224, -1, 0, 0, False) /* Create Wrapped Bundle of Greater Deadly Blunt Arrowheads (44224) for Shop */
     , (2000161, 4, 44365, -1, 0, 0, False) /* Create Wrapped Bundle of Greater Deadly Broad Arrowheads (44365) for Shop */
     , (2000161, 4, 44366, -1, 0, 0, False) /* Create Wrapped Bundle of Greater Deadly Armor Piercing Arrowheads (44366) for Shop */
     , (2000161, 4, 44367, -1, 0, 0, False) /* Create Wrapped Bundle of Greater Deadly Frog Crotch Arrowheads (44367) for Shop */
     , (2000161, 4,  9359, -1, 0, 0, False) /* Create Wrapped Bundle of Arrowheads (9359) for Shop */
     , (2000161, 4,  9363, -1, 0, 0, False) /* Create Wrapped Bundle of Broad Arrowheads (9363) for Shop */
     , (2000161, 4,  9362, -1, 0, 0, False) /* Create Wrapped Bundle of Blunt Arrowheads (9362) for Shop */
     , (2000161, 4,  9361, -1, 0, 0, False) /* Create Wrapped Bundle of Armor Piercing Arrowheads (9361) for Shop */
     , (2000161, 4,  9366, -1, 0, 0, False) /* Create Wrapped Bundle of Frog Crotch Arrowheads (9366) for Shop */
     , (2000161, 4,  9377, -1, 0, 0, False) /* Create Wrapped Bundle of Arrowshafts (9377) for Shop */
     , (2000161, 4,  9378, -1, 0, 0, False) /* Create Wrapped Bundle of Quarrelshafts (9378) for Shop */
     , (2000161, 4, 23858, -1, 0, 0, False) /* Create Bundle of Wrapped Spiketails (23858) for Shop */
     , (2000161, 4, 15298, -1, 0, 0, False) /* Create Wrapped Bundle of Atlatl Dartshafts (15298) for Shop */
     , (2000161, 4,  2621, -1, 0, 0, False) /* Create Trade Note (100) (2621) for Shop */
     , (2000161, 4,  2622, -1, 0, 0, False) /* Create Trade Note (500) (2622) for Shop */
     , (2000161, 4,  2623, -1, 0, 0, False) /* Create Trade Note (1,000) (2623) for Shop */
     , (2000161, 4,  2624, -1, 0, 0, False) /* Create Trade Note (5,000) (2624) for Shop */
     , (2000161, 4,  2625, -1, 0, 0, False) /* Create Trade Note (10,000) (2625) for Shop */
     , (2000161, 4,  2626, -1, 0, 0, False) /* Create Trade Note (50,000) (2626) for Shop */
     , (2000161, 4,  2627, -1, 0, 0, False) /* Create Trade Note (100,000) (2627) for Shop */
     , (2000161, 4, 20628, -1, 0, 0, False) /* Create Trade Note (150,000) (20628) for Shop */
     , (2000161, 4, 20629, -1, 0, 0, False) /* Create Trade Note (200,000) (20629) for Shop */
     , (2000161, 4, 20630, -1, 0, 0, False) /* Create Trade Note (250,000) (20630) for Shop */
     , (2000161, 4, 44074, -1, 0, 0, False) /* Create Invitation to Master Fletchers (44074) for Shop */;

INSERT INTO `weenie_properties_palette` (`object_Id`, `sub_Palette_Id`, `offset`, `length`)
VALUES (2000161, 67109559, 0, 24)
     , (2000161, 67110065, 32, 8)
     , (2000161, 67110377, 40, 24)
     , (2000161, 67110377, 64, 8)
     , (2000161, 67112916, 72, 8)
     , (2000161, 67112916, 108, 8)
     , (2000161, 67112916, 116, 12)
     , (2000161, 67112916, 136, 16)
     , (2000161, 67112916, 160, 8)
     , (2000161, 67112916, 168, 6)
     , (2000161, 67112916, 174, 12)
     , (2000161, 67112916, 240, 10)
     , (2000161, 67116920, 92, 4)
     , (2000161, 67116920, 128, 8)
     , (2000161, 67116920, 152, 8)
     , (2000161, 67116920, 206, 10)
     , (2000161, 67116920, 250, 6)
     , (2000161, 67117021, 24, 8);

INSERT INTO `weenie_properties_texture_map` (`object_Id`, `index`, `old_Id`, `new_Id`)
VALUES (2000161, 0, 83889072, 83886685)
     , (2000161, 0, 83889342, 83889386)
     , (2000161, 1, 83887064, 83886241)
     , (2000161, 2, 83887066, 83887055)
     , (2000161, 5, 83887064, 83886241)
     , (2000161, 6, 83887066, 83887055)
     , (2000161, 9, 83887062, 83886686)
     , (2000161, 9, 83887070, 83886781)
     , (2000161, 10, 83887069, 83886782)
     , (2000161, 11, 83887067, 83891213)
     , (2000161, 13, 83887069, 83886782)
     , (2000161, 14, 83887067, 83891213)
     , (2000161, 16, 83886232, 83890685)
     , (2000161, 16, 83886668, 83890275)
     , (2000161, 16, 83886684, 83890357)
     , (2000161, 16, 83886837, 83890311);

INSERT INTO `weenie_properties_anim_part` (`object_Id`, `index`, `animation_Id`)
VALUES (2000161, 0, 16795207)
     , (2000161, 1, 16795220)
     , (2000161, 2, 16795219)
     , (2000161, 3, 16795214)
     , (2000161, 4, 16795223)
     , (2000161, 5, 16795222)
     , (2000161, 6, 16795221)
     , (2000161, 7, 16795215)
     , (2000161, 8, 16795224)
     , (2000161, 9, 16795213)
     , (2000161, 10, 16795209)
     , (2000161, 11, 16795208)
     , (2000161, 12, 16795216)
     , (2000161, 13, 16795211)
     , (2000161, 14, 16795210)
     , (2000161, 15, 16795217)
     , (2000161, 16, 16795218);
