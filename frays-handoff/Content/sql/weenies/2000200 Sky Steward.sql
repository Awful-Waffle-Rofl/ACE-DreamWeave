-- @owner: frays
-- @owns: wcid:2000200
-- @requires-restart: no
--
-- Frays: a vendor selling 2000196-2000199 and 2000203-2000206 for Sky Seals (AlternateCurrency 2000195).

DELETE FROM `weenie` WHERE `class_Id` = 2000200;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000200, 'dw-fray-sky-steward', 12, '2026-11-15 15:12:36') /* Vendor */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000200,   1,         16) /* ItemType - Creature */
     , (2000200,   2,         31) /* CreatureType - Human */
     , (2000200,   6,         -1) /* ItemsCapacity */
     , (2000200,   7,         -1) /* ContainersCapacity */
     , (2000200,   8,        120) /* Mass */
     , (2000200,  16,         32) /* ItemUseable - Remote */
     , (2000200,  25,         15) /* Level */
     , (2000200,  27,          0) /* ArmorType - None */
     , (2000200,  74,          0) /* MerchandiseItemTypes - None */
     , (2000200,  75,          0) /* MerchandiseMinValue */
     , (2000200,  76,     100000) /* MerchandiseMaxValue */
     , (2000200,  93,    6292504) /* PhysicsState - ReportCollisions, IgnoreCollisions, Gravity, ReportCollisionsAsEnvironment, EdgeSlide */
     , (2000200,  95,          8) /* RadarBlipColor - Yellow */
     , (2000200, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000200, 134,         16) /* PlayerKillerStatus - RubberGlue */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000200,   1, True ) /* Stuck */
     , (2000200,  12, True ) /* ReportCollisions */
     , (2000200,  13, False) /* Ethereal */
     , (2000200,  19, False) /* Attackable */
     , (2000200,  41, True ) /* ReportCollisionsAsEnvironment */
     , (2000200,  42, True ) /* AllowEdgeSlide */
     , (2000200,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000200,   1,       5) /* HeartbeatInterval */
     , (2000200,   2,       0) /* HeartbeatTimestamp */
     , (2000200,   3,    0.16) /* HealthRate */
     , (2000200,   4,       5) /* StaminaRate */
     , (2000200,   5,       1) /* ManaRate */
     , (2000200,  13,     0.9) /* ArmorModVsSlash */
     , (2000200,  14,       1) /* ArmorModVsPierce */
     , (2000200,  15,     1.1) /* ArmorModVsBludgeon */
     , (2000200,  16,     0.4) /* ArmorModVsCold */
     , (2000200,  17,     0.4) /* ArmorModVsFire */
     , (2000200,  18,       1) /* ArmorModVsAcid */
     , (2000200,  19,     0.6) /* ArmorModVsElectric */
     , (2000200,  37,       1) /* BuyPrice */
     , (2000200,  38,     0.5) /* SellPrice */
     , (2000200,  54,       3) /* UseRadius */
     , (2000200,  64,       1) /* ResistSlash */
     , (2000200,  65,       1) /* ResistPierce */
     , (2000200,  66,       1) /* ResistBludgeon */
     , (2000200,  67,       1) /* ResistFire */
     , (2000200,  68,       1) /* ResistCold */
     , (2000200,  69,       1) /* ResistAcid */
     , (2000200,  70,       1) /* ResistElectric */
     , (2000200,  71,       1) /* ResistHealthBoost */
     , (2000200,  72,       1) /* ResistStaminaDrain */
     , (2000200,  73,       1) /* ResistStaminaBoost */
     , (2000200,  74,       1) /* ResistManaDrain */
     , (2000200,  75,       1) /* ResistManaBoost */
     , (2000200, 104,      10) /* ObviousRadarRange */
     , (2000200, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000200,   1, 'Sky Steward') /* Name */
     , (2000200,   3, 'Male') /* Sex */
     , (2000200,   4, 'Gharu''ndim') /* HeritageGroup */
     , (2000200,   5, 'Sky Steward') /* Template */
     , (2000200,  14, 'The Sky Steward trades Sky Seals, earned by group camps, for fire looks, dye colours, titles, a trophy, Vault Keys, Kindling, Sigil Shards and Sky Banners.') /* Use */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000200,   1, 0x02000001) /* Setup */
     , (2000200,   2, 0x09000001) /* MotionTable */
     , (2000200,   3, 0x20000001) /* SoundTable */
     , (2000200,   4, 0x30000000) /* CombatTable */
     , (2000200,   8, 0x06001036) /* Icon */
     , (2000200,  57,    2000195) /* AlternateCurrency - Sky Seal */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000200,   1,  85, 0, 0) /* Strength */
     , (2000200,   2,  75, 0, 0) /* Endurance */
     , (2000200,   3, 100, 0, 0) /* Quickness */
     , (2000200,   4,  50, 0, 0) /* Coordination */
     , (2000200,   5, 160, 0, 0) /* Focus */
     , (2000200,   6, 180, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000200,   1,     0, 0, 0, 38) /* MaxHealth */
     , (2000200,   3,     0, 0, 0, 75) /* MaxStamina */
     , (2000200,   5,     0, 0, 0, 180) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000200,  6, 0, 2, 0,   4, 0, 0) /* MeleeDefense        Trained */
     , (2000200,  7, 0, 2, 0,   5, 0, 0) /* MissileDefense      Trained */
     , (2000200, 13, 0, 2, 0,   5, 0, 0) /* UnarmedCombat       Trained */
     , (2000200, 32, 0, 2, 0, 200, 0, 0) /* ItemEnchantment     Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000200,  0,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000200,  1,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000200,  2,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000200,  3,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000200,  4,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000200,  5,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000200,  6,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000200,  7,  4,  0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000200,  8,  4,  2, 0.75,    0,    0,    0,    0,    0,    0,    0,    0,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000200, 2,  5852,  0, 13, 0.8, False) /* Create Dho Vest and Robe (5852) for Wield */
     , (2000200, 2,  5894,  0, 13, 0.8, False) /* Create Fez (5894) for Wield */
     , (2000200, 2,   132,  0, 4, 0.6, False) /* Create Shoes (132) for Wield */
     , (2000200, 2,   338,  0, 0, 0, False) /* Create Quarter Staff (338) for Wield */
     , (2000200, 4, 2000196, -1, 0, 0, False) /* Create Sky Fray Trophy (2000196) for Shop */
     , (2000200, 4, 2000197, -1, 0, 0, False) /* Create Skyfire Sparks (2000197) for Shop */
     , (2000200, 4, 2000198, -1, 0, 0, False) /* Create Sky Dye Charter (2000198) for Shop */
     , (2000200, 4, 2000199, -1, 0, 0, False) /* Create Skybound Epithet (2000199) for Shop */
     , (2000200, 4, 2000203, -1, 0, 0, False) /* Create Sky Key Bundle (2000203) for Shop */
     , (2000200, 4, 2000204, -1, 0, 0, False) /* Create Sky Kindling Bundle (2000204) for Shop */
     , (2000200, 4, 2000205, -1, 0, 0, False) /* Create Sky Shard Bundle (2000205) for Shop */
     , (2000200, 4, 2000206, -1, 0, 0, False) /* Create Sky Banner (2000206) for Shop */;
